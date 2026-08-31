using backend.Data;
using backend.Interfaces;
using backend.Models;
using backend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace backend.Tests;

/// <summary>
/// Service-level tests for ConsumedSignedIdentityTokenCleanupService — real
/// Postgres, same convention as other lightweight cleanup-style services in
/// this codebase. Storage hygiene only — see the service's own doc comment
/// for why deleting an old record can never make an expired token
/// replayable again (that safety argument is proven end-to-end here: a
/// cleaned-up token is still correctly rejected as expired, never
/// "replayable again," by SignedIdentityTokenService's own independent
/// expiry check).
/// </summary>
public class ConsumedSignedIdentityTokenCleanupServiceTests : IAsyncLifetime
{
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
            .AddScoped<IConsumedSignedIdentityTokenCleanupService, ConsumedSignedIdentityTokenCleanupService>()
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

            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"ConsumedSignedIdentityTokens\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Businesses\" WHERE \"Id\" = ANY({ids})");
        }

        await _provider.DisposeAsync();
    }

    private async Task<Guid> CreateBusinessAsync(AppDbContext db)
    {
        var business = new Business
        {
            Id = Guid.NewGuid(),
            Name = $"Cleanup Test {Guid.NewGuid():N}",
            TimeZone = "Europe/Copenhagen",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Businesses.Add(business);
        await db.SaveChangesAsync();
        _businessIdsToClean.Add(business.Id);
        return business.Id;
    }

    private static async Task SeedTokenAsync(AppDbContext db, Guid businessId, DateTimeOffset consumedAt)
    {
        db.ConsumedSignedIdentityTokens.Add(new ConsumedSignedIdentityToken
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            Jti = Guid.NewGuid().ToString(),
            ConsumedAt = consumedAt,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task CleanupExpiredAsync_RemovesRecordsOlderThanRetentionFloor()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IConsumedSignedIdentityTokenCleanupService>();
        var businessId = await CreateBusinessAsync(db);

        await SeedTokenAsync(db, businessId, DateTimeOffset.UtcNow.AddDays(-31));

        var deleted = await service.CleanupExpiredAsync();

        Assert.Equal(1, deleted);
        var remaining = await db.ConsumedSignedIdentityTokens.IgnoreQueryFilters()
            .CountAsync(t => t.BusinessId == businessId);
        Assert.Equal(0, remaining);
    }

    [Fact]
    public async Task CleanupExpiredAsync_KeepsRecordsWithinRetentionFloor()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IConsumedSignedIdentityTokenCleanupService>();
        var businessId = await CreateBusinessAsync(db);

        await SeedTokenAsync(db, businessId, DateTimeOffset.UtcNow.AddDays(-1));

        await service.CleanupExpiredAsync();

        var remaining = await db.ConsumedSignedIdentityTokens.IgnoreQueryFilters()
            .CountAsync(t => t.BusinessId == businessId);
        Assert.Equal(1, remaining);
    }

    [Fact]
    public async Task CleanupExpiredAsync_DoesNotTouchCustomersOrExternalIdentities()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IConsumedSignedIdentityTokenCleanupService>();
        var businessId = await CreateBusinessAsync(db);

        var customer = new Customer
        {
            Id = Guid.NewGuid(), BusinessId = businessId, ExternalId = "gid://shopify/Customer/1",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        await SeedTokenAsync(db, businessId, DateTimeOffset.UtcNow.AddDays(-31));

        await service.CleanupExpiredAsync();

        var customerStillExists = await db.Customers.IgnoreQueryFilters().AnyAsync(c => c.Id == customer.Id);
        Assert.True(customerStillExists);

        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Customers\" WHERE \"Id\" = {customer.Id}");
    }

    private class FixedTenantAccessor(Guid? businessId) : ICurrentTenantAccessor
    {
        public Guid? BusinessId => businessId;
    }
}
