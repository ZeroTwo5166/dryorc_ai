using backend.Enums;
using backend.Models;

namespace backend.Services;

/// <summary>
/// Step 13A — standalone SLA-lookup/deadline and FirstRespondedAt domain
/// logic, deliberately named TicketSlaService rather than TicketService:
/// 13A does not implement the ticket lifecycle (creation, claiming,
/// priority selection) at all — only this narrower, fully-specified slice.
///
/// No production caller exists yet, deliberately — same standalone-ahead-
/// of-integration precedent as KnowledgeChunker (Step 11) and
/// ConversationModeService (this step). No ticket-creation workflow, no
/// Agent-message flow, is introduced to exercise this.
/// </summary>
public interface ITicketSlaService
{
    /// <summary>
    /// Resolves the SlaDeadline for a ticket being created now, using the
    /// documented exact-match-then-fallback lookup:
    /// (BusinessId, Priority, Channel) first, only falling back to
    /// (BusinessId, Priority, Channel = null) when no exact match exists.
    /// Returns null — never throws — when neither exists ("no applicable
    /// SLA"). The result is a point-in-time calculation from whichever
    /// SlaTarget was found; it is never recomputed later even if SlaTarget
    /// configuration subsequently changes, because this method is never
    /// called again for an existing ticket.
    /// </summary>
    Task<DateTimeOffset?> ComputeSlaDeadlineAsync(
        Guid businessId, TicketPriority priority, string channel, DateTimeOffset createdAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Pure, synchronous computation of what Ticket.FirstRespondedAt should
    /// be after considering one message — never mutates either argument.
    /// Returns the ticket's existing FirstRespondedAt unchanged if already
    /// set (never overwritten); otherwise returns message.CreatedAt only if
    /// message.Role == Agent and message.CreatedAt is after ticket.CreatedAt;
    /// otherwise null (the message doesn't qualify).
    /// </summary>
    DateTimeOffset? ComputeFirstRespondedAt(Ticket ticket, Message message);
}
