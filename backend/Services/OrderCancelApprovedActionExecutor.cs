using backend.Constants;
using backend.Data;
using backend.Enums;
using backend.Models;
using Microsoft.EntityFrameworkCore;

namespace backend.Services;

/// <summary>
/// The concrete IApprovedActionExecutor implementation for cancelOrder.
/// Implements steps 3–9 of the execution ordering in
/// docs/handoffs/step-22z-approval-proposal-approval-lifecycle-design.md
/// §10: the Approval load + tenant binding + ApprovalNotFound refusal
/// (Step 29), the Status/ExpiresAt/CustomerId checks (steps 6/7/8, per
/// docs/handoffs/step-38-cancelorder-execute-next-slice-design.md §6), and
/// — added by this pass, per
/// docs/handoffs/step-39-cancelorder-execute-steps-9-16-next-slice-design.md
/// §7 — the Level 2 verification check (step 9). Every step past that —
/// ownership, hash recompute, the atomic claim, and the Shopify call — is
/// explicitly NOT implemented by this pass and throws
/// NotImplementedException if reached. See step-39 for the full design
/// this class will eventually satisfy in full.
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
/// </summary>
public class OrderCancelApprovedActionExecutor(
    AppDbContext db,
    IAuditLogService auditLogService,
    ILevel2VerificationChecker level2Checker,
    IShopifyOrderLookupService orderLookupService,
    IOrderOwnershipAuthorizationService ownershipService) : IApprovedActionExecutor
{
    public async Task<ExecutionOutcome> ExecuteApprovedActionAsync(
        Guid approvalId, CancellationToken cancellationToken)
    {
        var approval = await db.Approvals
            .FirstOrDefaultAsync(a => a.Id == approvalId, cancellationToken);

        if (approval is null)
        {
            return new ExecutionOutcome.Refused(
                ExecutionRefusalReason.ApprovalNotFound, "No such approval.");
        }

        // Step 6 — Approval.Status must currently be Approved. Distinct from
        // step 13's own (not yet implemented) TOCTOU claim-race-loss, which
        // returns this same ApprovalNotApproved value from a different code
        // location (the atomic claim) and must NOT receive the AuditLog write
        // below — that later path is ordinary concurrency handling between
        // two legitimate callers racing on an otherwise-valid Approval, not a
        // security refusal. This check, here, is the only place in this
        // class that writes AuditEventTypes.UnauthorizedActionBlocked for
        // ApprovalNotApproved (step-38 §2a/§6).
        if (approval.Status != ApprovalStatus.Approved)
        {
            await WriteUnauthorizedExecutionBlockedAsync(approval, approval.Status.ToString(), cancellationToken);

            return new ExecutionOutcome.Refused(
                ExecutionRefusalReason.ApprovalNotApproved,
                $"Approval is {approval.Status}, not Approved.");
        }

        // Step 7 — ExpiresAt > now. Informational here; re-checked atomically
        // inside the future step 13 claim's own guard.
        var now = DateTimeOffset.UtcNow;
        if (approval.ExpiresAt <= now)
        {
            await WriteUnauthorizedExecutionBlockedAsync(approval, "Expired", cancellationToken);

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
            await WriteUnauthorizedExecutionBlockedAsync(approval, "Level2VerificationFailed", cancellationToken);

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
                approval, $"OwnershipVerificationFailed:{ownershipResult.Reason}", cancellationToken);

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
            await WriteUnauthorizedExecutionBlockedAsync(approval, "ActionHashMismatch", cancellationToken);

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

        throw new NotImplementedException(
            "OrderCancelApprovedActionExecutor implements steps 3-12 of the execution ordering so far " +
            "(Approval load/ApprovalNotFound + tenant binding, status/expiry checks with AuditLog " +
            "writes, customer re-derivation, Level 2 verification with an AuditLog write, ownership " +
            "verification with an AuditLog write, action-hash recomputation with an AuditLog write, " +
            "execution-leg key derivation). Every step beyond that — the atomic Approved->Consumed " +
            "claim, and the Shopify orderCancel mutation call — is deliberately not yet implemented. " +
            "See docs/handoffs/step-42-cancelorder-execute-steps-11-12-classification-and-identity-collapse-analysis.md " +
            "for the full design this method will eventually satisfy.");
    }

    /// <summary>
    /// Steps 6/7's shared AuditLog write — reuses the existing
    /// AuditEventTypes.UnauthorizedActionBlocked constant per step-22z §18's
    /// explicit instruction, not a new one. LogAsync does not call
    /// SaveChangesAsync itself, so this method does, immediately — these
    /// checks have no open transaction to share it with.
    ///
    /// ActorType.NivoraSystem / ChangedByUserId=null is a deliberate
    /// placeholder, not an oversight: IApprovedActionExecutor's locked
    /// signature (approvalId, CancellationToken only) carries no caller-
    /// identity parameter, so the specific staff member who triggered this
    /// refusal cannot be recorded here. Threading a staff actor into
    /// Execute's signature is an explicitly deferred design question
    /// (step-38 §6/§9) — NivoraSystem is used because this determination is
    /// made by Nivora's own backend logic, not a captured human/AI/customer
    /// identity, matching this codebase's existing precedent for exactly
    /// that shape of event (WidgetConversationService.cs's own
    /// ActorType.NivoraSystem usage for a backend-determined outcome with no
    /// captured actor, not merely a scheduled job).
    ///
    /// ConversationId is deliberately left unset: AuditLog.ConversationId's
    /// own doc comment reserves that column for Actor == Ai specifically —
    /// not populated here, consistent with that stated contract.
    /// </summary>
    private async Task WriteUnauthorizedExecutionBlockedAsync(
        Approval approval, string newValue, CancellationToken cancellationToken)
    {
        await auditLogService.LogAsync(
            approval.BusinessId, AuditEntityTypes.Approval, approval.Id.ToString(),
            AuditEventTypes.UnauthorizedActionBlocked, ActorType.NivoraSystem,
            changedByUserId: null, newValue: newValue, cancellationToken: cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }
}
