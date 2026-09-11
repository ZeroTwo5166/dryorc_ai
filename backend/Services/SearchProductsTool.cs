using System.Text.Json;
using Anthropic.Models.Messages;
using Microsoft.EntityFrameworkCore;
using backend.Data;

namespace backend.Services;

/// <summary>
/// Step Group B — searchProducts, a read-only, free-text Shopify catalog
/// search. See docs/handoffs/step-group-b-implementation-design.md for the
/// full design and docs/handoffs/step-group-b-getproduct-searchproducts-design.md
/// for the locked design this implementation is built on.
///
/// Direct IReadOnlyDispatchTool, mirroring GetProductTool/GetOrderTool — no
/// Approval row, no human approve/deny step, no Level 2 verification or
/// ownership authorization (locked design §3, implementation design §1
/// item 2) — tenant scoping (an active Integration) is sufficient.
/// Enforced unconditionally, regardless of Business.ShopifyReadToolsEnabled
/// — the same shared kill switch getOrder/getProduct already use, not a
/// new one.
///
/// Never exposes Shopify pagination to the AI — no AI-supplied `limit`
/// argument exists in InputSchema; a fixed server-side cap
/// (ShopifyProductLookupService.SearchResultCap) is applied instead
/// (locked design §6, restated implementation design §1 item 7). Never
/// calls getProduct internally — a single, separate Shopify query, per
/// locked design §5.1.
/// </summary>
public class SearchProductsTool(
    AppDbContext db,
    IShopifyProductLookupService productLookupService) : IReadOnlyDispatchTool
{
    /// <summary>
    /// A stable, non-stringly-typed reference to this tool's own name,
    /// mirroring GetOrderTool.ToolName's own precedent, for
    /// ClaudeReplyService's Business.ShopifyReadToolsEnabled gate to name
    /// without duplicating the literal "searchProducts" in a second file.
    /// </summary>
    public const string ToolName = "searchProducts";

    public string Name => ToolName;

    public string Description =>
        "Search the store's Shopify catalog by free text — title, keyword, or general description. " +
        "Use this to find products when the exact GID or SKU isn't known.";

    public InputSchema InputSchema => new()
    {
        Type = JsonSerializer.SerializeToElement("object"),
        Properties = new Dictionary<string, JsonElement>
        {
            ["query"] = JsonSerializer.SerializeToElement(new
            {
                type = "string",
                description = "Free-text product search — a title, keyword, or description of what the customer is looking for.",
            }),
        },
        Required = ["query"],
    };

    public async Task<ToolExecutionResult> ExecuteAsync(
        ToolDispatchContext context, IReadOnlyDictionary<string, JsonElement> input, CancellationToken cancellationToken)
    {
        // 1. Parse input — required string, raw AI-supplied text, no
        // client-side normalization.
        if (!input.TryGetValue("query", out var queryElement)
            || queryElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(queryElement.GetString()))
        {
            return ToolExecutionResult.Error("A search term is required to search the product catalog.");
        }

        var query = queryElement.GetString()!;

        // 2. Active-integration pre-check, identical to GetProductTool.
        var hasActiveIntegration = await db.Integrations.IgnoreQueryFilters()
            .AnyAsync(i => i.BusinessId == context.BusinessId
                && i.Platform == ShopifyIntegrationOAuthProvider.PlatformName && i.IsActive, cancellationToken);
        if (!hasActiveIntegration)
        {
            return RefuseInfraUnavailable();
        }

        try
        {
            // 3. Search — an empty result is an ordinary success (design
            // §6/§8), never a refusal.
            var result = await productLookupService.SearchProductsAsync(context.BusinessId, query, cancellationToken);
            return ToolExecutionResult.Success(FormatSearchResults(result.Products));
        }
        catch (ShopifyAuthFailureException)
        {
            return RefuseInfraUnavailable();
        }
    }

    /// <summary>Shopify unreachable / auth failure — same text and
    /// mechanism-leakage discipline as GetProductTool's own.</summary>
    private static ToolExecutionResult RefuseInfraUnavailable() => ToolExecutionResult.Error(
        "I'm having trouble reaching our product catalog right now — please try again in a moment.");

    private static string FormatProductSummary(ProductSummaryResult product)
    {
        var sb = new System.Text.StringBuilder();
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

    private static string FormatSearchResults(IReadOnlyList<ProductSummaryResult> products) =>
        products.Count == 0
            ? "No products matched that search."
            : string.Join("\n", products.Select(FormatProductSummary));
}
