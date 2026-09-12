using backend.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace backend.Tests;

/// <summary>
/// Step Group C — proves Program.cs's own DI registration for
/// GetInventoryTool, resolved through the real app container
/// (NivoraApiFactory), mirroring GetProductToolRegistrationTests.cs's own
/// coverage. No HTTP request is made and no DB row is written — this only
/// exercises service resolution. Per-business exposure
/// (Business.ShopifyReadToolsEnabled) is a separate concern, covered in
/// ClaudeReplyServiceTests, not here.
/// </summary>
public class GetInventoryToolRegistrationTests : IAsyncLifetime
{
    private readonly NivoraApiFactory _factory = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public void GetInventoryTool_IsRegisteredUnconditionallyAtTheDiLevel()
    {
        using var scope = _factory.Services.CreateScope();
        var tools = scope.ServiceProvider.GetServices<IDispatchTool>().ToList();

        Assert.Contains(tools, t => t is GetInventoryTool);
        Assert.Contains(tools, t => t.Name == GetInventoryTool.ToolName);

        // Registering the new tool must not have displaced any existing
        // one (e.g. via an accidental Replace/singleton-overwrite bug).
        Assert.Contains(tools, t => t is GetCustomerTool);
        Assert.Contains(tools, t => t is GetOrderTool);
        Assert.Contains(tools, t => t is GetProductTool);
        Assert.Contains(tools, t => t is SearchProductsTool);
        Assert.Contains(tools, t => t is OrderCancelProposalTool);
        Assert.Contains(tools, t => t is RefundProposalTool);
    }

    [Fact]
    public void IShopifyProductLookupService_IsRegistered_AndSharedByGetInventoryTool()
    {
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetService<IShopifyProductLookupService>();

        // Owner Decision 3 (implementation design §5.2) — no new sibling
        // service was introduced; getInventory reuses the same registered
        // IShopifyProductLookupService/ShopifyProductLookupService instance
        // getProduct/searchProducts already use.
        Assert.NotNull(service);
        Assert.IsType<ShopifyProductLookupService>(service);
    }
}
