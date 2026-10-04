using Anthropic.Models.Messages;

namespace backend.Services;

/// <summary>
/// Server-derived, trusted context for one tool-dispatch turn. Every field
/// here comes from the authenticated conversation/request on the server
/// side — never from the model's tool-call arguments. Tool implementations
/// must read BusinessId/CustomerId/ConversationId from here, never attempt
/// to read an equivalent value out of the model-supplied `input` dictionary,
/// even if the model happens to include one — see the Step 19 tool-dispatch
/// design approval's Decision 1/18 rationale for why trusted context must
/// stay server-side and explicit.
/// ReplyCorrelation is for the dispatch loop's provider calls only; tools
/// never read or advance it.
/// </summary>
public record ToolDispatchContext(Guid BusinessId, Guid CustomerId, Guid? ConversationId, CustomerReplyCorrelation ReplyCorrelation);

/// <summary>
/// The one CustomerReply operation of a customer turn (v11 §4:154): created by
/// ClaudeReplyService.GenerateReplyAsync; the A1 call or each A2 loop
/// iteration takes the next Sequence value.
/// Not thread-safe by design: the reply operation's calls are sequential.
/// </summary>
public sealed class CustomerReplyCorrelation
{
    public Guid CorrelationId { get; } = Guid.NewGuid();
    private int _nextSequence;
    public int NextSequence() => _nextSequence++;
}

/// <summary>
/// The result of executing one tool call. IsError maps directly onto the
/// Anthropic `tool_result` block's own `is_error` field — a tool failure
/// (validation, authorization denial, downstream error) must always be
/// represented this way, never as an IsError=false result containing an
/// apologetic message, so the model can distinguish "the action did not
/// happen" from "the action happened and this is its outcome."
/// </summary>
public record ToolExecutionResult(string Content, bool IsError)
{
    public static ToolExecutionResult Success(string content) => new(content, IsError: false);
    public static ToolExecutionResult Error(string content) => new(content, IsError: true);
}

/// <summary>
/// Common shape shared by every dispatchable tool, read-only or
/// write/mutating. Name/Description/InputSchema feed directly into the
/// Anthropic `Tool` definition sent on every dispatch turn.
/// </summary>
public interface IDispatchTool
{
    string Name { get; }
    string Description { get; }
    InputSchema InputSchema { get; }
}

/// <summary>
/// A tool with no mutating side effect — it never needs an idempotency key
/// and is executed directly by the dispatch loop.
/// </summary>
public interface IReadOnlyDispatchTool : IDispatchTool
{
    Task<ToolExecutionResult> ExecuteAsync(
        ToolDispatchContext context, IReadOnlyDictionary<string, System.Text.Json.JsonElement> input,
        CancellationToken cancellationToken);
}

/// <summary>
/// The inputs a write tool contributes toward idempotency-key derivation —
/// see the approved idempotency-key derivation design
/// (docs/handoffs/step-19-idempotency-key-derivation-approved.md). ResourceId
/// and CanonicalArguments are the tool's own known-shape canonicalization of
/// its `input`; the dispatch loop combines them with BusinessId, the tool
/// name, the assistant message id, and the tool_use id — it never derives
/// these two fields itself, since only the tool knows which of its own
/// arguments are operation-defining versus incidental (mirroring
/// OrderCancellationService/CustomerUpdateActionService's own existing
/// intent-comparison fields).
/// </summary>
public record IdempotencyToolInputs(string ResourceId, string CanonicalArguments);

/// <summary>
/// A mutating tool. Never implement this interface directly — derive from
/// WriteToolBase, whose sealed ExecuteAsync is the only path that reaches
/// mutation logic (Decision 18, structural authorization enforcement).
/// idempotencyKey is supplied by the dispatch loop (Decisions 11/12,
/// dispatch owns key derivation) — a write tool never generates or
/// substitutes its own key.
/// </summary>
public interface IWriteDispatchTool : IDispatchTool
{
    IdempotencyToolInputs DescribeIdempotencyInputs(IReadOnlyDictionary<string, System.Text.Json.JsonElement> input);

    Task<ToolExecutionResult> ExecuteAsync(
        ToolDispatchContext context, IReadOnlyDictionary<string, System.Text.Json.JsonElement> input,
        string idempotencyKey, CancellationToken cancellationToken);
}
