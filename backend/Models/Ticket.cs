using backend.Enums;
using backend.Interfaces;

namespace backend.Models;

public class Ticket : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid BusinessId { get; set; }
    public Guid CustomerId { get; set; }
    public Guid? ConversationId { get; set; }

    public TicketStatus Status { get; set; } = TicketStatus.Open;
    public TicketPriority Priority { get; set; }

    /// <summary>See <see cref="TicketPriorityReasons"/> for valid values.
    /// Drives TagModeMappings.IsMinimumFloor Manual-only enforcement.</summary>
    public required string PriorityReason { get; set; }

    /// <summary>
    /// Snapshot at creation time — from Conversation.Channel when one
    /// exists, otherwise selected by staff/system for a conversationless
    /// ticket. Does not change even if the source conversation's channel
    /// could theoretically change later. Drives the SlaTargets lookup.
    /// </summary>
    public required string Channel { get; set; }

    public Guid? ClaimedByUserId { get; set; }
    public DateTimeOffset? ClaimedAt { get; set; }

    public Guid? ResolvedByUserId { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }

    /// <summary>
    /// When the first human (Agent-role) response was sent in this ticket's
    /// conversation, after the ticket was created. AI-generated replies do
    /// NOT count — SLA is meant to measure staff responsiveness, and an
    /// AI-only metric would trivially read as ~100% met regardless of actual
    /// human attention. Distinct from ClaimedAt, which is when a staff member
    /// took ownership, not when they actually sent a reply.
    /// </summary>
    public DateTimeOffset? FirstRespondedAt { get; set; }

    public DateTimeOffset? SlaDeadline { get; set; }

    public string? Summary { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}