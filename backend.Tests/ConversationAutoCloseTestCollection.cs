namespace backend.Tests;

/// <summary>
/// Step 25 Candidate 2, Phase 4 test-isolation fix — the identical class of
/// bug ScheduledReportsTestCollection.cs already documents and fixes for
/// Step 24, reproduced here for ConversationInactivityCloseService.
/// CloseInactiveConversationsAsync's discovery query is deliberately
/// cross-tenant (IgnoreQueryFilters(), no BusinessId scoping) — correct in
/// production, but unsafe when MULTIPLE test files independently create
/// real ConversationAutoCloseEnabled=true Business rows with deliberately
/// backdated (stale) Conversation rows: one file's sweep can discover and
/// close another file's still-in-progress conversation before that other
/// file's own test gets to assert on it.
///
/// Confirmed empirically this phase: ConversationInactivityCloseServiceTests
/// and DataLifecycleBackgroundServiceTests are each 100% clean in isolation,
/// but DataLifecycleBackgroundServiceTests'
/// AllFourJobs_CoexistInOneCycle_ThroughTheRealBackgroundService test
/// (which seeds a deliberately-stale, auto-close-enabled Conversation meant
/// to be closed only by ITS OWN retention job, not auto-close) failed only
/// when run concurrently with ConversationInactivityCloseServiceTests —
/// whose own tests each construct and run a separate, real
/// ConversationInactivityCloseService instance that performs a genuine
/// cross-tenant sweep with no awareness of any other file's businesses.
///
/// Fix: put every test file that seeds a real, deliberately-stale
/// Conversation row on a ConversationAutoCloseEnabled=true Business, or
/// drives a real (direct or hosted) ConversationInactivityCloseService
/// sweep, in this one shared xUnit collection — collection members never
/// run in parallel with each other, but every other test class in the
/// suite keeps running with full xUnit parallelism exactly as before.
///
/// BusinessApiTests.cs also sets ConversationAutoCloseEnabled=true (Phase 1's
/// own endpoint tests) but deliberately does NOT join this collection: it
/// never creates a Conversation row at all, so it has nothing a concurrent
/// sweep could discover and act on — it can be neither the aggressor nor
/// the victim of this specific race, unlike ScheduledReportsApiTests'
/// own inclusion in the Step 24 collection (which guards a real seeded row,
/// not just a flag).
///
/// Membership: ConversationInactivityCloseServiceTests,
/// DataLifecycleBackgroundServiceTests, GdprLifecycleFinalE2ETests,
/// Step25Candidate2FinalE2ETests, AiProviderInvocationRetentionServiceTests.
/// If a future test file ever seeds a real, stale Conversation row on an
/// auto-close-enabled Business, it must join this same collection.
///
/// AiProviderInvocationRetentionServiceTests (Step 27 A1 Slice 6) joins for
/// a related reason: every test host that runs the real
/// DataLifecycleBackgroundService is in this collection, and that host's
/// first tick runs the same global AiProviderInvocation retention delete —
/// which, run concurrently, could delete that test's deliberately-old
/// seeded rows before the test itself does.
/// </summary>
[CollectionDefinition("ConversationAutoClose")]
public class ConversationAutoCloseTestCollection;
