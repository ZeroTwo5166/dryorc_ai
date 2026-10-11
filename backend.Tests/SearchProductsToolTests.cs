using backend.Data;
using backend.Interfaces;
using backend.Models;
using backend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace backend.Tests;

/// <summary>
/// Step Group B — security/functional coverage for
/// SearchProductsTool.ExecuteAsync itself, using a fake
/// IShopifyProductLookupService substitute so this suite exercises
/// SearchProductsTool's own sequencing/branching without a real Shopify
/// call. Real Postgres for the Business/Integration rows the tool's own
/// db.Integrations query requires — same convention as GetProductToolTests.
/// </summary>
public class SearchProductsToolTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;
    private readonly List<Guid> _businessIdsToClean = [];

    public Task InitializeAsync()
    {
        var services = new ServiceCollection()
            .AddSingleton<ICurrentTenantAccessor>(new FixedTenantAccessor(null))
            .AddDbContext<AppDbContext>(o => o.UseNpgsql(
                TestDatabase.ConnectionString, x => x.UseVector()));
        _provider = services.BuildServiceProvider();

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await TestBusinessCleanup.DeleteThenDisposeAsync(_businessIdsToClean, _provider);
    }

    private async Task<Guid> SeedBusinessAsync(bool withActiveIntegration)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var business = new Business
        {
            Id = Guid.NewGuid(),
            Name = $"SearchProductsTool Test {Guid.NewGuid():N}",
            TimeZone = "Europe/Copenhagen",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Businesses.Add(business);

        if (withActiveIntegration)
        {
            db.Integrations.Add(new Integration
            {
                Id = Guid.NewGuid(),
                BusinessId = business.Id,
                Platform = ShopifyIntegrationOAuthProvider.PlatformName,
                EncryptedAccessToken = "unused-in-this-suite-shopify-is-faked",
                ExternalAccountIdentifier = $"test-shop-{Guid.NewGuid():N}",
                IsActive = true,
                ConnectedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }

        await db.SaveChangesAsync();
        _businessIdsToClean.Add(business.Id);
        return business.Id;
    }

    private static Dictionary<string, JsonElement> Input(string query) => new()
    {
        ["query"] = JsonSerializer.SerializeToElement(query),
    };

    // ── Functional ─────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_ResultsFound_ReturnsFormattedList()
    {
        var businessId = await SeedBusinessAsync(withActiveIntegration: true);
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var products = new List<ProductSummaryResult>
        {
            new("gid://shopify/Product/1", "Blue Hoodie", "Comfy.",
                new ProductVariantSummary("HOODIE-BLU-M", "39.99", true, [])),
            new("gid://shopify/Product/2", "Red Hat", null, null),
        };
        var lookup = new FakeShopifyProductLookupService
        {
            SearchResultFactory = () => new ProductSearchResult(products),
        };
        var tool = new SearchProductsTool(db, lookup);

        var result = await tool.ExecuteAsync(
            new ToolDispatchContext(businessId, Guid.NewGuid(), Guid.NewGuid(), new CustomerReplyCorrelation()), Input("hoodie"), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("Blue Hoodie", result.Content);
        Assert.Contains("$39.99", result.Content);
        Assert.Contains("Red Hat", result.Content);
        Assert.Contains("No variant information available.", result.Content);
        Assert.Equal(1, lookup.SearchProductsCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_EmptyResults_ReturnsSuccess_NotAnError()
    {
        var businessId = await SeedBusinessAsync(withActiveIntegration: true);
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var lookup = new FakeShopifyProductLookupService
        {
            SearchResultFactory = () => new ProductSearchResult([]),
        };
        var tool = new SearchProductsTool(db, lookup);

        var result = await tool.ExecuteAsync(
            new ToolDispatchContext(businessId, Guid.NewGuid(), Guid.NewGuid(), new CustomerReplyCorrelation()), Input("nonexistent"), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("No products matched that search.", result.Content);
    }

    // ── Infra / auth failure ───────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_NoActiveIntegration_ReturnsInfraRefusal_NeverCallsLookup()
    {
        var businessId = await SeedBusinessAsync(withActiveIntegration: false);
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var lookup = new FakeShopifyProductLookupService();
        var tool = new SearchProductsTool(db, lookup);

        var result = await tool.ExecuteAsync(
            new ToolDispatchContext(businessId, Guid.NewGuid(), Guid.NewGuid(), new CustomerReplyCorrelation()), Input("anything"), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("trouble reaching", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, lookup.SearchProductsCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_ShopifyAuthFailure_ReturnsInfraRefusal_NotGenericErrorText()
    {
        var businessId = await SeedBusinessAsync(withActiveIntegration: true);
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var lookup = new FakeShopifyProductLookupService
        {
            SearchResultFactory = () => throw new ShopifyAuthFailureException(),
        };
        var tool = new SearchProductsTool(db, lookup);

        var result = await tool.ExecuteAsync(
            new ToolDispatchContext(businessId, Guid.NewGuid(), Guid.NewGuid(), new CustomerReplyCorrelation()), Input("anything"), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("trouble reaching", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("internal error", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    // ── Malformed input ────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_MissingQuery_ReturnsError()
    {
        var businessId = await SeedBusinessAsync(withActiveIntegration: true);
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var tool = new SearchProductsTool(db, new FakeShopifyProductLookupService());

        var result = await tool.ExecuteAsync(
            new ToolDispatchContext(businessId, Guid.NewGuid(), Guid.NewGuid(), new CustomerReplyCorrelation()),
            new Dictionary<string, JsonElement>(), CancellationToken.None);

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task ExecuteAsync_BlankQuery_ReturnsError()
    {
        var businessId = await SeedBusinessAsync(withActiveIntegration: true);
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var tool = new SearchProductsTool(db, new FakeShopifyProductLookupService());

        var result = await tool.ExecuteAsync(
            new ToolDispatchContext(businessId, Guid.NewGuid(), Guid.NewGuid(), new CustomerReplyCorrelation()), Input("   "), CancellationToken.None);

        Assert.True(result.IsError);
    }

    // ── No customer-ownership dimension (locked design §3) ────────────

    [Fact]
    public async Task ExecuteAsync_Succeeds_RegardlessOfCustomerIdOrNullConversationId()
    {
        var businessId = await SeedBusinessAsync(withActiveIntegration: true);
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var lookup = new FakeShopifyProductLookupService
        {
            SearchResultFactory = () => new ProductSearchResult([]),
        };
        var tool = new SearchProductsTool(db, lookup);

        var result = await tool.ExecuteAsync(
            new ToolDispatchContext(businessId, Guid.NewGuid(), ConversationId: null, new CustomerReplyCorrelation()), Input("anything"), CancellationToken.None);

        Assert.False(result.IsError);
    }

    // ── Test doubles ──────────────────────────────────────────────────

    private sealed class FixedTenantAccessor(Guid? businessId) : ICurrentTenantAccessor
    {
        public Guid? BusinessId => businessId;
    }

    private sealed class FakeShopifyProductLookupService : IShopifyProductLookupService
    {
        public int GetProductByGidCallCount { get; private set; }
        public int GetProductBySkuCallCount { get; private set; }
        public int SearchProductsCallCount { get; private set; }

        public Func<ProductLookupResult>? GidResultFactory { get; set; }
        public Func<ProductLookupResult>? SkuResultFactory { get; set; }
        public Func<ProductSearchResult>? SearchResultFactory { get; set; }

        public Task<ProductLookupResult> GetProductByGidAsync(Guid businessId, string productGid, CancellationToken cancellationToken)
        {
            GetProductByGidCallCount++;
            return Task.FromResult(GidResultFactory!.Invoke());
        }

        public Task<ProductLookupResult> GetProductBySkuAsync(Guid businessId, string sku, CancellationToken cancellationToken)
        {
            GetProductBySkuCallCount++;
            return Task.FromResult(SkuResultFactory!.Invoke());
        }

        public Task<ProductSearchResult> SearchProductsAsync(Guid businessId, string query, CancellationToken cancellationToken)
        {
            SearchProductsCallCount++;
            return Task.FromResult(SearchResultFactory!.Invoke());
        }

        // Step Group C (getInventory) — not exercised by this suite (see
        // GetInventoryToolTests.cs); implemented only to satisfy the
        // interface after its extension (Owner Decision 3).
        public Task<InventoryLookupResult> GetInventoryByGidAsync(Guid businessId, string productGid, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<InventoryLookupResult> GetInventoryBySkuAsync(Guid businessId, string sku, CancellationToken cancellationToken) =>
            throw new NotImplementedException();
    }
}
