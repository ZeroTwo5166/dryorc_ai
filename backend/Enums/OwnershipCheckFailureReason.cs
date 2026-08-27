namespace backend.Enums;

/// <summary>
/// Step 20. Every reason IOrderOwnershipAuthorizationService.AuthorizeAsync
/// can deny a (CustomerId, VerifiedOrderOwnership) pair. There is no value
/// representing an ambiguous/unknown outcome — every branch that isn't a
/// clean match maps to one of these, and None is reserved for the
/// successful case (OwnershipCheckResult.IsAuthorized == true).
/// </summary>
public enum OwnershipCheckFailureReason
{
    None,
    NoVerifiedCustomer,
    CustomerNotFound,
    CustomerAnonymized,
    CustomerNotLinkedToExternalIdentity,
    NoOrderOwner,
    OwnerMismatch,
    OrderIdentifierMismatch,
}
