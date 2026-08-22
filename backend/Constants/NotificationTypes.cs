namespace backend.Constants;

/// <summary>
/// String constants for Notification.Type — an open-ended, growing value
/// set (per NIVORA_DEVELOPMENT_PLAN_V2.md Section 11's rule: string +
/// static class, not an enum, so new values need no migration), same
/// pattern as AuditEventTypes/HandoffReasons.
///
/// Whenever a value is added here, NotificationCategoryMap and
/// CK_Notifications_CategoryTypeConsistency (AppDbContext.cs) must both be
/// updated in the same change — see NotificationCategoryMap's doc comment.
///
/// Deliberately excludes any Refund-linked type (e.g. a future
/// RefundRequiresApproval/RefundAutoApproved) — Refund has no TicketId and
/// only an optional ConversationId, so it doesn't fit this schema's
/// Conversation/Ticket-only reference model; adding one would need a third
/// named FK, out of scope for this pass.
/// </summary>
public static class NotificationTypes
{
    // Team
    public const string TeamMemberJoined = "TeamMemberJoined";
    public const string StaffRoleChanged = "StaffRoleChanged";
    public const string StaffAccessRevoked = "StaffAccessRevoked";

    // Customer
    public const string ConversationAssigned = "ConversationAssigned";
    public const string TicketSlaWarning = "TicketSlaWarning";
    public const string CustomerReplied = "CustomerReplied";
    public const string NewConversation = "NewConversation";
    public const string ConversationEscalated = "ConversationEscalated";

    // System
    public const string KnowledgeImportCompleted = "KnowledgeImportCompleted";
    public const string IntegrationSyncCompleted = "IntegrationSyncCompleted";
}
