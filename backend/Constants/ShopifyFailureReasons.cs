namespace backend.Constants;

/// <summary>
/// Step Shopify-auth-reauth (docs/handoffs/step-shopify-auth-reauth-design.md
/// §7.2/§8.2) — the marker prefix a Refund/OrderCancellation.FailureReason
/// carries when ShopifyRefundExecutor/ShopifyOrderCancellationExecutor
/// classified the mutation failure as Shopify authentication/authorization
/// (HTTP 401/403) or a local token-decryption failure, per the locked
/// Decision 7 predicate. Never applied to an ordinary GraphQL userErrors
/// rejection, whose FailureReason is always a Shopify error code/message and
/// therefore never starts with this literal string. Used by
/// RefundApprovedActionExecutor/OrderCancelApprovedActionExecutor to decide
/// whether to also write the IntegrationReauthorizationRequired audit event,
/// without needing any other signal threaded back from the low-level
/// executor.
/// </summary>
public static class ShopifyFailureReasons
{
    public const string ReauthorizationRequiredPrefix = "ShopifyReauthorizationRequired: ";
}
