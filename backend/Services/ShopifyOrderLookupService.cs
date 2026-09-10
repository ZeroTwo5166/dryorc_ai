using System.Net;
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

    /// <summary>
    /// Step getOrder (design §2.1a) — deliberate near-duplicate of
    /// LookupOrderOwnerAsync above: same OrderOwnerQuery text, same
    /// credential-resolution block, same VerifiedOrderOwnership return type.
    /// The only behavioral difference is the 401/403 branch immediately
    /// below, inserted before the existing blanket throw — every other
    /// failure path is byte-for-byte identical to LookupOrderOwnerAsync's
    /// own. See IShopifyOrderLookupService's doc comment on this method, and
    /// the design doc's §1.4, for why this is a new method rather than a
    /// modification to LookupOrderOwnerAsync itself.
    /// </summary>
    public async Task<VerifiedOrderOwnership> LookupOrderOwnerForReadToolAsync(
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
            // The one behavioral difference from LookupOrderOwnerAsync —
            // see this method's own doc comment.
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                await db.Integrations.IgnoreQueryFilters()
                    .Where(i => i.BusinessId == businessId && i.Platform == ShopifyIntegrationOAuthProvider.PlatformName)
                    .ExecuteUpdateAsync(s => s.SetProperty(i => i.ReauthorizationRequiredAt, DateTimeOffset.UtcNow), cancellationToken);
                throw new ShopifyAuthFailureException();
            }

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

    private const string OrderNameResolutionQuery = """
        query OrderNameResolution($query: String!) {
          orders(first: 2, query: $query) {
            edges {
              node {
                id
              }
            }
          }
        }
        """;

    /// <summary>
    /// Step getOrder (design §2.1). See IShopifyOrderLookupService's doc
    /// comment for the UNVERIFIED '#'-handling caveat — this sends the raw
    /// orderName value unmodified as "name:&lt;raw&gt;".
    /// </summary>
    public async Task<OrderNameResolutionResult> ResolveOrderNameAsync(
        Guid businessId, string orderName, CancellationToken cancellationToken)
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
            new { query = OrderNameResolutionQuery, variables = new { query = $"name:{orderName}" } });

        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = new StringContent(requestJson, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Shopify-Access-Token", accessToken);

        using var response = await httpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                await db.Integrations.IgnoreQueryFilters()
                    .Where(i => i.BusinessId == businessId && i.Platform == ShopifyIntegrationOAuthProvider.PlatformName)
                    .ExecuteUpdateAsync(s => s.SetProperty(i => i.ReauthorizationRequiredAt, DateTimeOffset.UtcNow), cancellationToken);
                throw new ShopifyAuthFailureException();
            }

            throw new InvalidOperationException(
                $"Shopify order name resolution returned a non-success status ({(int)response.StatusCode}).");
        }

        ShopifyOrderNameResolutionResponse? body;
        try
        {
            body = await response.Content.ReadFromJsonAsync<ShopifyOrderNameResolutionResponse>(JsonOptions, cancellationToken);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Shopify order name resolution returned malformed JSON.", ex);
        }

        if (body is null)
        {
            throw new InvalidOperationException("Shopify order name resolution returned an empty response body.");
        }

        if (body.Errors is { Count: > 0 })
        {
            var messages = string.Join("; ", body.Errors.Select(e => e.Message));
            throw new InvalidOperationException($"Shopify order name resolution returned GraphQL errors: {messages}");
        }

        // Zero/one/multiple matches are ordinary, successful outcomes here —
        // never thrown. See this method's own doc comment and design §1.4.
        var edges = body.Data?.Orders?.Edges ?? [];
        return edges.Count switch
        {
            0 => new OrderNameResolutionResult(OrderNameResolutionOutcome.NotFound, null),
            1 => new OrderNameResolutionResult(OrderNameResolutionOutcome.Resolved, edges[0].Node.Id),
            _ => new OrderNameResolutionResult(OrderNameResolutionOutcome.Ambiguous, null),
        };
    }

    private const string OrderDetailQuery = """
        query OrderDetailLookup($id: ID!) {
          order(id: $id) {
            id
            name
            displayFinancialStatus
            displayFulfillmentStatus
            cancelledAt
            lineItems(first: 50) {
              edges {
                node {
                  title
                  quantity
                  discountedUnitPriceSet {
                    presentmentMoney {
                      amount
                    }
                  }
                }
              }
            }
            fulfillments(first: 10) {
              trackingInfo {
                number
                url
              }
            }
          }
        }
        """;

    /// <summary>
    /// Step getOrder (design §2.2). See IShopifyOrderLookupService's doc
    /// comment for the UNVERIFIED fulfillments/scope caveat.
    /// </summary>
    public async Task<OrderDetailResult> GetOrderDetailAsync(
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
            new { query = OrderDetailQuery, variables = new { id = externalOrderId } });

        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = new StringContent(requestJson, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Shopify-Access-Token", accessToken);

        using var response = await httpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                await db.Integrations.IgnoreQueryFilters()
                    .Where(i => i.BusinessId == businessId && i.Platform == ShopifyIntegrationOAuthProvider.PlatformName)
                    .ExecuteUpdateAsync(s => s.SetProperty(i => i.ReauthorizationRequiredAt, DateTimeOffset.UtcNow), cancellationToken);
                throw new ShopifyAuthFailureException();
            }

            throw new InvalidOperationException(
                $"Shopify order detail lookup returned a non-success status ({(int)response.StatusCode}).");
        }

        ShopifyOrderDetailResponse? body;
        try
        {
            body = await response.Content.ReadFromJsonAsync<ShopifyOrderDetailResponse>(JsonOptions, cancellationToken);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Shopify order detail lookup returned malformed JSON.", ex);
        }

        if (body is null)
        {
            throw new InvalidOperationException("Shopify order detail lookup returned an empty response body.");
        }

        if (body.Errors is { Count: > 0 })
        {
            var messages = string.Join("; ", body.Errors.Select(e => e.Message));
            throw new InvalidOperationException($"Shopify order detail lookup returned GraphQL errors: {messages}");
        }

        var order = body.Data?.Order;
        if (order is null)
        {
            // A genuine anomaly here, not an ordinary outcome — see this
            // method's own doc comment. Ownership was already confirmed
            // moments ago by the caller's own LookupOrderOwnerForReadToolAsync
            // call.
            throw new InvalidOperationException(
                $"Shopify reported no order for id '{externalOrderId}' during order-detail lookup, " +
                "immediately after ownership verification succeeded for the same id.");
        }

        var lineItems = (order.LineItems?.Edges ?? [])
            .Select(e => new OrderLineItemDetail(
                e.Node.Title, e.Node.Quantity, e.Node.DiscountedUnitPriceSet?.PresentmentMoney?.Amount))
            .ToList();

        var tracking = (order.Fulfillments ?? [])
            .SelectMany(f => f.TrackingInfo ?? [])
            .Select(t => new OrderTrackingInfo(t.Number, t.Url))
            .ToList();

        return new OrderDetailResult(
            order.Name, order.DisplayFinancialStatus, order.DisplayFulfillmentStatus,
            order.CancelledAt, lineItems, tracking);
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

    private sealed record ShopifyOrderNameResolutionResponse(
        [property: JsonPropertyName("data")] ShopifyOrderNameResolutionData? Data,
        [property: JsonPropertyName("errors")] List<ShopifyGraphQlError>? Errors);

    private sealed record ShopifyOrderNameResolutionData(
        [property: JsonPropertyName("orders")] ShopifyOrderNameResolutionConnection? Orders);

    private sealed record ShopifyOrderNameResolutionConnection(
        [property: JsonPropertyName("edges")] List<ShopifyOrderNameResolutionEdge> Edges);

    private sealed record ShopifyOrderNameResolutionEdge(
        [property: JsonPropertyName("node")] ShopifyOrderNameResolutionNode Node);

    private sealed record ShopifyOrderNameResolutionNode(
        [property: JsonPropertyName("id")] string Id);

    private sealed record ShopifyOrderDetailResponse(
        [property: JsonPropertyName("data")] ShopifyOrderDetailData? Data,
        [property: JsonPropertyName("errors")] List<ShopifyGraphQlError>? Errors);

    private sealed record ShopifyOrderDetailData(
        [property: JsonPropertyName("order")] ShopifyOrderDetailOrderData? Order);

    private sealed record ShopifyOrderDetailOrderData(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("displayFinancialStatus")] string? DisplayFinancialStatus,
        [property: JsonPropertyName("displayFulfillmentStatus")] string? DisplayFulfillmentStatus,
        [property: JsonPropertyName("cancelledAt")] DateTimeOffset? CancelledAt,
        [property: JsonPropertyName("lineItems")] ShopifyOrderDetailLineItemConnection? LineItems,
        [property: JsonPropertyName("fulfillments")] List<ShopifyOrderDetailFulfillment>? Fulfillments);

    private sealed record ShopifyOrderDetailLineItemConnection(
        [property: JsonPropertyName("edges")] List<ShopifyOrderDetailLineItemEdge> Edges);

    private sealed record ShopifyOrderDetailLineItemEdge(
        [property: JsonPropertyName("node")] ShopifyOrderDetailLineItemNode Node);

    private sealed record ShopifyOrderDetailLineItemNode(
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("quantity")] int Quantity,
        [property: JsonPropertyName("discountedUnitPriceSet")] ShopifyMoneyBag? DiscountedUnitPriceSet);

    private sealed record ShopifyMoneyBag(
        [property: JsonPropertyName("presentmentMoney")] ShopifyMoney? PresentmentMoney);

    private sealed record ShopifyMoney(
        [property: JsonPropertyName("amount")] string Amount);

    private sealed record ShopifyOrderDetailFulfillment(
        [property: JsonPropertyName("trackingInfo")] List<ShopifyOrderDetailTrackingInfo>? TrackingInfo);

    private sealed record ShopifyOrderDetailTrackingInfo(
        [property: JsonPropertyName("number")] string? Number,
        [property: JsonPropertyName("url")] string? Url);
}
