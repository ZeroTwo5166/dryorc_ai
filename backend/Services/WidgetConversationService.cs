using backend.Constants;
using backend.Data;
using backend.Enums;
using backend.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

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
///
/// Staff-notification wiring: after the customer message commits, if the
/// conversation is HandledBy.Human (a staff member has already taken over
/// — see Conversation.HandledBy's own doc comment), this fires a
/// CustomerReplied notification, UNLESS an unread one already exists for
/// this conversation. That "at most one unread CustomerReplied per
/// conversation" check is deliberately a plain query, not
/// NotificationService's IdempotencyKey mechanism — IdempotencyKey is
/// permanently unique per (BusinessId, key), which fits "notify at most
/// once ever," not "notify at most once until resolved, then allow a
/// fresh one." A conversation resolved once (staff replies, notification
/// marked read — see ConversationsController.CreateMessage) must still be
/// able to notify again for a later, still-Human-handled message. This is
/// a secondary side effect: a NotifyAsync failure is logged and swallowed,
/// never allowed to fail the already-committed customer message, same
/// reasoning ClaudeReplyService's own call site already established for
/// its own downstream failures.
///
/// Concurrency (audit-fixed): the check-then-create above is racy on its
/// own — two customer messages on the same conversation, close enough
/// together, no longer share the ForVisitorSession lock by the time each
/// reaches this step (that lock's transaction already committed at the
/// PostMessageAsync call site). Fixed the same way the lazy
/// conversation-creation race is fixed: a dedicated
/// pg_advisory_xact_lock (AdvisoryLockKeyGenerator.ForConversationNotification)
/// held for the duration of the check-then-create, inside a new
/// transaction opened here. NotificationService.NotifyAsync detects this
/// ambient transaction and writes into it instead of opening its own —
/// see that method's own comment — so the lock genuinely covers the
/// Notification/NotificationRecipient insert, not just the read. Two
/// concurrent calls for the same conversation now serialize on the lock;
/// the second one's check runs only after the first's transaction has
/// fully committed, so it always observes the first's write under
/// ordinary READ COMMITTED visibility — same reasoning already documented
/// for ForVisitorSession above.
///
/// Zero active staff: NotificationService.NotifyAsync's own documented,
/// general contract is to still create a Notification with zero
/// recipients when there are no eligible recipients (a deliberate
/// cross-notification-type behavior — see its class doc comment — left
/// unchanged here). That default doesn't fit CustomerReplied specifically:
/// a recipient-less Notification of this type can never be marked read
/// (ConversationsController's mark-read only ever flips
/// NotificationRecipient rows), so it would silently and permanently
/// suppress every later CustomerReplied notification for that
/// conversation — worse than the unbounded-orphan failure mode it would
/// otherwise cause. Handled by a caller-side pre-check, scoped to this one
/// call site only: skip creating a notification at all when the business
/// currently has no active staff. No NotificationRecipient row ever exists
/// with zero recipients for CustomerReplied, so the existing
/// recipient-based dedup query needs no special-casing for that case.
/// </summary>
public class WidgetConversationService(AppDbContext db, INotificationService notificationService, ILogger<WidgetConversationService> logger) : IWidgetConversationService
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

            if (conversation.HandledBy == HandledBy.Human)
            {
                await NotifyStaffOfCustomerReplyAsync(businessId, conversation.Id, cancellationToken);
            }

            return message;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    /// <summary>
    /// Deliberately called after the customer message's own transaction has
    /// already committed — a notification is a side effect of that write,
    /// never a precondition for it. See the class doc comment for the "at
    /// most one unread per conversation" reasoning, the concurrency fix, the
    /// zero-active-staff handling, and why this doesn't use IdempotencyKey.
    /// The entire body is deliberately one try/catch(Exception) — including
    /// BeginTransactionAsync itself — so nothing from this method can ever
    /// propagate into PostMessageAsync's own (already-committed-by-now)
    /// transaction/catch block.
    /// </summary>
    private async Task NotifyStaffOfCustomerReplyAsync(Guid businessId, Guid conversationId, CancellationToken cancellationToken)
    {
        var lockKey = AdvisoryLockKeyGenerator.ForConversationNotification(businessId, conversationId);
        IDbContextTransaction? transaction = null;
        try
        {
            transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            // Blocks until any other transaction holding this exact
            // conversation's notification lock commits or rolls back —
            // serializing the check-then-create below against a concurrent
            // customer message on the same conversation. See the class doc
            // comment.
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock({lockKey})", cancellationToken);

            var alreadyUnread = await CustomerRepliedNotificationQueries
                .UnreadRecipients(db, businessId, conversationId)
                .AnyAsync(cancellationToken);

            if (!alreadyUnread)
            {
                var hasActiveStaff = await db.Users
                    .IgnoreQueryFilters()
                    .AnyAsync(u => u.BusinessId == businessId && u.IsActive, cancellationToken);

                if (hasActiveStaff)
                {
                    await notificationService.NotifyAsync(
                        businessId,
                        NotificationTypes.CustomerReplied,
                        new CustomerRepliedArgs(),
                        ActorType.NivoraSystem,
                        conversationId: conversationId,
                        cancellationToken: cancellationToken);
                }
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            // Never lets a notification failure invalidate the customer
            // message this already returned successfully — see the class
            // doc comment.
            logger.LogWarning(ex, "Failed to notify staff of a customer reply for conversation {ConversationId}", conversationId);
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }
    }
}
