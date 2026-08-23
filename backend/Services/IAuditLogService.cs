using backend.Enums;

namespace backend.Services;

/// <summary>
/// Step 13A — minimal AuditLog-writing service, deliberately narrower than
/// Step 29's eventual scope: no PII-allowlist validation, no generalized
/// payload policy infrastructure. Exists solely to give
/// TagModeMappingsController (the first production feature with a locked
/// AuditLog requirement — confirmed nothing writes to AuditLog anywhere
/// else in this codebase) something real to call.
/// </summary>
public interface IAuditLogService
{
    /// <summary>
    /// Adds one AuditLog row to the CALLER's own scoped AppDbContext change
    /// tracker. Deliberately does NOT call SaveChangesAsync() and does NOT
    /// open its own transaction — the caller's own subsequent
    /// SaveChangesAsync() is what persists this row, together with
    /// whatever else that caller is doing in the same unit of work. This is
    /// required, not incidental: TagModeMapping.cs's own doc comment
    /// requires the mapping mutation and its AuditLog entry to commit in
    /// the same transaction.
    /// </summary>
    Task LogAsync(
        Guid businessId,
        string entityType,
        string entityId,
        string eventType,
        ActorType actor,
        Guid? changedByUserId = null,
        string? oldValue = null,
        string? newValue = null,
        Guid? conversationId = null,
        Guid? correlationId = null,
        CancellationToken cancellationToken = default);
}
