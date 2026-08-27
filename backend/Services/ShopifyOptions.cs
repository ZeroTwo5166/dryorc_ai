namespace backend.Services;

/// <summary>
/// Shopify app credentials + the OAuth redirect URI Shopify calls back to.
/// Same secret-handling rule as IntegrationStateToken:SigningKey / the DB
/// connection string: ClientSecret is never in appsettings.json, set via
/// `dotnet user-secrets set "Shopify:ClientSecret" "..."` (Development) or
/// the hosting platform's configuration (production).
///
/// RedirectUri is a stable, explicitly configured value — deliberately not
/// derived from the current request's Host header, same reasoning as
/// IntegrationsController.BuildSettingsRedirectUrl using the configured
/// Frontend:BaseUrl instead of Request.Host — and must exactly match the
/// redirect URL registered for this app in Shopify's Partner Dashboard;
/// Shopify itself rejects an authorization request whose redirect_uri isn't
/// on that allowlist, which is the actual backstop against a spoofed value
/// here, not Nivora's own trust of the request.
/// </summary>
public class ShopifyOptions
{
    public const string SectionName = "Shopify";

    public required string ClientId { get; set; }
    public required string ClientSecret { get; set; }
    public required string RedirectUri { get; set; }

    /// <summary>
    /// Step 20. The Shopify Admin API version ShopifyOrderLookupService
    /// targets (e.g. "2026-07"), used to build the GraphQL endpoint URL.
    /// Configuration-driven, never hardcoded in application logic — verify
    /// the current stable version directly against Shopify (Partner
    /// Dashboard or a live X-Shopify-API-Version response header) before
    /// changing this value, rather than guessing.
    /// </summary>
    public required string AdminApiVersion { get; set; }
}
