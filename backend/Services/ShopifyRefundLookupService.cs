using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using backend.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace backend.Services;

/// <summary>
/// See IShopifyRefundLookupService for the contract. IgnoreQueryFilters() +
/// explicit BusinessId, same non-HTTP-service posture as
/// ShopifyOrderLookupService/ShopifyOrderCancellationExecutor/RefundService —
/// this service has no HTTP-request-scoped caller guaranteeing the right
/// tenant context.
///
/// Credential resolution (Integration lookup + token decrypt) is deliberately
/// duplicated from ShopifyOrderLookupService rather than factored into a
/// shared helper, matching ShopifyOrderCancellationExecutor's own identical,
/// explicitly-stated decision not to touch that already-audited file's
/// existing method bodies/constructor.
///
/// One GraphQL call resolves both halves of design §8.3's capability at
/// once: Order.lineItems(first: 250) { refundableQuantity } for the
/// refundable-quantity question (research Q1), and
/// Order.suggestedRefund(refundLineItems: ...) for the resolved-amount
/// question (research Q2) — Shopify's own computed, discount/tax-aware
/// amount for exactly the requested quantities, never a locally-derived
/// quantity * per-unit-price calculation.
/// </summary>
public class ShopifyRefundLookupService(
    HttpClient httpClient,
    AppDbContext db,
    IIntegrationTokenProtector tokenProtector,
    IOptions<ShopifyOptions> options) : IShopifyRefundLookupService
{
    private const string RefundPreflightQuery = """
        query RefundPreflight($id: ID!, $refundLineItems: [RefundLineItemInput!]!) {
          order(id: $id) {
            id
            lineItems(first: 250) {
              edges {
                node {
                  id
                  refundableQuantity
                }
              }
            }
            suggestedRefund(refundLineItems: $refundLineItems) {
              refundLineItems {
                subtotalSet {
                  presentmentMoney {
                    amount
                    currencyCode
                  }
                }
              }
            }
          }
        }
        """;

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        jsonOptions.Converters.Add(new ShopifyDecimalStringConverter());
        return jsonOptions;
    }

    public async Task<ShopifyRefundPreflightResult> GetRefundPreflightAsync(
        Guid businessId, string externalOrderId,
        IReadOnlyList<(string ExternalLineItemId, int Quantity)> requestedItems,
        CancellationToken cancellationToken)
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
        var requestJson = JsonSerializer.Serialize(new
        {
            query = RefundPreflightQuery,
            variables = new
            {
                id = externalOrderId,
                refundLineItems = requestedItems.Select(i => new { lineItemId = i.ExternalLineItemId, quantity = i.Quantity }),
            },
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = new StringContent(requestJson, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Shopify-Access-Token", accessToken);

        using var response = await httpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Shopify refund preflight lookup returned a non-success status ({(int)response.StatusCode}).");
        }

        RefundPreflightResponse? body;
        try
        {
            body = await response.Content.ReadFromJsonAsync<RefundPreflightResponse>(JsonOptions, cancellationToken);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Shopify refund preflight lookup returned malformed JSON.", ex);
        }

        if (body is null)
        {
            throw new InvalidOperationException("Shopify refund preflight lookup returned an empty response body.");
        }

        if (body.Errors is { Count: > 0 })
        {
            var messages = string.Join("; ", body.Errors.Select(e => e.Message));
            throw new InvalidOperationException($"Shopify refund preflight lookup returned GraphQL errors: {messages}");
        }

        var order = body.Data?.Order;
        if (order is null)
        {
            throw new InvalidOperationException(
                $"Shopify reported no order for id '{externalOrderId}' during refund preflight lookup.");
        }

        var refundableQuantityByLineItemId = order.LineItems.Edges
            .ToDictionary(e => e.Node.Id, e => e.Node.RefundableQuantity, StringComparer.Ordinal);

        var suggestedLines = order.SuggestedRefund?.RefundLineItems;
        if (suggestedLines is null || suggestedLines.Count != requestedItems.Count)
        {
            // Research Q4/Q6's own disclosed uncertainty about
            // suggestedRefund's response shape for an invalid/partial
            // request — this pass does not guess at a mapping when the
            // response doesn't unambiguously correspond 1:1, positionally,
            // to what was requested.
            throw new InvalidOperationException(
                "Shopify suggestedRefund did not return exactly one entry per requested line item " +
                $"(requested {requestedItems.Count}, received {suggestedLines?.Count ?? 0}).");
        }

        var lines = new List<ShopifyRefundLineFacts>(requestedItems.Count);
        string? currencyCode = null;
        for (var i = 0; i < requestedItems.Count; i++)
        {
            var (externalLineItemId, quantity) = requestedItems[i];

            if (!refundableQuantityByLineItemId.TryGetValue(externalLineItemId, out var refundableQuantity))
            {
                throw new LineItemNotFoundOnOrderException(externalLineItemId);
            }

            var presentmentMoney = suggestedLines[i].SubtotalSet.PresentmentMoney;
            currencyCode ??= presentmentMoney.CurrencyCode;

            lines.Add(new ShopifyRefundLineFacts(
                externalLineItemId, quantity, refundableQuantity, presentmentMoney.Amount));
        }

        return new ShopifyRefundPreflightResult(lines, currencyCode!);
    }

    private sealed record RefundPreflightResponse(
        [property: JsonPropertyName("data")] RefundPreflightData? Data,
        [property: JsonPropertyName("errors")] List<ShopifyGraphQlErrorDto>? Errors);

    private sealed record RefundPreflightData(
        [property: JsonPropertyName("order")] RefundPreflightOrderDto? Order);

    private sealed record RefundPreflightOrderDto(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("lineItems")] RefundPreflightLineItemsDto LineItems,
        [property: JsonPropertyName("suggestedRefund")] RefundPreflightSuggestedRefundDto? SuggestedRefund);

    private sealed record RefundPreflightLineItemsDto(
        [property: JsonPropertyName("edges")] List<RefundPreflightLineItemEdgeDto> Edges);

    private sealed record RefundPreflightLineItemEdgeDto(
        [property: JsonPropertyName("node")] RefundPreflightLineItemNodeDto Node);

    private sealed record RefundPreflightLineItemNodeDto(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("refundableQuantity")] int RefundableQuantity);

    private sealed record RefundPreflightSuggestedRefundDto(
        [property: JsonPropertyName("refundLineItems")] List<RefundPreflightSuggestedRefundLineDto> RefundLineItems);

    private sealed record RefundPreflightSuggestedRefundLineDto(
        [property: JsonPropertyName("subtotalSet")] RefundPreflightMoneyBagDto SubtotalSet);

    private sealed record RefundPreflightMoneyBagDto(
        [property: JsonPropertyName("presentmentMoney")] RefundPreflightMoneyDto PresentmentMoney);

    // Research Q8: Shopify's Decimal scalar is transmitted as a JSON
    // string (e.g. "29.99"), never a bare numeric literal — parsed via the
    // explicit ShopifyDecimalStringConverter below (registered on
    // JsonOptions above), not relied upon as an assumed default framework
    // behavior. decimal.Parse from a string is lossless, unlike a
    // double/float intermediate would be.
    private sealed record RefundPreflightMoneyDto(
        [property: JsonPropertyName("amount")] decimal Amount,
        [property: JsonPropertyName("currencyCode")] string CurrencyCode);

    private sealed record ShopifyGraphQlErrorDto(
        [property: JsonPropertyName("message")] string Message);

    /// <summary>
    /// Research Q8: Shopify's admin-graphql Decimal scalar is documented as
    /// "serialized as a string" — this converter reads that string form
    /// explicitly via decimal.Parse(InvariantCulture), rather than trusting
    /// any JsonSerializerOptions default to coerce a quoted number into a
    /// decimal. Also accepts a bare JSON number defensively (never throws on
    /// the "friendlier" shape), but the string case is the one Shopify's own
    /// documentation actually establishes.
    /// </summary>
    private sealed class ShopifyDecimalStringConverter : JsonConverter<decimal>
    {
        public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.String
                ? decimal.Parse(reader.GetString()!, System.Globalization.CultureInfo.InvariantCulture)
                : reader.GetDecimal();

        public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}
