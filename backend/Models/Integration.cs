using backend.Interfaces;

namespace backend.Models;

public class Integration : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid BusinessId { get; set; }

    public required string Platform { get; set; }

    public required string EncryptedAccessToken { get; set; }

    /// <summary>Populated for providers whose token model requires it — as
    /// of Step 19, Shopify (a public/distributed app, subject to Shopify's
    /// expiring-offline-token model; see ShopifyIntegrationOAuthProvider's
    /// own doc comment for the evidence trail). Null for providers with no
    /// refresh concept. This comment previously asserted Shopify didn't need
    /// this field at all — that was Step 18's stale assumption that Shopify
    /// offline tokens never expire, corrected by Step 19's verification
    /// against Shopify's current documentation.</summary>
    public string? EncryptedRefreshToken { get; set; }

    public string? ExternalAccountIdentifier { get; set; }

    /// <summary>
    /// Nivora's cached record of the last confirmed OAuth scopes — NOT a
    /// live guarantee. Shopify's API is the ultimate authority; a scope
    /// check here can pass while Shopify still rejects the call with 403
    /// if the permission was revoked outside a fresh OAuth event. Always
    /// handle that live rejection gracefully — never assume this array
    /// being correct means the call will succeed. Must be fully replaced
    /// (never merged) on every fresh OAuth response.
    /// </summary>
    public List<string> Scopes { get; set; } = [];

    /// <summary>When Scopes was last confirmed/replaced.</summary>
    public DateTimeOffset? ScopesUpdatedAt { get; set; }

    public DateTimeOffset ConnectedAt { get; set; }
    public DateTimeOffset? DisconnectedAt { get; set; }
    public DateTimeOffset? TokenExpiresAt { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Step Shopify-auth-reauth (docs/handoffs/step-shopify-auth-reauth-design.md
    /// §4) — orthogonal to IsActive, never a substitute for it. Null means
    /// healthy (no known credential problem); non-null is the UTC instant a
    /// Shopify mutation for this integration was most recently classified as
    /// an authentication/authorization failure (HTTP 401/403) or a local
    /// token-decryption failure, per the locked Decision 7 predicate — never
    /// set from TokenExpiresAt, never set proactively, never set from an
    /// ordinary GraphQL userErrors rejection. Cleared to null on a successful
    /// OAuth reconnect (IntegrationsController.Callback) or a subsequent
    /// confirmed Shopify mutation success for this integration. IsActive
    /// takes priority over this field wherever both are consulted together:
    /// a disconnected integration (IsActive == false) is "Disconnected," not
    /// "Requires Reauthorization," regardless of this value.
    /// </summary>
    public DateTimeOffset? ReauthorizationRequiredAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
