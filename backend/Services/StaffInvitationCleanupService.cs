using backend.Data;
using Microsoft.EntityFrameworkCore;

namespace backend.Services;

public interface IStaffInvitationCleanupService
{
    /// <summary>Deletes StaffInvitation rows whose ExpiresAt is at least 30
    /// days past, across every business. Returns how many were removed.</summary>
    Task<int> DeleteExpiredAsync(CancellationToken ct = default);
}

/// <summary>
/// Staff invite retention (DEC D9, I8; staff-invite design Section 12): the
/// sixth job DataLifecycleBackgroundService runs each tick. A row is deleted
/// once its current ExpiresAt is at least 30 days past; the boundary row is
/// deleted too (<c>&lt;=</c>, unlike VerificationCodeCleanupService's strict
/// <c>&lt;</c>). It runs whatever the release switch says, so rows created
/// while the switch was on still age out.
///
/// The second and last IgnoreQueryFilters site for invitations (DEC D15
/// site 2, as amended by E1; I2). A background job has no tenant, and the
/// tenant filter is fail-closed, so it would match no row at all. This is
/// one sweep across all businesses on each row's current ExpiresAt, with no
/// tenant input, the same as VerificationCodeCleanupService.
///
/// Resend interaction (D9): resend holds the row lock and moves ExpiresAt
/// forward. This DELETE waits for that lock and, under READ COMMITTED,
/// re-evaluates its WHERE on the updated row, so a resent row survives; if
/// the DELETE commits first, resend finds no row and answers 404.
/// </summary>
public class StaffInvitationCleanupService(AppDbContext db) : IStaffInvitationCleanupService
{
    private static readonly TimeSpan RetentionAfterExpiry = TimeSpan.FromDays(30);

    public Task<int> DeleteExpiredAsync(CancellationToken ct = default)
    {
        var cutoff = DateTimeOffset.UtcNow - RetentionAfterExpiry;

        return db.StaffInvitations
            .IgnoreQueryFilters()
            .Where(i => i.ExpiresAt <= cutoff)
            .ExecuteDeleteAsync(ct);
    }
}
