using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using backend.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace backend.Services;

/// <summary>
/// Step 20. See IShopifyOrderLookupService for the contract, security
/// posture, and the deliberate 60-day/read_all_orders limitation.
///
/// IgnoreQueryFilters() + explicit BusinessId, same non-HTTP-safe posture
/// as ConversationModeService/WidgetConversationService — this service has
/// no HTTP-request-scoped caller guaranteeing the right tenant context.
///
/// Uses the Admin GraphQL API (order(id: ID!)), not REST — Nivora's Shopify
/// app is a public/distributed app, and Shopify's current documentation
/// requires new public apps to use GraphQL exclusively. AdminApiVersion is
/// configuration-driven (ShopifyOptions), never hardcoded here.
/// </summary>
public class ShopifyOrderLookupService(
    HttpClient httpClient,
    AppDbContext db,
    IIntegrationTokenProtector tokenProtector,
    IOptions<ShopifyOptions> options) : IShopifyOrderLookupService
{
    private const string OrderOwnerQuery = """
        query OrderOwnerLookup($id: ID!) {
          order(id: $id) {
            id
            customer {
              id
            }
          }
        }
        """;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<VerifiedOrderOwnership> LookupOrderOwnerAsync(
        Guid businessId, string externalOrderId, CancellationToken cancellationToken)
    {
        var integration = await db.Integrations
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                i => i.BusinessId == businessId
                    && i.Platform == ShopifyIntegrationOAuthProvider.PlatformName
                    && i.IsActive,
                cancellationToken);

        if (integration is null)
        {
            throw new InvalidOperationException(
                $"No active Shopify integration found for business {businessId}.");
        }

        if (string.IsNullOrEmpty(integration.ExternalAccountIdentifier))
        {
            throw new InvalidOperationException(
                $"Shopify integration for business {businessId} has no shop (ExternalAccountIdentifier) recorded.");
        }

        string accessToken;
        try
        {
            accessToken = tokenProtector.Unprotect(
                ShopifyIntegrationOAuthProvider.PlatformName, integration.EncryptedAccessToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"Failed to decrypt the Shopify access token for business {businessId}.", ex);
        }

        var shop = integration.ExternalAccountIdentifier;
        var requestUri = $"https://{shop}.myshopify.com/admin/api/{options.Value.AdminApiVersion}/graphql.json";
        var requestJson = JsonSerializer.Serialize(
            new { query = OrderOwnerQuery, variables = new { id = externalOrderId } });

        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = new StringContent(requestJson, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Shopify-Access-Token", accessToken);

        using var response = await httpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Shopify order lookup returned a non-success status ({(int)response.StatusCode}).");
        }

        ShopifyOrderLookupResponse? body;
        try
        {
            body = await response.Content.ReadFromJsonAsync<ShopifyOrderLookupResponse>(JsonOptions, cancellationToken);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Shopify order lookup returned malformed JSON.", ex);
        }

        if (body is null)
        {
            throw new InvalidOperationException("Shopify order lookup returned an empty response body.");
        }

        if (body.Errors is { Count: > 0 })
        {
            var messages = string.Join("; ", body.Errors.Select(e => e.Message));
            throw new InvalidOperationException($"Shopify order lookup returned GraphQL errors: {messages}");
        }

        var order = body.Data?.Order;
        if (order is null)
        {
            throw new InvalidOperationException(
                $"Shopify reported no order for id '{externalOrderId}' — either it does not exist, or it is " +
                "outside Shopify's default 60-day accessible window (this deployment does not request " +
                "read_all_orders, so the two cases cannot be distinguished).");
        }

        return new VerifiedOrderOwnership(order.Id, order.Customer?.Id);
    }

    private const string OrderCancellationStateQuery = """
        query OrderCancellationStateLookup($id: ID!) {
          order(id: $id) {
            id
            cancelledAt
          }
        }
        """;

    /// <summary>
    /// New method, added alongside LookupOrderOwnerAsync without modifying
    /// it — its own private response DTOs and its own inline credential
    /// resolution (deliberately not factored out into a shared helper this
    /// pass, to avoid touching LookupOrderOwnerAsync's already-audited,
    /// already-tested body). See IShopifyOrderLookupService for the
    /// rationale (Order.cancelledAt as reconciliation ground truth).
    /// </summary>
    public async Task<ShopifyOrderCancellationState> GetCancellationStateAsync(
        Guid businessId, string externalOrderId, CancellationToken cancellationToken)
    {
        var integration = await db.Integrations
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                i => i.BusinessId == businessId
                    && i.Platform == ShopifyIntegrationOAuthProvider.PlatformName
                    && i.IsActive,
                cancellationToken);

        if (integration is null)
        {
            throw new InvalidOperationException(
                $"No active Shopify integration found for business {businessId}.");
        }

        if (string.IsNullOrEmpty(integration.ExternalAccountIdentifier))
        {
            throw new InvalidOperationException(
                $"Shopify integration for business {businessId} has no shop (ExternalAccountIdentifier) recorded.");
        }

        string accessToken;
        try
        {
            accessToken = tokenProtector.Unprotect(
                ShopifyIntegrationOAuthProvider.PlatformName, integration.EncryptedAccessToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"Failed to decrypt the Shopify access token for business {businessId}.", ex);
        }

        var shop = integration.ExternalAccountIdentifier;
        var requestUri = $"https://{shop}.myshopify.com/admin/api/{options.Value.AdminApiVersion}/graphql.json";
        var requestJson = JsonSerializer.Serialize(
            new { query = OrderCancellationStateQuery, variables = new { id = externalOrderId } });

        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = new StringContent(requestJson, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Shopify-Access-Token", accessToken);

        using var response = await httpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Shopify order cancellation-state lookup returned a non-success status ({(int)response.StatusCode}).");
        }

        ShopifyOrderCancellationStateResponse? body;
        try
        {
            body = await response.Content.ReadFromJsonAsync<ShopifyOrderCancellationStateResponse>(JsonOptions, cancellationToken);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Shopify order cancellation-state lookup returned malformed JSON.", ex);
        }

        if (body is null)
        {
            throw new InvalidOperationException("Shopify order cancellation-state lookup returned an empty response body.");
        }

        if (body.Errors is { Count: > 0 })
        {
            var messages = string.Join("; ", body.Errors.Select(e => e.Message));
            throw new InvalidOperationException($"Shopify order cancellation-state lookup returned GraphQL errors: {messages}");
        }

        var order = body.Data?.Order;
        if (order is null)
        {
            throw new InvalidOperationException(
                $"Shopify reported no order for id '{externalOrderId}' during cancellation-state reconciliation.");
        }

        return new ShopifyOrderCancellationState(order.Id, order.CancelledAt);
    }

    private sealed record ShopifyOrderCancellationStateResponse(
        [property: JsonPropertyName("data")] ShopifyOrderCancellationStateData? Data,
        [property: JsonPropertyName("errors")] List<ShopifyGraphQlError>? Errors);

    private sealed record ShopifyOrderCancellationStateData(
        [property: JsonPropertyName("order")] ShopifyOrderCancellationStateOrderData? Order);

    private sealed record ShopifyOrderCancellationStateOrderData(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("cancelledAt")] DateTimeOffset? CancelledAt);

    private sealed record ShopifyOrderLookupResponse(
        [property: JsonPropertyName("data")] ShopifyOrderLookupData? Data,
        [property: JsonPropertyName("errors")] List<ShopifyGraphQlError>? Errors);

    private sealed record ShopifyOrderLookupData(
        [property: JsonPropertyName("order")] ShopifyOrderData? Order);

    private sealed record ShopifyOrderData(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("customer")] ShopifyCustomerData? Customer);

    private sealed record ShopifyCustomerData(
        [property: JsonPropertyName("id")] string Id);

    private sealed record ShopifyGraphQlError(
        [property: JsonPropertyName("message")] string Message);
}
