using backend.Enums;

namespace backend.Services;

public record OrderCancellationResult(
    Guid OrderCancellationId,
    IdempotentOperationOutcome Outcome,
    string? FailureReason);

/// <summary>
/// Thrown by RequestCancellationAsync when the (BusinessId, ExternalOrderId)
/// WHERE Status='Pending' partial unique index blocks a second, concurrent
/// Pending row for the same order — see
/// docs/handoffs/step-22-ai-action-safety-model-order-cancel-already-cancelled-classification.md's
/// "Guard Concurrency Mechanism" subsection and
/// docs/handoffs/step-23q-order-cancellation-pending-guard-design.md.
/// Deliberately a distinct exception type, not a new IdempotentOperationOutcome
/// value (Owner Decision, Step 23S, Option B of 23Q §3) — this is a
/// structurally different conflict from KeyReusedWithDifferentIntent: a
/// different idempotency key, colliding on a different unique constraint,
/// against a row created by an entirely separate caller/Approval, not "the
/// same key reused with different intent." Callers must treat this as an
/// explicit, distinct refusal — never silently swallowed, never retried
/// automatically, never treated as license to proceed with a fresh mutation
/// (22R's own explicit "Blocked-Caller Behavior" requirement).
/// </summary>
public sealed class DuplicatePendingOrderCancellationException(Guid businessId, string externalOrderId)
    : Exception(
        $"A Pending OrderCancellation already exists for order '{externalOrderId}' in business {businessId}. " +
        "This action is blocked pending manual recovery of the existing attempt.")
{
    public Guid BusinessId { get; } = businessId;
    public string ExternalOrderId { get; } = externalOrderId;
}

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
