using backend.Data;
using backend.Interfaces;
using backend.Models;
using backend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace backend.Tests;

/// <summary>
/// Step Group C — security/functional coverage for
/// GetInventoryTool.ExecuteAsync itself, using a fake
/// IShopifyProductLookupService substitute so this suite exercises
/// GetInventoryTool's own sequencing/branching without a real Shopify
/// call. Real Postgres for the Business/Integration rows the tool's own
/// db.Integrations query requires — same convention as GetProductToolTests.
///
/// No Level2VerificationChecker/OwnershipAuthorizationService/
/// AuditLogService fakes are needed — this tool has none of those
/// dependencies (implementation design §1.1/§5.3).
/// </summary>
public class GetInventoryToolTests : IAsyncLifetime
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
            Name = $"GetInventoryTool Test {Guid.NewGuid():N}",
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

    private static InventoryAvailabilityResult SampleAvailability(bool inStock) => new(
        "gid://shopify/Product/1", "Blue Hoodie", "HOODIE-BLU-M",
        [new ProductVariantOptionValue("Size", "Medium")], inStock);

    // ── Identifier branching ──────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_GidInput_CallsGetInventoryByGidAsync_NotSku()
    {
        var businessId = await SeedBusinessAsync(withActiveIntegration: true);
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var lookup = new FakeShopifyProductLookupService
        {
            GidInventoryResultFactory = () => new InventoryLookupResult(InventoryLookupOutcome.Resolved, SampleAvailability(true)),
        };
        var tool = new GetInventoryTool(db, lookup);

        var result = await tool.ExecuteAsync(
            new ToolDispatchContext(businessId, Guid.NewGuid(), Guid.NewGuid(), new CustomerReplyCorrelation()),
            Input("gid://shopify/Product/1"), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(1, lookup.GetInventoryByGidCallCount);
        Assert.Equal(0, lookup.GetInventoryBySkuCallCount);
        Assert.Contains("Blue Hoodie", result.Content);
        Assert.Contains("HOODIE-BLU-M", result.Content);
        Assert.Contains("in stock", result.Content);
    }

    [Fact]
    public async Task ExecuteAsync_NonGidInput_CallsGetInventoryBySkuAsync_NotGid()
    {
        var businessId = await SeedBusinessAsync(withActiveIntegration: true);
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var lookup = new FakeShopifyProductLookupService
        {
            SkuInventoryResultFactory = () => new InventoryLookupResult(InventoryLookupOutcome.Resolved, SampleAvailability(true)),
        };
        var tool = new GetInventoryTool(db, lookup);

        var result = await tool.ExecuteAsync(
            new ToolDispatchContext(businessId, Guid.NewGuid(), Guid.NewGuid(), new CustomerReplyCorrelation()),
            Input("HOODIE-BLU-M"), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(0, lookup.GetInventoryByGidCallCount);
        Assert.Equal(1, lookup.GetInventoryBySkuCallCount);
    }

    // ── Availability mapping (out of stock too — not just the success path) ─

    [Fact]
    public async Task ExecuteAsync_OutOfStock_ReturnsSuccess_NotARefusal_WithOutOfStockWording()
    {
        var businessId = await SeedBusinessAsync(withActiveIntegration: true);
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var lookup = new FakeShopifyProductLookupService
        {
            SkuInventoryResultFactory = () => new InventoryLookupResult(InventoryLookupOutcome.Resolved, SampleAvailability(false)),
        };
        var tool = new GetInventoryTool(db, lookup);

        var result = await tool.ExecuteAsync(
            new ToolDispatchContext(businessId, Guid.NewGuid(), Guid.NewGuid(), new CustomerReplyCorrelation()), Input("HOODIE-BLU-M"), CancellationToken.None);

        // Out of stock is an ordinary, successful lookup outcome — not a
        // refusal — the same "the answer can be no" discipline getProduct's
        // NotFound/Ambiguous split establishes for a different question.
        Assert.False(result.IsError);
        Assert.Contains("out of stock", result.Content);
        Assert.DoesNotContain("in stock", result.Content);
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
            SkuInventoryResultFactory = () => new InventoryLookupResult(InventoryLookupOutcome.NotFound, null),
        };
        var tool = new GetInventoryTool(db, lookup);

        var result = await tool.ExecuteAsync(
            new ToolDispatchContext(businessId, Guid.NewGuid(), Guid.NewGuid(), new CustomerReplyCorrelation()), Input("NO-SUCH-SKU"), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("I can't find inventory information for that.", result.Content);
    }

    [Fact]
    public async Task ExecuteAsync_AmbiguousSku_ReturnsNarrowDownRefusal()
    {
        var businessId = await SeedBusinessAsync(withActiveIntegration: true);
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var lookup = new FakeShopifyProductLookupService
        {
            SkuInventoryResultFactory = () => new InventoryLookupResult(InventoryLookupOutcome.Ambiguous, null),
        };
        var tool = new GetInventoryTool(db, lookup);

        var result = await tool.ExecuteAsync(
            new ToolDispatchContext(businessId, Guid.NewGuid(), Guid.NewGuid(), new CustomerReplyCorrelation()), Input("DUP-SKU"), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("more than one product", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Owner Decision 2 (design §4.3) — a multi-variant Product GID must
    /// refuse, not silently resolve a representative variant. This is the
    /// tool-level proof that GetInventoryTool actually surfaces the
    /// Ambiguous outcome as a refusal for the GID branch specifically, not
    /// just the SKU branch (which GetProductTool's own ambiguity precedent
    /// already covers).
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_AmbiguousGid_ReturnsNarrowDownRefusal_NeverAGuess()
    {
        var businessId = await SeedBusinessAsync(withActiveIntegration: true);
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var lookup = new FakeShopifyProductLookupService
        {
            GidInventoryResultFactory = () => new InventoryLookupResult(InventoryLookupOutcome.Ambiguous, null),
        };
        var tool = new GetInventoryTool(db, lookup);

        var result = await tool.ExecuteAsync(
            new ToolDispatchContext(businessId, Guid.NewGuid(), Guid.NewGuid(), new CustomerReplyCorrelation()),
            Input("gid://shopify/Product/1"), CancellationToken.None);

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
        var tool = new GetInventoryTool(db, lookup);

        var result = await tool.ExecuteAsync(
            new ToolDispatchContext(businessId, Guid.NewGuid(), Guid.NewGuid(), new CustomerReplyCorrelation()), Input("gid://shopify/Product/1"), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("trouble reaching", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, lookup.GetInventoryByGidCallCount);
        Assert.Equal(0, lookup.GetInventoryBySkuCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_ShopifyAuthFailure_ReturnsInfraRefusal_NotGenericErrorText()
    {
        var businessId = await SeedBusinessAsync(withActiveIntegration: true);
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var lookup = new FakeShopifyProductLookupService
        {
            SkuInventoryResultFactory = () => throw new ShopifyAuthFailureException(),
        };
        var tool = new GetInventoryTool(db, lookup);

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

        var tool = new GetInventoryTool(db, new FakeShopifyProductLookupService());

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

        var tool = new GetInventoryTool(db, new FakeShopifyProductLookupService());

        var result = await tool.ExecuteAsync(
            new ToolDispatchContext(businessId, Guid.NewGuid(), Guid.NewGuid(), new CustomerReplyCorrelation()), Input("   "), CancellationToken.None);

        Assert.True(result.IsError);
    }

    // ── No customer-ownership dimension ────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_Succeeds_RegardlessOfCustomerIdOrNullConversationId()
    {
        var businessId = await SeedBusinessAsync(withActiveIntegration: true);
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var lookup = new FakeShopifyProductLookupService
        {
            GidInventoryResultFactory = () => new InventoryLookupResult(InventoryLookupOutcome.Resolved, SampleAvailability(true)),
        };
        var tool = new GetInventoryTool(db, lookup);

        var result = await tool.ExecuteAsync(
            new ToolDispatchContext(businessId, Guid.NewGuid(), ConversationId: null, new CustomerReplyCorrelation()),
            Input("gid://shopify/Product/1"), CancellationToken.None);

        Assert.False(result.IsError);
    }

    // ── Locked v1 contract boundary test — no numeric quantity ever
    // reaches the AI-facing text, in either direction ──

    [Fact]
    public async Task ExecuteAsync_FormattedOutput_NeverContainsADigitBearingQuantitySubstring()
    {
        var businessId = await SeedBusinessAsync(withActiveIntegration: true);
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        foreach (var inStock in new[] { true, false })
        {
            var lookup = new FakeShopifyProductLookupService
            {
                GidInventoryResultFactory = () => new InventoryLookupResult(InventoryLookupOutcome.Resolved, SampleAvailability(inStock)),
            };
            var tool = new GetInventoryTool(db, lookup);

            var result = await tool.ExecuteAsync(
                new ToolDispatchContext(businessId, Guid.NewGuid(), Guid.NewGuid(), new CustomerReplyCorrelation()),
                Input("gid://shopify/Product/1"), CancellationToken.None);

            Assert.False(result.IsError);
            // No digit anywhere in the formatted reply — the locked v1
            // contract (design §0.1/§5.1) exposes boolean availability
            // only; this is the boundary test enforcing it at the point
            // the AI actually sees the text, not just at the DTO-shape
            // level (a passing DTO-shape check alone couldn't catch a
            // future formatting change that accidentally interpolated a
            // quantity into the reply string).
            Assert.DoesNotMatch(new Regex(@"\d"), result.Content);
            Assert.Contains(inStock ? "in stock" : "out of stock", result.Content);
        }
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
        public int GetInventoryByGidCallCount { get; private set; }
        public int GetInventoryBySkuCallCount { get; private set; }

        public Func<ProductLookupResult>? GidResultFactory { get; set; }
        public Func<ProductLookupResult>? SkuResultFactory { get; set; }
        public Func<ProductSearchResult>? SearchResultFactory { get; set; }
        public Func<InventoryLookupResult>? GidInventoryResultFactory { get; set; }
        public Func<InventoryLookupResult>? SkuInventoryResultFactory { get; set; }

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

        public Task<InventoryLookupResult> GetInventoryByGidAsync(Guid businessId, string productGid, CancellationToken cancellationToken)
        {
            GetInventoryByGidCallCount++;
            return Task.FromResult(GidInventoryResultFactory!.Invoke());
        }

        public Task<InventoryLookupResult> GetInventoryBySkuAsync(Guid businessId, string sku, CancellationToken cancellationToken)
        {
            GetInventoryBySkuCallCount++;
            return Task.FromResult(SkuInventoryResultFactory!.Invoke());
        }
    }
}
