using backend.Enums;
using backend.Interfaces;

namespace backend.Models;

/// <summary>
/// Step 24 Phase 3 — the durable run-tracking record for one Scheduled AI
/// Report attempt. See docs/handoffs/step-24-scheduled-ai-reports-design.md
/// §15.1 for the locked field-by-field contract this implements verbatim,
/// and §7/§12 for why this entity plays both the "discovery claim" and
/// "execution intent" roles a single design pass — there is no separate
/// earlier proposal step for scheduled reports the way there is for the AI
/// Action Safety Model's Approval/OrderCancellation split.
///
/// One row per (BusinessId, ReportPeriodStart) — created only at claim
/// time (Section 12.2), never precomputed ahead of the period being due.
///
/// ReportPeriodStart/ReportPeriodEnd are the UTC instants corresponding to
/// the [local-Monday-00:00, local-Monday-00:00) calendar period
/// BusinessWeekPeriodCalculator.Compute produced (Section 5.5) — together
/// they ARE this run's natural idempotency key; no derived hash column is
/// needed here, unlike OrderCancellation.IdempotencyKey (design §15.1's own
/// reasoning: a UTC timestamp pair is already compact and directly
/// indexable, with no encoding-ambiguity risk a hash would be guarding
/// against).
///
/// RecipientOwnerId is nullable, per the design's own explicit correction
/// to its initial sketch (§15.1): the row is created at claim time, before
/// Owner resolution (Section 9 step 2) ever runs, so it cannot be
/// known/non-null at INSERT time. Set only on the path that reaches a
/// resolved single Owner; left null on every Failed/Skipped outcome.
///
/// This entity is persistence only, per this phase's own explicitly narrow
/// scope (mirroring Approval.cs's own precedent for the identical
/// reasoning) — nothing in this phase constructs, reads, or writes this
/// table; Phases 5/6 do.
/// </summary>
public class ScheduledReportRun : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid BusinessId { get; set; }

    public required DateTimeOffset ReportPeriodStart { get; set; }
    public required DateTimeOffset ReportPeriodEnd { get; set; }

    public ScheduledReportRunStatus Status { get; set; } = ScheduledReportRunStatus.Processing;

    public Guid? RecipientOwnerId { get; set; }
    public DateTimeOffset ClaimedAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }

    /// <summary>
    /// "NoActiveOwner" | "MultipleActiveOwners" | "AnalyticsQueryFailed" |
    /// "EmailSendFailed:{provider detail}" | null — free-text, matching
    /// OrderCancellation.FailureReason/CustomerUpdateAction.FailureReason/
    /// Refund.FailureReason's identical existing convention (design §15.1),
    /// not a classified/enum representation.
    /// </summary>
    public string? FailureReason { get; set; }
}
