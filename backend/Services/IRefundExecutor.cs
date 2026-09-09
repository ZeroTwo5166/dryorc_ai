namespace backend.Services;

/// <summary>
/// Actually calls Shopify's refundCreate mutation for a Pending Refund row —
/// the Shopify-facing half of the workflow RefundService (Phase 1) and
/// RefundProposalTool (Phase 2) deliberately left unbuilt. Mirrors
/// IOrderCancellationExecutor's shape, but the response-handling shape
/// diverges materially: refundCreate is confirmed SYNCHRONOUS (dedicated
/// Shopify research, docs/handoffs/step-refundorder-shopify-research.md Q5),
/// so a successful mutation call resolves the row directly — there is no
/// separate reconciliation service polling a job id the way
/// OrderCancellationReconciliationService does for orderCancel.
///
/// idempotencyKey is the caller's own execution-leg identity
/// (ApprovalExecutionKeyDeriver.Derive's output) — passed through to
/// Shopify's own @idempotent directive (research Q11). This makes a genuine
/// retry of the same execution attempt safe against Shopify creating a
/// second refund, but does not by itself close the distinct-Approval race
/// disclosed in design §17 (two different Approvals derive two different
/// keys; Shopify has no way to recognize them as related).
/// </summary>
public interface IRefundExecutor
{
    /// <summary>
    /// Loads the row by id. A no-op if it is not currently Pending (already
    /// resolved by a prior call) — never re-executes a terminal row. On a
    /// genuine Shopify-level rejection (refundCreate userErrors non-empty),
    /// calls MarkFailedAsync and returns. On success, calls MarkConfirmedAsync
    /// directly (never leaves the row Pending for a separate reconciliation
    /// pass — refundCreate's own confirmed synchronicity is what makes this
    /// safe). Infrastructure-level failures (missing/inactive Integration,
    /// token decrypt failure, non-2xx, malformed JSON, top-level GraphQL
    /// errors) propagate as exceptions — these are not legitimate refund
    /// outcomes and must not be silently recorded as one; the row is left
    /// Pending for manual recovery, per design §11.3/§18.
    /// </summary>
    Task ExecuteAsync(Guid refundId, string idempotencyKey, CancellationToken cancellationToken);
}
