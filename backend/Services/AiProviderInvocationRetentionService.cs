using backend.Data;
using Microsoft.EntityFrameworkCore;

namespace backend.Services;

public interface IAiProviderInvocationRetentionService
{
    /// <summary>Deletes AiProviderInvocation rows older than the fixed
    /// retention window, across every business. Returns how many were removed.</summary>
    Task<int> DeleteExpiredAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Step 27 A1 Slice 6 — the retention job A1 v11 §7 Decision 4 requires
/// (docs/handoffs/billing-usage-a1-invocation-capture-design-v11.md), and
/// the only code permitted to delete AiProviderInvocation rows (v11 §3:
/// rows are immutable after insert; only the retention policy may delete
/// expired events). Design: docs/handoffs/billing-usage-a1-slice6-design.md.
///
/// A fixed 90-day GLOBAL window, the same for every business — deliberately
/// NOT Business.RetentionDays. Same fixed-floor shape as
/// VerificationCodeCleanupService/ConsumedSignedIdentityTokenCleanupService.
/// The cutoff is strict: a row exactly at the cutoff is kept.
///
/// Cross-tenant by nature (one sweep across all businesses), so this uses
/// IgnoreQueryFilters(): in this scheduled, no-HttpContext context there is
/// no tenant to resolve, and the tenant filter is fail-closed — it would
/// match no row at all. This is the one query against this table that is
/// not tenant-scoped; the standalone CreatedAt index (see AppDbContext's
/// own comment on it) exists for exactly this predicate.
///
/// Hard delete of this table only — no other table is touched, and nothing
/// reads this table today. No audit row and no deletion-count logging
/// (owner ruling, Slice 6); a failure is logged by
/// DataLifecycleBackgroundService's per-job RunJobAsync boundary.
/// </summary>
public class AiProviderInvocationRetentionService(AppDbContext db) : IAiProviderInvocationRetentionService
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(90);

    public Task<int> DeleteExpiredAsync(CancellationToken cancellationToken = default)
    {
        var cutoff = DateTimeOffset.UtcNow - Retention;

        return db.AiProviderInvocations
            .IgnoreQueryFilters()
            .Where(i => i.CreatedAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
