using backend.Constants;
using backend.Enums;
using backend.Models;

namespace backend.Services;

/// <summary>
/// Computes a Notification's frontend route at read time from Type +
/// ConversationId/TicketId — deliberately not persisted as a stored
/// LinkUrl column. A stored URL has no "frozen at creation" requirement the
/// way Title's rendered text does (a route is pure, cheap, current routing
/// logic), so persisting it would only create a staleness risk (old
/// notifications pointing at a dead route after a URL scheme change) with
/// no offsetting benefit.
/// </summary>
public static class NotificationLinkResolver
{
    public static string? Resolve(Notification notification)
    {
        var category = NotificationCategoryMap.Resolve(notification.Type);

        return category switch
        {
            NotificationCategory.Customer => ResolveCustomerLink(notification),
            NotificationCategory.System => ResolveSystemLink(notification),
            _ => null, // Team notifications never link anywhere today.
        };
    }

    private static string? ResolveCustomerLink(Notification notification)
    {
        if (notification.ConversationId is { } conversationId)
        {
            return $"/conversations/{conversationId}";
        }

        if (notification.TicketId is { } ticketId)
        {
            return $"/tickets/{ticketId}";
        }

        return null;
    }

    private static string? ResolveSystemLink(Notification notification) => notification.Type switch
    {
        NotificationTypes.KnowledgeImportCompleted => "/knowledge",
        NotificationTypes.IntegrationSyncCompleted => "/integrations",
        _ => null,
    };
}
