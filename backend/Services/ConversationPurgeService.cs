using backend.Data;
using Microsoft.EntityFrameworkCore;

namespace backend.Services;

public interface IConversationPurgeService
{
    Task PurgeConversationAsync(Guid conversationId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The single authoritative purge operation both CustomerErasureService
/// (on-demand, per customer) and RetentionEnforcementService (scheduled,
/// per business) call — see each for how they invoke this per conversation.
/// Never call this with an ambient authenticated request assumed: both
/// callers may run with no HttpContext at all (a background job, or a
/// system-triggered erasure), so BusinessId is resolved explicitly from
/// the loaded Conversation row (via IgnoreQueryFilters) and threaded
/// through every subsequent query, rather than relying on
/// ICurrentTenantAccessor — see ICurrentTenantAccessor's own doc comment:
/// "background jobs iterating all businesses must call
/// .IgnoreQueryFilters() explicitly... rather than relying on a null
/// accessor."
///
/// End state: Conversation survives as an anonymized analytics shell
/// (Channel/Status/Tags/CreatedAt/LastMessageAt intact, VisitorSessionId
/// nulled, IsAnonymized/AnonymizedAt set); every Message under it is hard-
/// deleted (its MessageKnowledgeSource rows go with it via the existing DB-
/// level FK cascade — no separate deletion here, see AppDbContext's
/// MessageKnowledgeSource config).
///
/// Ticket is deliberately untouched: as of this pass, no code anywhere
/// creates or updates a Ticket (Tickets aren't built yet — see
/// TicketService's absence), so there is nothing populated to purge. This
/// needs revisiting once Ticket creation actually exists.
///
/// CustomerNotification is NOT handled here because it doesn't exist yet
/// (notifications are a separate, later pass by explicit instruction).
/// When it's built, its FK to Conversation must be Cascade — deleting a
/// purged conversation's notifications is part of this same lifecycle, not
/// a separate retention clock, and it must never store a message-preview
/// snapshot (previews are fetched live from Message while the conversation
/// still has messages; once purged, there's nothing left to preview,
/// correctly).
/// </summary>
public class ConversationPurgeService(AppDbContext db, IPurgeFailurePoint failurePoint) : IConversationPurgeService
{
    public async Task PurgeConversationAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        var conversation = await db.Conversations
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == conversationId, cancellationToken);

        // Nothing to purge — either it never existed, or a previous call's
        // transaction already committed and this is a retry racing a
        // caller-side failure after commit (see the idempotency test).
        if (conversation is null || conversation.IsAnonymized)
        {
            return;
        }

        var businessId = conversation.BusinessId;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            failurePoint.MaybeFail("before-message-delete");

            // A real SQL DELETE (not the change tracker), so Postgres's own
            // ON DELETE CASCADE fires for MessageKnowledgeSource — no
            // separate deletion step needed or wanted here.
            await db.Messages
                .IgnoreQueryFilters()
                .Where(m => m.BusinessId == businessId && m.ConversationId == conversationId)
                .ExecuteDeleteAsync(cancellationToken);

            failurePoint.MaybeFail("after-message-delete");

            conversation.VisitorSessionId = null;
            conversation.IsAnonymized = true;
            conversation.AnonymizedAt = DateTimeOffset.UtcNow;
            conversation.UpdatedAt = DateTimeOffset.UtcNow;

            failurePoint.MaybeFail("before-commit");

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }
}
