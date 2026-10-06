using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using backend.Constants;
using backend.Data;
using backend.Enums;
using backend.Interfaces;
using backend.Models;
using backend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace backend.Tests;

/// <summary>
/// Step 27 Slice A1, Slice 3 — VoyageEmbeddingInvoker. Test plan: T-I1..T-I10
/// in docs/handoffs/billing-usage-a1-slice3-design-pass.md §7, against the
/// locked decisions in docs/handoffs/billing-usage-a1-slice3-decisions-addendum.md
/// (Q1, OD-2, OD-4, OD-5, OD-6, OD-9). Unit tests run FakeVoyageEmbeddingClient
/// (BatchSize 2, so 4 texts = 2 batches) under a CapturingAiInvocationRecorder;
/// the DB-backed tests use the real AiInvocationRecorder against the dev DB,
/// same bare-ServiceProvider pattern as AiInvocationRecorderTests.
/// </summary>
public class VoyageEmbeddingInvokerTests : IAsyncLifetime
{
    private static readonly Guid FixedBusinessId = Guid.Parse("7a1c0d2e-0000-4000-8000-000000000001");
    private static readonly Guid FixedCorrelationId = Guid.Parse("7a1c0d2e-0000-4000-8000-0000000000c1");
    private static readonly Guid FixedConversationId = Guid.Parse("7a1c0d2e-0000-4000-8000-0000000000d1");
    private static readonly string[] FourTexts = ["t0", "t1", "t2", "t3"];

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

    private static AiCallContext Context(Guid? businessId = null) =>
        new(businessId ?? FixedBusinessId, AiOperations.KnowledgeIndexing, FixedCorrelationId, 3, FixedConversationId);

    // Batch 0 ("t0","t1") reports 30, batch 1 ("t2","t3") reports 12.
    private static FakeVoyageEmbeddingClient TwoBatchFake() => new()
    {
        BatchSize = 2,
        UsageForBatch = batch => batch[0] == "t0" ? 30 : 12,
    };

    private static (VoyageEmbeddingInvoker Invoker, CapturingAiInvocationRecorder Recorder) Build(IVoyageEmbeddingClient client)
    {
        var recorder = new CapturingAiInvocationRecorder();
        return (new VoyageEmbeddingInvoker(client, recorder, NullLogger<VoyageEmbeddingInvoker>.Instance), recorder);
    }

    private static HashSet<string> AllowedErrorCategories() =>
        typeof(AiErrorCategories).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet();

    private static void AssertContextCopied(AiInvocationRecordRequest record, AiCallContext ctx)
    {
        Assert.Equal(ctx.BusinessId, record.BusinessId);
        Assert.Equal(ctx.Operation, record.Operation);
        Assert.Equal(ctx.CorrelationId, record.CorrelationId);
        Assert.Equal(ctx.Sequence, record.Sequence);
        Assert.Equal(ctx.ConversationId, record.ConversationId);
    }

    // ── T-I1: full success over 2 batches ───────────────────────────────
    // Mutations: record twice (success path + finally) -> 2 rows; set
    // OutputTokens; InputItemCount from completed batches instead of all
    // texts (OD-5).

    [Fact]
    public async Task EmbedAsync_FullSuccessOverTwoBatches_RecordsExactlyOneSucceededRow()
    {
        var (invoker, recorder) = Build(TwoBatchFake());
        var ctx = Context();

        var result = await invoker.EmbedAsync(ctx, FourTexts, VoyageInputType.Document);

        Assert.Equal(4, result.Count);
        Assert.Equal(FakeVoyageEmbeddingClient.DeterministicVector("t3"), result[3]);

        var record = Assert.Single(recorder.Records);
        AssertContextCopied(record, ctx);
        Assert.Equal(AiProviders.Voyage, record.Provider);
        Assert.Equal(VoyageModels.Voyage3, record.Model);
        Assert.Equal(42, record.InputTokens);
        Assert.Null(record.OutputTokens);
        Assert.Null(record.CacheCreationInputTokens);
        Assert.Null(record.CacheReadInputTokens);
        Assert.Equal(AiOutcomes.Succeeded, record.Outcome);
        Assert.Null(record.ErrorCategory);
        Assert.False(record.IsUsagePartial);
        Assert.Equal(4, record.InputItemCount); // OD-5: all requested texts
    }

    // ── T-I2 (v11 Test 5): fails on batch 1 → null, not 0; not partial ──
    // Mutations: `?? 0`; set IsUsagePartial from Failed alone.

    [Fact]
    public async Task EmbedAsync_FailsOnFirstBatch_RecordsFailedWithNullTokens_NotPartial()
    {
        var fake = TwoBatchFake();
        var thrown = new VoyageEmbeddingException("Voyage embeddings request failed with status 500: x");
        fake.ExceptionToThrow = thrown;
        fake.FailAtBatch = 0;
        var (invoker, recorder) = Build(fake);

        var ex = await Assert.ThrowsAsync<VoyageEmbeddingException>(() => invoker.EmbedAsync(Context(), FourTexts, VoyageInputType.Document));

        Assert.Same(thrown, ex);
        var record = Assert.Single(recorder.Records);
        Assert.Equal(AiOutcomes.Failed, record.Outcome);
        Assert.Null(record.InputTokens);
        Assert.False(record.IsUsagePartial);
        Assert.NotNull(record.ErrorCategory);
        Assert.Contains(record.ErrorCategory!, AllowedErrorCategories());
        Assert.Equal(4, record.InputItemCount); // OD-5: unchanged by outcome
    }

    // ── T-I3 (v11 Test 5, R10): fails on batch 2 of 2 → partial sum ─────
    // Mutations: drop the partial sum on failure; estimate the failed
    // batch; force IsUsagePartial=false; InputItemCount = completed texts.

    [Fact]
    public async Task EmbedAsync_FailsOnSecondBatch_RecordsFailedWithFirstBatchSum_Partial_Rethrows()
    {
        var fake = TwoBatchFake();
        var thrown = new VoyageEmbeddingException("Voyage embeddings request failed with status 500: x");
        fake.ExceptionToThrow = thrown;
        fake.FailAtBatch = 1;
        var (invoker, recorder) = Build(fake);

        var ex = await Assert.ThrowsAsync<VoyageEmbeddingException>(() => invoker.EmbedAsync(Context(), FourTexts, VoyageInputType.Document));

        Assert.Same(thrown, ex);
        var record = Assert.Single(recorder.Records);
        Assert.Equal(AiOutcomes.Failed, record.Outcome);
        Assert.Equal(30, record.InputTokens);
        Assert.True(record.IsUsagePartial);
        Assert.Equal(4, record.InputItemCount);
        Assert.Null(record.OutputTokens);
    }

    // ── T-I4: what the real client throws, after batch 1 completed ──────
    // Caller cancellation: the fake cancels the caller's CTS during the call
    // and throws a raw OperationCanceledException (the real client never
    // wraps it). No wrapper pre-check under locked OD-6, so it gets a row.
    // ErrorCategory = Cancelled encodes the locked OD-3 mapping (see
    // AiErrorCategorizer).
    // Mutation: handle only VoyageEmbeddingException, so the OCE path
    // records nothing.

    [Fact]
    public async Task EmbedAsync_CallerCancelledDuringSecondBatch_RecordsCancelledWithPartialSum()
    {
        using var cts = new CancellationTokenSource();
        var fake = TwoBatchFake();
        var thrown = new OperationCanceledException(cts.Token);
        fake.ExceptionToThrow = thrown;
        fake.FailAtBatch = 1;
        fake.OnFailing = cts.Cancel;
        var (invoker, recorder) = Build(fake);

        var ex = await Assert.ThrowsAsync<OperationCanceledException>(
            () => invoker.EmbedAsync(Context(), FourTexts, VoyageInputType.Document, cts.Token));

        Assert.Same(thrown, ex);
        var record = Assert.Single(recorder.Records);
        Assert.Equal(AiOutcomes.Failed, record.Outcome);
        Assert.Equal(AiErrorCategories.Cancelled, record.ErrorCategory);
        Assert.Equal(30, record.InputTokens);
        Assert.True(record.IsUsagePartial);
    }

    // Timeout: the real client's shape — VoyageEmbeddingException with an
    // inner TaskCanceledException. ErrorCategory = Timeout encodes the
    // locked OD-3 mapping. Mutation: drop the partial sum
    // when the exception is a VoyageEmbeddingException.
    [Fact]
    public async Task EmbedAsync_TimeoutDuringSecondBatch_RecordsTimeoutWithPartialSum()
    {
        var fake = TwoBatchFake();
        var thrown = new VoyageEmbeddingException("Voyage embeddings request timed out.", new TaskCanceledException());
        fake.ExceptionToThrow = thrown;
        fake.FailAtBatch = 1;
        var (invoker, recorder) = Build(fake);

        var ex = await Assert.ThrowsAsync<VoyageEmbeddingException>(() => invoker.EmbedAsync(Context(), FourTexts, VoyageInputType.Document));

        Assert.Same(thrown, ex);
        var record = Assert.Single(recorder.Records);
        Assert.Equal(AiOutcomes.Failed, record.Outcome);
        Assert.Equal(AiErrorCategories.Timeout, record.ErrorCategory);
        Assert.Equal(30, record.InputTokens);
        Assert.True(record.IsUsagePartial);
    }

    // ── T-I5: a fresh accumulator per call ──────────────────────────────
    // Mutation: hoist the accumulator to a field.

    [Fact]
    public async Task EmbedAsync_TwoSequentialCalls_RecordIndependentSums()
    {
        var (invoker, recorder) = Build(TwoBatchFake());

        await invoker.EmbedAsync(Context(), FourTexts, VoyageInputType.Document);
        await invoker.EmbedAsync(Context() with { Sequence = 4 }, ["t2"], VoyageInputType.Document);

        Assert.Equal(2, recorder.Records.Count);
        Assert.Equal(42, recorder.Records[0].InputTokens);
        Assert.Equal(12, recorder.Records[1].InputTokens); // not 54
        Assert.Equal(1, recorder.Records[1].InputItemCount);
    }

    // ── T-I6 (OD-6): calls that never reach the client/HTTP → no row ────
    // Mutation: record unconditionally.

    [Fact]
    public async Task EmbedAsync_EmptyTexts_ReturnsEmpty_RecordsNothing()
    {
        var (invoker, recorder) = Build(TwoBatchFake());

        var result = await invoker.EmbedAsync(Context(), [], VoyageInputType.Document);

        Assert.Empty(result);
        Assert.Empty(recorder.Records);
    }

    [Fact]
    public async Task EmbedAsync_VoyageNotConfigured_Rethrows_RecordsNothing()
    {
        var (invoker, recorder) = Build(new MissingVoyageApiKeyEmbeddingClient());

        await Assert.ThrowsAsync<VoyageNotConfiguredException>(() => invoker.EmbedAsync(Context(), FourTexts, VoyageInputType.Document));

        Assert.Empty(recorder.Records);
    }

    // ── T-I8 (v11 Test 12, Voyage half) ─────────────────────────────────
    // InputTokens is the summed total_tokens; OutputTokens is null. Same
    // mutations as T-I1 (DP §7 says "covered by T-I1's mutations"); kept as
    // its own test for traceability.

    [Fact]
    public async Task EmbedAsync_Success_InputTokensFromSummedTotalTokens_OutputTokensNull()
    {
        var fake = new FakeVoyageEmbeddingClient { BatchSize = 1, UsageForBatch = _ => 7 };
        var (invoker, recorder) = Build(fake);

        await invoker.EmbedAsync(Context(), ["a", "b", "c"], VoyageInputType.Query);

        var record = Assert.Single(recorder.Records);
        Assert.Equal(21, record.InputTokens);
        Assert.Null(record.OutputTokens);
    }

    // ── T-I9 (v11 §5:182): the recording write is awaited ───────────────
    // Precondition: the fake client completes synchronously (Task.FromResult).
    // Mutation: `_ = recorder.RecordAsync(...)` (not awaited).

    [Fact]
    public async Task EmbedAsync_DoesNotCompleteUntilRecordingWriteCompletes()
    {
        var (invoker, recorder) = Build(TwoBatchFake());
        recorder.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var task = invoker.EmbedAsync(Context(), FourTexts, VoyageInputType.Document);

        Assert.Single(recorder.Records);
        Assert.False(task.IsCompleted);

        recorder.Gate.SetResult();
        var result = await task;
        Assert.Equal(4, result.Count);
    }

    // ── C6: Voyage equivalent of T-A6 (v11 Test 10, wrapper half) ───────
    // A recorder that throws (breaking its contract) never replaces the
    // provider's result or exception; the guard logs non-content fields
    // only. Mutation: remove the wrapper's recorder guard (M11).

    private sealed class FixedResultVoyageClient(IReadOnlyList<float[]> result) : IVoyageEmbeddingClient
    {
        public Task<IReadOnlyList<float[]>> EmbedAsync(
            IReadOnlyList<string> texts, VoyageInputType inputType, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<float[]>> EmbedWithUsageAsync(
            IReadOnlyList<string> texts, VoyageInputType inputType, VoyageUsageAccumulator usage,
            CancellationToken cancellationToken = default)
        {
            usage.SetRequestedBatches(1);
            usage.AddCompletedBatch(5);
            return Task.FromResult(result);
        }
    }

    private static void AssertRecorderThrowLoggedTypeNameOnly(
        UnauthorizedActionAuditWriterTests.EventCapturingLogger<VoyageEmbeddingInvoker> logger, AiCallContext ctx)
    {
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Error, entry.Level);
        Assert.Null(entry.Exception);
        var fields = entry.State.Where(kv => kv.Key != "{OriginalFormat}").ToDictionary(kv => kv.Key, kv => kv.Value);
        Assert.Equal(["BusinessId", "CorrelationId", "ExceptionType", "Operation", "Sequence"], fields.Keys.Order());
        Assert.Equal(nameof(InvalidOperationException), fields["ExceptionType"]);
        Assert.Equal(ctx.CorrelationId, fields["CorrelationId"]);
    }

    [Fact]
    public async Task EmbedAsync_RecorderThrows_SameResultStillReturned()
    {
        IReadOnlyList<float[]> expected = [[1f], [2f]];
        var recorder = new CapturingAiInvocationRecorder { ExceptionToThrow = new InvalidOperationException("recorder broke") };
        var logger = new UnauthorizedActionAuditWriterTests.EventCapturingLogger<VoyageEmbeddingInvoker>();
        var invoker = new VoyageEmbeddingInvoker(new FixedResultVoyageClient(expected), recorder, logger);
        var ctx = Context();

        IReadOnlyList<float[]>? returned = null;
        var ex = await Record.ExceptionAsync(async () => returned = await invoker.EmbedAsync(ctx, ["a", "b"], VoyageInputType.Document));

        Assert.Null(ex);
        Assert.Same(expected, returned);
        Assert.Single(recorder.Records);
        AssertRecorderThrowLoggedTypeNameOnly(logger, ctx);
    }

    [Fact]
    public async Task EmbedAsync_RecorderThrows_OriginalProviderExceptionStillRethrown()
    {
        var fake = TwoBatchFake();
        var thrown = new VoyageEmbeddingException("Voyage embeddings request failed with status 500: x");
        fake.ExceptionToThrow = thrown;
        fake.FailAtBatch = 1;
        var recorder = new CapturingAiInvocationRecorder { ExceptionToThrow = new InvalidOperationException("recorder broke") };
        var logger = new UnauthorizedActionAuditWriterTests.EventCapturingLogger<VoyageEmbeddingInvoker>();
        var invoker = new VoyageEmbeddingInvoker(fake, recorder, logger);
        var ctx = Context();

        var ex = await Record.ExceptionAsync(() => invoker.EmbedAsync(ctx, FourTexts, VoyageInputType.Document));

        Assert.Same(thrown, ex);
        Assert.Single(recorder.Records);
        AssertRecorderThrowLoggedTypeNameOnly(logger, ctx);
    }

    // ── T-I10 (v11 Test 6, Voyage): no raw provider text in ErrorCategory ─
    // Mutations: ErrorCategory = ex.Message; copy a raw substring of it.

    [Fact]
    public async Task EmbedAsync_ErrorCategoryNeverContainsRawProviderText()
    {
        const string sentinel = "SENTINEL-7f3a-raw-voyage-body";
        var fake = TwoBatchFake();
        var thrown = new VoyageEmbeddingException($"Voyage embeddings request failed with status 500: {sentinel}");
        fake.ExceptionToThrow = thrown;
        var (invoker, recorder) = Build(fake);

        var ex = await Assert.ThrowsAsync<VoyageEmbeddingException>(() => invoker.EmbedAsync(Context(), FourTexts, VoyageInputType.Document));

        Assert.Same(thrown, ex);
        var record = Assert.Single(recorder.Records);
        Assert.Contains(record.ErrorCategory!, AllowedErrorCategories());
        Assert.DoesNotContain(sentinel, record.ErrorCategory!);
        Assert.DoesNotContain("500", record.ErrorCategory!);
    }

    // ── OD-9 validation cases (addendum §9) ─────────────────────────────
    // Cases 2, 3 and 5 are T-I2, T-I3 and T-I4 above. Cases 1 and 4, and
    // both OD-4 worked examples, below. Mutation for all four: compute
    // IsUsagePartial as "Failed && CompletedBatches > 0" (the DP §3 draft
    // rule), or as "any batch's usage undetermined" (the superseded OD-9
    // wording, which the CHECK rejects).

    // Case 1: batch 1's usage unparseable (OD-2), then the invocation fails.
    [Fact]
    public async Task EmbedAsync_Od9Case1_UsageUnparseableThenFailure_FailedNullNotPartial()
    {
        var fake = TwoBatchFake();
        fake.UsageForBatch = _ => null;
        fake.ExceptionToThrow = new VoyageEmbeddingException("x");
        fake.FailAtBatch = 1;
        var (invoker, recorder) = Build(fake);

        await Assert.ThrowsAsync<VoyageEmbeddingException>(() => invoker.EmbedAsync(Context(), FourTexts, VoyageInputType.Document));

        var record = Assert.Single(recorder.Records);
        Assert.Equal(AiOutcomes.Failed, record.Outcome);
        Assert.Null(record.InputTokens);
        Assert.False(record.IsUsagePartial);
    }

    // Case 4: every batch 2xx, one batch's usage unparseable → Succeeded, null.
    [Fact]
    public async Task EmbedAsync_Od9Case4_AllBatchesSucceedOneUsageUnparseable_SucceededNullNotPartial()
    {
        var fake = TwoBatchFake();
        fake.UsageForBatch = batch => batch[0] == "t0" ? 30 : null;
        var (invoker, recorder) = Build(fake);

        await invoker.EmbedAsync(Context(), FourTexts, VoyageInputType.Document);

        var record = Assert.Single(recorder.Records);
        Assert.Equal(AiOutcomes.Succeeded, record.Outcome);
        Assert.Null(record.InputTokens); // OD-2: not 30
        Assert.False(record.IsUsagePartial);
    }

    // OD-4 worked example, single batch: usage captured, then a Nivora-side
    // check fails → Failed, known total, NOT partial (no requested batch
    // failed to contribute). Simulated as a failure after every batch has
    // been added to the accumulator.
    [Fact]
    public async Task EmbedAsync_Od4SingleBatch_UsageCapturedThenValidationFails_FailedKnownTotalNotPartial()
    {
        var fake = new FakeVoyageEmbeddingClient
        {
            UsageForBatch = _ => 42,
            ExceptionToThrow = new VoyageEmbeddingException("Voyage returned a duplicate embedding index 0."),
            FailAtBatch = 1, // after the only batch contributed
        };
        var (invoker, recorder) = Build(fake);

        await Assert.ThrowsAsync<VoyageEmbeddingException>(() => invoker.EmbedAsync(Context(), ["a", "b"], VoyageInputType.Query));

        var record = Assert.Single(recorder.Records);
        Assert.Equal(AiOutcomes.Failed, record.Outcome);
        Assert.Equal(42, record.InputTokens);
        Assert.False(record.IsUsagePartial);
    }

    // OD-4 multi-batch counterexample: batch 1 contributes, then fails
    // validation, batch 2 never attempted → Failed, known total, partial.
    [Fact]
    public async Task EmbedAsync_Od4MultiBatch_FirstBatchContributesThenFails_FailedKnownTotalPartial()
    {
        var fake = TwoBatchFake();
        fake.ExceptionToThrow = new VoyageEmbeddingException("Voyage returned a duplicate embedding index 0.");
        fake.FailAtBatch = 1;
        var (invoker, recorder) = Build(fake);

        await Assert.ThrowsAsync<VoyageEmbeddingException>(() => invoker.EmbedAsync(Context(), FourTexts, VoyageInputType.Document));

        var record = Assert.Single(recorder.Records);
        Assert.Equal(30, record.InputTokens);
        Assert.True(record.IsUsagePartial);
    }

    // ── T-I7 (DB-backed, real recorder) ─────────────────────────────────
    // Every row shape the invoker produces actually inserts, passing the
    // IsUsagePartial CHECK (v11 §3:133) and the Voyage-nulls CHECK (E1-4):
    // T-I1's success row, T-I3's partial row, and the remaining OD-9
    // validation/worked-example shapes. Mutation: set a cache field on a
    // Voyage row — the CHECK rejects it, the recorder fails open, 0 rows.

    public static TheoryData<string, int, int?, bool, int, long?, bool> PersistedShapes() => new()
    {
        // name, batchSize, failAtBatch (null = success), batch-0 usage null?, texts, expected InputTokens, expected IsUsagePartial
        { "T-I1 success", 2, null, false, 4, 42, false },
        { "T-I3 / OD-9 case 3 partial", 2, 1, false, 4, 30, true },
        { "OD-9 case 1", 2, 1, true, 4, null, false },
        { "OD-9 case 2", 2, 0, false, 4, null, false },
        { "OD-9 case 4", 2, null, true, 4, null, false },
        { "OD-4 single batch", 2, 1, false, 2, 30, false },
    };

    [Theory]
    [MemberData(nameof(PersistedShapes))]
    public async Task EmbedAsync_RealRecorder_EveryProducedShapePersistsThroughShippedConstraints(
        string name, int batchSize, int? failAtBatch, bool firstBatchUsageNull, int textCount, long? expectedTokens, bool expectedPartial)
    {
        var businessId = await SeedBusinessAsync($"Voyage Invoker {name}");
        var logger = new UnauthorizedActionAuditWriterTests.EventCapturingLogger<AiInvocationRecorder>();
        var recorder = new AiInvocationRecorder(_provider.GetRequiredService<IServiceScopeFactory>(), logger);
        var fake = new FakeVoyageEmbeddingClient
        {
            BatchSize = batchSize,
            UsageForBatch = batch => batch[0] == "t0" ? (firstBatchUsageNull ? null : 30) : 12,
        };
        if (failAtBatch is not null)
        {
            fake.ExceptionToThrow = new VoyageEmbeddingException("x");
            fake.FailAtBatch = failAtBatch.Value;
        }
        var invoker = new VoyageEmbeddingInvoker(fake, recorder, NullLogger<VoyageEmbeddingInvoker>.Instance);
        var ctx = new AiCallContext(businessId, AiOperations.KnowledgeIndexing, Guid.NewGuid(), 0);

        var ex = await Record.ExceptionAsync(() => invoker.EmbedAsync(ctx, FourTexts.Take(textCount).ToList(), VoyageInputType.Document));
        Assert.Equal(failAtBatch is not null, ex is not null);

        Assert.Empty(logger.Entries); // no fail-open diagnostic: the insert was accepted
        var row = await FindRowAsync(ctx.CorrelationId);
        Assert.NotNull(row);
        Assert.Equal(AiProviders.Voyage, row!.Provider);
        Assert.Equal(VoyageModels.Voyage3, row.Model);
        Assert.Equal(failAtBatch is null ? AiOutcomes.Succeeded : AiOutcomes.Failed, row.Outcome);
        Assert.Equal(expectedTokens, row.InputTokens);
        Assert.Equal(expectedPartial, row.IsUsagePartial);
        Assert.Equal(textCount, row.InputItemCount);
        Assert.Null(row.OutputTokens);
        Assert.Null(row.CacheCreationInputTokens);
        Assert.Null(row.CacheReadInputTokens);
    }

    // ── R2 (owner ruling): a 2xx batch whose usage is unusable or whose body
    //    can't be decoded, AFTER earlier batches completed with parsed usage,
    //    makes the whole row's usage null (OD-2) — never the earlier
    //    batches' sum, never IsUsagePartial=true. Real VoyageEmbeddingClient
    //    over a stub handler (2005 texts = 3 batches), real recorder, dev DB.

    private sealed class ThreeBatchHandler(Func<int, int, HttpResponseMessage> thirdBatch) : HttpMessageHandler
    {
        private int _calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var doc = JsonDocument.Parse(request.Content!.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult());
            var count = doc.RootElement.GetProperty("input").GetArrayLength();
            return Task.FromResult(_calls++ switch
            {
                0 => Ok(count, """{"total_tokens":40}"""),
                1 => Ok(count, """{"total_tokens":2}"""),
                _ => thirdBatch(count, _calls),
            });
        }

        public static string Data(int count) => string.Join(",", Enumerable.Range(0, count)
            .Select(i => $$$"""{"embedding":[{{{i}}}],"index":{{{i}}}}"""));

        private static HttpResponseMessage Ok(int count, string usageJson) => new(HttpStatusCode.OK)
        {
            Content = new StringContent($$$"""{"data":[{{{Data(count)}}}],"usage":{{{usageJson}}}}""", Encoding.UTF8, "application/json"),
        };
    }

    private async Task<(Exception? Thrown, AiProviderInvocation? Row, int LogEntries)> RunThreeBatchAsync(
        string name, Func<int, int, HttpResponseMessage> thirdBatch)
    {
        var businessId = await SeedBusinessAsync($"Voyage R2 {name}");
        var logger = new UnauthorizedActionAuditWriterTests.EventCapturingLogger<AiInvocationRecorder>();
        var recorder = new AiInvocationRecorder(_provider.GetRequiredService<IServiceScopeFactory>(), logger);
        var client = new VoyageEmbeddingClient(new HttpClient(new ThreeBatchHandler(thirdBatch)) { BaseAddress = new Uri("https://api.voyageai.com/") });
        var invoker = new VoyageEmbeddingInvoker(client, recorder, NullLogger<VoyageEmbeddingInvoker>.Instance);
        var ctx = new AiCallContext(businessId, AiOperations.KnowledgeIndexing, Guid.NewGuid(), 0);

        var thrown = await Record.ExceptionAsync(() => invoker.EmbedAsync(
            ctx, Enumerable.Range(0, 2005).Select(i => $"t{i}").ToList(), VoyageInputType.Document));

        return (thrown, await FindRowAsync(ctx.CorrelationId), logger.Entries.Count);
    }

    // C3a — R2 exact scenario: batch 3 is 2xx with unusable usage (negative
    // total_tokens), then fails client-side validation (duplicate index).
    // Correct before the C1/C2 correction pass (by trace, VoyageEmbeddingInvoker's
    // failure path reads the accumulator's null total). Mutation: the
    // accumulator does not null the total on unusable usage (a 42 partial
    // sum and IsUsagePartial=true would be persisted).
    [Fact]
    public async Task EmbedAsync_RealClient_Batch3TwoXxUnusableUsageThenFails_FailedNullUsageNotPartial()
    {
        var (thrown, row, logEntries) = await RunThreeBatchAsync("unusable usage", (_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"data":[{"embedding":[1],"index":0},{"embedding":[2],"index":0}],"usage":{"total_tokens":-1}}""",
                Encoding.UTF8, "application/json"),
        });

        Assert.IsType<VoyageEmbeddingException>(thrown);
        Assert.Equal(0, logEntries);
        Assert.NotNull(row);
        Assert.Equal(AiOutcomes.Failed, row!.Outcome);
        Assert.Null(row.InputTokens); // not 42
        Assert.False(row.IsUsagePartial);
        Assert.Equal(2005, row.InputItemCount); // OD-5: all three batches' items
    }

    // C3b — C1 ruling: batch 3 is 2xx but its body can't be decoded (invalid
    // charset), so ReadFromJsonAsync throws InvalidOperationException. OD-2
    // governs exactly as for an unparseable body; the caller still observes
    // the same InvalidOperationException as before.
    [Fact]
    public async Task EmbedAsync_RealClient_Batch3TwoXxUndecodableBody_FailedNullUsageNotPartial_SameExceptionType()
    {
        var (thrown, row, logEntries) = await RunThreeBatchAsync("undecodable body", (count, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(
                    $$$"""{"data":[{{{ThreeBatchHandler.Data(count)}}}],"usage":{"total_tokens":5}}""")),
            };
            response.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json; charset=bogus-charset");
            return response;
        });

        Assert.IsType<InvalidOperationException>(thrown);
        Assert.Equal(0, logEntries);
        Assert.NotNull(row);
        Assert.Equal(AiOutcomes.Failed, row!.Outcome);
        Assert.Null(row.InputTokens); // not 42
        Assert.False(row.IsUsagePartial);
        Assert.Equal(2005, row.InputItemCount);
    }

    // C4a — owner ruling C4: a body-read that fails because the response
    // content was disposed means the body was never received — a transport
    // failure, not an unusable 2xx. The batch "did not complete" (OD-9), so
    // OD-2 does NOT apply: batches 1+2's sum is kept and IsUsagePartial=true.
    // The content serializes normally (so HttpClient's own buffering
    // succeeds) and then disposes itself, so the client's ReadFromJsonAsync
    // hits ObjectDisposedException.
    private sealed class DisposedAfterBufferingContent(byte[] body) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        {
            await stream.WriteAsync(body);
            Dispose();
        }

        protected override bool TryComputeLength(out long length)
        {
            length = body.Length;
            return true;
        }
    }

    [Fact]
    public async Task EmbedAsync_RealClient_Batch3BodyReadObjectDisposed_FailedPartialSumKept_Partial()
    {
        var (thrown, row, logEntries) = await RunThreeBatchAsync("disposed body", (count, _) =>
        {
            var content = new DisposedAfterBufferingContent(Encoding.UTF8.GetBytes(
                $$$"""{"data":[{{{ThreeBatchHandler.Data(count)}}}],"usage":{"total_tokens":5}}"""));
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });

        Assert.IsType<ObjectDisposedException>(thrown);
        Assert.Equal(0, logEntries);
        Assert.NotNull(row);
        Assert.Equal(AiOutcomes.Failed, row!.Outcome);
        Assert.Equal(42, row.InputTokens); // batches 1+2 (40 + 2)
        Assert.True(row.IsUsagePartial);
        Assert.Equal(2005, row.InputItemCount);
    }

    // ── Constraint discipline (Slice 3 prompt §5) ───────────────────────
    // Directly hands the real recorder an IsUsagePartial combination no
    // production path generates (built here, never by production code),
    // and verifies (a) the shipped CK_AiProviderInvocations_IsUsagePartial_Implication
    // (20260924141056_AddAiProviderInvocation.cs:41) is what rejects it, and
    // (b) the recorder's intentional fail-open (Decision 1) is observable:
    // no row, no throw, one Decision 1 diagnostic carrying the Postgres
    // check violation. Complements Slice 1's raw-SQL Test 14
    // (AiProviderInvocationPersistenceTests.cs:255/:274/:293), which never
    // goes through the recorder.

    public static TheoryData<string, string, string, long?, string?> InvalidPartialShapes() => new()
    {
        // name, provider, outcome, inputTokens, errorCategory
        { "partial on Succeeded", AiProviders.Voyage, AiOutcomes.Succeeded, 30, null },
        { "partial on Anthropic", AiProviders.Anthropic, AiOutcomes.Failed, 30, AiErrorCategories.ServerError },
        { "partial with null tokens (OD-2 null)", AiProviders.Voyage, AiOutcomes.Failed, null, AiErrorCategories.Unknown },
    };

    [Theory]
    [MemberData(nameof(InvalidPartialShapes))]
    public async Task Recorder_InvalidIsUsagePartialCombination_RejectedByShippedCheck_FailOpenIsObservable(
        string name, string provider, string outcome, long? inputTokens, string? errorCategory)
    {
        var businessId = await SeedBusinessAsync($"Invalid Partial {name}");
        var logger = new UnauthorizedActionAuditWriterTests.EventCapturingLogger<AiInvocationRecorder>();
        var recorder = new AiInvocationRecorder(_provider.GetRequiredService<IServiceScopeFactory>(), logger);
        var request = new AiInvocationRecordRequest
        {
            BusinessId = businessId,
            Provider = provider,
            Model = provider == AiProviders.Voyage ? VoyageModels.Voyage3 : AnthropicModels.Default,
            Operation = AiOperations.KnowledgeIndexing,
            CorrelationId = Guid.NewGuid(),
            Sequence = 0,
            InputTokens = inputTokens,
            Outcome = outcome,
            ErrorCategory = errorCategory,
            IsUsagePartial = true,
        };

        var ex = await Record.ExceptionAsync(() => recorder.RecordAsync(request));

        Assert.Null(ex); // fail-open, per Slice 2 / Decision 1
        Assert.Null(await FindRowAsync(request.CorrelationId));
        var entry = Assert.Single(logger.Entries);
        var postgres = Assert.IsType<PostgresException>(Assert.IsType<DbUpdateException>(entry.Exception).InnerException);
        Assert.Equal("23514", postgres.SqlState); // check_violation
        Assert.Equal("CK_AiProviderInvocations_IsUsagePartial_Implication", postgres.ConstraintName);
    }

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
}
