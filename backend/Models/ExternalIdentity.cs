using backend.Interfaces;

namespace backend.Models;

/// <summary>
/// Per-channel identity verification links between a Customer and an
/// external identity assertion — e.g. a signed token from the business's
/// storefront login, or an OTP-verified email/phone. A Customer may have
/// multiple ExternalIdentities across different channels (Section 4,
/// per-channel scoping).
///
/// Distinct from Customer.ExternalId, which is the commerce-system lookup
/// key (e.g. Shopify customer ID) — this table is about proof of identity,
/// not where to find order data.
///
/// Customer anonymization does not cascade-delete these rows automatically
/// (Customer is never hard-deleted, only anonymized in place) — the
/// anonymization service must explicitly delete this customer's
/// ExternalIdentities rows as its own step, since they are pure identity
/// proof with no reason to survive deletion.
///
/// IMPORTANT: RevokedAt IS NULL does not mean an identity is currently
/// valid — it may still be expired. Always check validity via
/// ExternalIdentityExtensions.IsCurrentlyValid(), never RevokedAt alone.
/// </summary>
public class ExternalIdentity : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid BusinessId { get; set; }
    public Guid CustomerId { get; set; }

    /// <summary>See <see cref="ConversationChannels"/> for valid values.</summary>
    public required string Channel { get; set; }

    /// <summary>See <see cref="IdentityVerificationMethods"/> for valid values.</summary>
    public required string VerificationMethod { get; set; }

    public required string ExternalUserId { get; set; }

    public DateTimeOffset LinkedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>
    /// Populated only when RevokedAt is set and revocation was
    /// staff-initiated (not an automatic expiry).
    /// </summary>
    public Guid? RevokedByUserId { get; set; }
}
