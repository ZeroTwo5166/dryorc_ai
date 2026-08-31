using backend.Enums;

namespace backend.Services;

public record OrderCancellationResult(
    Guid OrderCancellationId,
    IdempotentOperationOutcome Outcome,
    string? FailureReason);

/// <summary>
/// Idempotency mechanism half of the OrderCancellation workflow — see the
/// idempotency-mechanism handoff for the full design. Does not call Shopify
/// itself; RequestCancellationAsync only ever leaves a Pending row behind on
/// a fresh request. ShopifyOrderCancellationExecutor actually invokes
/// Shopify's orderCancel and calls MarkFailedAsync on orderCancelUserErrors;
/// OrderCancellationReconciliationService is the only caller of
/// MarkConfirmedAsync, via polling Order.cancelledAt — see
/// docs/handoffs/pending-reconciliation-design-scoping.md.
/// </summary>
public interface IOrderCancellationService
{
    /// <summary>
    /// Idempotent entry point. businessId/customerId are explicit,
    /// caller-supplied — never inferred, matching every other non-HTTP
    /// service in this codebase's own defensive posture. idempotencyKey is
    /// caller-supplied — never generated or substituted here; the future
    /// tool-dispatch caller must derive a stable key per logical intent and
    /// reuse it across retries of that same intent.
    /// </summary>
    Task<OrderCancellationResult> RequestCancellationAsync(
        Guid businessId, Guid customerId, Guid? conversationId,
        string externalOrderId, string? customerReason,
        string idempotencyKey, bool restock, RefundMethod? refundMethod, OrderCancelReason reason,
        CancellationToken cancellationToken);

    /// <summary>
    /// Transitions Pending → Confirmed and sets CompletedAt. A no-op if the
    /// row is not currently Pending (atomic conditional update — the WHERE
    /// clause is the entire guard, same idiom as
    /// NotificationsController.MarkRead/VerificationCodeService). The
    /// trigger for calling this — actually invoking Shopify's orderCancel
    /// and interpreting its response, or the future reconciliation
    /// mechanism confirming an async job — is out of scope here.
    /// </summary>
    Task MarkConfirmedAsync(Guid orderCancellationId, CancellationToken cancellationToken);

    /// <summary>Transitions Pending → Failed. Same no-op-if-not-Pending
    /// guard and out-of-scope trigger caveat as MarkConfirmedAsync.</summary>
    Task MarkFailedAsync(Guid orderCancellationId, string failureReason, CancellationToken cancellationToken);
}
