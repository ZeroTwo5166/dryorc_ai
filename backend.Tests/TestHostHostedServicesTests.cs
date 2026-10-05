using backend.Services;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace backend.Tests;

public class TestHostHostedServicesTests
{
    private static readonly Type[] AppHostedServiceTypes =
    [
        typeof(DataLifecycleBackgroundService),
        typeof(OrderCancellationReconciliationBackgroundService),
        typeof(ScheduledReportBackgroundService),
    ];

    [Fact]
    public async Task DefaultHost_RunsNoneOfTheThreeApplicationHostedServices()
    {
        await using var factory = new NivoraApiFactory();
        var running = factory.Services.GetServices<IHostedService>()
            .Select(s => s.GetType()).Where(AppHostedServiceTypes.Contains).ToList();
        Assert.Empty(running);
    }

    [Theory]
    [InlineData(TestHostedServices.DataLifecycle, typeof(DataLifecycleBackgroundService))]
    [InlineData(TestHostedServices.ScheduledReport, typeof(ScheduledReportBackgroundService))]
    public async Task OptInHost_RegistersExactlyTheRequestedService(TestHostedServices requested, Type expected)
    {
        List<Type>? registered = null;
        await using var factory = new NivoraApiFactory { HostedServices = requested }
            .WithWebHostBuilder(b => b.ConfigureTestServices(services =>
            {
                var app = services.Where(d => d.ServiceType == typeof(IHostedService) && !d.IsKeyedService
                                              && AppHostedServiceTypes.Contains(d.ImplementationType)).ToList();
                registered = app.Select(d => d.ImplementationType!).ToList();
                foreach (var d in app) services.Remove(d); // no real tick from this check (R4)
            }));
        _ = factory.Services;
        Assert.Equal([expected], registered);
        Assert.DoesNotContain(factory.Services.GetServices<IHostedService>(), s => AppHostedServiceTypes.Contains(s.GetType()));
    }

    [Fact]
    public void OptInSurface_HasNoOrderCancellationReconciliationMember() =>
        Assert.Equal(["None", "DataLifecycle", "ScheduledReport"], Enum.GetNames<TestHostedServices>());
}
