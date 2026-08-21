namespace backend.Services;

/// <summary>
/// Test-only seam for deterministically failing PurgeConversationAsync
/// partway through, to prove its transaction actually rolls back —
/// registered as a real DI service (AddScoped/AddSingleton) rather than
/// literally killing the process mid-test, so the failure is reproducible
/// and doesn't require tearing down the real Neon dev connection this test
/// suite runs against. Production always uses NoOpPurgeFailurePoint; tests
/// substitute a fake that throws at a named checkpoint (same
/// register-a-fake-in-a-fresh-ServiceCollection pattern
/// AppDbContextTenantFilterTests.MutableTenantAccessor already uses).
/// </summary>
public interface IPurgeFailurePoint
{
    /// <summary>Called at each named checkpoint inside PurgeConversationAsync. A no-op in production.</summary>
    void MaybeFail(string checkpoint);
}

public class NoOpPurgeFailurePoint : IPurgeFailurePoint
{
    public void MaybeFail(string checkpoint) { }
}
