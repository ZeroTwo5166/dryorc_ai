namespace backend.Services;

/// <summary>
/// Result of a successful authorization-code exchange with an integration
/// provider. AccessToken/RefreshToken are plaintext here — encryption
/// happens exactly once, at IntegrationsController.Callback, via
/// IIntegrationTokenProtector; no other code path ever sees raw tokens.
///
/// RefreshToken is expected to be null for every provider Step 18 actually
/// ships against (Shopify's offline access tokens don't expire) — it's
/// plumbed through only because Integration.EncryptedRefreshToken already
/// exists in the schema for a future refresh-requiring platform (Slack/
/// Google/Microsoft, per that column's own doc comment). No refresh flow is
/// implemented in Step 18.
/// </summary>
public record OAuthTokenResult(
    string AccessToken,
    string? RefreshToken,
    string? ExternalAccountIdentifier,
    List<string> Scopes,
    DateTimeOffset? ExpiresAt);

/// <summary>
/// Minimal per-platform OAuth2 abstraction — deliberately just the two
/// operations every provider genuinely shares (build the authorization URL,
/// exchange a code for tokens). No RefreshTokenAsync member: nothing built
/// so far needs it (see OAuthTokenResult's doc comment); adding an
/// unimplemented/uncalled method here would design for a hypothetical
/// requirement instead of a real one.
///
/// No concrete production implementation of this interface is registered in
/// Program.cs as of Step 18 — that is intentional, not a gap. Every
/// {platform} route on IntegrationsController resolves providers via
/// IEnumerable&lt;IIntegrationOAuthProvider&gt;, which the built-in DI
/// container correctly resolves to an empty sequence when nothing is
/// registered, so every production request today hits the controller's
/// explicit "unregistered platform → 404" branch. Step 19 registers the
/// first real implementation (Shopify). backend.Tests registers a
/// test-only fake (FakeIntegrationOAuthProvider) to prove the full
/// connect → callback → exchange → persist → audit flow without any real
/// provider or network call.
/// </summary>
public interface IIntegrationOAuthProvider
{
    /// <summary>Matches Integration.Platform / the {platform} route segment
    /// this provider handles, e.g. "shopify". Exact, case-sensitive match —
    /// no normalization, consistent with how Channel/EventType string
    /// constants are compared elsewhere in this codebase.</summary>
    string Platform { get; }

    /// <summary>
    /// Takes <paramref name="externalAccountIdentifier"/> directly rather
    /// than the whole ConnectIntegrationRequest (Controllers-namespace type)
    /// — deliberate, to avoid a Services→Controllers dependency with no
    /// precedent anywhere else in this codebase. Today this carries all
    /// currently-defined connect-request data, since
    /// ConnectIntegrationRequest has exactly this one field. If a future
    /// provider genuinely requires additional connection parameters,
    /// widening this signature (or the request shape feeding it) is part of
    /// that future provider's own design/implementation pass — same
    /// deferral already decided for ConnectIntegrationRequest itself, not a
    /// gap introduced by this signature choice.
    /// </summary>
    string BuildAuthorizationUrl(string state, string externalAccountIdentifier);

    Task<OAuthTokenResult> ExchangeCodeAsync(string code, CancellationToken cancellationToken);
}
