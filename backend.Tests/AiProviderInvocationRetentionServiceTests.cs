using backend.Constants;
using backend.Data;
using backend.Interfaces;
using backend.Models;
using backend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace backend.Tests;

/// <summary>
/// A1 v11 §8 test 11: the retention job deletes AiProviderInvocation rows
/// older than 90 days and leaves newer rows untouched
/// (docs/handoffs/billing-usage-a1-slice6-design.md §4). Real Postgres,
/// same harness as ConsumedSignedIdentityTokenCleanupServiceTests.
///
/// The tenant accessor resolves no business: the fail-closed tenant filter
/// then matches no row, so this test only passes if the service bypasses
/// it — the global, cross-tenant sweep, not one tenant's.
///
/// Asserts only on the row IDs this test seeds, never on the returned
/// count or any table-wide count: the delete is global and other tests'
/// rows share the database. 91 and 89 days sit either side of the strict
/// 90-day cutoff; the exact-cutoff instant is not tested (no injectable
/// clock).
///
/// In the ConversationAutoClose collection because every test host that
/// runs the real DataLifecycleBackgroundService is in it, and that host's
/// first tick runs this same global delete — see that collection's own
/// doc comment.
/// </summary>
[Collection("ConversationAutoClose")]
public class AiProviderInvocationRetentionServiceTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;
    private readonly List<Guid> _businessIdsToClean = [];

    public Task InitializeAsync()
    {
        _provider = new ServiceCollection()
            .AddSingleton<ICurrentTenantAccessor>(new FixedTenantAccessor(null))
            .AddDbContext<AppDbContext>(o => o.UseNpgsql(
                TestDatabase.ConnectionString, x => x.UseVector()))
            .AddScoped<IAiProviderInvocationRetentionService, AiProviderInvocationRetentionService>()
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
            Name = $"Invocation Retention Test {Guid.NewGuid():N}",
            TimeZone = "Europe/Copenhagen",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Businesses.Add(business);
        await db.SaveChangesAsync();
        _businessIdsToClean.Add(business.Id);
        return business.Id;
    }

    private static AiProviderInvocation BuildRow(Guid businessId, DateTimeOffset createdAt) => new()
    {
        Id = Guid.NewGuid(),
        BusinessId = businessId,
        Provider = AiProviders.Anthropic,
        Model = AnthropicModels.Default,
        Operation = AiOperations.CustomerReply,
        CorrelationId = Guid.NewGuid(),
        Sequence = 0,
        InputTokens = 100,
        OutputTokens = 50,
        Outcome = AiOutcomes.Succeeded,
        IsUsagePartial = false,
        CreatedAt = createdAt,
    };

    [Fact]
    public async Task DeleteExpiredAsync_DeletesRowsOlderThan90Days_KeepsNewerRows_AcrossBusinesses()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IAiProviderInvocationRetentionService>();

        var businessA = await CreateBusinessAsync(db);
        var businessB = await CreateBusinessAsync(db);

        var now = DateTimeOffset.UtcNow;
        var expiredA = BuildRow(businessA, now.AddDays(-91));
        var keptA = BuildRow(businessA, now.AddDays(-89));
        var expiredB = BuildRow(businessB, now.AddDays(-91));
        var keptB = BuildRow(businessB, now.AddDays(-89));
        db.AiProviderInvocations.AddRange(expiredA, keptA, expiredB, keptB);
        await db.SaveChangesAsync();

        Guid[] seededIds = [expiredA.Id, keptA.Id, expiredB.Id, keptB.Id];

        // Setup guard — a failure here is a seeding failure, not a retention one.
        Assert.Equal(seededIds.Order(), await PresentSeededIdsAsync(seededIds));

        await service.DeleteExpiredAsync();

        Assert.Equal(new[] { keptA.Id, keptB.Id }.Order(), await PresentSeededIdsAsync(seededIds));
    }

    /// <summary>Which of the given seeded IDs still exist, sorted — read
    /// through a fresh scope, never the seeding context's change tracker.</summary>
    private async Task<List<Guid>> PresentSeededIdsAsync(Guid[] seededIds)
    {
        await using var readScope = _provider.CreateAsyncScope();
        var readDb = readScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var present = await readDb.AiProviderInvocations
            .IgnoreQueryFilters()
            .Where(i => seededIds.Contains(i.Id))
            .Select(i => i.Id)
            .ToListAsync();
        return present.Order().ToList();
    }

    private sealed class FixedTenantAccessor(Guid? businessId) : ICurrentTenantAccessor
    {
        public Guid? BusinessId => businessId;
    }
}
