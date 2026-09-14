using backend.Models;

namespace backend.Services;

/// <summary>
/// Step 24 Phase 5 — discovers enabled, due businesses and atomically
/// claims one <see cref="ScheduledReportRun"/> per due, unclaimed
/// `(BusinessId, ReportPeriodStart)`. See
/// docs/handoffs/step-24-scheduled-ai-reports-design.md §8/§12 for the full
/// design this implements. Persistence/discovery only — this phase does not
/// build what happens after a successful claim (Phase 6's
/// IScheduledReportExecutionService).
/// </summary>
public interface IScheduledReportSchedulerService
{
    /// <summary>
    /// One discovery/claim pass. Returns exactly the runs this call
    /// successfully claimed this poll — never a run another instance won,
    /// never a run that already existed for its period. A caller with
    /// nothing further to do (Phase 5 itself) may simply discard the
    /// result; Phase 7's BackgroundService wiring will pass each returned
    /// run to Phase 6's execution service.
    /// </summary>
    Task<IReadOnlyList<ScheduledReportRun>> PollAsync(CancellationToken cancellationToken);
}
