using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace backend.Services;

/// <summary>
/// Step 24 Phase 5 — thin scheduler, same shape as
/// OrderCancellationReconciliationBackgroundService/DataLifecycleBackgroundService:
/// PeriodicTimer, fresh DI scope per tick, delegates all real logic to a
/// directly-testable scoped service. Real logic lives entirely in
/// ScheduledReportSchedulerService, tested directly, not through this timer
/// loop.
///
/// NOT registered in DI as of this phase (deliberately - see design/plan
/// §4's dependency-ordering rationale: registering this before Phase 6's
/// execution service exists would let a live scheduler claim real
/// ScheduledReportRun rows that nothing can ever finalize, an unnecessary
/// stuck-row risk for a feature that is not yet usable end to end). DI
/// registration (`AddScoped&lt;IScheduledReportSchedulerService,...&gt;()` +
/// `AddHostedService&lt;ScheduledReportBackgroundService&gt;()`) is Phase 7's own,
/// dedicated final-phase responsibility.
///
/// This phase's own PollAsync result (the runs claimed this tick) is
/// deliberately discarded here — there is no IScheduledReportExecutionService
/// to hand it to yet; Phase 7's wiring will add that call once Phase 6
/// exists.
/// </summary>
public class ScheduledReportBackgroundService(IServiceProvider serviceProvider) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        do
        {
            await using var scope = serviceProvider.CreateAsyncScope();
            var scheduler = scope.ServiceProvider.GetRequiredService<IScheduledReportSchedulerService>();
            await scheduler.PollAsync(stoppingToken);
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
