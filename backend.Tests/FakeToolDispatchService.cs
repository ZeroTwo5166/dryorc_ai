using Anthropic.Models.Messages;
using backend.Services;

namespace backend.Tests;

/// <summary>
/// Registered in place of the real ToolDispatchService for tests that need
/// to isolate ClaudeReplyService's own branching/wiring logic from
/// ToolDispatchService's internals (Tools-array construction, the dispatch
/// loop itself) — same recording-fake shape as FakeMessageService/
/// FakeKnowledgeRetrievalService, applied to a different interface, not a
/// new testing approach.
/// </summary>
public class FakeToolDispatchService : IToolDispatchService
{
    public ToolDispatchOutcome OutcomeToReturn { get; set; } = ToolDispatchOutcome.Completed("Fake tool-dispatch reply.");
    public int CallCount { get; private set; }
    public ToolDispatchContext? LastContext { get; private set; }
    public string? LastSystemPrompt { get; private set; }
    public List<MessageParam>? LastMessages { get; private set; }
    public IReadOnlySet<string>? LastDisabledToolNames { get; private set; }

    public Task<ToolDispatchOutcome> RunAsync(
        ToolDispatchContext context, string systemPrompt, List<MessageParam> messages,
        IReadOnlySet<string> disabledToolNames, CancellationToken cancellationToken)
    {
        CallCount++;
        LastContext = context;
        LastSystemPrompt = systemPrompt;
        LastMessages = messages;
        LastDisabledToolNames = disabledToolNames;
        return Task.FromResult(OutcomeToReturn);
    }
}
