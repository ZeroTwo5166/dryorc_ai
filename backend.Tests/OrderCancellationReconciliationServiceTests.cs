using System.Net;
using System.Text;
using backend.Data;
using backend.Enums;
using backend.Interfaces;
using backend.Models;
using backend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace backend.Tests;

/// <summary>
/// Service-level tests for OrderCancellationReconciliationService — real
/// Postgres, FakeShopifyHttpMessageHandler for the outbound
/// GetCancellationStateAsync call. Per the design handoff's approved scope:
/// no auto-retry, no ticket escalation — a stuck row is only logged.
/// </summary>
public class OrderCancellationReconciliationServiceTests : IAsyncLifetime
{
    private const string AdminApiVersion = "2026-07";
    private const string AccessToken = "test-access-token";
    private const string OrderId = "gid://shopify/Order/1";

    // Unique per test class instance — see ShopifyOrderCancellationExecutorTests
    // for why this can't be a shared literal.
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
            Name = $"Reconciliation Test {Guid.NewGuid():N}",
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
        AppDbContext db, IServiceProvider scopedProvider, Guid businessId, Guid customerId,
        DateTimeOffset? requestedAtOverride = null, string externalOrderId = OrderId)
    {
        var service = scopedProvider.GetRequiredService<IOrderCancellationService>();
        var result = await service.RequestCancellationAsync(
            businessId, customerId, null, externalOrderId, null, Guid.NewGuid().ToString("N"),
            restock: false, RefundMethod.OriginalPaymentMethod, OrderCancelReason.Customer, CancellationToken.None);

        if (requestedAtOverride is not null)
        {
            await db.OrderCancellations.IgnoreQueryFilters()
                .Where(x => x.Id == result.OrderCancellationId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.RequestedAt, requestedAtOverride.Value));
        }

        return result.OrderCancellationId;
    }

    private static ShopifyOrderLookupService NewLookupService(
        IServiceProvider scopedProvider, Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(
            new HttpClient(new FakeShopifyHttpMessageHandler(respond)),
            scopedProvider.GetRequiredService<AppDbContext>(),
            scopedProvider.GetRequiredService<IIntegrationTokenProtector>(),
            Options.Create(new ShopifyOptions
            {
                ClientId = "unused", ClientSecret = "unused", RedirectUri = "unused", AdminApiVersion = AdminApiVersion,
            }));

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string CancellationStateResponse(string orderId, DateTimeOffset? cancelledAt) =>
        cancelledAt is null
            ? $$"""{ "data": { "order": { "id": "{{orderId}}", "cancelledAt": null } } }"""
            : $$"""{ "data": { "order": { "id": "{{orderId}}", "cancelledAt": "{{cancelledAt.Value:O}}" } } }""";

    [Fact]
    public async Task ReconcileAsync_CancelledAtSet_MarksConfirmed()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (businessId, customerId) = await CreateBusinessWithIntegrationAsync(db, scope.ServiceProvider);
        var rowId = await CreatePendingRowAsync(db, scope.ServiceProvider, businessId, customerId);

        // ReconcileAsync sweeps ALL Pending rows across the database by
        // design — a blanket "always cancelled" response would incorrectly
        // confirm another concurrently-running test's unrelated row too,
        // so only requests for this test's own shop get that response.
        var lookup = NewLookupService(scope.ServiceProvider, req =>
            req.RequestUri!.Host.StartsWith(_shop, StringComparison.Ordinal)
                ? JsonResponse(CancellationStateResponse(OrderId, DateTimeOffset.UtcNow))
                : JsonResponse(CancellationStateResponse("gid://shopify/Order/ambient", null)));
        var orderCancellationService = scope.ServiceProvider.GetRequiredService<IOrderCancellationService>();
        var logger = new CapturingLogger<OrderCancellationReconciliationService>();
        var reconciliation = new OrderCancellationReconciliationService(db, lookup, orderCancellationService, logger);

        await reconciliation.ReconcileAsync(CancellationToken.None);

        var row = await db.OrderCancellations.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == rowId);
        Assert.Equal(OrderCancellationStatus.Confirmed, row.Status);
        Assert.NotNull(row.CompletedAt);
    }

    [Fact]
    public async Task ReconcileAsync_CancelledAtNull_RecentRow_LeavesPending_NoWarning()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (businessId, customerId) = await CreateBusinessWithIntegrationAsync(db, scope.ServiceProvider);
        var rowId = await CreatePendingRowAsync(db, scope.ServiceProvider, businessId, customerId);

        var lookup = NewLookupService(scope.ServiceProvider, _ => JsonResponse(CancellationStateResponse(OrderId, null)));
        var orderCancellationService = scope.ServiceProvider.GetRequiredService<IOrderCancellationService>();
        var logger = new CapturingLogger<OrderCancellationReconciliationService>();
        var reconciliation = new OrderCancellationReconciliationService(db, lookup, orderCancellationService, logger);

        await reconciliation.ReconcileAsync(CancellationToken.None);

        var row = await db.OrderCancellations.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == rowId);
        Assert.Equal(OrderCancellationStatus.Pending, row.Status);
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains(rowId.ToString()));
    }

    [Fact]
    public async Task ReconcileAsync_CancelledAtNull_StuckPastThreshold_LogsWarning_StaysPending_NoRetry()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (businessId, customerId) = await CreateBusinessWithIntegrationAsync(db, scope.ServiceProvider);
        var rowId = await CreatePendingRowAsync(
            db, scope.ServiceProvider, businessId, customerId, requestedAtOverride: DateTimeOffset.UtcNow.AddHours(-2));

        // See ReconcileAsync_AlreadyConfirmedRow_IsIgnored for why this is
        // filtered by _shop rather than a raw call count — the sweep also
        // processes any unrelated Pending row from a concurrently-running
        // test class.
        var callsToThisShop = 0;
        var lookup = NewLookupService(scope.ServiceProvider, req =>
        {
            if (req.RequestUri!.Host.StartsWith(_shop, StringComparison.Ordinal)) callsToThisShop++;
            return JsonResponse(CancellationStateResponse(OrderId, null));
        });
        var orderCancellationService = scope.ServiceProvider.GetRequiredService<IOrderCancellationService>();
        var logger = new CapturingLogger<OrderCancellationReconciliationService>();
        var reconciliation = new OrderCancellationReconciliationService(db, lookup, orderCancellationService, logger);

        await reconciliation.ReconcileAsync(CancellationToken.None);

        var row = await db.OrderCancellations.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == rowId);
        Assert.Equal(OrderCancellationStatus.Pending, row.Status);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains(rowId.ToString()));
        // Exactly one lookup call for this row — no automated retry of the
        // Shopify call itself.
        Assert.Equal(1, callsToThisShop);
    }

    [Fact]
    public async Task ReconcileAsync_AlreadyConfirmedRow_IsIgnored()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (businessId, customerId) = await CreateBusinessWithIntegrationAsync(db, scope.ServiceProvider);
        var rowId = await CreatePendingRowAsync(db, scope.ServiceProvider, businessId, customerId);
        var orderCancellationService = scope.ServiceProvider.GetRequiredService<IOrderCancellationService>();
        await orderCancellationService.MarkConfirmedAsync(rowId, CancellationToken.None);

        // ReconcileAsync sweeps ALL Pending rows across the database by
        // design (a real production background job, not scoped to one
        // business) — other test classes running in parallel may have
        // their own unrelated Pending rows in flight at the same time.
        // Filtering by _shop (unique per test class instance) isolates
        // this assertion to calls this test's own row could have caused,
        // without asserting on the sweep's total call count.
        var callsToThisShop = 0;
        var lookup = NewLookupService(scope.ServiceProvider, req =>
        {
            if (req.RequestUri!.Host.StartsWith(_shop, StringComparison.Ordinal)) callsToThisShop++;
            return JsonResponse(CancellationStateResponse(OrderId, DateTimeOffset.UtcNow));
        });
        var logger = new CapturingLogger<OrderCancellationReconciliationService>();
        var reconciliation = new OrderCancellationReconciliationService(db, lookup, orderCancellationService, logger);

        await reconciliation.ReconcileAsync(CancellationToken.None);

        Assert.Equal(0, callsToThisShop);
    }

    [Fact]
    public async Task ReconcileAsync_OneRowThrows_OthersStillProcessed()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (businessId, customerId) = await CreateBusinessWithIntegrationAsync(db, scope.ServiceProvider);
        const string brokenOrderId = "gid://shopify/Order/broken";
        const string healthyOrderId = "gid://shopify/Order/healthy";
        var brokenRowId = await CreatePendingRowAsync(db, scope.ServiceProvider, businessId, customerId, externalOrderId: brokenOrderId);
        var healthyRowId = await CreatePendingRowAsync(db, scope.ServiceProvider, businessId, customerId, externalOrderId: healthyOrderId);

        // Routed by which order id the request actually targets, not by
        // call order — the sweep's row order is not guaranteed, and the
        // point of this test is "one row's failure doesn't stop the rest,"
        // not "the first row processed fails." Requests for any other
        // shop (an unrelated Pending row from a concurrently-running test
        // class — ReconcileAsync sweeps ALL businesses by design) get a
        // neutral "not cancelled" response, never the "healthy" success
        // response — otherwise this test would incorrectly confirm another
        // test's row.
        var seenTargetOrderIds = new HashSet<string>();
        var lookup = NewLookupService(scope.ServiceProvider, req =>
        {
            if (!req.RequestUri!.Host.StartsWith(_shop, StringComparison.Ordinal))
            {
                return JsonResponse(CancellationStateResponse("gid://shopify/Order/ambient", null));
            }

            var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            if (body.Contains(brokenOrderId))
            {
                seenTargetOrderIds.Add(brokenOrderId);
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }

            seenTargetOrderIds.Add(healthyOrderId);
            return JsonResponse(CancellationStateResponse(healthyOrderId, DateTimeOffset.UtcNow));
        });
        var orderCancellationService = scope.ServiceProvider.GetRequiredService<IOrderCancellationService>();
        var logger = new CapturingLogger<OrderCancellationReconciliationService>();
        var reconciliation = new OrderCancellationReconciliationService(db, lookup, orderCancellationService, logger);

        await reconciliation.ReconcileAsync(CancellationToken.None);

        var rows = await db.OrderCancellations.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.Id == brokenRowId || x.Id == healthyRowId)
            .ToDictionaryAsync(x => x.Id, x => x.Status);
        Assert.Equal([brokenOrderId, healthyOrderId], seenTargetOrderIds.Order());
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error);
        Assert.Equal(OrderCancellationStatus.Pending, rows[brokenRowId]);
        Assert.Equal(OrderCancellationStatus.Confirmed, rows[healthyRowId]);
    }

    private class FixedTenantAccessor(Guid? businessId) : ICurrentTenantAccessor
    {
        public Guid? BusinessId => businessId;
    }
}
