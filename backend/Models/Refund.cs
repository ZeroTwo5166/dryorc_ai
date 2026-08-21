using backend.Enums;
using backend.Interfaces;

namespace backend.Models;

/// <summary>
/// Idempotency-tracked record of a Shopify refundCreate call. Supports
/// full, partial, and line-item-specific refunds via RefundItem — distinct
/// from OrderCancellation (orderCancel), since a refund can happen without
/// cancelling anything.
///
/// refundCreate is SYNCHRONOUS — same as customerUpdate, no job/polling.
///
/// TotalAmount is nullable — null while Pending/Failed (amount not yet
/// confirmed), set only from Shopify's actual response on success. Never
/// derived by summing RefundItem.Amount client-side.
///
/// Method reflects a two-layer authorization: Business.StoreCreditRefundsEnabled
/// gates whether store credit is offered at all; the customer then chooses
/// between whatever option(s) that gate allows. The AI relays customer
/// intent but never decides eligibility or amount — the backend validates
/// against the business toggle and refund policy before executing.
///
/// ExternalRefundId is kept (not redacted on customer anonymization) —
/// an operational fact, same as OrderCancellation.ExternalOrderId. Not
/// touched by CustomerErasureService/ConversationPurgeService at all — see
/// FinancialRecordRetentionPolicy for why (a legal retention obligation,
/// not indefinite retention; no enforcement job exists yet).
///
/// The backend must verify CustomerId actually owns ExternalOrderId
/// before calling Shopify.
/// </summary>
public class Refund : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid BusinessId { get; set; }

    public Guid CustomerId { get; set; }
    public Guid? ConversationId { get; set; }

    public required string ExternalOrderId { get; set; }
    public string? ExternalRefundId { get; set; }

    public RefundStatus Status { get; set; } = RefundStatus.Pending;
    public RefundMethod Method { get; set; }

    public required string IdempotencyKey { get; set; }

    /// <summary>Null until Confirmed. Set from Shopify's response only.</summary>
    public decimal? TotalAmount { get; set; }
    public required string Currency { get; set; }

    public string? FailureReason { get; set; }

    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
