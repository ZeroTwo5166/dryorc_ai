namespace backend.Services;

/// <summary>
/// Step 20. Shopify's own authoritative statement of who owns a given
/// order, or that the order has no owner (a guest checkout). The
/// constructor is internal, restricted to the backend assembly (with
/// InternalsVisibleTo granted to backend.Tests only) — no controller, HTTP
/// model-binder, or AI/Claude tool-call argument path can ever construct
/// one. Only IShopifyOrderLookupService, and only after a fully successful,
/// unambiguous Shopify GraphQL response, may construct one.
///
/// This is a real, compiler-enforced guarantee against external/HTTP/
/// AI-tool-argument forgery. It does NOT fully prevent a careless future
/// same-assembly caller from constructing one from unchecked data — that
/// residual gap is a code-review/documentation-discipline matter, not
/// something the type system alone closes. Do not describe this type as
/// unforgeable without that caveat.
/// </summary>
public sealed class VerifiedOrderOwnership
{
    /// <summary>
    /// The Shopify order GID as confirmed by the lookup, echoed back so a
    /// caller can assert it matches what it originally requested. This
    /// service's own AuthorizeAsync counterpart (IOrderOwnershipAuthorizationService)
    /// does not perform this comparison itself — its signature takes only
    /// businessId/customerId/orderOwnership, with no separate "expected
    /// order id" parameter — so any consistency check between this value
    /// and the order id a caller originally asked for is the future
    /// orchestration layer's responsibility, not this unit's.
    /// </summary>
    public string ExternalOrderId { get; }

    /// <summary>
    /// The Shopify customer GID (gid://shopify/Customer/&lt;id&gt;) that owns
    /// this order, in the canonical representation Customer.ExternalId is
    /// also expected to use — or null if Shopify reports the order has no
    /// owner (order.customer == null, a real, documented guest-checkout
    /// state, not an error).
    /// </summary>
    public string? OwnerExternalCustomerId { get; }

    internal VerifiedOrderOwnership(string externalOrderId, string? ownerExternalCustomerId)
    {
        ExternalOrderId = externalOrderId;
        OwnerExternalCustomerId = ownerExternalCustomerId;
    }
}

/// <summary>
/// Shopify's own authoritative cancellation state for an order — used by
/// OrderCancellationReconciliationService as ground truth, in preference to
/// tracking/polling the orderCancel mutation's own async Job id (see the
/// pending-reconciliation design handoff for why: Order.cancelledAt answers
/// "is this order actually cancelled" directly, without needing a stored
/// job reference or inferring success from Job.done, which does not by
/// itself distinguish a successful cancellation from a failed one).
/// Same internal-constructor discipline as VerifiedOrderOwnership.
/// </summary>
public sealed class ShopifyOrderCancellationState
{
    public string ExternalOrderId { get; }

    /// <summary>Null if Shopify reports the order has not been cancelled.</summary>
    public DateTimeOffset? CancelledAt { get; }

    internal ShopifyOrderCancellationState(string externalOrderId, DateTimeOffset? cancelledAt)
    {
        ExternalOrderId = externalOrderId;
        CancelledAt = cancelledAt;
    }
}

/// <summary>
/// Step getOrder (design §2.1a) — thrown by any of ShopifyOrderLookupService's
/// read-only-tool-facing methods (LookupOrderOwnerForReadToolAsync,
/// ResolveOrderNameAsync, GetOrderDetailAsync) when Shopify responds with
/// HTTP 401/403, distinct from the plain InvalidOperationException every
/// other infra failure throws. GetOrderTool catches specifically this type
/// to route to its Group D refusal — ToolDispatchService's generic catch has
/// no hook to write Integration.ReauthorizationRequiredAt, so that write
/// happens inline in the throwing method itself, before this is raised.
/// Deliberately does NOT extend InvalidOperationException — callers must be
/// able to catch this type specifically without also catching every other
/// infra-failure throw from the same methods.
/// </summary>
public sealed class ShopifyAuthFailureException()
    : Exception("Shopify reported an authentication/authorization failure (401/403).");

/// <summary>
/// Step getOrder (design §2.1) — the three ordinary outcomes of resolving a
/// customer-typed order name/number against Shopify's orders(query: "name:...")
/// search. Not a VerifiedOrderOwnership-style internal-constructor type: this
/// result carries no authorization claim, only "did Shopify's search return
/// exactly one match" — a fully public record is safe to construct anywhere
/// (e.g. in tests) without weakening any security boundary.
/// </summary>
public enum OrderNameResolutionOutcome
{
    Resolved,
    NotFound,
    Ambiguous,
}

/// <summary>
/// ResolvedOrderGid is non-null if and only if Outcome == Resolved — callers
/// (GetOrderTool) must switch on Outcome, never assume ResolvedOrderGid's
/// nullability alone communicates the outcome.
/// </summary>
public sealed record OrderNameResolutionResult(OrderNameResolutionOutcome Outcome, string? ResolvedOrderGid);

/// <summary>
/// Step getOrder (design §2.2/§2.2a) — the fields GetOrderTool discloses to
/// the AI for a support-facing "where's my order" answer. Deliberately
/// excludes billing/shipping address, phone, payment method detail, customer
/// GID, and any other PII beyond what that question needs — only the fields
/// named here are ever read out of Shopify's response, so nothing else can
/// leak by omission-failure. Tracking is a list, not a singular
/// number/URL pair: an order can have multiple fulfillments (partial
/// shipments), each with multiple tracking entries (multi-package
/// shipments) — see design §2.2a for why every entry is preserved rather
/// than selecting or dropping any of them.
/// </summary>
public sealed record OrderDetailResult(
    string Name,
    string? FinancialStatus,
    string? FulfillmentStatus,
    DateTimeOffset? CancelledAt,
    IReadOnlyList<OrderLineItemDetail> LineItems,
    IReadOnlyList<OrderTrackingInfo> Tracking);

/// <summary>Price is LineItem.discountedUnitPriceSet.presentmentMoney.amount — the
/// post-discount, presentment-currency per-unit price, reusing the same
/// convention step-refundorder-shopify-research.md §2.3/Q8 already
/// established for refundOrder, not an independent choice.</summary>
public sealed record OrderLineItemDetail(string Title, int Quantity, string? Price);

/// <summary>One Fulfillment.trackingInfo[] entry. Either field may be null —
/// Shopify's own FulfillmentTrackingInfo type declares both nullable.</summary>
public sealed record OrderTrackingInfo(string? Number, string? Url);

/// <summary>
/// Step 20. Resolves a business's Shopify order to its authoritative owner
/// via the Shopify Admin GraphQL API, using Nivora's own authenticated
/// integration credentials — never a caller-supplied owner identity.
///
/// LIMITATION, deliberate and initial (not a bug): Shopify's Admin API
/// exposes only the last 60 days of a shop's orders by default. This
/// service does not request the read_all_orders scope (a manually-reviewed
/// Shopify app-review process, out of scope for this unit) and therefore
/// cannot verify ownership for orders outside that window. Shopify's
/// order(id:) query does not appear to distinguish "order does not exist"
/// from "order exists but is outside the accessible window" — both surface
/// identically as a "not found" failure from this service. Revisiting this
/// is a separate, deliberate future product decision.
/// </summary>
public interface IShopifyOrderLookupService
{
    /// <summary>
    /// businessId is explicit and caller-supplied — never read from
    /// ICurrentTenantAccessor, which is structurally bound to
    /// IHttpContextAccessor and unusable by a non-HTTP caller such as a
    /// future AI tool-dispatch invocation. externalOrderId is the
    /// untrusted-but-legitimate Shopify order GID — the value an AI tool
    /// call is allowed to supply; the order's owner is never taken from
    /// this or any other caller input.
    ///
    /// Every failure path throws (missing/inactive Integration, token
    /// decrypt failure, HTTP non-2xx, GraphQL errors[], malformed JSON,
    /// order not found, timeout, cancellation) — there is no return value
    /// representing a degraded or ambiguous lookup, and no failure branch
    /// ever constructs a VerifiedOrderOwnership.
    ///
    /// getOrder (design §1.4/§2.1a) deliberately does NOT call this method —
    /// see LookupOrderOwnerForReadToolAsync below.
    /// </summary>
    Task<VerifiedOrderOwnership> LookupOrderOwnerAsync(
        Guid businessId,
        string externalOrderId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Same caller-input contract and same fail-closed-via-throw discipline
    /// as LookupOrderOwnerAsync — added for OrderCancellationReconciliationService,
    /// does not modify or share code with LookupOrderOwnerAsync's own query.
    /// </summary>
    Task<ShopifyOrderCancellationState> GetCancellationStateAsync(
        Guid businessId,
        string externalOrderId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Step getOrder (design §2.1a) — same query, same credential
    /// resolution, and same VerifiedOrderOwnership return type as
    /// LookupOrderOwnerAsync above, added as a separate method rather than
    /// reusing it: this design needs a 401/403 Shopify response to raise
    /// ShopifyAuthFailureException specifically (so GetOrderTool can route it
    /// to a reauthorization-safe refusal and write
    /// Integration.ReauthorizationRequiredAt), which LookupOrderOwnerAsync's
    /// existing, already-audited body has no branch for and must not be
    /// edited to add — see the design doc's §1.4 for the full reasoning.
    /// Every other failure path (missing/inactive Integration, token
    /// decrypt, non-2xx other than 401/403, malformed JSON, GraphQL
    /// errors[], order not found) throws InvalidOperationException, exactly
    /// as LookupOrderOwnerAsync does — this method changes behavior for the
    /// 401/403 case only.
    /// </summary>
    Task<VerifiedOrderOwnership> LookupOrderOwnerForReadToolAsync(
        Guid businessId,
        string externalOrderId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Step getOrder (design §2.1) — resolves a customer-typed order
    /// name/number (e.g. "#1001") to a canonical Shopify Order GID via
    /// orders(first: 2, query: "name:&lt;value&gt;"). Unlike every other
    /// method on this interface, "zero matches" and "more than one match"
    /// are ordinary, expected outcomes here (a typo, an ambiguous partial
    /// number) — not infrastructure anomalies — so they are returned as
    /// OrderNameResolutionOutcome.NotFound/.Ambiguous, never thrown. Genuine
    /// infrastructure failure (missing/inactive Integration, token decrypt,
    /// non-2xx other than 401/403, malformed JSON, GraphQL errors[]) still
    /// throws InvalidOperationException; a 401/403 throws
    /// ShopifyAuthFailureException, matching LookupOrderOwnerForReadToolAsync.
    ///
    /// UNVERIFIED AGAINST LIVE SHOPIFY (design §2.1's OPEN QUESTION, Q3 in
    /// the research doc): whether the query variable should carry the raw
    /// customer-typed value (including a leading '#'), a '#'-stripped
    /// value, or a quoted value was left as a required empirical check
    /// before shipping — no page in Shopify's own documentation exercises
    /// the '#'-prefixed case. This implementation sends the raw value
    /// unmodified (matching this codebase's established "accept raw text,
    /// no client-side normalization" convention, e.g.
    /// OrderCancelProposalTool's orderId parameter) as the best-supported
    /// default, NOT as a confirmed-correct behavior — this specific choice
    /// has not been validated against a real connected Shopify shop and
    /// must be before this tool is enabled for a real business. See the
    /// getOrder implementation session's own report for why: no active dev
    /// Shopify integration existed to test against at implementation time.
    /// </summary>
    Task<OrderNameResolutionResult> ResolveOrderNameAsync(
        Guid businessId,
        string orderName,
        CancellationToken cancellationToken);

    /// <summary>
    /// Step getOrder (design §2.2) — order(id:) with the fields
    /// OrderDetailResult needs. Called only after ownership has already been
    /// authorized (design §0/Decision 2's sequence), so unlike
    /// ResolveOrderNameAsync, "not found" here is a genuine anomaly (the
    /// order existed moments ago in the ownership call) and throws
    /// InvalidOperationException, exactly as LookupOrderOwnerAsync does for
    /// the same case. A 401/403 throws ShopifyAuthFailureException, matching
    /// LookupOrderOwnerForReadToolAsync/ResolveOrderNameAsync.
    ///
    /// fulfillments(first: 10) { trackingInfo { number url } } is included
    /// UNVERIFIED against a live shop under the currently-granted six scopes
    /// (design §2.2's OPEN QUESTION, Q11 in the research doc) — no page
    /// reached in that research confirmed or ruled out a field-level scope
    /// requirement beyond read_orders for fulfillment/tracking data
    /// specifically. Must be confirmed against a real connected shop before
    /// this tool is enabled for a real business; if it surfaces a genuine
    /// Shopify permission error, that becomes an owner decision (a new
    /// scope request), not something to silently work around here.
    /// </summary>
    Task<OrderDetailResult> GetOrderDetailAsync(
        Guid businessId,
        string externalOrderId,
        CancellationToken cancellationToken);
}
