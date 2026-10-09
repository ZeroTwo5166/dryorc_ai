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
/// Service-level tests for ShopifyRefundExecutor — real Postgres for
/// Business/Integration/Refund/RefundItem rows (same convention as
/// ShopifyOrderCancellationExecutorTests), FakeShopifyHttpMessageHandler for
/// the outbound refundCreate call.
/// </summary>
public class ShopifyRefundExecutorTests : IAsyncLifetime
{
    private const string AdminApiVersion = "2026-07";
    private const string AccessToken = "test-access-token";
    private const string OrderId = "gid://shopify/Order/1";
    private const string LineItemId = "gid://shopify/LineItem/1";

    private readonly string _shop = $"test-shop-{Guid.NewGuid():N}";

    private ServiceProvider _provider = null!;
    private readonly List<Guid> _businessIdsToClean = [];

    public Task InitializeAsync()
    {
        var services = new ServiceCollection()
            .AddSingleton<ICurrentTenantAccessor>(new FixedTenantAccessor(null))
            .AddDbContext<AppDbContext>(o => o.UseNpgsql(
                TestDatabase.ConnectionString, x => x.UseVector()))
            .AddScoped<IRefundService, RefundService>();
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
            Name = $"Refund Executor Test {Guid.NewGuid():N}",
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
        RefundMethod method = RefundMethod.OriginalPaymentMethod, int quantity = 2, decimal resolvedAmount = 19.99m)
    {
        var service = scopedProvider.GetRequiredService<IRefundService>();
        var result = await service.RequestRefundAsync(
            businessId, customerId, null, OrderId, null, Guid.NewGuid().ToString("N"),
            method, "EUR", [new RefundItemLine(LineItemId, quantity, resolvedAmount)], CancellationToken.None);
        return result.RefundId;
    }

    private static ShopifyRefundExecutor NewExecutor(
        IServiceProvider scopedProvider, Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(
            new HttpClient(new FakeShopifyHttpMessageHandler(respond)),
            scopedProvider.GetRequiredService<AppDbContext>(),
            scopedProvider.GetRequiredService<IIntegrationTokenProtector>(),
            Options.Create(new ShopifyOptions
            {
                ClientId = "unused", ClientSecret = "unused", RedirectUri = "unused", AdminApiVersion = AdminApiVersion,
            }),
            scopedProvider.GetRequiredService<IRefundService>());

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private const string SuccessResponse =
        """{ "data": { "refundCreate": { "refund": { "id": "gid://shopify/Refund/1", "totalRefundedSet": { "presentmentMoney": { "amount": "39.98" } } }, "userErrors": [] } } }""";

    [Fact]
    public async Task ExecuteAsync_Success_MarksConfirmed_WithShopifyResolvedAmount()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (businessId, customerId) = await CreateBusinessWithIntegrationAsync(db, scope.ServiceProvider);
        var rowId = await CreatePendingRowAsync(scope.ServiceProvider, businessId, customerId);

        var executor = NewExecutor(scope.ServiceProvider, _ => JsonResponse(SuccessResponse));
        await executor.ExecuteAsync(rowId, "test-idempotency-key", CancellationToken.None);

        var row = await db.Refunds.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == rowId);
        Assert.Equal(RefundStatus.Confirmed, row.Status);
        Assert.Equal("gid://shopify/Refund/1", row.ExternalRefundId);
        Assert.Equal(39.98m, row.TotalAmount);
        Assert.NotNull(row.CompletedAt);
    }

    [Fact]
    public async Task ExecuteAsync_UserErrors_MarksFailed_NeverConfirmed()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (businessId, customerId) = await CreateBusinessWithIntegrationAsync(db, scope.ServiceProvider);
        var rowId = await CreatePendingRowAsync(scope.ServiceProvider, businessId, customerId);

        const string response =
            """{ "data": { "refundCreate": { "refund": null, "userErrors": [ { "field": ["refundLineItems"], "message": "Quantity exceeds refundable quantity", "code": "INSUFFICIENT_QUANTITY" } ] } } }""";
        var executor = NewExecutor(scope.ServiceProvider, _ => JsonResponse(response));
        await executor.ExecuteAsync(rowId, "test-idempotency-key", CancellationToken.None);

        var row = await db.Refunds.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == rowId);
        Assert.Equal(RefundStatus.Failed, row.Status);
        Assert.Contains("Quantity exceeds refundable quantity", row.FailureReason);
        Assert.Contains("INSUFFICIENT_QUANTITY", row.FailureReason);
        Assert.Null(row.CompletedAt);
        Assert.Null(row.ExternalRefundId);
        Assert.Null(row.TotalAmount);

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
        var refundService = scope.ServiceProvider.GetRequiredService<IRefundService>();
        var (businessId, customerId) = await CreateBusinessWithIntegrationAsync(db, scope.ServiceProvider);
        var rowId = await CreatePendingRowAsync(scope.ServiceProvider, businessId, customerId);
        await refundService.MarkConfirmedAsync(rowId, "gid://shopify/Refund/existing", 10m, CancellationToken.None);

        var callCount = 0;
        var executor = NewExecutor(scope.ServiceProvider, _ => { callCount++; return JsonResponse(SuccessResponse); });
        await executor.ExecuteAsync(rowId, "test-idempotency-key", CancellationToken.None);

        Assert.Equal(0, callCount);
        var row = await db.Refunds.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == rowId);
        Assert.Equal("gid://shopify/Refund/existing", row.ExternalRefundId);
    }

    [Fact]
    public async Task ExecuteAsync_Throws_WhenRowDoesNotExist()
    {
        await using var scope = _provider.CreateAsyncScope();
        var executor = NewExecutor(scope.ServiceProvider, _ => JsonResponse(SuccessResponse));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => executor.ExecuteAsync(Guid.NewGuid(), "key", CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteAsync_Throws_WhenIntegrationMissing()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var business = new Business
        {
            Id = Guid.NewGuid(), Name = "No Integration Refund", TimeZone = "Europe/Copenhagen",
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

        var executor = NewExecutor(scope.ServiceProvider, _ => JsonResponse(SuccessResponse));
        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync(rowId, "key", CancellationToken.None));
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
        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync(rowId, "key", CancellationToken.None));

        var row = await db.Refunds.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == rowId);
        Assert.Equal(RefundStatus.Pending, row.Status);

        var integration = await db.Integrations.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(i => i.BusinessId == businessId && i.Platform == ShopifyIntegrationOAuthProvider.PlatformName);
        Assert.Null(integration.ReauthorizationRequiredAt);
    }

    /// <summary>
    /// Step Shopify-auth-reauth (design §6/§7) — Decision 7 items 1/2: HTTP
    /// 401/403 are classified as auth failures. Never an uncaught exception,
    /// never a status quo Pending row — MarkFailedAsync is called with an
    /// auth-marked FailureReason and Integration.ReauthorizationRequiredAt
    /// is set, mirroring the existing userErrors branch's own return-normally
    /// shape.
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
        await executor.ExecuteAsync(rowId, "key", CancellationToken.None);

        var row = await db.Refunds.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == rowId);
        Assert.Equal(RefundStatus.Failed, row.Status);
        Assert.NotNull(row.FailureReason);
        Assert.StartsWith(ShopifyFailureReasons.ReauthorizationRequiredPrefix, row.FailureReason);
        Assert.Contains(((int)status).ToString(), row.FailureReason);
        Assert.Null(row.CompletedAt);
        Assert.Null(row.ExternalRefundId);

        var integration = await db.Integrations.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(i => i.BusinessId == businessId && i.Platform == ShopifyIntegrationOAuthProvider.PlatformName);
        Assert.NotNull(integration.ReauthorizationRequiredAt);
        Assert.True(integration.IsActive); // Decision 1/2 — never deactivated by an auth failure
    }

    /// <summary>
    /// Decision 7 item 3 — any exception from IIntegrationTokenProtector.Unprotect
    /// is classified identically to the HTTP 401/403 cases above. Corrupts
    /// the stored EncryptedAccessToken directly (bypassing Protect) so
    /// Unprotect genuinely throws, never reaching the HTTP call at all.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_TokenDecryptFailure_MarksFailed_WithAuthReason_SetsReauthorizationRequired()
    {
        await using var seedScope = _provider.CreateAsyncScope();
        var seedDb = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (businessId, customerId) = await CreateBusinessWithIntegrationAsync(seedDb, seedScope.ServiceProvider);
        var rowId = await CreatePendingRowAsync(seedScope.ServiceProvider, businessId, customerId);

        // A fresh scope for both the corruption write and the executor
        // itself — never one that has this Integration already tracked.
        // ExecuteUpdateAsync bypasses the change tracker on write but does
        // not invalidate an already-tracked in-memory instance elsewhere,
        // so reusing seedScope's own `seedDb` for either side would let the
        // executor's tracked (non-AsNoTracking) read silently see the
        // stale, uncorrupted value regardless of what actually reached
        // Postgres. Tampers a validly-protected ciphertext byte-for-byte —
        // same proven technique as IntegrationTokenProtectorTests.
        // Unprotect_TamperedCiphertext_ThrowsRatherThanReturningGarbage —
        // rather than a cross-purpose Protect/Unprotect pair, since this
        // bare ServiceCollection's AddDataProtection() (no persisted key
        // ring, unlike NivoraApiFactory's real Program.cs-configured one)
        // does not reliably enforce purpose separation on its own, but
        // authenticated-encryption tampering detection holds regardless.
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
        await executor.ExecuteAsync(rowId, "key", CancellationToken.None);

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await verifyDb.Refunds.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == rowId);
        Assert.Equal(RefundStatus.Failed, row.Status);
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
        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync(rowId, "key", CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteAsync_Throws_OnTopLevelGraphQlErrors()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (businessId, customerId) = await CreateBusinessWithIntegrationAsync(db, scope.ServiceProvider);
        var rowId = await CreatePendingRowAsync(scope.ServiceProvider, businessId, customerId);

        var executor = NewExecutor(scope.ServiceProvider, _ => JsonResponse(
            """{ "errors": [ { "message": "Field 'refundCreate' doesn't exist" } ] }"""));
        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync(rowId, "key", CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteAsync_Throws_WhenNeitherRefundNorUserErrorsPresent()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (businessId, customerId) = await CreateBusinessWithIntegrationAsync(db, scope.ServiceProvider);
        var rowId = await CreatePendingRowAsync(scope.ServiceProvider, businessId, customerId);

        var executor = NewExecutor(scope.ServiceProvider, _ => JsonResponse(
            """{ "data": { "refundCreate": { "refund": null, "userErrors": [] } } }"""));
        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync(rowId, "key", CancellationToken.None));

        var row = await db.Refunds.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == rowId);
        Assert.Equal(RefundStatus.Pending, row.Status);
    }

    // ── Request-shape assertions ─────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_SendsIdempotentDirectiveKeyAsVariable()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (businessId, customerId) = await CreateBusinessWithIntegrationAsync(db, scope.ServiceProvider);
        var rowId = await CreatePendingRowAsync(scope.ServiceProvider, businessId, customerId);

        string? capturedBody = null;
        var executor = NewExecutor(scope.ServiceProvider, req =>
        {
            capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse(SuccessResponse);
        });
        await executor.ExecuteAsync(rowId, "my-execution-key-123", CancellationToken.None);

        Assert.NotNull(capturedBody);
        using var doc = JsonDocument.Parse(capturedBody!);
        Assert.Equal("my-execution-key-123", doc.RootElement.GetProperty("variables").GetProperty("idempotencyKey").GetString());
        Assert.Contains("@idempotent(key: $idempotencyKey)", doc.RootElement.GetProperty("query").GetString());
    }

    [Fact]
    public async Task ExecuteAsync_SendsItemQuantityOnly_NoAmountField()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (businessId, customerId) = await CreateBusinessWithIntegrationAsync(db, scope.ServiceProvider);
        var rowId = await CreatePendingRowAsync(scope.ServiceProvider, businessId, customerId, quantity: 3, resolvedAmount: 29.97m);

        string? capturedBody = null;
        var executor = NewExecutor(scope.ServiceProvider, req =>
        {
            capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse(SuccessResponse);
        });
        await executor.ExecuteAsync(rowId, "key", CancellationToken.None);

        using var doc = JsonDocument.Parse(capturedBody!);
        var refundLineItems = doc.RootElement.GetProperty("variables").GetProperty("input").GetProperty("refundLineItems");
        Assert.Equal(1, refundLineItems.GetArrayLength());
        var line = refundLineItems[0];
        Assert.Equal(LineItemId, line.GetProperty("lineItemId").GetString());
        Assert.Equal(3, line.GetProperty("quantity").GetInt32());
        Assert.False(line.TryGetProperty("amount", out _));
    }

    [Fact]
    public async Task ExecuteAsync_SendsAllowOverRefundingFalse()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (businessId, customerId) = await CreateBusinessWithIntegrationAsync(db, scope.ServiceProvider);
        var rowId = await CreatePendingRowAsync(scope.ServiceProvider, businessId, customerId);

        string? capturedBody = null;
        var executor = NewExecutor(scope.ServiceProvider, req =>
        {
            capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse(SuccessResponse);
        });
        await executor.ExecuteAsync(rowId, "key", CancellationToken.None);

        using var doc = JsonDocument.Parse(capturedBody!);
        Assert.False(doc.RootElement.GetProperty("variables").GetProperty("input").GetProperty("allowOverRefunding").GetBoolean());
    }

    [Theory]
    [InlineData(RefundMethod.OriginalPaymentMethod)]
    [InlineData(RefundMethod.StoreCredit)]
    public async Task ExecuteAsync_SendsRefundMethodsShape(RefundMethod method)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (businessId, customerId) = await CreateBusinessWithIntegrationAsync(db, scope.ServiceProvider);
        var rowId = await CreatePendingRowAsync(scope.ServiceProvider, businessId, customerId, method: method);

        string? capturedBody = null;
        var executor = NewExecutor(scope.ServiceProvider, req =>
        {
            capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse(SuccessResponse);
        });
        await executor.ExecuteAsync(rowId, "key", CancellationToken.None);

        using var doc = JsonDocument.Parse(capturedBody!);
        var refundMethods = doc.RootElement.GetProperty("variables").GetProperty("input").GetProperty("refundMethods");
        if (method == RefundMethod.OriginalPaymentMethod)
        {
            Assert.Equal(0, refundMethods.GetArrayLength());
        }
        else
        {
            Assert.Equal(1, refundMethods.GetArrayLength());
            Assert.True(refundMethods[0].TryGetProperty("storeCreditRefund", out _));
        }
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
            return JsonResponse(SuccessResponse);
        });
        await executor.ExecuteAsync(rowId, "key", CancellationToken.None);

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
