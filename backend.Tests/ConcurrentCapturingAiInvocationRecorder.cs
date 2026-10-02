using System.Collections.Concurrent;
using backend.Services;

namespace backend.Tests;

/// <summary>
/// Thread-safe, non-persisting IAiInvocationRecorder for A1 Slice 4b
/// (design §7.1-§7.2): NivoraApiFactory registers one instance per factory
/// as a singleton, so concurrent requests within one host record into the
/// same store. Kept separate from the Slice 3 CapturingAiInvocationRecorder
/// (§13 #7). No Clear(): tests isolate by querying their own seeded
/// BusinessId or CorrelationId.
/// </summary>
public sealed class ConcurrentCapturingAiInvocationRecorder : IAiInvocationRecorder
{
    private readonly ConcurrentQueue<AiInvocationRecordRequest> _queue = new();

    public Task RecordAsync(AiInvocationRecordRequest request)
    {
        _queue.Enqueue(request);
        return Task.CompletedTask;
    }

    /// <summary>Snapshot, in enqueue order, of every request for this business.</summary>
    public IReadOnlyList<AiInvocationRecordRequest> ForBusiness(Guid businessId) =>
        _queue.Where(r => r.BusinessId == businessId).ToList();

    /// <summary>Snapshot, in enqueue order, of every request for this correlation.</summary>
    public IReadOnlyList<AiInvocationRecordRequest> ForCorrelation(Guid correlationId) =>
        _queue.Where(r => r.CorrelationId == correlationId).ToList();
}
