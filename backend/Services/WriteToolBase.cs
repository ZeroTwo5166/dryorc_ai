using System.Text.Json;

namespace backend.Services;

/// <summary>
/// Outcome of a write tool's own authorization step (Decision 5 — per-tool
/// resource-specific authorization, e.g. via IOrderOwnershipAuthorizationService).
/// Concrete write tools construct this; WriteToolBase only ever branches on
/// IsAuthorized.
/// </summary>
public record ToolAuthorizationResult(bool IsAuthorized, string? DenialMessage = null)
{
    public static ToolAuthorizationResult Authorized() => new(true);
    public static ToolAuthorizationResult Denied(string message) => new(false, message);
}

/// <summary>
/// Step 19, Decision 18 — sealed/template-method structural authorization
/// enforcement for write-tools. This class implements
/// IWriteDispatchTool.ExecuteAsync as a plain (non-virtual) method: because
/// it is never declared `virtual`, no subclass can override it, and even a
/// subclass that declares its own `new` method of the same signature does
/// not change what runs when the dispatch loop calls a tool through its
/// IWriteDispatchTool-typed reference (interface dispatch resolves to the
/// interface's original implementing member, not a derived class's `new`
/// hide) — see WriteToolBaseTests for an explicit proof of this property.
///
/// ExecuteMutationAsync is `protected abstract`: it is only ever reachable
/// from inside this class's own ExecuteAsync, after authorization has
/// already succeeded. There is no public or externally-callable path to
/// mutation logic that skips authorization.
///
/// This mechanism prevents omission of the authorization step. It does not
/// and cannot prove that a concrete subclass's own AuthorizeAsync/resource-
/// resolution logic is itself correct — see the Step 19 tool-dispatch design
/// approval's own explicit limitations on Decision 18.
///
/// No concrete subclass of this type exists yet — cancelOrder, refundOrder,
/// updateCustomer, and every other write tool remain unauthorized for
/// implementation (see docs/handoffs/step-19-tool-dispatch-design-approved.md
/// §8, Explicitly Blocked Work). This class is reusable infrastructure only.
/// </summary>
public abstract class WriteToolBase : IWriteDispatchTool
{
    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract Anthropic.Models.Messages.InputSchema InputSchema { get; }

    public abstract IdempotencyToolInputs DescribeIdempotencyInputs(IReadOnlyDictionary<string, JsonElement> input);

    public async Task<ToolExecutionResult> ExecuteAsync(
        ToolDispatchContext context, IReadOnlyDictionary<string, JsonElement> input,
        string idempotencyKey, CancellationToken cancellationToken)
    {
        var authorization = await AuthorizeAsync(context, input, cancellationToken);
        if (!authorization.IsAuthorized)
        {
            return ToolExecutionResult.Error(authorization.DenialMessage ?? "Not authorized to perform this action.");
        }

        return await ExecuteMutationAsync(context, input, idempotencyKey, cancellationToken);
    }

    /// <summary>
    /// Resource-specific authorization (Decision 5) — e.g. verifying the
    /// caller's CustomerId actually owns the order/account the tool's
    /// arguments target. Runs before ExecuteMutationAsync is ever reachable.
    /// </summary>
    protected abstract Task<ToolAuthorizationResult> AuthorizeAsync(
        ToolDispatchContext context, IReadOnlyDictionary<string, JsonElement> input,
        CancellationToken cancellationToken);

    /// <summary>
    /// The actual mutation. Only ever invoked by this class's own
    /// ExecuteAsync, after AuthorizeAsync has returned IsAuthorized == true.
    /// idempotencyKey is the dispatch-loop-derived key — pass it straight
    /// through to the underlying idempotent operation service
    /// (OrderCancellationService/CustomerUpdateActionService-style), never
    /// regenerate or ignore it.
    /// </summary>
    protected abstract Task<ToolExecutionResult> ExecuteMutationAsync(
        ToolDispatchContext context, IReadOnlyDictionary<string, JsonElement> input,
        string idempotencyKey, CancellationToken cancellationToken);
}
