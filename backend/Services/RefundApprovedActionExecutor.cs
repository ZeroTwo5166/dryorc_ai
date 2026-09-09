using System.Text.Json;
using backend.Constants;
using backend.Data;
using backend.Enums;
using backend.Models;
using Microsoft.EntityFrameworkCore;

namespace backend.Services;

/// <summary>
/// The concrete IApprovedActionExecutor implementation for refundOrder —
/// Step refundOrder Phase 3. Mirrors OrderCancelApprovedActionExecutor's
/// overall shape and execution-ordering discipline (see that class's own doc
/// comment for the full step-numbering convention this reuses), diverging
/// only where docs/handoffs/step-refundorder-design.md explicitly requires
/// it. Section references below are to that design document unless noted.
///
/// ToolName identifies this executor to ApprovalsController's multi-executor
/// routing (design §3.3, already implemented in
/// IApprovedActionExecutor/ApprovalsController ahead of this phase).
///
/// Divergences from OrderCancelApprovedActionExecutor's own step ordering,
/// stated explicitly rather than left for a reader to infer:
///
/// 1. Owner Decision 3 / Layer 2 (§14.3) — a new, refund-specific
///    Business.RefundToolEnabled execution-time check, with no cancelOrder
///    analogue at all (CancelOrderToolEnabled is exposure-time-only).
///    Checked early, alongside the Approval's own Status/ExpiresAt checks,
///    since it is the cheapest possible business-configuration precondition
///    (a single Business row read, no Shopify call, no re-derived
///    CustomerId needed) and Decision 3 treats it as the primary kill
///    switch for this tool.
///
/// 2. The refundable-quantity/amount preflight (§8.3/§17, via
///    IShopifyRefundLookupService) runs BEFORE the atomic claim, not after
///    it the way cancelOrder's ground-truth check (step 14) runs after its
///    own atomic claim (step 13). This is not a stylistic choice: Phase 1's
///    already-shipped IRefundService.RequestRefundAsync requires each
///    RefundItemLine's ResolvedAmount to already be a positive, resolved
///    value before it is ever called (it throws ArgumentException
///    otherwise) — so the Shopify amount-resolution call must happen before
///    the local Refund row can be created at all. This is the same "late,
///    immediately-before-mutation" check design §17 asks for in spirit
///    (nothing but the local, non-Shopify atomic claim happens between this
///    check and the actual refundCreate call), just necessarily positioned
///    before local persistence rather than after it, given Phase 1's own
///    established contract. Per design §17/research Q10, this still only
///    narrows — does not close — the disclosed distinct-Approval race: two
///    different Approvals can each pass this preflight check independently
///    before either submits its own refundCreate mutation.
///
/// 3. No separate "already satisfied externally" ground-truth check exists
///    (unlike cancelOrder's own step 14) — the refundable-quantity preflight
///    itself naturally covers this case: an order already fully refunded
///    for a requested line item reports refundableQuantity 0, which this
///    executor refuses via InsufficientRefundableQuantity before ever
///    calling refundCreate. No separate mechanism is invented for this.
///
/// 4. Terminal-state audit logging (RefundIssued/RefundFailed) is written
///    here, after the Shopify call resolves — a deliberate divergence from
///    cancelOrder's own shipped, under-audited precedent (design §15.1/§15.2:
///    OrderCancelApprovedActionExecutor writes no audit row at all for its
///    own terminal Confirmed/Failed transition). Payload is minimal,
///    hand-picked fields only (§15.3): ExternalOrderId, Method, TotalAmount,
///    Currency, Status — never Customer fields, never raw
///    CanonicalArguments.
///
/// 5. No DuplicatePendingRefundException-equivalent catch exists around the
///    RequestRefundAsync call, unlike cancelOrder's own
///    DuplicatePendingCancellation handling — IRefundService deliberately
///    has no such exception (design §9.4/§17: refunds are naturally
///    repeatable, no blanket per-order Pending-guard). RequestRefundAsync
///    instead returns an IdempotentOperationOutcome; this executor's own
///    handling of that outcome is documented at its call site below.
/// </summary>
public class RefundApprovedActionExecutor(
    AppDbContext db,
    IAuditLogService auditLogService,
    ILevel2VerificationChecker level2Checker,
    IShopifyOrderLookupService orderLookupService,
    IOrderOwnershipAuthorizationService ownershipService,
    IShopifyRefundLookupService refundLookupService,
    IRefundService refundService,
    IRefundExecutor refundExecutor) : IApprovedActionExecutor
{
    public string ToolName => RefundProposalTool.ToolName;

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

        // Step 6-equivalent — Approval.Status must currently be Approved.
        if (approval.Status != ApprovalStatus.Approved)
        {
            await WriteUnauthorizedExecutionBlockedAsync(approval, approval.Status.ToString(), executingStaffUserId, cancellationToken);

            return new ExecutionOutcome.Refused(
                ExecutionRefusalReason.ApprovalNotApproved,
                $"Approval is {approval.Status}, not Approved.");
        }

        // Step 7-equivalent — ExpiresAt > now. Re-checked atomically inside
        // the atomic claim's own guard below.
        var now = DateTimeOffset.UtcNow;
        if (approval.ExpiresAt <= now)
        {
            await WriteUnauthorizedExecutionBlockedAsync(approval, "Expired", executingStaffUserId, cancellationToken);

            return new ExecutionOutcome.Refused(
                ExecutionRefusalReason.ApprovalExpired, "Approval has expired.");
        }

        // Owner Decision 3, Layer 2 (design §14.3) — the cheapest possible
        // business-configuration precondition, checked before anything else
        // that requires re-deriving CustomerId or touching Shopify. No
        // AuditLog write — a business-configuration fact, not a check on the
        // Approval's own authorization state, matching
        // ShopifyIntegrationUnavailable's own identical reasoning below.
        // Human staff approval never bypasses this: an Approval proposed and
        // even approved while this was true is still refused here if the
        // business has since disabled it.
        var business = await db.Businesses
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == approval.BusinessId, cancellationToken);

        if (business is null || !business.RefundToolEnabled)
        {
            return new ExecutionOutcome.Refused(
                ExecutionRefusalReason.RefundToolDisabled,
                "Business.RefundToolEnabled is not currently enabled for this business.");
        }

        // Step 8-equivalent — re-derive CustomerId fresh from the live
        // Conversation, never trust a stale value. Identical
        // IgnoreQueryFilters()+explicit-BusinessId-predicate discipline as
        // OrderCancelApprovedActionExecutor's own identical query.
        var conversation = await db.Conversations
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == approval.ConversationId && c.BusinessId == approval.BusinessId, cancellationToken);

        if (conversation?.CustomerId is not Guid)
        {
            return new ExecutionOutcome.Refused(
                ExecutionRefusalReason.CustomerUnresolved,
                "Conversation no longer has a resolved customer.");
        }

        var customerId = conversation!.CustomerId!.Value;

        // Step 9-equivalent — Level 2 verification, execution-time only,
        // identical reasoning/AuditLog-write discipline to cancelOrder's own
        // step 9 (design §13).
        var level2Verified = await level2Checker.IsVerifiedAsync(
            approval.BusinessId, approval.ConversationId, cancellationToken);
        if (!level2Verified)
        {
            await WriteUnauthorizedExecutionBlockedAsync(approval, "Level2VerificationFailed", executingStaffUserId, cancellationToken);

            return new ExecutionOutcome.Refused(
                ExecutionRefusalReason.Level2VerificationFailed,
                "Conversation is not currently Level 2 verified.");
        }

        // Active-Shopify-integration precondition (design §8.1), identical
        // predicate and reasoning to cancelOrder's own step-48 pre-check —
        // not the ambient global query filter, an explicit
        // IgnoreQueryFilters()+BusinessId predicate.
        var hasActiveShopifyIntegration = await db.Integrations
            .IgnoreQueryFilters()
            .AnyAsync(
                i => i.BusinessId == approval.BusinessId
                    && i.Platform == ShopifyIntegrationOAuthProvider.PlatformName
                    && i.IsActive,
                cancellationToken);
        if (!hasActiveShopifyIntegration)
        {
            return new ExecutionOutcome.Refused(
                ExecutionRefusalReason.ShopifyIntegrationUnavailable,
                "No active Shopify integration exists for this business.");
        }

        // Step 10-equivalent — ownership verification, identical reasoning
        // and reuse to cancelOrder's own step 10 (design §7).
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

        // Step 11-equivalent — recompute the action hash from the same
        // six-value tuple ProposeAsync originally hashed, using the freshly
        // re-derived customerId (never approval.CustomerId).
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

        // Step 12-equivalent — derive this execution attempt's own identity
        // key. Reused below both as Refund.IdempotencyKey (Nivora-side) and
        // as refundCreate's own @idempotent directive key (Shopify-side,
        // design §9.5/research Q11) — a single Phase 3 engineering decision
        // this design left open ("which key (if any) Nivora passes as
        // @idempotent remains a Phase 3 engineering decision") now made
        // explicitly: reusing the same execution-leg identity for both is
        // the natural choice, since both represent "this Approval's one
        // legitimate execution attempt."
        var approvalExecutionKey = ApprovalExecutionKeyDeriver.Derive(
            approval.BusinessId, approval.ToolName, approval.ResourceId,
            approval.CanonicalArguments, approval.Id);

        // Decode the item/quantity/method contract Phase 2's ProposeAsync
        // originally canonicalized — no raw AI input is available at
        // execution time (design §5.1/§9.1, Owner Decision 1).
        var (method, requestedItems) = ToolArgumentCanonicalizer.DecodeRefundArgs(approval.CanonicalArguments);

        // Shopify refundable-quantity/amount preflight (design §8.3/§17,
        // research Q1/Q2/Q10) — see this class's own doc comment, divergence
        // 2, for why this runs before the atomic claim rather than after it.
        // Throws uncaught on infra failure, same convention as the ownership
        // lookup above.
        ShopifyRefundPreflightResult preflight;
        try
        {
            preflight = await refundLookupService.GetRefundPreflightAsync(
                approval.BusinessId, approval.ResourceId, requestedItems, cancellationToken);
        }
        catch (LineItemNotFoundOnOrderException ex)
        {
            return new ExecutionOutcome.Refused(
                ExecutionRefusalReason.RefundLineItemNotOnOrder,
                $"RefundLineItemNotOnOrder:{ex.ExternalLineItemId}");
        }

        var insufficientLine = preflight.Lines.FirstOrDefault(l => l.RequestedQuantity > l.RefundableQuantity);
        if (insufficientLine is not null)
        {
            return new ExecutionOutcome.Refused(
                ExecutionRefusalReason.InsufficientRefundableQuantity,
                $"InsufficientRefundableQuantity:{insufficientLine.ExternalLineItemId}:" +
                $"requested={insufficientLine.RequestedQuantity}:refundable={insufficientLine.RefundableQuantity}");
        }

        var resolvedItems = preflight.Lines
            .Select(l => new RefundItemLine(l.ExternalLineItemId, l.RequestedQuantity, l.ResolvedAmount))
            .ToList();

        // Step 13-equivalent — ATOMIC: claim the Approval (Approved->Consumed)
        // and create the Refund/RefundItem execution-intent rows in one DB
        // transaction. Zero Shopify network I/O occurs inside this
        // transaction — RequestRefundAsync only ever performs local DB
        // writes on this same scoped AppDbContext, enlisting in the ambient
        // transaction, identical discipline to cancelOrder's own step 13.
        Guid refundId;
        IdempotentOperationOutcome requestOutcome;
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

            await auditLogService.LogAsync(
                approval.BusinessId, AuditEntityTypes.Approval, approval.Id.ToString(),
                AuditEventTypes.ExecutionClaimed, ActorType.NivoraBusinessStaff,
                changedByUserId: executingStaffUserId, cancellationToken: cancellationToken);

            // IRefundService deliberately has no DuplicatePendingRefundException
            // analogue (design §9.4/§17) — it instead returns an
            // IdempotentOperationOutcome. Given the atomic claim immediately
            // above guarantees this Approval reaches this line at most once
            // (a second concurrent Execute call would have lost the claim),
            // this key is expected to be fresh (InProgress) on every normal
            // path. A non-InProgress outcome here would mean a prior attempt
            // against this exact same Approval already ran RequestRefundAsync
            // to completion in an earlier, non-committed or crashed attempt —
            // handled below by skipping the Shopify call entirely rather than
            // risking a second refundCreate submission.
            var result = await refundService.RequestRefundAsync(
                approval.BusinessId, customerId, approval.ConversationId,
                approval.ResourceId, customerReason: null,
                approvalExecutionKey, method, preflight.CurrencyCode, resolvedItems, cancellationToken);

            if (result.Outcome == IdempotentOperationOutcome.KeyReusedWithDifferentIntent)
            {
                // Structurally should never happen: approvalExecutionKey is
                // derived from the same CanonicalArguments this same method
                // just decoded, so the "intent" comparison inside
                // RequestRefundAsync can never disagree with itself here.
                // Refused loudly rather than silently proceeding on an
                // assumption that has just been proven false.
                await tx.RollbackAsync(cancellationToken);
                throw new InvalidOperationException(
                    "RequestRefundAsync reported KeyReusedWithDifferentIntent for this Approval's own " +
                    "deterministically-derived execution key — this should be structurally impossible.");
            }

            refundId = result.RefundId;
            requestOutcome = result.Outcome;

            await db.Approvals
                .Where(a => a.Id == approvalId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(a => a.RefundId, refundId), cancellationToken);

            await tx.CommitAsync(cancellationToken);
        }

        // Step 15-equivalent — the actual refundCreate mutation. Only called
        // when this transaction's own RequestRefundAsync call left the row
        // genuinely Pending for the first time (InProgress) — if a prior
        // attempt already resolved this same idempotency key to Confirmed or
        // Failed, that resolution is authoritative and refundCreate is never
        // called a second time for it.
        if (requestOutcome == IdempotentOperationOutcome.InProgress)
        {
            await refundExecutor.ExecuteAsync(refundId, approvalExecutionKey, cancellationToken);
        }

        var finalRow = await db.Refunds
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstAsync(x => x.Id == refundId, cancellationToken);

        // Divergence 4 (this class's own doc comment) — terminal-state audit
        // logging, design §15.2's own recommendation. Minimal payload only
        // (§15.3): never Customer fields, never raw CanonicalArguments.
        var (eventType, resultStatus) = finalRow.Status switch
        {
            RefundStatus.Confirmed => (AuditEventTypes.RefundIssued, ExecutionResultStatus.Confirmed),
            RefundStatus.Failed => (AuditEventTypes.RefundFailed, ExecutionResultStatus.Failed),
            _ => throw new InvalidOperationException(
                $"Refund {refundId} is unexpectedly {finalRow.Status} after a completed executor call — " +
                "refundCreate's confirmed synchronicity means this should be unreachable."),
        };

        var auditPayload = JsonSerializer.Serialize(new
        {
            finalRow.ExternalOrderId,
            Method = finalRow.Method.ToString(),
            finalRow.TotalAmount,
            finalRow.Currency,
            Status = finalRow.Status.ToString(),
        });
        await auditLogService.LogAsync(
            approval.BusinessId, AuditEntityTypes.Refund, refundId.ToString(),
            eventType, ActorType.NivoraBusinessStaff,
            changedByUserId: executingStaffUserId, newValue: auditPayload, cancellationToken: cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        return new ExecutionOutcome.Executed(
            refundId, resultStatus, finalRow.Status == RefundStatus.Failed ? finalRow.FailureReason : null);
    }

    /// <summary>Mirrors OrderCancelApprovedActionExecutor's own identical
    /// helper — see that class's doc comment for the full rationale.</summary>
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
