namespace backend.Services;

/// <summary>
/// Actually calls Shopify's orderCancel mutation for a Pending
/// OrderCancellation row — the half of the workflow the idempotency
/// mechanism (OrderCancellationService) deliberately left unbuilt. See
/// docs/handoffs/pending-reconciliation-design-scoping.md for the full
/// design and its disclosed residual risks.
///
/// Deliberately never calls MarkConfirmedAsync — job acceptance is not
/// completion (orderCancel is asynchronous), so only
/// OrderCancellationReconciliationService, polling Shopify's own
/// Order.cancelledAt as ground truth, ever performs that transition. This
/// keeps exactly one code path responsible for Confirmed.
///
/// No production trigger exists yet (deliberate, per the approved design
/// scope) — exercised only by tests against a fake Shopify HTTP handler.
/// </summary>
public interface IOrderCancellationExecutor
{
    /// <summary>
    /// Loads the row by id. A no-op if it is not currently Pending (already
    /// resolved by a prior call or by reconciliation) — never re-executes a
    /// terminal row. On a genuine Shopify-level rejection
    /// (orderCancelUserErrors non-empty), calls MarkFailedAsync and
    /// returns. On any other outcome (job accepted, still processing)
    /// leaves the row Pending. Infrastructure-level failures (missing/
    /// inactive Integration, token decrypt failure, non-2xx, malformed
    /// JSON, top-level GraphQL errors) propagate as exceptions — these are
    /// not legitimate cancellation outcomes and must not be silently
    /// recorded as one.
    /// </summary>
    Task ExecuteAsync(Guid orderCancellationId, CancellationToken cancellationToken);
}
