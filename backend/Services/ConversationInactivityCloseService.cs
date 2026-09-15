using System.Text.Json;
using backend.Constants;
using backend.Data;
using backend.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace backend.Services;

public interface IConversationInactivityCloseService
{
    /// <summary>Closes every eligible stale Conversation across every
    /// auto-close-enabled Business. Returns how many were actually
    /// closed.</summary>
    Task<int> CloseInactiveConversationsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Step 25 Candidate 2, Phase 3 — implements the locked design
/// (docs/handoffs/step-25-candidate2-stale-conversation-autoclose-design.md
/// §1-§9) and its implementation plan (...-implementation-plan.md §2 Phase
/// 3). Not yet registered with DI or called from any BackgroundService —
/// Phase 4's own, later scope (mirrors Step 24 Phases 5/6's identical
/// "not yet reachable from any running process" safety argument).
///
/// Two-phase shape, mirroring RetentionEnforcementService's own "select
/// candidate ids, then per-row processing" structure (design §4) rather
/// than a ScheduledReportRun-style claim table — there is no new entity to
/// create here: the Conversation row itself already exists and is itself
/// the thing being claimed, the same way TicketsController.Resolve claims
/// a Ticket row without a separate claim table.
///
/// Explicitly cross-tenant by nature (iterates every auto-close-enabled
/// business in one pass), so every query uses IgnoreQueryFilters() with an
/// explicit BusinessId/Id filter rather than the ambient
/// ICurrentTenantAccessor, which has nothing to resolve in this
/// no-HttpContext, scheduled-job context — same precedent, same reasoning,
/// cited verbatim from RetentionEnforcementService.cs.
/// </summary>
public class ConversationInactivityCloseService(
    AppDbContext db,
    IAuditLogService auditLogService,
    ILogger<ConversationInactivityCloseService> logger) : IConversationInactivityCloseService
{
    public async Task<int> CloseInactiveConversationsAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;

        // Design §4 Phase 1 — cross-tenant discovery, unbounded per design
        // §1.5 (no precedent for pagination exists anywhere in this
        // codebase's comparable background jobs).
        var businesses = await db.Businesses
            .IgnoreQueryFilters()
            .Where(b => b.ConversationAutoCloseEnabled)
            .Select(b => new { b.Id, b.ConversationInactivityCloseDays })
            .ToListAsync(cancellationToken);

        var closedCount = 0;

        foreach (var business in businesses)
        {
            var cutoff = now.AddDays(-business.ConversationInactivityCloseDays);

            var candidateIds = await FindCandidateIdsAsync(business.Id, cutoff, cancellationToken);

            foreach (var conversationId in candidateIds)
            {
                // Design §7 — per-conversation try/catch: one conversation's
                // claim failure must not abort the rest of this business's
                // sweep, nor any other business's sweep in this same call.
                // A failed conversation is simply left Open (the guarded
                // transaction below either fully commits or fully rolls
                // back — no partial state) and re-evaluated on the next run
                // if still eligible.
                try
                {
                    if (await TryCloseConversationAsync(business.Id, conversationId, now, cancellationToken))
                    {
                        closedCount++;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw; // real shutdown — never swallowed, never logged as a failure
                }
                catch (Exception ex)
                {
                    logger.LogError(ex,
                        "Failed to close Conversation {ConversationId} for Business {BusinessId} — left Open, will be re-evaluated next run.",
                        conversationId, business.Id);
                }
            }
        }

        return closedCount;
    }

    /// <summary>
    /// Design §1's eligibility predicate, used only for discovery — every
    /// condition here is re-asserted, independently and freshly, inside
    /// TryCloseConversationAsync's guarded ExecuteUpdateAsync WHERE clause
    /// (design §3). A conversation returned here is a CANDIDATE only; this
    /// method's own result is never trusted as the final authorization for
    /// closing it.
    ///
    /// internal, not private — exposed to backend.Tests (this project's
    /// existing InternalsVisibleTo, the same precedent
    /// BusinessWeekPeriodCalculator.ToUtc already established) so the
    /// discovery/claim race the design's §3 atomic-recheck requirement
    /// exists to close can be tested directly and deterministically: seed
    /// a candidate, mutate state via a second, independent AppDbContext
    /// (simulating a concurrent worker/message/ticket/config change), then
    /// call TryCloseConversationAsync and assert the mutation-time recheck
    /// catches it — rather than depending on a real, non-deterministic
    /// race between two threads.
    /// </summary>
    internal async Task<List<Guid>> FindCandidateIdsAsync(Guid businessId, DateTimeOffset cutoff, CancellationToken cancellationToken) =>
        await db.Conversations
            .IgnoreQueryFilters()
            .Where(c => c.BusinessId == businessId
                && c.Status == ConversationStatus.Open
                && !c.IsAnonymized
                // Design §2 — Case 1 (has at least one Message): staleness
                // from LastMessageAt. Case 2 (zero Messages): staleness
                // from CreatedAt. Reused verbatim from
                // RetentionEnforcementService.cs's own identical fallback.
                && ((c.LastMessageAt != null && c.LastMessageAt < cutoff)
                    || (c.LastMessageAt == null && c.CreatedAt < cutoff))
                // Decision 2 — exempt any Conversation with an active
                // linked Ticket (Open/InProgress/Reopened). "Active" reuses
                // the exact same Status != Resolved predicate
                // IX_Tickets_ActiveTicketPerConversation's own filter
                // already encodes, not an invented definition.
                && !db.Tickets.IgnoreQueryFilters().Any(t => t.ConversationId == c.Id && t.Status != TicketStatus.Resolved))
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// Design §3/§4/§6 — the atomic per-conversation claim. Mirrors
    /// TicketsController.Resolve's exact transaction shape
    /// (BeginTransactionAsync → guarded ExecuteUpdateAsync → LogAsync →
    /// SaveChangesAsync → CommitAsync, all on this one AppDbContext
    /// instance so the state mutation and the AuditLog write commit or
    /// roll back together — design §6 Option A). Returns whether this
    /// conversation was actually closed (false = lost race / no longer
    /// eligible at mutation time — ordinary concurrency, never an error).
    ///
    /// internal, not private — see FindCandidateIdsAsync's own doc comment
    /// for why.
    /// </summary>
    internal async Task<bool> TryCloseConversationAsync(
        Guid businessId, Guid conversationId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            // Design §3 condition 4 / §4's "claim linearization" — a fresh
            // re-read of the owning Business's current config, never the
            // discovery-time snapshot, which may be stale by the time this
            // specific conversation is reached.
            var current = await db.Businesses
                .IgnoreQueryFilters()
                .Where(b => b.Id == businessId)
                .Select(b => new { b.ConversationAutoCloseEnabled, b.ConversationInactivityCloseDays })
                .SingleAsync(cancellationToken);

            if (!current.ConversationAutoCloseEnabled)
            {
                await transaction.CommitAsync(cancellationToken); // disabled since discovery — ordinary concurrency
                return false;
            }

            var cutoff = now.AddDays(-current.ConversationInactivityCloseDays);

            // Design §3 — the full, five-condition eligibility predicate,
            // re-asserted here at mutation time, not merely at discovery:
            // (1) still Open, (2) still past a freshly-recomputed threshold,
            // (3) still no active linked Ticket, (4) AutoCloseEnabled still
            // true (checked immediately above), (5) still not anonymized.
            var affected = await db.Conversations
                .IgnoreQueryFilters()
                .Where(c => c.Id == conversationId
                    && c.Status == ConversationStatus.Open
                    && !c.IsAnonymized
                    && ((c.LastMessageAt != null && c.LastMessageAt < cutoff)
                        || (c.LastMessageAt == null && c.CreatedAt < cutoff))
                    && !db.Tickets.IgnoreQueryFilters().Any(t => t.ConversationId == c.Id && t.Status != TicketStatus.Resolved))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(c => c.Status, ConversationStatus.Closed)
                    .SetProperty(c => c.UpdatedAt, now), cancellationToken);

            if (affected == 0)
            {
                // Ordinary concurrency (design §3): a Message arrived, a
                // Ticket became active, the feature was disabled, or
                // another worker already closed this conversation first —
                // never an error, never logged as one.
                await transaction.CommitAsync(cancellationToken);
                return false;
            }

            // Read AFTER the successful, now-committed-in-transaction
            // state transition — safe (no further race to guard against;
            // LastMessageAt is never itself mutated by this update) and
            // only needed for the audit payload below.
            var lastMessageAt = await db.Conversations
                .IgnoreQueryFilters()
                .Where(c => c.Id == conversationId)
                .Select(c => c.LastMessageAt)
                .SingleAsync(cancellationToken);

            await auditLogService.LogAsync(
                businessId,
                AuditEntityTypes.Conversation,
                conversationId.ToString(),
                AuditEventTypes.ConversationAutoClosed,
                ActorType.NivoraSystem,
                conversationId: conversationId,
                newValue: SerializeAuditValue(current.ConversationInactivityCloseDays, lastMessageAt));

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

    /// <summary>Deliberately hand-picked, safe fields only — never the
    /// full Conversation entity, never customer PII, matching
    /// BusinessController.SerializeAuditValue's own convention.</summary>
    private static string SerializeAuditValue(int inactivityDays, DateTimeOffset? lastMessageAt) =>
        JsonSerializer.Serialize(new { Status = "Closed", InactivityDays = inactivityDays, LastMessageAt = lastMessageAt });
}
