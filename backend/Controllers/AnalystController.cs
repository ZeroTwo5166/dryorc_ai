using backend.Auth;
using backend.Data;
using backend.Interfaces;
using backend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace backend.Controllers;

/// <summary>
/// Single-turn only, per owner decision 3 — no conversation id, no history,
/// no persisted session. Each request is independent.
/// </summary>
public record AnalystQuestionRequest(string Question);

/// <summary>
/// Outcome is one of "Answered"/"Refused"/"CapabilityFailed"/"Disabled"/
/// "ClaudeCallFailed" (AnalystOutcomeType.ToString(), plus the controller-
/// authored "Disabled" literal — matching StatusCountEntry.Status's own
/// convention of exposing an enum-shaped value as a plain string, not a raw
/// serialized C# enum). Message is Answered's synthesized answer for that
/// outcome, or a fixed backend-authored explanation for every other outcome
/// — never null, so the frontend always has something to display. Per
/// design §5, capability names and raw tool data are never included.
/// </summary>
public record AnalystAnswerResponse(string Outcome, string Message);

/// <summary>
/// Step 23 Phase 5 (design §4/§5/§6) — the endpoint that wires Phases 1-4
/// together. Kill-switch check (design §4) happens here, at the very top,
/// before IAnalystOrchestrationService is ever called — before any Claude
/// call, before any capability execution. `AnyStaff` (design §6, the same
/// policy AnalyticsController itself already uses for the identical
/// underlying data) — narrower authorization was considered and rejected:
/// the design doc explicitly names `AnyStaff`, not a stricter tier, despite
/// this endpoint triggering real Claude API spend per call; the write-side
/// kill-switch toggle (BusinessController.SetAiBusinessAnalystEnabled)
/// remains OwnerOnly, matching this codebase's universal pattern of gating
/// a capability's *exposure* more strictly than its *use*
/// (SetCancelOrderToolEnabled/SetAiBusinessAnalystEnabled's own doc
/// comments). No AuditLog entry (design §7 — a read-only, staff-initiated,
/// AnyStaff-authorized request, the same convention every other AnyStaff GET
/// in this codebase already follows unaudited).
///
/// HTTP status mapping (owner decision, this phase — the design itself only
/// locks Refused=200/Disabled=403; CapabilityFailed and ClaudeCallFailed
/// were both undetermined before this pass, found during Phase 5 discovery
/// and resolved before implementation): Answered/Refused=200 (design §5,
/// "a refusal is a valid, non-error outcome of a well-formed request");
/// CapabilityFailed=500 (an internal capability/query genuinely broke —
/// design §3c's own "legitimately broke," not a well-formed decline);
/// ClaudeCallFailed=502 (Nivora depends on an upstream API — Anthropic —
/// that failed to respond usably); Disabled=403 (design §4's recommendation,
/// defense-in-depth only per Draft 3 — the client is the primary UX gate,
/// hiding the entry point entirely via MeResponse.AiBusinessAnalystEnabled,
/// already exposed since Phase 1).
/// </summary>
[ApiController]
[Route("api/analyst")]
[Authorize(Policy = NivoraPolicies.AnyStaff)]
public class AnalystController(
    AppDbContext db,
    ICurrentTenantAccessor tenantAccessor,
    IAnalystOrchestrationService orchestrationService) : ControllerBase
{
    private const string DisabledMessage = "The AI Business Analyst is not enabled for this business.";

    [HttpPost]
    public async Task<ActionResult<AnalystAnswerResponse>> Ask(
        [FromBody] AnalystQuestionRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Question))
        {
            return BadRequest("question must not be empty.");
        }

        var businessId = tenantAccessor.BusinessId
            ?? throw new InvalidOperationException("Authenticated but no tenant resolved — unreachable behind [Authorize].");

        var business = await db.Businesses.FirstOrDefaultAsync(b => b.Id == businessId, cancellationToken);
        if (business is null)
        {
            return Unauthorized();
        }

        // Design §4: a single, request-time check at the very top of this
        // action — before any Claude call, before any tool execution. No
        // second, execution-time re-check is needed (design §4: no propose-
        // now/execute-later time gap exists in this synchronous, single-
        // request flow for one to protect against).
        if (!business.AiBusinessAnalystEnabled)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new AnalystAnswerResponse("Disabled", DisabledMessage));
        }

        var outcome = await orchestrationService.AnswerAsync(businessId, request.Question, cancellationToken);

        return outcome.Type switch
        {
            AnalystOutcomeType.Answered => Ok(new AnalystAnswerResponse(outcome.Type.ToString(), outcome.ResponseText)),
            AnalystOutcomeType.Refused => Ok(new AnalystAnswerResponse(outcome.Type.ToString(), outcome.ResponseText)),
            AnalystOutcomeType.CapabilityFailed => StatusCode(
                StatusCodes.Status500InternalServerError, new AnalystAnswerResponse(outcome.Type.ToString(), outcome.ResponseText)),
            AnalystOutcomeType.ClaudeCallFailed => StatusCode(
                StatusCodes.Status502BadGateway, new AnalystAnswerResponse(outcome.Type.ToString(), outcome.ResponseText)),
            _ => throw new NotSupportedException($"Unhandled AnalystOutcomeType '{outcome.Type}'."),
        };
    }
}
