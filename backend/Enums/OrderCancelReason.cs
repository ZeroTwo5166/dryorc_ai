namespace backend.Enums;

/// <summary>
/// Mirrors Shopify's OrderCancelReason enum exactly (verified against
/// current shopify.dev Admin GraphQL docs) — the orderCancel mutation
/// requires this as a non-null argument. Do not add/rename/reorder values
/// without re-verifying against Shopify's current documentation first.
/// </summary>
public enum OrderCancelReason
{
    Customer,
    Payment,
    Fraud,
    Inventory,
    StaffError,
    Other
}
