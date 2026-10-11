using backend.Data;
using backend.Enums;
using backend.Interfaces;
using backend.Models;
using backend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace backend.Tests;

/// <summary>
/// Service-level tests for CustomerUpdateActionService — real Postgres,
/// same convention as OrderCancellationServiceTests. Exercises only the
/// idempotency mechanism itself: no Shopify call exists yet to exercise.
/// </summary>
public class CustomerUpdateActionServiceTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;
    private readonly List<Guid> _businessIdsToClean = [];

    public Task InitializeAsync()
    {
        _provider = new ServiceCollection()
            .AddSingleton<ICurrentTenantAccessor>(new FixedTenantAccessor(null))
            .AddDbContext<AppDbContext>(o => o.UseNpgsql(
                TestDatabase.ConnectionString, x => x.UseVector()))
            .AddScoped<ICustomerUpdateActionService, CustomerUpdateActionService>()
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
            Name = $"CustomerUpdateAction Test {Guid.NewGuid():N}",
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
    public async Task RequestUpdateAsync_FreshKey_CreatesPendingRow_ReturnsInProgress()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<ICustomerUpdateActionService>();

        var businessId = await CreateBusinessAsync(db);
        var customerId = await CreateCustomerAsync(db, businessId);
        var key = Guid.NewGuid().ToString("N");

        var result = await service.RequestUpdateAsync(
            businessId, customerId, null, ["email", "phone"], key, CancellationToken.None);

        Assert.Equal(IdempotentOperationOutcome.InProgress, result.Outcome);
        Assert.Null(result.FailureReason);

        var row = await db.CustomerUpdateActions.IgnoreQueryFilters().SingleAsync(x => x.Id == result.CustomerUpdateActionId);
        Assert.Equal(CustomerUpdateActionStatus.Pending, row.Status);
        Assert.Null(row.CompletedAt);
        Assert.Equal(new[] { "email", "phone" }, row.FieldsChanged.OrderBy(f => f).ToArray());
    }

    [Fact]
    public async Task RequestUpdateAsync_DuplicateWhilePending_ReturnsInProgress_NoSecondRow()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<ICustomerUpdateActionService>();

        var businessId = await CreateBusinessAsync(db);
        var customerId = await CreateCustomerAsync(db, businessId);
        var key = Guid.NewGuid().ToString("N");

        var first = await service.RequestUpdateAsync(businessId, customerId, null, ["email"], key, CancellationToken.None);
        var second = await service.RequestUpdateAsync(businessId, customerId, null, ["email"], key, CancellationToken.None);

        Assert.Equal(IdempotentOperationOutcome.InProgress, second.Outcome);
        Assert.Equal(first.CustomerUpdateActionId, second.CustomerUpdateActionId);

        var count = await db.CustomerUpdateActions.IgnoreQueryFilters()
            .CountAsync(x => x.BusinessId == businessId && x.IdempotencyKey == key);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task RequestUpdateAsync_DuplicateAfterConfirmed_ReturnsCachedConfirmed()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<ICustomerUpdateActionService>();

        var businessId = await CreateBusinessAsync(db);
        var customerId = await CreateCustomerAsync(db, businessId);
        var key = Guid.NewGuid().ToString("N");

        var first = await service.RequestUpdateAsync(businessId, customerId, null, ["email"], key, CancellationToken.None);
        await service.MarkConfirmedAsync(first.CustomerUpdateActionId, CancellationToken.None);

        var second = await service.RequestUpdateAsync(businessId, customerId, null, ["email"], key, CancellationToken.None);

        Assert.Equal(IdempotentOperationOutcome.Confirmed, second.Outcome);
        Assert.Equal(first.CustomerUpdateActionId, second.CustomerUpdateActionId);
    }

    [Fact]
    public async Task RequestUpdateAsync_DuplicateAfterFailed_ReturnsCachedFailure()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<ICustomerUpdateActionService>();

        var businessId = await CreateBusinessAsync(db);
        var customerId = await CreateCustomerAsync(db, businessId);
        var key = Guid.NewGuid().ToString("N");

        var first = await service.RequestUpdateAsync(businessId, customerId, null, ["email"], key, CancellationToken.None);
        await service.MarkFailedAsync(first.CustomerUpdateActionId, "customerUpdateUserErrors: invalid email", CancellationToken.None);

        var second = await service.RequestUpdateAsync(businessId, customerId, null, ["email"], key, CancellationToken.None);

        Assert.Equal(IdempotentOperationOutcome.Failed, second.Outcome);
        Assert.Equal("customerUpdateUserErrors: invalid email", second.FailureReason);
    }

    [Fact]
    public async Task RequestUpdateAsync_SameKeyDifferentFieldsChanged_ReturnsKeyReusedWithDifferentIntent()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<ICustomerUpdateActionService>();

        var businessId = await CreateBusinessAsync(db);
        var customerId = await CreateCustomerAsync(db, businessId);
        var key = Guid.NewGuid().ToString("N");

        var first = await service.RequestUpdateAsync(businessId, customerId, null, ["email"], key, CancellationToken.None);
        var second = await service.RequestUpdateAsync(businessId, customerId, null, ["phone"], key, CancellationToken.None);

        Assert.Equal(IdempotentOperationOutcome.KeyReusedWithDifferentIntent, second.Outcome);
        Assert.Equal(first.CustomerUpdateActionId, second.CustomerUpdateActionId);

        var count = await db.CustomerUpdateActions.IgnoreQueryFilters()
            .CountAsync(x => x.BusinessId == businessId && x.IdempotencyKey == key);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task RequestUpdateAsync_SameKeyDifferentCustomer_ReturnsKeyReusedWithDifferentIntent()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<ICustomerUpdateActionService>();

        var businessId = await CreateBusinessAsync(db);
        var customerId1 = await CreateCustomerAsync(db, businessId);
        var customerId2 = await CreateCustomerAsync(db, businessId);
        var key = Guid.NewGuid().ToString("N");

        await service.RequestUpdateAsync(businessId, customerId1, null, ["email"], key, CancellationToken.None);
        var second = await service.RequestUpdateAsync(businessId, customerId2, null, ["email"], key, CancellationToken.None);

        Assert.Equal(IdempotentOperationOutcome.KeyReusedWithDifferentIntent, second.Outcome);
    }

    [Fact]
    public async Task RequestUpdateAsync_SameKeySameFieldsDifferentOrder_TreatedAsMatch()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<ICustomerUpdateActionService>();

        var businessId = await CreateBusinessAsync(db);
        var customerId = await CreateCustomerAsync(db, businessId);
        var key = Guid.NewGuid().ToString("N");

        var first = await service.RequestUpdateAsync(
            businessId, customerId, null, ["email", "phone"], key, CancellationToken.None);
        // FieldsChanged intent comparison is a set comparison, not
        // order-sensitive (Section 3 of the handoff).
        var second = await service.RequestUpdateAsync(
            businessId, customerId, null, ["phone", "email"], key, CancellationToken.None);

        Assert.Equal(IdempotentOperationOutcome.InProgress, second.Outcome);
        Assert.Equal(first.CustomerUpdateActionId, second.CustomerUpdateActionId);
    }

    [Fact]
    public async Task RequestUpdateAsync_ConcurrentIdenticalRequests_ExactlyOneRow_BothCoherent()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var businessId = await CreateBusinessAsync(db);
        var customerId = await CreateCustomerAsync(db, businessId);
        var key = Guid.NewGuid().ToString("N");

        async Task<CustomerUpdateActionResult> CallAsync()
        {
            await using var callScope = _provider.CreateAsyncScope();
            var callService = callScope.ServiceProvider.GetRequiredService<ICustomerUpdateActionService>();
            return await callService.RequestUpdateAsync(businessId, customerId, null, ["email"], key, CancellationToken.None);
        }

        var results = await Task.WhenAll(CallAsync(), CallAsync());

        Assert.All(results, r => Assert.Equal(IdempotentOperationOutcome.InProgress, r.Outcome));
        Assert.Equal(results[0].CustomerUpdateActionId, results[1].CustomerUpdateActionId);

        var count = await db.CustomerUpdateActions.IgnoreQueryFilters()
            .CountAsync(x => x.BusinessId == businessId && x.IdempotencyKey == key);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task RequestUpdateAsync_SameKeyDifferentBusiness_NoCollision()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<ICustomerUpdateActionService>();

        var businessId1 = await CreateBusinessAsync(db);
        var businessId2 = await CreateBusinessAsync(db);
        var customerId1 = await CreateCustomerAsync(db, businessId1);
        var customerId2 = await CreateCustomerAsync(db, businessId2);
        var key = Guid.NewGuid().ToString("N");

        var first = await service.RequestUpdateAsync(businessId1, customerId1, null, ["email"], key, CancellationToken.None);
        var second = await service.RequestUpdateAsync(businessId2, customerId2, null, ["email"], key, CancellationToken.None);

        Assert.Equal(IdempotentOperationOutcome.InProgress, first.Outcome);
        Assert.Equal(IdempotentOperationOutcome.InProgress, second.Outcome);
        Assert.NotEqual(first.CustomerUpdateActionId, second.CustomerUpdateActionId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RequestUpdateAsync_BlankIdempotencyKey_Throws(string key)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<ICustomerUpdateActionService>();

        var businessId = await CreateBusinessAsync(db);
        var customerId = await CreateCustomerAsync(db, businessId);

        await Assert.ThrowsAsync<ArgumentException>(() => service.RequestUpdateAsync(
            businessId, customerId, null, ["email"], key, CancellationToken.None));
    }

    [Fact]
    public async Task RequestUpdateAsync_TooLongIdempotencyKey_Throws()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<ICustomerUpdateActionService>();

        var businessId = await CreateBusinessAsync(db);
        var customerId = await CreateCustomerAsync(db, businessId);
        var key = new string('k', 257);

        await Assert.ThrowsAsync<ArgumentException>(() => service.RequestUpdateAsync(
            businessId, customerId, null, ["email"], key, CancellationToken.None));
    }

    [Fact]
    public async Task MarkConfirmedAsync_FromPending_SetsConfirmedAndCompletedAt()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<ICustomerUpdateActionService>();

        var businessId = await CreateBusinessAsync(db);
        var customerId = await CreateCustomerAsync(db, businessId);
        var key = Guid.NewGuid().ToString("N");

        var created = await service.RequestUpdateAsync(businessId, customerId, null, ["email"], key, CancellationToken.None);
        await service.MarkConfirmedAsync(created.CustomerUpdateActionId, CancellationToken.None);

        var row = await db.CustomerUpdateActions.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(x => x.Id == created.CustomerUpdateActionId);
        Assert.Equal(CustomerUpdateActionStatus.Confirmed, row.Status);
        Assert.NotNull(row.CompletedAt);
    }

    [Fact]
    public async Task MarkFailedAsync_FromPending_SetsFailedAndFailureReason_LeavesCompletedAtNull()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<ICustomerUpdateActionService>();

        var businessId = await CreateBusinessAsync(db);
        var customerId = await CreateCustomerAsync(db, businessId);
        var key = Guid.NewGuid().ToString("N");

        var created = await service.RequestUpdateAsync(businessId, customerId, null, ["email"], key, CancellationToken.None);
        await service.MarkFailedAsync(created.CustomerUpdateActionId, "customerUpdateUserErrors: invalid email", CancellationToken.None);

        var row = await db.CustomerUpdateActions.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(x => x.Id == created.CustomerUpdateActionId);
        Assert.Equal(CustomerUpdateActionStatus.Failed, row.Status);
        Assert.Equal("customerUpdateUserErrors: invalid email", row.FailureReason);
        Assert.Null(row.CompletedAt);
    }

    [Fact]
    public async Task MarkConfirmedAsync_AlreadyTerminal_IsNoOp()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<ICustomerUpdateActionService>();

        var businessId = await CreateBusinessAsync(db);
        var customerId = await CreateCustomerAsync(db, businessId);
        var key = Guid.NewGuid().ToString("N");

        var created = await service.RequestUpdateAsync(businessId, customerId, null, ["email"], key, CancellationToken.None);
        await service.MarkFailedAsync(created.CustomerUpdateActionId, "first failure", CancellationToken.None);

        await service.MarkConfirmedAsync(created.CustomerUpdateActionId, CancellationToken.None);

        var row = await db.CustomerUpdateActions.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(x => x.Id == created.CustomerUpdateActionId);
        Assert.Equal(CustomerUpdateActionStatus.Failed, row.Status);
        Assert.Equal("first failure", row.FailureReason);
        Assert.Null(row.CompletedAt);
    }

    private class FixedTenantAccessor(Guid? businessId) : ICurrentTenantAccessor
    {
        public Guid? BusinessId => businessId;
    }
}
