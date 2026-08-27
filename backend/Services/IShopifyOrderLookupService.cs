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
    /// </summary>
    Task<VerifiedOrderOwnership> LookupOrderOwnerAsync(
        Guid businessId,
        string externalOrderId,
        CancellationToken cancellationToken);
}
