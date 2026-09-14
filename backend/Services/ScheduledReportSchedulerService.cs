using backend.Data;
using backend.Enums;
using backend.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace backend.Services;

/// <summary>
/// Step 24 Phase 5 — see IScheduledReportSchedulerService's own doc comment
/// and design §8/§12 for the full contract. Explicitly cross-tenant by
/// nature (iterates every business in one pass, exactly like
/// RetentionEnforcementService/OrderCancellationReconciliationService), so
/// every query here uses IgnoreQueryFilters() with an explicit BusinessId
/// filter rather than the ambient ICurrentTenantAccessor, which has nothing
/// to resolve in this no-HttpContext, scheduled-job context — same
/// precedent, same reasoning, cited verbatim from RetentionEnforcementService.cs.
///
/// TimeProvider is injected here — and ONLY here, for this feature's own
/// due-detection — rather than reading DateTimeOffset.UtcNow directly the
/// way RetentionEnforcementService/OrderCancellationReconciliationService
/// do. This is a deliberate, narrow addition for this one service's own
/// testability (the concurrency/due-detection tests this phase requires
/// need a controllable clock); it does not replace, and must not be used
/// to replace, DateTimeOffset.UtcNow anywhere else in this codebase.
/// </summary>
public class ScheduledReportSchedulerService(
    AppDbContext db,
    TimeProvider timeProvider,
    ILogger<ScheduledReportSchedulerService> logger) : IScheduledReportSchedulerService
{
    /// <summary>
    /// The sole idempotency arbiter's DB-level backstop (design §12.2) —
    /// matches the Phase 3 migration exactly (AddScheduledReportRunTable).
    /// </summary>
    private const string UniqueConstraintName = "IX_ScheduledReportRuns_BusinessId_ReportPeriodStart";

    public async Task<IReadOnlyList<ScheduledReportRun>> PollAsync(CancellationToken cancellationToken)
    {
        var nowUtc = timeProvider.GetUtcNow();

        // Design §8.3 — cross-tenant discovery read, directly mirroring
        // RetentionEnforcementService.cs's own db.Businesses.IgnoreQueryFilters()
        // shape. Enablement here is read only to decide whether to *attempt*
        // a claim at all (an optimization) — never treated as authoritative;
        // it is re-read, fresh, immediately before the claim attempt below
        // (claim linearization), and Phase 6 re-checks it again,
        // authoritatively, before any external side effect.
        var candidates = await db.Businesses
            .IgnoreQueryFilters()
            .Where(b => b.ScheduledReportsEnabled)
            .Select(b => new { b.Id, b.TimeZone })
            .ToListAsync(cancellationToken);

        var claimed = new List<ScheduledReportRun>();

        foreach (var business in candidates)
        {
            BusinessWeekPeriodCalculator.WeekPeriod period;
            try
            {
                period = BusinessWeekPeriodCalculator.Compute(nowUtc, business.TimeZone);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                // A malformed Business.TimeZone must not abort discovery for
                // every other business in the same poll — mirrors
                // OrderCancellationReconciliationService's own "one row's
                // failure... does not stop the sweep from processing the
                // rest" precedent. No write-time TimeZone validation exists
                // anywhere in this codebase (design §19.1); this is the
                // per-business processing failure design §5.3 anticipated.
                logger.LogWarning(ex,
                    "Business {BusinessId} has an unrecognized TimeZone {TimeZone} - skipped for scheduled-report discovery this poll.",
                    business.Id, business.TimeZone);
                continue;
            }

            if (nowUtc < period.SendAtUtc)
            {
                continue; // not yet due
            }

            // Claim linearization (locked for this phase): re-read
            // enablement fresh, immediately before the INSERT attempt below
            // - never trust the value read in the discovery query above,
            // which may be stale by the time this specific business is
            // reached in the loop. The successful INSERT is the claim's own
            // linearization point; a concurrent enablement change landing
            // after that point does not retroactively invalidate an
            // already-created claim (Phase 6 owns the full authoritative
            // re-check before any external side effect).
            var stillEnabled = await db.Businesses
                .IgnoreQueryFilters()
                .Where(b => b.Id == business.Id)
                .Select(b => b.ScheduledReportsEnabled)
                .SingleAsync(cancellationToken);

            if (!stillEnabled)
            {
                continue;
            }

            var run = new ScheduledReportRun
            {
                Id = Guid.NewGuid(),
                BusinessId = business.Id,
                ReportPeriodStart = period.PeriodStartUtc,
                ReportPeriodEnd = period.PeriodEndUtcExclusive,
                Status = ScheduledReportRunStatus.Processing,
                ClaimedAt = nowUtc,
            };
            db.ScheduledReportRuns.Add(run);

            try
            {
                // Design §12.2 — the atomic INSERT, guarded entirely by the
                // Phase 3 DB-level UNIQUE INDEX (BusinessId,
                // ReportPeriodStart), is the primary and sole arbiter for
                // the concurrent-discovery race: Postgres guarantees exactly
                // one of two concurrent INSERTs for the same key commits, no
                // advisory lock, no service-level locking, no AnyAsync()
                // pre-check relied on for correctness.
                await db.SaveChangesAsync(cancellationToken);
                claimed.Add(run);
            }
            catch (DbUpdateException ex) when (
                ex.InnerException is PostgresException { SqlState: "23505", ConstraintName: UniqueConstraintName })
            {
                // Another instance already claimed (or a prior poll already
                // created) this (BusinessId, ReportPeriodStart) - ordinary
                // concurrency handling under multi-instance polling, not an
                // error (design §12.2, matching ApprovalsController's own
                // "0-row result = ordinary concurrency, never a security
                // event" precedent). This instance does nothing further for
                // this (business, period) this poll - no analytics call, no
                // Owner resolution, no state mutation: it never obtains a
                // run to pass anywhere, structurally (design §12.4).
                //
                // Detaching the failed entity is not optional here: this
                // DbContext is shared across every business in this same
                // PollAsync call (one scope per tick, not one per business),
                // so a still-Added, still-failed entity left in the change
                // tracker would be re-submitted - and fail again - on the
                // very next business's SaveChangesAsync call in this loop,
                // silently blocking every business after the first
                // collision. This is a correctness requirement of the loop
                // shape, not present in design §12.2's own single-attempt
                // illustration.
                db.Entry(run).State = EntityState.Detached;
                logger.LogDebug(
                    "Scheduled-report claim lost for business {BusinessId}, period {ReportPeriodStart} - ordinary multi-instance concurrency.",
                    business.Id, run.ReportPeriodStart);
            }
        }

        return claimed;
    }
}
