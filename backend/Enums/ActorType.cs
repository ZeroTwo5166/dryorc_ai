namespace backend.Enums;

/// <summary>
/// Records the origin of the request that caused Nivora's backend to act
/// — never who "did" it. Nivora's backend is always the actual executor;
/// Claude never has direct DB/API access, and business systems never
/// write to Nivora's DB directly either. Used by AuditLog.Actor,
/// FlaggedCustomerActivityLog.Actor, and ConversationHandoff.ActorType.
/// </summary>
public enum ActorType
{
    /// <summary>A logged-in staff member (Owner/Admin/Agent) took the action.</summary>
    NivoraBusinessStaff,

    /// <summary>The business's own system (e.g. a signed-token handoff from
    /// their storefront) triggered the action.</summary>
    BusinessInternalSystem,

    /// <summary>A scheduled/background job (retention cleanup,
    /// reconciliation) triggered the action.</summary>
    NivoraSystem,

    /// <summary>Claude, via a tool call within a conversation, triggered
    /// the request.</summary>
    Ai,

    /// <summary>Step 21: an anonymous widget visitor directly triggered the
    /// request over HTTP (e.g. an OTP verification attempt) — distinct from
    /// BusinessInternalSystem, which is the business's own backend system,
    /// not the end customer themselves.</summary>
    Customer
}