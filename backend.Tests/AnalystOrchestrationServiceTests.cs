using System.Net;
using System.Text.Json;
using Anthropic;
using Anthropic.Core;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using Anthropic.Services;
using Anthropic.Services.Messages;
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
        var service = new AnalystOrchestrationService([], fakeMessages, NullLogger<AnalystOrchestrationService>.Instance);

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
        var service = new AnalystOrchestrationService(capabilities, fakeMessages, NullLogger<AnalystOrchestrationService>.Instance);

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
        var service = new AnalystOrchestrationService(capabilities, fakeMessages, NullLogger<AnalystOrchestrationService>.Instance);

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
        var service = new AnalystOrchestrationService([failingCapability], fakeMessages, NullLogger<AnalystOrchestrationService>.Instance);

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
        var service = new AnalystOrchestrationService(
            [new ThrowingAnalystCapability()], fakeMessages, NullLogger<AnalystOrchestrationService>.Instance);

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
        var service = new AnalystOrchestrationService([], fakeMessages, NullLogger<AnalystOrchestrationService>.Instance);

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
        var service = new AnalystOrchestrationService([capability], fakeMessages, NullLogger<AnalystOrchestrationService>.Instance);

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
        var service = new AnalystOrchestrationService([capability], fakeMessages, NullLogger<AnalystOrchestrationService>.Instance);

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
        var service = new AnalystOrchestrationService([], fakeMessages, NullLogger<AnalystOrchestrationService>.Instance);

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
        var service = new AnalystOrchestrationService([capability], fakeMessages, NullLogger<AnalystOrchestrationService>.Instance);

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
        var service = new AnalystOrchestrationService([], fakeMessages, capturingLogger);

        await service.AnswerAsync(BusinessId, distinctiveQuestion, CancellationToken.None);

        Assert.NotEmpty(capturingLogger.Entries);
        Assert.DoesNotContain(capturingLogger.Entries, e => e.Message.Contains(distinctiveQuestion));
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
