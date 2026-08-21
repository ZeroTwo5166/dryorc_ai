using backend.Enums;
using backend.Interfaces;

namespace backend.Models;

/// <summary>
/// Idempotency-tracked record of a Shopify orderCancel call. Refund and
/// Restock are both parameters of the same orderCancel mutation (not
/// separate actions) — a standalone refund-without-cancellation goes
/// through a separate refundOrder tool/table, not yet designed.
///
/// Refund/Restock reflect Nivora's own policy default at the moment this
/// cancellation executed, translated into Shopify's orderCancel
/// parameters — Shopify does not require a refund on cancellation;
/// cancellation-without-refund is a fully valid Shopify state. Refund
/// defaulting true is a Nivora policy choice, not a Shopify requirement.
///
/// orderCancel is ASYNCHRONOUS — Shopify returns a job, not an immediate
/// result. Do not mark Status = Confirmed just because Shopify accepted
/// the mutation; wait for job completion (poll or webhook) before
/// confirming. FailureReason must capture both immediate mutation-level
/// validation errors (orderCancelUserErrors) and later async job
/// failures — these are two different failure points in the same flow.
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
    public bool Refund { get; set; } = true;

    public string? FailureReason { get; set; }

    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}