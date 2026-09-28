using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using backend.Enums;
using backend.Services;
using Xunit;

namespace backend.Tests;

/// <summary>
/// Direct unit tests for VoyageEmbeddingClient against a stubbed
/// HttpMessageHandler — specifically covers the index-based
/// embedding-to-input mapping (Voyage's docs give no guarantee that response
/// ordering matches request ordering) and the "never send output_dimension"
/// requirement (voyage-3 doesn't support it). No real HTTP calls, no DB.
/// </summary>
public class VoyageEmbeddingClientTests
{
    private class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private static VoyageEmbeddingClient BuildClient(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new HttpClient(new StubHandler(respond)) { BaseAddress = new Uri("https://api.voyageai.com/") });

    private static HttpResponseMessage JsonResponse(object body) =>
        new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };

    [Fact]
    public async Task EmbedAsync_MapsEmbeddingsByReturnedIndex_NotResponseArrayPosition()
    {
        var client = BuildClient(_ => JsonResponse(new
        {
            data = new[]
            {
                new { embedding = new[] { 2f }, index = 1 }, // scrambled order: index 1 returned first
                new { embedding = new[] { 1f }, index = 0 },
            },
        }));

        var result = await client.EmbedAsync(["first", "second"], VoyageInputType.Document);

        Assert.Equal(1f, result[0][0]); // texts[0] ("first") maps to index 0's embedding, not array position 0
        Assert.Equal(2f, result[1][0]);
    }

    [Fact]
    public async Task EmbedAsync_DuplicateIndex_ThrowsRatherThanMisassign()
    {
        var client = BuildClient(_ => JsonResponse(new
        {
            data = new[]
            {
                new { embedding = new[] { 1f }, index = 0 },
                new { embedding = new[] { 2f }, index = 0 },
            },
        }));

        await Assert.ThrowsAsync<VoyageEmbeddingException>(() => client.EmbedAsync(["a", "b"], VoyageInputType.Document));
    }

    [Fact]
    public async Task EmbedAsync_OutOfRangeIndex_ThrowsRatherThanMisassign()
    {
        var client = BuildClient(_ => JsonResponse(new
        {
            data = new[]
            {
                new { embedding = new[] { 1f }, index = 0 },
                new { embedding = new[] { 2f }, index = 5 },
            },
        }));

        await Assert.ThrowsAsync<VoyageEmbeddingException>(() => client.EmbedAsync(["a", "b"], VoyageInputType.Document));
    }

    [Fact]
    public async Task EmbedAsync_MissingIndex_ThrowsRatherThanReturnIncompleteResult()
    {
        var client = BuildClient(_ => JsonResponse(new
        {
            data = new[]
            {
                new { embedding = new[] { 1f }, index = 0 },
                // index 1 never returned
            },
        }));

        await Assert.ThrowsAsync<VoyageEmbeddingException>(() => client.EmbedAsync(["a", "b"], VoyageInputType.Document));
    }

    [Fact]
    public async Task EmbedAsync_NonSuccessStatus_ThrowsVoyageEmbeddingException()
    {
        var client = BuildClient(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("rate limited"),
        });

        await Assert.ThrowsAsync<VoyageEmbeddingException>(() => client.EmbedAsync(["a"], VoyageInputType.Document));
    }

    [Fact]
    public async Task EmbedAsync_NeverSendsOutputDimension_SendsModelAndInputType()
    {
        string? capturedBody = null;
        var client = BuildClient(request =>
        {
            capturedBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse(new { data = new[] { new { embedding = new[] { 1f }, index = 0 } } });
        });

        await client.EmbedAsync(["a"], VoyageInputType.Document);

        Assert.NotNull(capturedBody);
        Assert.DoesNotContain("output_dimension", capturedBody);
        Assert.Contains("\"model\":\"voyage-3\"", capturedBody);
        Assert.Contains("\"input_type\":\"document\"", capturedBody);
    }

    [Fact]
    public async Task EmbedAsync_QueryInputType_SendsQueryNotDocument()
    {
        string? capturedBody = null;
        var client = BuildClient(request =>
        {
            capturedBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse(new { data = new[] { new { embedding = new[] { 1f }, index = 0 } } });
        });

        await client.EmbedAsync(["a"], VoyageInputType.Query);

        Assert.Contains("\"input_type\":\"query\"", capturedBody);
    }

    [Fact]
    public async Task EmbedAsync_EmptyInput_ReturnsEmptyWithoutCallingVoyage()
    {
        var called = false;
        var client = BuildClient(_ =>
        {
            called = true;
            return JsonResponse(new { data = Array.Empty<object>() });
        });

        var result = await client.EmbedAsync([], VoyageInputType.Document);

        Assert.Empty(result);
        Assert.False(called);
    }

    // ── A1 Slice 3: usage accumulation (EmbedWithUsageAsync) ─────────────
    // Design: docs/handoffs/billing-usage-a1-slice3-design-pass.md §7
    // (T-V1..T-V8); locked decisions in
    // docs/handoffs/billing-usage-a1-slice3-decisions-addendum.md. T-V8 is
    // the 8 tests above, which keep calling the unchanged EmbedAsync (now
    // delegating to EmbedWithUsageAsync with a discarded accumulator) with
    // usage-less stub JSON — valid under OD-2 option (i).

    private class AsyncStubHandler(Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        private int _calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request, _calls++, cancellationToken);
    }

    private static VoyageEmbeddingClient BuildAsyncClient(Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> respond) =>
        new(new HttpClient(new AsyncStubHandler(respond)) { BaseAddress = new Uri("https://api.voyageai.com/") });

    private static int RequestInputCount(HttpRequestMessage request)
    {
        using var doc = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
        return doc.RootElement.GetProperty("input").GetArrayLength();
    }

    // A 2xx body with one embedding per input (embedding value = in-batch
    // index + valueOffset) and, unless usageJson is null, a raw "usage"
    // member spliced in verbatim.
    private static HttpResponseMessage BatchResponse(int count, string? usageJson, int valueOffset = 0)
    {
        var data = string.Join(",", Enumerable.Range(0, count)
            .Select(i => $$"""{"embedding":[{{i + valueOffset}}],"index":{{i}}}"""));
        var body = usageJson is null
            ? $$"""{"data":[{{data}}]}"""
            : $$"""{"data":[{{data}}],"usage":{{usageJson}}}""";
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private static HttpResponseMessage ServerError() =>
        new(HttpStatusCode.InternalServerError) { Content = new StringContent("boom") };

    private static List<string> Texts(int count) => Enumerable.Range(0, count).Select(i => $"t{i}").ToList();

    // T-V1. Mutations: don't parse usage; hardcode 0.
    [Fact]
    public async Task EmbedWithUsageAsync_OneBatch_AccumulatesReportedTotalTokens()
    {
        var client = BuildAsyncClient((req, _, _) => Task.FromResult(BatchResponse(RequestInputCount(req), """{"total_tokens":42}""")));
        var usage = new VoyageUsageAccumulator();

        await client.EmbedWithUsageAsync(["a", "b"], VoyageInputType.Document, usage);

        Assert.Equal(42, usage.TotalTokens);
        Assert.Equal(1, usage.CompletedBatches);
        Assert.Equal(1, usage.RequestedBatches);
    }

    // T-V2. Mutations: overwrite instead of add; count only the last batch.
    [Fact]
    public async Task EmbedWithUsageAsync_TwoBatches_SumsBothBatches_EmbeddingsUnchanged()
    {
        var client = BuildAsyncClient((req, call, _) => Task.FromResult(call == 0
            ? BatchResponse(RequestInputCount(req), """{"total_tokens":40}""", valueOffset: 0)
            : BatchResponse(RequestInputCount(req), """{"total_tokens":2}""", valueOffset: 1000)));
        var usage = new VoyageUsageAccumulator();

        var result = await client.EmbedWithUsageAsync(Texts(1005), VoyageInputType.Document, usage);

        Assert.Equal(42, usage.TotalTokens);
        Assert.Equal(2, usage.CompletedBatches);
        Assert.Equal(2, usage.RequestedBatches);
        Assert.Equal(1005, result.Count);
        for (var i = 0; i < result.Count; i++)
        {
            Assert.Equal(i, result[i][0]); // texts[i] -> its own embedding, across the batch boundary
        }
    }

    // T-V3 (R10 partial). Mutation: add a batch's tokens only once the whole
    // loop has finished.
    [Fact]
    public async Task EmbedWithUsageAsync_SecondBatchServerError_Throws_AccumulatorHoldsFirstBatch()
    {
        var client = BuildAsyncClient((req, call, _) => Task.FromResult(call == 0
            ? BatchResponse(RequestInputCount(req), """{"total_tokens":40}""")
            : ServerError()));
        var usage = new VoyageUsageAccumulator();

        await Assert.ThrowsAsync<VoyageEmbeddingException>(
            () => client.EmbedWithUsageAsync(Texts(1005), VoyageInputType.Document, usage));

        Assert.Equal(40, usage.TotalTokens);
        Assert.Equal(1, usage.CompletedBatches);
        Assert.Equal(2, usage.RequestedBatches);
    }

    // T-V4 (R11 placement), caller-cancellation variant. The handler cancels
    // the caller's CTS during batch 2's POST and then THROWS (a returning
    // handler could let the mutation survive). Mutation: add batch 1's
    // tokens only after batch 2's POST returns.
    [Fact]
    public async Task EmbedWithUsageAsync_CallerCancelledDuringSecondBatch_OcePropagates_AccumulatorHoldsFirstBatch()
    {
        using var cts = new CancellationTokenSource();
        var client = BuildAsyncClient((req, call, ct) =>
        {
            if (call == 0)
            {
                return Task.FromResult(BatchResponse(RequestInputCount(req), """{"total_tokens":40}"""));
            }
            cts.Cancel();
            throw new OperationCanceledException(ct);
        });
        var usage = new VoyageUsageAccumulator();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.EmbedWithUsageAsync(Texts(1005), VoyageInputType.Document, usage, cts.Token));

        Assert.Equal(40, usage.TotalTokens);
        Assert.Equal(1, usage.CompletedBatches);
    }

    // T-V4, timeout variant: TaskCanceledException with the caller's token
    // NOT cancelled -> wrapped as VoyageEmbeddingException (the client's
    // existing timeout filter). Same mutation.
    [Fact]
    public async Task EmbedWithUsageAsync_SecondBatchTimesOut_Throws_AccumulatorHoldsFirstBatch()
    {
        var client = BuildAsyncClient((req, call, _) =>
        {
            if (call == 0)
            {
                return Task.FromResult(BatchResponse(RequestInputCount(req), """{"total_tokens":40}"""));
            }
            throw new TaskCanceledException("simulated HttpClient timeout");
        });
        var usage = new VoyageUsageAccumulator();

        var ex = await Assert.ThrowsAsync<VoyageEmbeddingException>(
            () => client.EmbedWithUsageAsync(Texts(1005), VoyageInputType.Document, usage));

        Assert.IsType<TaskCanceledException>(ex.InnerException);
        Assert.Equal(40, usage.TotalTokens);
        Assert.Equal(1, usage.CompletedBatches);
    }

    // T-V5 (R8: null, never 0). Mutation: initialize the total to 0.
    [Fact]
    public async Task EmbedWithUsageAsync_FirstBatchServerError_TotalStaysNullNotZero()
    {
        var client = BuildAsyncClient((_, _, _) => Task.FromResult(ServerError()));
        var usage = new VoyageUsageAccumulator();

        await Assert.ThrowsAsync<VoyageEmbeddingException>(
            () => client.EmbedWithUsageAsync(Texts(1005), VoyageInputType.Document, usage));

        Assert.Null(usage.TotalTokens);
        Assert.Equal(0, usage.CompletedBatches);
    }

    // T-V6 (OD-2, locked option (i)): a 2xx whose usage.total_tokens is
    // missing or unusable leaves the whole invocation's usage null, and the
    // call itself still succeeds (option (iii), failing the batch, was not
    // taken — R4, caller behavior unchanged). Mutations: sum only the
    // batches that reported (option (ii)); coalesce missing usage to 0;
    // throw on missing/unusable usage.
    [Theory]
    [InlineData(null)]                         // no usage member at all
    [InlineData("null")]                       // usage: null
    [InlineData("{}")]                         // usage without total_tokens
    [InlineData("""{"total_tokens":null}""")]  // total_tokens: null
    [InlineData("""{"total_tokens":"12"}""")]  // wrong JSON type
    [InlineData("""{"total_tokens":-5}""")]    // negative — never a valid count
    [InlineData("""{"total_tokens":1.5}""")]   // non-integer
    [InlineData("7")]                          // usage not an object
    public async Task EmbedWithUsageAsync_TwoXxWithMissingOrUnusableUsage_WholeTotalNull_CallStillSucceeds(string? secondBatchUsage)
    {
        var client = BuildAsyncClient((req, call, _) => Task.FromResult(call == 0
            ? BatchResponse(RequestInputCount(req), """{"total_tokens":40}""", valueOffset: 0)
            : BatchResponse(RequestInputCount(req), secondBatchUsage, valueOffset: 1000)));
        var usage = new VoyageUsageAccumulator();

        var result = await client.EmbedWithUsageAsync(Texts(1005), VoyageInputType.Document, usage);

        Assert.Equal(1005, result.Count);
        Assert.Equal(1004, result[1004][0]);
        Assert.Null(usage.TotalTokens); // not 40: never a sum that silently omits a batch
        Assert.Equal(2, usage.CompletedBatches);
    }

    // T-V7 (OD-4, locked: included). A 2xx whose usage parsed, followed by a
    // failed Nivora-side validation check (duplicate index), still counts as
    // completed for usage accounting. Mutation: add the batch's usage only
    // after validation passes.
    [Fact]
    public async Task EmbedWithUsageAsync_TwoXxWithUsage_ThenDuplicateIndex_Throws_UsageStillCounted()
    {
        var client = BuildAsyncClient((_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"data":[{"embedding":[1],"index":0},{"embedding":[2],"index":0}],"usage":{"total_tokens":42}}""",
                Encoding.UTF8, "application/json"),
        }));
        var usage = new VoyageUsageAccumulator();

        await Assert.ThrowsAsync<VoyageEmbeddingException>(
            () => client.EmbedWithUsageAsync(["a", "b"], VoyageInputType.Document, usage));

        Assert.Equal(42, usage.TotalTokens);
        Assert.Equal(1, usage.CompletedBatches);
    }

    // OD-4's boundary with OD-2 (addendum item 3): a 2xx whose body can't be
    // parsed never yielded usage.total_tokens, so OD-2 governs — the whole
    // total is null (here, even though batch 1 reported 40). Mutation:
    // treat an unparseable 2xx as "not completed", which leaves a 40 total.
    [Fact]
    public async Task EmbedWithUsageAsync_TwoXxUnparseableBody_Throws_WholeTotalNull()
    {
        var client = BuildAsyncClient((req, call, _) => Task.FromResult(call == 0
            ? BatchResponse(RequestInputCount(req), """{"total_tokens":40}""")
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{not json", Encoding.UTF8, "application/json") }));
        var usage = new VoyageUsageAccumulator();

        await Assert.ThrowsAsync<VoyageEmbeddingException>(
            () => client.EmbedWithUsageAsync(Texts(1005), VoyageInputType.Document, usage));

        Assert.Null(usage.TotalTokens);
        Assert.Equal(2, usage.CompletedBatches);
    }
}
