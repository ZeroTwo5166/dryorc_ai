using backend.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace backend.Tests;

/// <summary>
/// Step getOrder — proves Program.cs's own DI registration for GetOrderTool,
/// resolved through the real app container (NivoraApiFactory), mirroring
/// DispatchToolRegistrationTests.cs's own OrderCancelProposalTool coverage.
/// No HTTP request is made and no DB row is written — this only exercises
/// service resolution. Per-business exposure (Business.ShopifyReadToolsEnabled)
/// is a separate concern, covered in ClaudeReplyServiceTests, not here.
/// </summary>
public class GetOrderToolRegistrationTests : IAsyncLifetime
{
    private readonly NivoraApiFactory _factory = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public void GetOrderTool_IsRegisteredUnconditionallyAtTheDiLevel()
    {
        using var scope = _factory.Services.CreateScope();
        var tools = scope.ServiceProvider.GetServices<IDispatchTool>().ToList();

        Assert.Contains(tools, t => t is GetOrderTool);
        Assert.Contains(tools, t => t.Name == GetOrderTool.ToolName);

        // Registering the new tool must not have displaced any existing one
        // (e.g. via an accidental Replace/singleton-overwrite bug).
        Assert.Contains(tools, t => t is GetCustomerTool);
        Assert.Contains(tools, t => t is OrderCancelProposalTool);
        Assert.Contains(tools, t => t is RefundProposalTool);
    }
}
