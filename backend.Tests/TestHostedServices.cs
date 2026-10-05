using backend.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace backend.Tests;

/// <summary>
/// Which of Program.cs's application hosted services a test host runs.
/// None by default; one flag per service a test may opt in to. There is
/// deliberately no member for OrderCancellationReconciliationBackgroundService.
/// </summary>
[Flags]
public enum TestHostedServices
{
    None = 0,
    DataLifecycle = 1,
    ScheduledReport = 2,
}

/// <summary>
/// Removes Program.cs's application hosted services from a test host's
/// service collection, except those enabled. Input comes only from the
/// caller, never from configuration.
/// </summary>
internal static class TestHostedServiceFilter
{
    // (flag, type). A row with no flag is always removed.
    private static readonly (TestHostedServices? Flag, Type Type)[] Rows =
    [
        (TestHostedServices.DataLifecycle, typeof(DataLifecycleBackgroundService)),
        (null, typeof(OrderCancellationReconciliationBackgroundService)),
        (TestHostedServices.ScheduledReport, typeof(ScheduledReportBackgroundService)),
    ];

    public static void Apply(IServiceCollection services, TestHostedServices enabled)
    {
        foreach (var (flag, type) in Rows)
        {
            // IsKeyedService first: ImplementationType throws on keyed descriptors.
            var matches = services
                .Where(d => d.ServiceType == typeof(IHostedService)
                            && !d.IsKeyedService
                            && d.ImplementationType == type)
                .ToList();
            if (matches.Count != 1)
            {
                throw new InvalidOperationException(
                    $"Expected exactly one IHostedService registration for {type.FullName}, found {matches.Count}.");
            }

            if (flag is { } f && enabled.HasFlag(f))
            {
                continue;
            }

            var descriptor = matches[0];
            services.Remove(descriptor);
        }
    }
}
