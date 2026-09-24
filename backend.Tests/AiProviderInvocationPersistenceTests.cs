using System.Reflection;
using backend.Constants;
using backend.Data;
using backend.Interfaces;
using backend.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace backend.Tests;

/// <summary>
/// Step 27 Slice A1 — schema/persistence tests for AiProviderInvocation,
/// against the real dev DB. Recorder/wrapper/call-site work is out of
/// scope for this slice (design docs/handoffs/billing-usage-a1-invocation-capture-design-v11.md),
/// so — same reasoning as ScheduledReportRunPersistenceTests.cs, which this
/// file mirrors — there is no HTTP surface or application write-path to
/// exercise yet; every row here is authored directly by the test.
/// </summary>
public class AiProviderInvocationPersistenceTests : IAsyncLifetime
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
            .AddSingleton<ICurrentTenantAccessor>(new MutableTenantAccessor())
            .AddDbContext<AppDbContext>(o => o.UseNpgsql(
                PostgresConnectionString.FromUri(connectionUri), x => x.UseVector()))
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

            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"AiProviderInvocations\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Businesses\" WHERE \"Id\" = ANY({ids})");
        }

        await _provider.DisposeAsync();
    }

    private async Task<Guid> SeedBusinessAsync(AppDbContext db, string name)
    {
        var businessId = Guid.NewGuid();
        db.Businesses.Add(new Business
        {
            Id = businessId,
            Name = name,
            TimeZone = "Europe/Copenhagen",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        _businessIdsToClean.Add(businessId);
        return businessId;
    }

    private static AiProviderInvocation BuildValidAnthropicRow(Guid businessId) => new()
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
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static AiProviderInvocation BuildValidVoyagePartialRow(Guid businessId) => new()
    {
        Id = Guid.NewGuid(),
        BusinessId = businessId,
        Provider = AiProviders.Voyage,
        Model = "voyage-3",
        Operation = AiOperations.KnowledgeIndexing,
        CorrelationId = Guid.NewGuid(),
        Sequence = 0,
        InputTokens = 900,
        Outcome = AiOutcomes.Failed,
        ErrorCategory = AiErrorCategories.ServerError,
        IsUsagePartial = true,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    // ── Test 3: tenant isolation on reads ───────────────────────────────

    [Fact]
    public async Task TenantFilter_BusinessASeesOnlyItsOwnInvocation_NeverBusinessBs()
    {
        await using var seedScope = _provider.CreateAsyncScope();
        var seedDb = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var businessIdA = await SeedBusinessAsync(seedDb, "Tenant Filter Business A");
        var businessIdB = await SeedBusinessAsync(seedDb, "Tenant Filter Business B");

        var rowA = BuildValidAnthropicRow(businessIdA);
        var rowB = BuildValidAnthropicRow(businessIdB);
        seedDb.AiProviderInvocations.AddRange(rowA, rowB);
        await seedDb.SaveChangesAsync();

        await using var scopeA = _provider.CreateAsyncScope();
        var dbA = scopeA.ServiceProvider.GetRequiredService<AppDbContext>();
        SetTenant(businessIdA);
        var seenByA = await dbA.AiProviderInvocations.ToListAsync();
        Assert.Single(seenByA);
        Assert.Equal(rowA.Id, seenByA[0].Id);

        await using var scopeB = _provider.CreateAsyncScope();
        var dbB = scopeB.ServiceProvider.GetRequiredService<AppDbContext>();
        SetTenant(businessIdB);
        var seenByB = await dbB.AiProviderInvocations.ToListAsync();
        Assert.Single(seenByB);
        Assert.Equal(rowB.Id, seenByB[0].Id);
    }

    [Fact]
    public async Task TenantFilter_NoAmbientTenant_SeesZeroRows_FailClosed()
    {
        await using var seedScope = _provider.CreateAsyncScope();
        var seedDb = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var businessId = await SeedBusinessAsync(seedDb, "No Ambient Tenant Business");
        seedDb.AiProviderInvocations.Add(BuildValidAnthropicRow(businessId));
        await seedDb.SaveChangesAsync();

        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        SetTenant(null);

        Assert.Empty(await db.AiProviderInvocations.ToListAsync());
    }

    // ── Test 8: string-property allowlist ───────────────────────────────
    // The allowlist is derived by reflection from the constants classes
    // themselves (not a second, hand-typed copy that could drift), then
    // every value actually persisted by this test's own seeded rows is
    // checked against it. Mutation check performed manually: insert a row
    // with Provider = 'BogusProvider' via raw SQL (nothing at the DB level
    // stops this — no CHECK constraint enforces Provider's value set,
    // reported separately as a prose-only invariant), confirm this test
    // fails, then delete that row.

    [Fact]
    public async Task StringProperties_AllPersistedValues_AreWithinExplicitAllowlists()
    {
        await using var seedScope = _provider.CreateAsyncScope();
        var seedDb = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var businessId = await SeedBusinessAsync(seedDb, "Allowlist Business");

        // One row per Provider, and one row per Operation, so every
        // constant in both classes is exercised at least once.
        seedDb.AiProviderInvocations.AddRange(
            NewRow(businessId, AiProviders.Anthropic, AnthropicModels.Default, AiOperations.CustomerReply, AiOutcomes.Succeeded, null),
            NewRow(businessId, AiProviders.Voyage, "voyage-3", AiOperations.KnowledgeIndexing, AiOutcomes.Succeeded, null),
            NewRow(businessId, AiProviders.Anthropic, AnthropicModels.Default, AiOperations.ConversationClassification, AiOutcomes.Succeeded, null),
            NewRow(businessId, AiProviders.Anthropic, AnthropicModels.Default, AiOperations.AnalystSelection, AiOutcomes.Succeeded, null),
            NewRow(businessId, AiProviders.Anthropic, AnthropicModels.Default, AiOperations.AnalystSynthesis, AiOutcomes.Failed, AiErrorCategories.RateLimited),
            NewRow(businessId, AiProviders.Voyage, "voyage-3", AiOperations.KnowledgeRetrievalQuery, AiOutcomes.Failed, AiErrorCategories.Timeout));
        await seedDb.SaveChangesAsync();

        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        SetTenant(businessId);

        var providers = await db.AiProviderInvocations.Select(x => x.Provider).Distinct().ToListAsync();
        var operations = await db.AiProviderInvocations.Select(x => x.Operation).Distinct().ToListAsync();
        var outcomes = await db.AiProviderInvocations.Select(x => x.Outcome).Distinct().ToListAsync();
        var errorCategories = await db.AiProviderInvocations
            .Where(x => x.ErrorCategory != null).Select(x => x.ErrorCategory!).Distinct().ToListAsync();

        var allowedProviders = GetConstStrings(typeof(AiProviders));
        var allowedOperations = GetConstStrings(typeof(AiOperations));
        var allowedOutcomes = GetConstStrings(typeof(AiOutcomes));
        var allowedErrorCategories = GetConstStrings(typeof(AiErrorCategories));

        Assert.NotEmpty(providers);
        Assert.All(providers, p => Assert.Contains(p, allowedProviders));
        Assert.All(operations, o => Assert.Contains(o, allowedOperations));
        Assert.All(outcomes, o => Assert.Contains(o, allowedOutcomes));
        Assert.All(errorCategories, e => Assert.Contains(e, allowedErrorCategories));

        // Every declared Operation constant is actually exercised at least
        // once above, so a future silently-unused or silently-renamed
        // value would be caught by a human reading this test's seed data
        // going stale, not just by the allowlist-membership check alone.
        Assert.Equal(allowedOperations.Count, operations.Count);
    }

    private static AiProviderInvocation NewRow(
        Guid businessId, string provider, string model, string operation, string outcome, string? errorCategory) => new()
    {
        Id = Guid.NewGuid(),
        BusinessId = businessId,
        Provider = provider,
        Model = model,
        Operation = operation,
        CorrelationId = Guid.NewGuid(),
        Sequence = 0,
        InputTokens = outcome == AiOutcomes.Succeeded ? 10 : null,
        Outcome = outcome,
        ErrorCategory = errorCategory,
        IsUsagePartial = false,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static HashSet<string> GetConstStrings(Type type) =>
        type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet();

    // ── Test 14: IsUsagePartial CHECK constraint ────────────────────────

    [Fact]
    public async Task IsUsagePartialTrue_OnSucceededRow_RawInsert_RejectedByCheckConstraint()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var businessId = await SeedBusinessAsync(db, "IsUsagePartial Succeeded Business");

        var ex = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "AiProviderInvocations"
                ("Id", "BusinessId", "Provider", "Model", "Operation", "CorrelationId", "Sequence",
                 "InputTokens", "Outcome", "IsUsagePartial", "CreatedAt")
            VALUES
                ({Guid.NewGuid()}, {businessId}, {AiProviders.Voyage}, 'voyage-3', {AiOperations.KnowledgeIndexing},
                 {Guid.NewGuid()}, 0, 900, {AiOutcomes.Succeeded}, true, {DateTimeOffset.UtcNow})
            """));
        Assert.Equal("23514", ex.SqlState); // check_violation
        Assert.Equal("CK_AiProviderInvocations_IsUsagePartial_Implication", ex.ConstraintName);
    }

    [Fact]
    public async Task IsUsagePartialTrue_OnAnthropicRow_RawInsert_RejectedByCheckConstraint()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var businessId = await SeedBusinessAsync(db, "IsUsagePartial Anthropic Business");

        var ex = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "AiProviderInvocations"
                ("Id", "BusinessId", "Provider", "Model", "Operation", "CorrelationId", "Sequence",
                 "InputTokens", "Outcome", "ErrorCategory", "IsUsagePartial", "CreatedAt")
            VALUES
                ({Guid.NewGuid()}, {businessId}, {AiProviders.Anthropic}, {AnthropicModels.Default}, {AiOperations.CustomerReply},
                 {Guid.NewGuid()}, 0, 100, {AiOutcomes.Failed}, {AiErrorCategories.ServerError}, true, {DateTimeOffset.UtcNow})
            """));
        Assert.Equal("23514", ex.SqlState);
        Assert.Equal("CK_AiProviderInvocations_IsUsagePartial_Implication", ex.ConstraintName);
    }

    [Fact]
    public async Task IsUsagePartialTrue_OnNullInputTokensRow_RawInsert_RejectedByCheckConstraint()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var businessId = await SeedBusinessAsync(db, "IsUsagePartial NullTokens Business");

        var ex = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "AiProviderInvocations"
                ("Id", "BusinessId", "Provider", "Model", "Operation", "CorrelationId", "Sequence",
                 "Outcome", "ErrorCategory", "IsUsagePartial", "CreatedAt")
            VALUES
                ({Guid.NewGuid()}, {businessId}, {AiProviders.Voyage}, 'voyage-3', {AiOperations.KnowledgeIndexing},
                 {Guid.NewGuid()}, 0, {AiOutcomes.Failed}, {AiErrorCategories.ServerError}, true, {DateTimeOffset.UtcNow})
            """));
        Assert.Equal("23514", ex.SqlState);
        Assert.Equal("CK_AiProviderInvocations_IsUsagePartial_Implication", ex.ConstraintName);
    }

    [Fact]
    public async Task IsUsagePartialTrue_OnValidVoyagePartialFailure_Succeeds()
    {
        // Negative control for the three rejection tests above — proves the
        // constraint accepts the one shape it's actually meant to allow,
        // not just that it rejects everything.
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var businessId = await SeedBusinessAsync(db, "IsUsagePartial Valid Business");

        db.AiProviderInvocations.Add(BuildValidVoyagePartialRow(businessId));
        await db.SaveChangesAsync();

        SetTenant(businessId);
        var reloaded = await db.AiProviderInvocations.SingleAsync();
        Assert.True(reloaded.IsUsagePartial);
    }

    private void SetTenant(Guid? businessId) =>
        ((MutableTenantAccessor)_provider.GetRequiredService<ICurrentTenantAccessor>()).CurrentBusinessId = businessId;

    private class MutableTenantAccessor : ICurrentTenantAccessor
    {
        public Guid? CurrentBusinessId { get; set; }
        public Guid? BusinessId => CurrentBusinessId;
    }
}
