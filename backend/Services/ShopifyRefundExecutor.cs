using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using backend.Constants;
using backend.Data;
using backend.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace backend.Services;

/// <summary>
/// See IRefundExecutor for the contract. IgnoreQueryFilters() + explicit
/// BusinessId, same non-HTTP-service posture as
/// ShopifyOrderCancellationExecutor/RefundService — this service has no
/// HTTP-request-scoped caller guaranteeing the right tenant context.
///
/// Credential resolution is deliberately duplicated from
/// ShopifyOrderLookupService/ShopifyRefundLookupService, matching
/// ShopifyOrderCancellationExecutor's own identical, explicitly-stated
/// decision not to factor this into a shared helper.
///
/// Mutation shape (RefundInput/RefundLineItemInput) cross-verified against
/// three official Shopify reference pages, per the dedicated research pass
/// (docs/handoffs/step-refundorder-shopify-research.md, Q3) — the
/// refundLineItems entries carry only {lineItemId, quantity}, never a
/// caller-supplied amount; Shopify computes the monetary effect server-side.
/// allowOverRefunding is left at its documented default (false) — never set
/// to true (research Q6/Q10: this is a genuine second server-side
/// enforcement layer against over-refunding, independent of
/// ShopifyRefundLookupService's own preflight check; removing it, or the
/// preflight check, would leave only one layer instead of two).
///
/// @idempotent(key: $idempotencyKey) — mandatory as of API version 2026-04,
/// confirmed still mandatory at this design's 2026-07 anchor (research Q11).
/// The key passed here is the caller's own execution-leg identity
/// (ApprovalExecutionKeyDeriver.Derive's output, one per Approval) — this
/// protects a genuine retry of the SAME execution attempt from creating a
/// second refund, but per research Q11/design §17 does NOT close the
/// distinct-Approval race: two different Approvals derive two different
/// keys, and Shopify has no way to recognize them as related.
///
/// RefundMethod -> Shopify RefundMethodInput mapping — DISCLOSED, unverified
/// residual risk, same posture as ShopifyOrderCancellationExecutor's own
/// ToShopifyRefundMethod: the dedicated Shopify research pass confirmed
/// RefundInput.refundMethods exists (default []) but did not confirm
/// RefundMethodInput's own exact field shape from an official reference
/// page. OriginalPaymentMethod is mapped to an empty array (Shopify's own
/// documented default when refundMethods is omitted). StoreCredit's mapping
/// below is a structural best-effort guess, modeled on
/// OrderCancelRefundMethodInput's own confirmed storeCreditRefund shape (a
/// different, but Shopify-published, sibling input type) — this must be
/// validated against a real sandbox refund before StoreCreditRefundsEnabled
/// is used in production. Do not describe this mapping as confirmed by
/// documentation in any future report.
/// </summary>
public class ShopifyRefundExecutor(
    HttpClient httpClient,
    AppDbContext db,
    IIntegrationTokenProtector tokenProtector,
    IOptions<ShopifyOptions> options,
    IRefundService refundService) : IRefundExecutor
{
    private const string RefundCreateMutation = """
        mutation RefundCreate($input: RefundInput!, $idempotencyKey: String!) {
          refundCreate(input: $input) @idempotent(key: $idempotencyKey) {
            order { id }
            refund {
              id
              totalRefundedSet {
                presentmentMoney {
                  amount
                }
              }
            }
            userErrors { field message code }
          }
        }
        """;

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        jsonOptions.Converters.Add(new ShopifyDecimalStringJsonConverter());
        return jsonOptions;
    }

    public async Task ExecuteAsync(Guid refundId, string idempotencyKey, CancellationToken cancellationToken)
    {
        var row = await db.Refunds
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == refundId, cancellationToken);

        if (row is null)
        {
            throw new InvalidOperationException($"Refund {refundId} does not exist.");
        }

        if (row.Status != RefundStatus.Pending)
        {
            // Already resolved (by a prior ExecuteAsync call) — never
            // re-execute a terminal row.
            return;
        }

        var items = await db.RefundItems
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(x => x.RefundId == refundId)
            .Select(x => new { x.ExternalLineItemId, x.Quantity })
            .ToListAsync(cancellationToken);

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
            // Decision 7 item 3, literal inclusion (design §6/§6.1) — ANY
            // non-cancellation exception from Unprotect is classified as
            // auth-classified. This repository draws no finer distinction
            // (this catch was already exception-type-agnostic before this
            // change; see §6.1's own disclosed, unresolved residual risk of
            // a transient Data Protection fault being misclassified this
            // way — accepted, not solved, by the locked decision).
            await MarkAuthFailureAsync(refundId, row.BusinessId, "Shopify access token decryption failed.", cancellationToken);
            return;
        }

        var shop = integration.ExternalAccountIdentifier;
        var requestUri = $"https://{shop}.myshopify.com/admin/api/{options.Value.AdminApiVersion}/graphql.json";
        var requestJson = JsonSerializer.Serialize(new
        {
            query = RefundCreateMutation,
            variables = new
            {
                input = new
                {
                    orderId = row.ExternalOrderId,
                    refundLineItems = items.Select(i => new { lineItemId = i.ExternalLineItemId, quantity = i.Quantity }),
                    refundMethods = ToShopifyRefundMethods(row.Method),
                    allowOverRefunding = false,
                },
                idempotencyKey,
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
            // Decision 7 items 1/2 (design §6) — ONLY 401/403 are
            // auth-classified. Every other non-success status (400, 404,
            // 429, 5xx, ...) keeps the existing, unchanged uncaught-throw
            // behavior below — this predicate must not be broadened.
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                await MarkAuthFailureAsync(
                    refundId, row.BusinessId, $"Shopify returned HTTP {(int)response.StatusCode}.", cancellationToken);
                return;
            }

            throw new InvalidOperationException(
                $"Shopify refundCreate returned a non-success status ({(int)response.StatusCode}).");
        }

        RefundCreateMutationResponse? body;
        try
        {
            body = await response.Content.ReadFromJsonAsync<RefundCreateMutationResponse>(JsonOptions, cancellationToken);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Shopify refundCreate returned malformed JSON.", ex);
        }

        if (body is null)
        {
            throw new InvalidOperationException("Shopify refundCreate returned an empty response body.");
        }

        if (body.Errors is { Count: > 0 })
        {
            var messages = string.Join("; ", body.Errors.Select(e => e.Message));
            throw new InvalidOperationException($"Shopify refundCreate returned GraphQL errors: {messages}");
        }

        var userErrors = body.Data?.RefundCreate?.UserErrors;
        if (userErrors is { Count: > 0 })
        {
            // Every populated userErrors case is treated uniformly as a
            // definite rejection, matching ShopifyOrderCancellationExecutor's
            // own convention — including IDEMPOTENCY_CONCURRENT_REQUEST
            // (research Q11), which is NOT special-cased into a retry here:
            // this design/research pass does not establish what safe retry
            // behavior for that specific code would look like, and inventing
            // one would be exactly the kind of unestablished Shopify-behavior
            // assumption this pass must not make.
            var reason = string.Join("; ", userErrors.Select(e => e.Code is null ? e.Message : $"{e.Code}: {e.Message}"));
            await refundService.MarkFailedAsync(refundId, reason, cancellationToken);
            return;
        }

        var refund = body.Data?.RefundCreate?.Refund;
        if (refund is null)
        {
            // No userErrors AND no refund object — an ambiguous response
            // shape research Q4 flagged as a genuine, undocumented
            // possibility (partial-success semantics unconfirmed). Never
            // silently treated as success; propagates as an infra-level
            // anomaly, leaving the row Pending for manual recovery.
            throw new InvalidOperationException(
                "Shopify refundCreate returned neither userErrors nor a refund object.");
        }

        // refundCreate is confirmed synchronous (research Q5) — the mutation
        // response IS the final outcome, resolved directly here, never left
        // Pending for a separate reconciliation service to poll.
        await refundService.MarkConfirmedAsync(
            refundId, refund.Id, refund.TotalRefundedSet.PresentmentMoney.Amount, cancellationToken);
    }

    /// <summary>
    /// Step Shopify-auth-reauth (design §7) — the only entry point for the
    /// auth-classified failure path (HTTP 401/403, token-decrypt failure).
    /// Mirrors the existing userErrors branch's own shape (MarkFailedAsync +
    /// normal return, no exception) so ExecutionOutcome's closed hierarchy
    /// needs no change and RefundApprovedActionExecutor's existing
    /// post-executor-call code (audit + return) runs unmodified. Wraps the
    /// Refund.Failed transition and the Integration.ReauthorizationRequiredAt
    /// write in one local transaction (design §11's own recommendation) —
    /// no Shopify network I/O occurs inside it, matching the atomic-claim
    /// discipline this codebase already uses elsewhere. Scoped to
    /// (BusinessId, Platform), never gated on IsActive — a concurrent
    /// disconnect racing this write is harmless (design §21): the dashboard
    /// derives "Disconnected" whenever IsActive is false regardless of this
    /// column's value.
    /// </summary>
    private async Task MarkAuthFailureAsync(
        Guid refundId, Guid businessId, string detail, CancellationToken cancellationToken)
    {
        var reason = ShopifyFailureReasons.ReauthorizationRequiredPrefix + detail;
        var now = DateTimeOffset.UtcNow;

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        await refundService.MarkFailedAsync(refundId, reason, cancellationToken);

        await db.Integrations
            .IgnoreQueryFilters()
            .Where(i => i.BusinessId == businessId && i.Platform == ShopifyIntegrationOAuthProvider.PlatformName)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.ReauthorizationRequiredAt, now), cancellationToken);

        await tx.CommitAsync(cancellationToken);
    }

    private static object[] ToShopifyRefundMethods(RefundMethod method) => method switch
    {
        RefundMethod.OriginalPaymentMethod => [],
        RefundMethod.StoreCredit => [new { storeCreditRefund = new { } }],
        _ => throw new ArgumentOutOfRangeException(nameof(method), method, "Unmapped RefundMethod value."),
    };

    private sealed record RefundCreateMutationResponse(
        [property: JsonPropertyName("data")] RefundCreateMutationData? Data,
        [property: JsonPropertyName("errors")] List<ShopifyGraphQlErrorDto>? Errors);

    private sealed record RefundCreateMutationData(
        [property: JsonPropertyName("refundCreate")] RefundCreatePayloadDto? RefundCreate);

    private sealed record RefundCreatePayloadDto(
        [property: JsonPropertyName("refund")] RefundDto? Refund,
        [property: JsonPropertyName("userErrors")] List<RefundUserErrorDto>? UserErrors);

    private sealed record RefundDto(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("totalRefundedSet")] MoneyBagDto TotalRefundedSet);

    private sealed record MoneyBagDto(
        [property: JsonPropertyName("presentmentMoney")] MoneyDto PresentmentMoney);

    private sealed record MoneyDto(
        [property: JsonPropertyName("amount")] decimal Amount);

    private sealed record RefundUserErrorDto(
        [property: JsonPropertyName("field")] List<string>? Field,
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("code")] string? Code);

    private sealed record ShopifyGraphQlErrorDto(
        [property: JsonPropertyName("message")] string Message);

    /// <summary>See ShopifyRefundLookupService's identical converter for the
    /// full rationale (research Q8: Shopify's Decimal scalar is a JSON
    /// string, never a bare numeric literal). Duplicated rather than shared,
    /// matching this file's own stated no-shared-helper convention.</summary>
    private sealed class ShopifyDecimalStringJsonConverter : JsonConverter<decimal>
    {
        public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.String
                ? decimal.Parse(reader.GetString()!, System.Globalization.CultureInfo.InvariantCulture)
                : reader.GetDecimal();

        public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}
