using backend.Enums;

namespace backend.Constants;

/// <summary>
/// Single source of truth mapping every NotificationTypes value to its
/// NotificationCategory. NotificationService.NotifyAsync derives Category
/// exclusively through this map — Category is never accepted as a
/// caller-supplied parameter, so a caller cannot independently mismatch a
/// Type against the wrong Category (a duplication-of-truth bug a CHECK
/// constraint alone can't fully prevent, since a constraint only validates
/// the pair once both values already exist).
///
/// This map, NotificationTypes, and CK_Notifications_CategoryTypeConsistency
/// (AppDbContext.cs) form a deliberate three-way redundancy — belt-and-
/// suspenders, same posture already used for actor pairing elsewhere in
/// this schema — and must be updated together whenever a notification type
/// is added or removed.
/// </summary>
public static class NotificationCategoryMap
{
    public static readonly IReadOnlyDictionary<string, NotificationCategory> Map =
        new Dictionary<string, NotificationCategory>
        {
            [NotificationTypes.TeamMemberJoined] = NotificationCategory.Team,
            [NotificationTypes.StaffRoleChanged] = NotificationCategory.Team,
            [NotificationTypes.StaffAccessRevoked] = NotificationCategory.Team,

            [NotificationTypes.ConversationAssigned] = NotificationCategory.Customer,
            [NotificationTypes.TicketSlaWarning] = NotificationCategory.Customer,
            [NotificationTypes.CustomerReplied] = NotificationCategory.Customer,
            [NotificationTypes.NewConversation] = NotificationCategory.Customer,
            [NotificationTypes.ConversationEscalated] = NotificationCategory.Customer,

            [NotificationTypes.KnowledgeImportCompleted] = NotificationCategory.System,
            [NotificationTypes.IntegrationSyncCompleted] = NotificationCategory.System,
        };

    /// <summary>
    /// Throws for an unrecognized Type — an unmapped type must never
    /// silently default to some category, since that would defeat the
    /// entire reason this map exists.
    /// </summary>
    public static NotificationCategory Resolve(string type)
    {
        if (Map.TryGetValue(type, out var category))
        {
            return category;
        }

        throw new ArgumentException(
            $"Unknown notification type '{type}' — not present in NotificationCategoryMap.", nameof(type));
    }
}
