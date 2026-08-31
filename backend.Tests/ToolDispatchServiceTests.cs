using System.Net;
using System.Text.Json;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using backend.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace backend.Tests;

/// <summary>
/// Test-only, non-mutating fake — not a real read-only tool. Records what
/// it was called with so tests can assert the dispatch loop supplied the
/// right context/input.
/// </summary>
public class RecordingReadOnlyTool : IReadOnlyDispatchTool
{
    public string Name { get; init; } = "getOrderStatus";
    public string Description => "Test-only fake read-only tool.";
    public InputSchema InputSchema => new();
    public string ResultContent { get; set; } = "shipped";
    public bool ResultIsError { get; set; }
    public ToolDispatchContext? LastContext { get; private set; }
    public IReadOnlyDictionary<string, JsonElement>? LastInput { get; private set; }
    public int CallCount { get; private set; }

    public Task<ToolExecutionResult> ExecuteAsync(
        ToolDispatchContext context, IReadOnlyDictionary<string, JsonElement> input, CancellationToken cancellationToken)
    {
        CallCount++;
        LastContext = context;
        LastInput = input;
        return Task.FromResult(new ToolExecutionResult(ResultContent, ResultIsError));
    }
}

/// <summary>
/// Test-only, non-mutating fake — not a real write tool (does not extend
/// WriteToolBase, since this test only needs to prove the dispatch loop's
/// own orchestration — key derivation before execution, server-derived
/// context, not WriteToolBase's separately-tested authorization gate).
/// </summary>
public class RecordingWriteTool : IWriteDispatchTool
{
    public string Name => "updateCustomer";
    public string Description => "Test-only fake write tool.";
    public InputSchema InputSchema => new();
    public string ResultContent { get; set; } = "updated";
    public ToolDispatchContext? LastContext { get; private set; }
    public string? LastIdempotencyKey { get; private set; }
    public int CallCount { get; private set; }

    public IdempotencyToolInputs DescribeIdempotencyInputs(IReadOnlyDictionary<string, JsonElement> input) =>
        new("customer-1", "email");

    public Task<ToolExecutionResult> ExecuteAsync(
        ToolDispatchContext context, IReadOnlyDictionary<string, JsonElement> input,
        string idempotencyKey, CancellationToken cancellationToken)
    {
        CallCount++;
        LastContext = context;
        LastIdempotencyKey = idempotencyKey;
        return Task.FromResult(ToolExecutionResult.Success(ResultContent));
    }
}

/// <summary>
/// Pure unit tests — no DB, no HTTP. IMessageService is
/// SequencedFakeMessageService throughout, so ToolDispatchService's actual
/// content-block parsing/tool-resolution/history-round-tripping logic runs
/// for real against realistic Anthropic SDK response shapes, only the
/// network call is faked (same philosophy as ClaudeReplyServiceTests /
/// FakeMessageService).
/// </summary>
public class ToolDispatchServiceTests
{
    private static readonly ToolDispatchContext Context = new(
        BusinessId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
        CustomerId: Guid.Parse("22222222-2222-2222-2222-222222222222"),
        ConversationId: Guid.Parse("33333333-3333-3333-3333-333333333333"));

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

    private static Dictionary<string, JsonElement> Args(object anonymous) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(anonymous))!;

    [Fact]
    public async Task RunAsync_NoToolUse_ReturnsCompletedWithText_AndCallsClaudeOnce()
    {
        var fakeMessages = new SequencedFakeMessageService();
        fakeMessages.Enqueue(TextResponse("Hello, how can I help?"));
        var service = new ToolDispatchService([], fakeMessages, NullLogger<ToolDispatchService>.Instance);
        var messages = new List<MessageParam>();

        var outcome = await service.RunAsync(Context, "system prompt", messages, CancellationToken.None);

        Assert.Equal(ToolDispatchStatus.Completed, outcome.Status);
        Assert.Equal("Hello, how can I help?", outcome.ResponseText);
        Assert.Equal(1, fakeMessages.CallCount);
    }

    [Fact]
    public async Task RunAsync_ToolUse_ResolvesRegisteredReadOnlyTool_FeedsResultBack_ThenCompletes()
    {
        var readTool = new RecordingReadOnlyTool { ResultContent = "order 123 has shipped" };
        var fakeMessages = new SequencedFakeMessageService();
        fakeMessages.Enqueue(ToolUseResponse("getOrderStatus", Args(new { orderId = "123" }), "toolu_1"));
        fakeMessages.Enqueue(TextResponse("Your order has shipped!"));
        var service = new ToolDispatchService([readTool], fakeMessages, NullLogger<ToolDispatchService>.Instance);
        var messages = new List<MessageParam>();

        var outcome = await service.RunAsync(Context, "system prompt", messages, CancellationToken.None);

        Assert.Equal(ToolDispatchStatus.Completed, outcome.Status);
        Assert.Equal("Your order has shipped!", outcome.ResponseText);
        Assert.Equal(2, fakeMessages.CallCount);
        Assert.Equal(1, readTool.CallCount);
        Assert.Equal(Context, readTool.LastContext);

        // Assistant turn + tool_result turn were both appended to history.
        Assert.Equal(2, messages.Count);
        Assert.Equal(Role.Assistant, (Role)messages[0].Role);
        Assert.Equal(Role.User, (Role)messages[1].Role);
        Assert.True(messages[1].Content.TryPickContentBlockParams(out var blocks));
        Assert.True(blocks![0].TryPickToolResult(out var toolResult));
        Assert.Equal("toolu_1", toolResult!.ToolUseID);
        Assert.False(toolResult.IsError);
    }

    [Fact]
    public async Task RunAsync_WriteTool_ReceivesServerDerivedContext_AndDispatchDerivedIdempotencyKey()
    {
        var writeTool = new RecordingWriteTool();
        // The model's own JSON input includes a bogus businessId — the tool
        // must never be trusted to have used it; ToolDispatchContext, built
        // by the caller from server-side state, is what the tool actually
        // receives.
        var maliciousInput = Args(new { businessId = Guid.NewGuid().ToString(), fieldsChanged = new[] { "email" } });
        var fakeMessages = new SequencedFakeMessageService();
        fakeMessages.Enqueue(ToolUseResponse("updateCustomer", maliciousInput, "toolu_write_1", "msg_write_1"));
        fakeMessages.Enqueue(TextResponse("Done!"));
        var service = new ToolDispatchService([writeTool], fakeMessages, NullLogger<ToolDispatchService>.Instance);

        var outcome = await service.RunAsync(Context, "system prompt", new List<MessageParam>(), CancellationToken.None);

        Assert.Equal(ToolDispatchStatus.Completed, outcome.Status);
        Assert.Equal(1, writeTool.CallCount);
        Assert.Equal(Context, writeTool.LastContext);
        Assert.Equal(Context.BusinessId, writeTool.LastContext!.BusinessId);

        var expectedKey = IdempotencyKeyDeriver.Derive(
            Context.BusinessId, "updateCustomer", "customer-1", "email", "msg_write_1", "toolu_write_1");
        Assert.Equal(expectedKey, writeTool.LastIdempotencyKey);
    }

    [Fact]
    public async Task RunAsync_MultipleToolTurns_TerminateCorrectlyOnFirstNonToolResponse()
    {
        var readTool = new RecordingReadOnlyTool();
        var fakeMessages = new SequencedFakeMessageService();
        fakeMessages.Enqueue(ToolUseResponse("getOrderStatus", Args(new { orderId = "1" }), "toolu_1", "msg_1"));
        fakeMessages.Enqueue(ToolUseResponse("getOrderStatus", Args(new { orderId = "2" }), "toolu_2", "msg_2"));
        fakeMessages.Enqueue(TextResponse("All done."));
        var service = new ToolDispatchService([readTool], fakeMessages, NullLogger<ToolDispatchService>.Instance);

        var outcome = await service.RunAsync(Context, "system prompt", new List<MessageParam>(), CancellationToken.None);

        Assert.Equal(ToolDispatchStatus.Completed, outcome.Status);
        Assert.Equal("All done.", outcome.ResponseText);
        Assert.Equal(3, fakeMessages.CallCount);
        Assert.Equal(2, readTool.CallCount);
    }

    [Fact]
    public async Task RunAsync_CancellationRequested_ThrowsAndNeverCallsClaude()
    {
        var fakeMessages = new SequencedFakeMessageService();
        fakeMessages.Enqueue(TextResponse("should never be reached"));
        var service = new ToolDispatchService([], fakeMessages, NullLogger<ToolDispatchService>.Instance);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.RunAsync(Context, "system prompt", new List<MessageParam>(), cts.Token));

        Assert.Equal(0, fakeMessages.CallCount);
    }

    [Fact]
    public async Task RunAsync_IterationLimitReached_ReturnsIterationLimitReached_NeverCompleted()
    {
        var readTool = new RecordingReadOnlyTool();
        var fakeMessages = new SequencedFakeMessageService();
        // Always returns a tool_use response — the model "never stops
        // calling tools" — enough queued for well past any reasonable
        // iteration bound.
        for (var i = 0; i < 50; i++)
        {
            fakeMessages.Enqueue(ToolUseResponse("getOrderStatus", Args(new { orderId = i.ToString() }), $"toolu_{i}", $"msg_{i}"));
        }
        var service = new ToolDispatchService([readTool], fakeMessages, NullLogger<ToolDispatchService>.Instance);

        var outcome = await service.RunAsync(Context, "system prompt", new List<MessageParam>(), CancellationToken.None);

        Assert.Equal(ToolDispatchStatus.IterationLimitReached, outcome.Status);
        Assert.Null(outcome.ResponseText);
        // Bounded: the loop must have stopped well short of the 50 queued
        // responses — the exact bound is a separate, flagged-as-new owner
        // decision (see the implementation report), so this only asserts
        // "bounded and small," not a specific number.
        Assert.True(fakeMessages.CallCount is > 0 and < 50);
    }

    [Fact]
    public async Task RunAsync_UnknownTool_ReturnsErrorToolResult_AndContinuesLoop()
    {
        var fakeMessages = new SequencedFakeMessageService();
        fakeMessages.Enqueue(ToolUseResponse("thisToolDoesNotExist", Args(new { }), "toolu_unknown"));
        fakeMessages.Enqueue(TextResponse("Sorry, I could not do that."));
        var service = new ToolDispatchService([], fakeMessages, NullLogger<ToolDispatchService>.Instance);
        var messages = new List<MessageParam>();

        var outcome = await service.RunAsync(Context, "system prompt", messages, CancellationToken.None);

        Assert.Equal(ToolDispatchStatus.Completed, outcome.Status);
        Assert.Equal(2, fakeMessages.CallCount);
        Assert.True(messages[1].Content.TryPickContentBlockParams(out var blocks));
        Assert.True(blocks![0].TryPickToolResult(out var toolResult));
        Assert.True(toolResult!.IsError);
    }

    [Fact]
    public async Task RunAsync_ToolThrows_ReturnsErrorToolResult_NeverCrashesTheLoop()
    {
        var throwingTool = new ThrowingReadOnlyTool();
        var fakeMessages = new SequencedFakeMessageService();
        fakeMessages.Enqueue(ToolUseResponse(throwingTool.Name, Args(new { }), "toolu_throw"));
        fakeMessages.Enqueue(TextResponse("Handled gracefully."));
        var service = new ToolDispatchService([throwingTool], fakeMessages, NullLogger<ToolDispatchService>.Instance);
        var messages = new List<MessageParam>();

        var outcome = await service.RunAsync(Context, "system prompt", messages, CancellationToken.None);

        Assert.Equal(ToolDispatchStatus.Completed, outcome.Status);
        Assert.True(messages[1].Content.TryPickContentBlockParams(out var blocks));
        Assert.True(blocks![0].TryPickToolResult(out var toolResult));
        Assert.True(toolResult!.IsError);
        // Never leaks the raw exception message to the model-visible result.
        Assert.DoesNotContain("simulated internal failure", toolResult.Content.Match(s => s, _ => ""), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(ClaudeFailureExceptions))]
    public async Task RunAsync_ClaudeCallFails_ReturnsClaudeCallFailed_NeverThrows(Exception exception)
    {
        var fakeMessages = new SequencedFakeMessageService { ExceptionToThrow = exception };
        var service = new ToolDispatchService([], fakeMessages, NullLogger<ToolDispatchService>.Instance);

        var outcome = await service.RunAsync(Context, "system prompt", new List<MessageParam>(), CancellationToken.None);

        Assert.Equal(ToolDispatchStatus.ClaudeCallFailed, outcome.Status);
        Assert.Null(outcome.ResponseText);
    }

    public static IEnumerable<object[]> ClaudeFailureExceptions()
    {
        yield return [new AnthropicRateLimitException { StatusCode = HttpStatusCode.TooManyRequests, ResponseBody = "{}" }];
        yield return [new Anthropic5xxException { StatusCode = HttpStatusCode.InternalServerError, ResponseBody = "{}" }];
        yield return [new AnthropicApiException("Simulated API error") { StatusCode = HttpStatusCode.BadRequest, ResponseBody = "{}" }];
        yield return [new HttpRequestException("Simulated network failure")];
    }

    private class ThrowingReadOnlyTool : IReadOnlyDispatchTool
    {
        public string Name => "throwingTool";
        public string Description => "Test-only fake tool that always throws.";
        public InputSchema InputSchema => new();

        public Task<ToolExecutionResult> ExecuteAsync(
            ToolDispatchContext context, IReadOnlyDictionary<string, JsonElement> input, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("simulated internal failure");
    }
}
