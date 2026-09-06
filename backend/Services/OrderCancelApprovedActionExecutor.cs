using backend.Constants;
using backend.Data;
using backend.Enums;
using backend.Models;
using Microsoft.EntityFrameworkCore;

namespace backend.Services;

/// <summary>
/// The concrete IApprovedActionExecutor implementation for cancelOrder.
/// Implements the full execution ordering in
/// docs/handoffs/step-22z-approval-proposal-approval-lifecycle-design.md
/// §10, steps 3–16: the Approval load + tenant binding + ApprovalNotFound
/// refusal (Step 29), the Status/ExpiresAt/CustomerId checks (steps 6/7/8,
/// per docs/handoffs/step-38-cancelorder-execute-next-slice-design.md §6),
/// the Level 2 verification check (step 9, per
/// docs/handoffs/step-39-cancelorder-execute-steps-9-16-next-slice-design.md
/// §7), ownership verification (step 10), action-hash recomputation and
/// execution-leg key derivation (steps 11/12), the atomic claim (step 13),
/// and — added per
/// docs/handoffs/step-47-execute-steps-14-16-and-staff-http-layer-design.md
/// — the pre-execution ground-truth check and Shopify orderCancel mutation
/// call (steps 14/15, reusing the already-existing, already-tested
/// IShopifyOrderLookupService.GetCancellationStateAsync and
/// IOrderCancellationExecutor.ExecuteAsync unmodified — no new Shopify
/// integration was written for this). Step 16 (reconciliation) requires no
/// code here at all — OrderCancellationReconciliationService already runs
/// independently, unaffected by anything in this class.
///
/// The Approval-load query below deliberately does NOT call
/// IgnoreQueryFilters() — this is a positive, load-bearing requirement, not
/// an oversight. Approval : ITenantOwned, so AppDbContext's ambient global
/// query filter (scoped by the current request's ICurrentTenantAccessor)
/// already makes a cross-tenant approvalId structurally unfindable,
/// returning null exactly as if no such row existed at all — folding tenant
/// binding (step 4 of the ordering) into this same ApprovalNotFound
/// refusal, independently confirmed by docs/handoffs/step-24/25/26. Adding
/// IgnoreQueryFilters() here would silently reopen a cross-tenant read.
///
/// Step 10 (ownership verification), added by
/// docs/handoffs/step-41-cancelorder-execute-steps-10-16-classification-and-atomic-claim-design.md
/// §6 (endorsing docs/handoffs/step-40-cancelorder-execute-step-10-ownership-design.md
/// §5): IShopifyOrderLookupService.LookupOrderOwnerAsync is called first —
/// it throws uncaught on every infra failure (no Integration, token
/// decrypt, non-2xx, malformed JSON, GraphQL errors, order not found),
/// consistent with the infra-failure-stays-exception convention steps
/// 14/15 already use — then IOrderOwnershipAuthorizationService.AuthorizeAsync
/// (a pure DB read against Customers, no Shopify call of its own) is the
/// only source of a refusal for this step. VerifiedOrderOwnership's
/// constructor is internal to IShopifyOrderLookupService, so AuthorizeAsync
/// cannot be invoked without first calling LookupOrderOwnerAsync — there is
/// no ownership-verification path that avoids the Shopify call entirely.
///
/// Steps 11 (action-hash recompute) and 12 (execution-leg key derivation),
/// added by docs/handoffs/step-42-cancelorder-execute-steps-11-12-classification-and-identity-collapse-analysis.md
/// §8: ActionHasher.Derive is recomputed from the same six-value tuple
/// ProposeAsync originally hashed (five fields read back unchanged from
/// the immutable Approval row, plus the freshly re-derived customerId from
/// step 8) and compared byte-for-byte to Approval.ActionHash — on mismatch,
/// refused and audited identically whether the drift is legitimate
/// (customer identity changed) or a genuine tampering attempt (step-22z
/// §13: "a legitimate state-drift denial and a genuine tampering attempt
/// are handled identically"). ApprovalExecutionKeyDeriver.Derive is a pure,
/// always-succeeding function (re-confirmed: zero throw/validation
/// branches) with no refusal path and no audit-write requirement of its
/// own (step-42 §2) — its result is held for step 13 (not yet implemented)
/// to consume. The two derivations share four of their inputs but must
/// never be conflated: see the naming discipline and inline warning at
/// their call sites below, and step-42 §7 for the exact collapse risk this
/// guards against.
///
/// Step 13 (atomic claim + OrderCancellation row creation), added per
/// docs/handoffs/step-43-cancelorder-execute-step-13-atomic-claim-design.md:
/// a single DB transaction containing, in order, the guarded
/// Approved→Consumed ExecuteUpdateAsync (re-checking ExpiresAt a second
/// time, independently of step 7 — step-43 §5), the "execution claimed"
/// AuditLog write (added to the tracked context but not flushed here —
/// step-43 §15a), IOrderCancellationService.RequestCancellationAsync
/// (creating the OrderCancellation row, called unmodified — step-43 §9/§11),
/// and the OrderCancellationId back-write. Zero network/Shopify I/O occurs
/// inside this transaction (step-43 §3). A 0-row claim result and a
/// DuplicatePendingOrderCancellationException are the only two refusal
/// paths, mapping to ApprovalClaimLost and DuplicatePendingCancellation
/// respectively (step-43 §12 addendum) — neither writes an AuditLog row
/// (step-43 §15b).
/// </summary>
public class OrderCancelApprovedActionExecutor(
    AppDbContext db,
    IAuditLogService auditLogService,
    ILevel2VerificationChecker level2Checker,
    IShopifyOrderLookupService orderLookupService,
    IOrderOwnershipAuthorizationService ownershipService,
    IOrderCancellationService orderCancellationService,
    IOrderCancellationExecutor orderCancellationExecutor) : IApprovedActionExecutor
{
    public async Task<ExecutionOutcome> ExecuteApprovedActionAsync(
        Guid approvalId, Guid executingStaffUserId, CancellationToken cancellationToken)
    {
        var approval = await db.Approvals
            .FirstOrDefaultAsync(a => a.Id == approvalId, cancellationToken);

        if (approval is null)
        {
            return new ExecutionOutcome.Refused(
                ExecutionRefusalReason.ApprovalNotFound, "No such approval.");
        }

        // Step 6 — Approval.Status must currently be Approved. Distinct from
        // step 13's own claim-race-loss (0 rows affected by the atomic
        // claim), which returns the separate ApprovalClaimLost value, not
        // this one (step-43 addendum owner decision) — that later path is
        // ordinary concurrency handling between two legitimate callers
        // racing on an otherwise-valid Approval, not a security refusal, and
        // deliberately does NOT receive the AuditLog write below. This
        // check, here, is the only place in this class that writes
        // AuditEventTypes.UnauthorizedActionBlocked for ApprovalNotApproved
        // (step-38 §2a/§6).
        if (approval.Status != ApprovalStatus.Approved)
        {
            await WriteUnauthorizedExecutionBlockedAsync(approval, approval.Status.ToString(), executingStaffUserId, cancellationToken);

            return new ExecutionOutcome.Refused(
                ExecutionRefusalReason.ApprovalNotApproved,
                $"Approval is {approval.Status}, not Approved.");
        }

        // Step 7 — ExpiresAt > now. Informational here; re-checked atomically
        // inside the future step 13 claim's own guard.
        var now = DateTimeOffset.UtcNow;
        if (approval.ExpiresAt <= now)
        {
            await WriteUnauthorizedExecutionBlockedAsync(approval, "Expired", executingStaffUserId, cancellationToken);

            return new ExecutionOutcome.Refused(
                ExecutionRefusalReason.ApprovalExpired, "Approval has expired.");
        }

        // Step 8 — re-derive CustomerId fresh from the live Conversation,
        // never trust a stale value: Conversation.CustomerId is mutable
        // (docs/handoffs/step-22-ai-action-safety-model-owner-decisions.md §3
        // item 8). IgnoreQueryFilters() + explicit BusinessId predicate here
        // is deliberate and safe, per step-24 §3's resolved reasoning: unlike
        // the Approval load above, this query's tenant safety comes from the
        // explicit predicate (sourced from the already-tenant-verified
        // approval.BusinessId), not from the ambient filter.
        //
        // No AuditLog write for this refusal — genuinely unresolved from
        // source (step-38 §2a/§7): unlike steps 6/7, this is not a check on
        // Approval's own authorization state, and no document establishes
        // whether it counts as an "execution-time security refusal." Left
        // undecided here, not silently resolved either way.
        var conversation = await db.Conversations
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == approval.ConversationId && c.BusinessId == approval.BusinessId, cancellationToken);

        if (conversation?.CustomerId is not Guid)
        {
            return new ExecutionOutcome.Refused(
                ExecutionRefusalReason.CustomerUnresolved,
                "Conversation no longer has a resolved customer.");
        }

        // Named capture of the value step 8's own check already validated
        // above (not a new check, not a behavior change) — step 10 needs
        // this value by name; step 8's existing refusal condition and its
        // return statement are both left untouched.
        var customerId = conversation!.CustomerId!.Value;

        // Step 9 — Level 2 verification, execution-time only per Decision 2
        // (ADOPTED, docs/handoffs/step-22-ai-action-safety-model-owner-decisions.md
        // §0 item 2). A pure read (ILevel2VerificationChecker performs only
        // local DB reads, no external call, no infra-failure branch to
        // classify — step-39 §2). AuditLog write required — DECIDED by
        // explicit owner decision, not source-derived, per step-39 §5a: no
        // shared mechanical gate, no draft naming, and no standalone
        // confirming statement exists for this refusal the way steps 6/7/11
        // each have one, but the owner chose parity with steps 6/7 anyway.
        var level2Verified = await level2Checker.IsVerifiedAsync(
            approval.BusinessId, approval.ConversationId, cancellationToken);
        if (!level2Verified)
        {
            await WriteUnauthorizedExecutionBlockedAsync(approval, "Level2VerificationFailed", executingStaffUserId, cancellationToken);

            return new ExecutionOutcome.Refused(
                ExecutionRefusalReason.Level2VerificationFailed,
                "Conversation is not currently Level 2 verified.");
        }

        // Step 10 — ownership verification. LookupOrderOwnerAsync throws
        // uncaught on infra failure (missing/inactive Integration, token
        // decrypt failure, non-2xx, malformed JSON, GraphQL errors, order
        // not found) — that throw is intentionally allowed to propagate out
        // of this method, matching the same infra-failure-stays-exception
        // convention steps 14/15 already use (step-40 §5/§9, step-41 §2).
        // AuthorizeAsync is a pure DB read against Customers (no Shopify
        // call of its own — IOrderOwnershipAuthorizationService's own doc
        // comment) and is the only source of a refusal for this step.
        // VerifiedOrderOwnership's constructor is internal to
        // IShopifyOrderLookupService, so AuthorizeAsync cannot be invoked
        // without first calling LookupOrderOwnerAsync — there is no
        // ownership-verification path that avoids the Shopify call
        // entirely.
        var ownership = await orderLookupService.LookupOrderOwnerAsync(
            approval.BusinessId, approval.ResourceId, cancellationToken);
        var ownershipResult = await ownershipService.AuthorizeAsync(
            approval.BusinessId, customerId, ownership, cancellationToken);
        if (!ownershipResult.IsAuthorized)
        {
            await WriteUnauthorizedExecutionBlockedAsync(
                approval, $"OwnershipVerificationFailed:{ownershipResult.Reason}", executingStaffUserId, cancellationToken);

            return new ExecutionOutcome.Refused(
                ExecutionRefusalReason.OwnershipVerificationFailed,
                $"Order ownership verification failed: {ownershipResult.Reason}.");
        }

        // Step 11 — recompute the action hash from the same six-value tuple
        // ProposeAsync originally hashed: five fields read back unchanged
        // from the immutable Approval row, plus the freshly re-derived
        // customerId (step 8) — never approval.CustomerId, which may have
        // drifted since proposal time. Compared byte-for-byte to the
        // stored Approval.ActionHash. On mismatch, refuse unconditionally
        // and audit identically regardless of cause — step-22z §13: "a
        // legitimate state-drift denial and a genuine tampering attempt
        // are handled identically (fail closed)." No special-casing by
        // cause is introduced here.
        var recomputedActionHash = ActionHasher.Derive(
            approval.BusinessId, approval.ConversationId, customerId,
            approval.ToolName, approval.ResourceId, approval.CanonicalArguments);
        if (recomputedActionHash != approval.ActionHash)
        {
            await WriteUnauthorizedExecutionBlockedAsync(approval, "ActionHashMismatch", executingStaffUserId, cancellationToken);

            return new ExecutionOutcome.Refused(
                ExecutionRefusalReason.ActionHashMismatch,
                "Recomputed action hash does not match the approved hash.");
        }

        // Step 12 — derive this execution attempt's own identity key, for
        // step 13 (not yet implemented) to consume as
        // OrderCancellation.IdempotencyKey. A pure, always-succeeding
        // function (ApprovalExecutionKeyDeriver.Derive has zero throw/
        // validation branches) — no refusal path, no AuditLog write, both
        // confirmed source-side (step-42 §2), not merely assumed.
        //
        // Do NOT reuse recomputedActionHash (step 11's output) as an
        // idempotency key input for step 13 — they serve different
        // purposes and mixing them produces a silent, hard-to-detect bug
        // (the wrong uniqueness guard fires without error). See step-42
        // §7 for the exact collapse risk this guards against.
        var approvalExecutionKey = ApprovalExecutionKeyDeriver.Derive(
            approval.BusinessId, approval.ToolName, approval.ResourceId,
            approval.CanonicalArguments, approval.Id);

        // Step 13 — ATOMIC: claim the Approval (Approved->Consumed) and
        // create the OrderCancellation execution-intent row in one DB
        // transaction, per
        // docs/handoffs/step-43-cancelorder-execute-step-13-atomic-claim-design.md.
        // Zero network/Shopify I/O occurs inside this transaction (§3 of
        // that document) — RequestCancellationAsync only ever performs a
        // local DB write on this same scoped AppDbContext (§3a), enlisting
        // in the ambient transaction opened below.
        //
        // The claim's own WHERE clause re-checks ExpiresAt > now a second
        // time, independently of step 7's earlier check — reusing the same
        // `now` captured at step 7, which is safe because reusing an older
        // timestamp can only make this predicate harder to satisfy, never
        // easier (step-43 §5). If this affects 0 rows, either a concurrent
        // Execute call won the claim first or the Approval's ExpiresAt
        // column changed to a value at or before `now` in the interim —
        // the two causes are indistinguishable from the affected-row count
        // alone, so both map to the single ApprovalClaimLost value
        // (step-43 §4/§12 addendum), never ApprovalNotApproved.
        //
        // customerId (step 8) and approvalExecutionKey (step 12) are reused
        // here, never re-derived or recomputed — re-deriving customerId
        // would bind a value step 11 never actually hash-verified (step-43
        // §6); recomputing an idempotency key from recomputedActionHash
        // instead of approvalExecutionKey would collapse two distinct
        // identity concepts (step-42 §7, restated step-43 §8) and must
        // never happen.
        Guid orderCancellationId;
        await using (var tx = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            var claimed = await db.Approvals
                .Where(a => a.Id == approvalId && a.Status == ApprovalStatus.Approved && a.ExpiresAt > now)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(a => a.Status, ApprovalStatus.Consumed)
                    .SetProperty(a => a.ConsumedAt, now), cancellationToken);

            if (claimed == 0)
            {
                await tx.RollbackAsync(cancellationToken);
                return new ExecutionOutcome.Refused(
                    ExecutionRefusalReason.ApprovalClaimLost, "ApprovalClaimLost");
            }

            // "Execution claimed" — required durable AuditLog row (step-22z
            // §18). Added to the same tracked scoped AppDbContext but NOT
            // flushed here: RequestCancellationAsync's own internal
            // SaveChangesAsync call below flushes this row together with
            // the new OrderCancellation row, in the same round trip, still
            // inside this open transaction (step-43 §15a) — so a
            // DuplicatePendingOrderCancellationException on that same call
            // rolls both back together; no stray audit row survives a
            // refused attempt.
            await auditLogService.LogAsync(
                approval.BusinessId, AuditEntityTypes.Approval, approval.Id.ToString(),
                AuditEventTypes.ExecutionClaimed, ActorType.NivoraBusinessStaff,
                changedByUserId: executingStaffUserId, cancellationToken: cancellationToken);

            // Parsed once at proposal time (Step 23N); Execute re-derives
            // only what OrderCancelProposalTool.ParseInput originally
            // captured in CanonicalArguments, not any live AI input
            // (Execute has none).
            var (restock, method, reason) =
                ToolArgumentCanonicalizer.DecodeOrderCancellationArgs(approval.CanonicalArguments);

            try
            {
                var result = await orderCancellationService.RequestCancellationAsync(
                    approval.BusinessId, customerId, approval.ConversationId,
                    approval.ResourceId, customerReason: null,
                    approvalExecutionKey, restock, method, reason, cancellationToken);
                orderCancellationId = result.OrderCancellationId;
            }
            catch (DuplicatePendingOrderCancellationException ex)
            {
                // No AuditLog write on this path (step-43 addendum owner
                // decision, §15b) — a different, independently-legitimate
                // Approval already has an unresolved attempt for this same
                // order; this is ordinary concurrency handling between two
                // legitimate callers, not a security refusal.
                await tx.RollbackAsync(cancellationToken);
                return new ExecutionOutcome.Refused(
                    ExecutionRefusalReason.DuplicatePendingCancellation,
                    $"DuplicatePendingCancellation:{ex.ExternalOrderId}");
            }

            // Unconditional — no status predicate needed beyond Id, since
            // the claim's own UPDATE above already holds a row-level lock
            // on this exact Approval for the remainder of this transaction
            // under PostgreSQL READ COMMITTED (step-24 §4b).
            await db.Approvals
                .Where(a => a.Id == approvalId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(a => a.OrderCancellationId, orderCancellationId), cancellationToken);

            await tx.CommitAsync(cancellationToken);
        }

        // Step 14 — the first action strictly outside step 13's transaction
        // (step-22z §10/§16, step-38 §2 row 14): reuse the existing,
        // unmodified ground-truth check GetCancellationStateAsync already
        // uses for reconciliation. Throws uncaught on infra failure, same
        // convention as step 10's Shopify call. If Shopify already reports
        // the order cancelled, this Approval's own attempt did not cause it
        // (Decision 6, adopted, unweakened) — resolve directly to
        // ExternallySatisfied and never call the mutation executor at all.
        var groundTruth = await orderLookupService.GetCancellationStateAsync(
            approval.BusinessId, approval.ResourceId, cancellationToken);
        if (groundTruth.CancelledAt is not null)
        {
            await orderCancellationService.MarkConfirmedAsync(orderCancellationId, cancellationToken);
            return new ExecutionOutcome.Executed(
                orderCancellationId, ExecutionResultStatus.ExternallySatisfied,
                "Order was already cancelled externally.");
        }

        // Step 15 — the existing, unmodified mutation executor. Throws
        // uncaught on infra failure; on a genuine Shopify-reported rejection
        // it has already called MarkFailedAsync internally before returning;
        // otherwise it leaves the row Pending for reconciliation (step 16,
        // unchanged, running independently — no code here). Re-read the row
        // fresh (AsNoTracking) rather than trust any in-memory value, since
        // ExecuteAsync mutates it via its own separate DB call, not via this
        // method's own tracked context.
        await orderCancellationExecutor.ExecuteAsync(orderCancellationId, cancellationToken);

        var finalRow = await db.OrderCancellations
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstAsync(x => x.Id == orderCancellationId, cancellationToken);

        return finalRow.Status switch
        {
            OrderCancellationStatus.Failed =>
                new ExecutionOutcome.Executed(orderCancellationId, ExecutionResultStatus.Failed, finalRow.FailureReason),
            _ =>
                new ExecutionOutcome.Executed(orderCancellationId, ExecutionResultStatus.UnknownNeedsReconciliation, null),
        };
    }

    /// <summary>
    /// Steps 6/7/9/10/11's shared AuditLog write — reuses the existing
    /// AuditEventTypes.UnauthorizedActionBlocked constant per step-22z §18's
    /// explicit instruction, not a new one. LogAsync does not call
    /// SaveChangesAsync itself, so this method does, immediately — these
    /// checks have no open transaction to share it with.
    ///
    /// ActorType.NivoraBusinessStaff / ChangedByUserId=executingStaffUserId,
    /// per docs/handoffs/step-45-staff-actor-identity-threading-design.md
    /// (owner decision confirmed in that document's addendum) — corrected
    /// from the prior ActorType.NivoraSystem/null placeholder. ActorType
    /// records the origin of the request, not who evaluated the refusal
    /// condition (Nivora's own backend logic evaluates every write this
    /// codebase makes); the origin of every refusal this method records is,
    /// today and for every anticipated caller, the executing staff member's
    /// own authenticated HTTP request — never a scheduled job. See step-45
    /// §5 for why the prior WidgetConversationService.cs analogy does not
    /// hold (that call site has no staff request to attribute to at all;
    /// this one always does).
    ///
    /// ConversationId is deliberately left unset: AuditLog.ConversationId's
    /// own doc comment reserves that column for Actor == Ai specifically —
    /// not populated here, consistent with that stated contract.
    /// </summary>
    private async Task WriteUnauthorizedExecutionBlockedAsync(
        Approval approval, string newValue, Guid executingStaffUserId, CancellationToken cancellationToken)
    {
        await auditLogService.LogAsync(
            approval.BusinessId, AuditEntityTypes.Approval, approval.Id.ToString(),
            AuditEventTypes.UnauthorizedActionBlocked, ActorType.NivoraBusinessStaff,
            changedByUserId: executingStaffUserId, newValue: newValue, cancellationToken: cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }
}
