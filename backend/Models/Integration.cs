using backend.Interfaces;

namespace backend.Models;

public class Integration : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid BusinessId { get; set; }

    public required string Platform { get; set; }

    public required string EncryptedAccessToken { get; set; }

    /// <summary>NOT YET IMPLEMENTED — deferred until a refresh-requiring
    /// platform (Slack/Google/Microsoft) is built. Shopify doesn't need
    /// this.</summary>
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
    public DateTimeOffset UpdatedAt { get; set; }
}
