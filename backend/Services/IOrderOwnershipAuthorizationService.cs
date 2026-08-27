using backend.Enums;

namespace backend.Services;

public record OwnershipCheckResult(bool IsAuthorized, OwnershipCheckFailureReason Reason);

/// <summary>
/// Step 20. Independently decides whether a Nivora customer actually owns a
/// Shopify-confirmed order, given an already-trusted VerifiedOrderOwnership
/// (never a raw string). Depends only on AppDbContext — does not depend on
/// IShopifyOrderLookupService and never calls Shopify itself.
///
/// Level 2 verification and order-ownership authorization are independent
/// security properties. This service has no dependency on
/// Conversation.Level2VerifiedUntil or ExternalIdentity — it only ever
/// reasons about a specific (CustomerId, VerifiedOrderOwnership) pair. A
/// Level-2-verified conversation must still be denied access to another
/// customer's order; nothing here lets a Level-2 check substitute for this
/// DB/Shopify-derived comparison.
///
/// Fails closed: every non-match case (no customer, customer not found,
/// cross-tenant customer, anonymized customer, missing ExternalId, no order
/// owner, owner mismatch) returns IsAuthorized == false with a specific
/// OwnershipCheckFailureReason. No path defaults to authorized.
/// </summary>
public interface IOrderOwnershipAuthorizationService
{
    /// <summary>
    /// businessId and customerId are explicit and caller-supplied — never
    /// read from ICurrentTenantAccessor. orderOwnership must have been
    /// constructed by IShopifyOrderLookupService; there is no overload or
    /// parameter here that accepts a caller-supplied owner identity string.
    /// </summary>
    Task<OwnershipCheckResult> AuthorizeAsync(
        Guid businessId,
        Guid customerId,
        VerifiedOrderOwnership orderOwnership,
        CancellationToken cancellationToken);
}
