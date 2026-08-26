using backend.Services;

namespace backend.Tests;

/// <summary>
/// Test-only IIntegrationOAuthProvider — proves IntegrationsController's
/// full connect -> callback -> exchange -> persist -> audit flow without any
/// real external provider or network call. Registered only in
/// NivoraApiFactory (the test host), never in Program.cs — Step 18 ships
/// zero concrete production providers by design (see
/// IIntegrationOAuthProvider's own doc comment). Tests that want to prove
/// the "unregistered platform" contract (§ connect/callback -> 404) simply
/// target a platform string other than PlatformName, e.g. "shopify" — which
/// has no registered provider in this test host either, matching production
/// exactly.
/// </summary>
public class FakeIntegrationOAuthProvider : IIntegrationOAuthProvider
{
    public const string PlatformName = "fake";
    public const string ValidCode = "valid-test-code";

    public string Platform => PlatformName;

    public string BuildAuthorizationUrl(string state, string externalAccountIdentifier) =>
        $"https://fake-provider.example/authorize?state={Uri.EscapeDataString(state)}&account={Uri.EscapeDataString(externalAccountIdentifier)}";

    public Task<OAuthTokenResult> ExchangeCodeAsync(string code, CancellationToken cancellationToken)
    {
        if (code != ValidCode)
        {
            throw new InvalidOperationException("Fake provider rejected an unrecognized code.");
        }

        return Task.FromResult(new OAuthTokenResult(
            AccessToken: "fake-access-token-" + Guid.NewGuid(),
            RefreshToken: null,
            ExternalAccountIdentifier: "fake-shop.example.com",
            Scopes: ["read_orders", "write_orders"],
            ExpiresAt: null));
    }
}
