using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace backend.Services;

/// <summary>
/// Step 19 — Shopify's IIntegrationOAuthProvider implementation. Read this
/// comment in full before touching HMAC/shop-validation logic here; this is
/// the security-critical surface of the whole integration.
///
/// PROJECT DECISION (not a Shopify documentation fact): Nivora's Shopify app
/// is a public/distributed app — one Nivora application installed by many
/// unrelated merchant businesses, not a custom app built for a single
/// merchant. Shopify's current documentation
/// (https://shopify.dev/docs/apps/build/authentication-authorization/access-tokens/offline-access-tokens.md)
/// states: "Use expiring offline access tokens for public apps" and "This
/// requirement doesn't apply to custom apps or apps created by merchants" —
/// so this decision is what makes the expiring-token flow (expiring=1,
/// 1-hour access tokens, 90-day refresh tokens per the same page) the
/// correct model for Nivora, not something Shopify's docs could determine on
/// their own. Step 19 captures the refresh token on every exchange; it does
/// NOT implement refresh execution/scheduling — see ExchangeCodeAsync.
///
/// HMAC CANONICALIZATION — EXPLICIT PROJECT RISK ACCEPTANCE, not a
/// documentation-confirmed fact: Shopify's current documentation
/// (https://shopify.dev/docs/apps/build/authentication-authorization/access-tokens/authorization-code-grant)
/// states the general procedure — "Remove the hmac parameter from the query
/// string, sort the remaining parameters alphabetically, and compute an
/// HMAC-SHA256 hash using your client secret" — and its own reference
/// implementation computes this over an already-URL-decoded parameter map
/// (e.g. Node's req.query). It does NOT state, anywhere found during
/// verification, whether the raw/still-percent-encoded query bytes or the
/// decoded values are what Shopify's own server-side computation actually
/// used, nor does it define an escaping rule for a literal '&' or '=' inside
/// a parameter's own value. This class uses ASP.NET Core's decoded
/// Request.Query — consistent with Shopify's own reference pattern — as a
/// deliberate, explicitly labeled risk acceptance, not a Shopify-confirmed
/// fact. ShopifyIntegrationOAuthProviderTests' special-character HMAC test
/// exercises a realistic URL-encoded `state` value through this exact code
/// path; if real-world Shopify callbacks ever demonstrate incompatibility,
/// that is evidence this assumption was wrong and must be revisited — never
/// paper over such a failure by weakening the test.
/// </summary>
public class ShopifyIntegrationOAuthProvider(HttpClient httpClient, IOptions<ShopifyOptions> options) : IIntegrationOAuthProvider
{
    public const string PlatformName = "shopify";

    /// <summary>
    /// Requested in anticipation of Step 20's already-designed functionality
    /// so a merchant's Step 19 connect grants everything Step 20 needs
    /// without re-authorization. Spot-verified against
    /// https://shopify.dev/docs/api/usage/access-scopes as currently valid
    /// scope names during Step 19 verification (write_customers was not
    /// independently re-quoted in that pass; no evidence found that it is
    /// invalid).
    /// </summary>
    public static readonly string[] RequiredScopes =
    [
        "read_orders", "write_orders", "read_customers", "write_customers", "read_products", "read_inventory",
    ];

    private static readonly string[] RequiredCallbackParameters = ["code", "hmac", "shop", "state", "timestamp"];

    /// <summary>
    /// Shopify shop names: letters/digits/hyphens, no leading/trailing
    /// hyphen, no scheme/path/port/whitespace/dots — deliberately strict
    /// (see IntegrationsController §4/§10 shop-validation contract). Applied
    /// identically at connect time (BuildAuthorizationUrl) and, after HMAC
    /// success, at callback time (ValidateCallbackAuthenticity) — the same
    /// rule both places, per the SSRF-boundary requirement that a
    /// HMAC-verified shop still gets independently syntax-checked before
    /// being used to build an outbound URL.
    /// </summary>
    private static readonly Regex ShopNameRegex = new(
        @"^[a-zA-Z0-9]([a-zA-Z0-9-]{0,98}[a-zA-Z0-9])?$", RegexOptions.Compiled);

    public string Platform => PlatformName;

    public string BuildAuthorizationUrl(string state, string externalAccountIdentifier)
    {
        var shop = externalAccountIdentifier;
        if (!ShopNameRegex.IsMatch(shop))
        {
            throw new ArgumentException(
                "Shopify shop must be a bare shop name (e.g. \"example-store\"), not a URL/hostname.",
                nameof(externalAccountIdentifier));
        }

        var scope = string.Join(',', RequiredScopes);
        var query = string.Join('&',
            $"client_id={Uri.EscapeDataString(options.Value.ClientId)}",
            $"scope={Uri.EscapeDataString(scope)}",
            $"redirect_uri={Uri.EscapeDataString(options.Value.RedirectUri)}",
            $"state={Uri.EscapeDataString(state)}",
            "expiring=1");

        return $"https://{shop}.myshopify.com/admin/oauth/authorize?{query}";
    }

    /// <summary>
    /// Exactly the five required parameters, each exactly once, no others —
    /// a strict allowlist, not "known parameters plus tolerate extras".
    /// Checking query.Count == RequiredCallbackParameters.Length together
    /// with "every required name present exactly once" is sufficient to
    /// also rule out any unknown parameter name (including the legacy,
    /// unsupported `signature` parameter, which current Shopify
    /// documentation does not mention at all) — by pigeonhole, five keys all
    /// matching the five required names leaves no room for a sixth,
    /// differently-named key.
    /// </summary>
    public bool ValidateCallbackStructure(IQueryCollection query)
    {
        if (query.Count != RequiredCallbackParameters.Length) return false;

        foreach (var name in RequiredCallbackParameters)
        {
            if (!query.TryGetValue(name, out var values)) return false;
            if (values.Count != 1) return false;
            if (string.IsNullOrEmpty(values[0])) return false;
        }

        return true;
    }

    /// <summary>
    /// Must only be called after ValidateCallbackStructure has returned true
    /// for the same query (so code/shop/state/timestamp/hmac are each known
    /// to be present exactly once). Excludes hmac from the canonicalized
    /// input, sorts the remaining four parameters alphabetically by name,
    /// joins as key=value pairs, computes HMAC-SHA256 with
    /// Shopify:ClientSecret, and compares using a constant-time comparison.
    /// A malformed hmac value (wrong length/non-hex) fails the same closed
    /// path as a mismatch, never an unhandled exception. Only once the HMAC
    /// check succeeds is `shop` syntax-revalidated and returned — this
    /// ordering (HMAC first, then shop syntax) is the SSRF boundary from
    /// IntegrationsController §4.2/§10.
    /// </summary>
    public string? ValidateCallbackAuthenticity(IQueryCollection query)
    {
        var code = query["code"].ToString();
        var shop = query["shop"].ToString();
        var state = query["state"].ToString();
        var timestamp = query["timestamp"].ToString();
        var receivedHmac = query["hmac"].ToString();

        var canonical = string.Join('&',
            new (string Name, string Value)[]
            {
                ("code", code), ("shop", shop), ("state", state), ("timestamp", timestamp),
            }
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => $"{p.Name}={p.Value}"));

        var expectedBytes = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(options.Value.ClientSecret),
            Encoding.UTF8.GetBytes(canonical));

        if (!TryDecodeHex(receivedHmac, out var receivedBytes))
        {
            return null;
        }

        if (receivedBytes.Length != expectedBytes.Length ||
            !CryptographicOperations.FixedTimeEquals(expectedBytes, receivedBytes))
        {
            return null;
        }

        return ShopNameRegex.IsMatch(shop) ? shop : null;
    }

    /// <summary>
    /// externalAccountIdentifier here is the value ValidateCallbackAuthenticity
    /// already verified (HMAC-authentic + syntax-revalidated shop) — never
    /// the raw, unverified callback query value. Requests the public-app
    /// expiring-token flow (expiring=1). Per the project's expiring-token
    /// decision, a response missing expires_in or refresh_token is an
    /// anomaly, not a degraded-but-acceptable success — both throw, which
    /// IntegrationsController.Callback maps to the existing
    /// integrationError=exchange_failed path with no partial persistence.
    /// </summary>
    public async Task<OAuthTokenResult> ExchangeCodeAsync(
        string code, string externalAccountIdentifier, CancellationToken cancellationToken)
    {
        var shop = externalAccountIdentifier;

        var requestBody = new Dictionary<string, string>
        {
            ["client_id"] = options.Value.ClientId,
            ["client_secret"] = options.Value.ClientSecret,
            ["code"] = code,
            ["expiring"] = "1",
        };

        using var response = await httpClient.PostAsync(
            $"https://{shop}.myshopify.com/admin/oauth/access_token",
            new FormUrlEncodedContent(requestBody),
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Shopify token exchange returned a non-success status ({(int)response.StatusCode}).");
        }

        ShopifyTokenResponse? body;
        try
        {
            body = await response.Content.ReadFromJsonAsync<ShopifyTokenResponse>(
                JsonOptions, cancellationToken);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Shopify token exchange returned malformed JSON.", ex);
        }

        if (body is null || string.IsNullOrEmpty(body.AccessToken))
        {
            throw new InvalidOperationException("Shopify token exchange response had no usable access token.");
        }

        if (body.ExpiresIn is not (> 0))
        {
            throw new InvalidOperationException(
                "Shopify token exchange response had no expires_in for the expiring-token flow.");
        }

        if (string.IsNullOrEmpty(body.RefreshToken))
        {
            throw new InvalidOperationException(
                "Shopify token exchange response had no refresh_token for the expiring-token flow.");
        }

        var scopes = string.IsNullOrEmpty(body.Scope)
            ? []
            : body.Scope.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        return new OAuthTokenResult(
            AccessToken: body.AccessToken,
            RefreshToken: body.RefreshToken,
            ExternalAccountIdentifier: shop,
            Scopes: scopes,
            ExpiresAt: DateTimeOffset.UtcNow.AddSeconds(body.ExpiresIn.Value));
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static bool TryDecodeHex(string value, out byte[] bytes)
    {
        try
        {
            bytes = Convert.FromHexString(value);
            return true;
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }

    private sealed record ShopifyTokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("expires_in")] int? ExpiresIn,
        [property: JsonPropertyName("scope")] string? Scope);
}
