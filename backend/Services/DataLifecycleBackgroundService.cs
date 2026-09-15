using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace backend.Services;

/// <summary>
/// Periodic scheduler for the age-based cleanup clocks (conversation
/// retention, VerificationCode expiry, ConsumedSignedIdentityToken expiry —
/// Step 21 signed-token half) — deliberately thin. All the actual logic
/// lives in RetentionEnforcementService/VerificationCodeCleanupService/
/// ConsumedSignedIdentityTokenCleanupService, each tested directly by
/// calling its own methods, not by running this timer loop — a
/// BackgroundService is host-lifecycle glue, not something worth
/// exercising through its own timer in a test.
///
/// Step 25 Candidate 2, Phase 2 — each job now runs behind its own
/// independent error boundary (RunJobAsync below), mirroring
/// ScheduledReportBackgroundService's own already-proven per-run
/// catch/log/continue shape (ScheduledReportBackgroundService.cs:79-94),
/// applied here at per-JOB granularity instead of per-claimed-run.
/// Previously this class had ZERO error boundary at all: an uncaught
/// exception from any one job (a) aborted every job sequenced after it in
/// the same tick, and (b) propagated out of ExecuteAsync, risking the
/// unconfigured .NET default BackgroundServiceExceptionBehavior.StopHost
/// taking down the entire application host over one job's failure — a
/// real, pre-existing risk (present since this class's original three-job
/// form), closed here for all three jobs. See
/// docs/handoffs/step-25-candidate2-stale-conversation-autoclose-design.md
/// §1.2/§1.3 for the full analysis this refactor implements. This phase
/// deliberately does not add a fourth job (auto-close) — that is Phase 4's
/// own, later scope.
///
/// AppDbContext is Scoped, so a fresh DI scope is created per tick rather
/// than resolving the two services once at startup.
/// </summary>
public class DataLifecycleBackgroundService(
    IServiceProvider serviceProvider,
    ILogger<DataLifecycleBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        do
        {
            await using var scope = serviceProvider.CreateAsyncScope();

            await RunJobAsync(scope.ServiceProvider, "RetentionEnforcement",
                sp => sp.GetRequiredService<IRetentionEnforcementService>().EnforceRetentionAsync(stoppingToken),
                stoppingToken);

            await RunJobAsync(scope.ServiceProvider, "VerificationCodeCleanup",
                sp => sp.GetRequiredService<IVerificationCodeCleanupService>().CleanupExpiredAsync(stoppingToken),
                stoppingToken);

            await RunJobAsync(scope.ServiceProvider, "ConsumedSignedIdentityTokenCleanup",
                sp => sp.GetRequiredService<IConsumedSignedIdentityTokenCleanupService>().CleanupExpiredAsync(stoppingToken),
                stoppingToken);
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>
    /// One lifecycle job's independent error boundary. An
    /// OperationCanceledException tied to stoppingToken is real host
    /// shutdown — rethrown, never logged as a failure, never swallowed —
    /// so it propagates out of ExecuteAsync's do-while and this tick's
    /// remaining RunJobAsync calls are never reached, matching
    /// BackgroundService's own graceful-shutdown contract exactly
    /// (ScheduledReportBackgroundService.cs:83-88's identical guard). Any
    /// other exception — including an OperationCanceledException NOT tied
    /// to stoppingToken, e.g. an unrelated internal cancellation a job
    /// raises for its own reasons — is logged with jobName identifying
    /// which specific job failed, then swallowed so the next job in this
    /// tick still runs.
    /// </summary>
    private async Task RunJobAsync(
        IServiceProvider scopedProvider, string jobName, Func<IServiceProvider, Task> job, CancellationToken stoppingToken)
    {
        try
        {
            await job(scopedProvider);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw; // real shutdown — never swallowed, never logged as a failure
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "{JobName} threw during this DataLifecycleBackgroundService tick — other lifecycle jobs still run.",
                jobName);
        }
    }
}
