using System.Text;
using System.Text.Json;
using Anthropic.Models.Messages;
using Microsoft.EntityFrameworkCore;
using backend.Data;

namespace backend.Services;

/// <summary>
/// Step Group B — getProduct, a read-only Shopify catalog lookup. See
/// docs/handoffs/step-group-b-implementation-design.md for the full design
/// (as amended by its independent-verification corrections and the owner
/// resolution recorded in its §11.1) and
/// docs/handoffs/step-group-b-getproduct-searchproducts-design.md for the
/// locked design this implementation is built on.
///
/// Direct IReadOnlyDispatchTool, mirroring GetOrderTool — no Approval row,
/// no human approve/deny step. Unlike GetOrderTool, this tool performs no
/// Level 2 verification and no ownership authorization: catalog data is
/// merchant-owned, not customer-owned, so there is no "does this customer
/// own this" question to answer (locked design §3, implementation design
/// §1 item 2) — tenant scoping (an active Integration) is sufficient.
/// Enforced unconditionally, regardless of Business.ShopifyReadToolsEnabled
/// — that flag is an exposure/control-plane gate only (checked one layer
/// up, in ClaudeReplyService, before this class is ever reached), the same
/// shared kill switch getOrder already uses, not a new one.
///
/// Identifier contract (Decision 1, locked): GID or exact SKU only. No
/// title/handle lookup, no fuzzy matching, no fallback search — broader
/// discovery is exclusively searchProducts' responsibility.
/// </summary>
public class GetProductTool(
    AppDbContext db,
    IShopifyProductLookupService productLookupService) : IReadOnlyDispatchTool
{
    /// <summary>
    /// A stable, non-stringly-typed reference to this tool's own name,
    /// mirroring GetOrderTool.ToolName's own precedent, for
    /// ClaudeReplyService's Business.ShopifyReadToolsEnabled gate to name
    /// without duplicating the literal "getProduct" in a second file.
    /// </summary>
    public const string ToolName = "getProduct";

    private const string GidProductPrefix = "gid://shopify/Product/";

    public string Name => ToolName;

    public string Description =>
        "Look up a specific product in the store's Shopify catalog — price, availability, and options. " +
        "Accepts either the product's Shopify GID or its exact SKU.";

    public InputSchema InputSchema => new()
    {
        Type = JsonSerializer.SerializeToElement("object"),
        Properties = new Dictionary<string, JsonElement>
        {
            ["productIdentifier"] = JsonSerializer.SerializeToElement(new
            {
                type = "string",
                description = "The product's Shopify GID or exact SKU, exactly as known from the conversation.",
            }),
        },
        Required = ["productIdentifier"],
    };

    public async Task<ToolExecutionResult> ExecuteAsync(
        ToolDispatchContext context, IReadOnlyDictionary<string, JsonElement> input, CancellationToken cancellationToken)
    {
        // 1. Parse input — required string, raw AI-supplied text, no
        // client-side normalization (design §4.1's own convention).
        if (!input.TryGetValue("productIdentifier", out var identifierElement)
            || identifierElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(identifierElement.GetString()))
        {
            return ToolExecutionResult.Error("A product identifier is required to look up a product.");
        }

        var productIdentifier = identifierElement.GetString()!;

        // 2. Active-integration pre-check (mirrors GetOrderTool step 3) — a
        // clean refusal, not an uncaught exception, when no active Shopify
        // integration exists. No Level 2/ownership step exists for this
        // tool (design §3; this tool's own class doc comment above).
        var hasActiveIntegration = await db.Integrations.IgnoreQueryFilters()
            .AnyAsync(i => i.BusinessId == context.BusinessId
                && i.Platform == ShopifyIntegrationOAuthProvider.PlatformName && i.IsActive, cancellationToken);
        if (!hasActiveIntegration)
        {
            return RefuseInfraUnavailable();
        }

        try
        {
            // 3. Identifier branch — GID or SKU, exact-match only
            // (Decision 1). A GID-shaped input skips SKU lookup entirely —
            // no Shopify call needed to make that determination, it's a
            // pure string check.
            var result = productIdentifier.StartsWith(GidProductPrefix, StringComparison.Ordinal)
                ? await productLookupService.GetProductByGidAsync(context.BusinessId, productIdentifier, cancellationToken)
                : await productLookupService.GetProductBySkuAsync(context.BusinessId, productIdentifier, cancellationToken);

            switch (result.Outcome)
            {
                case ProductLookupOutcome.NotFound:
                    return RefuseNotFound();
                case ProductLookupOutcome.Ambiguous:
                    return RefuseAmbiguous();
            }

            return ToolExecutionResult.Success(FormatProductSummary(result.Product!));
        }
        catch (ShopifyAuthFailureException)
        {
            // Integration.ReauthorizationRequiredAt is already written by
            // whichever IShopifyProductLookupService method raised this.
            // Routed here to the infra-unavailable refusal, never
            // ToolDispatchService's fully generic catch, which has no
            // reauthorization-writing hook and would otherwise return the
            // generic "internal error occurred" text instead of this
            // reauthorization-safe one.
            return RefuseInfraUnavailable();
        }
    }

    // ── Refusal messaging (design §5) ──

    /// <summary>Product not found, either branch — an ordinary refusal, not
    /// an exception. No enumeration-prevention collapsing is needed here,
    /// unlike getOrder's Group B, since there is no ownership dimension to
    /// protect against enumeration for merchant-owned catalog data.</summary>
    private static ToolExecutionResult RefuseNotFound() => ToolExecutionResult.Error(
        "I can't find a product matching that.");

    /// <summary>Ambiguous SKU match, SKU branch only — Shopify does not
    /// enforce SKU uniqueness (research doc Q3); a multi-match response is
    /// refused rather than silently resolved to a guess (Decision 3).</summary>
    private static ToolExecutionResult RefuseAmbiguous() => ToolExecutionResult.Error(
        "I found more than one product matching that — could you narrow it down, for example with the exact SKU?");

    /// <summary>Shopify unreachable / auth failure (no active integration,
    /// 401/403, token-decrypt failure, non-2xx, malformed JSON, GraphQL
    /// errors, timeout). Never mentions "401," "403," "token," or
    /// "reconnect" — matches the established reauthorization-copy
    /// discipline elsewhere in this codebase (design §5).</summary>
    private static ToolExecutionResult RefuseInfraUnavailable() => ToolExecutionResult.Error(
        "I'm having trouble reaching our product catalog right now — please try again in a moment.");

    private static string FormatProductSummary(ProductSummaryResult product)
    {
        var sb = new StringBuilder();
        sb.Append(product.Title).Append(':');
        sb.Append(' ').Append(product.Description ?? "(no description)").Append('.');
        sb.Append(' ');
        sb.Append(product.Variant is { } v ? FormatVariant(v) : "No variant information available.");
        return sb.ToString();
    }

    private static string FormatVariant(ProductVariantSummary v)
    {
        var options = v.Options.Count == 0
            ? ""
            : $" ({string.Join(", ", v.Options.Select(o => $"{o.Name}: {o.Value}"))})";
        var sku = v.Sku is null ? "" : $" [SKU {v.Sku}]";
        var price = v.Price is null ? "(price unknown)" : $"${v.Price}";
        var availability = v.AvailableForSale ? "in stock" : "out of stock";
        return $"{price}{sku}{options} — {availability}.";
    }
}
