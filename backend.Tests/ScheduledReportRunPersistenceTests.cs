using backend.Data;
using backend.Enums;
using backend.Interfaces;
using backend.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace backend.Tests;

/// <summary>
/// Step 24 Phase 3 — schema/persistence tests for ScheduledReportRun,
/// against the real dev DB. This entity has no controller/endpoint yet
/// (design/plan: persistence only, mirroring Approval.cs's own precedent),
/// so this file follows AppDbContextTenantFilterTests.cs's own pattern
/// (real AppDbContext constructed directly via DI, a settable
/// ICurrentTenantAccessor test double, real Postgres via user-secrets) —
/// not the HTTP-level NivoraApiFactory pattern most other *ApiTests.cs
/// files use, since there is no HTTP surface to exercise yet.
///
/// Every assertion in this file is backed by real PostgreSQL behavior
/// (unique-constraint violations, NOT NULL rejections, FK rejections,
/// direct information_schema/pg_catalog inspection) — never EF metadata
/// alone, per this phase's own explicit instruction that the database is
/// the source of truth.
/// </summary>
public class ScheduledReportRunPersistenceTests : IAsyncLifetime
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
        await TestBusinessCleanup.DeleteThenDisposeAsync(_businessIdsToClean, _provider);
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

    private static ScheduledReportRun BuildValidRun(Guid businessId, DateTimeOffset periodStart) => new()
    {
        Id = Guid.NewGuid(),
        BusinessId = businessId,
        ReportPeriodStart = periodStart,
        ReportPeriodEnd = periodStart.AddDays(7),
        Status = ScheduledReportRunStatus.Processing,
        ClaimedAt = DateTimeOffset.UtcNow,
    };

    // ── 1. Valid entity creation ────────────────────────────────────────

    [Fact]
    public async Task ValidRun_PersistsSuccessfully_WithExpectedValues()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        SetTenant(null); // ambient tenant irrelevant for a direct write by Id

        var businessId = await SeedBusinessAsync(db, "Valid Run Business");
        var periodStart = new DateTimeOffset(2026, 8, 17, 0, 0, 0, TimeSpan.Zero);
        var run = BuildValidRun(businessId, periodStart);

        db.ScheduledReportRuns.Add(run);
        await db.SaveChangesAsync();

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        SetTenant(businessId);

        var reloaded = await verifyDb.ScheduledReportRuns.SingleAsync(r => r.Id == run.Id);
        Assert.Equal(businessId, reloaded.BusinessId);
        Assert.Equal(periodStart, reloaded.ReportPeriodStart);
        Assert.Equal(periodStart.AddDays(7), reloaded.ReportPeriodEnd);
        Assert.Equal(ScheduledReportRunStatus.Processing, reloaded.Status);
        Assert.Null(reloaded.RecipientOwnerId);
        Assert.Null(reloaded.SentAt);
        Assert.Null(reloaded.FailureReason);
    }

    // ── 2. Unique constraint — real DB enforcement ──────────────────────

    [Fact]
    public async Task DuplicateBusinessIdAndReportPeriodStart_RealPostgresUniqueConstraint_SecondInsertFails()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        SetTenant(null);

        var businessId = await SeedBusinessAsync(db, "Duplicate Claim Business");
        var periodStart = new DateTimeOffset(2026, 8, 17, 0, 0, 0, TimeSpan.Zero);

        db.ScheduledReportRuns.Add(BuildValidRun(businessId, periodStart));
        await db.SaveChangesAsync();

        // A genuinely separate DbContext instance, not a second Add() on
        // the same tracked context - proves the database itself rejects
        // this, not merely EF's own change-tracker noticing a duplicate key
        // it already has in memory.
        await using var secondScope = _provider.CreateAsyncScope();
        var secondDb = secondScope.ServiceProvider.GetRequiredService<AppDbContext>();
        secondDb.ScheduledReportRuns.Add(BuildValidRun(businessId, periodStart));

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => secondDb.SaveChangesAsync());
        var pgEx = Assert.IsType<PostgresException>(ex.InnerException);
        Assert.Equal("23505", pgEx.SqlState); // unique_violation
        Assert.Equal("IX_ScheduledReportRuns_BusinessId_ReportPeriodStart", pgEx.ConstraintName);

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        SetTenant(businessId);
        var count = await verifyDb.ScheduledReportRuns.CountAsync(r => r.ReportPeriodStart == periodStart);
        Assert.Equal(1, count); // the rejected second row never landed
    }

    [Fact]
    public async Task DifferentReportPeriodStart_SameBusiness_BothInsertsSucceed()
    {
        // Negative control for the unique-constraint test above - proves
        // the constraint is scoped to (BusinessId, ReportPeriodStart)
        // together, not to BusinessId alone.
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        SetTenant(null);

        var businessId = await SeedBusinessAsync(db, "Two Periods Business");
        db.ScheduledReportRuns.Add(BuildValidRun(businessId, new DateTimeOffset(2026, 8, 17, 0, 0, 0, TimeSpan.Zero)));
        db.ScheduledReportRuns.Add(BuildValidRun(businessId, new DateTimeOffset(2026, 8, 24, 0, 0, 0, TimeSpan.Zero)));
        await db.SaveChangesAsync();

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        SetTenant(businessId);
        Assert.Equal(2, await verifyDb.ScheduledReportRuns.CountAsync());
    }

    [Fact]
    public async Task SameReportPeriodStart_DifferentBusiness_BothInsertsSucceed()
    {
        // Negative control in the other direction - proves the constraint
        // is scoped per-business, not globally on ReportPeriodStart alone.
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        SetTenant(null);

        var businessIdA = await SeedBusinessAsync(db, "Same Period Business A");
        var businessIdB = await SeedBusinessAsync(db, "Same Period Business B");
        var periodStart = new DateTimeOffset(2026, 8, 17, 0, 0, 0, TimeSpan.Zero);
        db.ScheduledReportRuns.Add(BuildValidRun(businessIdA, periodStart));
        db.ScheduledReportRuns.Add(BuildValidRun(businessIdB, periodStart));
        await db.SaveChangesAsync();

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await verifyDb.ScheduledReportRuns.IgnoreQueryFilters()
            .CountAsync(r => r.BusinessId == businessIdA));
        Assert.Equal(1, await verifyDb.ScheduledReportRuns.IgnoreQueryFilters()
            .CountAsync(r => r.BusinessId == businessIdB));
    }

    // ── 3. Required fields — real DB enforcement, raw SQL (bypassing EF's
    //    own compile-time `required` keyword entirely) ───────────────────

    [Fact]
    public async Task MissingBusinessId_RawInsert_RejectedByPostgresNotNull()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var businessId = await SeedBusinessAsync(db, "Missing BusinessId Raw Insert Business");

        var ex = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "ScheduledReportRuns"
                ("Id", "ReportPeriodStart", "ReportPeriodEnd", "Status", "ClaimedAt")
            VALUES
                ({Guid.NewGuid()}, {new DateTimeOffset(2026, 8, 17, 0, 0, 0, TimeSpan.Zero)},
                 {new DateTimeOffset(2026, 8, 24, 0, 0, 0, TimeSpan.Zero)}, 'Processing', {DateTimeOffset.UtcNow})
            """));
        Assert.Equal("23502", ex.SqlState); // not_null_violation
        Assert.Equal("BusinessId", ex.ColumnName);
    }

    [Fact]
    public async Task MissingReportPeriodStart_RawInsert_RejectedByPostgresNotNull()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var businessId = await SeedBusinessAsync(db, "Missing PeriodStart Raw Insert Business");

        var ex = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "ScheduledReportRuns"
                ("Id", "BusinessId", "ReportPeriodEnd", "Status", "ClaimedAt")
            VALUES
                ({Guid.NewGuid()}, {businessId},
                 {new DateTimeOffset(2026, 8, 24, 0, 0, 0, TimeSpan.Zero)}, 'Processing', {DateTimeOffset.UtcNow})
            """));
        Assert.Equal("23502", ex.SqlState);
        Assert.Equal("ReportPeriodStart", ex.ColumnName);
    }

    [Fact]
    public async Task MissingStatus_RawInsert_RejectedByPostgresNotNull()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var businessId = await SeedBusinessAsync(db, "Missing Status Raw Insert Business");

        var ex = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "ScheduledReportRuns"
                ("Id", "BusinessId", "ReportPeriodStart", "ReportPeriodEnd", "ClaimedAt")
            VALUES
                ({Guid.NewGuid()}, {businessId}, {new DateTimeOffset(2026, 8, 17, 0, 0, 0, TimeSpan.Zero)},
                 {new DateTimeOffset(2026, 8, 24, 0, 0, 0, TimeSpan.Zero)}, {DateTimeOffset.UtcNow})
            """));
        Assert.Equal("23502", ex.SqlState);
        Assert.Equal("Status", ex.ColumnName);
    }

    [Fact]
    public async Task SentStatusWithoutSentAt_RawInsert_RejectedByCheckConstraint()
    {
        // Not a NOT NULL violation - CK_ScheduledReportRuns_SentAt itself.
        // RecipientOwnerId is left NULL here deliberately (it's genuinely
        // nullable per the design's own correction, 15.1) - only the
        // Status/SentAt pairing is under test.
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var businessId = await SeedBusinessAsync(db, "Sent Without SentAt Raw Insert Business");

        var ex = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "ScheduledReportRuns"
                ("Id", "BusinessId", "ReportPeriodStart", "ReportPeriodEnd", "Status", "ClaimedAt")
            VALUES
                ({Guid.NewGuid()}, {businessId}, {new DateTimeOffset(2026, 8, 17, 0, 0, 0, TimeSpan.Zero)},
                 {new DateTimeOffset(2026, 8, 24, 0, 0, 0, TimeSpan.Zero)}, 'Sent', {DateTimeOffset.UtcNow})
            """));
        Assert.Equal("23514", ex.SqlState); // check_violation
        Assert.Equal("CK_ScheduledReportRuns_SentAt", ex.ConstraintName);
    }

    // ── 4. Tenant isolation — the same global query filter mechanism the
    //    rest of the application uses ─────────────────────────────────

    [Fact]
    public async Task TenantFilter_BusinessASeesOnlyItsOwnRun_NeverBusinessBs()
    {
        await using var seedScope = _provider.CreateAsyncScope();
        var seedDb = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var businessIdA = await SeedBusinessAsync(seedDb, "Tenant Filter Business A");
        var businessIdB = await SeedBusinessAsync(seedDb, "Tenant Filter Business B");

        var runA = BuildValidRun(businessIdA, new DateTimeOffset(2026, 8, 17, 0, 0, 0, TimeSpan.Zero));
        var runB = BuildValidRun(businessIdB, new DateTimeOffset(2026, 8, 17, 0, 0, 0, TimeSpan.Zero));
        seedDb.ScheduledReportRuns.AddRange(runA, runB);
        await seedDb.SaveChangesAsync();

        await using var scopeA = _provider.CreateAsyncScope();
        var dbA = scopeA.ServiceProvider.GetRequiredService<AppDbContext>();
        SetTenant(businessIdA);
        var seenByA = await dbA.ScheduledReportRuns.ToListAsync();
        Assert.Single(seenByA);
        Assert.Equal(runA.Id, seenByA[0].Id);

        await using var scopeB = _provider.CreateAsyncScope();
        var dbB = scopeB.ServiceProvider.GetRequiredService<AppDbContext>();
        SetTenant(businessIdB);
        var seenByB = await dbB.ScheduledReportRuns.ToListAsync();
        Assert.Single(seenByB);
        Assert.Equal(runB.Id, seenByB[0].Id);
    }

    [Fact]
    public async Task TenantFilter_NoAmbientTenant_SeesZeroRows_FailClosed()
    {
        // Matches ICurrentTenantAccessor's own documented fail-closed
        // contract (design §3.1) - directly relevant since Phase 5/6's
        // scheduler/execution services run with no HttpContext and must
        // rely on IgnoreQueryFilters() + an explicit BusinessId, never the
        // ambient filter, for exactly this reason.
        await using var seedScope = _provider.CreateAsyncScope();
        var seedDb = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var businessId = await SeedBusinessAsync(seedDb, "No Ambient Tenant Business");
        seedDb.ScheduledReportRuns.Add(BuildValidRun(businessId, new DateTimeOffset(2026, 8, 17, 0, 0, 0, TimeSpan.Zero)));
        await seedDb.SaveChangesAsync();

        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        SetTenant(null);

        Assert.Empty(await db.ScheduledReportRuns.ToListAsync());
    }

    // ── 5. Business FK ───────────────────────────────────────────────

    [Fact]
    public async Task NonExistentBusinessId_Insert_RejectedByForeignKey()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.ScheduledReportRuns.Add(BuildValidRun(Guid.NewGuid(), new DateTimeOffset(2026, 8, 17, 0, 0, 0, TimeSpan.Zero)));

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        var pgEx = Assert.IsType<PostgresException>(ex.InnerException);
        Assert.Equal("23503", pgEx.SqlState); // foreign_key_violation
        Assert.Equal("FK_ScheduledReportRuns_Businesses_BusinessId", pgEx.ConstraintName);
    }

    [Fact]
    public async Task DeletingBusinessWithAScheduledReportRun_RejectedByRestrict()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var businessId = await SeedBusinessAsync(db, "FK Restrict Business");
        db.ScheduledReportRuns.Add(BuildValidRun(businessId, new DateTimeOffset(2026, 8, 17, 0, 0, 0, TimeSpan.Zero)));
        await db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Businesses\" WHERE \"Id\" = {businessId}"));
        // Postgres distinguishes this from the generic 23503
        // foreign_key_violation (asserted in NonExistentBusinessId_Insert_
        // RejectedByForeignKey above, for the "insert points at nothing"
        // case) - 23001 restrict_violation is the specific code for a
        // DELETE blocked by ON DELETE RESTRICT. Confirmed empirically this
        // phase, not assumed.
        Assert.Equal("23001", ex.SqlState); // restrict_violation
    }

    [Fact]
    public async Task DeletingApplicationUser_SetsRecipientOwnerIdNull_NotRestrict()
    {
        // Confirms the OTHER FK's delete behavior (SetNull), the opposite
        // of Business's (Restrict) - both configured by design §15.2,
        // directly verified here rather than assumed from the migration
        // source alone.
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var businessId = await SeedBusinessAsync(db, "FK SetNull Owner Business");

        var ownerId = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "AspNetUsers"
                ("Id", "BusinessId", "UserName", "NormalizedUserName", "Email", "NormalizedEmail",
                 "EmailConfirmed", "IsActive", "CreatedAt", "SecurityStamp", "ConcurrencyStamp",
                 "PhoneNumberConfirmed", "TwoFactorEnabled", "LockoutEnabled", "AccessFailedCount")
            VALUES
                ({ownerId}, {businessId}, 'fk-setnull-owner@example.com', 'FK-SETNULL-OWNER@EXAMPLE.COM',
                 'fk-setnull-owner@example.com', 'FK-SETNULL-OWNER@EXAMPLE.COM', true, true,
                 {DateTimeOffset.UtcNow}, {Guid.NewGuid().ToString()}, {Guid.NewGuid().ToString()}, false, false, true, 0)
            """);

        var run = BuildValidRun(businessId, new DateTimeOffset(2026, 8, 17, 0, 0, 0, TimeSpan.Zero));
        run.RecipientOwnerId = ownerId;
        db.ScheduledReportRuns.Add(run);
        await db.SaveChangesAsync();

        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"AspNetUsers\" WHERE \"Id\" = {ownerId}");

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        SetTenant(businessId);
        var reloaded = await verifyDb.ScheduledReportRuns.SingleAsync(r => r.Id == run.Id);
        Assert.Null(reloaded.RecipientOwnerId);
    }

    // ── 6. Schema/index verification — direct PostgreSQL inspection,
    //    mirroring AiBusinessAnalystApiTests' own Migration_* convention ──

    [Fact]
    public async Task Migration_ScheduledReportRunsTable_ColumnsMatchDesignExactly()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync();

        var expected = new Dictionary<string, (string Type, string Nullable, int? MaxLength)>
        {
            ["Id"] = ("uuid", "NO", null),
            ["BusinessId"] = ("uuid", "NO", null),
            ["ReportPeriodStart"] = ("timestamp with time zone", "NO", null),
            ["ReportPeriodEnd"] = ("timestamp with time zone", "NO", null),
            ["Status"] = ("character varying", "NO", 32),
            ["RecipientOwnerId"] = ("uuid", "YES", null),
            ["ClaimedAt"] = ("timestamp with time zone", "NO", null),
            ["SentAt"] = ("timestamp with time zone", "YES", null),
            ["FailureReason"] = ("character varying", "YES", 512),
        };

        var actual = new Dictionary<string, (string Type, string Nullable, int? MaxLength)>();
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT column_name, data_type, is_nullable, character_maximum_length
                FROM information_schema.columns
                WHERE table_name = 'ScheduledReportRuns'
                """;
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                actual[reader.GetString(0)] = (
                    reader.GetString(1), reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetInt32(3));
            }
        }

        Assert.Equal(expected.Count, actual.Count); // no unexpected extra/missing column
        foreach (var (column, expectedShape) in expected)
        {
            Assert.True(actual.TryGetValue(column, out var actualShape), $"Column {column} is missing.");
            Assert.Equal(expectedShape, actualShape);
        }
    }

    [Fact]
    public async Task Migration_UniqueConstraint_IsUniqueOnBusinessIdAndReportPeriodStart()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync();

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT indexdef FROM pg_indexes WHERE indexname = 'IX_ScheduledReportRuns_BusinessId_ReportPeriodStart'";
        var indexDef = (string?)await cmd.ExecuteScalarAsync();

        Assert.NotNull(indexDef);
        Assert.Contains("UNIQUE INDEX", indexDef);
        Assert.Contains("(\"BusinessId\", \"ReportPeriodStart\")", indexDef);
    }

    [Fact]
    public async Task Migration_DuePollingIndex_MatchesDesignExactly()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync();

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT indexdef FROM pg_indexes WHERE indexname = 'IX_ScheduledReportRuns_BusinessId_Processing'";
        var indexDef = (string?)await cmd.ExecuteScalarAsync();

        Assert.NotNull(indexDef);
        Assert.DoesNotContain("UNIQUE INDEX", indexDef); // non-unique, per design §15.2
        Assert.Contains("(\"BusinessId\")", indexDef);
        Assert.Contains("WHERE ((\"Status\")::text = 'Processing'::text)", indexDef);
    }

    [Fact]
    public async Task Migration_BusinessForeignKey_ExistsWithRestrictDeleteBehavior()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync();

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT con.confdeltype::text, (SELECT relname FROM pg_class WHERE oid = con.confrelid)
            FROM pg_constraint con
            WHERE con.conname = 'FK_ScheduledReportRuns_Businesses_BusinessId'
            """;
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "FK_ScheduledReportRuns_Businesses_BusinessId does not exist.");
        Assert.Equal("r", reader.GetString(0)); // 'r' = RESTRICT
        Assert.Equal("Businesses", reader.GetString(1));
    }

    // ICurrentTenantAccessor is registered Singleton (matching
    // AppDbContextTenantFilterTests.cs's own precedent) - every AppDbContext
    // built from _provider, regardless of scope, reads the same shared
    // instance live at query-filter-evaluation time (AppDbContext.cs's own
    // documented "read fresh, never snapshotted" design), so mutating it
    // here affects every scope's queries from this point forward. Safe
    // per-test: xUnit constructs a fresh test-class instance (and therefore
    // a fresh _provider, via InitializeAsync) per [Fact] by default, so
    // this singleton is never actually shared across test methods.
    private void SetTenant(Guid? businessId) =>
        ((MutableTenantAccessor)_provider.GetRequiredService<ICurrentTenantAccessor>()).CurrentBusinessId = businessId;

    private class MutableTenantAccessor : ICurrentTenantAccessor
    {
        public Guid? CurrentBusinessId { get; set; }
        public Guid? BusinessId => CurrentBusinessId;
    }
}
