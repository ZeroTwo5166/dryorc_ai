namespace backend.Services;

/// <summary>
/// Distinct outcomes for one RunAsync call — never conflated, same
/// discipline as IdempotentOperationOutcome. Completed is the only status
/// carrying a model-facing reply; ClaudeCallFailed and IterationLimitReached
/// must never be treated as a successful completion by any caller.
/// </summary>
public enum ToolDispatchStatus
{
    /// <summary>The model produced a final, non-tool-use response.</summary>
    Completed,

    /// <summary>The underlying Claude call failed (rate limit, 5xx, API
    /// error, network failure, timeout) — same failure classes
    /// ClaudeReplyService already treats as "no reply this turn."</summary>
    ClaudeCallFailed,

    /// <summary>The configured iteration bound was reached without the
    /// model producing a terminal, non-tool-use response. Never reported as
    /// Completed.</summary>
    IterationLimitReached,
}

public record ToolDispatchOutcome(ToolDispatchStatus Status, string? ResponseText)
{
    public static ToolDispatchOutcome Completed(string responseText) => new(ToolDispatchStatus.Completed, responseText);
    public static ToolDispatchOutcome ClaudeCallFailed() => new(ToolDispatchStatus.ClaudeCallFailed, null);
    public static ToolDispatchOutcome IterationLimitReached() => new(ToolDispatchStatus.IterationLimitReached, null);
}

/// <summary>
/// Step 19 — hand-rolled tool-dispatch loop on the stable/non-Beta Anthropic
/// SDK surface (Decision 1; BetaToolRunner is not used — confirmed by direct
/// inspection of the installed Anthropic 12.42.0 package that no non-Beta
/// ToolRunner/RunnableTool helper exists at all, so this is not merely
/// preferred but necessary). See ToolDispatchService for the implementation
/// and docs/handoffs/step-19-tool-dispatch-design-approved.md for the
/// approved architecture.
///
/// This is infrastructure only — no concrete write tool is registered
/// anywhere in this codebase yet (see WriteToolBase's own doc comment).
/// With zero IDispatchTool implementations registered, a conversation
/// dispatched through this service behaves like an ordinary tool-free
/// Claude call, mirroring the existing ClaudeReplyService path, which this
/// service does not replace, modify, or get wired into.
/// </summary>
public interface IToolDispatchService
{
    /// <summary>
    /// Runs the tool-dispatch loop to completion (or its iteration bound).
    /// <paramref name="messages"/> is mutated in place — every assistant
    /// turn and tool_result turn generated during dispatch is appended to
    /// it, so callers can inspect/persist the full transcript afterward.
    /// </summary>
    Task<ToolDispatchOutcome> RunAsync(
        ToolDispatchContext context,
        string systemPrompt,
        List<Anthropic.Models.Messages.MessageParam> messages,
        CancellationToken cancellationToken);
}
