using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Anthropic;
using Anthropic.Core;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using Anthropic.Services;
using Anthropic.Services.Messages;
using backend.Constants;
using backend.Data;
using backend.Interfaces;
using backend.Models;
using Message = Anthropic.Models.Messages.Message;
using backend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace backend.Tests;

/// <summary>
/// Step 27 Slice A1, Slice 3 — AnthropicInvoker. Test plan: T-A1..T-A10 in
/// docs/handoffs/billing-usage-a1-slice3-design-pass.md §7 (as corrected
/// through pass 3), against the locked decisions in
/// docs/handoffs/billing-usage-a1-slice3-decisions-addendum.md (OD-6: no
/// wrapper-level cancellation pre-check). Unit tests use a scripted
/// IMessageService and a CapturingAiInvocationRecorder; T-A6's second half
/// and T-A7 use the real AiInvocationRecorder against the dev DB.
/// </summary>
public class AnthropicInvokerTests : IAsyncLifetime
{
    private static readonly Guid FixedBusinessId = Guid.Parse("a1a1c0d2-0000-4000-8000-000000000001");
    private static readonly Guid FixedCorrelationId = Guid.Parse("a1a1c0d2-0000-4000-8000-0000000000c1");
    private static readonly Guid FixedConversationId = Guid.Parse("a1a1c0d2-0000-4000-8000-0000000000d1");

    private ServiceProvider _provider = null!;
    private readonly List<Guid> _businessIdsToClean = [];

    public Task InitializeAsync()
    {
        var configuration = new ConfigurationBuilder()
            .AddUserSecrets<Program>()
            .Build();
        var connectionUri = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection not found via user-secrets.");

        _provider = new ServiceCollection()
            .AddSingleton<ICurrentTenantAccessor>(new FixedTenantAccessor(null))
            .AddDbContext<AppDbContext>(o => o.UseNpgsql(PostgresConnectionString.FromUri(connectionUri), x => x.UseVector()))
            .BuildServiceProvider();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await TestBusinessCleanup.DeleteThenDisposeAsync(_businessIdsToClean, _provider);
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static AiCallContext Context(Guid? businessId = null) =>
        new(businessId ?? FixedBusinessId, AiOperations.ConversationClassification, FixedCorrelationId, 3, FixedConversationId);

    private static MessageCreateParams Params(string model = AnthropicModels.Default) => new()
    {
        Model = model,
        MaxTokens = 16,
        Messages = [new MessageParam { Role = Role.User, Content = "hi" }],
    };

    // Params built via FromRawUnchecked with the given raw model JSON
    // (null = model key absent), bypassing the typed Model setter.
    private static MessageCreateParams RawParams(string? modelJson)
    {
        var empty = new Dictionary<string, JsonElement>();
        var body = new Dictionary<string, JsonElement>
        {
            ["max_tokens"] = JsonDocument.Parse("16").RootElement.Clone(),
            ["messages"] = JsonDocument.Parse("""[{"role":"user","content":"hi"}]""").RootElement.Clone(),
        };
        if (modelJson is not null)
        {
            body["model"] = JsonDocument.Parse(modelJson).RootElement.Clone();
        }
        return MessageCreateParams.FromRawUnchecked(empty, empty, body);
    }

    private static Message BuildMessage(long input = 11, long output = 7, long? cacheCreation = 3, long? cacheRead = null, string model = "claude-response-model-differs") => new()
    {
        ID = $"msg_test_{Guid.NewGuid():N}",
        Container = null,
        Content = new List<ContentBlock> { new TextBlock { Text = "ok", Citations = null } },
        Model = model,
        StopDetails = null,
        StopReason = "end_turn",
        StopSequence = null,
        Usage = new Usage
        {
            CacheCreation = null,
            CacheCreationInputTokens = cacheCreation,
            CacheReadInputTokens = cacheRead,
            InferenceGeo = null,
            InputTokens = input,
            OutputTokens = output,
            OutputTokensDetails = null,
            ServerToolUse = null,
            ServiceTier = null,
        },
    };

    private static (AnthropicInvoker Invoker, CapturingAiInvocationRecorder Recorder) Build(IMessageService messages)
    {
        var recorder = new CapturingAiInvocationRecorder();
        return (new AnthropicInvoker(messages, recorder, NullLogger<AnthropicInvoker>.Instance), recorder);
    }

    private static HashSet<string> AllowedErrorCategories() =>
        typeof(AiErrorCategories).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet();

    // ── T-A1: success → exactly 1 record, ctx copied, response unchanged ─
    // Mutations: drop the success-path record; CorrelationId from a fresh
    // Guid; Sequence = 0 instead of ctx.Sequence; return a different object.

    [Fact]
    public async Task CreateMessageAsync_Success_RecordsOneSucceededRow_ReturnsSameResponseInstance()
    {
        var response = BuildMessage();
        var (invoker, recorder) = Build(new ScriptedMessageService((_, _) => Task.FromResult(response)));
        var ctx = Context();

        var returned = await invoker.CreateMessageAsync(ctx, Params());

        Assert.Same(response, returned);
        var record = Assert.Single(recorder.Records);
        Assert.Equal(AiProviders.Anthropic, record.Provider);
        Assert.Equal(ctx.BusinessId, record.BusinessId);
        Assert.Equal(ctx.Operation, record.Operation);
        Assert.Equal(FixedCorrelationId, record.CorrelationId);
        Assert.Equal(3, record.Sequence);
        Assert.Equal(FixedConversationId, record.ConversationId);
        Assert.Equal(AiOutcomes.Succeeded, record.Outcome);
        Assert.Null(record.ErrorCategory);
        Assert.False(record.IsUsagePartial);
        Assert.Null(record.InputItemCount);
    }

    // ── T-A2 (v11 Test 12, Anthropic half): usage maps field-for-field ──
    // Mutations: map Output←Input; coalesce a null cache field to 0.

    [Fact]
    public async Task CreateMessageAsync_Success_MapsUsageFieldForField_NullStaysNull()
    {
        var (invoker, recorder) = Build(new ScriptedMessageService((_, _) =>
            Task.FromResult(BuildMessage(input: 11, output: 7, cacheCreation: 3, cacheRead: null))));

        await invoker.CreateMessageAsync(Context(), Params());

        var record = Assert.Single(recorder.Records);
        Assert.Equal(11, record.InputTokens);
        Assert.Equal(7, record.OutputTokens);
        Assert.Equal(3, record.CacheCreationInputTokens);
        Assert.Null(record.CacheReadInputTokens);
    }

    // ── C2 (correction pass): unreadable usage on a SUCCESSFUL call ──────
    // Messages deserialized from raw JSON, so the SDK's JSON-backed usage
    // getters see genuinely missing/mistyped fields.

    private static Message DeserializeMessage(string usageJson) => JsonSerializer.Deserialize<Message>(
        $$$"""{"id":"msg_c2","type":"message","role":"assistant","model":"claude-response-model-differs","content":[{"type":"text","text":"ok"}],"stop_reason":"end_turn","stop_sequence":null,"usage":{{{usageJson}}}}""")!;

    // C3c: if any usage field can't be read, the whole row's usage is null
    // (mirrors OD-2); the row is still Succeeded, the same response is
    // returned, nothing is thrown.
    [Theory]
    [InlineData("""{"output_tokens":6}""")]                                            // missing input_tokens
    [InlineData("""{"input_tokens":5,"output_tokens":6,"cache_read_input_tokens":"x"}""")] // mistyped cache field
    public async Task CreateMessageAsync_UnreadableUsageField_SucceededRow_AllUsageNull_SameResponse_NoThrow(string usageJson)
    {
        var response = DeserializeMessage(usageJson);
        var (invoker, recorder) = Build(new ScriptedMessageService((_, _) => Task.FromResult(response)));

        Message? returned = null;
        var ex = await Record.ExceptionAsync(async () => returned = await invoker.CreateMessageAsync(Context(), Params()));

        Assert.Null(ex);
        Assert.Same(response, returned);
        var record = Assert.Single(recorder.Records);
        Assert.Equal(AiOutcomes.Succeeded, record.Outcome);
        Assert.Null(record.InputTokens);
        Assert.Null(record.OutputTokens);
        Assert.Null(record.CacheCreationInputTokens);
        Assert.Null(record.CacheReadInputTokens);
    }

    // C3d: readable usage with ABSENT cache fields — input/output populated,
    // cache fields null (never 0) and not nulling the others. Mutation:
    // coalesce a null cache field to 0.
    [Fact]
    public async Task CreateMessageAsync_ReadableUsageWithAbsentCacheFields_TokensPopulated_CacheNullNotZero()
    {
        var response = DeserializeMessage("""{"input_tokens":5,"output_tokens":6}""");
        var (invoker, recorder) = Build(new ScriptedMessageService((_, _) => Task.FromResult(response)));

        await invoker.CreateMessageAsync(Context(), Params());

        var record = Assert.Single(recorder.Records);
        Assert.Equal(5, record.InputTokens);
        Assert.Equal(6, record.OutputTokens);
        Assert.Null(record.CacheCreationInputTokens);
        Assert.Null(record.CacheReadInputTokens);
    }

    // ── T-A3 (v11 Test 5): failures → 1 Failed row, null tokens, rethrow ─
    // Each case first makes one SUCCESSFUL call on the same invoker (usage
    // 11/7), so a stale-usage mutation is killable. The caller-cancelled
    // case cancels the caller's CTS *during* Create, then throws
    // OperationCanceledException(token). Expected categories encode the
    // locked OD-3 mapping implemented in AiErrorCategorizer. Mutations:
    // swallow the exception; record
    // usage from a stale variable; map every failure to one category; wrap
    // the exception instead of rethrowing it.

    public static TheoryData<string, string> FailureCases() => new()
    {
        { "RateLimit", AiErrorCategories.RateLimited },
        { "5xx", AiErrorCategories.ServerError },
        { "Api400", AiErrorCategories.InvalidRequest },
        { "IO", AiErrorCategories.Unknown },
        { "TimeoutTce", AiErrorCategories.Timeout },
        { "CallerCancelled", AiErrorCategories.Cancelled },
    };

    [Theory]
    [MemberData(nameof(FailureCases))]
    public async Task CreateMessageAsync_ProviderFailure_RecordsOneFailedRow_NullTokens_RethrowsSameInstance(string failure, string expectedCategory)
    {
        using var cts = new CancellationTokenSource();
        Exception? thrown = null;
        var calls = 0;
        var (invoker, recorder) = Build(new ScriptedMessageService((_, ct) =>
        {
            if (calls++ == 0)
            {
                return Task.FromResult(BuildMessage(input: 11, output: 7));
            }
            thrown = failure switch
            {
                "RateLimit" => new AnthropicRateLimitException { StatusCode = HttpStatusCode.TooManyRequests, ResponseBody = "{}" },
                "5xx" => new Anthropic5xxException { StatusCode = HttpStatusCode.InternalServerError, ResponseBody = "{}" },
                "Api400" => new AnthropicApiException("Simulated API error") { StatusCode = HttpStatusCode.BadRequest, ResponseBody = "{}" },
                "IO" => new AnthropicIOException("Simulated network failure", new HttpRequestException("unreachable")),
                "TimeoutTce" => new TaskCanceledException("Simulated SDK/HttpClient timeout"),
                "CallerCancelled" => CancelThen(cts, ct),
                _ => throw new InvalidOperationException(failure),
            };
            throw thrown;
        }));

        await invoker.CreateMessageAsync(Context(), Params(), cts.Token);
        var ex = await Record.ExceptionAsync(() => invoker.CreateMessageAsync(Context() with { Sequence = 4 }, Params(), cts.Token));

        Assert.NotNull(thrown);
        Assert.Same(thrown, ex);
        Assert.Equal(2, recorder.Records.Count);
        var record = recorder.Records[1];
        Assert.Equal(4, record.Sequence);
        Assert.Equal(AiOutcomes.Failed, record.Outcome);
        Assert.Null(record.InputTokens);
        Assert.Null(record.OutputTokens);
        Assert.Null(record.CacheCreationInputTokens);
        Assert.Null(record.CacheReadInputTokens);
        Assert.Equal(expectedCategory, record.ErrorCategory);
        Assert.False(record.IsUsagePartial);
    }

    private static OperationCanceledException CancelThen(CancellationTokenSource cts, CancellationToken token)
    {
        cts.Cancel();
        return new OperationCanceledException(token);
    }

    // ── T-A4 (v11 Test 6): no raw provider text in ErrorCategory ────────
    // Mutation: ErrorCategory = ex.Message.

    [Fact]
    public async Task CreateMessageAsync_ErrorCategoryNeverContainsRawProviderText()
    {
        const string sentinel = "SENTINEL-2b9e-raw-anthropic-body";
        var (invoker, recorder) = Build(new ScriptedMessageService((_, _) =>
            throw new AnthropicApiException($"Simulated API error {sentinel}") { StatusCode = HttpStatusCode.BadRequest, ResponseBody = sentinel }));

        await Assert.ThrowsAsync<AnthropicApiException>(() => invoker.CreateMessageAsync(Context(), Params()));

        var record = Assert.Single(recorder.Records);
        Assert.Contains(record.ErrorCategory!, AllowedErrorCategories());
        Assert.DoesNotContain(sentinel, record.ErrorCategory!);
    }

    // ── T-A5 (OD-6) ─────────────────────────────────────────────────────
    // AnthropicNotConfiguredException never reached the SDK → 0 records.
    // Mutation: remove the AnthropicNotConfiguredException special case.

    [Fact]
    public async Task CreateMessageAsync_AnthropicNotConfigured_Rethrows_RecordsNothing()
    {
        var (invoker, recorder) = Build(new MissingAnthropicApiKeyMessageService());

        await Assert.ThrowsAsync<AnthropicNotConfiguredException>(() => invoker.CreateMessageAsync(Context(), Params()));

        Assert.Empty(recorder.Records);
    }

    // ── F1 (Slice 4b design §10.6) ──────────────────────────────────────
    // The bare `throw;` must keep MissingAnthropicApiKeyMessageService.Create
    // in the caught trace. Type-qualified on purpose: the bare
    // nameof(...Create) is "Create", which AnthropicInvoker.CreateMessageAsync
    // also contains, so it would pass under the mutant. Mutation: `throw nce;`.

    [Fact]
    public async Task CreateMessageAsync_AnthropicNotConfigured_RethrowKeepsMissingKeyThrowSiteInStackTrace()
    {
        var (invoker, recorder) = Build(new MissingAnthropicApiKeyMessageService());

        Exception? caught = null;
        string? stackTrace = null;
        try
        {
            await invoker.CreateMessageAsync(Context(), Params());
        }
        catch (Exception ex)
        {
            caught = ex;
            stackTrace = ex.StackTrace;
        }

        Assert.IsType<AnthropicNotConfiguredException>(caught);
        Assert.Contains($"{nameof(MissingAnthropicApiKeyMessageService)}.{nameof(MissingAnthropicApiKeyMessageService.Create)}", stackTrace);
        Assert.Empty(recorder.Records);
    }

    // Locked OD-6: no wrapper-level pre-check; every call that reaches the
    // SDK gets a row. A token cancelled before the call still reaches
    // Create; SequencedFakeMessageService.Create checks the token first and
    // throws. Mutation: add ThrowIfCancellationRequested() before Create
    // (0 records).
    [Fact]
    public async Task CreateMessageAsync_PreCancelledToken_StillReachesSdk_RecordsOneFailedRow()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var sdk = new SequencedFakeMessageService();
        var (invoker, recorder) = Build(sdk);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invoker.CreateMessageAsync(Context(), Params(), cts.Token));

        var record = Assert.Single(recorder.Records);
        Assert.Equal(AiOutcomes.Failed, record.Outcome);
        Assert.Equal(AiErrorCategories.Cancelled, record.ErrorCategory);
        Assert.Null(record.InputTokens);
    }

    // ── T-A6 (v11 Test 10, wrapper half) ────────────────────────────────
    // A recorder that throws (breaking its contract) never replaces the
    // provider's result or exception. Mutations: remove the wrapper's guard;
    // let the recorder's exception replace the provider's.

    [Fact]
    public async Task CreateMessageAsync_RecorderThrows_ResponseStillReturned()
    {
        var response = BuildMessage();
        var (invoker, recorder) = Build(new ScriptedMessageService((_, _) => Task.FromResult(response)));
        recorder.ExceptionToThrow = new InvalidOperationException("recorder broke");

        var returned = await invoker.CreateMessageAsync(Context(), Params());

        Assert.Same(response, returned);
    }

    [Fact]
    public async Task CreateMessageAsync_RecorderThrows_OriginalProviderExceptionStillRethrown()
    {
        var thrown = new Anthropic5xxException { StatusCode = HttpStatusCode.InternalServerError, ResponseBody = "{}" };
        var (invoker, recorder) = Build(new ScriptedMessageService((_, _) => throw thrown));
        recorder.ExceptionToThrow = new InvalidOperationException("recorder broke");

        var ex = await Record.ExceptionAsync(() => invoker.CreateMessageAsync(Context(), Params()));

        Assert.Same(thrown, ex);
    }

    // Real recorder against a write that fails (no Business row for the FK,
    // as at AiInvocationRecorderTests.cs:187): the caller still gets the
    // response, and the Decision 1 diagnostic is emitted.
    [Fact]
    public async Task CreateMessageAsync_RealRecorderWriteFails_ResponseReturned_Decision1DiagnosticLogged()
    {
        var logger = new UnauthorizedActionAuditWriterTests.EventCapturingLogger<AiInvocationRecorder>();
        var recorder = new AiInvocationRecorder(_provider.GetRequiredService<IServiceScopeFactory>(), logger);
        var response = BuildMessage();
        var invoker = new AnthropicInvoker(new ScriptedMessageService((_, _) => Task.FromResult(response)), recorder, NullLogger<AnthropicInvoker>.Instance);
        var ctx = new AiCallContext(Guid.NewGuid(), AiOperations.AnalystSynthesis, Guid.NewGuid(), 5); // no Business row

        var returned = await invoker.CreateMessageAsync(ctx, Params());

        Assert.Same(response, returned);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        var fields = entry.State.Where(kv => kv.Key != "{OriginalFormat}").ToDictionary(kv => kv.Key, kv => kv.Value);
        Assert.Equal(ctx.BusinessId, fields["BusinessId"]);
        Assert.Equal(ctx.Operation, fields["Operation"]);
        Assert.Equal(ctx.CorrelationId, fields["CorrelationId"]);
        Assert.Equal(ctx.Sequence, fields["Sequence"]);
        Assert.Equal(nameof(DbUpdateException), fields["ExceptionType"]);
    }

    // ── C5: building the failure row must never replace the provider's
    //    exception ───────────────────────────────────────────────────────
    // Params from FromRawUnchecked with the model JSON null or absent make
    // ReadModelSent throw AnthropicInvalidDataException. Expected: the
    // provider's exception instance propagates, no row, one log entry with
    // non-content fields only. Mutation: remove the failure-path build guard.

    [Theory]
    [InlineData("null")]
    [InlineData(null)] // model key absent
    public async Task CreateMessageAsync_FailureRowUnbuildable_RethrowsOriginalException_NoRow_LogsTypeNameOnly(string? modelJson)
    {
        var empty = new Dictionary<string, JsonElement>();
        var body = new Dictionary<string, JsonElement>
        {
            ["max_tokens"] = JsonDocument.Parse("16").RootElement.Clone(),
            ["messages"] = JsonDocument.Parse("""[{"role":"user","content":"hi"}]""").RootElement.Clone(),
        };
        if (modelJson is not null)
        {
            body["model"] = JsonDocument.Parse(modelJson).RootElement.Clone();
        }
        var parameters = MessageCreateParams.FromRawUnchecked(empty, empty, body);
        Assert.IsType<AnthropicInvalidDataException>(Record.Exception(() => parameters.Model.Raw())); // precondition

        var thrown = new Anthropic5xxException { StatusCode = HttpStatusCode.InternalServerError, ResponseBody = "{}" };
        var recorder = new CapturingAiInvocationRecorder();
        var logger = new UnauthorizedActionAuditWriterTests.EventCapturingLogger<AnthropicInvoker>();
        var invoker = new AnthropicInvoker(new ScriptedMessageService((_, _) => throw thrown), recorder, logger);
        var ctx = Context();

        var ex = await Record.ExceptionAsync(() => invoker.CreateMessageAsync(ctx, parameters));

        Assert.Same(thrown, ex);
        Assert.Empty(recorder.Records);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Null(entry.Exception);
        Assert.Contains("after a failed Anthropic call", (string)entry.State.Single(kv => kv.Key == "{OriginalFormat}").Value!);
        var fields = entry.State.Where(kv => kv.Key != "{OriginalFormat}").ToDictionary(kv => kv.Key, kv => kv.Value);
        Assert.Equal(["BusinessId", "CorrelationId", "ExceptionType", "Operation", "Sequence"], fields.Keys.Order());
        Assert.Equal(nameof(AnthropicInvalidDataException), fields["ExceptionType"]);
        Assert.Equal(ctx.CorrelationId, fields["CorrelationId"]);
    }

    // ── C8: building the success row must never lose the response ───────
    // Same unreadable-model params as C5, but the provider returns
    // successfully. Expected: the same response instance, no throw, no
    // row, one log entry with non-content fields only. Mutation: disable
    // the success-path build guard.

    [Theory]
    [InlineData("null")]
    [InlineData(null)] // model key absent
    public async Task CreateMessageAsync_SuccessRowUnbuildable_ReturnsSameResponse_NoRow_LogsTypeNameOnly(string? modelJson)
    {
        var empty = new Dictionary<string, JsonElement>();
        var body = new Dictionary<string, JsonElement>
        {
            ["max_tokens"] = JsonDocument.Parse("16").RootElement.Clone(),
            ["messages"] = JsonDocument.Parse("""[{"role":"user","content":"hi"}]""").RootElement.Clone(),
        };
        if (modelJson is not null)
        {
            body["model"] = JsonDocument.Parse(modelJson).RootElement.Clone();
        }
        var parameters = MessageCreateParams.FromRawUnchecked(empty, empty, body);
        Assert.IsType<AnthropicInvalidDataException>(Record.Exception(() => parameters.Model.Raw())); // precondition

        var response = BuildMessage();
        var recorder = new CapturingAiInvocationRecorder();
        var logger = new UnauthorizedActionAuditWriterTests.EventCapturingLogger<AnthropicInvoker>();
        var invoker = new AnthropicInvoker(new ScriptedMessageService((_, _) => Task.FromResult(response)), recorder, logger);
        var ctx = Context();

        Message? returned = null;
        var ex = await Record.ExceptionAsync(async () => returned = await invoker.CreateMessageAsync(ctx, parameters));

        Assert.Null(ex);
        Assert.Same(response, returned);
        Assert.Empty(recorder.Records);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Null(entry.Exception);
        Assert.Contains("after a succeeded Anthropic call", (string)entry.State.Single(kv => kv.Key == "{OriginalFormat}").Value!);
        var fields = entry.State.Where(kv => kv.Key != "{OriginalFormat}").ToDictionary(kv => kv.Key, kv => kv.Value);
        Assert.Equal(["BusinessId", "CorrelationId", "ExceptionType", "Operation", "Sequence"], fields.Keys.Order());
        Assert.Equal(nameof(AnthropicInvalidDataException), fields["ExceptionType"]);
        Assert.Equal(ctx.CorrelationId, fields["CorrelationId"]);
    }

    // ── C9a: the failure path rethrows with a bare `throw;` ─────────────
    // Assert.Same can't tell `throw;` from `throw ex;` — both rethrow the
    // same instance, but `throw ex;` resets its stack trace to start in
    // AnthropicInvoker. The provider exception is thrown from a test-owned
    // method, so that frame must still be in the trace of the exception the
    // caller catches. Both failure-path cases: the row builds (one Failed
    // row) and C5's unbuildable row (no row). Mutation: `throw ex;`.

    [Theory]
    [InlineData(false)]
    [InlineData(true)] // C5: model null, failure row unbuildable
    public async Task CreateMessageAsync_ProviderFailure_RethrowKeepsProviderThrowSiteInStackTrace(bool rowUnbuildable)
    {
        var parameters = rowUnbuildable ? RawParams("null") : Params();
        if (rowUnbuildable)
        {
            Assert.IsType<AnthropicInvalidDataException>(Record.Exception(() => parameters.Model.Raw())); // precondition
        }
        var (invoker, recorder) = Build(new ScriptedMessageService((_, _) => ThrowFromTestOwnedProviderFrame()));

        Exception? caught = null;
        string? stackTrace = null;
        try
        {
            await invoker.CreateMessageAsync(Context(), parameters);
        }
        catch (Exception ex)
        {
            caught = ex;
            stackTrace = ex.StackTrace;
        }

        Assert.IsType<Anthropic5xxException>(caught);
        Assert.Contains(nameof(ThrowFromTestOwnedProviderFrame), stackTrace);
        if (rowUnbuildable)
        {
            Assert.Empty(recorder.Records);
        }
        else
        {
            Assert.Equal(AiOutcomes.Failed, Assert.Single(recorder.Records).Outcome);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<Message> ThrowFromTestOwnedProviderFrame() =>
        throw new Anthropic5xxException { StatusCode = HttpStatusCode.InternalServerError, ResponseBody = "{}" };

    // ── C9c (OD-7): a numeric raw-JSON model goes through the raw-JSON
    //    fallback ───────────────────────────────────────────────────────
    // Real SDK over a stub handler that counts sends. Observed test fact,
    // not a guarantee of the SDK's specification: SDK 12.42.0 was observed
    // to reject a numeric model client-side, before any HTTP send — so in
    // this case OD-7's "wire-sent" model means the value passed to the SDK.
    // Model.Raw() throws for it; ReadModelSent's fallback records the raw
    // JSON text. Mutation: a placeholder for a numeric model only, with
    // null/absent handling unchanged.

    [Fact]
    public async Task CreateMessageAsync_NumericRawModel_RealSdkRejectsBeforeSend_FailedRowRecordsRawJson()
    {
        var parameters = RawParams("42");
        Assert.IsType<AnthropicInvalidDataException>(Record.Exception(() => parameters.Model.Raw())); // precondition

        var sends = 0;
        using var httpClient = new HttpClient(new CountingHandler(() => Interlocked.Increment(ref sends)));
        var client = new AnthropicClient { ApiKey = "test-key-not-real", HttpClient = httpClient, MaxRetries = 0 };
        Exception? sdkThrown = null;
        var sdk = new ScriptedMessageService(async (p, ct) =>
        {
            try
            {
                return await client.Messages.Create(p, ct);
            }
            catch (Exception e)
            {
                sdkThrown = e;
                throw;
            }
        });
        var recorder = new CapturingAiInvocationRecorder();
        var logger = new UnauthorizedActionAuditWriterTests.EventCapturingLogger<AnthropicInvoker>();
        var invoker = new AnthropicInvoker(sdk, recorder, logger);

        var ex = await Record.ExceptionAsync(() => invoker.CreateMessageAsync(Context(), parameters));

        Assert.NotNull(sdkThrown);
        Assert.Same(sdkThrown, ex);
        Assert.Equal(0, sends);
        var record = Assert.Single(recorder.Records);
        Assert.Equal(AiOutcomes.Failed, record.Outcome);
        Assert.Equal("42", record.Model);
        Assert.Equal(AiErrorCategories.Unknown, record.ErrorCategory);
        Assert.Empty(logger.Entries);
    }

    // ── T-A7 (v11 Test 5, token clause): the write survives the caller's
    //    token being cancelled during the provider call (real recorder) ──
    // Mutation: pass the caller's token into anything on the recording path
    // (or skip recording once it is cancelled).

    [Fact]
    public async Task CreateMessageAsync_CallerTokenCancelledDuringCall_FailedRowStillPersists()
    {
        var businessId = await SeedBusinessAsync("Anthropic Invoker Cancelled Business");
        var recorder = new AiInvocationRecorder(_provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<AiInvocationRecorder>.Instance);
        using var cts = new CancellationTokenSource();
        var invoker = new AnthropicInvoker(new ScriptedMessageService((_, ct) => throw CancelThen(cts, ct)), recorder, NullLogger<AnthropicInvoker>.Instance);
        var ctx = new AiCallContext(businessId, AiOperations.CustomerReply, Guid.NewGuid(), 0);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invoker.CreateMessageAsync(ctx, Params(), cts.Token));

        var row = await FindRowAsync(ctx.CorrelationId);
        Assert.NotNull(row);
        Assert.Equal(AiOutcomes.Failed, row!.Outcome);
        Assert.Equal(AiErrorCategories.Cancelled, row.ErrorCategory);
        Assert.Null(row.InputTokens);
        Assert.False(row.IsUsagePartial);
    }

    [Fact]
    public async Task CreateMessageAsync_CallerTokenCancelledDuringSuccessfulCall_SucceededRowStillPersists()
    {
        var businessId = await SeedBusinessAsync("Anthropic Invoker Late-Cancel Business");
        var recorder = new AiInvocationRecorder(_provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<AiInvocationRecorder>.Instance);
        using var cts = new CancellationTokenSource();
        var response = BuildMessage(input: 11, output: 7, cacheCreation: 3, cacheRead: 2);
        var invoker = new AnthropicInvoker(new ScriptedMessageService((_, _) =>
        {
            cts.Cancel();
            return Task.FromResult(response);
        }), recorder, NullLogger<AnthropicInvoker>.Instance);
        var ctx = new AiCallContext(businessId, AiOperations.CustomerReply, Guid.NewGuid(), 0);

        var returned = await invoker.CreateMessageAsync(ctx, Params(), cts.Token);

        Assert.Same(response, returned);
        var row = await FindRowAsync(ctx.CorrelationId);
        Assert.NotNull(row);
        Assert.Equal(AiOutcomes.Succeeded, row!.Outcome);
        Assert.Equal(AiProviders.Anthropic, row.Provider);
        Assert.Equal(AnthropicModels.Default, row.Model);
        Assert.Equal(11, row.InputTokens);
        Assert.Equal(7, row.OutputTokens);
        Assert.Equal(3, row.CacheCreationInputTokens);
        Assert.Equal(2, row.CacheReadInputTokens);
        Assert.Null(row.InputItemCount);
        Assert.False(row.IsUsagePartial);
    }

    // ── T-A8: record after the call, never before ───────────────────────
    // Mutation: move the record ahead of the call.

    [Fact]
    public async Task CreateMessageAsync_RecorderNotCalledBeforeSdkCreate()
    {
        CapturingAiInvocationRecorder? recorderRef = null;
        var recordsSeenAtCreate = -1;
        var (invoker, recorder) = Build(new ScriptedMessageService((_, _) =>
        {
            recordsSeenAtCreate = recorderRef!.Records.Count;
            return Task.FromResult(BuildMessage());
        }));
        recorderRef = recorder;

        await invoker.CreateMessageAsync(Context(), Params());

        Assert.Equal(0, recordsSeenAtCreate);
        Assert.Single(recorder.Records);
    }

    // ── T-A9 (v11 Test 9, Anthropic half; OD-7 per the Slice 3 prompt:
    //    record the literal sent) ────────────────────────────────────────
    // The fake response carries a DIFFERENT Model value. Mutations: record
    // Model from the response (Message.Model); record a different constant
    // (e.g. VoyageModels.Voyage3); force AnthropicModels.Default.

    [Theory]
    [InlineData(AnthropicModels.Default)]
    [InlineData("claude-literal-sent-by-caller")]
    public async Task CreateMessageAsync_RecordsModelSent_NotResponseModel(string modelSent)
    {
        var (invoker, recorder) = Build(new ScriptedMessageService((_, _) =>
            Task.FromResult(BuildMessage(model: "claude-response-model-differs"))));

        await invoker.CreateMessageAsync(Context(), Params(modelSent));

        var record = Assert.Single(recorder.Records);
        Assert.Equal(modelSent, record.Model);
    }

    // ── T-A10 (v11 §5:182): the recording write is awaited ──────────────
    // Precondition: the provider fake completes synchronously. Mutation:
    // `_ = recorder.RecordAsync(...)` (not awaited).

    [Fact]
    public async Task CreateMessageAsync_DoesNotCompleteUntilRecordingWriteCompletes()
    {
        var response = BuildMessage();
        var (invoker, recorder) = Build(new ScriptedMessageService((_, _) => Task.FromResult(response)));
        recorder.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var task = invoker.CreateMessageAsync(Context(), Params());

        Assert.Single(recorder.Records);
        Assert.False(task.IsCompleted);

        recorder.Gate.SetResult();
        Assert.Same(response, await task);
    }

    // ── Fixture helpers ─────────────────────────────────────────────────

    private async Task<Guid> SeedBusinessAsync(string name)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var businessId = Guid.NewGuid();
        db.Businesses.Add(new Business
        {
            Id = businessId,
            Name = name,
            TimeZone = "Europe/Copenhagen",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        _businessIdsToClean.Add(businessId);
        return businessId;
    }

    private async Task<AiProviderInvocation?> FindRowAsync(Guid correlationId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.AiProviderInvocations.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.CorrelationId == correlationId);
    }

    private class FixedTenantAccessor(Guid? businessId) : ICurrentTenantAccessor
    {
        public Guid? BusinessId => businessId;
    }

    /// <summary>
    /// IMessageService whose Create runs a test-supplied script — lets a test
    /// cancel the caller's token mid-call, observe the recorder at Create
    /// time, or vary results per call, none of which FakeMessageService or
    /// SequencedFakeMessageService support as-is (Slice 3 design pass §7 T-A3).
    /// </summary>
    private sealed class ScriptedMessageService(Func<MessageCreateParams, CancellationToken, Task<Message>> onCreate) : IMessageService
    {
        public Task<Message> Create(MessageCreateParams parameters, CancellationToken cancellationToken = default) =>
            onCreate(parameters, cancellationToken);

        public IMessageServiceWithRawResponse WithRawResponse => throw new NotSupportedException();

        public IMessageService WithOptions(Func<ClientOptions, ClientOptions> modifier) => throw new NotSupportedException();

        public IBatchService Batches => throw new NotSupportedException();

        public IAsyncEnumerable<RawMessageStreamEvent> CreateStreaming(MessageCreateParams parameters, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<MessageTokensCount> CountTokens(MessageCountTokensParams parameters, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    /// <summary>
    /// HTTP handler under the real SDK that counts sends and answers 500, so
    /// a send that should never happen is visible (C9c).
    /// </summary>
    private sealed class CountingHandler(Action onSend) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            onSend();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("{}") });
        }
    }
}
