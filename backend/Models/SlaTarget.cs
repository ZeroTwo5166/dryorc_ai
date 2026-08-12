using backend.Enums;
using backend.Interfaces;

namespace backend.Models;

/// <summary>
/// Business-configured SLA rule: how many minutes a ticket of this
/// priority (and optionally, this channel) has to receive its first
/// response before breaching. Compared against Ticket.FirstRespondedAt,
/// not Ticket.ClaimedAt — claiming a ticket isn't the same as actually
/// responding to the customer.
///
/// Channel is nullable to support a channel-agnostic fallback rule — when
/// resolving a ticket's SlaDeadline, check for an exact
/// (Priority, Channel) match first, then fall back to
/// (Priority, Channel = null) if no channel-specific rule exists.
/// </summary>
public class SlaTarget : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid BusinessId { get; set; }

    public TicketPriority Priority { get; set; }

    /// <summary>See <see cref="ConversationChannels"/> for valid values.
    /// Null means this rule applies to any channel not otherwise
    /// covered by a more specific rule.</summary>
    public string? Channel { get; set; }

    public int TargetMinutes { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
