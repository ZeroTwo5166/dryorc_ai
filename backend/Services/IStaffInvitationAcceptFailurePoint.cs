namespace backend.Services;

/// <summary>
/// Test-only seam for deterministically failing
/// StaffInvitationService.AcceptAsync after the new user and its role exist
/// but before the invitation row is deleted and the transaction commits
/// (staff-invite design ED-35, test T-I1b), to prove the whole acceptance
/// rolls back. Same idea as IPurgeFailurePoint: a real DI-registered no-op in
/// production; tests substitute a fake that throws here.
/// </summary>
public interface IStaffInvitationAcceptFailurePoint
{
    /// <summary>Called once inside AcceptAsync, after AddToRoleAsync. A no-op in production.</summary>
    Task AfterUserCreatedAsync(CancellationToken ct);
}

public class NoOpStaffInvitationAcceptFailurePoint : IStaffInvitationAcceptFailurePoint
{
    public Task AfterUserCreatedAsync(CancellationToken ct) => Task.CompletedTask;
}
