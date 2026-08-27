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
/// Service-level tests for OrderOwnershipAuthorizationService — real
/// Postgres for the Business/Customer rows (same convention as
/// ConversationModeServiceTests). VerifiedOrderOwnership is constructed
/// directly via this test assembly's InternalsVisibleTo grant — no HTTP
/// mocking needed for this half at all.
/// </summary>
public class OrderOwnershipAuthorizationServiceTests : IAsyncLifetime
{
    private const string OrderId = "gid://shopify/Order/1";
    private const string CustomerExternalId = "gid://shopify/Customer/1";

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
            .AddScoped<IOrderOwnershipAuthorizationService, OrderOwnershipAuthorizationService>()
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
            Name = $"Ownership Test {Guid.NewGuid():N}",
            TimeZone = "Europe/Copenhagen",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Businesses.Add(business);
        await db.SaveChangesAsync();
        _businessIdsToClean.Add(business.Id);
        return business.Id;
    }

    private static async Task<Guid> CreateCustomerAsync(
        AppDbContext db, Guid businessId, string? externalId = CustomerExternalId, bool isAnonymized = false)
    {
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            ExternalId = externalId,
            IsAnonymized = isAnonymized,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        return customer.Id;
    }

    // Only this test assembly's InternalsVisibleTo grant allows this direct
    // construction — proves the "no HTTP mocking needed" claim in the class
    // doc comment above.
    private static VerifiedOrderOwnership Owned(string? ownerExternalCustomerId) =>
        new(OrderId, ownerExternalCustomerId);

    [Fact]
    public async Task AuthorizeAsync_Authorizes_OnMatchingOwner()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IOrderOwnershipAuthorizationService>();

        var businessId = await CreateBusinessAsync(db);
        var customerId = await CreateCustomerAsync(db, businessId);

        var result = await service.AuthorizeAsync(businessId, customerId, Owned(CustomerExternalId), CancellationToken.None);

        Assert.True(result.IsAuthorized);
        Assert.Equal(OwnershipCheckFailureReason.None, result.Reason);
    }

    [Fact]
    public async Task AuthorizeAsync_Denies_OnOwnerMismatch()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IOrderOwnershipAuthorizationService>();

        var businessId = await CreateBusinessAsync(db);
        var customerId = await CreateCustomerAsync(db, businessId, externalId: "gid://shopify/Customer/1");

        var result = await service.AuthorizeAsync(
            businessId, customerId, Owned("gid://shopify/Customer/DIFFERENT"), CancellationToken.None);

        Assert.False(result.IsAuthorized);
        Assert.Equal(OwnershipCheckFailureReason.OwnerMismatch, result.Reason);
    }

    [Fact]
    public async Task AuthorizeAsync_Denies_OnMissingCustomer()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IOrderOwnershipAuthorizationService>();

        var businessId = await CreateBusinessAsync(db);

        var result = await service.AuthorizeAsync(
            businessId, Guid.NewGuid(), Owned(CustomerExternalId), CancellationToken.None);

        Assert.False(result.IsAuthorized);
        Assert.Equal(OwnershipCheckFailureReason.CustomerNotFound, result.Reason);
    }

    [Fact]
    public async Task AuthorizeAsync_Denies_OnCrossTenantCustomer()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IOrderOwnershipAuthorizationService>();

        // Two customers in different businesses may legally share the same
        // ExternalId (the uniqueness index is per (BusinessId, ExternalId)).
        var ownBusinessId = await CreateBusinessAsync(db);
        var otherBusinessId = await CreateBusinessAsync(db);
        var otherTenantsCustomerId = await CreateCustomerAsync(db, otherBusinessId, externalId: CustomerExternalId);

        var result = await service.AuthorizeAsync(
            ownBusinessId, otherTenantsCustomerId, Owned(CustomerExternalId), CancellationToken.None);

        Assert.False(result.IsAuthorized);
        Assert.Equal(OwnershipCheckFailureReason.CustomerNotFound, result.Reason);
    }

    [Fact]
    public async Task AuthorizeAsync_Denies_OnAnonymizedCustomer()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IOrderOwnershipAuthorizationService>();

        var businessId = await CreateBusinessAsync(db);
        var customerId = await CreateCustomerAsync(db, businessId, externalId: CustomerExternalId, isAnonymized: true);

        var result = await service.AuthorizeAsync(businessId, customerId, Owned(CustomerExternalId), CancellationToken.None);

        Assert.False(result.IsAuthorized);
        Assert.Equal(OwnershipCheckFailureReason.CustomerAnonymized, result.Reason);
    }

    [Fact]
    public async Task AuthorizeAsync_Denies_OnMissingCustomerExternalId()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IOrderOwnershipAuthorizationService>();

        var businessId = await CreateBusinessAsync(db);
        var customerId = await CreateCustomerAsync(db, businessId, externalId: null);

        var result = await service.AuthorizeAsync(businessId, customerId, Owned(CustomerExternalId), CancellationToken.None);

        Assert.False(result.IsAuthorized);
        Assert.Equal(OwnershipCheckFailureReason.CustomerNotLinkedToExternalIdentity, result.Reason);
    }

    [Fact]
    public async Task AuthorizeAsync_Denies_OnGuestOrderWithNoOwner()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IOrderOwnershipAuthorizationService>();

        var businessId = await CreateBusinessAsync(db);
        var customerId = await CreateCustomerAsync(db, businessId);

        var result = await service.AuthorizeAsync(businessId, customerId, Owned(null), CancellationToken.None);

        Assert.False(result.IsAuthorized);
        Assert.Equal(OwnershipCheckFailureReason.NoOrderOwner, result.Reason);
    }

    [Fact]
    public async Task AuthorizeAsync_Denies_OnEmptyCustomerId()
    {
        await using var scope = _provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IOrderOwnershipAuthorizationService>();

        var result = await service.AuthorizeAsync(
            Guid.NewGuid(), Guid.Empty, Owned(CustomerExternalId), CancellationToken.None);

        Assert.False(result.IsAuthorized);
        Assert.Equal(OwnershipCheckFailureReason.NoVerifiedCustomer, result.Reason);
    }

    private class FixedTenantAccessor(Guid? businessId) : ICurrentTenantAccessor
    {
        public Guid? BusinessId => businessId;
    }
}
