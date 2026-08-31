namespace backend.Services;

/// <summary>
/// Periodic sweep of Pending OrderCancellation rows against Shopify's own
/// Order.cancelledAt as ground truth. See
/// docs/handoffs/pending-reconciliation-design-scoping.md for the full
/// design. Per the approved scope: no auto-retry, no ticket escalation —
/// a row stuck Pending past the threshold is only logged/flagged.
/// </summary>
public interface IOrderCancellationReconciliationService
{
    Task ReconcileAsync(CancellationToken cancellationToken);
}
