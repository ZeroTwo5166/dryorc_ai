using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace backend.Tests;

/// <summary>
/// Step 22 Phase 3 (concurrency-test correction) — pauses a writer
/// immediately BEFORE its SaveChangesAsync-issued UPDATE to "Conversations"
/// is actually sent to Postgres. This is deliberately earlier in the
/// pipeline than a pre-commit hook would be: at this point the writer has
/// already (successfully) executed any earlier command in the same
/// transaction — including a real "SELECT pg_advisory_xact_lock(...)" —
/// but has not yet issued the UPDATE that would take Postgres's own
/// ordinary row-level lock. Pausing here, not at commit time, removes the
/// row-lock confound an earlier version of this test's own pre-commit-pause
/// design had by construction: with the writer parked here, no row lock has
/// been acquired for it yet. (No deterministic backend-observation
/// mechanism currently exists to complete the corresponding negative proof
/// — see NIVORA_FULL_PLAN_CHECKLIST_V2.md's loose-ends section for why.)
/// Zero production code changes — wired in only via a test-specific
/// DbContextOptions.
/// </summary>
public class UpdateCommandPauseInterceptor : DbCommandInterceptor
{
    public Func<Task>? BeforeUpdate { get; set; }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        await MaybePause(command);
        return await base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    // EF Core 10 / Npgsql sends SaveChangesAsync's UPDATE for this entity
    // via the reader path (an UPDATE ... RETURNING statement, Npgsql's
    // save-changes optimization for reading back store-generated/computed
    // values), not ExecuteNonQuery — confirmed empirically, not assumed, by
    // logging every command text this interceptor observed during a real
    // run before this override existed. Both overrides are kept (rather
    // than only this one) so this interceptor pauses correctly regardless
    // of which path a future EF Core/Npgsql version routes this UPDATE
    // through.
    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        await MaybePause(command);
        return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    private async Task MaybePause(DbCommand command)
    {
        if (BeforeUpdate is not null && command.CommandText.Contains("UPDATE \"Conversations\"", StringComparison.Ordinal))
        {
            await BeforeUpdate();
        }
    }
}
