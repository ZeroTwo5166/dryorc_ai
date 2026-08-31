using System.Net;
using System.Text;
using System.Text.Json;
using backend.Data;
using backend.Enums;
using backend.Interfaces;
using backend.Models;
using backend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace backend.Tests;

/// <summary>
/// Service-level tests for ShopifyOrderCancellationExecutor — real Postgres
/// for Business/Integration/OrderCancellation rows (same convention as
/// ShopifyOrderLookupServiceTests), FakeShopifyHttpMessageHandler for the
/// outbound orderCancel call. No production trigger exists yet (deliberate,
/// per the pending-reconciliation design handoff) — this is the only
/// exerciser.
/// </summary>
public class ShopifyOrderCancellationExecutorTests : IAsyncLifetime
{
    private const string AdminApiVersion = "2026-07";
    private const string AccessToken = "test-access-token";
    private const string OrderId = "gid://shopify/Order/1";

    // Unique per test class instance (xUnit creates one per [Fact]) — not a
    // shared literal, to avoid colliding with the unique
    // (Platform, ExternalAccountIdentifier) constraint on Integrations
    // across test classes that run in parallel (ShopifyOrderLookupServiceTests,
    // OrderCancellationReconciliationServiceTests, this class).
    private readonly string _shop = $"test-shop-{Guid.NewGuid():N}";

    private ServiceProvider _provider = null!;
    private readonly List<Guid> _businessIdsToClean = [];

    public Task InitializeAsync()
    {
        var configuration = new ConfigurationBuilder()
            .AddUserSecrets<Program>()
            .Build();

        var connectionUri = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection not found via user-secrets.");

        var services = new ServiceCollection()
            .AddSingleton<ICurrentTenantAccessor>(new FixedTenantAccessor(null))
            .AddDbContext<AppDbContext>(o => o.UseNpgsql(
                PostgresConnectionString.FromUri(connectionUri), x => x.UseVector()))
            .AddScoped<IOrderCancellationService, OrderCancellationService>();
        services.AddDataProtection();
        services.AddScoped<IIntegrationTokenProtector, IntegrationTokenProtector>();
        _provider = services.BuildServiceProvider();

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_businessIdsToClean.Count > 0)
        {
            await using var scope = _provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var ids = _businessIdsToClean.ToArray();

            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"OrderCancellations\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Integrations\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Customers\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Businesses\" WHERE \"Id\" = ANY({ids})");
        }

        await _provider.DisposeAsync();
    }

    private async Task<(Guid BusinessId, Guid CustomerId)> CreateBusinessWithIntegrationAsync(AppDbContext db, IServiceProvider scopedProvider)
    {
        var tokenProtector = scopedProvider.GetRequiredService<IIntegrationTokenProtector>();

        var business = new Business
        {
            Id = Guid.NewGuid(),
            Name = $"OrderCancellation Executor Test {Guid.NewGuid():N}",
            TimeZone = "Europe/Copenhagen",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Businesses.Add(business);

        db.Integrations.Add(new Integration
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id,
            Platform = ShopifyIntegrationOAuthProvider.PlatformName,
            EncryptedAccessToken = tokenProtector.Protect(ShopifyIntegrationOAuthProvider.PlatformName, AccessToken),
            ExternalAccountIdentifier = _shop,
            IsActive = true,
            ConnectedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Customers.Add(customer);

        await db.SaveChangesAsync();
        _businessIdsToClean.Add(business.Id);
        return (business.Id, customer.Id);
    }

    private async Task<Guid> CreatePendingRowAsync(
        IServiceProvider scopedProvider, Guid businessId, Guid customerId,
        RefundMethod? method = RefundMethod.OriginalPaymentMethod, OrderCancelReason reason = OrderCancelReason.Customer,
        bool restock = false)
    {
        var service = scopedProvider.GetRequiredService<IOrderCancellationService>();
        var result = await service.RequestCancellationAsync(
            businessId, customerId, null, OrderId, null, Guid.NewGuid().ToString("N"),
            restock, method, reason, CancellationToken.None);
        return result.OrderCancellationId;
    }

    private static ShopifyOrderCancellationExecutor NewExecutor(
        IServiceProvider scopedProvider, Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(
            new HttpClient(new FakeShopifyHttpMessageHandler(respond)),
            scopedProvider.GetRequiredService<AppDbContext>(),
            scopedProvider.GetRequiredService<IIntegrationTokenProtector>(),
            Options.Create(new ShopifyOptions
            {
                ClientId = "unused", ClientSecret = "unused", RedirectUri = "unused", AdminApiVersion = AdminApiVersion,
            }),
            scopedProvider.GetRequiredService<IOrderCancellationService>());

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private const string AcceptedJobResponse =
        """{ "data": { "orderCancel": { "job": { "id": "gid://shopify/Job/1", "done": false }, "orderCancelUserErrors": [] } } }""";

    [Fact]
    public async Task ExecuteAsync_AcceptedJob_LeavesRowPending_DoesNotConfirm()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (businessId, customerId) = await CreateBusinessWithIntegrationAsync(db, scope.ServiceProvider);
        var rowId = await CreatePendingRowAsync(scope.ServiceProvider, businessId, customerId);

        var executor = NewExecutor(scope.ServiceProvider, _ => JsonResponse(AcceptedJobResponse));
        await executor.ExecuteAsync(rowId, CancellationToken.None);

        var row = await db.OrderCancellations.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == rowId);
        Assert.Equal(OrderCancellationStatus.Pending, row.Status);
    }

    [Fact]
    public async Task ExecuteAsync_OrderCancelUserErrors_MarksFailed()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (businessId, customerId) = await CreateBusinessWithIntegrationAsync(db, scope.ServiceProvider);
        var rowId = await CreatePendingRowAsync(scope.ServiceProvider, businessId, customerId);

        const string response =
            """{ "data": { "orderCancel": { "job": null, "orderCancelUserErrors": [ { "field": ["orderId"], "message": "Order already cancelled", "code": "ALREADY_CANCELLED" } ] } } }""";
        var executor = NewExecutor(scope.ServiceProvider, _ => JsonResponse(response));
        await executor.ExecuteAsync(rowId, CancellationToken.None);

        var row = await db.OrderCancellations.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == rowId);
        Assert.Equal(OrderCancellationStatus.Failed, row.Status);
        Assert.Contains("Order already cancelled", row.FailureReason);
        Assert.Null(row.CompletedAt);
    }

    [Fact]
    public async Task ExecuteAsync_NotPending_IsNoOp_NeverReExecutes()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var orderCancellationService = scope.ServiceProvider.GetRequiredService<IOrderCancellationService>();
        var (businessId, customerId) = await CreateBusinessWithIntegrationAsync(db, scope.ServiceProvider);
        var rowId = await CreatePendingRowAsync(scope.ServiceProvider, businessId, customerId);
        await orderCancellationService.MarkConfirmedAsync(rowId, CancellationToken.None);

        var callCount = 0;
        var executor = NewExecutor(scope.ServiceProvider, _ => { callCount++; return JsonResponse(AcceptedJobResponse); });
        await executor.ExecuteAsync(rowId, CancellationToken.None);

        Assert.Equal(0, callCount);
        var row = await db.OrderCancellations.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == rowId);
        Assert.Equal(OrderCancellationStatus.Confirmed, row.Status);
    }

    [Fact]
    public async Task ExecuteAsync_Throws_WhenRowDoesNotExist()
    {
        await using var scope = _provider.CreateAsyncScope();
        var executor = NewExecutor(scope.ServiceProvider, _ => JsonResponse(AcceptedJobResponse));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => executor.ExecuteAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteAsync_Throws_WhenIntegrationMissing()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var business = new Business
        {
            Id = Guid.NewGuid(), Name = "No Integration", TimeZone = "Europe/Copenhagen",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        var customer = new Customer
        {
            Id = Guid.NewGuid(), BusinessId = business.Id, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Businesses.Add(business);
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        _businessIdsToClean.Add(business.Id);

        var rowId = await CreatePendingRowAsync(scope.ServiceProvider, business.Id, customer.Id);

        var executor = NewExecutor(scope.ServiceProvider, _ => JsonResponse(AcceptedJobResponse));
        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync(rowId, CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task ExecuteAsync_Throws_OnNonSuccessStatus(HttpStatusCode status)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (businessId, customerId) = await CreateBusinessWithIntegrationAsync(db, scope.ServiceProvider);
        var rowId = await CreatePendingRowAsync(scope.ServiceProvider, businessId, customerId);

        var executor = NewExecutor(scope.ServiceProvider, _ => new HttpResponseMessage(status));
        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync(rowId, CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteAsync_Throws_OnMalformedJson()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (businessId, customerId) = await CreateBusinessWithIntegrationAsync(db, scope.ServiceProvider);
        var rowId = await CreatePendingRowAsync(scope.ServiceProvider, businessId, customerId);

        var executor = NewExecutor(scope.ServiceProvider, _ => JsonResponse("not json"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync(rowId, CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteAsync_Throws_OnTopLevelGraphQlErrors()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (businessId, customerId) = await CreateBusinessWithIntegrationAsync(db, scope.ServiceProvider);
        var rowId = await CreatePendingRowAsync(scope.ServiceProvider, businessId, customerId);

        var executor = NewExecutor(scope.ServiceProvider, _ => JsonResponse(
            """{ "errors": [ { "message": "Field 'orderCancel' doesn't exist" } ] }"""));
        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync(rowId, CancellationToken.None));
    }

    // ── Request-shape assertions — sends what the design says it must ────

    [Theory]
    [InlineData(RefundMethod.OriginalPaymentMethod, "originalPaymentMethodsRefund")]
    [InlineData(RefundMethod.StoreCredit, "storeCreditRefund")]
    public async Task ExecuteAsync_SendsExpectedRefundMethodShape(RefundMethod method, string expectedKey)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (businessId, customerId) = await CreateBusinessWithIntegrationAsync(db, scope.ServiceProvider);
        var rowId = await CreatePendingRowAsync(scope.ServiceProvider, businessId, customerId, method: method);

        string? capturedBody = null;
        var executor = NewExecutor(scope.ServiceProvider, req =>
        {
            capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse(AcceptedJobResponse);
        });
        await executor.ExecuteAsync(rowId, CancellationToken.None);

        Assert.NotNull(capturedBody);
        using var doc = JsonDocument.Parse(capturedBody!);
        var refundMethod = doc.RootElement.GetProperty("variables").GetProperty("refundMethod");
        Assert.True(refundMethod.TryGetProperty(expectedKey, out _));
    }

    [Fact]
    public async Task ExecuteAsync_NullMethod_SendsEmptyRefundMethodObject()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (businessId, customerId) = await CreateBusinessWithIntegrationAsync(db, scope.ServiceProvider);
        var rowId = await CreatePendingRowAsync(scope.ServiceProvider, businessId, customerId, method: null);

        string? capturedBody = null;
        var executor = NewExecutor(scope.ServiceProvider, req =>
        {
            capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse(AcceptedJobResponse);
        });
        await executor.ExecuteAsync(rowId, CancellationToken.None);

        using var doc = JsonDocument.Parse(capturedBody!);
        var refundMethod = doc.RootElement.GetProperty("variables").GetProperty("refundMethod");
        Assert.Equal(0, refundMethod.EnumerateObject().Count());
    }

    [Fact]
    public async Task ExecuteAsync_SendsMappedShopifyReasonString()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (businessId, customerId) = await CreateBusinessWithIntegrationAsync(db, scope.ServiceProvider);
        var rowId = await CreatePendingRowAsync(scope.ServiceProvider, businessId, customerId, reason: OrderCancelReason.StaffError);

        string? capturedBody = null;
        var executor = NewExecutor(scope.ServiceProvider, req =>
        {
            capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse(AcceptedJobResponse);
        });
        await executor.ExecuteAsync(rowId, CancellationToken.None);

        using var doc = JsonDocument.Parse(capturedBody!);
        Assert.Equal("STAFF_ERROR", doc.RootElement.GetProperty("variables").GetProperty("reason").GetString());
    }

    [Fact]
    public async Task ExecuteAsync_SendsAccessTokenHeaderAndVersionedUrl()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (businessId, customerId) = await CreateBusinessWithIntegrationAsync(db, scope.ServiceProvider);
        var rowId = await CreatePendingRowAsync(scope.ServiceProvider, businessId, customerId);

        HttpRequestMessage? capturedRequest = null;
        var executor = NewExecutor(scope.ServiceProvider, req =>
        {
            capturedRequest = req;
            return JsonResponse(AcceptedJobResponse);
        });
        await executor.ExecuteAsync(rowId, CancellationToken.None);

        Assert.Equal(
            $"https://{_shop}.myshopify.com/admin/api/{AdminApiVersion}/graphql.json",
            capturedRequest!.RequestUri!.ToString());
        Assert.Equal(AccessToken, capturedRequest.Headers.GetValues("X-Shopify-Access-Token").Single());
    }

    private class FixedTenantAccessor(Guid? businessId) : ICurrentTenantAccessor
    {
        public Guid? BusinessId => businessId;
    }
}
