namespace backend.Services;

/// <summary>
/// Everything needed to persist one <see cref="Models.AiProviderInvocation"/>
/// row, minus the server-generated <c>Id</c>/<c>CreatedAt</c>. A Slice 2-owned
/// shape, not the eventual <c>AiCallContext</c> the A1 design (v11 §4) assigns
/// to the wrapper layer — that type, and the wrapper that would construct one
/// of these from it, are Slice 3/4 scope. See
/// docs/handoffs/billing-usage-a1-invocation-capture-design-v11.md §3 for the
/// authoritative field-by-field contract this mirrors.
/// </summary>
public sealed record AiInvocationRecordRequest
{
    public required Guid BusinessId { get; init; }
    public required string Provider { get; init; }
    public required string Model { get; init; }
    public required string Operation { get; init; }
    public required Guid CorrelationId { get; init; }
    public required int Sequence { get; init; }
    public Guid? ConversationId { get; init; }
    public long? InputTokens { get; init; }
    public long? OutputTokens { get; init; }
    public long? CacheCreationInputTokens { get; init; }
    public long? CacheReadInputTokens { get; init; }
    public int? InputItemCount { get; init; }
    public required string Outcome { get; init; }
    public bool IsUsagePartial { get; init; }
    public string? ErrorCategory { get; init; }
}

/// <summary>
/// Persists one <see cref="Models.AiProviderInvocation"/> row, isolated from
/// the caller's own unit of work (A1 design v11 §5, "Isolation") and never
/// using the caller's <see cref="CancellationToken"/> (v11 §5, "Cancellation").
/// Fails open on any failure (v11 §5, Decision 1) — <see cref="RecordAsync"/>
/// never throws.
/// </summary>
public interface IAiInvocationRecorder
{
    Task RecordAsync(AiInvocationRecordRequest request);
}
