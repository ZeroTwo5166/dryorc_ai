namespace backend.Constants;

/// <summary>
/// Internal timeout shared by the isolated writers — AiInvocationRecorder and
/// UnauthorizedActionAuditWriter (A1 design
/// docs/handoffs/billing-usage-a1-invocation-capture-design-v11.md §5). The
/// 3-second value, and its use by the audit writer as well as the recorder,
/// are post-v11 owner decisions (Slice 2), pending a v11 errata.
/// </summary>
public static class IsolatedWriteLimits
{
    /// <summary>
    /// The countdown starts when each writer creates its
    /// <c>CancellationTokenSource</c> — in <c>AiInvocationRecorder</c>, before
    /// its in-memory <c>Add(...)</c>; in <c>UnauthorizedActionAuditWriter</c>,
    /// after <c>IAuditLogService.LogAsync</c>'s own in-memory add — and covers
    /// the isolated write's <c>SaveChangesAsync</c> call, including connection
    /// open and any lock wait on the insert. It does NOT
    /// bound DI scope creation, service resolution, scope disposal, or
    /// Npgsql's own post-cancellation work (the cancel request and its
    /// CancellationTimeout). Not an end-to-end guarantee on the writer's total
    /// duration.
    /// </summary>
    public static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(3);
}
