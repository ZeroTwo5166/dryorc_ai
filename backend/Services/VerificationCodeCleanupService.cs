using backend.Data;
using Microsoft.EntityFrameworkCore;

namespace backend.Services;

public interface IVerificationCodeCleanupService
{
    /// <summary>Deletes expired VerificationCode rows past their independent retention floor. Returns how many were removed.</summary>
    Task<int> CleanupExpiredAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// VerificationCode's own independent cleanup clock — deliberately NOT
/// tied to Business.RetentionDays. OTP codes are short-lived security
/// material, not conversation content; a business's (potentially much
/// longer, up to 730-day) conversation retention window has no bearing on
/// how long a stale, already-expired OTP should stick around. This is a
/// fixed floor past each code's own ExpiresAt, the same for every business.
///
/// Cross-tenant by nature (one sweep across all businesses), so this uses
/// IgnoreQueryFilters() the same way RetentionEnforcementService does.
/// </summary>
public class VerificationCodeCleanupService(AppDbContext db) : IVerificationCodeCleanupService
{
    private static readonly TimeSpan RetentionAfterExpiry = TimeSpan.FromDays(30);

    public Task<int> CleanupExpiredAsync(CancellationToken cancellationToken = default)
    {
        var cutoff = DateTimeOffset.UtcNow - RetentionAfterExpiry;

        return db.VerificationCodes
            .IgnoreQueryFilters()
            .Where(v => v.ExpiresAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
