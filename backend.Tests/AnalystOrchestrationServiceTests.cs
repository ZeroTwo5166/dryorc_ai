using System.Net;
using System.Text.Json;
using Anthropic;
using Anthropic.Core;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using Anthropic.Services;
using Anthropic.Services.Messages;
using backend.Constants;
using backend.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace backend.Tests;

/// <summary>
/// Test-only, non-mutating fake — not a real Analyst capability. Records
/// what it was called with so tests can assert the orchestration engine
/// supplied the right businessId/input. Mirrors RecordingReadOnlyTool
/// (ToolDispatchServiceTests.cs) exactly, adapted to IAnalystCapability's
/// businessId-only signature.
/// </summary>
public class RecordingAnalystCapability : IAnalystCapability
{
    public string Name { get; init; } = "getConversationsByDay";
    public string Description => "Test-only fake Analyst capability.";
    public InputSchema InputSchema => new();
    public string ResultContent { get; set; } = "[]";
    public bool ResultIsError { get; set; }
    public Guid? LastBusinessId { get; private set; }
    public IReadOnlyDictionary<string, JsonElement>? LastInput { get; private set; }
    public int CallCount { get; private set; }

    public Task<ToolExecutionResult> ExecuteAsync(
        Guid businessId, IReadOnlyDictionary<string, JsonElement> input, CancellationToken cancellationToken)
    {
        CallCount++;
        LastBusinessId = businessId;
        LastInput = input;
        return Task.FromResult(new ToolExecutionResult(ResultContent, ResultIsError));
    }
}

/// <summary>
/// Step 23 Phase 4 — design §2/§3, implementation plan §3 "the genuinely
/// adversarial tests." Pure unit tests — no DB, no HTTP, no real Anthropic
/// call. IMessageService is SequencedFakeMessageService throughout (shared
/// infrastructure, already used by ToolDispatchServiceTests and
/// ConversationClassificationServiceTests) and every IAnalystCapability is
/// a fake (RecordingAnalystCapability/ThrowingAnalystCapability, mirroring
/// ToolDispatchServiceTests' own RecordingReadOnlyTool/ThrowingReadOnlyTool)
/// — proving orchestration correctness independently of Phase 3's real
/// capabilities, per the locked implementation plan's explicit instruction
/// that this phase not depend on them.
/// </summary>
public class AnalystOrchestrationServiceTests
{
    private static readonly Guid BusinessId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private static Usage FakeUsage() => new()
    {
        CacheCreation = null,
        CacheCreationInputTokens = null,
        CacheReadInputTokens = null,
        InferenceGeo = null,
        InputTokens = 1,
        OutputTokens = 1,
        OutputTokensDetails = null,
        ServerToolUse = null,
        ServiceTier = null,
    };

    private static Message TextResponse(string text, string id = "msg_text") => new()
    {
        ID = id,
        Container = null,
        Content = [new TextBlock { Text = text, Citations = null }],
        Model = "claude-sonnet-5",
        StopDetails = null,
        StopReason = "end_turn",
        StopSequence = null,
        Usage = FakeUsage(),
    };

    private static Message ToolUseResponse(string toolName, Dictionary<string, JsonElement> input, string toolUseId, string messageId = "msg_tool") => new()
    {
        ID = messageId,
        Container = null,
        Content = [new ToolUseBlock { ID = toolUseId, Name = toolName, Input = input, Caller = null }],
        Model = "claude-sonnet-5",
        StopDetails = null,
        StopReason = "tool_use",
        StopSequence = null,
        Usage = FakeUsage(),
    };

    private static Message MultiToolUseResponse(
        IReadOnlyList<(string Name, Dictionary<string, JsonElement> Input, string ToolUseId)> uses, string messageId = "msg_tool")
    {
        var content = new List<ContentBlock>();
        foreach (var u in uses)
        {
            content.Add(new ToolUseBlock { ID = u.ToolUseId, Name = u.Name, Input = u.Input, Caller = null });
        }

        return new Message
        {
            ID = messageId,
            Container = null,
            Content = content,
            Model = "claude-sonnet-5",
            StopDetails = null,
            StopReason = "tool_use",
            StopSequence = null,
            Usage = FakeUsage(),
        };
    }

    private static Dictionary<string, JsonElement> Args(object anonymous) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(anonymous))!;

    // A1 Slice 4b design §7.5: the service now takes IAnthropicInvoker, so
    // every test wraps its fake IMessageService in a real AnthropicInvoker.
    // A test that doesn't pass a recorder still gets a throwaway capture, so
    // nothing ever persists.
    private static AnalystOrchestrationService NewService(
        IEnumerable<IAnalystCapability> capabilities,
        IMessageService fakeMessages,
        ConcurrentCapturingAiInvocationRecorder? recorder = null,
        ILogger<AnalystOrchestrationService>? logger = null) =>
        new(
            capabilities,
            new AnthropicInvoker(fakeMessages, recorder ?? new ConcurrentCapturingAiInvocationRecorder(), NullLogger<AnthropicInvoker>.Instance),
            logger ?? NullLogger<AnalystOrchestrationService>.Instance);

    // A tool_use selection response that also carries a ThinkingBlock — the
    // exact shape a real claude-sonnet-5 selection turn produced in Step
    // 23's live validation (adaptive thinking, on by default when Thinking
    // is omitted), which crashed ToAssistantParam before this fix.
    private static Message ToolUseWithThinkingResponse(string toolName, Dictionary<string, JsonElement> input, string toolUseId, string messageId = "msg_tool_thinking") => new()
    {
        ID = messageId,
        Container = null,
        Content =
        [
            new ThinkingBlock { Thinking = "Considering which capability best answers this question.", Signature = "sig_fake" },
            new ToolUseBlock { ID = toolUseId, Name = toolName, Input = input, Caller = null },
        ],
        Model = "claude-sonnet-5",
        StopDetails = null,
        StopReason = "tool_use",
        StopSequence = null,
        Usage = FakeUsage(),
    };

    // A tool_use selection response carrying a block type unrelated to
    // thinking entirely (a code-execution container-upload block) —
    // proving the graceful-degradation fix is general, not special-cased
    // to ThinkingBlock. Not reachable under this service's current
    // capability configuration (no server tool is ever offered), but the
    // defensive handling must not assume that stays true.
    private static Message ToolUseWithUnrecognizedBlockResponse(string toolName, Dictionary<string, JsonElement> input, string toolUseId, string messageId = "msg_tool_unrecognized") => new()
    {
        ID = messageId,
        Container = null,
        Content =
        [
            new ContainerUploadBlock("file_abc123"),
            new ToolUseBlock { ID = toolUseId, Name = toolName, Input = input, Caller = null },
        ],
        Model = "claude-sonnet-5",
        StopDetails = null,
        StopReason = "tool_use",
        StopSequence = null,
        Usage = FakeUsage(),
    };

    // ── Step 23 ThinkingBlock crash fix — adversarial coverage ─────────

    [Fact]
    public async Task AnswerAsync_SelectionResponseAlsoContainsThinkingBlock_DegradesToClaudeCallFailed_NeverThrows()
    {
        var capability = new RecordingAnalystCapability { Name = "getConversationsByDay" };
        var fakeMessages = new SequencedFakeMessageService();
        fakeMessages.Enqueue(ToolUseWithThinkingResponse("getConversationsByDay", Args(new { range = "7d" }), "tu_1"));
        var service = NewService([capability], fakeMessages);

        var outcome = await service.AnswerAsync(BusinessId, "How many conversations this week?", CancellationToken.None);

        Assert.Equal(AnalystOutcomeType.ClaudeCallFailed, outcome.Type);
        Assert.Equal(1, fakeMessages.CallCount); // the crash happens before any capability executes or synthesis is called
        Assert.Equal(0, capability.CallCount);
    }

    [Fact]
    public async Task AnswerAsync_SelectionResponseAlsoContainsUnrecognizedBlockType_DegradesToClaudeCallFailed_NeverThrows()
    {
        var capability = new RecordingAnalystCapability { Name = "getConversationsByDay" };
        var fakeMessages = new SequencedFakeMessageService();
        fakeMessages.Enqueue(ToolUseWithUnrecognizedBlockResponse("getConversationsByDay", Args(new { range = "7d" }), "tu_1"));
        var service = NewService([capability], fakeMessages);

        var outcome = await service.AnswerAsync(BusinessId, "How many conversations this week?", CancellationToken.None);

        Assert.Equal(AnalystOutcomeType.ClaudeCallFailed, outcome.Type);
        Assert.Equal(1, fakeMessages.CallCount);
        Assert.Equal(0, capability.CallCount);
    }

    [Fact]
    public async Task AnswerAsync_BothSelectionAndSynthesisCalls_ExplicitlyDisableAdaptiveThinking()
    {
        var capability = new RecordingAnalystCapability { Name = "getConversationsByDay" };
        var fakeMessages = new SequencedFakeMessageService();
        fakeMessages.Enqueue(ToolUseResponse("getConversationsByDay", Args(new { range = "7d" }), "tu_1"));
        fakeMessages.Enqueue(TextResponse("Here is the summary."));
        var service = NewService([capability], fakeMessages);

        await service.AnswerAsync(BusinessId, "How many conversations this week?", CancellationToken.None);

        Assert.Equal(2, fakeMessages.Requests.Count); // selection + synthesis
        Assert.All(fakeMessages.Requests, r =>
        {
            Assert.NotNull(r.Thinking);
            Assert.IsType<ThinkingConfigDisabled>(r.Thinking!.Value);
        });
    }

    // ── The single most important test in this phase ──────────────────
    // Design §3a/§3b: a zero-tool Phase-1 selection must NEVER surface
    // Claude's own text as the answer, no matter how plausible it looks.

    [Fact]
    public async Task AnswerAsync_ZeroToolsSelected_ReturnsRefused_FabricatedTextNeverSurfaces()
    {
        const string fabricatedButPlausibleText =
            "Based on general trends, your AI handled approximately 87% of conversations last month, " +
            "up from 81% the month before.";

        var fakeMessages = new SequencedFakeMessageService();
        fakeMessages.Enqueue(TextResponse(fabricatedButPlausibleText));
        var service = NewService([], fakeMessages);

        var outcome = await service.AnswerAsync(BusinessId, "How has AI handling changed recently?", CancellationToken.None);

        Assert.Equal(AnalystOutcomeType.Refused, outcome.Type);
        // Not merely "the discriminator says Refused" — a direct string-
        // absence assertion against the specific fabricated text, since the
        // discriminator alone would not have caught Draft 1's original bug.
        Assert.DoesNotContain("87%", outcome.ResponseText);
        Assert.DoesNotContain(fabricatedButPlausibleText, outcome.ResponseText);
        Assert.Equal(1, fakeMessages.CallCount);
    }

    // ── Ceiling enforcement (design §2d) ───────────────────────────────

    [Fact]
    public async Task AnswerAsync_MoreThanThreeToolsRequested_ReturnsRefused_NoCapabilityExecuted_NoSynthesisCall()
    {
        var capabilities = new[]
        {
            new RecordingAnalystCapability { Name = "getConversationsByDay" },
            new RecordingAnalystCapability { Name = "getConversationStatusDistribution" },
            new RecordingAnalystCapability { Name = "getHandoffsByDay" },
            new RecordingAnalystCapability { Name = "getTicketStatusDistribution" },
        };
        var fakeMessages = new SequencedFakeMessageService();
        fakeMessages.Enqueue(MultiToolUseResponse([
            ("getConversationsByDay", Args(new { range = "7d" }), "tu_1"),
            ("getConversationStatusDistribution", Args(new { range = "7d" }), "tu_2"),
            ("getHandoffsByDay", Args(new { range = "7d" }), "tu_3"),
            ("getTicketStatusDistribution", Args(new { range = "7d" }), "tu_4"),
        ]));
        var service = NewService(capabilities, fakeMessages);

        var outcome = await service.AnswerAsync(BusinessId, "Give me everything.", CancellationToken.None);

        Assert.Equal(AnalystOutcomeType.Refused, outcome.Type);
        Assert.All(capabilities, c => Assert.Equal(0, c.CallCount));
        Assert.Equal(1, fakeMessages.CallCount);
    }

    [Fact]
    public async Task AnswerAsync_ExactlyThreeToolsRequested_AtCeiling_SucceedsNormally()
    {
        var capabilities = new[]
        {
            new RecordingAnalystCapability { Name = "getConversationsByDay" },
            new RecordingAnalystCapability { Name = "getConversationStatusDistribution" },
            new RecordingAnalystCapability { Name = "getHandoffsByDay" },
        };
        var fakeMessages = new SequencedFakeMessageService();
        fakeMessages.Enqueue(MultiToolUseResponse([
            ("getConversationsByDay", Args(new { range = "7d" }), "tu_1"),
            ("getConversationStatusDistribution", Args(new { range = "7d" }), "tu_2"),
            ("getHandoffsByDay", Args(new { range = "7d" }), "tu_3"),
        ]));
        fakeMessages.Enqueue(TextResponse("Here is the summary."));
        var service = NewService(capabilities, fakeMessages);

        var outcome = await service.AnswerAsync(BusinessId, "Give me everything you reasonably can.", CancellationToken.None);

        Assert.Equal(AnalystOutcomeType.Answered, outcome.Type);
        Assert.Equal("Here is the summary.", outcome.ResponseText);
        Assert.All(capabilities, c => Assert.Equal(1, c.CallCount));
        Assert.Equal(2, fakeMessages.CallCount);
    }

    // ── Capability failure short-circuits before synthesis (design §3c) ─

    [Fact]
    public async Task AnswerAsync_OneCapabilityFails_ReturnsCapabilityFailed_NoSynthesisCall_BackendAuthoredText()
    {
        var failingCapability = new RecordingAnalystCapability
        {
            Name = "getConversationsByDay",
            ResultIsError = true,
            ResultContent = "Simulated downstream failure.",
        };
        var fakeMessages = new SequencedFakeMessageService();
        fakeMessages.Enqueue(ToolUseResponse("getConversationsByDay", Args(new { range = "7d" }), "tu_1"));
        var service = NewService([failingCapability], fakeMessages);

        var outcome = await service.AnswerAsync(BusinessId, "How many conversations this week?", CancellationToken.None);

        Assert.Equal(AnalystOutcomeType.CapabilityFailed, outcome.Type);
        Assert.DoesNotContain("Simulated downstream failure", outcome.ResponseText);
        Assert.Equal(1, fakeMessages.CallCount);
        Assert.Equal(1, failingCapability.CallCount);
    }

    [Fact]
    public async Task AnswerAsync_CapabilityThrows_ReturnsCapabilityFailed_NeverCrashes()
    {
        var fakeMessages = new SequencedFakeMessageService();
        fakeMessages.Enqueue(ToolUseResponse("throwingCapability", Args(new { }), "tu_throw"));
        var service = NewService([new ThrowingAnalystCapability()], fakeMessages);

        var outcome = await service.AnswerAsync(BusinessId, "Anything?", CancellationToken.None);

        Assert.Equal(AnalystOutcomeType.CapabilityFailed, outcome.Type);
        Assert.DoesNotContain("simulated internal failure", outcome.ResponseText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, fakeMessages.CallCount);
    }

    [Fact]
    public async Task AnswerAsync_UnknownCapabilityRequested_ReturnsCapabilityFailed_NeverExecutesAnything()
    {
        var fakeMessages = new SequencedFakeMessageService();
        fakeMessages.Enqueue(ToolUseResponse("thisCapabilityDoesNotExist", Args(new { }), "tu_unknown"));
        var service = NewService([], fakeMessages);

        var outcome = await service.AnswerAsync(BusinessId, "Anything?", CancellationToken.None);

        Assert.Equal(AnalystOutcomeType.CapabilityFailed, outcome.Type);
        Assert.Equal(1, fakeMessages.CallCount);
    }

    // ── Happy path — exactly 2 calls, synthesis structurally cannot request a tool ─

    [Fact]
    public async Task AnswerAsync_HappyPath_ExactlyTwoCalls_SynthesisRequestHasNoTools()
    {
        var capability = new RecordingAnalystCapability { Name = "getConversationsByDay", ResultContent = "[{\"Day\":\"2026-09-01\",\"Count\":3}]" };
        var fakeMessages = new SequencedFakeMessageService();
        fakeMessages.Enqueue(ToolUseResponse("getConversationsByDay", Args(new { range = "7d" }), "tu_1"));
        fakeMessages.Enqueue(TextResponse("You had 3 conversations on September 1st."));
        var service = NewService([capability], fakeMessages);

        var outcome = await service.AnswerAsync(BusinessId, "How many conversations on Sept 1?", CancellationToken.None);

        Assert.Equal(AnalystOutcomeType.Answered, outcome.Type);
        Assert.Equal("You had 3 conversations on September 1st.", outcome.ResponseText);
        Assert.Equal(2, fakeMessages.CallCount);
        Assert.Equal(BusinessId, capability.LastBusinessId);

        var synthesisRequest = fakeMessages.Requests[1];
        Assert.NotNull(synthesisRequest.Tools);
        Assert.Empty(synthesisRequest.Tools!);
    }

    // ── Carry-forward from Phase 3: getHandledBySnapshot's bounded, ──────
    // ── all-history/non-range nature must survive into synthesis ────────
    // ── unaltered, and the orchestration layer must not itself reframe ──
    // ── bounded data as a trend. ─────────────────────────────────────────

    [Fact]
    public async Task AnswerAsync_HandledBySnapshotCapability_PreservesOutputUnaltered_AndIntroducesNoDerivedTrendClaim()
    {
        // Deliberately time-series-SHAPED content for a capability whose
        // real-world identity (getHandledBySnapshot) has no time dimension
        // at all — the point is proving the orchestration layer passes
        // whatever a capability returns through byte-for-byte, regardless
        // of what it looks like, never "fixing," dropping, or reinterpreting
        // it. (This test does not validate GetHandledBySnapshotCapability's
        // actual real-world output shape — that is Phase 3's job. It proves
        // Phase 4's orchestration pass-through guarantee holds even under
        // an adversarially-shaped input.)
        const string plantedTimeSeriesSoundingOutput =
            "[{\"Day\":\"2026-09-01\",\"Count\":5},{\"Day\":\"2026-09-02\",\"Count\":9}]";
        var capability = new RecordingAnalystCapability
        {
            Name = "getHandledBySnapshot",
            ResultContent = plantedTimeSeriesSoundingOutput,
        };

        // Deliberately trend-free — states only the current snapshot, no
        // "increase"/"trend"/"changed" language. If the final response ever
        // diverges from this exact text, the orchestration layer itself
        // introduced something Claude didn't actually say.
        const string trendFreeSynthesisText = "Right now, 5 conversations are AI-handled and 9 are human-handled.";

        var fakeMessages = new SequencedFakeMessageService();
        fakeMessages.Enqueue(ToolUseResponse("getHandledBySnapshot", Args(new { }), "tu_snapshot"));
        fakeMessages.Enqueue(TextResponse(trendFreeSynthesisText));
        var service = NewService([capability], fakeMessages);

        var outcome = await service.AnswerAsync(
            BusinessId, "How has AI-vs-human handling trended over the last 30 days?", CancellationToken.None);

        Assert.Equal(AnalystOutcomeType.Answered, outcome.Type);

        // (1) The synthesis input preserves the capability result as
        // bounded factual data, unaltered — the exact planted content must
        // appear byte-for-byte in the tool_result block sent to Phase 2.
        var synthesisRequest = fakeMessages.Requests[1];
        var toolResultMessage = synthesisRequest.Messages.Last();
        Assert.True(toolResultMessage.Content.TryPickContentBlockParams(out var blocks));
        Assert.True(blocks![0].TryPickToolResult(out var toolResult));
        Assert.Equal(plantedTimeSeriesSoundingOutput, toolResult!.Content.Match(s => s, _ => ""));
        Assert.False(toolResult.IsError);

        // (2) The final response introduces no unsupported temporal/trend
        // claim derived from that data — proven structurally, not by a
        // banned-word check: the orchestration layer never transforms,
        // enriches, or reformats Claude's own synthesis text, so the
        // returned answer is exactly what Phase 2 said, character for
        // character. Since this test's own Phase-2 text contains no trend
        // claim, and the orchestration is proven here to add nothing of its
        // own, the final response can contain none either.
        Assert.Equal(trendFreeSynthesisText, outcome.ResponseText);
    }

    // ── Claude API failure — a 5th, this-service-internal outcome ──────
    // (owner decision, this phase — see IAnalystOrchestrationService's own
    // doc comment for why this is not folded into Refused).

    public static IEnumerable<object[]> ClaudeFailureExceptions()
    {
        yield return [new AnthropicRateLimitException { StatusCode = HttpStatusCode.TooManyRequests, ResponseBody = "{}" }];
        yield return [new Anthropic5xxException { StatusCode = HttpStatusCode.InternalServerError, ResponseBody = "{}" }];
        yield return [new AnthropicApiException("Simulated API error") { StatusCode = HttpStatusCode.BadRequest, ResponseBody = "{}" }];
        yield return [new HttpRequestException("Simulated network failure")];
    }

    [Theory]
    [MemberData(nameof(ClaudeFailureExceptions))]
    public async Task AnswerAsync_ClaudeCallFailsDuringSelection_ReturnsClaudeCallFailed_NeverThrows(Exception exception)
    {
        var fakeMessages = new SequencedFakeMessageService { ExceptionToThrow = exception };
        var service = NewService([], fakeMessages);

        var outcome = await service.AnswerAsync(BusinessId, "Anything?", CancellationToken.None);

        Assert.Equal(AnalystOutcomeType.ClaudeCallFailed, outcome.Type);
    }

    [Theory]
    [MemberData(nameof(ClaudeFailureExceptions))]
    public async Task AnswerAsync_ClaudeCallFailsDuringSynthesis_ReturnsClaudeCallFailed_AfterCapabilitySucceeded(Exception exception)
    {
        // SequencedFakeMessageService's ExceptionToThrow is all-or-nothing
        // (every call throws), which cannot express "succeed on the
        // selection call, then fail on the synthesis call" — this uses a
        // small purpose-built fake instead (see
        // SelectionThenThrowingMessageService below).
        var capability = new RecordingAnalystCapability { Name = "getConversationsByDay" };
        var fakeMessages = new SelectionThenThrowingMessageService(
            ToolUseResponse("getConversationsByDay", Args(new { range = "7d" }), "tu_1"), exception);
        var service = NewService([capability], fakeMessages);

        var outcome = await service.AnswerAsync(BusinessId, "Anything?", CancellationToken.None);

        Assert.Equal(AnalystOutcomeType.ClaudeCallFailed, outcome.Type);
        Assert.Equal(1, capability.CallCount);
    }

    // ── Telemetry: raw staff question text must never be logged ────────
    // (locked Telemetry decision, design §0/§8).

    [Fact]
    public async Task AnswerAsync_NeverLogsRawQuestionText()
    {
        const string distinctiveQuestion = "XyzzyDistinctiveQuestionMarkerThatMustNeverAppearInLogs12345";
        var capturingLogger = new CapturingLogger<AnalystOrchestrationService>();
        var fakeMessages = new SequencedFakeMessageService();
        fakeMessages.Enqueue(TextResponse("some plausible-sounding text"));
        var service = NewService([], fakeMessages, logger: capturingLogger);

        await service.AnswerAsync(BusinessId, distinctiveQuestion, CancellationToken.None);

        Assert.NotEmpty(capturingLogger.Entries);
        Assert.DoesNotContain(capturingLogger.Entries, e => e.Message.Contains(distinctiveQuestion));
    }

    // ── A1 Slice 4b (design §10.2): invocation capture at A4/A5 ────────

    // T-A4b1. Mutations: Guid.NewGuid() per call; Sequence order swapped
    // (A4 takes 1, A5 takes 0); the two Operations swapped; a different
    // BusinessId; one Sequence value reused for both calls.
    [Fact]
    public async Task AnswerAsync_Answered_RecordsSelectionSeq0AndSynthesisSeq1_UnderOneCorrelation()
    {
        var capture = new ConcurrentCapturingAiInvocationRecorder();
        var capability = new RecordingAnalystCapability { Name = "getConversationsByDay" };
        var fakeMessages = new SequencedFakeMessageService();
        fakeMessages.Enqueue(ToolUseResponse("getConversationsByDay", Args(new { range = "7d" }), "tu_1"));
        fakeMessages.Enqueue(TextResponse("Here is the summary."));
        var service = NewService([capability], fakeMessages, capture);

        var outcome = await service.AnswerAsync(BusinessId, "How many conversations this week?", CancellationToken.None);

        Assert.Equal(AnalystOutcomeType.Answered, outcome.Type);
        var records = capture.ForBusiness(BusinessId);
        Assert.Equal(2, records.Count);
        var (selection, synthesis) = (records[0], records[1]);
        Assert.Equal(selection.CorrelationId, synthesis.CorrelationId);
        Assert.Equal(AiOperations.AnalystSelection, selection.Operation);
        Assert.Equal(0, selection.Sequence);
        Assert.Equal(AiOperations.AnalystSynthesis, synthesis.Operation);
        Assert.Equal(1, synthesis.Sequence);
        Assert.All(records, r =>
        {
            Assert.Equal(BusinessId, r.BusinessId);
            Assert.Null(r.ConversationId);
            Assert.Equal(AiProviders.Anthropic, r.Provider);
            Assert.Equal(AiOutcomes.Succeeded, r.Outcome);
        });
    }

    // T-A4b2 (§14 Q1 ruling): every path that ends after the selection call
    // records exactly one row. Mutation: the selection context takes
    // Sequence 1.
    [Theory]
    [InlineData("NoToolUse", AnalystOutcomeType.Refused)]
    [InlineData("OverCeiling", AnalystOutcomeType.Refused)]
    [InlineData("NotSupportedReplay", AnalystOutcomeType.ClaudeCallFailed)]
    [InlineData("CapabilityFailed", AnalystOutcomeType.CapabilityFailed)]
    public async Task AnswerAsync_PathEndsAfterSelection_RecordsExactlyOneSelectionRowSeq0(string path, AnalystOutcomeType expectedOutcome)
    {
        var capture = new ConcurrentCapturingAiInvocationRecorder();
        var capabilities = new[]
        {
            new RecordingAnalystCapability { Name = "getConversationsByDay", ResultIsError = path == "CapabilityFailed" },
            new RecordingAnalystCapability { Name = "getConversationStatusDistribution" },
            new RecordingAnalystCapability { Name = "getHandoffsByDay" },
            new RecordingAnalystCapability { Name = "getTicketStatusDistribution" },
        };
        var fakeMessages = new SequencedFakeMessageService();
        fakeMessages.Enqueue(path switch
        {
            "NoToolUse" => TextResponse("some plausible-sounding text"),
            "OverCeiling" => MultiToolUseResponse(capabilities.Select((c, i) => (c.Name, Args(new { range = "7d" }), $"tu_{i}")).ToList()),
            "NotSupportedReplay" => ToolUseWithThinkingResponse("getConversationsByDay", Args(new { range = "7d" }), "tu_1"),
            "CapabilityFailed" => ToolUseResponse("getConversationsByDay", Args(new { range = "7d" }), "tu_1"),
            _ => throw new ArgumentOutOfRangeException(nameof(path)),
        });
        var service = NewService(capabilities, fakeMessages, capture);

        var outcome = await service.AnswerAsync(BusinessId, "Anything?", CancellationToken.None);

        Assert.Equal(expectedOutcome, outcome.Type);
        var record = Assert.Single(capture.ForBusiness(BusinessId));
        Assert.Equal(AiOperations.AnalystSelection, record.Operation);
        Assert.Equal(0, record.Sequence);
        Assert.Equal(AiOutcomes.Succeeded, record.Outcome);
    }

    public static IEnumerable<object[]> SelectionOrSynthesisFailureExceptions() =>
        ClaudeFailureExceptions().Append([new TaskCanceledException("Simulated timeout")]);

    // T-A4b3. Mutations: swallow the selection failure inside the call site
    // (outcome changes); or let the call site see a different exception
    // type than the provider threw (outcome changes).
    [Theory]
    [MemberData(nameof(SelectionOrSynthesisFailureExceptions))]
    public async Task AnswerAsync_SelectionFails_StillClaudeCallFailed_RecordsOneFailedSelectionRowSeq0(Exception exception)
    {
        var capture = new ConcurrentCapturingAiInvocationRecorder();
        var fakeMessages = new SequencedFakeMessageService { ExceptionToThrow = exception };
        var service = NewService([], fakeMessages, capture);

        var outcome = await service.AnswerAsync(BusinessId, "Anything?", CancellationToken.None);

        Assert.Equal(AnalystOutcomeType.ClaudeCallFailed, outcome.Type);
        var record = Assert.Single(capture.ForBusiness(BusinessId));
        Assert.Equal(AiOperations.AnalystSelection, record.Operation);
        Assert.Equal(0, record.Sequence);
        Assert.Equal(AiOutcomes.Failed, record.Outcome);
    }

    // T-A4b4. Mutations: one Sequence value reused for both calls (the
    // failed row's Seq 1 goes red); Sequence order swapped (both go red).
    [Theory]
    [MemberData(nameof(SelectionOrSynthesisFailureExceptions))]
    public async Task AnswerAsync_SynthesisFails_StillClaudeCallFailed_RecordsSucceededSeq0AndFailedSeq1(Exception exception)
    {
        var capture = new ConcurrentCapturingAiInvocationRecorder();
        var capability = new RecordingAnalystCapability { Name = "getConversationsByDay" };
        var fakeMessages = new SelectionThenThrowingMessageService(
            ToolUseResponse("getConversationsByDay", Args(new { range = "7d" }), "tu_1"), exception);
        var service = NewService([capability], fakeMessages, capture);

        var outcome = await service.AnswerAsync(BusinessId, "Anything?", CancellationToken.None);

        Assert.Equal(AnalystOutcomeType.ClaudeCallFailed, outcome.Type);
        var records = capture.ForBusiness(BusinessId);
        Assert.Equal(2, records.Count);
        Assert.Equal(records[0].CorrelationId, records[1].CorrelationId);
        Assert.Equal(AiOperations.AnalystSelection, records[0].Operation);
        Assert.Equal(0, records[0].Sequence);
        Assert.Equal(AiOutcomes.Succeeded, records[0].Outcome);
        Assert.Equal(AiOperations.AnalystSynthesis, records[1].Operation);
        Assert.Equal(1, records[1].Sequence);
        Assert.Equal(AiOutcomes.Failed, records[1].Outcome);
    }

    // T-A4b5. Mutation: hoist correlationId to a field.
    [Fact]
    public async Task AnswerAsync_TwoCallsOnOneInstance_GetDistinctCorrelationIds()
    {
        var capture = new ConcurrentCapturingAiInvocationRecorder();
        var fakeMessages = new SequencedFakeMessageService();
        fakeMessages.Enqueue(TextResponse("first"));
        fakeMessages.Enqueue(TextResponse("second"));
        var service = NewService([], fakeMessages, capture);

        await service.AnswerAsync(BusinessId, "First question?", CancellationToken.None);
        await service.AnswerAsync(BusinessId, "Second question?", CancellationToken.None);

        var records = capture.ForBusiness(BusinessId);
        Assert.Equal(2, records.Count);
        Assert.NotEqual(records[0].CorrelationId, records[1].CorrelationId);
    }

    // T-A4b6. Mutation: change one call's model literal. Reverting to the
    // identical "claude-sonnet-5" literal is value-equal and can't be seen
    // here (design §10.2; caught by the verifier's grep instead).
    [Fact]
    public async Task AnswerAsync_BothCalls_SendAndRecordAnthropicModelsDefault()
    {
        var capture = new ConcurrentCapturingAiInvocationRecorder();
        var capability = new RecordingAnalystCapability { Name = "getConversationsByDay" };
        var fakeMessages = new SequencedFakeMessageService();
        fakeMessages.Enqueue(ToolUseResponse("getConversationsByDay", Args(new { range = "7d" }), "tu_1"));
        fakeMessages.Enqueue(TextResponse("Here is the summary."));
        var service = NewService([capability], fakeMessages, capture);

        await service.AnswerAsync(BusinessId, "How many conversations this week?", CancellationToken.None);

        Assert.Equal(2, fakeMessages.Requests.Count);
        Assert.All(fakeMessages.Requests, r => Assert.Equal(AnthropicModels.Default, r.Model.Raw()));
        var records = capture.ForBusiness(BusinessId);
        Assert.Equal(2, records.Count);
        Assert.All(records, r => Assert.Equal(AnthropicModels.Default, r.Model));
    }

    // T-A4b7 (behaviour pin). Mutation: catch AnthropicNotConfiguredException
    // in AnswerAsync and return Finish(ClaudeCallFailed, ...).
    [Fact]
    public async Task AnswerAsync_AnthropicNotConfigured_Propagates_RecordsNothing()
    {
        var capture = new ConcurrentCapturingAiInvocationRecorder();
        var service = NewService([], new MissingAnthropicApiKeyMessageService(), capture);

        await Assert.ThrowsAsync<AnthropicNotConfiguredException>(
            () => service.AnswerAsync(BusinessId, "Anything?", CancellationToken.None));

        Assert.Empty(capture.ForBusiness(BusinessId));
    }

    private sealed class ThrowingAnalystCapability : IAnalystCapability
    {
        public string Name => "throwingCapability";
        public string Description => "Test-only fake capability that always throws.";
        public InputSchema InputSchema => new();

        public Task<ToolExecutionResult> ExecuteAsync(
            Guid businessId, IReadOnlyDictionary<string, JsonElement> input, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("simulated internal failure");
    }

    /// <summary>
    /// A minimal, purpose-built IMessageService fake for exactly one test
    /// shape SequencedFakeMessageService cannot express: succeed on the
    /// first Create call, then throw on the second. Not added to
    /// SequencedFakeMessageService itself since no other test needs this
    /// and that type's own doc comment scopes it to
    /// ToolDispatchServiceTests-style all-or-nothing exception behavior.
    /// </summary>
    private sealed class SelectionThenThrowingMessageService(Message firstResponse, Exception secondCallException)
        : IMessageService
    {
        private int _callCount;

        public Task<Message> Create(MessageCreateParams parameters, CancellationToken cancellationToken = default)
        {
            _callCount++;
            if (_callCount == 1)
            {
                return Task.FromResult(firstResponse);
            }

            throw secondCallException;
        }

        public IMessageServiceWithRawResponse WithRawResponse =>
            throw new NotSupportedException();

        public IMessageService WithOptions(Func<ClientOptions, ClientOptions> modifier) =>
            throw new NotSupportedException();

        public IBatchService Batches =>
            throw new NotSupportedException();

        public IAsyncEnumerable<RawMessageStreamEvent> CreateStreaming(MessageCreateParams parameters, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<MessageTokensCount> CountTokens(MessageCountTokensParams parameters, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
