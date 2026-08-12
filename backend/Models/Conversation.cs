using backend.Enums;
using backend.Interfaces;

namespace backend.Models;

public class Conversation : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid BusinessId { get; set; }
    public Guid WidgetId { get; set; }

    /// <summary>Null until the visitor is identified/verified (Section 4).</summary>
    public Guid? CustomerId { get; set; }

    /// <summary>
    /// Always set, from widget init — this is what an anonymous visitor is
    /// tracked by before (and after) a CustomerId is attached. Not unique —
    /// a single visitor session can create multiple conversations over
    /// time (e.g. a resolved conversation weeks ago, a new one today).
    /// </summary>
    public required string VisitorSessionId { get; set; }

    public ConversationStatus Status { get; set; } = ConversationStatus.Open;

    /// <summary>
    /// AI/human status shown in the Step 9 inbox. Defaults to Ai; Step 15's
    /// human handoff flips this to Human.
    /// </summary>
    public HandledBy HandledBy { get; set; } = HandledBy.Ai;

    /// <summary>
    /// Which channel this conversation came in through. See
    /// <see cref="ConversationChannels"/> for valid values. Drives the
    /// SlaTargets(BusinessId, Priority, Channel) lookup at ticket-creation
    /// time. Must always be set explicitly by conversation-creation code —
    /// an empty/unset value silently breaks SLA matching for any resulting
    /// ticket.
    /// </summary>
    public required string Channel { get; set; }

    /// <summary>
    /// Drives ConversationModeService's Auto/Assist/Manual computation via
    /// TagModeMappings. Tags are the data; TagModeMappings is the policy.
    /// </summary>
    public List<string> Tags { get; set; } = [];

    /// <summary>Level 2 verification expiry (Section 4, OTP path).</summary>
    public DateTimeOffset? Level2VerifiedUntil { get; set; }

    /// <summary>
    /// Denormalized from the latest Message.CreatedAt for this conversation,
    /// updated whenever a new Message is written. Avoids a join+aggregate
    /// against Messages for the Business Inbox's "most recently active
    /// first" sort.
    /// </summary>
    public DateTimeOffset? LastMessageAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}







