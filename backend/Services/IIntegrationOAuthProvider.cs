using Microsoft.AspNetCore.Http;

namespace backend.Services;

/// <summary>
/// Result of a successful authorization-code exchange with an integration
/// provider. AccessToken/RefreshToken are plaintext here — encryption
/// happens exactly once, at IntegrationsController.Callback, via
/// IIntegrationTokenProtector; no other code path ever sees raw tokens.
///
/// RefreshToken is non-null for Shopify as of Step 19 — Nivora's Shopify app
/// is a public/distributed app (one app, many independent merchant
/// installs), which Shopify's current documentation subjects to the
/// expiring-offline-token model (1-hour access tokens, 90-day refresh
/// tokens, requested via expiring=1). See ShopifyIntegrationOAuthProvider's
/// own doc comment for the evidence trail. This comment previously asserted
/// the opposite ("Shopify's offline access tokens don't expire... No refresh
/// flow is implemented") — that was Step 18's stale assumption, corrected
/// here by Step 19's verification against Shopify's current documentation.
/// </summary>
public record OAuthTokenResult(
    string AccessToken,
    string? RefreshToken,
    string? ExternalAccountIdentifier,
    List<string> Scopes,
    DateTimeOffset? ExpiresAt);

/// <summary>
/// Per-platform OAuth2 abstraction. BuildAuthorizationUrl/ExchangeCodeAsync
/// are the two operations every provider shares; ValidateCallbackStructure/
/// ValidateCallbackAuthenticity are Step 19 additions for providers (so far,
/// only Shopify) with their own callback-authenticity contract beyond
/// Nivora's own state-token/live-cookie checks (see
/// IntegrationsController.Callback for where these run in the pipeline).
///
/// Step 19 registers the first real implementation (Shopify;
/// ShopifyIntegrationOAuthProvider, wired in Program.cs) — this comment
/// previously described Step 18 as shipping zero concrete providers, which
/// was accurate at the time but is now superseded. backend.Tests registers
/// a second, test-only fake (FakeIntegrationOAuthProvider) to prove the full
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
    /// ConnectIntegrationRequest has exactly this one field. Providers with
    /// their own syntax rules for this value (e.g. Shopify's bare-shop-name
    /// rule) must validate it here and throw ArgumentException on an invalid
    /// value — IntegrationsController.Connect maps that to a 400.
    /// </summary>
    string BuildAuthorizationUrl(string state, string externalAccountIdentifier);

    /// <summary>
    /// Step 19 addition. Validates the provider's own structural contract
    /// for the raw incoming callback query (e.g. Shopify's exactly-five-
    /// required-parameters / no-duplicates / no-unknown-parameters rule) —
    /// deliberately evaluated against the actual query collection, not
    /// controller-bound parameters, which would silently hide duplicates or
    /// unknown parameters. Called BEFORE Nivora's own state-token
    /// validation, so a structurally tampered callback can never influence
    /// which state value gets validated. Returns false to fail closed;
    /// providers with no such contract (e.g. the test-only fake) simply
    /// return true.
    /// </summary>
    bool ValidateCallbackStructure(IQueryCollection query);

    /// <summary>
    /// Step 19 addition. Validates provider-specific callback authenticity
    /// beyond Nivora's own state-token/live-cookie checks (e.g. Shopify's
    /// HMAC contract), and — only once that authenticity check succeeds —
    /// re-validates the resulting external account identifier's syntax
    /// before returning it. Returns null to fail closed on either check.
    /// Must only be called after ValidateCallbackStructure has already
    /// returned true for the same query. The returned value (non-null) is
    /// what IntegrationsController.Callback passes to ExchangeCodeAsync.
    /// </summary>
    string? ValidateCallbackAuthenticity(IQueryCollection query);

    /// <summary>
    /// <paramref name="externalAccountIdentifier"/> is the value
    /// ValidateCallbackAuthenticity already verified — not a controller
    /// request/context object, same Services→Controllers-dependency
    /// reasoning as BuildAuthorizationUrl. Required because some providers'
    /// token endpoints are per-account (Shopify's is per-shop:
    /// https://{shop}.myshopify.com/admin/oauth/access_token), not a single
    /// fixed, provider-wide URL.
    /// </summary>
    Task<OAuthTokenResult> ExchangeCodeAsync(string code, string externalAccountIdentifier, CancellationToken cancellationToken);
}
