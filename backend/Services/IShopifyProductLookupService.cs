namespace backend.Services;

/// <summary>
/// Step Group B (§2.3 of this document — corrected cross-reference, fourth
/// independent verification pass: this originally, incorrectly, read
/// "design §2.3," which would point to the locked design doc's §2, a flat
/// list with no subsections) — the coarse in-stock/out-of-stock signal
/// (Boolean, ProductVariant.availableForSale) plus the identifying/
/// descriptive fields locked design §8 asked for, for one variant — the
/// single representative variant a product's ProductSummaryResult carries
/// (owner Decision 4, §11.1). An earlier version of this design carried a
/// capped list of these per product instead; the project owner rejected
/// that expansion and confirmed locked design §8's original "Primary
/// variant" (singular) framing — see the implementation design's §2.3 own
/// revision note for the full resolution record.
/// </summary>
public sealed record ProductVariantSummary(
    string? Sku,
    string? Price,
    bool AvailableForSale,
    IReadOnlyList<ProductVariantOptionValue> Options);

/// <summary>One ProductVariant.selectedOptions[] entry (e.g. "Size" / "Medium").</summary>
public sealed record ProductVariantOptionValue(string Name, string Value);

/// <summary>
/// Shared output shape for both getProduct and searchProducts (locked
/// design §8) — a single product's identifying/descriptive fields plus its
/// single representative variant (owner Decision 4, §11.1/§2.3). Nullable:
/// a product could in principle return zero variants from the
/// variants(first: 1) selection (§2.4), even though Shopify requires every
/// real product to have at least one — defensive nullability, not an
/// expected case. No image/media field (OWNER DECISION 2, resolved).
/// </summary>
public sealed record ProductSummaryResult(
    string Id,
    string Title,
    string? Description,
    ProductVariantSummary? Variant);

/// <summary>
/// Step Group B (design §4.2/§10) — zero matches and more than one match
/// are ordinary, expected outcomes for getProduct's SKU branch (Shopify
/// does not enforce SKU uniqueness, research doc Q3) and for its GID
/// branch's "product not found" case (design §10 does not carve out an
/// exception for GID) — never thrown, mirroring OrderNameResolutionOutcome's
/// own precedent exactly.
/// </summary>
public enum ProductLookupOutcome { Resolved, NotFound, Ambiguous }

/// <summary>
/// Product is non-null if and only if Outcome == Resolved. GetProductByGidAsync
/// only ever returns Resolved or NotFound — a direct id lookup cannot be
/// ambiguous by construction — but shares this same type with
/// GetProductBySkuAsync (which can return all three) for interface
/// consistency, mirroring how getOrder's own GID branch shares
/// OrderNameResolutionResult's shape conceptually even though it skips
/// calling the method that produces one.
/// </summary>
public sealed record ProductLookupResult(ProductLookupOutcome Outcome, ProductSummaryResult? Product);

/// <summary>
/// searchProducts (design §6/§8) — an empty Products list is itself a
/// successful outcome, never an error; there is no separate "not found"
/// outcome for a search, unlike a single-identifier lookup.
/// </summary>
public sealed record ProductSearchResult(IReadOnlyList<ProductSummaryResult> Products);

/// <summary>
/// Step Group B. Resolves Shopify catalog data (Product/ProductVariant) via
/// the Shopify Admin GraphQL API, using Nivora's own authenticated
/// integration credentials — never a caller-supplied owner identity. New
/// sibling service to IShopifyOrderLookupService, not an extension of it —
/// catalog and order data are different Shopify object families with no
/// shared method bodies to reuse (implementation design §4). Tenant-scoped
/// only — no Level 2 verification or ownership authorization applies to
/// catalog reads (locked design §3, implementation design §1 item 2).
/// </summary>
public interface IShopifyProductLookupService
{
    /// <summary>
    /// §2.4 of the implementation design (corrected cross-reference, sixth
    /// independent verification pass — this originally, incorrectly, read
    /// "design §2.4") — GID-branch query. NotFound (product(id:) returned
    /// null), not thrown — design §10. Genuine infra failure throws
    /// InvalidOperationException; 401/403 throws the existing
    /// ShopifyAuthFailureException (IShopifyOrderLookupService.cs), writing
    /// Integration.ReauthorizationRequiredAt inline first, same convention
    /// as every existing method on ShopifyOrderLookupService.
    /// </summary>
    Task<ProductLookupResult> GetProductByGidAsync(
        Guid businessId,
        string productGid,
        CancellationToken cancellationToken);

    /// <summary>
    /// §2.4 of the implementation design (corrected cross-reference, sixth
    /// independent verification pass — this originally, incorrectly, read
    /// "design §2.4") — SKU-branch query (§2.1's chosen
    /// productVariants(query: "sku:...") path, confirmed as NEW OWNER
    /// DECISION 3). Same outcome/throw discipline as GetProductByGidAsync,
    /// plus a real Ambiguous case: Shopify does not enforce SKU uniqueness
    /// (research doc Q3), so a SKU matching more than one variant — whether
    /// of the same product or of different products — is refused rather
    /// than silently resolved to a guess.
    /// </summary>
    Task<ProductLookupResult> GetProductBySkuAsync(
        Guid businessId,
        string sku,
        CancellationToken cancellationToken);

    /// <summary>
    /// §2.4 of the implementation design (corrected cross-reference, sixth
    /// independent verification pass — this originally, incorrectly, read
    /// "design §2.4") — searchProducts query. Always succeeds with a
    /// (possibly empty) Products list for the ordinary case; genuine infra
    /// failure and 401/403 follow the same throw discipline as the other
    /// two methods.
    /// </summary>
    Task<ProductSearchResult> SearchProductsAsync(
        Guid businessId,
        string query,
        CancellationToken cancellationToken);
}
