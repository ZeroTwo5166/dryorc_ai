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
/// Step Group B. See IShopifyProductLookupService for the contract and
/// security posture. New sibling service to ShopifyOrderLookupService, per
/// the locked design's §2 "parallel sibling service" conclusion — catalog
/// and order data share no method bodies, so this is not an extension of
/// ShopifyOrderLookupService.
///
/// IgnoreQueryFilters() + explicit BusinessId, same non-HTTP-safe posture
/// as ShopifyOrderLookupService — this service has no HTTP-request-scoped
/// caller guaranteeing the right tenant context.
///
/// Uses the Admin GraphQL API, not REST, matching ShopifyOrderLookupService.
/// AdminApiVersion is configuration-driven (ShopifyOptions), never
/// hardcoded here.
///
/// Each method duplicates its own inline credential-resolution block
/// (Integration lookup + token unprotect + request construction) rather
/// than factoring out a shared helper — matching ShopifyOrderLookupService's
/// own deliberate-duplication convention (its GetCancellationStateAsync doc
/// comment), so no already-audited method body is put at risk by a change
/// to another.
/// </summary>
public class ShopifyProductLookupService(
    HttpClient httpClient,
    AppDbContext db,
    IIntegrationTokenProtector tokenProtector,
    IOptions<ShopifyOptions> options) : IShopifyProductLookupService
{
    /// <summary>
    /// searchProducts' product-count cap (implementation design §2.4) — a
    /// fixed server-side constant, never AI-controlled, matching locked
    /// design §5.1/§6's explicit "no AI-supplied limit parameter"
    /// instruction.
    /// </summary>
    private const int SearchResultCap = 10;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // ── GID branch (implementation design §2.4) ──────────────────────────

    private const string ProductGidLookupQuery = """
        query ProductGidLookup($id: ID!) {
          product(id: $id) {
            id
            title
            description
            variants(first: 1) {
              edges {
                node {
                  sku
                  availableForSale
                  price {
                    amount
                  }
                  selectedOptions {
                    name
                    value
                  }
                }
              }
            }
          }
        }
        """;

    public async Task<ProductLookupResult> GetProductByGidAsync(
        Guid businessId, string productGid, CancellationToken cancellationToken)
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
            new { query = ProductGidLookupQuery, variables = new { id = productGid } });

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
                $"Shopify product lookup returned a non-success status ({(int)response.StatusCode}).");
        }

        ShopifyProductGidLookupResponse? body;
        try
        {
            body = await response.Content.ReadFromJsonAsync<ShopifyProductGidLookupResponse>(JsonOptions, cancellationToken);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Shopify product lookup returned malformed JSON.", ex);
        }

        if (body is null)
        {
            throw new InvalidOperationException("Shopify product lookup returned an empty response body.");
        }

        if (body.Errors is { Count: > 0 })
        {
            var messages = string.Join("; ", body.Errors.Select(e => e.Message));
            throw new InvalidOperationException($"Shopify product lookup returned GraphQL errors: {messages}");
        }

        // "Not found" is an ordinary, non-throwing outcome here — see
        // IShopifyProductLookupService's own doc comment and implementation
        // design §2.4/§10 (no carve-out limiting the ordinary-refusal
        // treatment to the SKU branch).
        var product = body.Data?.Product;
        if (product is null)
        {
            return new ProductLookupResult(ProductLookupOutcome.NotFound, null);
        }

        var variantNode = product.Variants?.Edges?.FirstOrDefault()?.Node;
        var variant = variantNode is null
            ? null
            : MapVariantFields(variantNode.Sku, variantNode.AvailableForSale, variantNode.Price, variantNode.SelectedOptions);

        return new ProductLookupResult(
            ProductLookupOutcome.Resolved,
            new ProductSummaryResult(product.Id, product.Title, product.Description, variant));
    }

    // ── SKU branch (implementation design §2.1/§2.4) ─────────────────────

    private const string ProductVariantSkuLookupQuery = """
        query ProductVariantSkuLookup($query: String!) {
          productVariants(first: 2, query: $query) {
            edges {
              node {
                sku
                availableForSale
                price {
                  amount
                }
                selectedOptions {
                  name
                  value
                }
                product {
                  id
                  title
                  description
                }
              }
            }
          }
        }
        """;

    /// <summary>
    /// NEW OWNER DECISION 3 (implementation design §11.1) — CONFIRMED:
    /// productVariants(query: "sku:...") followed by the .product hop, not
    /// products(query: "sku:..."). This counts matching variants directly —
    /// the correct unit for a SKU-uniqueness check — so a SKU matching more
    /// than one variant of the same product is correctly detected as
    /// ambiguous, which a product-level query would silently miss.
    /// </summary>
    public async Task<ProductLookupResult> GetProductBySkuAsync(
        Guid businessId, string sku, CancellationToken cancellationToken)
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
            new { query = ProductVariantSkuLookupQuery, variables = new { query = $"sku:{sku}" } });

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
                $"Shopify product SKU lookup returned a non-success status ({(int)response.StatusCode}).");
        }

        ShopifyProductVariantSkuLookupResponse? body;
        try
        {
            body = await response.Content.ReadFromJsonAsync<ShopifyProductVariantSkuLookupResponse>(JsonOptions, cancellationToken);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Shopify product SKU lookup returned malformed JSON.", ex);
        }

        if (body is null)
        {
            throw new InvalidOperationException("Shopify product SKU lookup returned an empty response body.");
        }

        if (body.Errors is { Count: > 0 })
        {
            var messages = string.Join("; ", body.Errors.Select(e => e.Message));
            throw new InvalidOperationException($"Shopify product SKU lookup returned GraphQL errors: {messages}");
        }

        // Zero/one/multiple matches are ordinary, successful outcomes here —
        // never thrown. Shopify does not enforce SKU uniqueness (research
        // doc Q3), so a multi-match response is a real, expected case, not
        // an infrastructure anomaly.
        var edges = body.Data?.ProductVariants?.Edges ?? [];
        return edges.Count switch
        {
            0 => new ProductLookupResult(ProductLookupOutcome.NotFound, null),
            1 => new ProductLookupResult(ProductLookupOutcome.Resolved, MapSkuNodeToProduct(edges[0].Node)),
            _ => new ProductLookupResult(ProductLookupOutcome.Ambiguous, null),
        };
    }

    // ── searchProducts (implementation design §2.4) ──────────────────────

    private const string ProductSearchQuery = """
        query ProductSearch($query: String!, $first: Int!) {
          products(first: $first, query: $query) {
            edges {
              node {
                id
                title
                description
                variants(first: 1) {
                  edges {
                    node {
                      sku
                      availableForSale
                      price {
                        amount
                      }
                      selectedOptions {
                        name
                        value
                      }
                    }
                  }
                }
              }
            }
          }
        }
        """;

    public async Task<ProductSearchResult> SearchProductsAsync(
        Guid businessId, string query, CancellationToken cancellationToken)
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
            new { query = ProductSearchQuery, variables = new { query, first = SearchResultCap } });

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
                $"Shopify product search returned a non-success status ({(int)response.StatusCode}).");
        }

        ShopifyProductSearchResponse? body;
        try
        {
            body = await response.Content.ReadFromJsonAsync<ShopifyProductSearchResponse>(JsonOptions, cancellationToken);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Shopify product search returned malformed JSON.", ex);
        }

        if (body is null)
        {
            throw new InvalidOperationException("Shopify product search returned an empty response body.");
        }

        if (body.Errors is { Count: > 0 })
        {
            var messages = string.Join("; ", body.Errors.Select(e => e.Message));
            throw new InvalidOperationException($"Shopify product search returned GraphQL errors: {messages}");
        }

        // An empty match list is an ordinary, successful outcome (design
        // §6/§8) — never a refusal, never thrown.
        var edges = body.Data?.Products?.Edges ?? [];
        var products = edges.Select(e =>
        {
            var node = e.Node;
            var variantNode = node.Variants?.Edges?.FirstOrDefault()?.Node;
            var variant = variantNode is null
                ? null
                : MapVariantFields(variantNode.Sku, variantNode.AvailableForSale, variantNode.Price, variantNode.SelectedOptions);
            return new ProductSummaryResult(node.Id, node.Title, node.Description, variant);
        }).ToList();

        return new ProductSearchResult(products);
    }

    // ── GID branch (implementation design §4.3/§5.2) ──────────────────────

    private const string ProductInventoryGidLookupQuery = """
        query ProductInventoryGidLookup($id: ID!) {
          product(id: $id) {
            id
            title
            variants(first: 2) {
              edges {
                node {
                  sku
                  availableForSale
                  selectedOptions {
                    name
                    value
                  }
                }
              }
            }
          }
        }
        """;

    /// <summary>
    /// OWNER DECISION 2 (implementation design §4.3/§0.3) — CONFIRMED:
    /// variants(first: 2) detects "more than one variant" without walking a
    /// full list, the same exact-count idiom GetProductBySkuAsync's SKU
    /// branch already uses (research doc Q9 — variants(first: 2) is exact;
    /// Product.variantsCount can be a non-exact AT_LEAST value). A
    /// multi-variant product refuses (Ambiguous) here — unlike
    /// GetProductByGidAsync, which always resolves the first/representative
    /// variant. No InventoryItem/InventoryLevel field is ever requested;
    /// availableForSale is the sole source of InStock (Owner Decision 1).
    /// </summary>
    public async Task<InventoryLookupResult> GetInventoryByGidAsync(
        Guid businessId, string productGid, CancellationToken cancellationToken)
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
            new { query = ProductInventoryGidLookupQuery, variables = new { id = productGid } });

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
                $"Shopify inventory lookup returned a non-success status ({(int)response.StatusCode}).");
        }

        ShopifyProductInventoryGidLookupResponse? body;
        try
        {
            body = await response.Content.ReadFromJsonAsync<ShopifyProductInventoryGidLookupResponse>(JsonOptions, cancellationToken);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Shopify inventory lookup returned malformed JSON.", ex);
        }

        if (body is null)
        {
            throw new InvalidOperationException("Shopify inventory lookup returned an empty response body.");
        }

        if (body.Errors is { Count: > 0 })
        {
            var messages = string.Join("; ", body.Errors.Select(e => e.Message));
            throw new InvalidOperationException($"Shopify inventory lookup returned GraphQL errors: {messages}");
        }

        var product = body.Data?.Product;
        if (product is null)
        {
            return new InventoryLookupResult(InventoryLookupOutcome.NotFound, null);
        }

        var edges = product.Variants?.Edges ?? [];
        return edges.Count switch
        {
            0 => new InventoryLookupResult(InventoryLookupOutcome.NotFound, null),
            1 => new InventoryLookupResult(
                InventoryLookupOutcome.Resolved, MapGidNodeToInventory(product.Id, product.Title, edges[0].Node)),
            _ => new InventoryLookupResult(InventoryLookupOutcome.Ambiguous, null),
        };
    }

    // ── SKU branch (implementation design §4.2/§5.2) ──────────────────────

    private const string ProductVariantInventorySkuLookupQuery = """
        query ProductVariantInventorySkuLookup($query: String!) {
          productVariants(first: 2, query: $query) {
            edges {
              node {
                sku
                availableForSale
                selectedOptions {
                  name
                  value
                }
                product {
                  id
                  title
                }
              }
            }
          }
        }
        """;

    /// <summary>
    /// Reuses GetProductBySkuAsync's exact productVariants(query:"sku:...")
    /// + .product hop query shape and identical 0/1/2+ →
    /// NotFound/Resolved/Ambiguous mapping (design §4.2) — the same
    /// SKU-uniqueness-is-not-guaranteed problem already solved there, not
    /// re-solved differently. No InventoryItem/InventoryLevel field is ever
    /// requested; availableForSale is the sole source of InStock (Owner
    /// Decision 1).
    /// </summary>
    public async Task<InventoryLookupResult> GetInventoryBySkuAsync(
        Guid businessId, string sku, CancellationToken cancellationToken)
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
            new { query = ProductVariantInventorySkuLookupQuery, variables = new { query = $"sku:{sku}" } });

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
                $"Shopify inventory SKU lookup returned a non-success status ({(int)response.StatusCode}).");
        }

        ShopifyProductVariantInventorySkuLookupResponse? body;
        try
        {
            body = await response.Content.ReadFromJsonAsync<ShopifyProductVariantInventorySkuLookupResponse>(JsonOptions, cancellationToken);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Shopify inventory SKU lookup returned malformed JSON.", ex);
        }

        if (body is null)
        {
            throw new InvalidOperationException("Shopify inventory SKU lookup returned an empty response body.");
        }

        if (body.Errors is { Count: > 0 })
        {
            var messages = string.Join("; ", body.Errors.Select(e => e.Message));
            throw new InvalidOperationException($"Shopify inventory SKU lookup returned GraphQL errors: {messages}");
        }

        var edges = body.Data?.ProductVariants?.Edges ?? [];
        return edges.Count switch
        {
            0 => new InventoryLookupResult(InventoryLookupOutcome.NotFound, null),
            1 => new InventoryLookupResult(InventoryLookupOutcome.Resolved, MapSkuNodeToInventory(edges[0].Node)),
            _ => new InventoryLookupResult(InventoryLookupOutcome.Ambiguous, null),
        };
    }

    // ── Shared, pure DTO-mapping helpers (no credential resolution shared —
    // see this class's own doc comment for why that stays duplicated) ────

    private static ProductVariantSummary MapVariantFields(
        string? sku, bool availableForSale, ShopifyMoney? price, List<ShopifySelectedOption>? selectedOptions) =>
        new(
            sku,
            price?.Amount,
            availableForSale,
            (selectedOptions ?? []).Select(o => new ProductVariantOptionValue(o.Name, o.Value)).ToList());

    private static ProductSummaryResult MapSkuNodeToProduct(ShopifyProductVariantSkuNode node)
    {
        var variant = MapVariantFields(node.Sku, node.AvailableForSale, node.Price, node.SelectedOptions);
        var product = node.Product!;
        return new ProductSummaryResult(product.Id, product.Title, product.Description, variant);
    }

    private static InventoryAvailabilityResult MapGidNodeToInventory(
        string productId, string productTitle, ShopifyInventoryVariantNode node) =>
        new(productId, productTitle, node.Sku, MapOptions(node.SelectedOptions), node.AvailableForSale);

    private static InventoryAvailabilityResult MapSkuNodeToInventory(ShopifyProductVariantInventorySkuNode node)
    {
        var product = node.Product!;
        return new InventoryAvailabilityResult(product.Id, product.Title, node.Sku, MapOptions(node.SelectedOptions), node.AvailableForSale);
    }

    private static IReadOnlyList<ProductVariantOptionValue> MapOptions(List<ShopifySelectedOption>? selectedOptions) =>
        (selectedOptions ?? []).Select(o => new ProductVariantOptionValue(o.Name, o.Value)).ToList();

    // ── Response DTOs ─────────────────────────────────────────────────

    private sealed record ShopifyGraphQlError(
        [property: JsonPropertyName("message")] string Message);

    private sealed record ShopifyMoney(
        [property: JsonPropertyName("amount")] string Amount);

    private sealed record ShopifySelectedOption(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("value")] string Value);

    // GID branch + searchProducts share this variant-node shape (no
    // `product` back-reference — unlike the SKU branch's node shape below).

    private sealed record ShopifyProductVariantConnection(
        [property: JsonPropertyName("edges")] List<ShopifyProductVariantEdge> Edges);

    private sealed record ShopifyProductVariantEdge(
        [property: JsonPropertyName("node")] ShopifyProductVariantNode Node);

    private sealed record ShopifyProductVariantNode(
        [property: JsonPropertyName("sku")] string? Sku,
        [property: JsonPropertyName("availableForSale")] bool AvailableForSale,
        [property: JsonPropertyName("price")] ShopifyMoney? Price,
        [property: JsonPropertyName("selectedOptions")] List<ShopifySelectedOption>? SelectedOptions);

    // GID branch.

    private sealed record ShopifyProductGidLookupResponse(
        [property: JsonPropertyName("data")] ShopifyProductGidLookupData? Data,
        [property: JsonPropertyName("errors")] List<ShopifyGraphQlError>? Errors);

    private sealed record ShopifyProductGidLookupData(
        [property: JsonPropertyName("product")] ShopifyProductGidLookupProductData? Product);

    private sealed record ShopifyProductGidLookupProductData(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("variants")] ShopifyProductVariantConnection? Variants);

    // SKU branch — node shape carries its own `product` back-reference.

    private sealed record ShopifyProductVariantSkuLookupResponse(
        [property: JsonPropertyName("data")] ShopifyProductVariantSkuLookupData? Data,
        [property: JsonPropertyName("errors")] List<ShopifyGraphQlError>? Errors);

    private sealed record ShopifyProductVariantSkuLookupData(
        [property: JsonPropertyName("productVariants")] ShopifyProductVariantSkuConnection? ProductVariants);

    private sealed record ShopifyProductVariantSkuConnection(
        [property: JsonPropertyName("edges")] List<ShopifyProductVariantSkuEdge> Edges);

    private sealed record ShopifyProductVariantSkuEdge(
        [property: JsonPropertyName("node")] ShopifyProductVariantSkuNode Node);

    private sealed record ShopifyProductVariantSkuNode(
        [property: JsonPropertyName("sku")] string? Sku,
        [property: JsonPropertyName("availableForSale")] bool AvailableForSale,
        [property: JsonPropertyName("price")] ShopifyMoney? Price,
        [property: JsonPropertyName("selectedOptions")] List<ShopifySelectedOption>? SelectedOptions,
        [property: JsonPropertyName("product")] ShopifyProductRefData? Product);

    private sealed record ShopifyProductRefData(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("description")] string? Description);

    // searchProducts.

    private sealed record ShopifyProductSearchResponse(
        [property: JsonPropertyName("data")] ShopifyProductSearchData? Data,
        [property: JsonPropertyName("errors")] List<ShopifyGraphQlError>? Errors);

    private sealed record ShopifyProductSearchData(
        [property: JsonPropertyName("products")] ShopifyProductSearchConnection? Products);

    private sealed record ShopifyProductSearchConnection(
        [property: JsonPropertyName("edges")] List<ShopifyProductSearchEdge> Edges);

    private sealed record ShopifyProductSearchEdge(
        [property: JsonPropertyName("node")] ShopifyProductSearchNode Node);

    private sealed record ShopifyProductSearchNode(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("variants")] ShopifyProductVariantConnection? Variants);

    // getInventory — GID branch. A dedicated node shape (no `price`, no
    // `product` back-reference) — this method requests only the fields
    // InStock/identification need (implementation design §5.1/§5.2).

    private sealed record ShopifyInventoryVariantConnection(
        [property: JsonPropertyName("edges")] List<ShopifyInventoryVariantEdge> Edges);

    private sealed record ShopifyInventoryVariantEdge(
        [property: JsonPropertyName("node")] ShopifyInventoryVariantNode Node);

    private sealed record ShopifyInventoryVariantNode(
        [property: JsonPropertyName("sku")] string? Sku,
        [property: JsonPropertyName("availableForSale")] bool AvailableForSale,
        [property: JsonPropertyName("selectedOptions")] List<ShopifySelectedOption>? SelectedOptions);

    private sealed record ShopifyProductInventoryGidLookupResponse(
        [property: JsonPropertyName("data")] ShopifyProductInventoryGidLookupData? Data,
        [property: JsonPropertyName("errors")] List<ShopifyGraphQlError>? Errors);

    private sealed record ShopifyProductInventoryGidLookupData(
        [property: JsonPropertyName("product")] ShopifyProductInventoryGidLookupProductData? Product);

    private sealed record ShopifyProductInventoryGidLookupProductData(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("variants")] ShopifyInventoryVariantConnection? Variants);

    // getInventory — SKU branch. Node shape carries its own `product`
    // back-reference (id/title only — no `description`, unneeded here).

    private sealed record ShopifyProductVariantInventorySkuLookupResponse(
        [property: JsonPropertyName("data")] ShopifyProductVariantInventorySkuLookupData? Data,
        [property: JsonPropertyName("errors")] List<ShopifyGraphQlError>? Errors);

    private sealed record ShopifyProductVariantInventorySkuLookupData(
        [property: JsonPropertyName("productVariants")] ShopifyProductVariantInventorySkuConnection? ProductVariants);

    private sealed record ShopifyProductVariantInventorySkuConnection(
        [property: JsonPropertyName("edges")] List<ShopifyProductVariantInventorySkuEdge> Edges);

    private sealed record ShopifyProductVariantInventorySkuEdge(
        [property: JsonPropertyName("node")] ShopifyProductVariantInventorySkuNode Node);

    private sealed record ShopifyProductVariantInventorySkuNode(
        [property: JsonPropertyName("sku")] string? Sku,
        [property: JsonPropertyName("availableForSale")] bool AvailableForSale,
        [property: JsonPropertyName("selectedOptions")] List<ShopifySelectedOption>? SelectedOptions,
        [property: JsonPropertyName("product")] ShopifyProductRefData? Product);
}
