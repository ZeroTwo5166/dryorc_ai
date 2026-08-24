using backend.Constants;
using backend.Data;
using backend.Models;
using Microsoft.EntityFrameworkCore;

namespace backend.Services;

/// <summary>
/// The single shared definition of "unread CustomerReplied
/// NotificationRecipient rows for a conversation" — used by both
/// WidgetConversationService's dedup check (does one already exist?) and
/// ConversationsController's staff-reply mark-read step (resolve all of
/// them). Kept deliberately narrow to this one notification type + this one
/// query shape, not a general notification-lookup abstraction — see the
/// Step 13A-adjacent audit note this was extracted to close: the two call
/// sites had drifted into two independently-written copies of the same
/// predicate.
/// </summary>
internal static class CustomerRepliedNotificationQueries
{
    public static IQueryable<NotificationRecipient> UnreadRecipients(AppDbContext db, Guid businessId, Guid conversationId) =>
        db.NotificationRecipients
            .IgnoreQueryFilters()
            .Where(r => r.BusinessId == businessId && !r.IsRead)
            .Where(r => db.Notifications.IgnoreQueryFilters().Any(n =>
                n.Id == r.NotificationId
                && n.BusinessId == businessId
                && n.Type == NotificationTypes.CustomerReplied
                && n.ConversationId == conversationId));
}
