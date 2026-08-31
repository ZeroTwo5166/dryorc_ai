using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using backend.Data;
using backend.Enums;
using backend.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace backend.Services;

/// <summary>
/// See IOrderCancellationExecutor for the contract. IgnoreQueryFilters() +
/// explicit BusinessId, same non-HTTP-service posture as
/// ShopifyOrderLookupService/OrderCancellationService — this service has no
/// HTTP-request-scoped caller guaranteeing the right tenant context.
///
/// Credential resolution (Integration lookup + token decrypt) is
/// deliberately duplicated from ShopifyOrderLookupService rather than
/// factored into a shared helper this pass — see the pending-reconciliation
/// design handoff's explicit decision not to touch that already-audited
/// file's existing method bodies/constructor.
///
/// refundMethod construction — the mapping from OrderCancellation.Method to
/// Shopify's OrderCancelRefundMethodInput, including the null->{} "no
/// refund" case — is an accepted, DISCLOSED residual risk. Shopify's own
/// documentation does not state what an empty refundMethod object means;
/// this must be validated against the first real live cancellation. See
/// the design handoff for the full disclosure — do not describe this as
/// confirmed-by-documentation in any future report.
/// </summary>
public class ShopifyOrderCancellationExecutor(
    HttpClient httpClient,
    AppDbContext db,
    IIntegrationTokenProtector tokenProtector,
    IOptions<ShopifyOptions> options,
    IOrderCancellationService orderCancellationService) : IOrderCancellationExecutor
{
    private const string OrderCancelMutation = """
        mutation OrderCancel($orderId: ID!, $restock: Boolean!, $reason: OrderCancelReason!, $refundMethod: OrderCancelRefundMethodInput!) {
          orderCancel(orderId: $orderId, restock: $restock, reason: $reason, refundMethod: $refundMethod) {
            job { id done }
            orderCancelUserErrors { field message code }
          }
        }
        """;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task ExecuteAsync(Guid orderCancellationId, CancellationToken cancellationToken)
    {
        var row = await db.OrderCancellations
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == orderCancellationId, cancellationToken);

        if (row is null)
        {
            throw new InvalidOperationException($"OrderCancellation {orderCancellationId} does not exist.");
        }

        if (row.Status != OrderCancellationStatus.Pending)
        {
            // Already resolved (by a prior ExecuteAsync call or by
            // reconciliation) — never re-execute a terminal row.
            return;
        }

        var integration = await db.Integrations
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                i => i.BusinessId == row.BusinessId
                    && i.Platform == ShopifyIntegrationOAuthProvider.PlatformName
                    && i.IsActive,
                cancellationToken);

        if (integration is null)
        {
            throw new InvalidOperationException(
                $"No active Shopify integration found for business {row.BusinessId}.");
        }

        if (string.IsNullOrEmpty(integration.ExternalAccountIdentifier))
        {
            throw new InvalidOperationException(
                $"Shopify integration for business {row.BusinessId} has no shop (ExternalAccountIdentifier) recorded.");
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
                $"Failed to decrypt the Shopify access token for business {row.BusinessId}.", ex);
        }

        var shop = integration.ExternalAccountIdentifier;
        var requestUri = $"https://{shop}.myshopify.com/admin/api/{options.Value.AdminApiVersion}/graphql.json";
        var requestJson = JsonSerializer.Serialize(new
        {
            query = OrderCancelMutation,
            variables = new
            {
                orderId = row.ExternalOrderId,
                restock = row.Restock,
                reason = ToShopifyReason(row.Reason),
                refundMethod = ToShopifyRefundMethod(row.Method),
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
                $"Shopify orderCancel returned a non-success status ({(int)response.StatusCode}).");
        }

        OrderCancelMutationResponse? body;
        try
        {
            body = await response.Content.ReadFromJsonAsync<OrderCancelMutationResponse>(JsonOptions, cancellationToken);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Shopify orderCancel returned malformed JSON.", ex);
        }

        if (body is null)
        {
            throw new InvalidOperationException("Shopify orderCancel returned an empty response body.");
        }

        if (body.Errors is { Count: > 0 })
        {
            var messages = string.Join("; ", body.Errors.Select(e => e.Message));
            throw new InvalidOperationException($"Shopify orderCancel returned GraphQL errors: {messages}");
        }

        var userErrors = body.Data?.OrderCancel?.OrderCancelUserErrors;
        if (userErrors is { Count: > 0 })
        {
            var reason = string.Join("; ", userErrors.Select(e => e.Message));
            await orderCancellationService.MarkFailedAsync(orderCancellationId, reason, cancellationToken);
            return;
        }

        // Mutation accepted — job is async and may still be processing.
        // Deliberately not marking Confirmed here even if job.done == true;
        // see the class doc comment and OrderCancellation's own doc comment
        // for why only reconciliation performs that transition.
    }

    private static string ToShopifyReason(OrderCancelReason reason) => reason switch
    {
        OrderCancelReason.Customer => "CUSTOMER",
        OrderCancelReason.Payment => "PAYMENT",
        OrderCancelReason.Fraud => "FRAUD",
        OrderCancelReason.Inventory => "INVENTORY",
        OrderCancelReason.StaffError => "STAFF_ERROR",
        OrderCancelReason.Other => "OTHER",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unmapped OrderCancelReason value."),
    };

    /// <summary>
    /// See the class doc comment's residual-risk disclosure — the null
    /// case's empty-object shape is an unverified assumption, not a
    /// Shopify-confirmed one.
    /// </summary>
    private static object ToShopifyRefundMethod(RefundMethod? method) => method switch
    {
        null => new { },
        RefundMethod.OriginalPaymentMethod => new { originalPaymentMethodsRefund = true },
        RefundMethod.StoreCredit => new { storeCreditRefund = new { } },
        _ => throw new ArgumentOutOfRangeException(nameof(method), method, "Unmapped RefundMethod value."),
    };

    private sealed record OrderCancelMutationResponse(
        [property: JsonPropertyName("data")] OrderCancelMutationData? Data,
        [property: JsonPropertyName("errors")] List<ShopifyGraphQlErrorDto>? Errors);

    private sealed record OrderCancelMutationData(
        [property: JsonPropertyName("orderCancel")] OrderCancelPayloadDto? OrderCancel);

    private sealed record OrderCancelPayloadDto(
        [property: JsonPropertyName("job")] OrderCancelJobDto? Job,
        [property: JsonPropertyName("orderCancelUserErrors")] List<OrderCancelUserErrorDto>? OrderCancelUserErrors);

    private sealed record OrderCancelJobDto(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("done")] bool Done);

    private sealed record OrderCancelUserErrorDto(
        [property: JsonPropertyName("field")] List<string>? Field,
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("code")] string? Code);

    private sealed record ShopifyGraphQlErrorDto(
        [property: JsonPropertyName("message")] string Message);
}
