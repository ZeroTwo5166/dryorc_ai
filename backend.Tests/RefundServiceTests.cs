using backend.Data;
using backend.Enums;
using backend.Interfaces;
using backend.Models;
using backend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace backend.Tests;

/// <summary>
/// Service-level tests for RefundService — real Postgres, same convention
/// as OrderCancellationServiceTests. Step refundOrder Phase 1: exercises
/// only the persistence/idempotency mechanism itself, per
/// docs/handoffs/step-refundorder-design.md. No AI proposal tool, no
/// Shopify-calling executor exist yet — this file does not (and cannot)
/// exercise those.
/// </summary>
public class RefundServiceTests : IAsyncLifetime
{
    private const string OrderId = "gid://shopify/Order/1";
    private const RefundMethod DefaultMethod = RefundMethod.OriginalPaymentMethod;
    private const string DefaultCurrency = "EUR";

    private ServiceProvider _provider = null!;
    private readonly List<Guid> _businessIdsToClean = [];

    public Task InitializeAsync()
    {
        _provider = new ServiceCollection()
            .AddSingleton<ICurrentTenantAccessor>(new FixedTenantAccessor(null))
            .AddDbContext<AppDbContext>(o => o.UseNpgsql(
                TestDatabase.ConnectionString, x => x.UseVector()))
            .AddScoped<IRefundService, RefundService>()
            .BuildServiceProvider();

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await TestBusinessCleanup.DeleteThenDisposeAsync(_businessIdsToClean, _provider);
    }

    private async Task<Guid> CreateBusinessAsync(AppDbContext db)
    {
        var business = new Business
        {
            Id = Guid.NewGuid(),
            Name = $"Refund Test {Guid.NewGuid():N}",
            TimeZone = "Europe/Copenhagen",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Businesses.Add(business);
        await db.SaveChangesAsync();
        _businessIdsToClean.Add(business.Id);
        return business.Id;
    }

    private static async Task<Guid> CreateCustomerAsync(AppDbContext db, Guid businessId)
    {
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        return customer.Id;
    }

    private static IReadOnlyList<RefundItemLine> OneItem(string lineItemId = "gid://shopify/LineItem/1", int quantity = 1, decimal amount = 10m) =>
        [new RefundItemLine(lineItemId, quantity, amount)];

    // ── Fresh request ────────────────────────────────────────────────

    [Fact]
    public async Task RequestRefundAsync_FreshKey_CreatesPendingRowWithItems_ReturnsInProgress()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IRefundService>();

        var businessId = await CreateBusinessAsync(db);
        var customerId = await CreateCustomerAsync(db, businessId);
        var key = Guid.NewGuid().ToString("N");
        var items = new List<RefundItemLine>
        {
            new("gid://shopify/LineItem/1", 2, 20m),
            new("gid://shopify/LineItem/2", 1, 5m),
        };

        var result = await service.RequestRefundAsync(
            businessId, customerId, null, OrderId, "changed mind", key, DefaultMethod, DefaultCurrency, items,
            CancellationToken.None);

        Assert.Equal(IdempotentOperationOutcome.InProgress, result.Outcome);
        Assert.Null(result.FailureReason);

        var row = await db.Refunds.IgnoreQueryFilters().SingleAsync(x => x.Id == result.RefundId);
        Assert.Equal(RefundStatus.Pending, row.Status);
        Assert.Null(row.CompletedAt);
        Assert.Null(row.TotalAmount);
        Assert.Equal(DefaultMethod, row.Method);
        Assert.Equal(OrderId, row.ExternalOrderId);
        Assert.Equal(DefaultCurrency, row.Currency);

        var persistedItems = await db.RefundItems.IgnoreQueryFilters()
            .Where(x => x.RefundId == result.RefundId)
            .OrderBy(x => x.ExternalLineItemId)
            .ToListAsync();
        Assert.Equal(2, persistedItems.Count);
        Assert.Equal(2, persistedItems[0].Quantity);
        Assert.Equal(20m, persistedItems[0].Amount);
        Assert.Equal(1, persistedItems[1].Quantity);
        Assert.Equal(5m, persistedItems[1].Amount);
    }

    // ── Input/domain safety ─────────────────────────────────────────────

    [Fact]
    public async Task RequestRefundAsync_EmptyItemList_Throws()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IRefundService>();

        var businessId = await CreateBusinessAsync(db);
        var customerId = await CreateCustomerAsync(db, businessId);

        await Assert.ThrowsAsync<ArgumentException>(() => service.RequestRefundAsync(
            businessId, customerId, null, OrderId, null, Guid.NewGuid().ToString("N"), DefaultMethod,
            DefaultCurrency, [], CancellationToken.None));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task RequestRefundAsync_NonPositiveQuantity_Throws(int quantity)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IRefundService>();

        var businessId = await CreateBusinessAsync(db);
        var customerId = await CreateCustomerAsync(db, businessId);
        var items = new List<RefundItemLine> { new("gid://shopify/LineItem/1", quantity, 10m) };

        await Assert.ThrowsAsync<ArgumentException>(() => service.RequestRefundAsync(
            businessId, customerId, null, OrderId, null, Guid.NewGuid().ToString("N"), DefaultMethod,
            DefaultCurrency, items, CancellationToken.None));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task RequestRefundAsync_NonPositiveResolvedAmount_Throws(decimal amount)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IRefundService>();

        var businessId = await CreateBusinessAsync(db);
        var customerId = await CreateCustomerAsync(db, businessId);
        var items = new List<RefundItemLine> { new("gid://shopify/LineItem/1", 1, amount) };

        await Assert.ThrowsAsync<ArgumentException>(() => service.RequestRefundAsync(
            businessId, customerId, null, OrderId, null, Guid.NewGuid().ToString("N"), DefaultMethod,
            DefaultCurrency, items, CancellationToken.None));
    }

    [Fact]
    public async Task RequestRefundAsync_DuplicateLineItemWithinRequest_Throws()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IRefundService>();

        var businessId = await CreateBusinessAsync(db);
        var customerId = await CreateCustomerAsync(db, businessId);
        var items = new List<RefundItemLine>
        {
            new("gid://shopify/LineItem/1", 1, 10m),
            new("gid://shopify/LineItem/1", 1, 5m),
        };

        await Assert.ThrowsAsync<ArgumentException>(() => service.RequestRefundAsync(
            businessId, customerId, null, OrderId, null, Guid.NewGuid().ToString("N"), DefaultMethod,
            DefaultCurrency, items, CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RequestRefundAsync_BlankIdempotencyKey_Throws(string key)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IRefundService>();

        var businessId = await CreateBusinessAsync(db);
        var customerId = await CreateCustomerAsync(db, businessId);

        await Assert.ThrowsAsync<ArgumentException>(() => service.RequestRefundAsync(
            businessId, customerId, null, OrderId, null, key, DefaultMethod, DefaultCurrency, OneItem(),
            CancellationToken.None));
    }

    [Fact]
    public async Task RequestRefundAsync_TooLongIdempotencyKey_Throws()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IRefundService>();

        var businessId = await CreateBusinessAsync(db);
        var customerId = await CreateCustomerAsync(db, businessId);
        var key = new string('k', 257);

        await Assert.ThrowsAsync<ArgumentException>(() => service.RequestRefundAsync(
            businessId, customerId, null, OrderId, null, key, DefaultMethod, DefaultCurrency, OneItem(),
            CancellationToken.None));
    }

    [Fact]
    public async Task RequestRefundAsync_BlankExternalOrderId_Throws()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IRefundService>();

        var businessId = await CreateBusinessAsync(db);
        var customerId = await CreateCustomerAsync(db, businessId);

        await Assert.ThrowsAsync<ArgumentException>(() => service.RequestRefundAsync(
            businessId, customerId, null, "   ", null, Guid.NewGuid().ToString("N"), DefaultMethod,
            DefaultCurrency, OneItem(), CancellationToken.None));
    }

    [Fact]
    public async Task RequestRefundAsync_BlankCurrency_Throws()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IRefundService>();

        var businessId = await CreateBusinessAsync(db);
        var customerId = await CreateCustomerAsync(db, businessId);

        await Assert.ThrowsAsync<ArgumentException>(() => service.RequestRefundAsync(
            businessId, customerId, null, OrderId, null, Guid.NewGuid().ToString("N"), DefaultMethod,
            "   ", OneItem(), CancellationToken.None));
    }

    // ── Idempotency ──────────────────────────────────────────────────────

    [Fact]
    public async Task RequestRefundAsync_DuplicateWhilePending_ReturnsInProgress_NoSecondRow()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IRefundService>();

        var businessId = await CreateBusinessAsync(db);
        var customerId = await CreateCustomerAsync(db, businessId);
        var key = Guid.NewGuid().ToString("N");

        var first = await service.RequestRefundAsync(
            businessId, customerId, null, OrderId, null, key, DefaultMethod, DefaultCurrency, OneItem(),
            CancellationToken.None);
        var second = await service.RequestRefundAsync(
            businessId, customerId, null, OrderId, null, key, DefaultMethod, DefaultCurrency, OneItem(),
            CancellationToken.None);

        Assert.Equal(IdempotentOperationOutcome.InProgress, second.Outcome);
        Assert.Equal(first.RefundId, second.RefundId);

        var count = await db.Refunds.IgnoreQueryFilters()
            .CountAsync(x => x.BusinessId == businessId && x.IdempotencyKey == key);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task RequestRefundAsync_ItemOrderShuffled_StillTreatedAsSameIntent()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IRefundService>();

        var businessId = await CreateBusinessAsync(db);
        var customerId = await CreateCustomerAsync(db, businessId);
        var key = Guid.NewGuid().ToString("N");

        var firstOrder = new List<RefundItemLine>
        {
            new("gid://shopify/LineItem/1", 1, 10m),
            new("gid://shopify/LineItem/2", 2, 20m),
        };
        var shuffledOrder = new List<RefundItemLine>
        {
            new("gid://shopify/LineItem/2", 2, 20m),
            new("gid://shopify/LineItem/1", 1, 10m),
        };

        var first = await service.RequestRefundAsync(
            businessId, customerId, null, OrderId, null, key, DefaultMethod, DefaultCurrency, firstOrder,
            CancellationToken.None);
        var second = await service.RequestRefundAsync(
            businessId, customerId, null, OrderId, null, key, DefaultMethod, DefaultCurrency, shuffledOrder,
            CancellationToken.None);

        // Order-independent set equality — design doc §9.1: the set of
        // (ExternalLineItemId, Quantity) pairs defines intent, not list order.
        Assert.Equal(IdempotentOperationOutcome.InProgress, second.Outcome);
        Assert.Equal(first.RefundId, second.RefundId);
    }

    [Fact]
    public async Task RequestRefundAsync_DuplicateAfterConfirmed_ReturnsCachedConfirmed()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IRefundService>();

        var businessId = await CreateBusinessAsync(db);
        var customerId = await CreateCustomerAsync(db, businessId);
        var key = Guid.NewGuid().ToString("N");

        var first = await service.RequestRefundAsync(
            businessId, customerId, null, OrderId, null, key, DefaultMethod, DefaultCurrency, OneItem(),
            CancellationToken.None);
        await service.MarkConfirmedAsync(first.RefundId, "gid://shopify/Refund/1", 10m, CancellationToken.None);

        var second = await service.RequestRefundAsync(
            businessId, customerId, null, OrderId, null, key, DefaultMethod, DefaultCurrency, OneItem(),
            CancellationToken.None);

        Assert.Equal(IdempotentOperationOutcome.Confirmed, second.Outcome);
        Assert.Equal(first.RefundId, second.RefundId);
    }

    [Fact]
    public async Task RequestRefundAsync_DuplicateAfterFailed_ReturnsCachedFailure()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IRefundService>();

        var businessId = await CreateBusinessAsync(db);
        var customerId = await CreateCustomerAsync(db, businessId);
        var key = Guid.NewGuid().ToString("N");

        var first = await service.RequestRefundAsync(
            businessId, customerId, null, OrderId, null, key, DefaultMethod, DefaultCurrency, OneItem(),
            CancellationToken.None);
        await service.MarkFailedAsync(first.RefundId, "Shopify rejected the mutation", CancellationToken.None);

        var second = await service.RequestRefundAsync(
            businessId, customerId, null, OrderId, null, key, DefaultMethod, DefaultCurrency, OneItem(),
            CancellationToken.None);

        Assert.Equal(IdempotentOperationOutcome.Failed, second.Outcome);
        Assert.Equal("Shopify rejected the mutation", second.FailureReason);
    }

    [Fact]
    public async Task RequestRefundAsync_SameKeyDifferentExternalOrderId_ReturnsKeyReusedWithDifferentIntent()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IRefundService>();

        var businessId = await CreateBusinessAsync(db);
        var customerId = await CreateCustomerAsync(db, businessId);
        var key = Guid.NewGuid().ToString("N");

        var first = await service.RequestRefundAsync(
            businessId, customerId, null, OrderId, null, key, DefaultMethod, DefaultCurrency, OneItem(),
            CancellationToken.None);
        var second = await service.RequestRefundAsync(
            businessId, customerId, null, "gid://shopify/Order/DIFFERENT", null, key, DefaultMethod,
            DefaultCurrency, OneItem(), CancellationToken.None);

        Assert.Equal(IdempotentOperationOutcome.KeyReusedWithDifferentIntent, second.Outcome);
        Assert.Equal(first.RefundId, second.RefundId);

        var count = await db.Refunds.IgnoreQueryFilters()
            .CountAsync(x => x.BusinessId == businessId && x.IdempotencyKey == key);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task RequestRefundAsync_SameKeyDifferentMethod_ReturnsKeyReusedWithDifferentIntent()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IRefundService>();

        var businessId = await CreateBusinessAsync(db);
        var customerId = await CreateCustomerAsync(db, businessId);
        var key = Guid.NewGuid().ToString("N");

        await service.RequestRefundAsync(
            businessId, customerId, null, OrderId, null, key, RefundMethod.OriginalPaymentMethod, DefaultCurrency,
            OneItem(), CancellationToken.None);
        var second = await service.RequestRefundAsync(
            businessId, customerId, null, OrderId, null, key, RefundMethod.StoreCredit, DefaultCurrency,
            OneItem(), CancellationToken.None);

        Assert.Equal(IdempotentOperationOutcome.KeyReusedWithDifferentIntent, second.Outcome);
    }

    [Fact]
    public async Task RequestRefundAsync_SameKeyDifferentQuantity_ReturnsKeyReusedWithDifferentIntent()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IRefundService>();

        var businessId = await CreateBusinessAsync(db);
        var customerId = await CreateCustomerAsync(db, businessId);
        var key = Guid.NewGuid().ToString("N");

        await service.RequestRefundAsync(
            businessId, customerId, null, OrderId, null, key, DefaultMethod, DefaultCurrency,
            OneItem(quantity: 1), CancellationToken.None);
        var second = await service.RequestRefundAsync(
            businessId, customerId, null, OrderId, null, key, DefaultMethod, DefaultCurrency,
            OneItem(quantity: 2), CancellationToken.None);

        Assert.Equal(IdempotentOperationOutcome.KeyReusedWithDifferentIntent, second.Outcome);
    }

    [Fact]
    public async Task RequestRefundAsync_SameKeyDifferentLineItem_ReturnsKeyReusedWithDifferentIntent()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IRefundService>();

        var businessId = await CreateBusinessAsync(db);
        var customerId = await CreateCustomerAsync(db, businessId);
        var key = Guid.NewGuid().ToString("N");

        await service.RequestRefundAsync(
            businessId, customerId, null, OrderId, null, key, DefaultMethod, DefaultCurrency,
            OneItem("gid://shopify/LineItem/1"), CancellationToken.None);
        var second = await service.RequestRefundAsync(
            businessId, customerId, null, OrderId, null, key, DefaultMethod, DefaultCurrency,
            OneItem("gid://shopify/LineItem/DIFFERENT"), CancellationToken.None);

        Assert.Equal(IdempotentOperationOutcome.KeyReusedWithDifferentIntent, second.Outcome);
    }

    [Fact]
    public async Task RequestRefundAsync_SameKeyDifferentCustomerReason_StillTreatedAsMatch()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IRefundService>();

        var businessId = await CreateBusinessAsync(db);
        var customerId = await CreateCustomerAsync(db, businessId);
        var key = Guid.NewGuid().ToString("N");

        var first = await service.RequestRefundAsync(
            businessId, customerId, null, OrderId, "reason A", key, DefaultMethod, DefaultCurrency, OneItem(),
            CancellationToken.None);
        var second = await service.RequestRefundAsync(
            businessId, customerId, null, OrderId, "reason B (different)", key, DefaultMethod, DefaultCurrency,
            OneItem(), CancellationToken.None);

        // CustomerReason is explicitly excluded from the operation-defining
        // intent, matching OrderCancellationService's own identical
        // exclusion — a duplicate with a different reason string must
        // still be treated as the same logical request.
        Assert.Equal(IdempotentOperationOutcome.InProgress, second.Outcome);
        Assert.Equal(first.RefundId, second.RefundId);
    }

    [Fact]
    public async Task RequestRefundAsync_ConcurrentIdenticalRequests_ExactlyOneRow_BothCoherent()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var businessId = await CreateBusinessAsync(db);
        var customerId = await CreateCustomerAsync(db, businessId);
        var key = Guid.NewGuid().ToString("N");

        // Each concurrent caller gets its own scope/DbContext — a single EF
        // DbContext is not thread-safe for concurrent operations, mirroring
        // OrderCancellationServiceTests' own identical concurrency test.
        async Task<RefundResult> CallAsync()
        {
            await using var callScope = _provider.CreateAsyncScope();
            var callService = callScope.ServiceProvider.GetRequiredService<IRefundService>();
            return await callService.RequestRefundAsync(
                businessId, customerId, null, OrderId, null, key, DefaultMethod, DefaultCurrency, OneItem(),
                CancellationToken.None);
        }

        var results = await Task.WhenAll(CallAsync(), CallAsync());

        Assert.All(results, r => Assert.Equal(IdempotentOperationOutcome.InProgress, r.Outcome));
        Assert.Equal(results[0].RefundId, results[1].RefundId);

        var count = await db.Refunds.IgnoreQueryFilters()
            .CountAsync(x => x.BusinessId == businessId && x.IdempotencyKey == key);
        Assert.Equal(1, count);
    }

    // ── Tenant isolation ─────────────────────────────────────────────────

    [Fact]
    public async Task RequestRefundAsync_SameKeyDifferentBusiness_NoCollision()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IRefundService>();

        var businessId1 = await CreateBusinessAsync(db);
        var businessId2 = await CreateBusinessAsync(db);
        var customerId1 = await CreateCustomerAsync(db, businessId1);
        var customerId2 = await CreateCustomerAsync(db, businessId2);
        var key = Guid.NewGuid().ToString("N");

        var first = await service.RequestRefundAsync(
            businessId1, customerId1, null, OrderId, null, key, DefaultMethod, DefaultCurrency, OneItem(),
            CancellationToken.None);
        var second = await service.RequestRefundAsync(
            businessId2, customerId2, null, OrderId, null, key, DefaultMethod, DefaultCurrency, OneItem(),
            CancellationToken.None);

        Assert.Equal(IdempotentOperationOutcome.InProgress, first.Outcome);
        Assert.Equal(IdempotentOperationOutcome.InProgress, second.Outcome);
        Assert.NotEqual(first.RefundId, second.RefundId);

        // Each business's own RefundItem rows are correctly scoped —
        // confirms no client-supplied BusinessId is needed or accepted to
        // achieve this: it falls straight out of the explicit businessId
        // parameter used to construct each row.
        var items1 = await db.RefundItems.IgnoreQueryFilters().CountAsync(x => x.BusinessId == businessId1);
        var items2 = await db.RefundItems.IgnoreQueryFilters().CountAsync(x => x.BusinessId == businessId2);
        Assert.Equal(1, items1);
        Assert.Equal(1, items2);
    }

    // ── State behavior ───────────────────────────────────────────────────

    [Fact]
    public async Task MarkConfirmedAsync_FromPending_SetsConfirmedCompletedAtExternalRefundIdAndTotalAmount()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IRefundService>();

        var businessId = await CreateBusinessAsync(db);
        var customerId = await CreateCustomerAsync(db, businessId);
        var key = Guid.NewGuid().ToString("N");

        var created = await service.RequestRefundAsync(
            businessId, customerId, null, OrderId, null, key, DefaultMethod, DefaultCurrency, OneItem(),
            CancellationToken.None);
        await service.MarkConfirmedAsync(created.RefundId, "gid://shopify/Refund/99", 42.50m, CancellationToken.None);

        var row = await db.Refunds.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == created.RefundId);
        Assert.Equal(RefundStatus.Confirmed, row.Status);
        Assert.NotNull(row.CompletedAt);
        Assert.Equal("gid://shopify/Refund/99", row.ExternalRefundId);
        Assert.Equal(42.50m, row.TotalAmount);
    }

    [Fact]
    public async Task MarkFailedAsync_FromPending_SetsFailedAndFailureReason_LeavesCompletedAtNull()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IRefundService>();

        var businessId = await CreateBusinessAsync(db);
        var customerId = await CreateCustomerAsync(db, businessId);
        var key = Guid.NewGuid().ToString("N");

        var created = await service.RequestRefundAsync(
            businessId, customerId, null, OrderId, null, key, DefaultMethod, DefaultCurrency, OneItem(),
            CancellationToken.None);
        await service.MarkFailedAsync(created.RefundId, "refundCreate rejected the mutation", CancellationToken.None);

        var row = await db.Refunds.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == created.RefundId);
        Assert.Equal(RefundStatus.Failed, row.Status);
        Assert.Equal("refundCreate rejected the mutation", row.FailureReason);
        Assert.Null(row.CompletedAt);
    }

    [Fact]
    public async Task MarkConfirmedAsync_AlreadyTerminal_IsNoOp()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IRefundService>();

        var businessId = await CreateBusinessAsync(db);
        var customerId = await CreateCustomerAsync(db, businessId);
        var key = Guid.NewGuid().ToString("N");

        var created = await service.RequestRefundAsync(
            businessId, customerId, null, OrderId, null, key, DefaultMethod, DefaultCurrency, OneItem(),
            CancellationToken.None);
        await service.MarkFailedAsync(created.RefundId, "first failure", CancellationToken.None);

        // Row is now Failed (terminal) — a later MarkConfirmedAsync call
        // must never flip it to Confirmed; Pending -> Confirmed/Failed are
        // the only legal transitions.
        await service.MarkConfirmedAsync(created.RefundId, "gid://shopify/Refund/should-not-apply", 1m, CancellationToken.None);

        var row = await db.Refunds.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == created.RefundId);
        Assert.Equal(RefundStatus.Failed, row.Status);
        Assert.Equal("first failure", row.FailureReason);
        Assert.Null(row.CompletedAt);
        Assert.Null(row.ExternalRefundId);
    }

    private class FixedTenantAccessor(Guid? businessId) : ICurrentTenantAccessor
    {
        public Guid? BusinessId => businessId;
    }
}
