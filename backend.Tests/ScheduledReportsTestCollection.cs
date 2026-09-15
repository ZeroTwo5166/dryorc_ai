namespace backend.Tests;

/// <summary>
/// Step 24 Phase 7 test-isolation fix. ScheduledReportSchedulerService's
/// discovery query (design §8.3) is deliberately cross-tenant —
/// IgnoreQueryFilters(), no BusinessId scoping, by design, since Phase 5's
/// whole point is scanning every business with ScheduledReportsEnabled=true
/// in one pass. That is correct in production (Business rows are never
/// hard-deleted there) but becomes unsafe when MULTIPLE test files
/// independently create-and-hard-delete real ScheduledReportsEnabled=true
/// Business rows concurrently: one file's scheduler can discover another
/// file's business in its own cross-tenant batch, and if that OTHER file's
/// own cleanup deletes it between the batch discovery query and the
/// per-business "still enabled" recheck, PollAsync throws uncaught (a real
/// gap, but one that cannot occur in production and must not be
/// "fixed" there merely to accommodate this test pattern).
///
/// Confirmed empirically this phase in two rounds: (1)
/// ScheduledReportSchedulerServiceTests is 100% clean in isolation (3/3
/// runs) and fails only when it runs concurrently with
/// ScheduledReportExecutionServiceTests and/or
/// ScheduledReportBackgroundServiceTests's own real hosted-service E2E test
/// — both of which also seed ScheduledReportsEnabled=true businesses; (2)
/// even with those three collected together, a full-suite run still showed
/// the identical failure signature once — traced to ScheduledReportsApiTests
/// (Phase 1's real toggle-endpoint tests), the only other file in the whole
/// suite that sets Business.ScheduledReportsEnabled=true (confirmed by
/// grep) and was not yet in this collection. Its own NivoraApiFactory-hosted
/// scheduler is already frozen to a permanently-not-due instant (that
/// factory's other Phase 7 test-isolation fix), so it can never be the
/// AGGRESSOR — but nothing protected it from being the VICTIM of Phase 5's
/// own always-due direct PollAsync calls discovering its business in the
/// same cross-tenant batch.
///
/// Fix: put every test file that seeds a ScheduledReportsEnabled=true
/// Business, or drives a real (direct or hosted) scheduler tick, in this one
/// shared xUnit collection — collection members never run in parallel with
/// each other, but every other test class in the suite (including every
/// other *ApiTests.cs file) is completely unaffected and keeps running with
/// full xUnit parallelism exactly as before. A narrow, test-only scoping
/// fix, not a suite-wide parallelism change. If a future test file ever
/// needs to set this flag too, it must join this same collection.
///
/// Membership as of Step 24's Final E2E verification pass:
/// ScheduledReportSchedulerServiceTests, ScheduledReportExecutionServiceTests,
/// ScheduledReportBackgroundServiceTests, ScheduledReportsApiTests, and
/// ScheduledReportsFinalE2ETests (the last one added directly at authoring
/// time, not discovered as a later fix — its own real registration +
/// enablement HTTP calls and its own due-scheduler-overridden verification
/// host are exactly the shape this collection exists to protect).
/// </summary>
[CollectionDefinition("ScheduledReports")]
public class ScheduledReportsTestCollection;
