using backend.Models;

namespace backend.Services;

/// <summary>
/// Step 24 Phase 6 — carries one already-claimed (Status == Processing)
/// <see cref="ScheduledReportRun"/> through owner resolution, report-content
/// generation, email send, and finalization. See
/// docs/handoffs/step-24-scheduled-ai-reports-design.md §9/§10/§11/§13 for
/// the full design this implements verbatim. Persistence/discovery/claiming
/// (Phase 5, <see cref="IScheduledReportSchedulerService"/>) is a strict
/// prerequisite this service never repeats or re-validates beyond the single
/// authoritative enablement re-check design §9 step 1 requires — by the time
/// <see cref="ExecuteAsync"/> is called, the claim has already committed and
/// no further multi-instance race exists for this run (design §12.4: the
/// losing claimant never reaches this service at all).
///
/// Not yet registered in DI or invoked by any BackgroundService as of this
/// phase (Phase 7's own dedicated responsibility, per the implementation
/// plan's dependency-ordering rationale — see
/// ScheduledReportBackgroundService.cs's own doc comment for why DI wiring
/// is held to a single final phase).
/// </summary>
public interface IScheduledReportExecutionService
{
    /// <summary>
    /// Runs exactly once per claimed run. Never retried, never called twice
    /// for the same run by design (design §10.4 — a stuck Processing row is
    /// never automatically resumed). See design §9 for the exact step
    /// ordering (enablement re-check → Owner resolution → report generation
    /// → email send → finalize) and §13 for why send (step 5) always
    /// precedes the terminal-state persist (step 6), never the reverse.
    /// </summary>
    Task ExecuteAsync(ScheduledReportRun claimedRun, CancellationToken cancellationToken);
}
