using backend.Enums;
using backend.Interfaces;

namespace backend.Models;

/// <summary>
/// One notification event, fanned out to zero or more staff recipients via
/// NotificationRecipient. Deliberately a single unified table rather than
/// separate Team/Customer tables — see the notification design review: the
/// team/customer boundary is enforced structurally via Category +
/// CK_Notifications_CategoryRefs, not by table separation.
///
/// ConversationId/TicketId are named, real FKs (not a polymorphic
/// EntityType/EntityId pair) so Postgres can enforce real delete behavior —
/// this matters specifically because ConversationPurgeService must delete
/// Notification rows referencing a purged conversation, and a polymorphic
/// reference couldn't participate in that the same way.
///
/// There are no live callers of NotificationService yet — this ships as
/// ready-to-call infrastructure, same as the GDPR lifecycle services did.
/// </summary>
public class Notification : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid BusinessId { get; set; }

    public NotificationCategory Category { get; set; }

    /// <summary>See <see cref="Constants.NotificationTypes"/> for valid values.</summary>
    public required string Type { get; set; }

    /// <summary>
    /// Pre-rendered at creation time via a typed NotificationTitleArgs
    /// subtype's Render() — never a raw caller-supplied string. Must never
    /// contain customer PII (name, email, message content); see
    /// NotificationTitleArgs's doc comment for the structural defense this
    /// relies on.
    /// </summary>
    public required string Title { get; set; }

    /// <summary>Nullable; must be null for Category == Team — enforced by
    /// CK_Notifications_CategoryRefs.</summary>
    public Guid? ConversationId { get; set; }

    /// <summary>Nullable; must be null for Category == Team — enforced by
    /// CK_Notifications_CategoryRefs.</summary>
    public Guid? TicketId { get; set; }

    /// <summary>Reuses AuditLog/ConversationHandoff's ActorType enum — same
    /// "what originated this" question.</summary>
    public ActorType ActorType { get; set; }

    /// <summary>Required when ActorType == NivoraBusinessStaff; must be
    /// null otherwise — enforced by CK_Notifications_Actor.</summary>
    public Guid? ActorUserId { get; set; }

    /// <summary>
    /// Optional caller-supplied dedup key, unique per (BusinessId,
    /// IdempotencyKey) when present. Not every notification type has a
    /// natural dedup key (a one-off mention doesn't); supply one only when
    /// the triggering event could plausibly retry (e.g. one SLA warning per
    /// ticket per breach window).
    /// </summary>
    public string? IdempotencyKey { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
