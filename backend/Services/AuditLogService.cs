using backend.Data;
using backend.Enums;
using backend.Models;

namespace backend.Services;

public class AuditLogService(AppDbContext db) : IAuditLogService
{
    public Task LogAsync(
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
        CancellationToken cancellationToken = default)
    {
        // Add only — no SaveChangesAsync(), no transaction of its own. See
        // this class's interface doc comment for why that's required, not
        // an oversight.
        db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            EntityType = entityType,
            EntityId = entityId,
            EventType = eventType,
            Actor = actor,
            ChangedByUserId = changedByUserId,
            OldValue = oldValue,
            NewValue = newValue,
            ConversationId = conversationId,
            CorrelationId = correlationId,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        return Task.CompletedTask;
    }
}
