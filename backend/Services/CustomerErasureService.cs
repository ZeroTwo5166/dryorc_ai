using System.Text.Json;
using backend.Constants;
using backend.Data;
using backend.Enums;
using Microsoft.EntityFrameworkCore;

namespace backend.Services;

public interface ICustomerErasureService
{
    Task EraseCustomerAsync(Guid customerId, Guid changedByUserId, CancellationToken cancellationToken = default);
}

/// <summary>
/// GDPR data-subject erasure request handling (GDPR lifecycle design,
/// docs/handoffs/gdpr-lifecycle-design.md §3, Mechanism 2). Three ordered
/// phases:
///
/// 1. Delete the pure identity/security/logging rows that have no reason
///    to survive erasure (ExternalIdentity, FlaggedCustomerActivityLog,
///    CustomerExtendedLoggingHistory, VerificationCode), plus a
///    customer-scoped Approval belt-and-braces delete (Decision 4). All
///    five are plain DELETE-WHERE operations, naturally idempotent, one
///    small transaction, unchanged in shape from before this redesign
///    except for the added Approval delete. Not itself subject to the
///    locked audit-atomicity property below — no conversation purge
///    happens in this phase.
/// 2. Purge every Conversation this customer had, one small transaction
///    per conversation, each pairing that conversation's purge (via the
///    shared ConversationPurgeService, joining this method's own
///    transaction — Option B, Phase 2) with its own ConversationErasurePurged
///    audit row. This is the locked evidentiary property (Decision 5
///    addendum): "Every destructive conversation purge performed as part
///    of a successful GDPR customer erasure must have contemporaneous
///    audit coverage within the same atomic commit boundary." Each
///    conversation's purge and its own audit row share the same small
///    transaction — if it commits, both commit; if it rolls back, neither
///    does. A failure partway through the loop leaves every
///    already-committed conversation's purge and audit intact, and every
///    not-yet-attempted conversation completely untouched — the operation
///    is safely retryable (§3.3).
/// 3. Anonymize the Customer row's own identity fields (Email/Name/
///    ExternalId → null, IsAnonymized/AnonymizedAt set) plus a completion
///    CustomerErased audit row, both in one small transaction — deliberately
///    LAST, so IsAnonymized == true reliably means "this customer's
///    erasure fully completed," not just "we started."
///
/// erasureCorrelationId (AuditLog.CorrelationId) links every row a single
/// erasure call writes — one per-conversation ConversationErasurePurged
/// row for each conversation actually purged, plus the terminal
/// CustomerErased row.
///
/// §7 item 2 (LOCKED, Option A): synchronous erasure with unbounded
/// wall-clock duration is an accepted, known limitation — no batching,
/// chunking, background execution, or artificial threshold is introduced
/// here, or ever should be without a fresh design pass.
///
/// Like ConversationPurgeService, BusinessId is resolved explicitly from
/// the loaded Customer row (not the ambient ICurrentTenantAccessor) so
/// this works correctly whether it's triggered by an authenticated request
/// or a system process with no HttpContext at all. changedByUserId stops
/// here — it is never passed into ConversationPurgeService, which remains
/// ignorant of caller identity/intent by design (Decision 5's settled
/// boundary).
/// </summary>
public class CustomerErasureService(
    AppDbContext db, IConversationPurgeService purgeService, IAuditLogService auditLogService,
    ICustomerErasureCheckpoint checkpoint)
    : ICustomerErasureService
{
    public async Task EraseCustomerAsync(Guid customerId, Guid changedByUserId, CancellationToken cancellationToken = default)
    {
        var customer = await db.Customers
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == customerId, cancellationToken);

        if (customer is null || customer.IsAnonymized)
        {
            // Idempotent no-op. Unlike the previous shape (a bare
            // `customer is null` guard at the top, with an IsAnonymized
            // check only gating Phase 3), this returns immediately for an
            // already-erased customer — a genuine strengthening, not a
            // relocation of prior behavior (Phase 1's deletes and Phase
            // 2's purge loop no longer run needlessly against an
            // already-erased customer).
            return;
        }

        var businessId = customer.BusinessId;
        var erasureCorrelationId = Guid.NewGuid();

        // Phase 0 (identity-binding/erasure-integrity design;
        // implementation plan Phase 3) — establishes the persisted
        // "erasure in progress" fact before any destructive work begins.
        // Own short transaction, separate from Phase 1: ForCustomerErasure
        // is a transaction-scoped advisory lock (like every other lock in
        // this codebase — released on commit/rollback), so it alone can't
        // signal "erasure in progress" to a transaction that acquires it
        // only after this one has already committed and released it. The
        // persisted PendingErasureAt column is what survives that gap; the
        // lock here only serializes this write against a concurrent
        // identity-mutating write's own eligibility recheck under the same
        // key. Idempotent (COALESCE) so re-running EraseCustomerAsync for
        // an already-in-progress customer never clobbers the original
        // timestamp. No audit row (C-1, design §9) — this step performs no
        // destructive operation and the terminal CustomerErased row already
        // covers this erasure run.
        var now = DateTimeOffset.UtcNow;
        var pendingErasureLockKey = AdvisoryLockKeyGenerator.ForCustomerErasure(businessId, customerId);
        await using (var phase0Transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock({pendingErasureLockKey})", cancellationToken);

            await db.Customers
                .IgnoreQueryFilters()
                .Where(c => c.Id == customerId)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(c => c.PendingErasureAt, c => c.PendingErasureAt ?? now),
                    cancellationToken);

            await phase0Transaction.CommitAsync(cancellationToken);
        }

        await checkpoint.ReachedAsync("after-phase-0-committed", cancellationToken);

        // Phase 1 — identity-row deletes + customer-scoped Approval
        // belt-and-braces delete, own small transaction. Not subject to
        // the locked audit-atomicity property — no conversation purge here.
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            await db.ExternalIdentities
                .IgnoreQueryFilters()
                .Where(e => e.BusinessId == businessId && e.CustomerId == customerId)
                .ExecuteDeleteAsync(cancellationToken);

            await db.FlaggedCustomerActivityLogs
                .IgnoreQueryFilters()
                .Where(f => f.BusinessId == businessId && f.CustomerId == customerId)
                .ExecuteDeleteAsync(cancellationToken);

            await db.CustomerExtendedLoggingHistories
                .IgnoreQueryFilters()
                .Where(h => h.BusinessId == businessId && h.CustomerId == customerId)
                .ExecuteDeleteAsync(cancellationToken);

            await db.VerificationCodes
                .IgnoreQueryFilters()
                .Where(v => v.BusinessId == businessId && v.CustomerId == customerId)
                .ExecuteDeleteAsync(cancellationToken);

            // Decision 4 — customer-scoped, keyed on Approval.CustomerId
            // directly (immutable once set), independent of which
            // Conversation the Approval currently points at. This is the
            // only mechanism that catches an Approval whose
            // ConversationId has since been re-linked to a different
            // customer via VerificationCodeService's own unconditional
            // Conversation.CustomerId overwrite (§7 item 1, confirmed
            // real, not redundant with ConversationPurgeService's own
            // per-conversation Approval delete).
            await db.Approvals
                .IgnoreQueryFilters()
                .Where(a => a.BusinessId == businessId && a.CustomerId == customerId && a.Status != ApprovalStatus.Consumed)
                .ExecuteDeleteAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }

        // Phase 2 — one small transaction per conversation. Each pairs
        // that conversation's purge with its own audit row.
        var conversationIds = await db.Conversations
            .IgnoreQueryFilters()
            .Where(c => c.BusinessId == businessId && c.CustomerId == customerId)
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);

        foreach (var conversationId in conversationIds)
        {
            await TryPurgeAndAuditConversationAsync(businessId, customerId, conversationId, changedByUserId, erasureCorrelationId, cancellationToken);
        }

        // Phase 3 — terminal state + completion audit row, own small
        // transaction. Deliberately last: IsAnonymized == true reliably
        // means the erasure fully completed.
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            try
            {
                var completedAt = DateTimeOffset.UtcNow;
                customer.Email = null;
                customer.Name = null;
                customer.ExternalId = null;
                customer.IsAnonymized = true;
                customer.AnonymizedAt = completedAt;
                customer.UpdatedAt = completedAt;

                // C-3 (design §2/§9) — not required for correctness
                // (IsAnonymized alone already makes every PendingErasureAt
                // check moot once true), but clearing it here keeps a
                // future "find stuck erasures" query
                // (WHERE PendingErasureAt IS NOT NULL) from being
                // permanently polluted by every successfully-completed
                // erasure. Deliberately a direct ExecuteUpdateAsync, not a
                // tracked-entity assignment: Phase 0 wrote this column via
                // its own ExecuteUpdateAsync, bypassing this DbContext's
                // change tracker entirely — the tracked `customer`
                // instance's in-memory PendingErasureAt is still whatever
                // it was when loaded at the top of this method (null, in
                // the ordinary case), so `customer.PendingErasureAt = null`
                // would be a no-op the tracker silently drops from the
                // generated UPDATE. ExecuteUpdateAsync here joins this same
                // ambient transaction (Database.CurrentTransaction), same
                // as every other ExecuteUpdateAsync call already used
                // throughout this file — no new transaction, no new lock
                // acquisition.
                await db.Customers
                    .IgnoreQueryFilters()
                    .Where(c => c.Id == customerId)
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.PendingErasureAt, (DateTimeOffset?)null), cancellationToken);

                await auditLogService.LogAsync(
                    businessId, AuditEntityTypes.Customer, customerId.ToString(),
                    AuditEventTypes.CustomerErased, ActorType.NivoraBusinessStaff,
                    changedByUserId: changedByUserId, correlationId: erasureCorrelationId,
                    newValue: SerializeErasureAuditValue(conversationIds.Count));

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

    /// <summary>
    /// The atomic per-conversation claim + purge + audit. internal, not
    /// private — exposed to backend.Tests (this project's existing
    /// InternalsVisibleTo, the same precedent
    /// RetentionEnforcementService.TryPurgeConversationAsync already
    /// established for the identical reason) so the locked audit-atomicity
    /// property and the multi-conversation partial-failure behavior can be
    /// tested directly and deterministically.
    ///
    /// The claim (ExecuteUpdateAsync, touching only UpdatedAt) re-verifies
    /// this conversation still belongs to this customer and hasn't already
    /// been purged (e.g. by a prior, partially-completed attempt at this
    /// same erasure) — never mutates IsAnonymized, so
    /// ConversationPurgeService's own idempotency guard stays the single,
    /// unambiguous signal for "has this conversation been purged." A
    /// claimed == 0 result is ordinary idempotent retry, not an error, not
    /// re-audited.
    ///
    /// ConversationPurgeService joins this method's own transaction
    /// (Option B, Phase 2) — its Message/Notification/non-Consumed-Approval
    /// deletes and the Conversation mutation land in the same transaction
    /// as the claim and the AuditLog write below, committed or rolled back
    /// together. A failure here rolls back only this one conversation's
    /// transaction and rethrows, which — since the outer Phase 2 loop in
    /// EraseCustomerAsync has no try/catch of its own — propagates all the
    /// way out of EraseCustomerAsync: every conversation already committed
    /// earlier in the loop remains committed and audited; every
    /// conversation not yet reached is left completely untouched.
    /// </summary>
    internal async Task TryPurgeAndAuditConversationAsync(
        Guid businessId, Guid customerId, Guid conversationId, Guid changedByUserId,
        Guid erasureCorrelationId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var claimed = await db.Conversations
                .IgnoreQueryFilters()
                .Where(c => c.Id == conversationId && c.BusinessId == businessId && c.CustomerId == customerId && !c.IsAnonymized)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.UpdatedAt, DateTimeOffset.UtcNow), cancellationToken);

            if (claimed == 0)
            {
                await transaction.CommitAsync(cancellationToken);
                return;
            }

            await purgeService.PurgeConversationAsync(conversationId, cancellationToken);

            await auditLogService.LogAsync(
                businessId, AuditEntityTypes.Conversation, conversationId.ToString(),
                AuditEventTypes.ConversationErasurePurged, ActorType.NivoraBusinessStaff,
                changedByUserId: changedByUserId, correlationId: erasureCorrelationId);

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    /// <summary>Deliberately hand-picked, safe field only — never customer
    /// PII, matching RetentionEnforcementService's own
    /// SerializeRetentionAuditValue convention.</summary>
    private static string SerializeErasureAuditValue(int conversationsPurged) =>
        JsonSerializer.Serialize(new { ConversationsPurged = conversationsPurged });
}
