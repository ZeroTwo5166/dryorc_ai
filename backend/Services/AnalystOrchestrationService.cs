using System.Diagnostics;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using Anthropic.Services;
using Microsoft.Extensions.Logging;

namespace backend.Services;

/// <summary>
/// See IAnalystOrchestrationService for the contract and design references.
///
/// Message threading (echoing the selection call's own assistant turn back
/// into history before appending tool_results, then sending that full
/// history — with Tools withheld — as the synthesis call) mirrors
/// ToolDispatchService.cs's own established shape exactly, since both sit
/// on top of the same Anthropic Messages API turn-taking rules. Duplicated
/// here rather than shared, matching this codebase's own convention of
/// small, self-contained per-service helpers (e.g. every IDispatchTool
/// implementation builds its own InputSchema inline rather than sharing a
/// builder) — these two services have no other reason to depend on each
/// other.
///
/// Capability outputs reach the synthesis call byte-for-byte as each
/// IAnalystCapability.ExecuteAsync returned them (design's own pass-through
/// requirement, carried forward from the Phase 3 discovery note on
/// GetHandledBySnapshotCapability): this class never parses, reformats, or
/// derives anything from a ToolExecutionResult.Content string — it is
/// placed directly into the tool_result block sent to Claude, unmodified.
/// The only text this class ever originates itself is the 3 fixed,
/// backend-authored non-Answered messages below — Answered's ResponseText
/// is always Claude's own Phase-2 text, passed through exactly as returned,
/// with no post-processing either.
/// </summary>
public class AnalystOrchestrationService(
    IEnumerable<IAnalystCapability> capabilities,
    IMessageService messageService,
    ILogger<AnalystOrchestrationService> logger) : IAnalystOrchestrationService
{
    private const int MaxCapabilityCalls = 3;
    private const int MaxSelectionTokens = 512;
    private const int MaxSynthesisTokens = 1024;

    // Design §3b: "may describe currently supported capabilities." Content
    // is explicitly out of scope for the design pass (deferred to
    // implementation) — this is this pass's implementation choice, not a
    // locked decision.
    private const string RefusedNoCapabilityMessage =
        "I can't answer that with the data currently available to the Analyst. I can answer questions " +
        "about conversation volume, conversation status distribution, AI-vs-human handling, handoffs, and " +
        "ticket status distribution — try rephrasing your question around one of those.";

    private const string RefusedOverCeilingMessage =
        "This question needs more data than the Analyst can retrieve in a single request. Try asking a " +
        "narrower question.";

    private const string CapabilityFailedMessage =
        "This data could not be retrieved right now. Please try again shortly.";

    private const string ClaudeCallFailedMessage =
        "The Analyst is temporarily unavailable. Please try again shortly.";

    private readonly IReadOnlyDictionary<string, IAnalystCapability> _capabilitiesByName =
        capabilities.ToDictionary(c => c.Name);

    public async Task<AnalystOutcome> AnswerAsync(Guid businessId, string question, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var messages = new List<MessageParam> { new() { Role = Role.User, Content = question } };

        Message selectionResponse;
        try
        {
            selectionResponse = await messageService.Create(new MessageCreateParams
            {
                Model = "claude-sonnet-5",
                MaxTokens = MaxSelectionTokens,
                System = SelectionSystemPrompt,
                Messages = messages,
                // ToolChoice deliberately left at its default (ToolChoiceAuto)
                // — design §2b: Claude is free to request zero, one, or
                // several capabilities in this one response, or answer with
                // text alone (that text is never surfaced either way — see
                // the zero-ToolUseBlock branch below).
                Tools = _capabilitiesByName.Values
                    .Select(c => (ToolUnion)new Tool { Name = c.Name, Description = c.Description, InputSchema = c.InputSchema })
                    .ToList(),
            }, cancellationToken);
        }
        // Same typed hierarchy and reasoning as ToolDispatchService/
        // ConversationClassificationService's own catch chains. See
        // IAnalystOrchestrationService's own doc comment for why this is a
        // distinct outcome, not folded into Refused.
        catch (AnthropicRateLimitException)
        {
            return Finish(AnalystOutcomeType.ClaudeCallFailed, ClaudeCallFailedMessage, stopwatch, [], null, null);
        }
        catch (Anthropic5xxException)
        {
            return Finish(AnalystOutcomeType.ClaudeCallFailed, ClaudeCallFailedMessage, stopwatch, [], null, null);
        }
        catch (AnthropicApiException)
        {
            return Finish(AnalystOutcomeType.ClaudeCallFailed, ClaudeCallFailedMessage, stopwatch, [], null, null);
        }
        catch (HttpRequestException)
        {
            return Finish(AnalystOutcomeType.ClaudeCallFailed, ClaudeCallFailedMessage, stopwatch, [], null, null);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Finish(AnalystOutcomeType.ClaudeCallFailed, ClaudeCallFailedMessage, stopwatch, [], null, null);
        }

        var toolUses = selectionResponse.Content.Select(b => b.Value).OfType<ToolUseBlock>().ToList();

        // Design §3a/§3b, the single most important guarantee in this
        // phase: a zero-capability selection is ALWAYS Refused, using a
        // fixed backend-authored message — Claude's own Phase-1 text
        // (`selectionResponse`'s TextBlocks, if any) is never read, never
        // surfaced, regardless of how plausible or specific it looks. This
        // holds no matter *why* zero capabilities were selected.
        if (toolUses.Count == 0)
        {
            return Finish(AnalystOutcomeType.Refused, RefusedNoCapabilityMessage, stopwatch, [], selectionResponse.Usage, null);
        }

        // Design §2d: backend-authoritative ceiling, never trusting Claude's
        // own compliance with the "at most 3" instruction in the system
        // prompt. Over-limit is rejected outright — zero capabilities
        // executed, no synthesis call — never truncated to the first 3.
        if (toolUses.Count > MaxCapabilityCalls)
        {
            return Finish(AnalystOutcomeType.Refused, RefusedOverCeilingMessage, stopwatch, [], selectionResponse.Usage, null);
        }

        // Echo the assistant's own selection turn back into history verbatim
        // before appending tool results — required by the Messages API
        // shape, same reasoning as ToolDispatchService.cs:110-114.
        messages.Add(new MessageParam
        {
            Role = Role.Assistant,
            Content = selectionResponse.Content.Select(ToAssistantParam).ToList(),
        });

        var capabilityNames = toolUses.Select(t => t.Name).ToList();
        var resultBlocks = new List<ContentBlockParam>();
        var anyFailed = false;
        foreach (var toolUse in toolUses)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await ExecuteCapabilityAsync(businessId, toolUse, cancellationToken);
            anyFailed |= result.IsError;
            resultBlocks.Add(new ContentBlockParam(
                new ToolResultBlockParam(toolUse.ID)
                {
                    Content = result.Content,
                    IsError = result.IsError,
                },
                null));
        }

        // Design §3c: if any of the ≤3 requested capabilities failed, Phase
        // 2 is never called at all — Claude never sees the failure and
        // never gets a chance to smooth it over with a plausible-sounding
        // approximation. A fixed backend-authored message is returned
        // instead.
        if (anyFailed)
        {
            return Finish(AnalystOutcomeType.CapabilityFailed, CapabilityFailedMessage, stopwatch, capabilityNames, selectionResponse.Usage, null);
        }

        messages.Add(new MessageParam { Role = Role.User, Content = resultBlocks });

        Message synthesisResponse;
        try
        {
            synthesisResponse = await messageService.Create(new MessageCreateParams
            {
                Model = "claude-sonnet-5",
                MaxTokens = MaxSynthesisTokens,
                System = SynthesisSystemPrompt,
                Messages = messages,
                // Design §2b: Tools withheld entirely — Claude is
                // structurally unable to request a further capability in
                // this call, it can only produce text. This is "the
                // synthesis call" the amended Decision 5 names; combined
                // with the selection call above, never more than 2 real
                // Anthropic API calls total for any path that reaches here.
                Tools = [],
            }, cancellationToken);
        }
        catch (AnthropicRateLimitException)
        {
            return Finish(AnalystOutcomeType.ClaudeCallFailed, ClaudeCallFailedMessage, stopwatch, capabilityNames, selectionResponse.Usage, null);
        }
        catch (Anthropic5xxException)
        {
            return Finish(AnalystOutcomeType.ClaudeCallFailed, ClaudeCallFailedMessage, stopwatch, capabilityNames, selectionResponse.Usage, null);
        }
        catch (AnthropicApiException)
        {
            return Finish(AnalystOutcomeType.ClaudeCallFailed, ClaudeCallFailedMessage, stopwatch, capabilityNames, selectionResponse.Usage, null);
        }
        catch (HttpRequestException)
        {
            return Finish(AnalystOutcomeType.ClaudeCallFailed, ClaudeCallFailedMessage, stopwatch, capabilityNames, selectionResponse.Usage, null);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Finish(AnalystOutcomeType.ClaudeCallFailed, ClaudeCallFailedMessage, stopwatch, capabilityNames, selectionResponse.Usage, null);
        }

        // Passed through exactly as Claude wrote it — no reformatting, no
        // enrichment, no derived facts added. This is the only path in this
        // class that ever returns Claude-authored text to the caller, and
        // it is reached only after real, successfully-retrieved capability
        // data was attached to the conversation above (design §3a).
        var answerText = string.Concat(synthesisResponse.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text));
        return Finish(AnalystOutcomeType.Answered, answerText, stopwatch, capabilityNames, selectionResponse.Usage, synthesisResponse.Usage);
    }

    private async Task<ToolExecutionResult> ExecuteCapabilityAsync(
        Guid businessId, ToolUseBlock toolUse, CancellationToken cancellationToken)
    {
        // Defense in depth, mirroring ToolDispatchService.cs:156's identical
        // reasoning — should be unreachable in practice (Claude cannot name
        // a capability it was never offered in the selection call's Tools
        // list), but must fail closed, not execute, if it ever is.
        if (!_capabilitiesByName.TryGetValue(toolUse.Name, out var capability))
        {
            logger.LogWarning("Unknown Analyst capability {CapabilityName} requested by the model; returning an error tool_result.", toolUse.Name);
            return ToolExecutionResult.Error("Unknown capability.");
        }

        try
        {
            return await capability.ExecuteAsync(businessId, toolUse.Input, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Never leaked to the model or the staff member as a raw
            // exception message/stack trace — same reasoning as
            // ToolDispatchService.cs:204-213. Full detail goes to the
            // operational log only.
            logger.LogError(ex, "Analyst capability {CapabilityName} threw during execution.", capability.Name);
            return ToolExecutionResult.Error("An internal error occurred while retrieving this data.");
        }
    }

    private static ContentBlockParam ToAssistantParam(ContentBlock block) => block.Value switch
    {
        TextBlock t => new ContentBlockParam(new TextBlockParam(t.Text), null),
        ToolUseBlock tu => new ContentBlockParam(
            new ToolUseBlockParam { ID = tu.ID, Name = tu.Name, Input = tu.Input }, null),
        _ => throw new NotSupportedException(
            $"Unsupported assistant content block type '{block.Value?.GetType().Name}' encountered while replaying Analyst selection history."),
    };

    private AnalystOutcome Finish(
        AnalystOutcomeType type, string responseText, Stopwatch stopwatch,
        IReadOnlyList<string> capabilityNames, Usage? selectionUsage, Usage? synthesisUsage)
    {
        var outcome = new AnalystOutcome(type, responseText);

        // Design §8, restated per the locked Telemetry decision (§0): outcome
        // type, elapsed time, token usage for both calls actually made,
        // count+names of capabilities invoked — never the raw staff
        // question text. `question` (this method's own parameter) never
        // appears in this log line or anywhere else in this class.
        var logLevel = type == AnalystOutcomeType.Answered ? LogLevel.Information : LogLevel.Warning;
        logger.Log(
            logLevel,
            "AI Business Analyst request: {Outcome} in {ElapsedMs}ms. Capabilities invoked ({CapabilityCount}): " +
            "{CapabilityNames}. Selection tokens: in={SelectionInputTokens} out={SelectionOutputTokens}. " +
            "Synthesis tokens: in={SynthesisInputTokens} out={SynthesisOutputTokens}.",
            type, stopwatch.ElapsedMilliseconds, capabilityNames.Count, string.Join(",", capabilityNames),
            selectionUsage?.InputTokens, selectionUsage?.OutputTokens,
            synthesisUsage?.InputTokens, synthesisUsage?.OutputTokens);

        return outcome;
    }

    private static string SelectionSystemPrompt =>
        "You are the Nivora AI Business Analyst, answering an internal staff member's question about this " +
        "business's own support and conversation data. You may call the provided tools, at most 3 in total, " +
        "to gather the data you need before answering. If none of the provided tools can retrieve data " +
        "relevant to the question, do not call any tool and do not attempt to answer from general " +
        "knowledge — an unanswerable question is a normal, expected outcome, not a failure to work around.";

    private static string SynthesisSystemPrompt =>
        "You are the Nivora AI Business Analyst. Answer the staff member's question using only the tool " +
        "results already provided in this conversation. Do not introduce any fact, number, or trend that " +
        "is not directly supported by those results. If a result is a single point-in-time snapshot with no " +
        "historical data, describe it only as the current state — never as a trend, and never as evidence " +
        "of how it has changed over time.";
}
