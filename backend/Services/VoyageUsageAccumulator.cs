namespace backend.Services;

/// <summary>
/// Per-invocation running usage total for one Voyage embedding call, passed
/// in by the wrapper (<see cref="IVoyageEmbeddingInvoker"/>) so the total is
/// still readable in the wrapper's recording path when the client throws —
/// including on cancellation and timeout (A1 design v11 §2:97; Slice 3
/// decisions addendum Q1, docs/handoffs/billing-usage-a1-slice3-decisions-addendum.md).
/// Deliberately not an exception carrying the total (errata 3 §1).
///
/// One instance per invocation — never reused across calls, never
/// DI-registered.
/// </summary>
public sealed class VoyageUsageAccumulator
{
    private long _sum;
    private bool _usageUnavailable;

    /// <summary>
    /// Number of batches the client intends to dispatch for this call. Set
    /// once by the client before its first request.
    /// </summary>
    public int RequestedBatches { get; private set; }

    /// <summary>
    /// Number of batches that received a 2xx response whose body was read —
    /// counted whether or not a later Nivora-side validation check on that
    /// batch then failed (addendum OD-4), and whether or not the batch's
    /// usage was usable (OD-2).
    /// </summary>
    public int CompletedBatches { get; private set; }

    /// <summary>
    /// Sum of <c>usage.total_tokens</c> across completed batches. <c>null</c>
    /// when no batch has completed (v11 §2:79 — null, never 0), or when any
    /// completed batch's usage was missing or unusable: the whole
    /// invocation's usage is then unknown, never a sum that silently omits a
    /// batch (addendum OD-2; errata 3 §3).
    /// </summary>
    public long? TotalTokens => CompletedBatches == 0 || _usageUnavailable ? null : _sum;

    public void SetRequestedBatches(int count) => RequestedBatches = count;

    /// <summary>
    /// Records one completed batch. <paramref name="totalTokens"/> is the
    /// batch's parsed <c>usage.total_tokens</c>, or <c>null</c> when it was
    /// missing or unusable (OD-2).
    /// </summary>
    public void AddCompletedBatch(long? totalTokens)
    {
        CompletedBatches++;
        if (totalTokens is null)
        {
            _usageUnavailable = true;
        }
        else
        {
            _sum += totalTokens.Value;
        }
    }
}
