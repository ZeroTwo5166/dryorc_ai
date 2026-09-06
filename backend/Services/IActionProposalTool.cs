using System.Text.Json;

namespace backend.Services;

/// <summary>
/// AI-facing proposal capability — see docs/handoffs/step-22z-approval-proposal-approval-lifecycle-design.md
/// §7 and docs/handoffs/step-23h-proposal-executor-interface-design.md for the
/// full design derivation. Structurally cannot reach
/// WriteToolBase.ExecuteMutationAsync-shaped logic: this interface extends
/// IDispatchTool directly, NOT IWriteDispatchTool, and exposes no
/// ExecuteAsync-shaped member — mutation capability first becomes reachable
/// only inside the staff-triggered Execute operation (IApprovedActionExecutor,
/// still design-only, not created by this pass), never from anything
/// ToolDispatchService's AI-facing tool collection can resolve (22A §5–§6,
/// 22B §2, restated unweakened 22Z §7). No mutation-shaped member may ever be
/// added to this interface.
///
/// Created by Step 23K solely as a prerequisite for
/// ToolDispatchService.ExecuteToolAsync's new IActionProposalTool switch case
/// (Step 23J) to compile and mean something — this pass adds no concrete
/// implementation of it, and it remains unreachable (no concrete class exists
/// to register in DI) exactly as before. Its shape is an exact, unmodified
/// transcription of Step 23H's already-reviewed design; no new invention.
/// </summary>
public interface IActionProposalTool : IDispatchTool
{
    /// <summary>
    /// Mirrors IWriteDispatchTool.DescribeIdempotencyInputs' existing shape
    /// (22Z §7) — the tool's own known-shape parsing of untrusted AI input
    /// into (ResourceId, CanonicalArguments), reused for action-hash
    /// derivation rather than dispatch-loop idempotency-key derivation.
    /// </summary>
    IdempotencyToolInputs DescribeProposalInputs(IReadOnlyDictionary<string, JsonElement> input);

    /// <summary>
    /// Validates AI input, resolves and requires non-null CustomerId, derives
    /// ActionHash, deduplicates against active proposals, creates exactly one
    /// Pending Approval row. Never mutates anything external (Shopify, or any
    /// write-tool table) — proposal creation is exclusively a durable-record
    /// operation (22Z §7).
    /// </summary>
    Task<ActionProposalResult> ProposeAsync(
        ToolDispatchContext context, IReadOnlyDictionary<string, JsonElement> input, CancellationToken cancellationToken);
}

/// <summary>
/// DispatchResult is always populated (the AI-facing tool_result content,
/// IsError on rejection). ApprovalId is populated only on success — the one
/// identifier the AI needs to reference this proposal going forward (22B §2:
/// the AI receives only an informational ToolExecutionResult plus, on
/// success, the ApprovalId — never ActionHash, raw CanonicalArguments, or any
/// other Approval's identity).
/// </summary>
public record ActionProposalResult(ToolExecutionResult DispatchResult, Guid? ApprovalId);
