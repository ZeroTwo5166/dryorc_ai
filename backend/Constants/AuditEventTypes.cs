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

    // Business — AI Business Analyst kill switch (Step 23 Phase 1). Named
    // like BusinessCancelOrderToolEnabled/Disabled above, for the same reason.
    public const string BusinessAiBusinessAnalystEnabled = "BusinessAiBusinessAnalystEnabled";
    public const string BusinessAiBusinessAnalystDisabled = "BusinessAiBusinessAnalystDisabled";

    // Business — Scheduled AI Reports kill switch (Step 24 Phase 1). Named
    // like BusinessAiBusinessAnalystEnabled/Disabled above, for the same reason.
    public const string BusinessScheduledReportsEnabled = "BusinessScheduledReportsEnabled";
    public const string BusinessScheduledReportsDisabled = "BusinessScheduledReportsDisabled";

    // Business — Stale/Inactive Conversation Auto-Close (Step 25 Candidate 2,
    // Phase 1). Named like BusinessScheduledReportsEnabled/Disabled above,
    // for the same reason. BusinessConversationInactivityCloseDaysChanged is
    // a single "changed" event (not an Enabled/Disabled pair) since the
    // underlying field is a bounded integer, not a boolean.
    public const string BusinessConversationAutoCloseEnabled = "BusinessConversationAutoCloseEnabled";
    public const string BusinessConversationAutoCloseDisabled = "BusinessConversationAutoCloseDisabled";
    public const string BusinessConversationInactivityCloseDaysChanged = "BusinessConversationInactivityCloseDaysChanged";

    // Customer
    public const string CustomerFieldUpdated = "CustomerFieldUpdated";
    public const string CustomerDeleted = "CustomerDeleted";
    public const string CustomerDataExported = "CustomerDataExported";

    /// <summary>
    /// GDPR lifecycle design (docs/handoffs/gdpr-lifecycle-design.md §4) —
    /// written by CustomerErasureService on a successful erasure. A new
    /// constant, not a reuse of CustomerDeleted above: the Customer row is
    /// anonymized in place, never deleted, so CustomerDeleted would
    /// misdescribe the operation in the audit trail.
    /// </summary>
    public const string CustomerErased = "CustomerErased";

    /// <summary>
    /// Identity-binding/erasure-integrity design
    /// (docs/handoffs/identity-binding-erasure-integrity-design.md §3.8/§11,
    /// OQ-3; implementation plan Phase 8) — written by
    /// CustomerErasureService.ClearPendingErasureAsync whenever an
    /// OwnerOnly-authorized recovery-clear actually mutates
    /// Customer.PendingErasureAt from non-null to null (never on the
    /// already-null no-op path). ActorType.NivoraBusinessStaff,
    /// ChangedByUserId = the acting Owner, in the same transaction as the
    /// marker clear.
    /// </summary>
    public const string CustomerErasureRecoveryCleared = "CustomerErasureRecoveryCleared";

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

    /// <summary>
    /// Step Shopify-auth-reauth (docs/handoffs/step-shopify-auth-reauth-design.md
    /// §8.3) — OrderCancellation's own terminal Failed audit event, deliberately
    /// scoped to ONLY the auth-classified branch, not every Failed transition
    /// (the pre-existing, un-audited userErrors-driven Failed path is left
    /// exactly as under-audited as it is today, per step-43 §15.1's own
    /// disclosed precedent — not a fix this narrow workstream is chartered to
    /// make). OrderCancelled (declared above) remains a distinct, unrelated
    /// success-shaped event never written by this design.
    /// </summary>
    public const string OrderCancellationFailed = "OrderCancellationFailed";

    /// <summary>
    /// Step Shopify-auth-reauth (design §10) — written against
    /// AuditEntityTypes.Integration whenever a Shopify mutation for that
    /// integration is classified as an authentication/authorization failure
    /// (HTTP 401/403) or a token-decryption failure, alongside the write to
    /// Integration.ReauthorizationRequiredAt. Distinct from
    /// IntegrationConnected/IntegrationDisconnected below — this event
    /// records a credential-health observation, never a lifecycle/
    /// configuration change, and never implies IsActive changed.
    /// </summary>
    public const string IntegrationReauthorizationRequired = "IntegrationReauthorizationRequired";

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

    /// <summary>
    /// Step 25 Candidate 2, Phase 3 (design §6, Decision 4) — written by
    /// ConversationInactivityCloseService on every successful automated
    /// close, ActorType.NivoraSystem, in the same transaction as the
    /// Conversation.Status mutation (Option A — see design §6 for why this
    /// is achievable, not merely best-effort).
    /// </summary>
    public const string ConversationAutoClosed = "ConversationAutoClosed";

    /// <summary>
    /// GDPR lifecycle design (docs/handoffs/gdpr-lifecycle-design.md §2.5/§4)
    /// — written by RetentionEnforcementService on every successful
    /// age-based purge, ActorType.NivoraSystem, no ChangedByUserId, in the
    /// same per-conversation transaction as the claim and the purge.
    /// Deliberately distinct from ConversationErasurePurged below — see
    /// that constant's own doc comment for why a shared event type is
    /// wrong.
    /// </summary>
    public const string ConversationRetentionPurged = "ConversationRetentionPurged";

    /// <summary>
    /// GDPR lifecycle design (docs/handoffs/gdpr-lifecycle-design.md §3.4/§4)
    /// — written by CustomerErasureService for each conversation purged as
    /// part of a customer erasure, ActorType.NivoraBusinessStaff, in the
    /// same per-conversation transaction as the claim and the purge.
    /// Deliberately a separate constant from ConversationRetentionPurged
    /// above, not a shared one — using one event type for both would
    /// silently re-introduce the "can't tell why a conversation was
    /// purged" gap the design's discovery pass flagged.
    /// </summary>
    public const string ConversationErasurePurged = "ConversationErasurePurged";

    /// <summary>
    /// Step 22 Phase 3 — written by ConversationTagCorrectionService on every
    /// successful staff correction of Conversation.Tags, in the same
    /// transaction as the Tags mutation and the
    /// ComputeAndRatchetEffectiveModeAsync call it triggers. Same category as
    /// TagModeMappingChanged/ApprovalApproved/ApprovalDenied — a write path
    /// that directly feeds AI-autonomy mode computation.
    /// </summary>
    public const string ConversationTagsCorrected = "ConversationTagsCorrected";
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

    /// <summary>
    /// Step getOrder (design §5) — written on both a successful getOrder
    /// tool call and an ownership-refusal, never on the ordinary
    /// unverified/not-found/ambiguous refusals (§5's own reasoning: those
    /// are not attempts to access something unauthorized). newValue is
    /// "Success" or "OwnershipVerificationFailed:{reason}", matching
    /// OrderCancelApprovedActionExecutor.WriteUnauthorizedExecutionBlockedAsync's
    /// own ownership-failure marker naming exactly.
    /// </summary>
    public const string OrderLookupPerformed = "OrderLookupPerformed";
}