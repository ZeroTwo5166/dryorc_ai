using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using Anthropic.Services;
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
    IMessageService messageService,
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
        CancellationToken cancellationToken)
    {
        for (var iteration = 0; iteration < MaxToolIterations; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Message response;
            try
            {
                response = await messageService.Create(new MessageCreateParams
                {
                    Model = "claude-sonnet-5",
                    MaxTokens = MaxReplyTokens,
                    System = systemPrompt,
                    Messages = messages,
                    Tools = _toolsByName.Values
                        .Select(t => (ToolUnion)new Tool
                        {
                            Name = t.Name,
                            Description = t.Description,
                            InputSchema = t.InputSchema,
                        })
                        .ToList(),
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
            messages.Add(new MessageParam
            {
                Role = Role.Assistant,
                Content = response.Content.Select(ToAssistantParam).ToList(),
            });

            var resultBlocks = new List<ContentBlockParam>();
            foreach (var toolUse in toolUses)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var result = await ExecuteToolAsync(context, response.ID, toolUse, cancellationToken);
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
        ToolDispatchContext context, string assistantMessageId, ToolUseBlock toolUse, CancellationToken cancellationToken)
    {
        if (!_toolsByName.TryGetValue(toolUse.Name, out var tool))
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

    private static ContentBlockParam ToAssistantParam(ContentBlock block) => block.Value switch
    {
        TextBlock t => new ContentBlockParam(new TextBlockParam(t.Text), null),
        ToolUseBlock tu => new ContentBlockParam(
            new ToolUseBlockParam { ID = tu.ID, Name = tu.Name, Input = tu.Input }, null),
        _ => throw new NotSupportedException(
            $"Unsupported assistant content block type '{block.Value?.GetType().Name}' encountered while replaying dispatch history."),
    };
}
