using System.Net;
using System.Security.Cryptography;
using System.Text;
using backend.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace backend.Tests;

public class ShopifyIntegrationOAuthProviderTests
{
    private const string ClientId = "test-client-id";
    private const string ClientSecret = "test-client-secret-value";
    private const string RedirectUri = "https://backend.example.com/api/integrations/shopify/callback";

    private static ShopifyIntegrationOAuthProvider NewProvider(HttpMessageHandler? handler = null) =>
        new(
            new HttpClient(handler ?? new FakeShopifyHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))),
            Options.Create(new ShopifyOptions { ClientId = ClientId, ClientSecret = ClientSecret, RedirectUri = RedirectUri }));

    // Independently implements Shopify's documented HMAC procedure — never
    // calls into ShopifyIntegrationOAuthProvider's own HMAC code — so these
    // tests prove correctness against an independent calculation, not just
    // self-consistency.
    private static string ComputeExpectedHmacHex(
        string code, string shop, string state, string timestamp, string secret = ClientSecret)
    {
        var canonical = string.Join('&', new[]
        {
            $"code={code}", $"shop={shop}", $"state={state}", $"timestamp={timestamp}",
        }.OrderBy(s => s, StringComparer.Ordinal));

        var bytes = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static IQueryCollection BuildQuery(Dictionary<string, StringValues> values) => new QueryCollection(values);

    private static IQueryCollection ValidQuery(
        string shop = "my-shop", string code = "auth-code", string state = "state-value", string timestamp = "1700000000") =>
        BuildQuery(new Dictionary<string, StringValues>
        {
            ["code"] = code,
            ["hmac"] = ComputeExpectedHmacHex(code, shop, state, timestamp),
            ["shop"] = shop,
            ["state"] = state,
            ["timestamp"] = timestamp,
        });

    private static string SuccessBody() =>
        """{ "access_token": "real-access-token", "refresh_token": "real-refresh-token", "expires_in": 3600, "scope": "read_orders,write_orders" }""";

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    // ── Shop validation ──────────────────────────────────────────────

    [Theory]
    [InlineData("my-shop")]
    [InlineData("shop123")]
    [InlineData("a")]
    public void BuildAuthorizationUrl_AcceptsValidBareShopNames(string shop)
    {
        var url = NewProvider().BuildAuthorizationUrl("state", shop);
        Assert.StartsWith($"https://{shop}.myshopify.com/admin/oauth/authorize?", url);
    }

    [Theory]
    [InlineData("https://example-store.myshopify.com")]
    [InlineData("http://example-store.myshopify.com")]
    [InlineData("example-store.myshopify.com")]
    [InlineData("example-store/path")]
    [InlineData("example-store:8080")]
    [InlineData("example store")]
    [InlineData("example_store")]
    [InlineData("-example-store")]
    [InlineData("example-store-")]
    [InlineData("")]
    [InlineData("evil.com")]
    public void BuildAuthorizationUrl_RejectsInvalidShopNames(string shop)
    {
        Assert.Throws<ArgumentException>(() => NewProvider().BuildAuthorizationUrl("state", shop));
    }

    // ── URL construction ─────────────────────────────────────────────

    [Fact]
    public void BuildAuthorizationUrl_HardcodesMyshopifyHostAndRequiredParams()
    {
        var url = NewProvider().BuildAuthorizationUrl("the-state", "my-shop");

        Assert.StartsWith("https://my-shop.myshopify.com/admin/oauth/authorize?", url);
        Assert.Contains($"client_id={Uri.EscapeDataString(ClientId)}", url);
        Assert.Contains("expiring=1", url);
        Assert.Contains($"state={Uri.EscapeDataString("the-state")}", url);
        Assert.Contains($"redirect_uri={Uri.EscapeDataString(RedirectUri)}", url);
        foreach (var scope in ShopifyIntegrationOAuthProvider.RequiredScopes)
        {
            Assert.Contains(Uri.EscapeDataString(scope), url);
        }
    }

    [Fact]
    public async Task ExchangeCodeAsync_PostsToShopSpecificMyshopifyTokenEndpoint_NoAttackerControlledHost()
    {
        var handler = new FakeShopifyHttpMessageHandler(_ => JsonResponse(SuccessBody()));
        var provider = NewProvider(handler);

        await provider.ExchangeCodeAsync("auth-code", "attacker-cannot-escape-shop", CancellationToken.None);

        Assert.NotNull(handler.LastRequest);
        Assert.Equal(
            "https://attacker-cannot-escape-shop.myshopify.com/admin/oauth/access_token",
            handler.LastRequest!.RequestUri!.ToString());
    }

    // ── HMAC ─────────────────────────────────────────────────────────

    [Fact]
    public void ValidateCallbackAuthenticity_AcceptsValidHmac_IndependentlyComputed()
    {
        var provider = NewProvider();
        var query = ValidQuery(shop: "my-shop");

        Assert.True(provider.ValidateCallbackStructure(query));
        Assert.Equal("my-shop", provider.ValidateCallbackAuthenticity(query));
    }

    [Fact]
    public void ValidateCallbackAuthenticity_RejectsTamperedHmac()
    {
        var query = BuildQuery(new Dictionary<string, StringValues>
        {
            ["code"] = "auth-code",
            ["hmac"] = new string('a', 64), // well-formed hex, wrong value
            ["shop"] = "my-shop",
            ["state"] = "state-value",
            ["timestamp"] = "1700000000",
        });

        var provider = NewProvider();
        Assert.True(provider.ValidateCallbackStructure(query));
        Assert.Null(provider.ValidateCallbackAuthenticity(query));
    }

    [Fact]
    public void ValidateCallbackAuthenticity_RejectsTamperedShop()
    {
        var hmac = ComputeExpectedHmacHex("auth-code", "my-shop", "state-value", "1700000000");
        var query = BuildQuery(new Dictionary<string, StringValues>
        {
            ["code"] = "auth-code",
            ["hmac"] = hmac,
            ["shop"] = "different-shop", // tampered after signing
            ["state"] = "state-value",
            ["timestamp"] = "1700000000",
        });

        var provider = NewProvider();
        Assert.True(provider.ValidateCallbackStructure(query));
        Assert.Null(provider.ValidateCallbackAuthenticity(query));
    }

    [Fact]
    public void ValidateCallbackAuthenticity_RejectsTamperedTimestamp()
    {
        var hmac = ComputeExpectedHmacHex("auth-code", "my-shop", "state-value", "1700000000");
        var query = BuildQuery(new Dictionary<string, StringValues>
        {
            ["code"] = "auth-code",
            ["hmac"] = hmac,
            ["shop"] = "my-shop",
            ["state"] = "state-value",
            ["timestamp"] = "1700000001", // tampered after signing
        });

        var provider = NewProvider();
        Assert.True(provider.ValidateCallbackStructure(query));
        Assert.Null(provider.ValidateCallbackAuthenticity(query));
    }

    [Fact]
    public void ValidateCallbackAuthenticity_RejectsMalformedHmac_FailsClosedNoException()
    {
        var query = BuildQuery(new Dictionary<string, StringValues>
        {
            ["code"] = "auth-code",
            ["hmac"] = "not-hex-!!!",
            ["shop"] = "my-shop",
            ["state"] = "state-value",
            ["timestamp"] = "1700000000",
        });

        var provider = NewProvider();
        Assert.True(provider.ValidateCallbackStructure(query));
        Assert.Null(provider.ValidateCallbackAuthenticity(query));
    }

    [Fact]
    public void ValidateCallbackAuthenticity_RejectsShopThatFailsPostHmacSyntaxCheck()
    {
        // Even a shop value that legitimately signs correctly (the secret is
        // known here, in-test) but fails bare-shop-name syntax must still be
        // rejected — proves the post-HMAC syntax gate runs independently of
        // HMAC success.
        var hmac = ComputeExpectedHmacHex("auth-code", "not a shop", "state-value", "1700000000");
        var query = BuildQuery(new Dictionary<string, StringValues>
        {
            ["code"] = "auth-code",
            ["hmac"] = hmac,
            ["shop"] = "not a shop",
            ["state"] = "state-value",
            ["timestamp"] = "1700000000",
        });

        var provider = NewProvider();
        Assert.True(provider.ValidateCallbackStructure(query));
        Assert.Null(provider.ValidateCallbackAuthenticity(query));
    }

    [Theory]
    [InlineData("code")]
    [InlineData("hmac")]
    [InlineData("shop")]
    [InlineData("state")]
    [InlineData("timestamp")]
    public void ValidateCallbackStructure_RejectsMissingRequiredParameter(string missing)
    {
        var values = new Dictionary<string, StringValues>
        {
            ["code"] = "auth-code",
            ["hmac"] = ComputeExpectedHmacHex("auth-code", "my-shop", "state-value", "1700000000"),
            ["shop"] = "my-shop",
            ["state"] = "state-value",
            ["timestamp"] = "1700000000",
        };
        values.Remove(missing);

        Assert.False(NewProvider().ValidateCallbackStructure(BuildQuery(values)));
    }

    [Theory]
    [InlineData("code")]
    [InlineData("hmac")]
    [InlineData("shop")]
    [InlineData("state")]
    [InlineData("timestamp")]
    public void ValidateCallbackStructure_RejectsDuplicatedRequiredParameter(string duplicated)
    {
        var values = new Dictionary<string, StringValues>
        {
            ["code"] = "auth-code",
            ["hmac"] = ComputeExpectedHmacHex("auth-code", "my-shop", "state-value", "1700000000"),
            ["shop"] = "my-shop",
            ["state"] = "state-value",
            ["timestamp"] = "1700000000",
        };
        values[duplicated] = new StringValues(new[] { values[duplicated].ToString(), "second-value" });

        Assert.False(NewProvider().ValidateCallbackStructure(BuildQuery(values)));
    }

    [Fact]
    public void ValidateCallbackStructure_RejectsUnknownParameter()
    {
        var values = new Dictionary<string, StringValues>
        {
            ["code"] = "auth-code",
            ["hmac"] = ComputeExpectedHmacHex("auth-code", "my-shop", "state-value", "1700000000"),
            ["shop"] = "my-shop",
            ["state"] = "state-value",
            ["timestamp"] = "1700000000",
            ["locale"] = "en", // a real Shopify param in some flows, but not one this contract allows
        };

        Assert.False(NewProvider().ValidateCallbackStructure(BuildQuery(values)));
    }

    [Fact]
    public void ValidateCallbackStructure_RejectsLegacySignatureParameterReplacingHmac()
    {
        var values = new Dictionary<string, StringValues>
        {
            ["code"] = "auth-code",
            ["shop"] = "my-shop",
            ["state"] = "state-value",
            ["timestamp"] = "1700000000",
            ["signature"] = "some-legacy-signature",
        };

        Assert.False(NewProvider().ValidateCallbackStructure(BuildQuery(values)));
    }

    [Fact]
    public void ValidateCallbackStructure_RejectsSignatureAlongsideHmac()
    {
        var values = new Dictionary<string, StringValues>
        {
            ["code"] = "auth-code",
            ["hmac"] = ComputeExpectedHmacHex("auth-code", "my-shop", "state-value", "1700000000"),
            ["shop"] = "my-shop",
            ["state"] = "state-value",
            ["timestamp"] = "1700000000",
            ["signature"] = "some-legacy-signature",
        };

        Assert.False(NewProvider().ValidateCallbackStructure(BuildQuery(values)));
    }

    /// <summary>
    /// Special-character HMAC test (project-mandated — see
    /// ShopifyIntegrationOAuthProvider's class doc comment). Builds a raw,
    /// URL-encoded query string whose `state` value contains '&amp;', '=',
    /// '+', and '%' after decoding, parses it with
    /// QueryHelpers.ParseQuery — the same decoding real ASP.NET Core
    /// Request.Query performs — and confirms an independently computed
    /// expected HMAC (over the decoded values) is accepted. This proves the
    /// chosen decoded-query canonicalization survives a realistic
    /// encode/decode round trip without corruption. It does NOT, and cannot
    /// without a live Shopify-signed capture, prove decoded values are what
    /// Shopify's own server computed over — that remains the documented
    /// risk acceptance. Per the project's critical-failure rule: if this
    /// test ever fails, that is evidence the risk-accepted assumption was
    /// wrong — do not weaken or remove it to force a pass.
    /// </summary>
    [Fact]
    public void ValidateCallbackAuthenticity_HandlesRealisticUrlEncodedSpecialCharactersInState()
    {
        const string decodedState = "abc&def=ghi+jkl%mno";
        const string shop = "my-shop";
        const string code = "auth-code";
        const string timestamp = "1700000000";

        var expectedHmac = ComputeExpectedHmacHex(code, shop, decodedState, timestamp);

        var rawQueryString =
            $"code={Uri.EscapeDataString(code)}" +
            $"&hmac={expectedHmac}" +
            $"&shop={Uri.EscapeDataString(shop)}" +
            $"&state={Uri.EscapeDataString(decodedState)}" +
            $"&timestamp={Uri.EscapeDataString(timestamp)}";

        var parsed = QueryHelpers.ParseQuery(rawQueryString);
        var query = BuildQuery(parsed);

        Assert.Equal(decodedState, query["state"].ToString()); // sanity: decoding actually happened

        var provider = NewProvider();
        Assert.True(provider.ValidateCallbackStructure(query));
        Assert.Equal(shop, provider.ValidateCallbackAuthenticity(query));
    }

    // ── Token exchange request ───────────────────────────────────────

    [Fact]
    public async Task ExchangeCodeAsync_SendsRequiredFieldsIncludingExpiring()
    {
        var handler = new FakeShopifyHttpMessageHandler(_ => JsonResponse(SuccessBody()));
        var provider = NewProvider(handler);

        await provider.ExchangeCodeAsync("the-auth-code", "my-shop", CancellationToken.None);

        Assert.NotNull(handler.LastRequestBody);
        var form = QueryHelpers.ParseQuery(handler.LastRequestBody!);
        Assert.Equal(ClientId, form["client_id"].ToString());
        Assert.Equal(ClientSecret, form["client_secret"].ToString());
        Assert.Equal("the-auth-code", form["code"].ToString());
        Assert.Equal("1", form["expiring"].ToString());
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("https://my-shop.myshopify.com/admin/oauth/access_token", handler.LastRequest.RequestUri!.ToString());
    }

    // ── Response validation ───────────────────────────────────────────

    [Fact]
    public async Task ExchangeCodeAsync_Throws_OnMissingAccessToken()
    {
        var provider = NewProvider(new FakeShopifyHttpMessageHandler(_ =>
            JsonResponse("""{ "scope": "read_orders", "expires_in": 3600, "refresh_token": "r" }""")));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.ExchangeCodeAsync("code", "my-shop", CancellationToken.None));
    }

    [Fact]
    public async Task ExchangeCodeAsync_Throws_OnMissingExpiresIn()
    {
        var provider = NewProvider(new FakeShopifyHttpMessageHandler(_ =>
            JsonResponse("""{ "access_token": "tok", "scope": "read_orders", "refresh_token": "r" }""")));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.ExchangeCodeAsync("code", "my-shop", CancellationToken.None));
    }

    [Fact]
    public async Task ExchangeCodeAsync_Throws_OnMissingRefreshToken()
    {
        var provider = NewProvider(new FakeShopifyHttpMessageHandler(_ =>
            JsonResponse("""{ "access_token": "tok", "scope": "read_orders", "expires_in": 3600 }""")));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.ExchangeCodeAsync("code", "my-shop", CancellationToken.None));
    }

    [Fact]
    public async Task ExchangeCodeAsync_Throws_OnMalformedJson()
    {
        var provider = NewProvider(new FakeShopifyHttpMessageHandler(_ => JsonResponse("this is not json")));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.ExchangeCodeAsync("code", "my-shop", CancellationToken.None));
    }

    [Fact]
    public async Task ExchangeCodeAsync_Throws_OnNon2xxResponse()
    {
        var provider = NewProvider(new FakeShopifyHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("""{ "error": "invalid_request" }""", Encoding.UTF8, "application/json"),
            }));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.ExchangeCodeAsync("code", "my-shop", CancellationToken.None));
    }

    [Fact]
    public async Task ExchangeCodeAsync_MapsSuccessfulResponse()
    {
        var provider = NewProvider(new FakeShopifyHttpMessageHandler(_ => JsonResponse(SuccessBody())));

        var result = await provider.ExchangeCodeAsync("code", "my-shop", CancellationToken.None);

        Assert.Equal("real-access-token", result.AccessToken);
        Assert.Equal("real-refresh-token", result.RefreshToken);
        Assert.Equal("my-shop", result.ExternalAccountIdentifier);
        Assert.Equal(["read_orders", "write_orders"], result.Scopes);
        Assert.NotNull(result.ExpiresAt);
        Assert.True(result.ExpiresAt > DateTimeOffset.UtcNow);
    }

    // ── Secret/token non-disclosure ───────────────────────────────────

    [Fact]
    public async Task ExchangeCodeAsync_FailureMessage_NeverContainsClientSecret()
    {
        var provider = NewProvider(new FakeShopifyHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("server exploded", Encoding.UTF8, "text/plain"),
            }));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.ExchangeCodeAsync("the-auth-code", "my-shop", CancellationToken.None));

        Assert.DoesNotContain(ClientSecret, ex.Message);
        Assert.DoesNotContain(ClientSecret, ex.ToString());
    }
}
