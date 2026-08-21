using backend.Enums;
using backend.Interfaces;

namespace backend.Models;

/// <summary>
/// Idempotency-tracked record of a Shopify customerUpdate call.
/// customerUpdate is SYNCHRONOUS — Shopify returns the result (updated
/// customer + userErrors) directly in the same response, no job/polling
/// involved. Status can be set to Confirmed/Failed immediately based on
/// that response.
///
/// No ExternalCustomerId snapshot is stored here (unlike
/// OrderCancellation.ExternalOrderId) — deliberately, to avoid retaining
/// a re-identifying link to the customer's external record past
/// anonymization. Use CustomerId → Customer.ExternalId while the customer
/// is not yet anonymized; that link is intentionally lost afterward.
///
/// UpdatedAt is not automatically maintained — whatever service mutates
/// this row (e.g. Pending → Confirmed) must explicitly set it in the same
/// write.
///
/// The AI's requested update is never executed on trust alone — the
/// backend must verify CustomerId matches the conversation's Level-2-
/// verified customer before calling Shopify.
///
/// Not touched by CustomerErasureService/ConversationPurgeService at all —
/// see FinancialRecordRetentionPolicy for why (a legal retention
/// obligation, not indefinite retention; no enforcement job exists yet).
/// </summary>
public class CustomerUpdateAction : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid BusinessId { get; set; }

    public Guid CustomerId { get; set; }
    public Guid? ConversationId { get; set; }

    /// <summary>Which fields were updated, e.g. ["email", "phone"] — never
    /// the actual old/new values (PII).</summary>
    public List<string> FieldsChanged { get; set; } = [];

    public required string IdempotencyKey { get; set; }
    public CustomerUpdateActionStatus Status { get; set; } = CustomerUpdateActionStatus.Pending;

    public string? FailureReason { get; set; }

    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
