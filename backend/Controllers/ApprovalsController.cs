using System.Security.Claims;
using backend.Auth;
using backend.Constants;
using backend.Data;
using backend.Enums;
using backend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace backend.Controllers;

public record ApprovalSummaryResponse(
    Guid Id, string ToolName, string ResourceId, string DisplaySummary,
    DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

/// <summary>
/// Step 47 addendum decision 3: Reason is optional, staff-supplied free
/// text — never required, never validated beyond being a string.
/// </summary>
public record DenyApprovalRequest(string? Reason);

/// <summary>
/// Flat wire shape over IApprovedActionExecutor's closed ExecutionOutcome
/// hierarchy (locked at step-44) — Result is either "Refused" or one of
/// ExecutionResultStatus's own member names ("ExternallySatisfied"/"Failed"/
/// "UnknownNeedsReconciliation"); RefusalReason is populated only when
/// Result == "Refused". Exact wire shape was explicitly left as this
/// controller's own engineering judgment by
/// docs/handoffs/step-47-execute-steps-14-16-and-staff-http-layer-design.md
/// §6 — not independently locked by any prior document.
/// </summary>
public record ExecuteApprovalResponse(string Result, string? RefusalReason, string? Detail, Guid? ExecutionIntentId);

/// <summary>
/// Step 47 — the staff-facing HTTP surface for the AI Action Safety Model's
/// Approval lifecycle. See docs/handoffs/step-47-execute-steps-14-16-and-staff-http-layer-design.md
/// for the full design this controller implements, including its addendum's
/// four owner-decided items (list visibility, no notification, DeniedReason,
/// no live staleness check).
///
/// All four actions: OwnerOrAdmin (step-22z §6, unchanged — Agent is
/// excluded from viewing this list, not only from acting on it, per the
/// addendum's decision 1) + the same two-layer tenant pattern every other
/// controller in this codebase uses (TicketsController's own doc comment):
/// the global query filter (Layer 1, since Approval : ITenantOwned) plus an
/// explicit SameTenant check on the loaded resource (Layer 2) for every
/// single-resource action. List needs only Layer 1 — there is no specific
/// resource to additionally check.
///
/// Approve/Deny follow TicketsController.Claim/Unclaim's own established
/// idiom exactly: an atomic ExecuteUpdateAsync conditional update (the WHERE
/// clause is the entire race guard) wrapped in an explicit transaction
/// together with the AuditLog write, so the two can never commit
/// independently. A 0-row result is ordinary concurrency handling (someone
/// else already acted, or the window lapsed) — Conflict (409), never treated
/// as a security event, no AuditLog row written on that path, matching
/// step-43's own established precedent for this exact class of guard.
///
/// Execute is a thin adapter: load + authorize, then delegate entirely to
/// the already-implemented, already-independently-verified
/// IApprovedActionExecutor.ExecuteApprovedActionAsync (steps 3-16, complete
/// as of step-47) — this controller performs no business logic of its own
/// for that action beyond resolving executingStaffUserId and serializing the
/// result.
/// </summary>
[ApiController]
[Route("api/approvals")]
[Authorize(Policy = NivoraPolicies.OwnerOrAdmin)]
public class ApprovalsController(
    AppDbContext db,
    IAuthorizationService authorizationService,
    IAuditLogService auditLogService,
    IApprovedActionExecutor executor) : ControllerBase
{
    private Guid CurrentUserId
    {
        get
        {
            var raw = User.FindFirstValue(ClaimTypes.NameIdentifier);
            return Guid.TryParse(raw, out var id)
                ? id
                : throw new InvalidOperationException("Authenticated but no parsable user id claim — unreachable behind [Authorize].");
        }
    }

    /// <summary>
    /// The staff "inbox" — live Pending Approvals only (Status='Pending' AND
    /// ExpiresAt>now). Step 47 addendum decision 4: no live Shopify
    /// staleness check is performed here — deliberately, not by omission;
    /// staleness is caught exclusively at execution time (step 14).
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<ApprovalSummaryResponse>>> List(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var approvals = await db.Approvals
            .Where(a => a.Status == ApprovalStatus.Pending && a.ExpiresAt > now)
            .OrderBy(a => a.CreatedAt)
            .Select(a => new ApprovalSummaryResponse(a.Id, a.ToolName, a.ResourceId, a.DisplaySummary, a.CreatedAt, a.ExpiresAt))
            .ToListAsync(cancellationToken);

        return Ok(approvals);
    }

    /// <summary>Pending, unexpired -> Approved. No expiry override — an
    /// expired Approval refuses here exactly the same as one that never
    /// existed in this state, per step-22z §5.</summary>
    [HttpPost("{id:guid}/approve")]
    public async Task<IActionResult> Approve(Guid id, CancellationToken cancellationToken)
    {
        var approval = await db.Approvals.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (approval is null) return NotFound();

        var authResult = await authorizationService.AuthorizeAsync(User, approval, NivoraPolicies.SameTenant);
        if (!authResult.Succeeded) return NotFound();

        var now = DateTimeOffset.UtcNow;
        var currentUserId = CurrentUserId;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var affected = await db.Approvals
                .Where(a => a.Id == id && a.Status == ApprovalStatus.Pending && a.ExpiresAt > now)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(a => a.Status, ApprovalStatus.Approved)
                    .SetProperty(a => a.ApprovedAt, now)
                    .SetProperty(a => a.ApprovedBy, currentUserId), cancellationToken);

            if (affected == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Conflict("Approval is not currently pending, or has expired.");
            }

            await auditLogService.LogAsync(
                approval.BusinessId, AuditEntityTypes.Approval, approval.Id.ToString(),
                AuditEventTypes.ApprovalApproved, ActorType.NivoraBusinessStaff,
                changedByUserId: currentUserId, cancellationToken: cancellationToken);

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }

        return NoContent();
    }

    /// <summary>Pending or Approved -> Denied. No expiry check in either
    /// source state (step-22z §5, unweakened) — denying an action nobody
    /// should execute is always safe. Reason is optional (step-47 addendum
    /// decision 3).</summary>
    [HttpPost("{id:guid}/deny")]
    public async Task<IActionResult> Deny(Guid id, [FromBody] DenyApprovalRequest? request, CancellationToken cancellationToken)
    {
        var approval = await db.Approvals.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (approval is null) return NotFound();

        var authResult = await authorizationService.AuthorizeAsync(User, approval, NivoraPolicies.SameTenant);
        if (!authResult.Succeeded) return NotFound();

        var now = DateTimeOffset.UtcNow;
        var currentUserId = CurrentUserId;
        var reason = request?.Reason;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var affected = await db.Approvals
                .Where(a => a.Id == id && (a.Status == ApprovalStatus.Pending || a.Status == ApprovalStatus.Approved))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(a => a.Status, ApprovalStatus.Denied)
                    .SetProperty(a => a.DeniedAt, now)
                    .SetProperty(a => a.DeniedBy, currentUserId)
                    .SetProperty(a => a.DeniedReason, reason), cancellationToken);

            if (affected == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Conflict("Approval has already been denied or consumed.");
            }

            await auditLogService.LogAsync(
                approval.BusinessId, AuditEntityTypes.Approval, approval.Id.ToString(),
                AuditEventTypes.ApprovalDenied, ActorType.NivoraBusinessStaff,
                changedByUserId: currentUserId, cancellationToken: cancellationToken);

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }

        return NoContent();
    }

    /// <summary>
    /// Thin adapter over IApprovedActionExecutor — see class doc comment.
    /// Delegates every authorization/state check past the load+SameTenant
    /// gate above to the executor itself (steps 6-16); this action does not
    /// duplicate any of them.
    /// </summary>
    [HttpPost("{id:guid}/execute")]
    public async Task<ActionResult<ExecuteApprovalResponse>> Execute(Guid id, CancellationToken cancellationToken)
    {
        var approval = await db.Approvals.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (approval is null) return NotFound();

        var authResult = await authorizationService.AuthorizeAsync(User, approval, NivoraPolicies.SameTenant);
        if (!authResult.Succeeded) return NotFound();

        var outcome = await executor.ExecuteApprovedActionAsync(id, CurrentUserId, cancellationToken);

        var response = outcome switch
        {
            ExecutionOutcome.Refused refused =>
                new ExecuteApprovalResponse("Refused", refused.Reason.ToString(), refused.Detail, null),
            ExecutionOutcome.Executed executed =>
                new ExecuteApprovalResponse(executed.Status.ToString(), null, executed.Detail, executed.ExecutionIntentId),
            _ => throw new InvalidOperationException("Unreachable — ExecutionOutcome is a closed, two-case hierarchy."),
        };

        return Ok(response);
    }
}
