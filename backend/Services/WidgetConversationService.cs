using backend.Constants;
using backend.Data;
using backend.Enums;
using backend.Models;
using Microsoft.EntityFrameworkCore;

namespace backend.Services;

public interface IWidgetConversationService
{
    /// <summary>
    /// Persists a customer message, lazily creating the conversation if the
    /// visitor has no currently-Open one. Race-safe under concurrent first
    /// messages from the same visitor — see AdvisoryLockKeyGenerator.
    /// </summary>
    Task<Message> PostMessageAsync(
        Guid businessId, Guid widgetId, string visitorSessionId, string content,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The only place that creates a Conversation from a widget message. Uses
/// IgnoreQueryFilters() + explicit BusinessId comparisons throughout,
/// deliberately not relying on the ambient global tenant query filter —
/// same defensive posture ConversationPurgeService already uses, so this
/// service's correctness never depends on whether
/// ClaimsCurrentTenantAccessor happens to also resolve the same BusinessId
/// for this request (it should, per the widget JWT sharing
/// NivoraClaimTypes.BusinessId with the staff cookie principal — verified
/// separately by a dedicated test — but this service doesn't lean on that).
///
/// Concurrency: two simultaneous first messages from the same visitor
/// (BusinessId, WidgetId, VisitorSessionId) must never create two
/// Conversations. A plain read-then-insert has exactly that race. Fixed
/// with pg_advisory_xact_lock — transaction-scoped, so it releases
/// automatically on commit, rollback, or any exception, with no manual
/// unlock call needed (and none should ever be added — a session-level
/// pg_advisory_lock would require exactly that manual bookkeeping and is
/// deliberately not used here).
/// </summary>
public class WidgetConversationService(AppDbContext db) : IWidgetConversationService
{
    public async Task<Message> PostMessageAsync(
        Guid businessId, Guid widgetId, string visitorSessionId, string content,
        CancellationToken cancellationToken = default)
    {
        var lockKey = AdvisoryLockKeyGenerator.ForVisitorSession(businessId, widgetId, visitorSessionId);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            // Blocks until any other transaction holding this exact
            // (BusinessId, WidgetId, VisitorSessionId) lock commits or rolls
            // back — serializing the find-or-create below against a
            // concurrent first message from the same visitor.
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock({lockKey})", cancellationToken);

            var conversation = await db.Conversations
                .IgnoreQueryFilters()
                .Where(c => c.BusinessId == businessId
                    && c.WidgetId == widgetId
                    && c.VisitorSessionId == visitorSessionId
                    && c.Status == ConversationStatus.Open
                    && !c.IsAnonymized)
                .OrderByDescending(c => c.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);

            var now = DateTimeOffset.UtcNow;

            if (conversation is null)
            {
                conversation = new Conversation
                {
                    Id = Guid.NewGuid(),
                    BusinessId = businessId,
                    WidgetId = widgetId,
                    VisitorSessionId = visitorSessionId,
                    Channel = ConversationChannels.Website,
                    HandledBy = HandledBy.Ai,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                db.Conversations.Add(conversation);
            }

            var message = new Message
            {
                Id = Guid.NewGuid(),
                BusinessId = businessId,
                ConversationId = conversation.Id,
                Role = MessageRole.Customer,
                Content = content,
                CreatedAt = now,
            };
            db.Messages.Add(message);

            // Same transaction as the Message insert — never a separate
            // write, per the schema doc's explicit requirement.
            conversation.LastMessageAt = now;
            conversation.UpdatedAt = now;

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return message;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }
}
