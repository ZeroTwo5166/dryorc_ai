using backend.Data;
using Microsoft.EntityFrameworkCore;

namespace backend.Services;

public interface IConsumedSignedIdentityTokenCleanupService
{
    /// <summary>Deletes expired ConsumedSignedIdentityToken rows past their
    /// retention floor. Returns how many were removed.</summary>
    Task<int> CleanupExpiredAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Storage hygiene only, not part of replay correctness (implementation-
/// ready spec §4A). Standard exp validation in SignedIdentityTokenService
/// runs before the jti-replay lookup, so a token old enough for its
/// consumed-jti record to be eligible for this cleanup is already far past
/// any realistic exp and would be rejected on ordinary expiry grounds
/// regardless of whether its replay record still exists — cleanup timing
/// has no bearing on replay correctness.
///
/// Same fixed-floor-past-consumption shape as VerificationCodeCleanupService,
/// reusing its 30-day retention convention. Cross-tenant sweep (one pass
/// across all businesses), IgnoreQueryFilters() the same way.
/// </summary>
public class ConsumedSignedIdentityTokenCleanupService(AppDbContext db) : IConsumedSignedIdentityTokenCleanupService
{
    private static readonly TimeSpan RetentionAfterConsumption = TimeSpan.FromDays(30);

    public Task<int> CleanupExpiredAsync(CancellationToken cancellationToken = default)
    {
        var cutoff = DateTimeOffset.UtcNow - RetentionAfterConsumption;

        return db.ConsumedSignedIdentityTokens
            .IgnoreQueryFilters()
            .Where(t => t.ConsumedAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
