using backend.Data;
using backend.Enums;
using backend.Interfaces;
using backend.Models;
using backend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace backend.Tests;

/// <summary>
/// Step 24 Phase 5 — adversarial concurrency/claiming proof for
/// ScheduledReportSchedulerService, against the real dev DB. See
/// docs/handoffs/step-24-scheduled-ai-reports-design.md §8/§12 and
/// docs/handoffs/step-24-scheduled-ai-reports-implementation-plan.md's
/// Phase 5 section (§3.2) for the full contract this proves.
///
/// Every test here constructs ScheduledReportSchedulerService directly and
/// calls PollAsync - never through ScheduledReportBackgroundService's timer
/// loop - proving the scoped service is independently testable per the
/// established thin-scheduler/scoped-service convention (test category 8).
/// This phase builds discovery/claiming only: no test here simulates or
/// invokes any Phase 6 (execution) behavior, since IScheduledReportExecutionService
/// does not exist yet - assertions stop at "the winner is determined to
/// have won, and the loser can prove it lost."
/// </summary>
[Collection("ScheduledReports")]
public class ScheduledReportSchedulerServiceTests : IAsyncLifetime
{
    private readonly List<Guid> _businessIdsToClean = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_businessIdsToClean.Count > 0)
        {
            await using var db = DbFor();
            var ids = _businessIdsToClean.ToArray();

            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"ScheduledReportRuns\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Businesses\" WHERE \"Id\" = ANY({ids})");
        }
    }

    // ── Infra helpers ────────────────────────────────────────────────

    private static AppDbContext DbFor() =>
        new ServiceCollection()
            .AddSingleton<ICurrentTenantAccessor>(new FixedTenantAccessor(null))
            .AddDbContext<AppDbContext>(o => o.UseNpgsql(
                PostgresConnectionString.FromUri(new ConfigurationBuilder()
                    .AddUserSecrets<Program>().Build()
                    .GetConnectionString("DefaultConnection")!), x => x.UseVector()))
            .BuildServiceProvider()
            .GetRequiredService<AppDbContext>();

    private sealed class FixedTenantAccessor(Guid? businessId) : ICurrentTenantAccessor
    {
        public Guid? BusinessId => businessId;
    }

    /// <summary>Fixed-instant TimeProvider - .NET's own built-in abstraction
    /// (System.TimeProvider), overriding only GetUtcNow(). Not a new
    /// third-party test package; a minimal hand-rolled fake, matching this
    /// codebase's general preference for small, dependency-free
    /// solutions.</summary>
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static ScheduledReportSchedulerService SchedulerFor(AppDbContext db, DateTimeOffset nowUtc) =>
        new(db, new FixedTimeProvider(nowUtc), NullLogger<ScheduledReportSchedulerService>.Instance);

    private async Task<Guid> SeedBusinessAsync(string namePrefix, bool enabled, string timeZone = "Europe/Copenhagen")
    {
        await using var db = DbFor();
        var businessId = Guid.NewGuid();
        db.Businesses.Add(new Business
        {
            Id = businessId,
            Name = $"{namePrefix} {Guid.NewGuid():N}",
            TimeZone = timeZone,
            ScheduledReportsEnabled = enabled,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        _businessIdsToClean.Add(businessId);
        return businessId;
    }

    private static async Task<int> CountRunsAsync(Guid businessId, DateTimeOffset periodStart)
    {
        await using var db = DbFor();
        return await db.ScheduledReportRuns.IgnoreQueryFilters()
            .CountAsync(r => r.BusinessId == businessId && r.ReportPeriodStart == periodStart);
    }

    // A reference instant safely past a known-due Monday 09:00 local for
    // Europe/Copenhagen, matching BusinessWeekPeriodCalculatorTests.cs's own
    // established fixture dates - no DST/month/year boundary nearby.
    private static readonly DateTimeOffset DueReferenceInstant = new(2026, 8, 26, 10, 0, 0, TimeSpan.Zero);

    // ── 1. Basic claim success ──────────────────────────────────────

    [Fact]
    public async Task PollAsync_DueEnabledBusiness_ClaimsExactlyOneRun_WithCorrectFields()
    {
        var businessId = await SeedBusinessAsync("Basic Claim", enabled: true);
        var expectedPeriod = BusinessWeekPeriodCalculator.Compute(DueReferenceInstant, "Europe/Copenhagen");

        await using var db = DbFor();
        var scheduler = SchedulerFor(db, DueReferenceInstant);
        var result = await scheduler.PollAsync(CancellationToken.None);

        var claimedForThisBusiness = Assert.Single(result, r => r.BusinessId == businessId);
        Assert.Equal(expectedPeriod.PeriodStartUtc, claimedForThisBusiness.ReportPeriodStart);
        Assert.Equal(expectedPeriod.PeriodEndUtcExclusive, claimedForThisBusiness.ReportPeriodEnd);
        Assert.Equal(ScheduledReportRunStatus.Processing, claimedForThisBusiness.Status);
        Assert.Equal(DueReferenceInstant, claimedForThisBusiness.ClaimedAt);
        Assert.Null(claimedForThisBusiness.RecipientOwnerId);
        Assert.Null(claimedForThisBusiness.SentAt);

        Assert.Equal(1, await CountRunsAsync(businessId, expectedPeriod.PeriodStartUtc));
    }

    // ── 2. Not-yet-due business is not claimed ───────────────────────

    [Fact]
    public async Task PollAsync_NotYetDueBusiness_ClaimsNothing()
    {
        var businessId = await SeedBusinessAsync("Not Due", enabled: true);
        var period = BusinessWeekPeriodCalculator.Compute(DueReferenceInstant, "Europe/Copenhagen");
        var justBeforeDue = period.SendAtUtc.AddSeconds(-1);

        await using var db = DbFor();
        var scheduler = SchedulerFor(db, justBeforeDue);
        var result = await scheduler.PollAsync(CancellationToken.None);

        Assert.DoesNotContain(result, r => r.BusinessId == businessId);
        Assert.Equal(0, await CountRunsAsync(businessId, period.PeriodStartUtc));
    }

    // ── 3. Disabled business is not claimed, even if otherwise due ───

    [Fact]
    public async Task PollAsync_DisabledBusiness_NeverClaimed_EvenThoughDue()
    {
        // Excluded by design §8.3's own discovery-query Where(ScheduledReportsEnabled)
        // filter before the per-business claim-linearization re-check is
        // ever reached for this business - see this file's own header
        // comment and the final report for why a fully deterministic test
        // isolating the re-check's own branch specifically (as opposed to
        // the overall enablement gate, which this test does prove) was not
        // constructed: the window between the batch discovery query and the
        // per-business fresh re-check is a same-process, sub-millisecond
        // synchronous gap with no natural external trigger point, and
        // forcing it open deterministically would require adding a
        // production-code testing hook solely for this one narrow ordering
        // proof - disproportionate to what it would prove beyond direct
        // source inspection of ScheduledReportSchedulerService.cs's two
        // distinct ScheduledReportsEnabled reads. The re-check's own "true"
        // branch (agreement, claim proceeds) is exercised by every
        // positive-path test in this file.
        var businessId = await SeedBusinessAsync("Disabled", enabled: false);
        var period = BusinessWeekPeriodCalculator.Compute(DueReferenceInstant, "Europe/Copenhagen");

        await using var db = DbFor();
        var scheduler = SchedulerFor(db, DueReferenceInstant);
        var result = await scheduler.PollAsync(CancellationToken.None);

        Assert.DoesNotContain(result, r => r.BusinessId == businessId);
        Assert.Equal(0, await CountRunsAsync(businessId, period.PeriodStartUtc));
    }

    // ── 4. Already-claimed period is not re-claimed — proven against the
    //    real unique constraint, not an application-level pre-check ────

    [Fact]
    public async Task PollAsync_PeriodAlreadyHasARun_NotReClaimed_RealUniqueConstraintEnforces()
    {
        var businessId = await SeedBusinessAsync("Already Claimed", enabled: true);
        var period = BusinessWeekPeriodCalculator.Compute(DueReferenceInstant, "Europe/Copenhagen");

        var existingRunId = Guid.NewGuid();
        await using (var seedDb = DbFor())
        {
            seedDb.ScheduledReportRuns.Add(new ScheduledReportRun
            {
                Id = existingRunId,
                BusinessId = businessId,
                ReportPeriodStart = period.PeriodStartUtc,
                ReportPeriodEnd = period.PeriodEndUtcExclusive,
                Status = ScheduledReportRunStatus.Processing,
                ClaimedAt = DueReferenceInstant.AddMinutes(-5),
            });
            await seedDb.SaveChangesAsync();
        }

        await using var db = DbFor();
        var scheduler = SchedulerFor(db, DueReferenceInstant);
        var result = await scheduler.PollAsync(CancellationToken.None);

        Assert.DoesNotContain(result, r => r.BusinessId == businessId);
        Assert.Equal(1, await CountRunsAsync(businessId, period.PeriodStartUtc)); // still just the pre-existing row

        await using var verifyDb = DbFor();
        var stillThere = await verifyDb.ScheduledReportRuns.IgnoreQueryFilters().SingleAsync(r => r.Id == existingRunId);
        Assert.Equal(DueReferenceInstant.AddMinutes(-5), stillThere.ClaimedAt); // untouched
    }

    // ── 5. Concurrency — the core adversarial proof ───────────────────

    [Fact]
    public async Task PollAsync_TwoConcurrentSchedulerInstances_ExactlyOneClaimsWins()
    {
        var businessId = await SeedBusinessAsync("Concurrent Claim", enabled: true);
        var period = BusinessWeekPeriodCalculator.Compute(DueReferenceInstant, "Europe/Copenhagen");

        // Two fully independent AppDbContext instances (independent
        // connections), each driving its own ScheduledReportSchedulerService
        // - the established "simulate two application instances" shape
        // (design §12.5, ApprovalExecutionKeyDeriver.cs's own cited
        // precedent). Raced via Task.WhenAll against the real dev DB, not a
        // mocked/in-memory provider.
        await using var dbA = DbFor();
        await using var dbB = DbFor();
        var schedulerA = SchedulerFor(dbA, DueReferenceInstant);
        var schedulerB = SchedulerFor(dbB, DueReferenceInstant);

        var taskA = schedulerA.PollAsync(CancellationToken.None);
        var taskB = schedulerB.PollAsync(CancellationToken.None);
        var results = await Task.WhenAll(taskA, taskB);

        var winners = results.Count(r => r.Any(run => run.BusinessId == businessId));
        var losers = results.Count(r => !r.Any(run => run.BusinessId == businessId));

        // Exactly one attempt succeeded...
        Assert.Equal(1, winners);
        // ...and the OTHER attempt's own returned claimed-list genuinely
        // never contains this business - the control-flow proof (design
        // §12.5's own explicit distinction from "just the final DB state"):
        // a caller iterating the loser's PollAsync result to hand runs to
        // Phase 6's execution service would structurally never see this
        // business's run from the losing call, regardless of what the
        // database now contains.
        Assert.Equal(1, losers);

        // Only one row exists afterward, confirmed via direct query.
        Assert.Equal(1, await CountRunsAsync(businessId, period.PeriodStartUtc));
    }

    // ── 6. Negative-proof mutation — see this phase's implementation
    //    report for the performed-and-reverted mutation and its results.
    //    Not a permanent test in this file (per design §12.5's own
    //    "committed suite contains only the real-schema test" convention,
    //    mirrored from Phase 3's own negative-proof methodology) - test 5
    //    above is what stays committed, run once against the correct
    //    implementation and once against a temporarily broken one during
    //    this phase's own verification, per the established ratchet
    //    standard.

    // ── 7. TimeProvider-driven due-detection correctness ──────────────

    [Fact]
    public async Task PollAsync_DueBoundary_ExactAtSendAtUtc_ClaimsRun()
    {
        var businessId = await SeedBusinessAsync("Due Exactly At Boundary", enabled: true);
        var period = BusinessWeekPeriodCalculator.Compute(DueReferenceInstant, "Europe/Copenhagen");

        await using var db = DbFor();
        var scheduler = SchedulerFor(db, period.SendAtUtc); // now == SendAtUtc exactly
        var result = await scheduler.PollAsync(CancellationToken.None);

        Assert.Contains(result, r => r.BusinessId == businessId);
    }

    [Fact]
    public async Task PollAsync_DueBoundary_OneTickBeforeSendAtUtc_DoesNotClaim()
    {
        var businessId = await SeedBusinessAsync("Not Due One Tick Before", enabled: true);
        var period = BusinessWeekPeriodCalculator.Compute(DueReferenceInstant, "Europe/Copenhagen");

        await using var db = DbFor();
        var scheduler = SchedulerFor(db, period.SendAtUtc.AddTicks(-1));
        var result = await scheduler.PollAsync(CancellationToken.None);

        Assert.DoesNotContain(result, r => r.BusinessId == businessId);
    }

    [Fact]
    public async Task PollAsync_DueBoundary_OneTickAfterSendAtUtc_Claims()
    {
        var businessId = await SeedBusinessAsync("Due One Tick After", enabled: true);
        var period = BusinessWeekPeriodCalculator.Compute(DueReferenceInstant, "Europe/Copenhagen");

        await using var db = DbFor();
        var scheduler = SchedulerFor(db, period.SendAtUtc.AddTicks(1));
        var result = await scheduler.PollAsync(CancellationToken.None);

        Assert.Contains(result, r => r.BusinessId == businessId);
    }

    // ── 8. Scoped service testable without the BackgroundService wrapper ─
    // Every test above already proves this directly (ScheduledReportSchedulerService
    // is constructed and PollAsync called directly, no
    // ScheduledReportBackgroundService/PeriodicTimer/HostedService anywhere
    // in this file). This test additionally confirms one poll correctly
    // and independently claims across MULTIPLE distinct businesses in a
    // single call - a scenario that specifically requires the change-tracker
    // detach-on-loss handling described in ScheduledReportSchedulerService.cs's
    // own doc comment (a shared AppDbContext across the whole loop), which
    // only a direct, multi-business PollAsync call (not a single-business
    // BackgroundService tick simulation) can exercise.

    [Fact]
    public async Task PollAsync_MultipleBusinesses_OneAlreadyClaimed_DoesNotBlockClaimingTheOthers()
    {
        var businessIdAlreadyClaimed = await SeedBusinessAsync("Multi A Already Claimed", enabled: true);
        var businessIdFresh1 = await SeedBusinessAsync("Multi B Fresh", enabled: true);
        var businessIdFresh2 = await SeedBusinessAsync("Multi C Fresh", enabled: true);
        var period = BusinessWeekPeriodCalculator.Compute(DueReferenceInstant, "Europe/Copenhagen");

        await using (var seedDb = DbFor())
        {
            seedDb.ScheduledReportRuns.Add(new ScheduledReportRun
            {
                Id = Guid.NewGuid(),
                BusinessId = businessIdAlreadyClaimed,
                ReportPeriodStart = period.PeriodStartUtc,
                ReportPeriodEnd = period.PeriodEndUtcExclusive,
                Status = ScheduledReportRunStatus.Processing,
                ClaimedAt = DueReferenceInstant.AddMinutes(-5),
            });
            await seedDb.SaveChangesAsync();
        }

        await using var db = DbFor();
        var scheduler = SchedulerFor(db, DueReferenceInstant);
        var result = await scheduler.PollAsync(CancellationToken.None);

        // The pre-claimed business correctly lost (no duplicate, no crash
        // propagating out of PollAsync)...
        Assert.DoesNotContain(result, r => r.BusinessId == businessIdAlreadyClaimed);
        // ...and this did NOT prevent the other two businesses, processed
        // later in the same loop over the same shared AppDbContext, from
        // being claimed successfully - proving the change-tracker
        // detach-on-loss handling actually works, not merely that it
        // compiles.
        Assert.Contains(result, r => r.BusinessId == businessIdFresh1);
        Assert.Contains(result, r => r.BusinessId == businessIdFresh2);
    }
}
