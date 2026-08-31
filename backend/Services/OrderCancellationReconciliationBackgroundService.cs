using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace backend.Services;

/// <summary>
/// Same thin-scheduler shape as DataLifecycleBackgroundService, kept as a
/// separate BackgroundService rather than folded into that one's loop — its
/// 24-hour interval is far too infrequent for order cancellations, which
/// should typically resolve within minutes. Real logic lives entirely in
/// OrderCancellationReconciliationService, tested directly, not through
/// this timer loop.
/// </summary>
public class OrderCancellationReconciliationBackgroundService(IServiceProvider serviceProvider) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        do
        {
            await using var scope = serviceProvider.CreateAsyncScope();
            var reconciliation = scope.ServiceProvider.GetRequiredService<IOrderCancellationReconciliationService>();
            await reconciliation.ReconcileAsync(stoppingToken);
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
