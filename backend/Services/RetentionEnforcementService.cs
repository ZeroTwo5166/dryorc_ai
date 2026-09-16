using System.Text.Json;
using backend.Constants;
using backend.Data;
using backend.Enums;
using Microsoft.EntityFrameworkCore;

namespace backend.Services;

public interface IRetentionEnforcementService
{
    /// <summary>Purges every conversation, across every business, past its retention cutoff. Returns how many were purged.</summary>
    Task<int> EnforceRetentionAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The scheduled counterpart to CustomerErasureService — same underlying
/// ConversationPurgeService, different trigger: age-based and per-business
/// rather than on-demand and per-customer (see ConversationPurgeService's
/// own doc comment for why both share one operation instead of two
/// divergent deletion implementations).
///
/// Cutoff: Conversation.LastMessageAt &lt; now - Business.RetentionDays,
/// falling back to Conversation.CreatedAt for conversations with no
/// messages at all. Already-anonymized conversations are excluded up
/// front — PurgeConversationAsync would no-op on them anyway, but skipping
/// them here avoids the round-trip.
///
/// GDPR lifecycle design (docs/handoffs/gdpr-lifecycle-design.md §2) —
/// Decision 3: a conversation with an active linked Ticket
/// (Status != Resolved) is exempt from age-based purge. The discovery
/// query's own Ticket filter below is an optimization only — NOT
/// sufficient alone, since a Ticket can be created in the gap between
/// discovery and a given conversation's turn in the loop. The real
/// guarantee is TryPurgeConversationAsync's atomic per-conversation claim,
/// which re-asserts every eligibility condition (staleness, RetentionDays,
/// the Ticket exemption, and !IsAnonymized) fresh, inside the same
/// transaction the purge and its audit row commit in — see that method's
/// own doc comment. This exemption does NOT apply to GDPR erasure
/// (CustomerErasureService) — Decision 3 is retention-only.
///
/// Every successful purge writes an AuditEventTypes.ConversationRetentionPurged
/// row (ActorType.NivoraSystem, no ChangedByUserId) inside the same
/// transaction as the claim and the purge (Decision 5) — this job now
/// provides the full positive audit-atomicity guarantee: a crash at any
/// point before that transaction's CommitAsync returns leaves neither the
/// purge nor its audit row committed.
///
/// Explicitly cross-tenant by nature (iterates every business in one
/// pass), so — like ConversationPurgeService/CustomerErasureService —
/// every query here uses IgnoreQueryFilters() with an explicit BusinessId
/// filter rather than the ambient ICurrentTenantAccessor, which has
/// nothing to resolve in this no-HttpContext, scheduled-job context.
/// </summary>
public class RetentionEnforcementService(AppDbContext db, IConversationPurgeService purgeService, IAuditLogService auditLogService)
    : IRetentionEnforcementService
{
    public async Task<int> EnforceRetentionAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;

        var businesses = await db.Businesses
            .IgnoreQueryFilters()
            .Select(b => new { b.Id, b.RetentionDays })
            .ToListAsync(cancellationToken);

        var purgedCount = 0;

        foreach (var business in businesses)
        {
            var cutoff = now.AddDays(-business.RetentionDays);

            var expiredConversationIds = await db.Conversations
                .IgnoreQueryFilters()
                .Where(c => c.BusinessId == business.Id && !c.IsAnonymized)
                .Where(c =>
                    (c.LastMessageAt != null && c.LastMessageAt < cutoff) ||
                    (c.LastMessageAt == null && c.CreatedAt < cutoff))
                // Decision 3 discovery-time narrowing — NOT sufficient alone
                // (see class doc comment); the same condition is
                // re-asserted fresh, atomically, in TryPurgeConversationAsync's
                // claim below. Present here only as an optimization to avoid
                // attempting a claim on conversations that obviously already
                // have an active Ticket.
                .Where(c => !db.Tickets.IgnoreQueryFilters().Any(t => t.ConversationId == c.Id && t.Status != TicketStatus.Resolved))
                .Select(c => c.Id)
                .ToListAsync(cancellationToken);

            foreach (var conversationId in expiredConversationIds)
            {
                if (await TryPurgeConversationAsync(business.Id, conversationId, cancellationToken))
                {
                    purgedCount++;
                }
            }
        }

        return purgedCount;
    }

    /// <summary>
    /// The atomic per-conversation claim + purge + audit. internal, not
    /// private — exposed to backend.Tests (this project's existing
    /// InternalsVisibleTo, the same precedent
    /// ConversationInactivityCloseService.TryCloseConversationAsync already
    /// established) so the discovery/claim race Decision 3's atomic-recheck
    /// requirement exists to close can be tested directly and
    /// deterministically: seed a candidate, mutate state via a second,
    /// independent AppDbContext (simulating a concurrent Ticket creation,
    /// Message arrival, or RetentionDays change), then call this method and
    /// assert the mutation-time recheck catches it.
    ///
    /// The claim (ExecuteUpdateAsync, touching only UpdatedAt) never
    /// mutates IsAnonymized — that remains ConversationPurgeService's own,
    /// sole responsibility, so its idempotency guard
    /// (conversation is null || conversation.IsAnonymized) stays the single,
    /// unambiguous signal for "has this conversation actually been purged."
    /// A 0-row claim result is ordinary concurrency (a Message arrived, a
    /// Ticket became active, RetentionDays changed, or another worker
    /// already purged this conversation) — never an error, never logged as
    /// one, matching the Step 25 auto-close design's identical framing.
    ///
    /// ConversationPurgeService joins this method's own transaction
    /// (Option B, Phase 2) — its Message/Notification/non-Consumed-Approval
    /// deletes and the Conversation mutation land in the same transaction
    /// as the claim and the AuditLog write below, committed or rolled back
    /// together.
    /// </summary>
    internal async Task<bool> TryPurgeConversationAsync(Guid businessId, Guid conversationId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            // Fresh re-read of Business.RetentionDays, never the
            // discovery-time snapshot — a business could have changed
            // RetentionDays between discovery and this conversation's turn
            // in the loop.
            var currentRetentionDays = await db.Businesses
                .IgnoreQueryFilters()
                .Where(b => b.Id == businessId)
                .Select(b => b.RetentionDays)
                .SingleAsync(cancellationToken);

            var now = DateTimeOffset.UtcNow;
            var cutoff = now.AddDays(-currentRetentionDays);

            // The atomic claim — re-asserts every eligibility condition
            // fresh, including the Ticket exemption, and touches only
            // UpdatedAt. Via Postgres's own row-level locking, this UPDATE
            // takes a row lock even when the assigned value doesn't
            // change, held for the rest of this transaction — no other
            // transaction can concurrently claim or mutate this exact
            // Conversation row until this one commits or rolls back.
            var claimed = await db.Conversations
                .IgnoreQueryFilters()
                .Where(c => c.Id == conversationId
                    && !c.IsAnonymized
                    && ((c.LastMessageAt != null && c.LastMessageAt < cutoff)
                        || (c.LastMessageAt == null && c.CreatedAt < cutoff))
                    && !db.Tickets.IgnoreQueryFilters().Any(t => t.ConversationId == c.Id && t.Status != TicketStatus.Resolved))
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.UpdatedAt, now), cancellationToken);

            if (claimed == 0)
            {
                await transaction.CommitAsync(cancellationToken);
                return false;
            }

            await purgeService.PurgeConversationAsync(conversationId, cancellationToken);

            await auditLogService.LogAsync(
                businessId, AuditEntityTypes.Conversation, conversationId.ToString(),
                AuditEventTypes.ConversationRetentionPurged, ActorType.NivoraSystem,
                newValue: SerializeRetentionAuditValue(currentRetentionDays));

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    /// <summary>Deliberately hand-picked, safe fields only — never the full
    /// Conversation entity, never customer PII, matching
    /// ConversationInactivityCloseService's own SerializeAuditValue
    /// convention.</summary>
    private static string SerializeRetentionAuditValue(int retentionDays) =>
        JsonSerializer.Serialize(new { RetentionDays = retentionDays });
}
