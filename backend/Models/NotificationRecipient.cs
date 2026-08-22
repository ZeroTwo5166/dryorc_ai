using backend.Interfaces;

namespace backend.Models;

/// <summary>
/// One row per (Notification, recipient staff member) — per-user fan-out,
/// so each staff member's read/unread state is fully independent. A
/// notification sent to 5 staff members creates 5 rows here.
/// </summary>
public class NotificationRecipient : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid BusinessId { get; set; }
    public Guid NotificationId { get; set; }
    public Guid UserId { get; set; }

    public bool IsRead { get; set; } = false;
    public DateTimeOffset? ReadAt { get; set; }

    /// <summary>
    /// Deliberately a denormalized copy of Notification.CreatedAt, captured
    /// once per NotifyAsync call and assigned identically to every fan-out
    /// row — never independently generated per recipient. Same reasoning as
    /// Conversation.LastMessageAt's denormalization: lets the recipient
    /// feed's own (BusinessId, UserId, CreatedAt, Id) index drive keyset
    /// pagination without joining through Notification just to sort.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }
}
