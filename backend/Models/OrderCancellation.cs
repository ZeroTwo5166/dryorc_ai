using backend.Enums;
using backend.Interfaces;

namespace backend.Models;

/// <summary>
/// Idempotency-tracked record of a Shopify orderCancel call. Method/Reason/
/// Restock are all parameters of the same orderCancel mutation (not
/// separate actions) — a standalone refund-without-cancellation goes
/// through a separate refundOrder tool/table (Refund/RefundItem), not yet
/// wired to any execution code.
///
/// Method/Restock/Reason reflect Nivora's own policy default at the moment
/// this cancellation executed, translated into Shopify's current (verified
/// against shopify.dev this session — the plain `refund: Boolean` argument
/// was deprecated in API version 2025-07) orderCancel parameters: `reason`
/// is a required enum, `refundMethod` is a structured object, not a bool.
/// Shopify does not require a refund on cancellation — Method == null is a
/// fully valid Shopify state (cancellation-without-refund), not an error.
///
/// orderCancel is ASYNCHRONOUS — Shopify returns a job, not an immediate
/// result. Do not mark Status = Confirmed just because Shopify accepted the
/// mutation (job.done doesn't by itself prove success). ShopifyOrderCancellationExecutor
/// never itself calls MarkConfirmedAsync — only OrderCancellationReconciliationService
/// does, by polling Shopify's own Order.cancelledAt as ground truth, so
/// exactly one code path ever performs that transition. FailureReason must
/// capture immediate mutation-level validation errors (orderCancelUserErrors)
/// recorded by the executor — async job failures with no corresponding
/// orderCancelUserErrors surface only as a row that never reaches
/// Order.cancelledAt, flagged (not auto-resolved) by reconciliation.
///
/// CompletedAt is set only on successful confirmation — remains null on
/// Failed (use FailureReason + UpdatedAt to determine when a failure was
/// recorded).
///
/// The AI's requested cancellation is never executed on trust alone —
/// the backend must verify CustomerId actually owns ExternalOrderId
/// before calling Shopify, independent of whether the conversation is
/// Level 2 verified for some customer in general.
///
/// Not touched by CustomerErasureService/ConversationPurgeService at all —
/// see FinancialRecordRetentionPolicy for why (a legal retention
/// obligation, not indefinite retention; no enforcement job exists yet).
/// </summary>
public class OrderCancellation : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid BusinessId { get; set; }

    public Guid CustomerId { get; set; }
    public Guid? ConversationId { get; set; }

    public required string ExternalOrderId { get; set; }
    public string? CustomerReason { get; set; }

    public required string IdempotencyKey { get; set; }
    public OrderCancellationStatus Status { get; set; } = OrderCancellationStatus.Pending;

    public bool Restock { get; set; } = false;

    /// <summary>
    /// Shopify's own required orderCancel argument — not to be confused
    /// with CustomerReason (free text, excluded from idempotency intent).
    /// </summary>
    public OrderCancelReason Reason { get; set; } = OrderCancelReason.Customer;

    /// <summary>
    /// Null means no refund. Non-null selects how Shopify's orderCancel
    /// refundMethod argument is built — see ShopifyOrderCancellationExecutor.
    /// Reuses the same enum as Refund.Method (a separate, still-unbuilt
    /// standalone refund feature) since both represent the identical
    /// Shopify-level choice.
    /// </summary>
    public RefundMethod? Method { get; set; } = Enums.RefundMethod.OriginalPaymentMethod;

    public string? FailureReason { get; set; }

    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}