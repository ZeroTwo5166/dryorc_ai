using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace backend.Services;

/// <summary>
/// Periodic scheduler for the two age-based cleanup clocks (conversation
/// retention, VerificationCode expiry) — deliberately thin. All the actual
/// logic lives in RetentionEnforcementService/VerificationCodeCleanupService,
/// which are tested directly by calling their methods, not by running this
/// timer loop — a BackgroundService is host-lifecycle glue, not something
/// worth exercising through its own timer in a test.
///
/// AppDbContext is Scoped, so a fresh DI scope is created per tick rather
/// than resolving the two services once at startup.
/// </summary>
public class DataLifecycleBackgroundService(IServiceProvider serviceProvider) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        do
        {
            await using var scope = serviceProvider.CreateAsyncScope();
            var retention = scope.ServiceProvider.GetRequiredService<IRetentionEnforcementService>();
            var verificationCodeCleanup = scope.ServiceProvider.GetRequiredService<IVerificationCodeCleanupService>();

            await retention.EnforceRetentionAsync(stoppingToken);
            await verificationCodeCleanup.CleanupExpiredAsync(stoppingToken);
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
