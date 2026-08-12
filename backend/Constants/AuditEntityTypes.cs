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
}