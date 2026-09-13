using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace backend.Tests;

/// <summary>
/// Step 22 — a genuine, non-invasive runtime observation seam: an EF Core
/// command interceptor that captures the actual numeric key value sent to
/// Postgres in every "SELECT pg_advisory_xact_lock(@p)" command issued by
/// the DbContext it's attached to. Zero production code changes — wired in
/// only via a test-specific DbContextOptions (AddInterceptors), never
/// through the shared fixture or Program.cs. Lets a test prove, from the
/// real SQL parameter value that actually reached the database, that two
/// different call paths compute the identical lock key for the same
/// (BusinessId, ConversationId) pair — rather than trusting they share a
/// helper because the source reads that way. Also usable as a deterministic
/// synchronization point: OnBeforeLockAcquisition fires synchronously,
/// driven by real command execution, immediately before the command is
/// sent — not a timer.
/// </summary>
public class LockAcquisitionInterceptor : DbCommandInterceptor
{
    private readonly object _gate = new();
    private readonly List<long> _observedLockKeys = [];

    public IReadOnlyList<long> ObservedLockKeys
    {
        get { lock (_gate) { return _observedLockKeys.ToArray(); } }
    }

    public Action<long>? OnBeforeLockAcquisition { get; set; }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        TryObserve(command);
        return base.NonQueryExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        TryObserve(command);
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        TryObserve(command);
        return base.ReaderExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        TryObserve(command);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    private void TryObserve(DbCommand command)
    {
        if (!command.CommandText.Contains("pg_advisory_xact_lock", StringComparison.Ordinal) || command.Parameters.Count == 0)
        {
            return;
        }

        var keyValue = Convert.ToInt64(command.Parameters[0]!.Value);
        lock (_gate)
        {
            _observedLockKeys.Add(keyValue);
        }

        OnBeforeLockAcquisition?.Invoke(keyValue);
    }
}
