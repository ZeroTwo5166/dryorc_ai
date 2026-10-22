namespace backend.Services;

/// <summary>The three StaffInvitationService operations that take a row lock.</summary>
public enum StaffInvitationLockOperation
{
    Accept,
    Resend,
    Revoke,
}

/// <summary>
/// Test-only seam for deterministically pausing an invitation operation
/// while it holds its row lock (staff-invite design ED-35, tests T-I5c,
/// T-I5d, T-I5f, T-I5e). AcceptAsync, ResendAsync and RevokeAsync call it
/// immediately after their SELECT ... FOR UPDATE. Same idea as
/// ICustomerErasureCheckpoint: a real DI-registered no-op in production;
/// tests substitute a fake that blocks until released.
/// </summary>
public interface IStaffInvitationLockCheckpoint
{
    /// <summary>Called right after the operation's row lock. A no-op in production.</summary>
    Task AfterRowLockAsync(StaffInvitationLockOperation op, CancellationToken ct);
}

public class NoOpStaffInvitationLockCheckpoint : IStaffInvitationLockCheckpoint
{
    public Task AfterRowLockAsync(StaffInvitationLockOperation op, CancellationToken ct) => Task.CompletedTask;
}
