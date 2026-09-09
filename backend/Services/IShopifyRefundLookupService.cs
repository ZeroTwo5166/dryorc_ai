namespace backend.Services;

/// <summary>
/// One requested line item's resolved refund facts, as reported by Shopify
/// for the specific requested quantity — see
/// docs/handoffs/step-refundorder-design.md §8.3/§17 and the dedicated
/// Shopify research (docs/handoffs/step-refundorder-shopify-research.md,
/// Q1/Q2). RefundableQuantity is Shopify's own current
/// LineItem.refundableQuantity (already nets out prior partial refunds
/// server-side — Nivora does not compute it). ResolvedAmount is Shopify's own
/// Order.suggestedRefund(...).refundLineItems[].subtotalSet, already
/// discount/tax-aware for the requested quantity — never
/// requestedQuantity * a per-unit price, and never independently rounded.
/// </summary>
public sealed record ShopifyRefundLineFacts(
    string ExternalLineItemId,
    int RequestedQuantity,
    int RefundableQuantity,
    decimal ResolvedAmount);

/// <summary>
/// The full result of a refundable-quantity/amount preflight lookup — one
/// entry per requested line item, plus the presentment currency Shopify
/// reported the resolved amounts in (design research Q8: presentmentMoney,
/// never shopMoney).
/// </summary>
public sealed record ShopifyRefundPreflightResult(
    IReadOnlyList<ShopifyRefundLineFacts> Lines,
    string CurrencyCode);

/// <summary>
/// Step refundOrder Phase 3 — resolves docs/handoffs/step-refundorder-design.md
/// §8.3's "Refundable line-item quantity/price lookup" capability, per the
/// dedicated Shopify research pass (Q1/Q2). Deliberately a new sibling
/// interface, not a new method on IShopifyOrderLookupService — that file's
/// own body is left untouched (§8.3's own text: "the research resolved the
/// Shopify-side facts, not Nivora's own interface layout"; keeping this
/// separate avoids any risk to cancelOrder's already-independently-verified
/// ownership-lookup code).
///
/// LIMITATION, deliberate and initial (not a bug): requests only the first
/// 250 line items of the order (Order.lineItems(first: 250)) — no pagination
/// is implemented. An order with more than 250 line items containing a
/// requested line item beyond that page would surface as
/// LineItemNotFoundOnOrder, indistinguishable from a genuinely wrong id. A
/// documented, narrow scope-limiting choice for this pass, not a silent gap.
/// </summary>
public interface IShopifyRefundLookupService
{
    /// <summary>
    /// businessId and requestedItems are explicit and caller-supplied — never
    /// read from ICurrentTenantAccessor. externalOrderId is the
    /// already-ownership-verified Shopify order GID (the caller must have
    /// already resolved ownership via IShopifyOrderLookupService before
    /// calling this — this service does not itself verify ownership).
    ///
    /// Every infra-level failure throws (missing/inactive Integration, token
    /// decrypt failure, non-2xx, malformed JSON, top-level GraphQL errors,
    /// order not found) — identical convention to IShopifyOrderLookupService.
    /// Additionally throws if Shopify reports a requested line item id that
    /// does not exist among the order's first 250 line items
    /// (LineItemNotFoundOnOrderException, a distinct, named exception — a
    /// business-fact refusal the caller must translate into
    /// ExecutionRefusalReason, not an infra failure, but not a legitimate
    /// "the amount was resolved" outcome either) or if Shopify's
    /// suggestedRefund response does not carry one entry per requested item
    /// (an ambiguous-response anomaly, per research Q4/Q6's own disclosed
    /// uncertainty about partial-response shapes — treated as an uncaught
    /// InvalidOperationException, never silently mismapped).
    ///
    /// Does NOT validate RequestedQuantity against RefundableQuantity itself
    /// — that comparison, and the resulting ExecutionRefusalReason, is the
    /// caller's (RefundApprovedActionExecutor's) own responsibility, per the
    /// existing codebase convention that a lookup service reports facts and
    /// the executor decides refusals (cf. IShopifyOrderLookupService/
    /// IOrderOwnershipAuthorizationService's own split).
    /// </summary>
    Task<ShopifyRefundPreflightResult> GetRefundPreflightAsync(
        Guid businessId,
        string externalOrderId,
        IReadOnlyList<(string ExternalLineItemId, int Quantity)> requestedItems,
        CancellationToken cancellationToken);
}

/// <summary>
/// Shopify reported a requested ExternalLineItemId that is not among the
/// order's own line items (within this service's documented first-250-item
/// scope limitation) — distinct from every other failure mode this service
/// raises, since a caller-authorized executor needs to map this specifically
/// to a "the requested item does not belong to the target order" refusal
/// (design §18), not a generic infra failure.
/// </summary>
public sealed class LineItemNotFoundOnOrderException(string externalLineItemId)
    : Exception($"Line item '{externalLineItemId}' was not found on the order.")
{
    public string ExternalLineItemId { get; } = externalLineItemId;
}
