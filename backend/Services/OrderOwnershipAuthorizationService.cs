using backend.Data;
using backend.Enums;
using Microsoft.EntityFrameworkCore;

namespace backend.Services;

/// <summary>
/// See IOrderOwnershipAuthorizationService for the full contract and
/// security posture. IgnoreQueryFilters() + explicit BusinessId, same
/// non-HTTP-safe posture as ConversationModeService/WidgetConversationService
/// — this service has no HTTP-request-scoped caller guaranteeing the right
/// tenant context.
///
/// customerId == Guid.Empty is treated as "no verified customer supplied"
/// (NoVerifiedCustomer) — the interface's customerId parameter is a
/// non-nullable Guid, so this is the only representable form of "the caller
/// has no customer to check" without changing the already-decided
/// interface shape.
///
/// OrderIdentifierMismatch (defined on OwnershipCheckFailureReason) is
/// deliberately never returned by this service: AuthorizeAsync's signature
/// carries no separate "order id originally requested" parameter to compare
/// orderOwnership.ExternalOrderId against — see VerifiedOrderOwnership's own
/// doc comment. That comparison, if performed, is the future orchestration
/// layer's responsibility.
/// </summary>
public class OrderOwnershipAuthorizationService(AppDbContext db) : IOrderOwnershipAuthorizationService
{
    public async Task<OwnershipCheckResult> AuthorizeAsync(
        Guid businessId, Guid customerId, VerifiedOrderOwnership orderOwnership, CancellationToken cancellationToken)
    {
        if (customerId == Guid.Empty)
        {
            return new OwnershipCheckResult(false, OwnershipCheckFailureReason.NoVerifiedCustomer);
        }

        var customer = await db.Customers
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == customerId && c.BusinessId == businessId, cancellationToken);

        if (customer is null)
        {
            // Also covers the cross-tenant case: a customer that exists but
            // in a different BusinessId simply never matches this predicate
            // — fails closed identically to "doesn't exist at all."
            return new OwnershipCheckResult(false, OwnershipCheckFailureReason.CustomerNotFound);
        }

        if (customer.IsAnonymized)
        {
            return new OwnershipCheckResult(false, OwnershipCheckFailureReason.CustomerAnonymized);
        }

        if (customer.ExternalId is null)
        {
            return new OwnershipCheckResult(false, OwnershipCheckFailureReason.CustomerNotLinkedToExternalIdentity);
        }

        if (orderOwnership.OwnerExternalCustomerId is null)
        {
            return new OwnershipCheckResult(false, OwnershipCheckFailureReason.NoOrderOwner);
        }

        if (!string.Equals(customer.ExternalId, orderOwnership.OwnerExternalCustomerId, StringComparison.Ordinal))
        {
            return new OwnershipCheckResult(false, OwnershipCheckFailureReason.OwnerMismatch);
        }

        return new OwnershipCheckResult(true, OwnershipCheckFailureReason.None);
    }
}
