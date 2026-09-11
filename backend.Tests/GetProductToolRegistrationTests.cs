using backend.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace backend.Tests;

/// <summary>
/// Step Group B — proves Program.cs's own DI registration for GetProductTool
/// and SearchProductsTool, resolved through the real app container
/// (NivoraApiFactory), mirroring GetOrderToolRegistrationTests.cs's own
/// GetOrderTool coverage. No HTTP request is made and no DB row is written
/// — this only exercises service resolution. Per-business exposure
/// (Business.ShopifyReadToolsEnabled) is a separate concern, covered in
/// ClaudeReplyServiceTests, not here.
/// </summary>
public class GetProductToolRegistrationTests : IAsyncLifetime
{
    private readonly NivoraApiFactory _factory = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public void GetProductToolAndSearchProductsTool_AreRegisteredUnconditionallyAtTheDiLevel()
    {
        using var scope = _factory.Services.CreateScope();
        var tools = scope.ServiceProvider.GetServices<IDispatchTool>().ToList();

        Assert.Contains(tools, t => t is GetProductTool);
        Assert.Contains(tools, t => t.Name == GetProductTool.ToolName);
        Assert.Contains(tools, t => t is SearchProductsTool);
        Assert.Contains(tools, t => t.Name == SearchProductsTool.ToolName);

        // Registering the two new tools must not have displaced any
        // existing one (e.g. via an accidental Replace/singleton-overwrite
        // bug).
        Assert.Contains(tools, t => t is GetCustomerTool);
        Assert.Contains(tools, t => t is GetOrderTool);
        Assert.Contains(tools, t => t is OrderCancelProposalTool);
        Assert.Contains(tools, t => t is RefundProposalTool);
    }

    [Fact]
    public void IShopifyProductLookupService_IsRegistered()
    {
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetService<IShopifyProductLookupService>();

        Assert.NotNull(service);
        Assert.IsType<ShopifyProductLookupService>(service);
    }
}
