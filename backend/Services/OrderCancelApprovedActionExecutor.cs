using backend.Constants;
using backend.Data;
using backend.Enums;
using backend.Models;
using Microsoft.EntityFrameworkCore;

namespace backend.Services;

/// <summary>
/// The concrete IApprovedActionExecutor implementation for cancelOrder.
/// Implements steps 3–8 of the execution ordering in
/// docs/handoffs/step-22z-approval-proposal-approval-lifecycle-design.md
/// §10: the Approval load + tenant binding + ApprovalNotFound refusal
/// (Step 29), and — added by this pass, per
/// docs/handoffs/step-38-cancelorder-execute-next-slice-design.md §6 — the
/// Status/ExpiresAt/CustomerId checks (steps 6/7/8). Every step past that
/// — Level 2, ownership, hash recompute, the atomic claim, and the Shopify
/// call — is explicitly NOT implemented by this pass and throws
/// NotImplementedException if reached. See step-38 for the full design
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
/// </summary>
public class OrderCancelApprovedActionExecutor(AppDbContext db, IAuditLogService auditLogService) : IApprovedActionExecutor
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

        throw new NotImplementedException(
            "OrderCancelApprovedActionExecutor implements steps 3-8 of the execution ordering so far " +
            "(Approval load/ApprovalNotFound + tenant binding, status/expiry checks with AuditLog " +
            "writes, customer re-derivation). Every step beyond that — Level 2 verification, ownership " +
            "verification, action-hash recomputation, the atomic Approved->Consumed claim, and the " +
            "Shopify orderCancel call — is deliberately not yet implemented. See " +
            "docs/handoffs/step-38-cancelorder-execute-next-slice-design.md for the full design this " +
            "method will eventually satisfy.");
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
