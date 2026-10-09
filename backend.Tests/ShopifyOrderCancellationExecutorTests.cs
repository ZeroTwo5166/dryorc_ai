using System.Net;
using System.Text;
using System.Text.Json;
using backend.Constants;
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
        var services = new ServiceCollection()
            .AddSingleton<ICurrentTenantAccessor>(new FixedTenantAccessor(null))
            .AddDbContext<AppDbContext>(o => o.UseNpgsql(
                TestDatabase.ConnectionString, x => x.UseVector()))
            .AddScoped<IOrderCancellationService, OrderCancellationService>();
        services.AddDataProtection();
        services.AddScoped<IIntegrationTokenProtector, IntegrationTokenProtector>();
        _provider = services.BuildServiceProvider();

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await TestBusinessCleanup.DeleteThenDisposeAsync(_businessIdsToClean, _provider);
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

        // A userErrors rejection must never be misclassified as an auth
        // failure (Decision 7: "existing GraphQL userErrors handling must
        // remain separate and unchanged").
        Assert.DoesNotContain(ShopifyFailureReasons.ReauthorizationRequiredPrefix, row.FailureReason);
        var integration = await db.Integrations.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(i => i.BusinessId == businessId && i.Platform == ShopifyIntegrationOAuthProvider.PlatformName);
        Assert.Null(integration.ReauthorizationRequiredAt);
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

    /// <summary>
    /// Decision 7 explicitly excludes these statuses from auth
    /// classification (401/403 moved to their own dedicated tests below) —
    /// every other non-success status keeps the original, unchanged
    /// uncaught-throw behavior.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task ExecuteAsync_Throws_OnNonSuccessStatus(HttpStatusCode status)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (businessId, customerId) = await CreateBusinessWithIntegrationAsync(db, scope.ServiceProvider);
        var rowId = await CreatePendingRowAsync(scope.ServiceProvider, businessId, customerId);

        var executor = NewExecutor(scope.ServiceProvider, _ => new HttpResponseMessage(status));
        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync(rowId, CancellationToken.None));

        var row = await db.OrderCancellations.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == rowId);
        Assert.Equal(OrderCancellationStatus.Pending, row.Status);

        var integration = await db.Integrations.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(i => i.BusinessId == businessId && i.Platform == ShopifyIntegrationOAuthProvider.PlatformName);
        Assert.Null(integration.ReauthorizationRequiredAt);
    }

    /// <summary>
    /// Step Shopify-auth-reauth (design §6/§8) — Decision 7 items 1/2: HTTP
    /// 401/403 are classified as auth failures. Never an uncaught exception
    /// — MarkFailedAsync is called with an auth-marked FailureReason and
    /// Integration.ReauthorizationRequiredAt is set.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task ExecuteAsync_ClassifiesHttp401And403_MarksFailed_WithAuthReason_SetsReauthorizationRequired(HttpStatusCode status)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (businessId, customerId) = await CreateBusinessWithIntegrationAsync(db, scope.ServiceProvider);
        var rowId = await CreatePendingRowAsync(scope.ServiceProvider, businessId, customerId);

        var executor = NewExecutor(scope.ServiceProvider, _ => new HttpResponseMessage(status));
        await executor.ExecuteAsync(rowId, CancellationToken.None);

        var row = await db.OrderCancellations.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == rowId);
        Assert.Equal(OrderCancellationStatus.Failed, row.Status);
        Assert.NotNull(row.FailureReason);
        Assert.StartsWith(ShopifyFailureReasons.ReauthorizationRequiredPrefix, row.FailureReason);
        Assert.Contains(((int)status).ToString(), row.FailureReason);
        Assert.Null(row.CompletedAt);

        var integration = await db.Integrations.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(i => i.BusinessId == businessId && i.Platform == ShopifyIntegrationOAuthProvider.PlatformName);
        Assert.NotNull(integration.ReauthorizationRequiredAt);
        Assert.True(integration.IsActive); // Decision 1/2 — never deactivated by an auth failure
    }

    /// <summary>
    /// Decision 7 item 3 — any exception from IIntegrationTokenProtector.Unprotect
    /// is classified identically to the HTTP 401/403 cases above.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_TokenDecryptFailure_MarksFailed_WithAuthReason_SetsReauthorizationRequired()
    {
        await using var seedScope = _provider.CreateAsyncScope();
        var seedDb = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (businessId, customerId) = await CreateBusinessWithIntegrationAsync(seedDb, seedScope.ServiceProvider);
        var rowId = await CreatePendingRowAsync(seedScope.ServiceProvider, businessId, customerId);

        // A fresh scope for both the corruption write and the executor
        // itself — see ShopifyRefundExecutorTests's identical test for the
        // full rationale (EF's change tracker would otherwise mask the
        // corrupted value via seedDb's own already-tracked Integration
        // instance). Tampers a validly-protected ciphertext byte-for-byte —
        // same proven technique as IntegrationTokenProtectorTests.
        // Unprotect_TamperedCiphertext_ThrowsRatherThanReturningGarbage.
        await using var execScope = _provider.CreateAsyncScope();
        var execDb = execScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var execTokenProtector = execScope.ServiceProvider.GetRequiredService<IIntegrationTokenProtector>();
        var validCiphertext = execTokenProtector.Protect(ShopifyIntegrationOAuthProvider.PlatformName, AccessToken);
        var tamperedChars = validCiphertext.ToCharArray();
        var mid = tamperedChars.Length / 2;
        tamperedChars[mid] = tamperedChars[mid] == 'A' ? 'B' : 'A';
        var tamperedCiphertext = new string(tamperedChars);
        await execDb.Integrations
            .IgnoreQueryFilters()
            .Where(i => i.BusinessId == businessId)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.EncryptedAccessToken, tamperedCiphertext));

        var executor = NewExecutor(execScope.ServiceProvider, _ => throw new InvalidOperationException(
            "The Shopify HTTP call must not be reached — token decryption should fail first."));
        await executor.ExecuteAsync(rowId, CancellationToken.None);

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await verifyDb.OrderCancellations.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == rowId);
        Assert.Equal(OrderCancellationStatus.Failed, row.Status);
        Assert.StartsWith(ShopifyFailureReasons.ReauthorizationRequiredPrefix, row.FailureReason);

        var integration = await verifyDb.Integrations.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(i => i.BusinessId == businessId && i.Platform == ShopifyIntegrationOAuthProvider.PlatformName);
        Assert.NotNull(integration.ReauthorizationRequiredAt);
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

    // Payment -> "DECLINED" and StaffError -> "STAFF" (not "PAYMENT"/
    // "STAFF_ERROR") per Shopify's actual OrderCancelReason enum values,
    // independently verified against shopify.dev — a prior version of this
    // mapping/test asserted the wrong wire strings for both.
    [Theory]
    [InlineData(OrderCancelReason.Customer, "CUSTOMER")]
    [InlineData(OrderCancelReason.Payment, "DECLINED")]
    [InlineData(OrderCancelReason.Fraud, "FRAUD")]
    [InlineData(OrderCancelReason.Inventory, "INVENTORY")]
    [InlineData(OrderCancelReason.StaffError, "STAFF")]
    [InlineData(OrderCancelReason.Other, "OTHER")]
    public async Task ExecuteAsync_SendsMappedShopifyReasonString(OrderCancelReason reason, string expectedShopifyValue)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (businessId, customerId) = await CreateBusinessWithIntegrationAsync(db, scope.ServiceProvider);
        var rowId = await CreatePendingRowAsync(scope.ServiceProvider, businessId, customerId, reason: reason);

        string? capturedBody = null;
        var executor = NewExecutor(scope.ServiceProvider, req =>
        {
            capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse(AcceptedJobResponse);
        });
        await executor.ExecuteAsync(rowId, CancellationToken.None);

        using var doc = JsonDocument.Parse(capturedBody!);
        Assert.Equal(expectedShopifyValue, doc.RootElement.GetProperty("variables").GetProperty("reason").GetString());
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
