using System.Text;
using System.Text.Json;
using Anthropic.Models.Messages;
using Microsoft.EntityFrameworkCore;
using backend.Data;

namespace backend.Services;

/// <summary>
/// Step Group C — getInventory, a read-only Shopify variant-availability
/// lookup. See docs/handoffs/step-getinventory-implementation-design.md
/// (as approved by its three owner decisions, §0.3) and
/// docs/handoffs/step-getinventory-shopify-research.md for the Shopify
/// research this implementation is built on.
///
/// Direct IReadOnlyDispatchTool, mirroring GetProductTool/SearchProductsTool
/// — no Approval row, no human approve/deny step. Like those two, this tool
/// performs no Level 2 verification and no ownership authorization:
/// catalog/availability data is merchant-owned, not customer-owned, so
/// there is no "does this customer own this" question to answer (locked
/// design §3, implementation design §1.1) — tenant scoping (an active
/// Integration) is sufficient. Enforced unconditionally, regardless of
/// Business.ShopifyReadToolsEnabled — that flag is an exposure/
/// control-plane gate only (checked one layer up, in ClaudeReplyService,
/// before this class is ever reached), the same shared kill switch
/// getOrder/getProduct/searchProducts already use, not a new one.
///
/// InStock reflects ProductVariant.availableForSale only — purchasability,
/// not a guarantee of physical stock on hand (Owner Decision 1, design
/// §3.5). No numeric inventory quantity is ever fetched or exposed, here
/// or in IShopifyProductLookupService's two new methods this tool calls.
///
/// Identifier contract mirrors GetProductTool's (GID or exact SKU only —
/// no title/handle lookup), but the GID branch's ambiguity behavior does
/// NOT mirror GetProductTool's: a Product GID resolving to more than one
/// variant refuses (Ambiguous) here rather than silently resolving a
/// representative variant (Owner Decision 2, design §4.3) — a wrong yes/no
/// availability claim is worse than a wrong descriptive detail.
/// </summary>
public class GetInventoryTool(
    AppDbContext db,
    IShopifyProductLookupService productLookupService) : IReadOnlyDispatchTool
{
    /// <summary>
    /// A stable, non-stringly-typed reference to this tool's own name,
    /// mirroring GetProductTool.ToolName's own precedent, for
    /// ClaudeReplyService's Business.ShopifyReadToolsEnabled gate to name
    /// without duplicating the literal "getInventory" in a second file.
    /// </summary>
    public const string ToolName = "getInventory";

    private const string GidProductPrefix = "gid://shopify/Product/";

    public string Name => ToolName;

    public string Description =>
        "Check whether a specific product in the store's Shopify catalog is currently in stock. " +
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
        // client-side normalization (matches GetProductTool's own convention).
        if (!input.TryGetValue("productIdentifier", out var identifierElement)
            || identifierElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(identifierElement.GetString()))
        {
            return ToolExecutionResult.Error("A product identifier is required to check inventory.");
        }

        var productIdentifier = identifierElement.GetString()!;

        // 2. Active-integration pre-check (mirrors GetProductTool step 2) —
        // a clean refusal, not an uncaught exception, when no active
        // Shopify integration exists. No Level 2/ownership step exists for
        // this tool (design §3; this tool's own class doc comment above).
        var hasActiveIntegration = await db.Integrations.IgnoreQueryFilters()
            .AnyAsync(i => i.BusinessId == context.BusinessId
                && i.Platform == ShopifyIntegrationOAuthProvider.PlatformName && i.IsActive, cancellationToken);
        if (!hasActiveIntegration)
        {
            return RefuseInfraUnavailable();
        }

        try
        {
            // 3. Identifier branch — GID or SKU, exact-match only, same
            // shape as GetProductTool (design §4.5). A GID-shaped input
            // skips SKU lookup entirely — no Shopify call needed to make
            // that determination, it's a pure string check.
            var result = productIdentifier.StartsWith(GidProductPrefix, StringComparison.Ordinal)
                ? await productLookupService.GetInventoryByGidAsync(context.BusinessId, productIdentifier, cancellationToken)
                : await productLookupService.GetInventoryBySkuAsync(context.BusinessId, productIdentifier, cancellationToken);

            switch (result.Outcome)
            {
                case InventoryLookupOutcome.NotFound:
                    return RefuseNotFound();
                case InventoryLookupOutcome.Ambiguous:
                    return RefuseAmbiguous();
            }

            return ToolExecutionResult.Success(FormatAvailability(result.Availability!));
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

    // ── Refusal messaging (mirrors GetProductTool's discipline) ──

    /// <summary>Product/SKU not found, either branch — an ordinary refusal,
    /// not an exception. No enumeration-prevention collapsing is needed
    /// here, unlike getOrder's Group B, since there is no ownership
    /// dimension to protect against enumeration for merchant-owned catalog
    /// data.</summary>
    private static ToolExecutionResult RefuseNotFound() => ToolExecutionResult.Error(
        "I can't find inventory information for that.");

    /// <summary>Ambiguous match — either a SKU matching 2+ variants (same
    /// underlying cause as GetProductTool's own ambiguity), or a Product
    /// GID resolving to a product with 2+ variants (Owner Decision 2 —
    /// getInventory never silently picks a representative variant the way
    /// GetProductTool does).</summary>
    private static ToolExecutionResult RefuseAmbiguous() => ToolExecutionResult.Error(
        "I found more than one product matching that — could you narrow it down, for example with the exact SKU?");

    /// <summary>Shopify unreachable / auth failure (no active integration,
    /// 401/403, token-decrypt failure, non-2xx, malformed JSON, GraphQL
    /// errors, timeout). Never mentions "401," "403," "token," or
    /// "reconnect" — matches the established reauthorization-copy
    /// discipline elsewhere in this codebase.</summary>
    private static ToolExecutionResult RefuseInfraUnavailable() => ToolExecutionResult.Error(
        "I'm having trouble reaching our product catalog right now — please try again in a moment.");

    private static string FormatAvailability(InventoryAvailabilityResult availability)
    {
        var sb = new StringBuilder();
        sb.Append(availability.ProductTitle);
        if (availability.Sku is { } sku)
        {
            sb.Append(" [SKU ").Append(sku).Append(']');
        }
        if (availability.Options.Count > 0)
        {
            sb.Append(" (").Append(string.Join(", ", availability.Options.Select(o => $"{o.Name}: {o.Value}"))).Append(')');
        }
        sb.Append(": ").Append(availability.InStock ? "in stock" : "out of stock").Append('.');
        return sb.ToString();
    }
}
