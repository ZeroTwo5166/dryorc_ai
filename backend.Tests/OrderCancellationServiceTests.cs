using backend.Data;
using backend.Enums;
using backend.Interfaces;
using backend.Models;
using backend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace backend.Tests;

/// <summary>
/// Service-level tests for OrderCancellationService — real Postgres, same
/// convention as OrderOwnershipAuthorizationServiceTests. Exercises only the
/// idempotency mechanism itself: ShopifyOrderCancellationExecutorTests and
/// OrderCancellationReconciliationServiceTests cover the Shopify-calling
/// half.
/// </summary>
public class OrderCancellationServiceTests : IAsyncLifetime
{
    private const string OrderId = "gid://shopify/Order/1";
    private const RefundMethod DefaultRefundMethod = RefundMethod.OriginalPaymentMethod;
    private const OrderCancelReason DefaultReason = OrderCancelReason.Customer;

    private ServiceProvider _provider = null!;
    private readonly List<Guid> _businessIdsToClean = [];

    public Task InitializeAsync()
    {
        var configuration = new ConfigurationBuilder()
            .AddUserSecrets<Program>()
            .Build();

        var connectionUri = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection not found via user-secrets.");

        _provider = new ServiceCollection()
            .AddSingleton<ICurrentTenantAccessor>(new FixedTenantAccessor(null))
            .AddDbContext<AppDbContext>(o => o.UseNpgsql(
                PostgresConnectionString.FromUri(connectionUri), x => x.UseVector()))
            .AddScoped<IOrderCancellationService, OrderCancellationService>()
            .BuildServiceProvider();

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
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Customers\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Businesses\" WHERE \"Id\" = ANY({ids})");
        }

        await _provider.DisposeAsync();
    }

    private async Task<Guid> CreateBusinessAsync(AppDbContext db)
    {
        var business = new Business
        {
            Id = Guid.NewGuid(),
            Name = $"OrderCancellation Test {Guid.NewGuid():N}",
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

    [Fact]
    public async Task RequestCancellationAsync_FreshKey_CreatesPendingRow_ReturnsInProgress()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IOrderCancellationService>();

        var businessId = await CreateBusinessAsync(db);
        var customerId = await CreateCustomerAsync(db, businessId);
        var key = Guid.NewGuid().ToString("N");

        var result = await service.RequestCancellationAsync(
            businessId, customerId, null, OrderId, "changed mind", key, restock: true,
            DefaultRefundMethod, DefaultReason, CancellationToken.None);

        Assert.Equal(IdempotentOperationOutcome.InProgress, result.Outcome);
        Assert.Null(result.FailureReason);

        var row = await db.OrderCancellations.IgnoreQueryFilters().SingleAsync(x => x.Id == result.OrderCancellationId);
        Assert.Equal(OrderCancellationStatus.Pending, row.Status);
        Assert.Null(row.CompletedAt);
        Assert.True(row.Restock);
        Assert.Equal(DefaultRefundMethod, row.Method);
        Assert.Equal(DefaultReason, row.Reason);
        Assert.Equal(OrderId, row.ExternalOrderId);
    }

    [Fact]
    public async Task RequestCancellationAsync_NullRefundMethod_MeansNoRefund()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IOrderCancellationService>();

        var businessId = await CreateBusinessAsync(db);
        var customerId = await CreateCustomerAsync(db, businessId);
        var key = Guid.NewGuid().ToString("N");

        var result = await service.RequestCancellationAsync(
            businessId, customerId, null, OrderId, null, key, restock: false,
            refundMethod: null, DefaultReason, CancellationToken.None);

        var row = await db.OrderCancellations.IgnoreQueryFilters().SingleAsync(x => x.Id == result.OrderCancellationId);
        Assert.Null(row.Method);
    }

    [Fact]
    public async Task RequestCancellationAsync_DuplicateWhilePending_ReturnsInProgress_NoSecondRow()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IOrderCancellationService>();

        var businessId = await CreateBusinessAsync(db);
        var customerId = await CreateCustomerAsync(db, businessId);
        var key = Guid.NewGuid().ToString("N");

        var first = await service.RequestCancellationAsync(
            businessId, customerId, null, OrderId, null, key, restock: false,
            DefaultRefundMethod, DefaultReason, CancellationToken.None);
        var second = await service.RequestCancellationAsync(
            businessId, customerId, null, OrderId, null, key, restock: false,
            DefaultRefundMethod, DefaultReason, CancellationToken.None);

        Assert.Equal(IdempotentOperationOutcome.InProgress, second.Outcome);
        Assert.Equal(first.OrderCancellationId, second.OrderCancellationId);

        var count = await db.OrderCancellations.IgnoreQueryFilters()
            .CountAsync(x => x.BusinessId == businessId && x.IdempotencyKey == key);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task RequestCancellationAsync_DuplicateAfterConfirmed_ReturnsCachedConfirmed()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IOrderCancellationService>();

        var businessId = await CreateBusinessAsync(db);
        var customerId = await CreateCustomerAsync(db, businessId);
        var key = Guid.NewGuid().ToString("N");

        var first = await service.RequestCancellationAsync(
            businessId, customerId, null, OrderId, null, key, restock: false,
            DefaultRefundMethod, DefaultReason, CancellationToken.None);
        await service.MarkConfirmedAsync(first.OrderCancellationId, CancellationToken.None);

        var second = await service.RequestCancellationAsync(
            businessId, customerId, null, OrderId, null, key, restock: false,
            DefaultRefundMethod, DefaultReason, CancellationToken.None);

        Assert.Equal(IdempotentOperationOutcome.Confirmed, second.Outcome);
        Assert.Equal(first.OrderCancellationId, second.OrderCancellationId);
    }

    [Fact]
    public async Task RequestCancellationAsync_DuplicateAfterFailed_ReturnsCachedFailure()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IOrderCancellationService>();

        var businessId = await CreateBusinessAsync(db);
        var customerId = await CreateCustomerAsync(db, businessId);
        var key = Guid.NewGuid().ToString("N");

        var first = await service.RequestCancellationAsync(
            businessId, customerId, null, OrderId, null, key, restock: false,
            DefaultRefundMethod, DefaultReason, CancellationToken.None);
        await service.MarkFailedAsync(first.OrderCancellationId, "Shopify rejected the mutation", CancellationToken.None);

        var second = await service.RequestCancellationAsync(
            businessId, customerId, null, OrderId, null, key, restock: false,
            DefaultRefundMethod, DefaultReason, CancellationToken.None);

        Assert.Equal(IdempotentOperationOutcome.Failed, second.Outcome);
        Assert.Equal("Shopify rejected the mutation", second.FailureReason);
    }

    [Fact]
    public async Task RequestCancellationAsync_SameKeyDifferentExternalOrderId_ReturnsKeyReusedWithDifferentIntent()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IOrderCancellationService>();

        var businessId = await CreateBusinessAsync(db);
        var customerId = await CreateCustomerAsync(db, businessId);
        var key = Guid.NewGuid().ToString("N");

        var first = await service.RequestCancellationAsync(
            businessId, customerId, null, OrderId, null, key, restock: false,
            DefaultRefundMethod, DefaultReason, CancellationToken.None);
        var second = await service.RequestCancellationAsync(
            businessId, customerId, null, "gid://shopify/Order/DIFFERENT", null, key,
            restock: false, DefaultRefundMethod, DefaultReason, CancellationToken.None);

        Assert.Equal(IdempotentOperationOutcome.KeyReusedWithDifferentIntent, second.Outcome);
        Assert.Equal(first.OrderCancellationId, second.OrderCancellationId);

        var count = await db.OrderCancellations.IgnoreQueryFilters()
            .CountAsync(x => x.BusinessId == businessId && x.IdempotencyKey == key);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task RequestCancellationAsync_SameKeyDifferentRestockOrRefund_ReturnsKeyReusedWithDifferentIntent()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IOrderCancellationService>();

        var businessId = await CreateBusinessAsync(db);
        var customerId = await CreateCustomerAsync(db, businessId);
        var key = Guid.NewGuid().ToString("N");

        await service.RequestCancellationAsync(
            businessId, customerId, null, OrderId, null, key, restock: false,
            DefaultRefundMethod, DefaultReason, CancellationToken.None);
        var second = await service.RequestCancellationAsync(
            businessId, customerId, null, OrderId, null, key, restock: true,
            DefaultRefundMethod, DefaultReason, CancellationToken.None);

        Assert.Equal(IdempotentOperationOutcome.KeyReusedWithDifferentIntent, second.Outcome);
    }

    [Fact]
    public async Task RequestCancellationAsync_SameKeyDifferentRefundMethod_ReturnsKeyReusedWithDifferentIntent()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IOrderCancellationService>();

        var businessId = await CreateBusinessAsync(db);
        var customerId = await CreateCustomerAsync(db, businessId);
        var key = Guid.NewGuid().ToString("N");

        await service.RequestCancellationAsync(
            businessId, customerId, null, OrderId, null, key, restock: false,
            RefundMethod.OriginalPaymentMethod, DefaultReason, CancellationToken.None);
        var second = await service.RequestCancellationAsync(
            businessId, customerId, null, OrderId, null, key, restock: false,
            RefundMethod.StoreCredit, DefaultReason, CancellationToken.None);

        Assert.Equal(IdempotentOperationOutcome.KeyReusedWithDifferentIntent, second.Outcome);
    }

    [Fact]
    public async Task RequestCancellationAsync_SameKeyDifferentReason_ReturnsKeyReusedWithDifferentIntent()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IOrderCancellationService>();

        var businessId = await CreateBusinessAsync(db);
        var customerId = await CreateCustomerAsync(db, businessId);
        var key = Guid.NewGuid().ToString("N");

        await service.RequestCancellationAsync(
            businessId, customerId, null, OrderId, null, key, restock: false,
            DefaultRefundMethod, OrderCancelReason.Customer, CancellationToken.None);
        var second = await service.RequestCancellationAsync(
            businessId, customerId, null, OrderId, null, key, restock: false,
            DefaultRefundMethod, OrderCancelReason.Fraud, CancellationToken.None);

        Assert.Equal(IdempotentOperationOutcome.KeyReusedWithDifferentIntent, second.Outcome);
    }

    [Fact]
    public async Task RequestCancellationAsync_SameKeyDifferentCustomerReason_StillTreatedAsMatch()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IOrderCancellationService>();

        var businessId = await CreateBusinessAsync(db);
        var customerId = await CreateCustomerAsync(db, businessId);
        var key = Guid.NewGuid().ToString("N");

        var first = await service.RequestCancellationAsync(
            businessId, customerId, null, OrderId, "reason A", key, restock: false,
            DefaultRefundMethod, DefaultReason, CancellationToken.None);
        var second = await service.RequestCancellationAsync(
            businessId, customerId, null, OrderId, "reason B (different)", key, restock: false,
            DefaultRefundMethod, DefaultReason, CancellationToken.None);

        // CustomerReason is explicitly excluded from the operation-defining
        // intent (Section 3 of the idempotency handoff) — a duplicate with
        // a different reason string must still be treated as the same
        // logical request.
        Assert.Equal(IdempotentOperationOutcome.InProgress, second.Outcome);
        Assert.Equal(first.OrderCancellationId, second.OrderCancellationId);
    }

    [Fact]
    public async Task RequestCancellationAsync_ConcurrentIdenticalRequests_ExactlyOneRow_BothCoherent()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var businessId = await CreateBusinessAsync(db);
        var customerId = await CreateCustomerAsync(db, businessId);
        var key = Guid.NewGuid().ToString("N");

        // Each concurrent caller gets its own scope/DbContext — a single
        // EF DbContext is not thread-safe for concurrent operations, and
        // this mirrors how two truly independent requests would each hold
        // their own scoped context in production.
        async Task<OrderCancellationResult> CallAsync()
        {
            await using var callScope = _provider.CreateAsyncScope();
            var callService = callScope.ServiceProvider.GetRequiredService<IOrderCancellationService>();
            return await callService.RequestCancellationAsync(
                businessId, customerId, null, OrderId, null, key, restock: false,
                DefaultRefundMethod, DefaultReason, CancellationToken.None);
        }

        var results = await Task.WhenAll(CallAsync(), CallAsync());

        Assert.All(results, r => Assert.Equal(IdempotentOperationOutcome.InProgress, r.Outcome));
        Assert.Equal(results[0].OrderCancellationId, results[1].OrderCancellationId);

        var count = await db.OrderCancellations.IgnoreQueryFilters()
            .CountAsync(x => x.BusinessId == businessId && x.IdempotencyKey == key);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task RequestCancellationAsync_SameKeyDifferentBusiness_NoCollision()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IOrderCancellationService>();

        var businessId1 = await CreateBusinessAsync(db);
        var businessId2 = await CreateBusinessAsync(db);
        var customerId1 = await CreateCustomerAsync(db, businessId1);
        var customerId2 = await CreateCustomerAsync(db, businessId2);
        var key = Guid.NewGuid().ToString("N");

        var first = await service.RequestCancellationAsync(
            businessId1, customerId1, null, OrderId, null, key, restock: false,
            DefaultRefundMethod, DefaultReason, CancellationToken.None);
        var second = await service.RequestCancellationAsync(
            businessId2, customerId2, null, OrderId, null, key, restock: false,
            DefaultRefundMethod, DefaultReason, CancellationToken.None);

        Assert.Equal(IdempotentOperationOutcome.InProgress, first.Outcome);
        Assert.Equal(IdempotentOperationOutcome.InProgress, second.Outcome);
        Assert.NotEqual(first.OrderCancellationId, second.OrderCancellationId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RequestCancellationAsync_BlankIdempotencyKey_Throws(string key)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IOrderCancellationService>();

        var businessId = await CreateBusinessAsync(db);
        var customerId = await CreateCustomerAsync(db, businessId);

        await Assert.ThrowsAsync<ArgumentException>(() => service.RequestCancellationAsync(
            businessId, customerId, null, OrderId, null, key, restock: false,
            DefaultRefundMethod, DefaultReason, CancellationToken.None));
    }

    [Fact]
    public async Task RequestCancellationAsync_TooLongIdempotencyKey_Throws()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IOrderCancellationService>();

        var businessId = await CreateBusinessAsync(db);
        var customerId = await CreateCustomerAsync(db, businessId);
        var key = new string('k', 257);

        await Assert.ThrowsAsync<ArgumentException>(() => service.RequestCancellationAsync(
            businessId, customerId, null, OrderId, null, key, restock: false,
            DefaultRefundMethod, DefaultReason, CancellationToken.None));
    }

    [Fact]
    public async Task MarkConfirmedAsync_FromPending_SetsConfirmedAndCompletedAt()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IOrderCancellationService>();

        var businessId = await CreateBusinessAsync(db);
        var customerId = await CreateCustomerAsync(db, businessId);
        var key = Guid.NewGuid().ToString("N");

        var created = await service.RequestCancellationAsync(
            businessId, customerId, null, OrderId, null, key, restock: false,
            DefaultRefundMethod, DefaultReason, CancellationToken.None);
        await service.MarkConfirmedAsync(created.OrderCancellationId, CancellationToken.None);

        var row = await db.OrderCancellations.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(x => x.Id == created.OrderCancellationId);
        Assert.Equal(OrderCancellationStatus.Confirmed, row.Status);
        Assert.NotNull(row.CompletedAt);
    }

    [Fact]
    public async Task MarkFailedAsync_FromPending_SetsFailedAndFailureReason_LeavesCompletedAtNull()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IOrderCancellationService>();

        var businessId = await CreateBusinessAsync(db);
        var customerId = await CreateCustomerAsync(db, businessId);
        var key = Guid.NewGuid().ToString("N");

        var created = await service.RequestCancellationAsync(
            businessId, customerId, null, OrderId, null, key, restock: false,
            DefaultRefundMethod, DefaultReason, CancellationToken.None);
        await service.MarkFailedAsync(created.OrderCancellationId, "orderCancelUserErrors: already cancelled", CancellationToken.None);

        var row = await db.OrderCancellations.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(x => x.Id == created.OrderCancellationId);
        Assert.Equal(OrderCancellationStatus.Failed, row.Status);
        Assert.Equal("orderCancelUserErrors: already cancelled", row.FailureReason);
        Assert.Null(row.CompletedAt);
    }

    [Fact]
    public async Task MarkConfirmedAsync_AlreadyTerminal_IsNoOp()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IOrderCancellationService>();

        var businessId = await CreateBusinessAsync(db);
        var customerId = await CreateCustomerAsync(db, businessId);
        var key = Guid.NewGuid().ToString("N");

        var created = await service.RequestCancellationAsync(
            businessId, customerId, null, OrderId, null, key, restock: false,
            DefaultRefundMethod, DefaultReason, CancellationToken.None);
        await service.MarkFailedAsync(created.OrderCancellationId, "first failure", CancellationToken.None);

        // Row is now Failed (terminal) — a later MarkConfirmedAsync call
        // must never flip it to Confirmed; Pending -> Confirmed/Failed are
        // the only legal transitions.
        await service.MarkConfirmedAsync(created.OrderCancellationId, CancellationToken.None);

        var row = await db.OrderCancellations.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(x => x.Id == created.OrderCancellationId);
        Assert.Equal(OrderCancellationStatus.Failed, row.Status);
        Assert.Equal("first failure", row.FailureReason);
        Assert.Null(row.CompletedAt);
    }

    private class FixedTenantAccessor(Guid? businessId) : ICurrentTenantAccessor
    {
        public Guid? BusinessId => businessId;
    }
}
