using backend.Constants;
using backend.Data;
using backend.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace backend.Services;

public class UnauthorizedActionAuditWriter(
    IServiceScopeFactory scopeFactory,
    ILogger<UnauthorizedActionAuditWriter> logger) : IUnauthorizedActionAuditWriter
{
    /// <summary>
    /// Stable, alertable EventId for a failed (lost) rule (c) audit write.
    /// Every failure path logs exactly this event, at Error level.
    /// </summary>
    public static readonly EventId WriteFailedEventId = new(27001, "UnauthorizedActionAuditWriteFailed");

    private const string ConversationBusinessMismatchMarker = "ConversationBusinessMismatch";

    public async Task WriteConversationBusinessMismatchBlockedAsync(Guid businessId)
    {
        try
        {
            // Fresh DI scope, never the caller's — IAuditLogService.LogAsync
            // only adds to ITS OWN scoped AppDbContext's change tracker and
            // deliberately never calls SaveChangesAsync (see its own
            // interface doc comment); resolving both from this new scope,
            // then calling SaveChangesAsync here, is what actually persists
            // the row independently of the caller's unit of work (v11 §5,
            // rule (c), step 2).
            using var scope = scopeFactory.CreateScope();
            var auditLogService = scope.ServiceProvider.GetRequiredService<IAuditLogService>();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            await auditLogService.LogAsync(
                businessId: businessId,
                entityType: AuditEntityTypes.Business,
                entityId: businessId.ToString(),
                eventType: AuditEventTypes.UnauthorizedActionBlocked,
                actor: ActorType.Customer,
                oldValue: null,
                newValue: ConversationBusinessMismatchMarker,
                conversationId: null);

            // v11 §5's "Cancellation" bullet gives no timeout for the rule (c)
            // audit write. This writer's own bounded timeout (D2) is a
            // post-v11 owner decision (Slice 2), pending a v11 errata. Value
            // and exact coverage: see IsolatedWriteLimits.WriteTimeout.
            using var timeoutCts = new CancellationTokenSource(IsolatedWriteLimits.WriteTimeout);
            await db.SaveChangesAsync(timeoutCts.Token);
        }
        catch (Exception ex)
        {
            // Fail open with a dedicated alertable event — a post-v11 owner
            // decision (Slice 2), pending a v11 errata; v11 does not specify
            // audit-write failure behavior. Never throws: the caller's own
            // throw (v11 §5, rule (c), step 3) is what blocks the provider
            // call, and this writer must never mask or replace it. A lost
            // audit row always leaves this event behind, carrying exactly
            // these four fields and nothing else (no exception object).
            logger.LogError(WriteFailedEventId,
                "UnauthorizedActionBlocked audit row lost: isolated write failed. BusinessId={BusinessId} EventType={EventType} NewValue={NewValue} ExceptionType={ExceptionType}",
                businessId, AuditEventTypes.UnauthorizedActionBlocked, ConversationBusinessMismatchMarker, ex.GetType().Name);
        }
    }
}
