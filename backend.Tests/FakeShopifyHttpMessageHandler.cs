namespace backend.Tests;

/// <summary>
/// Routes ShopifyIntegrationOAuthProvider's outbound HttpClient calls to a
/// caller-supplied delegate instead of the network. Used both directly
/// (provider unit tests — see ShopifyIntegrationOAuthProviderTests) and via
/// IntegrationsApiTests' factory.WithWebHostBuilder override (end-to-end
/// tests through the real provider-selection path — see NivoraApiFactory's
/// doc comment for why that path must stay real).
/// </summary>
public class FakeShopifyHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public HttpRequestMessage? LastRequest { get; private set; }
    public string? LastRequestBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        LastRequestBody = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);
        return respond(request);
    }
}
