using backend.Interfaces;

namespace backend.Models;

public class Customer : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid BusinessId { get; set; }

    /// <summary>
    /// The business's own customer identifier (e.g. Shopify customer ID),
    /// used to upsert this record when a signed identity token arrives
    /// (Section 4, Level 2 verification). Nulled on anonymization along
    /// with Email/Name.
    /// </summary>
    public string? ExternalId { get; set; }

    public string? Email { get; set; }
    public string? Name { get; set; }

    /// <summary>
    /// True once this customer has been anonymized via a data-subject
    /// deletion request. The row and Id persist (so Ticket.CustomerId and
    /// other required FKs stay valid), but Email/Name/ExternalId are
    /// nulled. Distinguishes "deliberately deleted" from "never had this
    /// info collected" — both would otherwise look identical.
    /// </summary>
    public bool IsAnonymized { get; set; } = false;
    public DateTimeOffset? AnonymizedAt { get; set; }

    /// <summary>
    /// Set once CustomerErasureService begins erasing this customer, cleared
    /// again once erasure reaches its normal terminal state. Persists the
    /// "erasure in progress" fact across the service's own multiple,
    /// separately-committing transactions — the ForCustomerErasure advisory
    /// lock (AdvisoryLockKeyGenerator) is transaction-scoped and leaves no
    /// trace once released, so this column is what a later-arriving
    /// identity-mutating write actually checks. May remain non-null
    /// indefinitely if an erasure is abandoned after being set; no
    /// automatic staleness/expiry/reconciliation is implied by this field
    /// alone (docs/handoffs/identity-binding-erasure-integrity-design.md
    /// §3.7).
    /// </summary>
    public DateTimeOffset? PendingErasureAt { get; set; }

    /// <summary>
    /// Current state only — true if this customer is presently flagged for
    /// closer review (e.g. suspected fraud). Takes effect only when
    /// Business.ExtendedAuditLoggingEnabled is also true. Full history of
    /// who enabled/disabled this, and when, lives in
    /// CustomerExtendedLoggingHistory — this field just reflects the
    /// current state for fast reads.
    /// </summary>
    public bool ExtendedLoggingEnabled { get; set; } = false;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}