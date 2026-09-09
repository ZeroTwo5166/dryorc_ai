using backend.Enums;

namespace backend.Services;

public record RefundResult(
    Guid RefundId,
    IdempotentOperationOutcome Outcome,
    string? FailureReason);

/// <summary>
/// One requested line item, per Step refundOrder Phase 1 — see
/// docs/handoffs/step-refundorder-design.md §5.1/§10 (Owner Decision 1:
/// item/quantity-only refund input, no arbitrary caller-supplied amount).
///
/// Quantity is the only caller-facing input this record carries in the
/// AI-facing sense — a future RefundProposalTool (Phase 2, not implemented
/// here) will only ever ask the AI for (ExternalLineItemId, Quantity)
/// pairs. ResolvedAmount is deliberately named to make clear it is NOT
/// caller-arbitrary input: it must already have been derived by a trusted
/// system layer from Shopify's own authoritative per-unit pricing (the
/// design's own §8.3 capability, not yet built) before this record is ever
/// constructed. RefundService performs no pricing computation, no
/// rounding, and no Shopify validation of its own — it only persists an
/// already-resolved value, exactly mirroring how OrderCancellationService
/// already accepts RefundMethod?/OrderCancelReason as pre-resolved,
/// caller-supplied values without computing or validating them itself.
/// </summary>
public record RefundItemLine(string ExternalLineItemId, int Quantity, decimal ResolvedAmount);

/// <summary>
/// Idempotency mechanism half of the Refund workflow — see
/// docs/handoffs/step-refundorder-design.md §9/§11 for the full design
/// rationale, and IOrderCancellationService for the sibling contract this
/// mirrors. Does not call Shopify itself, and does not resolve pricing —
/// RequestRefundAsync only ever leaves a Pending Refund/RefundItem row
/// behind on a fresh request. A future ShopifyRefundExecutor (a later
/// phase, not implemented here) is what would actually invoke Shopify's
/// refundCreate and call MarkConfirmedAsync/MarkFailedAsync.
///
/// Deliberately does NOT add a DuplicatePendingRefundException-equivalent
/// to OrderCancellationService's own DuplicatePendingOrderCancellationException
/// — the design's own §9.4/§17 explicitly reasons that a refund is
/// naturally repeatable (multiple legitimate partial refunds against the
/// same order), unlike order cancellation's one-shot terminal nature, so
/// no "at most one Pending Refund per order" guard is added here. The only
/// uniqueness this service enforces is per idempotency key.
/// </summary>
public interface IRefundService
{
    /// <summary>
    /// Idempotent entry point. businessId/customerId are explicit,
    /// caller-supplied — never inferred, matching every other non-HTTP
    /// service in this codebase's own defensive posture. idempotencyKey is
    /// caller-supplied — never generated or substituted here. items must be
    /// non-empty, each with a positive Quantity and a positive
    /// ResolvedAmount, and no two items may share the same
    /// ExternalLineItemId within one request.
    /// </summary>
    Task<RefundResult> RequestRefundAsync(
        Guid businessId, Guid customerId, Guid? conversationId,
        string externalOrderId, string? customerReason,
        string idempotencyKey, RefundMethod refundMethod, string currency,
        IReadOnlyList<RefundItemLine> items,
        CancellationToken cancellationToken);

    /// <summary>
    /// Transitions Pending → Confirmed, sets CompletedAt, and records the
    /// Shopify-confirmed externalRefundId/totalAmount — Refund.TotalAmount
    /// is "set only from Shopify's actual response on success" per the
    /// model's own doc comment, never derived by summing RefundItem.Amount
    /// client-side, so this method accepts it as an already-known value
    /// rather than computing it. A no-op if the row is not currently
    /// Pending (atomic conditional update — the WHERE clause is the entire
    /// guard, same idiom as OrderCancellationService.MarkConfirmedAsync).
    /// The trigger for calling this — actually invoking Shopify's
    /// refundCreate and interpreting its response — is out of scope here
    /// (a later phase).
    /// </summary>
    Task MarkConfirmedAsync(
        Guid refundId, string externalRefundId, decimal totalAmount, CancellationToken cancellationToken);

    /// <summary>Transitions Pending → Failed. Same no-op-if-not-Pending
    /// guard and out-of-scope trigger caveat as MarkConfirmedAsync.</summary>
    Task MarkFailedAsync(Guid refundId, string failureReason, CancellationToken cancellationToken);
}
