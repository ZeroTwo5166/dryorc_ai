using backend.Interfaces;

namespace backend.Models;

/// <summary>
/// Step 21 (signed-token half). A business's registered RS256 public key,
/// used to verify signed customer-identity assertions from its own
/// storefront/integration. Nivora never stores, generates, or receives a
/// private key here — only the public key a business submits.
///
/// Id doubles as the JWT `kid` (serialized to its Guid string form) — no
/// separate Kid column, avoiding a redundant identifier.
///
/// Multiple concurrently-active rows per business are legal and expected
/// during key rotation — no uniqueness constraint prevents it (see
/// AppDbContext's non-unique BusinessId index).
///
/// V1 provisioning is manual/staff-mediated (no dashboard/endpoint in this
/// implementation unit) — CreatedByUserId records which staff member
/// registered it, for audit purposes.
/// </summary>
public class BusinessSigningKey : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid BusinessId { get; set; }

    public required string PublicKeyPem { get; set; }

    /// <summary>Stored per-row, not a global constant, so a future
    /// algorithm change never retroactively reinterprets an
    /// already-registered key — same reasoning as OrderCancellation.MaxAttempts.
    /// v1 only ever stores "RS256" (SecurityAlgorithms.RsaSha256).</summary>
    public required string Algorithm { get; set; }

    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Null = active. Checked live at every token validation, never
    /// cached at issuance time — a revocation must reject already-in-flight
    /// tokens signed under this key, not just block future issuance.</summary>
    public DateTimeOffset? RevokedAt { get; set; }
}
