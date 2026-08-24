using backend.Constants;
using backend.Data;
using backend.Enums;
using backend.Models;
using Microsoft.EntityFrameworkCore;

namespace backend.Services;

public interface INotificationService
{
    /// <summary>
    /// Creates a Notification and fans it out to recipients, atomically.
    /// Category is always derived from Type via NotificationCategoryMap —
    /// never accepted here. See the class doc comment for the full
    /// contract.
    /// </summary>
    Task NotifyAsync(
        Guid businessId,
        string type,
        NotificationTitleArgs titleArgs,
        ActorType actorType,
        Guid? actorUserId = null,
        Guid? conversationId = null,
        Guid? ticketId = null,
        IReadOnlyList<Guid>? targetUserIds = null,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The single entry point for creating a Notification + its
/// NotificationRecipient fan-out. There is no live caller of this service
/// anywhere in the codebase yet — it ships as ready-to-call infrastructure,
/// same status the GDPR lifecycle services had when first built.
///
/// Category is derived exclusively from NotificationCategoryMap and is
/// never a parameter here, so a caller cannot independently mismatch Type
/// against the wrong Category — CK_Notifications_CategoryTypeConsistency is
/// the DB-level backstop for the same invariant.
///
/// Recipient resolution — broadcast or explicit — always happens inside
/// this method, never by a caller constructing NotificationRecipient rows
/// directly, so "who actually receives this" has exactly one
/// implementation. Both paths are always intersected against this
/// business's currently-active staff (ApplicationUser.IsActive) — a
/// caller-supplied targetUserIds list is treated as a hint, never a
/// trusted final list (e.g. a staff member could have been deactivated
/// between when a ticket was claimed and when this fires; a caller could
/// also, in principle, pass an ID from the wrong business).
///
/// Notification creation and recipient fan-out are wrapped in one explicit
/// transaction, same shape as ConversationPurgeService/CustomerErasureService
/// (BeginTransactionAsync -> mutate -> SaveChangesAsync -> CommitAsync,
/// RollbackAsync + rethrow on any failure) — a failure partway through never
/// leaves a Notification with only a partial recipient set.
///
/// Uses IgnoreQueryFilters() + an explicit businessId parameter throughout,
/// same as the GDPR services — this may run with no authenticated
/// HttpContext at all (a future background trigger), so it can't rely on
/// ICurrentTenantAccessor.
/// </summary>
public class NotificationService(AppDbContext db) : INotificationService
{
    private static readonly IReadOnlyDictionary<string, Type> ExpectedArgsType = new Dictionary<string, Type>
    {
        [NotificationTypes.TeamMemberJoined] = typeof(TeamMemberJoinedArgs),
        [NotificationTypes.StaffRoleChanged] = typeof(StaffRoleChangedArgs),
        [NotificationTypes.StaffAccessRevoked] = typeof(StaffAccessRevokedArgs),
        [NotificationTypes.ConversationAssigned] = typeof(ConversationAssignedArgs),
        [NotificationTypes.TicketSlaWarning] = typeof(TicketSlaWarningArgs),
        [NotificationTypes.CustomerReplied] = typeof(CustomerRepliedArgs),
        [NotificationTypes.NewConversation] = typeof(NewConversationArgs),
        [NotificationTypes.ConversationEscalated] = typeof(ConversationEscalatedArgs),
        [NotificationTypes.KnowledgeImportCompleted] = typeof(KnowledgeImportCompletedArgs),
        [NotificationTypes.IntegrationSyncCompleted] = typeof(IntegrationSyncCompletedArgs),
    };

    public async Task NotifyAsync(
        Guid businessId,
        string type,
        NotificationTitleArgs titleArgs,
        ActorType actorType,
        Guid? actorUserId = null,
        Guid? conversationId = null,
        Guid? ticketId = null,
        IReadOnlyList<Guid>? targetUserIds = null,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default)
    {
        var category = NotificationCategoryMap.Resolve(type);

        if (!ExpectedArgsType.TryGetValue(type, out var expectedArgsType) || titleArgs.GetType() != expectedArgsType)
        {
            throw new ArgumentException(
                $"titleArgs type '{titleArgs.GetType().Name}' does not match the expected " +
                $"NotificationTitleArgs subtype for notification type '{type}'.",
                nameof(titleArgs));
        }

        var recipientIds = await ResolveRecipientsAsync(businessId, targetUserIds, cancellationToken);

        // No documented existing-service convention covers "zero eligible
        // recipients" for a fan-out write. The GDPR services' closest
        // precedent (ExecuteDeleteAsync against a WHERE clause) simply
        // no-ops on an empty match set rather than skip the surrounding
        // operation — matching that here: the Notification is still
        // created (a future business-wide/admin view could still surface
        // it), it just ends up with zero NotificationRecipient rows.

        var now = DateTimeOffset.UtcNow;
        var notification = new Notification
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            Category = category,
            Type = type,
            Title = titleArgs.Render(),
            ConversationId = conversationId,
            TicketId = ticketId,
            ActorType = actorType,
            ActorUserId = actorUserId,
            IdempotencyKey = idempotencyKey,
            CreatedAt = now,
        };

        async Task AddAndSaveAsync()
        {
            db.Notifications.Add(notification);

            foreach (var userId in recipientIds)
            {
                db.NotificationRecipients.Add(new NotificationRecipient
                {
                    Id = Guid.NewGuid(),
                    BusinessId = businessId,
                    NotificationId = notification.Id,
                    UserId = userId,
                    IsRead = false,
                    CreatedAt = now,
                });
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        // A caller that already holds an open transaction on this same
        // AppDbContext (e.g. WidgetConversationService serializing the
        // CustomerReplied dedup check-then-create under
        // AdvisoryLockKeyGenerator.ForConversationNotification) is joined
        // instead of nesting a second BeginTransactionAsync on the same
        // connection, which Npgsql/EF Core does not support. Atomicity is
        // still guaranteed either way — it's just owned by the ambient
        // caller's transaction instead of one opened here. A standalone
        // caller (no ambient transaction — every caller today) is
        // unaffected: this method still owns its own transaction exactly as
        // before.
        if (db.Database.CurrentTransaction is not null)
        {
            await AddAndSaveAsync();
            return;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await AddAndSaveAsync();
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private async Task<IReadOnlyList<Guid>> ResolveRecipientsAsync(
        Guid businessId, IReadOnlyList<Guid>? targetUserIds, CancellationToken cancellationToken)
    {
        var activeStaffQuery = db.Users.IgnoreQueryFilters()
            .Where(u => u.BusinessId == businessId && u.IsActive);

        if (targetUserIds is { Count: > 0 })
        {
            return await activeStaffQuery
                .Where(u => targetUserIds.Contains(u.Id))
                .Select(u => u.Id)
                .ToListAsync(cancellationToken);
        }

        return await activeStaffQuery.Select(u => u.Id).ToListAsync(cancellationToken);
    }
}
