using backend.Enums;
using backend.Interfaces;

namespace backend.Models;

public class Message : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid BusinessId { get; set; }
    public Guid ConversationId { get; set; }

    public MessageRole Role { get; set; }
    public required string Content { get; set; }

    /// <summary>
    /// When Nivora ingested this message. For externally-sourced channels
    /// (Email, future WhatsApp/Instagram), see ExternalCreatedAt for when
    /// it was actually sent on the external system's own timeline.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// When this message was actually sent on the external system (e.g.
    /// the email's own timestamp), if different from CreatedAt. Null for
    /// messages that originated directly in Nivora (Website chat).
    /// </summary>
    public DateTimeOffset? ExternalCreatedAt { get; set; }

    /// <summary>
    /// Required when Role == Agent; must be null otherwise, enforced by
    /// CK_Messages_AuthorUserId_RolePairing. This constraint only holds
    /// because ApplicationUser is never hard-deleted, only deactivated
    /// (see ApplicationUser.IsActive) — SetNull on this FK would otherwise
    /// conflict with the constraint on staff account deletion.
    /// </summary>
    public Guid? AuthorUserId { get; set; }
}
