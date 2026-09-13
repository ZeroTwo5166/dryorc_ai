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
    /// Set from widget init — this is what an anonymous visitor is tracked
    /// by before (and after) a CustomerId is attached. Not unique — a
    /// single visitor session can create multiple conversations over time
    /// (e.g. a resolved conversation weeks ago, a new one today). Nulled by
    /// ConversationPurgeService (customer erasure or retention expiry) —
    /// this is the one remaining session-linkable identifier on the
    /// conversation shell once its Messages are gone.
    /// </summary>
    public string? VisitorSessionId { get; set; }

    public ConversationStatus Status { get; set; } = ConversationStatus.Open;

    /// <summary>
    /// AI/human status shown in the Step 9 inbox. Defaults to Ai. Flipped to
    /// Human by the Step 9 staff-reply endpoint on a conversation's first
    /// Agent message — while Human, ClaudeReplyService stops auto-replying
    /// to that conversation's customer messages (see its own doc comment).
    /// There is no reversal path back to Ai yet; that, plus a full
    /// ConversationHandoff audit trail/reason codes/dashboard affordance,
    /// is Step 15's own, larger scope.
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

    /// <summary>
    /// The most specific tag classification currently assigned, if any —
    /// display/UI concern only. Independent of Tags, which continues to
    /// hold the complete set (primary included, normalized) and is what
    /// ConversationModeService's matching actually reads; this column has
    /// zero effect on mode computation (step-22 design doc §D).
    /// </summary>
    public string? PrimaryTag { get; set; }

    /// <summary>
    /// Persisted, monotonically-non-decreasing ratchet over
    /// ConversationModeService.ComputeEffectiveModeAsync's own, unchanged,
    /// per-call result — only ComputeAndRatchetEffectiveModeAsync may ever
    /// advance this field, and only upward (Stricter/Max against the
    /// previous value). Defaults to Auto, the enum's own weakest value: a
    /// deliberate "start most-permissive" choice, safe because nothing
    /// enforces this field as an autonomy gate until a real consumer is
    /// wired in (step-22 design doc §C).
    /// </summary>
    public ConversationMode EffectiveMode { get; set; } = ConversationMode.Auto;

    /// <summary>Level 2 verification expiry (Section 4, OTP path).</summary>
    public DateTimeOffset? Level2VerifiedUntil { get; set; }

    /// <summary>
    /// Denormalized from the latest Message.CreatedAt for this conversation,
    /// updated whenever a new Message is written. Avoids a join+aggregate
    /// against Messages for the Business Inbox's "most recently active
    /// first" sort.
    /// </summary>
    public DateTimeOffset? LastMessageAt { get; set; }

    /// <summary>
    /// True once ConversationPurgeService has purged this conversation
    /// (customer erasure or retention expiry) — its Messages are gone and
    /// VisitorSessionId is nulled, but the row survives as an analytics
    /// shell (Channel/Status/Tags/CreatedAt/LastMessageAt all remain).
    /// Doubles as the idempotency marker: PurgeConversationAsync checks
    /// this first and no-ops if already true, rather than re-attempting
    /// work that already committed.
    /// </summary>
    public bool IsAnonymized { get; set; } = false;
    public DateTimeOffset? AnonymizedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}



