using backend.Constants;
using backend.Services;
using Xunit;

namespace backend.Tests;

/// <summary>
/// Step 27 Slice A1, Slice 4b — ConcurrentCapturingAiInvocationRecorder
/// (design §10.1 T-C1, T-C2). In-memory only, no DB.
/// </summary>
public class ConcurrentCapturingAiInvocationRecorderTests
{
    private static AiInvocationRecordRequest Request(Guid businessId, Guid correlationId, int sequence) => new()
    {
        BusinessId = businessId,
        Provider = AiProviders.Anthropic,
        Model = AnthropicModels.Default,
        Operation = AiOperations.AnalystSelection,
        CorrelationId = correlationId,
        Sequence = sequence,
        Outcome = AiOutcomes.Succeeded,
    };

    // ── T-C1 ────────────────────────────────────────────────────────────
    // Positive concurrency test only: mutation-exempt by owner ruling
    // (design §10.1) — a race mutation can't be killed deterministically.

    [Fact]
    public void RecordAsync_ConcurrentAcrossBusinessesAndCorrelations_ExactCountsPerBusinessAndCorrelation()
    {
        const int businessCount = 4;
        const int correlationsPerBusiness = 5;
        const int recordsPerCorrelation = 50;

        var recorder = new ConcurrentCapturingAiInvocationRecorder();
        var businessIds = Enumerable.Range(0, businessCount).Select(_ => Guid.NewGuid()).ToArray();
        var correlationIds = businessIds
            .Select(_ => Enumerable.Range(0, correlationsPerBusiness).Select(_ => Guid.NewGuid()).ToArray())
            .ToArray();

        const int total = businessCount * correlationsPerBusiness * recordsPerCorrelation;
        Parallel.For(0, total, new ParallelOptions { MaxDegreeOfParallelism = 16 }, i =>
        {
            var b = i % businessCount;
            var c = i / businessCount % correlationsPerBusiness;
            var seq = i / (businessCount * correlationsPerBusiness);
            Assert.True(recorder.RecordAsync(Request(businessIds[b], correlationIds[b][c], seq)).IsCompletedSuccessfully);
        });

        for (var b = 0; b < businessCount; b++)
        {
            var forBusiness = recorder.ForBusiness(businessIds[b]);
            Assert.Equal(correlationsPerBusiness * recordsPerCorrelation, forBusiness.Count);
            Assert.All(forBusiness, r => Assert.Equal(businessIds[b], r.BusinessId));

            foreach (var correlationId in correlationIds[b])
            {
                var forCorrelation = recorder.ForCorrelation(correlationId);
                Assert.Equal(recordsPerCorrelation, forCorrelation.Count);
                Assert.All(forCorrelation, r => Assert.Equal(correlationId, r.CorrelationId));
                Assert.Equal(Enumerable.Range(0, recordsPerCorrelation), forCorrelation.Select(r => r.Sequence).Order());
            }
        }
    }

    // ── T-C2 ────────────────────────────────────────────────────────────
    // Mutations: drop the BusinessId predicate from ForBusiness; separately,
    // drop the CorrelationId predicate from ForCorrelation.

    [Fact]
    public async Task ForBusiness_ExcludesOtherBusinesses_InEnqueueOrder()
    {
        var recorder = new ConcurrentCapturingAiInvocationRecorder();
        var businessA = Guid.NewGuid();
        var businessB = Guid.NewGuid();
        var correlation = Guid.NewGuid();

        await recorder.RecordAsync(Request(businessA, correlation, 0));
        await recorder.RecordAsync(Request(businessB, Guid.NewGuid(), 0));
        await recorder.RecordAsync(Request(businessA, correlation, 1));

        var forA = recorder.ForBusiness(businessA);

        Assert.Equal([0, 1], forA.Select(r => r.Sequence));
        Assert.All(forA, r => Assert.Equal(businessA, r.BusinessId));
        Assert.Single(recorder.ForBusiness(businessB));
        Assert.Empty(recorder.ForBusiness(Guid.NewGuid()));
    }

    [Fact]
    public async Task ForCorrelation_ExcludesOtherCorrelations_InEnqueueOrder()
    {
        var recorder = new ConcurrentCapturingAiInvocationRecorder();
        var businessId = Guid.NewGuid();
        var correlationA = Guid.NewGuid();
        var correlationB = Guid.NewGuid();

        await recorder.RecordAsync(Request(businessId, correlationA, 0));
        await recorder.RecordAsync(Request(businessId, correlationB, 0));
        await recorder.RecordAsync(Request(businessId, correlationA, 1));

        var forA = recorder.ForCorrelation(correlationA);

        Assert.Equal([0, 1], forA.Select(r => r.Sequence));
        Assert.All(forA, r => Assert.Equal(correlationA, r.CorrelationId));
        Assert.Single(recorder.ForCorrelation(correlationB));
        Assert.Empty(recorder.ForCorrelation(Guid.NewGuid()));
    }
}
