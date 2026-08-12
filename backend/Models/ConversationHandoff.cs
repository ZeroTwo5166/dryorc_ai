using backend.Enums;
using backend.Interfaces;

namespace backend.Models;

/// <summary>
/// Append-only event log of AI/human handoff transitions for a
/// conversation. Each row is an immutable historical fact — no UpdatedAt,
/// consistent with AuditLog/MessageKnowledgeSource. Reuses AuditLog's
/// ActorType enum (same "what originated this" question).
///
/// ConversationId is nullable/SetNull rather than Cascade — handoff
/// history is low-stakes/non-PII and potentially useful for future
/// escalation analytics (Step 21-23), so it's kept even after its
/// conversation is deleted by retention, rather than destroyed with it.
/// </summary>
public class ConversationHandoff : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid BusinessId { get; set; }
    public Guid? ConversationId { get; set; }

    public Guid? TicketId { get; set; }

    public HandledBy FromHandledBy { get; set; }
    public HandledBy ToHandledBy { get; set; }

    /// <summary>See <see cref="HandoffReasons"/> for valid values.</summary>
    public required string Reason { get; set; }

    public ActorType ActorType { get; set; }

    /// <summary>Required when ActorType == NivoraBusinessStaff; must be
    /// null otherwise — enforced by CK_ConversationHandoffs_Actor.</summary>
    public Guid? ActorId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}