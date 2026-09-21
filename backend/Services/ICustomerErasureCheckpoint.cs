namespace backend.Services;

/// <summary>
/// Test-only seam for deterministically pausing (or, in a test double,
/// failing) CustomerErasureService.EraseCustomerAsync at a named checkpoint —
/// same register-a-fake-in-a-fresh-ServiceCollection pattern
/// IPurgeFailurePoint already established, but for pausing mid-flight
/// rather than throwing. Production always uses
/// NoOpCustomerErasureCheckpoint; tests substitute a fake that blocks until
/// released (mirroring BlockingEmailSender's SendStarted/Release shape) or
/// throws at a named checkpoint (mirroring IPurgeFailurePoint's own
/// FailAtCheckpoint idiom).
/// </summary>
public interface ICustomerErasureCheckpoint
{
    /// <summary>Called at each named checkpoint inside EraseCustomerAsync. A no-op in production.</summary>
    Task ReachedAsync(string checkpoint, CancellationToken cancellationToken);
}

public class NoOpCustomerErasureCheckpoint : ICustomerErasureCheckpoint
{
    public Task ReachedAsync(string checkpoint, CancellationToken cancellationToken) => Task.CompletedTask;
}
