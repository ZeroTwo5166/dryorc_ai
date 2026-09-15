using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace backend.Services;

/// <summary>
/// Step 24 Phase 7 — the live, DI-registered scheduler. Same thin-scheduler
/// shape as OrderCancellationReconciliationBackgroundService/
/// DataLifecycleBackgroundService: PeriodicTimer (15-minute interval, per
/// design §8.1 — unchanged from Phase 5), fresh DI scope per tick, real
/// logic delegated entirely to directly-testable scoped services.
///
/// For each run IScheduledReportSchedulerService.PollAsync claims this tick,
/// this service hands it to IScheduledReportExecutionService.ExecuteAsync,
/// both resolved from the SAME per-tick scope. The SAME stoppingToken is
/// threaded through both calls, unmodified — nothing here creates, replaces,
/// or swallows it; this is the exact token the Phase 6 crash-window test
/// depended on reaching ExecuteAsync unchanged.
///
/// Enablement (Business.ScheduledReportsEnabled) is not re-checked here —
/// PollAsync already owns the discovery-time filter (design §8.3), and
/// ExecuteAsync already owns the authoritative execution-time re-check
/// (design §9 step 1). This class adds no third enablement check; it is
/// pure composition.
///
/// Per-run try/catch below: unlike OrderCancellationReconciliationService,
/// which owns its own internal per-row loop and per-row try/catch (so its
/// own BackgroundService wrapper needs none), Phase 6's ExecuteAsync
/// contract is deliberately single-run — Phase 5's own tests already prove
/// multiple businesses can be claimed in one tick
/// (PollAsync_MultipleBusinesses_OneAlreadyClaimed_DoesNotBlockClaimingTheOthers),
/// and design §9/§10.3 deliberately leaves steps 1/2/6 unwrapped, so an
/// uncaught exception there is expected to propagate out of ExecuteAsync
/// for that one run. Without a catch AT THIS LOOP, that exception would
/// both abort every other claimed run still waiting in this same tick and
/// escape this BackgroundService's own ExecuteAsync — which, since .NET 6,
/// defaults to stopping the entire application host
/// (BackgroundServiceExceptionBehavior.StopHost), not just this one
/// scheduler. That consequence is wildly disproportionate to design §10.4's
/// own stated stakes ("a missed weekly analytics email... materially lower
/// than the AI Action Safety Model's stuck-execution problem") and is what
/// this catch exists to prevent — the run itself still ends up correctly
/// stuck in Processing (10.4's own accepted outcome), logged here, with
/// every other claimed run in this tick still processed normally.
/// Cancellation from a real shutdown (stoppingToken itself firing) is
/// deliberately NOT caught here — it is rethrown so this loop, and the
/// BackgroundService's own graceful-shutdown path, still unwind normally.
///
/// DI registration (`AddScoped&lt;IScheduledReportSchedulerService,...&gt;()` +
/// `AddScoped&lt;IScheduledReportExecutionService,...&gt;()` +
/// `AddSingleton(TimeProvider.System)` + `AddHostedService&lt;ScheduledReportBackgroundService&gt;()`)
/// lives in Program.cs, this phase's own final piece — see Program.cs's own
/// comment at that registration for why going live changes production
/// behavior for zero real businesses today.
/// </summary>
public class ScheduledReportBackgroundService(
    IServiceProvider serviceProvider,
    ILogger<ScheduledReportBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        do
        {
            await using var scope = serviceProvider.CreateAsyncScope();
            var scheduler = scope.ServiceProvider.GetRequiredService<IScheduledReportSchedulerService>();
            var claimedRuns = await scheduler.PollAsync(stoppingToken);

            if (claimedRuns.Count > 0)
            {
                var executionService = scope.ServiceProvider.GetRequiredService<IScheduledReportExecutionService>();

                foreach (var run in claimedRuns)
                {
                    try
                    {
                        await executionService.ExecuteAsync(run, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        throw; // real shutdown - never swallowed, never logged as a failure
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex,
                            "Scheduled report execution threw for business {BusinessId}, period {ReportPeriodStart} - " +
                            "left Processing (design §10.4, never auto-resumed). Continuing with the rest of this tick's claimed runs.",
                            run.BusinessId, run.ReportPeriodStart);
                    }
                }
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
