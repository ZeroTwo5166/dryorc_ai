using backend.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace backend.Tests;

/// <summary>
/// Proves Program.cs's own DI registrations for IDispatchTool, resolved
/// through the real app container (NivoraApiFactory), not a hand-built
/// ServiceCollection that could silently drift from what production
/// actually wires. No HTTP request is made and no DB row is written —
/// this only exercises service resolution.
/// </summary>
public class DispatchToolRegistrationTests : IAsyncLifetime
{
    private readonly NivoraApiFactory _factory = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public void OrderCancelProposalTool_IsRegisteredUnconditionallyAtTheDiLevel()
    {
        // Step 48: registration alone must not depend on any
        // per-business flag — Program.cs registers this tool the same
        // way for every request, mirroring GetCustomerTool. The
        // per-business kill switch (Business.CancelOrderToolEnabled) is
        // enforced one layer up, in ClaudeReplyService, before
        // ToolDispatchService.RunAsync is ever called — see
        // ClaudeReplyServiceTests for that coverage.
        using var scope = _factory.Services.CreateScope();
        var tools = scope.ServiceProvider.GetServices<IDispatchTool>().ToList();

        Assert.Contains(tools, t => t is OrderCancelProposalTool);
        Assert.Contains(tools, t => t.Name == OrderCancelProposalTool.ToolName);

        // GetCustomerTool must still be registered alongside it —
        // registering the new tool must not have displaced the existing
        // one (e.g. via an accidental Replace/singleton-overwrite bug).
        Assert.Contains(tools, t => t is GetCustomerTool);
    }
}
