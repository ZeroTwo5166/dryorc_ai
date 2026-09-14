namespace backend.Enums;

/// <summary>
/// Step 24 Phase 3 — see docs/handoffs/step-24-scheduled-ai-reports-design.md
/// §10.1/§15.1 for the full state machine this represents. Exactly four
/// states, no more (no "Queued", no retry/permanent-failure split) —
/// Processing is the sole non-terminal state; Sent/Failed/Skipped are all
/// terminal, distinguished (for Failed) by ScheduledReportRun.FailureReason,
/// not by additional enum members, matching OrderCancellationStatus/
/// RefundStatus/CustomerUpdateActionStatus's own established granularity.
/// </summary>
public enum ScheduledReportRunStatus
{
    Processing,
    Sent,
    Failed,
    Skipped
}
