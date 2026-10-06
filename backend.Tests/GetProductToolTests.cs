using backend.Data;
using backend.Interfaces;
using backend.Models;
using backend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace backend.Tests;

/// <summary>
/// Step Group B — security/functional coverage for GetProductTool.ExecuteAsync
/// itself, using a fake IShopifyProductLookupService substitute so this
/// suite exercises GetProductTool's own sequencing/branching without a real
/// Shopify call. Real Postgres for the Business/Integration rows GetProductTool's
/// own db.Integrations query requires — same convention as GetOrderToolTests.
///
/// Unlike GetOrderToolTests, no Level2VerificationChecker/OwnershipAuthorizationService/
/// AuditLogService fakes are needed — this tool has none of those
/// dependencies (locked design §3, implementation design §1 item 2).
/// </summary>
public class GetProductToolTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;
    private readonly List<Guid> _businessIdsToClean = [];

    public Task InitializeAsync()
    {
        var configuration = new ConfigurationBuilder()
            .AddUserSecrets<Program>()
            .Build();

        var connectionUri = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection not found via user-secrets.");

        var services = new ServiceCollection()
            .AddSingleton<ICurrentTenantAccessor>(new FixedTenantAccessor(null))
            .AddDbContext<AppDbContext>(o => o.UseNpgsql(
                PostgresConnectionString.FromUri(connectionUri), x => x.UseVector()));
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
            Name = $"GetProductTool Test {Guid.NewGuid():N}",
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

    private static Dictionary<string, JsonElement> Input(string productIdentifier) => new()
    {
        ["productIdentifier"] = JsonSerializer.SerializeToElement(productIdentifier),
    };

    private static ProductSummaryResult SampleProduct(string id = "gid://shopify/Product/1") => new(
        id, "Blue Hoodie", "A comfy hoodie.",
        new ProductVariantSummary("HOODIE-BLU-M", "39.99", true, [new ProductVariantOptionValue("Size", "Medium")]));

    // ── Identifier branching ──────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_GidInput_CallsGetProductByGidAsync_NotSku()
    {
        var businessId = await SeedBusinessAsync(withActiveIntegration: true);
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var lookup = new FakeShopifyProductLookupService
        {
            GidResultFactory = () => new ProductLookupResult(ProductLookupOutcome.Resolved, SampleProduct()),
        };
        var tool = new GetProductTool(db, lookup);

        var result = await tool.ExecuteAsync(
            new ToolDispatchContext(businessId, Guid.NewGuid(), Guid.NewGuid(), new CustomerReplyCorrelation()),
            Input("gid://shopify/Product/1"), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(1, lookup.GetProductByGidCallCount);
        Assert.Equal(0, lookup.GetProductBySkuCallCount);
        Assert.Contains("Blue Hoodie", result.Content);
        Assert.Contains("$39.99", result.Content);
        Assert.Contains("HOODIE-BLU-M", result.Content);
        Assert.Contains("in stock", result.Content);
    }

    [Fact]
    public async Task ExecuteAsync_NonGidInput_CallsGetProductBySkuAsync_NotGid()
    {
        var businessId = await SeedBusinessAsync(withActiveIntegration: true);
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var lookup = new FakeShopifyProductLookupService
        {
            SkuResultFactory = () => new ProductLookupResult(ProductLookupOutcome.Resolved, SampleProduct()),
        };
        var tool = new GetProductTool(db, lookup);

        var result = await tool.ExecuteAsync(
            new ToolDispatchContext(businessId, Guid.NewGuid(), Guid.NewGuid(), new CustomerReplyCorrelation()),
            Input("HOODIE-BLU-M"), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(0, lookup.GetProductByGidCallCount);
        Assert.Equal(1, lookup.GetProductBySkuCallCount);
    }

    // ── Not found / ambiguous ─────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_NotFound_ReturnsRefusal_NotAnException()
    {
        var businessId = await SeedBusinessAsync(withActiveIntegration: true);
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var lookup = new FakeShopifyProductLookupService
        {
            SkuResultFactory = () => new ProductLookupResult(ProductLookupOutcome.NotFound, null),
        };
        var tool = new GetProductTool(db, lookup);

        var result = await tool.ExecuteAsync(
            new ToolDispatchContext(businessId, Guid.NewGuid(), Guid.NewGuid(), new CustomerReplyCorrelation()), Input("NO-SUCH-SKU"), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("I can't find a product matching that.", result.Content);
    }

    [Fact]
    public async Task ExecuteAsync_Ambiguous_ReturnsNarrowDownRefusal()
    {
        var businessId = await SeedBusinessAsync(withActiveIntegration: true);
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var lookup = new FakeShopifyProductLookupService
        {
            SkuResultFactory = () => new ProductLookupResult(ProductLookupOutcome.Ambiguous, null),
        };
        var tool = new GetProductTool(db, lookup);

        var result = await tool.ExecuteAsync(
            new ToolDispatchContext(businessId, Guid.NewGuid(), Guid.NewGuid(), new CustomerReplyCorrelation()), Input("DUP-SKU"), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("more than one product", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    // ── Infra / auth failure ───────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_NoActiveIntegration_ReturnsInfraRefusal_NeverCallsLookup()
    {
        var businessId = await SeedBusinessAsync(withActiveIntegration: false);
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var lookup = new FakeShopifyProductLookupService();
        var tool = new GetProductTool(db, lookup);

        var result = await tool.ExecuteAsync(
            new ToolDispatchContext(businessId, Guid.NewGuid(), Guid.NewGuid(), new CustomerReplyCorrelation()), Input("gid://shopify/Product/1"), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("trouble reaching", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, lookup.GetProductByGidCallCount);
        Assert.Equal(0, lookup.GetProductBySkuCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_ShopifyAuthFailure_ReturnsInfraRefusal_NotGenericErrorText()
    {
        var businessId = await SeedBusinessAsync(withActiveIntegration: true);
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var lookup = new FakeShopifyProductLookupService
        {
            SkuResultFactory = () => throw new ShopifyAuthFailureException(),
        };
        var tool = new GetProductTool(db, lookup);

        var result = await tool.ExecuteAsync(
            new ToolDispatchContext(businessId, Guid.NewGuid(), Guid.NewGuid(), new CustomerReplyCorrelation()), Input("ABC-123"), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("trouble reaching", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("internal error", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    // ── Malformed input ────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_MissingProductIdentifier_ReturnsError()
    {
        var businessId = await SeedBusinessAsync(withActiveIntegration: true);
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var tool = new GetProductTool(db, new FakeShopifyProductLookupService());

        var result = await tool.ExecuteAsync(
            new ToolDispatchContext(businessId, Guid.NewGuid(), Guid.NewGuid(), new CustomerReplyCorrelation()),
            new Dictionary<string, JsonElement>(), CancellationToken.None);

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task ExecuteAsync_BlankProductIdentifier_ReturnsError()
    {
        var businessId = await SeedBusinessAsync(withActiveIntegration: true);
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var tool = new GetProductTool(db, new FakeShopifyProductLookupService());

        var result = await tool.ExecuteAsync(
            new ToolDispatchContext(businessId, Guid.NewGuid(), Guid.NewGuid(), new CustomerReplyCorrelation()), Input("   "), CancellationToken.None);

        Assert.True(result.IsError);
    }

    // ── No customer-ownership dimension (locked design §3) ────────────
    //
    // Catalog reads are merchant-owned, not customer-owned — unlike
    // GetOrderTool, this tool must succeed identically regardless of which
    // CustomerId (or ConversationId) is on the dispatch context, since no
    // ownership/Level2 check exists to consult either value.

    [Fact]
    public async Task ExecuteAsync_Succeeds_RegardlessOfCustomerIdOrNullConversationId()
    {
        var businessId = await SeedBusinessAsync(withActiveIntegration: true);
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var lookup = new FakeShopifyProductLookupService
        {
            GidResultFactory = () => new ProductLookupResult(ProductLookupOutcome.Resolved, SampleProduct()),
        };
        var tool = new GetProductTool(db, lookup);

        // No Level 2 verification, no ConversationId requirement — an
        // arbitrary CustomerId and a null ConversationId must both succeed,
        // unlike GetOrderTool's Group A refusal for the identical inputs.
        var result = await tool.ExecuteAsync(
            new ToolDispatchContext(businessId, Guid.NewGuid(), ConversationId: null, new CustomerReplyCorrelation()),
            Input("gid://shopify/Product/1"), CancellationToken.None);

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
