using backend.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace backend.Tests;

/// <summary>
/// Step 27 Slice A1, Slice 4b — T-F1 (design §10.1): the default
/// NivoraApiFactory host resolves its own non-persisting
/// ConcurrentCapturingAiInvocationRecorder, and
/// PersistingInvocationRecorderApiFactory keeps Program.cs's real
/// AiInvocationRecorder. Boots both real hosts (dev DB via user-secrets);
/// seeds nothing and writes no rows, so there is nothing to clean up.
/// Mutations: remove the default factory's swap (default resolves the real
/// recorder); make the persisting factory also register the capture.
/// </summary>
public class InvocationRecorderFactoryWiringTests : IAsyncLifetime
{
    private readonly NivoraApiFactory _defaultFactory = new();
    private readonly PersistingInvocationRecorderApiFactory _persistingFactory = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _defaultFactory.DisposeAsync();
        await _persistingFactory.DisposeAsync();
    }

    [Fact]
    public async Task DefaultFactory_ResolvesItsOwnCapturingRecorder()
    {
        await using var scope = _defaultFactory.Services.CreateAsyncScope();

        var recorder = scope.ServiceProvider.GetRequiredService<IAiInvocationRecorder>();

        var capture = Assert.IsType<ConcurrentCapturingAiInvocationRecorder>(recorder);
        Assert.Same(_defaultFactory.InvocationCapture, capture);
    }

    [Fact]
    public async Task PersistingFactory_ResolvesRealRecorder()
    {
        await using var scope = _persistingFactory.Services.CreateAsyncScope();

        Assert.IsType<AiInvocationRecorder>(scope.ServiceProvider.GetRequiredService<IAiInvocationRecorder>());
    }
}
