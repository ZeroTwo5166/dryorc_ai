namespace backend.Constants;

/// <summary>
/// String constants for AuditLog.EventType. Use these instead of raw
/// string literals so typos are caught at compile time. Add new values
/// here as new auditable actions come online — no migration required,
/// since the underlying column is just varchar.
/// </summary>
public static class AuditEventTypes
{
    // Business
    public const string PlanChanged = "PlanChanged";
    public const string RetentionChanged = "RetentionChanged";
    public const string StatusChanged = "StatusChanged";

    // Business — Cancel Order Tool flag (step 49b). Named like
    // CustomerExtendedLoggingEnabled/Disabled below (its closest exercised
    // precedent), prefixed with the entity name to avoid reading like the
    // Business.CancelOrderToolEnabled field itself.
    public const string BusinessCancelOrderToolEnabled = "BusinessCancelOrderToolEnabled";
    public const string BusinessCancelOrderToolDisabled = "BusinessCancelOrderToolDisabled";

    // Customer
    public const string CustomerFieldUpdated = "CustomerFieldUpdated";
    public const string CustomerDeleted = "CustomerDeleted";
    public const string CustomerDataExported = "CustomerDataExported";

    // Commerce write actions
    public const string RefundIssued = "RefundIssued";
    public const string OrderCancelled = "OrderCancelled";

    // Step refundOrder Phase 3 (design §15.2) — refundOrder's own terminal
    // state transitions are audited, a deliberate divergence from
    // cancelOrder's own under-audited shipped precedent (§15.1: cancelOrder
    // writes no audit row for its own terminal Confirmed/Failed transition,
    // only for ExecutionClaimed). RefundIssued (declared above) is this
    // constant's own first real caller, on Pending -> Confirmed.
    public const string RefundFailed = "RefundFailed";

    // Verification / authorization
    public const string Level2VerificationSucceeded = "Level2VerificationSucceeded";
    public const string Level2VerificationFailed = "Level2VerificationFailed";
    public const string UnauthorizedActionBlocked = "UnauthorizedActionBlocked";

    // Step 22 AI Action Safety Model — Approval lifecycle
    public const string ApprovalApproved = "ApprovalApproved";
    public const string ApprovalDenied = "ApprovalDenied";
    public const string ExecutionClaimed = "ExecutionClaimed";

    // Staff / access
    public const string RoleChanged = "RoleChanged";
    public const string StaffAccessRevoked = "StaffAccessRevoked";
    public const string IntegrationConnected = "IntegrationConnected";
    public const string IntegrationDisconnected = "IntegrationDisconnected";
    public const string TagModeMappingChanged = "TagModeMappingChanged";

    // Conversation / ticket
    public const string ConversationModeOverridden = "ConversationModeOverridden";
    public const string TicketReopened = "TicketReopened";
    public const string TicketCreated = "TicketCreated";
    public const string TicketClaimed = "TicketClaimed";
    public const string TicketUnclaimed = "TicketUnclaimed";
    public const string TicketResolved = "TicketResolved";
    public const string TicketPriorityChanged = "TicketPriorityChanged";

    // Knowledge base
    public const string KnowledgeDocumentDeleted = "KnowledgeDocumentDeleted";
    public const string KnowledgeDocumentUpdated = "KnowledgeDocumentUpdated";

    // Step 14 — identity linking
    public const string IdentityLinked = "IdentityLinked";
    public const string IdentityRevoked = "IdentityRevoked";

    // Extended logging (customer-level flag)
    public const string CustomerExtendedLoggingEnabled = "CustomerExtendedLoggingEnabled";
    public const string CustomerExtendedLoggingDisabled = "CustomerExtendedLoggingDisabled";
}