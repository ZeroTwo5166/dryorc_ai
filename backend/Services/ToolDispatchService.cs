using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using Anthropic.Services;
using backend.Constants;
using Microsoft.Extensions.Logging;

namespace backend.Services;

/// <summary>
/// See IToolDispatchService for the contract and design references.
///
/// Tool registry mirrors the existing IEnumerable&lt;IIntegrationOAuthProvider&gt;
/// DI convention (Program.cs, IntegrationsController) — the built-in
/// container correctly resolves an empty sequence when zero IDispatchTool
/// implementations are registered, exactly as it already does for
/// integration providers today.
///
/// MaxToolIterations is a proposed default, NOT a previously approved Step
/// 19 decision — no existing repository convention for a tool-dispatch
/// iteration bound exists (ClaudeReplyService makes exactly one non-looping
/// call). Flagged explicitly as a new owner decision in this pass's report;
/// do not treat this number as settled.
/// </summary>
public class ToolDispatchService(
    IEnumerable<IDispatchTool> tools,
    IAnthropicInvoker anthropicInvoker,
    ILogger<ToolDispatchService> logger) : IToolDispatchService
{
    private const int MaxToolIterations = 10;
    private const int MaxReplyTokens = 1024;

    private readonly IReadOnlyDictionary<string, IDispatchTool> _toolsByName =
        tools.ToDictionary(t => t.Name);

    public async Task<ToolDispatchOutcome> RunAsync(
        ToolDispatchContext context,
        string systemPrompt,
        List<MessageParam> messages,
        IReadOnlySet<string> disabledToolNames,
        CancellationToken cancellationToken)
    {
        for (var iteration = 0; iteration < MaxToolIterations; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Message response;
            try
            {
                response = await anthropicInvoker.CreateMessageAsync(new AiCallContext(context.BusinessId, AiOperations.CustomerReply, context.ReplyCorrelation.CorrelationId, context.ReplyCorrelation.NextSequence(), context.ConversationId), new MessageCreateParams
                {
                    Model = AnthropicModels.Default,
                    MaxTokens = MaxReplyTokens,
                    System = systemPrompt,
                    Messages = messages,
                    // Step 48: a disabled tool is never included here — the
                    // model is never told it exists, not "offered but
                    // rejected later."
                    Tools = _toolsByName.Values
                        .Where(t => !disabledToolNames.Contains(t.Name))
                        .Select(t => (ToolUnion)new Tool
                        {
                            Name = t.Name,
                            Description = t.Description,
                            InputSchema = t.InputSchema,
                        })
                        .ToList(),
                    // This loop never reads or persists reasoning content
                    // (see ToAssistantParam below) and never requested it —
                    // claude-sonnet-5's adaptive thinking default was
                    // producing ThinkingBlocks on tool-selection turns
                    // regardless, which ToAssistantParam had no case for (a
                    // real crash on this exact call shape, found via Step
                    // 23's live validation). Disabling it here makes that
                    // block type unreachable by construction on every
                    // iteration of this loop, since this same object
                    // initializer is reused each pass.
                    Thinking = new ThinkingConfigDisabled(),
                }, cancellationToken);
            }
            // Same typed hierarchy and reasoning as ClaudeReplyService's own
            // catch chain — a genuine external/transient failure degrades to
            // ClaudeCallFailed, never thrown, never treated as a completed
            // reply. AnthropicNotConfiguredException (missing API key) is
            // deliberately NOT caught here, same reasoning as
            // ClaudeReplyService: a programming/deployment error must
            // propagate, not be indistinguishable from a normal external
            // failure.
            catch (AnthropicRateLimitException)
            {
                return ToolDispatchOutcome.ClaudeCallFailed();
            }
            catch (Anthropic5xxException)
            {
                return ToolDispatchOutcome.ClaudeCallFailed();
            }
            catch (AnthropicApiException)
            {
                return ToolDispatchOutcome.ClaudeCallFailed();
            }
            catch (HttpRequestException)
            {
                return ToolDispatchOutcome.ClaudeCallFailed();
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // A request timeout distinct from caller-requested
                // cancellation — same distinction ClaudeReplyService does
                // not need to make (it has no loop to keep running), but
                // matters here: caller cancellation must propagate
                // (ThrowIfCancellationRequested/OperationCanceledException),
                // never be silently swallowed into ClaudeCallFailed.
                return ToolDispatchOutcome.ClaudeCallFailed();
            }

            var toolUses = response.Content.Select(b => b.Value).OfType<ToolUseBlock>().ToList();
            if (toolUses.Count == 0)
            {
                var text = string.Concat(response.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text));
                return ToolDispatchOutcome.Completed(text);
            }

            // Echo the assistant's own turn back into history verbatim
            // before appending tool results — required by the Messages API
            // shape (a tool_result must follow the tool_use it answers in
            // the next user turn, and the assistant turn that produced it
            // must be present in history).
            //
            // ToAssistantParam throws NotSupportedException for any
            // content-block type outside its currently-recognized set —
            // Thinking is disabled on the call above specifically to keep
            // that unreachable in the common case, but the SDK's
            // ContentBlock union is extensible beyond what this method
            // maps today. Treated the same as a genuine Claude-call
            // problem, not a new failure mode: caught here and degraded to
            // ClaudeCallFailed, exactly like every typed exception from the
            // call above.
            List<ContentBlockParam> assistantContent;
            try
            {
                assistantContent = response.Content.Select(ToAssistantParam).ToList();
            }
            catch (NotSupportedException)
            {
                return ToolDispatchOutcome.ClaudeCallFailed();
            }

            messages.Add(new MessageParam
            {
                Role = Role.Assistant,
                Content = assistantContent,
            });

            var resultBlocks = new List<ContentBlockParam>();
            foreach (var toolUse in toolUses)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var result = await ExecuteToolAsync(context, response.ID, toolUse, disabledToolNames, cancellationToken);
                resultBlocks.Add(new ContentBlockParam(
                    new ToolResultBlockParam(toolUse.ID)
                    {
                        Content = result.Content,
                        IsError = result.IsError,
                    },
                    null));
            }

            messages.Add(new MessageParam { Role = Role.User, Content = resultBlocks });
        }

        logger.LogWarning(
            "Tool dispatch loop reached its iteration bound ({MaxToolIterations}) for conversation {ConversationId} without a terminal response.",
            MaxToolIterations, context.ConversationId);
        return ToolDispatchOutcome.IterationLimitReached();
    }

    private async Task<ToolExecutionResult> ExecuteToolAsync(
        ToolDispatchContext context, string assistantMessageId, ToolUseBlock toolUse,
        IReadOnlySet<string> disabledToolNames, CancellationToken cancellationToken)
    {
        // Step 48: a disabled tool is treated identically to an
        // unregistered one — defense in depth alongside the Tools-array
        // filtering above, since offering and dispatching are two separate
        // code paths that could in principle diverge. Should be
        // unreachable in practice (Claude cannot name a tool it was never
        // offered), but this must fail closed, not silently execute, if it
        // ever is.
        if (!_toolsByName.TryGetValue(toolUse.Name, out var tool) || disabledToolNames.Contains(toolUse.Name))
        {
            logger.LogWarning("Unknown tool {ToolName} requested by the model; returning an error tool_result.", toolUse.Name);
            return ToolExecutionResult.Error("Unknown tool.");
        }

        try
        {
            switch (tool)
            {
                case IWriteDispatchTool writeTool:
                {
                    var idempotencyInputs = writeTool.DescribeIdempotencyInputs(toolUse.Input);
                    var idempotencyKey = IdempotencyKeyDeriver.Derive(
                        context.BusinessId, tool.Name, idempotencyInputs.ResourceId,
                        idempotencyInputs.CanonicalArguments, assistantMessageId, toolUse.ID);
                    return await writeTool.ExecuteAsync(context, toolUse.Input, idempotencyKey, cancellationToken);
                }
                case IReadOnlyDispatchTool readOnlyTool:
                    return await readOnlyTool.ExecuteAsync(context, toolUse.Input, cancellationToken);
                case IActionProposalTool proposalTool:
                {
                    // Step 23J: no idempotency-key derivation belongs here,
                    // unlike the IWriteDispatchTool branch above —
                    // ProposeAsync takes no externally-supplied key at all,
                    // since action-hash/dedup derivation happens entirely
                    // inside it (ActionHasher.Derive, called internally).
                    // ApprovalId is deliberately discarded here — nothing in
                    // ToolDispatchOutcome/ToolExecutionResult has a slot for
                    // it, and audit logging for Approval creation is
                    // ProposeAsync's own internal responsibility.
                    var result = await proposalTool.ProposeAsync(context, toolUse.Input, cancellationToken);
                    return result.DispatchResult;
                }
                default:
                    // Structural safeguard, not expected to be reachable —
                    // every IDispatchTool implementation must be one of the
                    // two above. Treated as an unknown tool, not a crash.
                    logger.LogError(
                        "Tool {ToolName} implements IDispatchTool but neither IReadOnlyDispatchTool nor IWriteDispatchTool.",
                        tool.Name);
                    return ToolExecutionResult.Error("This tool is not correctly registered.");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Malformed/unexpected model-supplied arguments surface here
            // too (e.g. a tool's own DescribeIdempotencyInputs/ExecuteAsync
            // throwing on an unexpected JSON shape) — never leaked to the
            // model as a raw exception message/stack trace, never allowed
            // to crash the dispatch loop. Full exception detail goes to the
            // operational log only.
            logger.LogError(ex, "Tool {ToolName} threw during execution.", tool.Name);
            return ToolExecutionResult.Error("An internal error occurred while executing this tool.");
        }
    }

    // Deliberately still throws for anything outside this recognized set,
    // rather than silently omitting the block — omission is a replay-
    // fidelity decision this class has no basis to make on its own. Its one
    // caller (in the loop above) catches this and degrades the whole
    // iteration to ClaudeCallFailed instead — this method's contract is
    // "map what I recognize, refuse to guess about what I don't," not "map
    // what I recognize, drop the rest." Same policy as
    // AnalystOrchestrationService.ToAssistantParam, kept identical
    // deliberately even though the surrounding loop/call-count shape
    // differs between the two services.
    private static ContentBlockParam ToAssistantParam(ContentBlock block) => block.Value switch
    {
        TextBlock t => new ContentBlockParam(new TextBlockParam(t.Text), null),
        ToolUseBlock tu => new ContentBlockParam(
            new ToolUseBlockParam { ID = tu.ID, Name = tu.Name, Input = tu.Input }, null),
        _ => throw new NotSupportedException(
            $"Unsupported assistant content block type '{block.Value?.GetType().Name}' encountered while replaying dispatch history."),
    };
}
