using backend.Data;
using backend.Enums;
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
/// Ticket is deliberately untouched: Tickets now exist, but this service
/// still has no Ticket awareness by design, not by omission — the
/// active-Ticket retention exemption (GDPR lifecycle design, Decision 3)
/// lives entirely in RetentionEnforcementService instead. Decision 3
/// explicitly rejected giving this shared service a bypass/reason
/// parameter to special-case the exemption here; keeping it out means
/// ConversationPurgeService never has to know a Ticket table exists, for
/// either caller.
///
/// Notification rows referencing this conversation are explicitly deleted
/// below (ExecuteDeleteAsync, same shape as Messages) — their FK to
/// Conversation is SetNull, not Cascade, because this Conversation row is
/// never actually deleted, only anonymized in place; a Cascade FK would
/// never fire, so the app-level delete is the real, load-bearing mechanism.
/// NotificationRecipient rows go with them via the Notification ->
/// NotificationRecipient Cascade FK — never deleted directly here.
///
/// Approval rows are deleted the same way, conditionally: GDPR lifecycle
/// design Decision 4 keeps Consumed rows (durable operational evidence,
/// extending FinancialRecordRetentionPolicy's Art. 17(3)(b) rationale) and
/// erases every other status on the same basis as Message.
///
/// Transaction ownership (GDPR lifecycle design §1, Option B): this method
/// joins an already-open ambient transaction on `db` when the caller has
/// one (db.Database.CurrentTransaction is non-null) — skipping its own
/// Begin/Commit/Rollback/Dispose and leaving the caller to close it — or
/// owns a transaction exactly as before when called standalone. This is
/// derived purely from ambient DB state, never from who is calling or why,
/// preserving this service's reason-agnosticism.
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

        // Option B: join the caller's ambient transaction if one is
        // already open on this same AppDbContext; otherwise own one,
        // exactly as before. Never inspects who the caller is or why.
        var ownsTransaction = db.Database.CurrentTransaction is null;
        var transaction = ownsTransaction
            ? await db.Database.BeginTransactionAsync(cancellationToken)
            : db.Database.CurrentTransaction!;

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

            // Same reasoning as Messages above: Notification.ConversationId
            // is SetNull, not Cascade, because this Conversation row is
            // never actually deleted (only anonymized below) — a Cascade
            // FK would never fire. This explicit delete is the real
            // mechanism. NotificationRecipient rows go with it via the
            // Notification -> NotificationRecipient Cascade FK; never
            // deleted directly here.
            await db.Notifications
                .IgnoreQueryFilters()
                .Where(n => n.BusinessId == businessId && n.ConversationId == conversationId)
                .ExecuteDeleteAsync(cancellationToken);

            // Decision 4 — Consumed rows are durable operational evidence
            // and survive; every other status is erasure/purge-eligible on
            // the same basis as Message. Same conversation-scoped shape as
            // the two deletes above, placed before the final Conversation
            // mutation.
            await db.Approvals
                .IgnoreQueryFilters()
                .Where(a => a.BusinessId == businessId && a.ConversationId == conversationId && a.Status != ApprovalStatus.Consumed)
                .ExecuteDeleteAsync(cancellationToken);

            failurePoint.MaybeFail("after-message-delete");

            conversation.VisitorSessionId = null;
            conversation.IsAnonymized = true;
            conversation.AnonymizedAt = DateTimeOffset.UtcNow;
            conversation.UpdatedAt = DateTimeOffset.UtcNow;

            failurePoint.MaybeFail("before-commit");

            await db.SaveChangesAsync(cancellationToken);

            if (ownsTransaction)
            {
                await transaction.CommitAsync(cancellationToken);
            }
            // else: ambient transaction — the caller owns the commit; this
            // method's mutation is already flushed above and waits inside
            // the caller's own transaction for their own commit.
        }
        catch
        {
            if (ownsTransaction)
            {
                await transaction.RollbackAsync(cancellationToken);
            }
            // else: ambient transaction — never rolled back here; the
            // exception propagates for the caller's own catch/RollbackAsync
            // to undo everything together.
            throw;
        }
        finally
        {
            if (ownsTransaction)
            {
                await transaction.DisposeAsync();
            }
            // else: the caller owns this transaction's lifetime — never
            // dispose one this method doesn't own.
        }
    }
}
