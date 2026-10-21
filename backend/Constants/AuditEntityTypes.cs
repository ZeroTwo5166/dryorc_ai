namespace backend.Constants;

/// <summary>
/// String constants for AuditLog.EntityType. Use these instead of raw
/// string literals so typos are caught at compile time. Add new values
/// here as new auditable entities come online — no migration required,
/// since the underlying column is just varchar.
/// </summary>
public static class AuditEntityTypes
{
    public const string Business = "Business";
    public const string Customer = "Customer";
    public const string Order = "Order";
    public const string Ticket = "Ticket";
    public const string Integration = "Integration";
    public const string KnowledgeDocument = "KnowledgeDocument";
    public const string Conversation = "Conversation";
    public const string ExternalIdentity = "ExternalIdentity";
    public const string TagModeMapping = "TagModeMapping";
    public const string ApplicationUser = "ApplicationUser";
    public const string Approval = "Approval";

    /// <summary>Step refundOrder Phase 3 (design §15.2) — the Refund row
    /// itself, for its own terminal Confirmed/Failed audit rows. Distinct
    /// from Order (a Shopify-side concept) and from Approval (the claim
    /// audit row already uses AuditEntityTypes.Approval).</summary>
    public const string Refund = "Refund";

    /// <summary>Step Shopify-auth-reauth (design §8.3/§10) — the
    /// OrderCancellation row itself, for its own auth-classified terminal
    /// Failed audit row. Mirrors Refund above; distinct from Order and
    /// Approval for the same reasons.</summary>
    public const string OrderCancellation = "OrderCancellation";

    /// <summary>Staff invitations (DEC D11) — the StaffInvitation row, for
    /// its created, resent, revoked and accepted audit rows.</summary>
    public const string StaffInvitation = "StaffInvitation";
}