using backend.Data;
using backend.Enums;
using backend.Models;
using Microsoft.EntityFrameworkCore;

namespace backend.Services;

public class TicketSlaService(AppDbContext db) : ITicketSlaService
{
    public async Task<DateTimeOffset?> ComputeSlaDeadlineAsync(
        Guid businessId, TicketPriority priority, string channel, DateTimeOffset createdAt,
        CancellationToken cancellationToken = default)
    {
        // IgnoreQueryFilters() + explicit BusinessId — same reasoning as
        // ConversationModeService: no HTTP-request-scoped caller exists yet
        // to guarantee ambient tenant context.
        var exactMatchMinutes = await db.SlaTargets
            .IgnoreQueryFilters()
            .Where(s => s.BusinessId == businessId && s.Priority == priority && s.Channel == channel)
            .Select(s => (int?)s.TargetMinutes)
            .FirstOrDefaultAsync(cancellationToken);

        // Exact match always wins; the fallback (Channel = null) query only
        // ever runs when no exact-channel row exists.
        var targetMinutes = exactMatchMinutes ?? await db.SlaTargets
            .IgnoreQueryFilters()
            .Where(s => s.BusinessId == businessId && s.Priority == priority && s.Channel == null)
            .Select(s => (int?)s.TargetMinutes)
            .FirstOrDefaultAsync(cancellationToken);

        return targetMinutes is { } minutes ? createdAt.AddMinutes(minutes) : null;
    }

    public DateTimeOffset? ComputeFirstRespondedAt(Ticket ticket, Message message)
    {
        if (ticket.FirstRespondedAt is not null)
        {
            return ticket.FirstRespondedAt;
        }

        if (message.Role != MessageRole.Agent)
        {
            return null;
        }

        return message.CreatedAt > ticket.CreatedAt ? message.CreatedAt : null;
    }
}
