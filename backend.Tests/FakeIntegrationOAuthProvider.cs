using backend.Services;
using Microsoft.AspNetCore.Http;

namespace backend.Tests;

/// <summary>
/// Test-only IIntegrationOAuthProvider — proves IntegrationsController's
/// full connect -> callback -> exchange -> persist -> audit flow without any
/// real external provider or network call. Registered only in
/// NivoraApiFactory (the test host), never in Program.cs. Has no
/// callback-authenticity contract of its own (unlike Shopify), so
/// ValidateCallbackStructure/ValidateCallbackAuthenticity are trivial
/// pass-throughs. Tests that want to prove the "unregistered platform"
/// contract (connect/callback -> 404) target a platform string other than
/// PlatformName — as of Step 19, "shopify" IS registered for real
/// (ShopifyIntegrationOAuthProvider), so those tests use
/// UnregisteredPlatformName instead, which has no registered provider in
/// this test host either, matching production exactly.
/// </summary>
public class FakeIntegrationOAuthProvider : IIntegrationOAuthProvider
{
    public const string PlatformName = "fake";
    public const string ValidCode = "valid-test-code";
    public const string UnregisteredPlatformName = "not-a-registered-platform";

    public string Platform => PlatformName;

    public string BuildAuthorizationUrl(string state, string externalAccountIdentifier) =>
        $"https://fake-provider.example/authorize?state={Uri.EscapeDataString(state)}&account={Uri.EscapeDataString(externalAccountIdentifier)}";

    public bool ValidateCallbackStructure(IQueryCollection query) => true;

    public string? ValidateCallbackAuthenticity(IQueryCollection query) => "fake-shop.example.com";

    public Task<OAuthTokenResult> ExchangeCodeAsync(
        string code, string externalAccountIdentifier, CancellationToken cancellationToken)
    {
        if (code != ValidCode)
        {
            throw new InvalidOperationException("Fake provider rejected an unrecognized code.");
        }

        return Task.FromResult(new OAuthTokenResult(
            AccessToken: "fake-access-token-" + Guid.NewGuid(),
            RefreshToken: null,
            ExternalAccountIdentifier: externalAccountIdentifier,
            Scopes: ["read_orders", "write_orders"],
            ExpiresAt: null));
    }
}
