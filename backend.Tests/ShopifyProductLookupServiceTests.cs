using System.Net;
using System.Text;
using System.Text.Json;
using backend.Data;
using backend.Interfaces;
using backend.Models;
using backend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace backend.Tests;

/// <summary>
/// Service-level tests for ShopifyProductLookupService — real Postgres for
/// the Integration/Business rows (same convention as
/// ShopifyOrderLookupServiceTests: each test opens its own scope via
/// _provider.CreateAsyncScope()), FakeShopifyHttpMessageHandler for the
/// outbound Shopify call.
/// </summary>
public class ShopifyProductLookupServiceTests : IAsyncLifetime
{
    private const string AdminApiVersion = "2026-07";
    private readonly string Shop = $"test-shop-{Guid.NewGuid():N}";
    private const string AccessToken = "test-access-token";

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
        services.AddDataProtection();
        services.AddScoped<IIntegrationTokenProtector, IntegrationTokenProtector>();
        _provider = services.BuildServiceProvider();

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_businessIdsToClean.Count > 0)
        {
            await using var scope = _provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var ids = _businessIdsToClean.ToArray();

            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Integrations\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Businesses\" WHERE \"Id\" = ANY({ids})");
        }

        await _provider.DisposeAsync();
    }

    private async Task<Guid> CreateBusinessWithIntegrationAsync(
        bool isActive = true, string? externalAccountIdentifier = null, string? encryptedAccessTokenOverride = null,
        string? plaintextAccessTokenOverride = null)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tokenProtector = scope.ServiceProvider.GetRequiredService<IIntegrationTokenProtector>();

        var business = new Business
        {
            Id = Guid.NewGuid(),
            Name = $"Shopify Product Lookup Test {Guid.NewGuid():N}",
            TimeZone = "Europe/Copenhagen",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Businesses.Add(business);

        db.Integrations.Add(new Integration
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id,
            Platform = ShopifyIntegrationOAuthProvider.PlatformName,
            EncryptedAccessToken = encryptedAccessTokenOverride
                ?? tokenProtector.Protect(ShopifyIntegrationOAuthProvider.PlatformName, plaintextAccessTokenOverride ?? AccessToken),
            ExternalAccountIdentifier = externalAccountIdentifier ?? Shop,
            IsActive = isActive,
            ConnectedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync();
        _businessIdsToClean.Add(business.Id);
        return business.Id;
    }

    private static ShopifyProductLookupService NewService(
        IServiceProvider scopedProvider, Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(
            new HttpClient(new FakeShopifyHttpMessageHandler(respond)),
            scopedProvider.GetRequiredService<AppDbContext>(),
            scopedProvider.GetRequiredService<IIntegrationTokenProtector>(),
            Options.Create(new ShopifyOptions
            {
                ClientId = "unused", ClientSecret = "unused", RedirectUri = "unused", AdminApiVersion = AdminApiVersion,
            }));

    private static ShopifyProductLookupService NewServiceWithHandler(
        IServiceProvider scopedProvider, FakeShopifyHttpMessageHandler handler) =>
        new(
            new HttpClient(handler),
            scopedProvider.GetRequiredService<AppDbContext>(),
            scopedProvider.GetRequiredService<IIntegrationTokenProtector>(),
            Options.Create(new ShopifyOptions
            {
                ClientId = "unused", ClientSecret = "unused", RedirectUri = "unused", AdminApiVersion = AdminApiVersion,
            }));

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string VariantNodeJson(string sku, bool availableForSale = true, string amount = "9.99") => $$"""
        { "sku": "{{sku}}", "availableForSale": {{(availableForSale ? "true" : "false")}},
          "price": { "amount": "{{amount}}" },
          "selectedOptions": [ { "name": "Size", "value": "Medium" }, { "name": "Color", "value": "Blue" } ] }
        """;

    // ── GetProductByGidAsync — success cases ─────────────────────────────

    [Fact]
    public async Task GetProductByGidAsync_ReturnsResolved_WithMappedVariant_OnSuccessfulResponse()
    {
        var businessId = await CreateBusinessWithIntegrationAsync();
        const string productId = "gid://shopify/Product/1";
        var json = $$"""
            { "data": { "product": { "id": "{{productId}}", "title": "Blue Hoodie", "description": "A comfy hoodie.",
              "variants": { "edges": [ { "node": {{VariantNodeJson("HOODIE-BLU-M")}} } ] } } } }
            """;

        await using var scope = _provider.CreateAsyncScope();
        var service = NewService(scope.ServiceProvider, _ => JsonResponse(json));

        var result = await service.GetProductByGidAsync(businessId, productId, CancellationToken.None);

        Assert.Equal(ProductLookupOutcome.Resolved, result.Outcome);
        Assert.NotNull(result.Product);
        Assert.Equal(productId, result.Product!.Id);
        Assert.Equal("Blue Hoodie", result.Product.Title);
        Assert.Equal("A comfy hoodie.", result.Product.Description);
        Assert.NotNull(result.Product.Variant);
        Assert.Equal("HOODIE-BLU-M", result.Product.Variant!.Sku);
        Assert.Equal("9.99", result.Product.Variant.Price);
        Assert.True(result.Product.Variant.AvailableForSale);
        Assert.Equal(2, result.Product.Variant.Options.Count);
        Assert.Contains(result.Product.Variant.Options, o => o.Name == "Size" && o.Value == "Medium");
    }

    [Fact]
    public async Task GetProductByGidAsync_ReturnsResolved_WithNullVariant_WhenProductHasNoVariants()
    {
        var businessId = await CreateBusinessWithIntegrationAsync();
        const string productId = "gid://shopify/Product/1";
        var json = $$"""
            { "data": { "product": { "id": "{{productId}}", "title": "No Variants", "description": null,
              "variants": { "edges": [] } } } }
            """;

        await using var scope = _provider.CreateAsyncScope();
        var service = NewService(scope.ServiceProvider, _ => JsonResponse(json));

        var result = await service.GetProductByGidAsync(businessId, productId, CancellationToken.None);

        Assert.Equal(ProductLookupOutcome.Resolved, result.Outcome);
        Assert.Null(result.Product!.Variant);
    }

    [Fact]
    public async Task GetProductByGidAsync_SendsAccessTokenHeaderAndVersionedUrl()
    {
        var businessId = await CreateBusinessWithIntegrationAsync();
        HttpRequestMessage? capturedRequest = null;

        await using var scope = _provider.CreateAsyncScope();
        var service = NewService(scope.ServiceProvider, req =>
        {
            capturedRequest = req;
            return JsonResponse("""{ "data": { "product": null } }""");
        });

        await service.GetProductByGidAsync(businessId, "gid://shopify/Product/1", CancellationToken.None);

        Assert.NotNull(capturedRequest);
        Assert.Equal(
            $"https://{Shop}.myshopify.com/admin/api/{AdminApiVersion}/graphql.json",
            capturedRequest!.RequestUri!.ToString());
        Assert.Equal(AccessToken, capturedRequest.Headers.GetValues("X-Shopify-Access-Token").Single());
    }

    // ── GetProductByGidAsync — not found (ordinary outcome, not thrown) ──

    [Fact]
    public async Task GetProductByGidAsync_ReturnsNotFound_WhenProductIsNull()
    {
        var businessId = await CreateBusinessWithIntegrationAsync();

        await using var scope = _provider.CreateAsyncScope();
        var service = NewService(scope.ServiceProvider, _ => JsonResponse("""{ "data": { "product": null } }"""));

        var result = await service.GetProductByGidAsync(businessId, "gid://shopify/Product/999", CancellationToken.None);

        Assert.Equal(ProductLookupOutcome.NotFound, result.Outcome);
        Assert.Null(result.Product);
    }

    // ── GetProductByGidAsync — failure cases — must throw ────────────────

    [Fact]
    public async Task GetProductByGidAsync_Throws_WhenIntegrationMissing()
    {
        await using var scope = _provider.CreateAsyncScope();
        var service = NewService(scope.ServiceProvider, _ => JsonResponse("""{ "data": { "product": null } }"""));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.GetProductByGidAsync(Guid.NewGuid(), "gid://shopify/Product/1", CancellationToken.None));
    }

    [Fact]
    public async Task GetProductByGidAsync_Throws_WhenIntegrationInactive()
    {
        var businessId = await CreateBusinessWithIntegrationAsync(isActive: false);

        await using var scope = _provider.CreateAsyncScope();
        var service = NewService(scope.ServiceProvider, _ => JsonResponse("""{ "data": { "product": null } }"""));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.GetProductByGidAsync(businessId, "gid://shopify/Product/1", CancellationToken.None));
    }

    [Fact]
    public async Task GetProductByGidAsync_Throws_WhenAccessTokenCannotBeDecrypted()
    {
        var businessId = await CreateBusinessWithIntegrationAsync(encryptedAccessTokenOverride: "not-a-valid-protected-payload");

        await using var scope = _provider.CreateAsyncScope();
        var service = NewService(scope.ServiceProvider, _ => JsonResponse("""{ "data": { "product": null } }"""));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.GetProductByGidAsync(businessId, "gid://shopify/Product/1", CancellationToken.None));
    }

    [Fact]
    public async Task GetProductByGidAsync_Throws_OnGraphQlErrors()
    {
        var businessId = await CreateBusinessWithIntegrationAsync();

        await using var scope = _provider.CreateAsyncScope();
        var service = NewService(scope.ServiceProvider, _ => JsonResponse(
            """{ "errors": [ { "message": "Field 'product' doesn't exist" } ] }"""));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.GetProductByGidAsync(businessId, "gid://shopify/Product/1", CancellationToken.None));
    }

    [Fact]
    public async Task GetProductByGidAsync_Throws_OnMalformedJson()
    {
        var businessId = await CreateBusinessWithIntegrationAsync();

        await using var scope = _provider.CreateAsyncScope();
        var service = NewService(scope.ServiceProvider, _ => JsonResponse("this is not json"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.GetProductByGidAsync(businessId, "gid://shopify/Product/1", CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task GetProductByGidAsync_Throws_InvalidOperationException_OnOtherNonSuccessStatus(HttpStatusCode status)
    {
        var businessId = await CreateBusinessWithIntegrationAsync();

        await using var scope = _provider.CreateAsyncScope();
        var service = NewService(scope.ServiceProvider, _ => new HttpResponseMessage(status));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.GetProductByGidAsync(businessId, "gid://shopify/Product/1", CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task GetProductByGidAsync_Throws_ShopifyAuthFailureException_AndSetsReauthorizationRequiredAt(HttpStatusCode status)
    {
        var businessId = await CreateBusinessWithIntegrationAsync();

        await using var scope = _provider.CreateAsyncScope();
        var service = NewService(scope.ServiceProvider, _ => new HttpResponseMessage(status));

        await Assert.ThrowsAsync<ShopifyAuthFailureException>(
            () => service.GetProductByGidAsync(businessId, "gid://shopify/Product/1", CancellationToken.None));

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var integration = await db.Integrations.IgnoreQueryFilters().AsNoTracking().SingleAsync(i => i.BusinessId == businessId);
        Assert.NotNull(integration.ReauthorizationRequiredAt);
    }

    // ── GetProductBySkuAsync — resolved/not-found/ambiguous ──────────────

    [Fact]
    public async Task GetProductBySkuAsync_ReturnsResolved_OnExactlyOneMatch()
    {
        var businessId = await CreateBusinessWithIntegrationAsync();
        var json = $$"""
            { "data": { "productVariants": { "edges": [
              { "node": { "sku": "SKU-1", "availableForSale": true, "price": { "amount": "19.99" },
                "selectedOptions": [ { "name": "Size", "value": "Large" } ],
                "product": { "id": "gid://shopify/Product/1", "title": "Red Shirt", "description": "A shirt." } } }
            ] } } }
            """;

        await using var scope = _provider.CreateAsyncScope();
        var service = NewService(scope.ServiceProvider, _ => JsonResponse(json));

        var result = await service.GetProductBySkuAsync(businessId, "SKU-1", CancellationToken.None);

        Assert.Equal(ProductLookupOutcome.Resolved, result.Outcome);
        Assert.Equal("gid://shopify/Product/1", result.Product!.Id);
        Assert.Equal("Red Shirt", result.Product.Title);
        Assert.Equal("SKU-1", result.Product.Variant!.Sku);
        Assert.Equal("19.99", result.Product.Variant.Price);
    }

    [Fact]
    public async Task GetProductBySkuAsync_ReturnsNotFound_OnZeroMatches()
    {
        var businessId = await CreateBusinessWithIntegrationAsync();
        await using var scope = _provider.CreateAsyncScope();
        var service = NewService(scope.ServiceProvider, _ => JsonResponse(
            """{ "data": { "productVariants": { "edges": [] } } }"""));

        var result = await service.GetProductBySkuAsync(businessId, "NO-SUCH-SKU", CancellationToken.None);

        Assert.Equal(ProductLookupOutcome.NotFound, result.Outcome);
        Assert.Null(result.Product);
    }

    /// <summary>
    /// Mandatory per implementation design's Decision 3/NEW OWNER DECISION 3
    /// (§2.1/§11.1) — this is the exact scenario productVariants(query:
    /// "sku:...") was chosen over products(query: "sku:...") to correctly
    /// detect: two variants of the SAME product sharing a literal SKU. A
    /// products(query:...) approach would have collapsed this to a single
    /// product edge, silently missing the ambiguity; productVariants(...)
    /// counts matching variants directly, so this correctly resolves to
    /// Ambiguous.
    /// </summary>
    [Fact]
    public async Task GetProductBySkuAsync_ReturnsAmbiguous_WhenTwoVariantsOfTheSameProductShareASku()
    {
        var businessId = await CreateBusinessWithIntegrationAsync();
        const string sameProductId = "gid://shopify/Product/1";
        var json = $$"""
            { "data": { "productVariants": { "edges": [
              { "node": { "sku": "DUP-SKU", "availableForSale": true, "price": { "amount": "10.00" },
                "selectedOptions": [ { "name": "Color", "value": "Red" } ],
                "product": { "id": "{{sameProductId}}", "title": "Shirt", "description": null } } },
              { "node": { "sku": "DUP-SKU", "availableForSale": false, "price": { "amount": "10.00" },
                "selectedOptions": [ { "name": "Color", "value": "Blue" } ],
                "product": { "id": "{{sameProductId}}", "title": "Shirt", "description": null } } }
            ] } } }
            """;

        await using var scope = _provider.CreateAsyncScope();
        var service = NewService(scope.ServiceProvider, _ => JsonResponse(json));

        var result = await service.GetProductBySkuAsync(businessId, "DUP-SKU", CancellationToken.None);

        Assert.Equal(ProductLookupOutcome.Ambiguous, result.Outcome);
        Assert.Null(result.Product);
    }

    /// <summary>
    /// The other ambiguity shape — two DIFFERENT products' variants sharing
    /// a SKU (a messier merchant-catalog data pattern). productVariants(...)
    /// detects this uniformly with the same-product case above, via the
    /// same edges.Count > 1 check — no special-casing needed.
    /// </summary>
    [Fact]
    public async Task GetProductBySkuAsync_ReturnsAmbiguous_WhenTwoDifferentProductsShareASku()
    {
        var businessId = await CreateBusinessWithIntegrationAsync();
        var json = $$"""
            { "data": { "productVariants": { "edges": [
              { "node": { "sku": "DUP-SKU", "availableForSale": true, "price": { "amount": "10.00" },
                "selectedOptions": [], "product": { "id": "gid://shopify/Product/1", "title": "Shirt", "description": null } } },
              { "node": { "sku": "DUP-SKU", "availableForSale": true, "price": { "amount": "12.00" },
                "selectedOptions": [], "product": { "id": "gid://shopify/Product/2", "title": "Hat", "description": null } } }
            ] } } }
            """;

        await using var scope = _provider.CreateAsyncScope();
        var service = NewService(scope.ServiceProvider, _ => JsonResponse(json));

        var result = await service.GetProductBySkuAsync(businessId, "DUP-SKU", CancellationToken.None);

        Assert.Equal(ProductLookupOutcome.Ambiguous, result.Outcome);
        Assert.Null(result.Product);
    }

    [Fact]
    public async Task GetProductBySkuAsync_SendsTheRawValueUnmodified_PrefixedWithSku()
    {
        var businessId = await CreateBusinessWithIntegrationAsync();
        await using var scope = _provider.CreateAsyncScope();
        string? capturedBody = null;
        var service = NewService(scope.ServiceProvider, req =>
        {
            capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse("""{ "data": { "productVariants": { "edges": [] } } }""");
        });

        await service.GetProductBySkuAsync(businessId, "ABC-123", CancellationToken.None);

        using var json = JsonDocument.Parse(capturedBody!);
        Assert.Equal("sku:ABC-123", json.RootElement.GetProperty("variables").GetProperty("query").GetString());
    }

    [Fact]
    public async Task GetProductBySkuAsync_RequestsAtMostTwo_ToDistinguishOneFromMoreThanOne()
    {
        var businessId = await CreateBusinessWithIntegrationAsync();
        await using var scope = _provider.CreateAsyncScope();
        string? capturedBody = null;
        var service = NewService(scope.ServiceProvider, req =>
        {
            capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse("""{ "data": { "productVariants": { "edges": [] } } }""");
        });

        await service.GetProductBySkuAsync(businessId, "ABC-123", CancellationToken.None);

        Assert.Contains("productVariants(first: 2", capturedBody);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task GetProductBySkuAsync_Throws_ShopifyAuthFailureException_AndSetsReauthorizationRequiredAt(HttpStatusCode status)
    {
        var businessId = await CreateBusinessWithIntegrationAsync();
        await using var scope = _provider.CreateAsyncScope();
        var service = NewService(scope.ServiceProvider, _ => new HttpResponseMessage(status));

        await Assert.ThrowsAsync<ShopifyAuthFailureException>(
            () => service.GetProductBySkuAsync(businessId, "ABC-123", CancellationToken.None));

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var integration = await db.Integrations.IgnoreQueryFilters().AsNoTracking().SingleAsync(i => i.BusinessId == businessId);
        Assert.NotNull(integration.ReauthorizationRequiredAt);
    }

    [Fact]
    public async Task GetProductBySkuAsync_Throws_InvalidOperationException_OnOtherNonSuccessStatus()
    {
        var businessId = await CreateBusinessWithIntegrationAsync();
        await using var scope = _provider.CreateAsyncScope();
        var service = NewService(scope.ServiceProvider, _ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.GetProductBySkuAsync(businessId, "ABC-123", CancellationToken.None));
    }

    [Fact]
    public async Task GetProductBySkuAsync_Throws_OnMalformedJson()
    {
        var businessId = await CreateBusinessWithIntegrationAsync();
        await using var scope = _provider.CreateAsyncScope();
        var service = NewService(scope.ServiceProvider, _ => JsonResponse("this is not json"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.GetProductBySkuAsync(businessId, "ABC-123", CancellationToken.None));
    }

    [Fact]
    public async Task GetProductBySkuAsync_Throws_OnGraphQlErrors()
    {
        var businessId = await CreateBusinessWithIntegrationAsync();
        await using var scope = _provider.CreateAsyncScope();
        var service = NewService(scope.ServiceProvider, _ => JsonResponse(
            """{ "errors": [ { "message": "Field 'productVariants' doesn't exist" } ] }"""));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.GetProductBySkuAsync(businessId, "ABC-123", CancellationToken.None));
    }

    // ── SearchProductsAsync ───────────────────────────────────────────────

    [Fact]
    public async Task SearchProductsAsync_ReturnsMappedProducts_OnResults()
    {
        var businessId = await CreateBusinessWithIntegrationAsync();
        var json = $$"""
            { "data": { "products": { "edges": [
              { "node": { "id": "gid://shopify/Product/1", "title": "Blue Hoodie", "description": "Comfy.",
                "variants": { "edges": [ { "node": {{VariantNodeJson("HOODIE-BLU-M")}} } ] } } },
              { "node": { "id": "gid://shopify/Product/2", "title": "Red Hat", "description": null,
                "variants": { "edges": [] } } }
            ] } } }
            """;

        await using var scope = _provider.CreateAsyncScope();
        var service = NewService(scope.ServiceProvider, _ => JsonResponse(json));

        var result = await service.SearchProductsAsync(businessId, "hoodie", CancellationToken.None);

        Assert.Equal(2, result.Products.Count);
        var first = result.Products[0];
        Assert.Equal("Blue Hoodie", first.Title);
        Assert.NotNull(first.Variant);
        Assert.Equal("HOODIE-BLU-M", first.Variant!.Sku);
        var second = result.Products[1];
        Assert.Equal("Red Hat", second.Title);
        Assert.Null(second.Variant);
    }

    [Fact]
    public async Task SearchProductsAsync_ReturnsEmptyList_AsOrdinarySuccess_OnZeroMatches()
    {
        var businessId = await CreateBusinessWithIntegrationAsync();
        await using var scope = _provider.CreateAsyncScope();
        var service = NewService(scope.ServiceProvider, _ => JsonResponse(
            """{ "data": { "products": { "edges": [] } } }"""));

        var result = await service.SearchProductsAsync(businessId, "nonexistent", CancellationToken.None);

        Assert.Empty(result.Products);
    }

    [Fact]
    public async Task SearchProductsAsync_RequestsTheDocumentedResultCap_NotAiControlled()
    {
        var businessId = await CreateBusinessWithIntegrationAsync();
        await using var scope = _provider.CreateAsyncScope();
        string? capturedBody = null;
        var service = NewService(scope.ServiceProvider, req =>
        {
            capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse("""{ "data": { "products": { "edges": [] } } }""");
        });

        await service.SearchProductsAsync(businessId, "anything", CancellationToken.None);

        using var json = JsonDocument.Parse(capturedBody!);
        Assert.Equal(10, json.RootElement.GetProperty("variables").GetProperty("first").GetInt32());
        // No AI-visible pagination state — no `after`/cursor argument is
        // ever sent, matching design §6's "AI never controls a cursor".
        Assert.False(json.RootElement.GetProperty("variables").TryGetProperty("after", out _));
    }

    [Fact]
    public async Task SearchProductsAsync_SendsTheRawQueryUnmodified()
    {
        var businessId = await CreateBusinessWithIntegrationAsync();
        await using var scope = _provider.CreateAsyncScope();
        string? capturedBody = null;
        var service = NewService(scope.ServiceProvider, req =>
        {
            capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse("""{ "data": { "products": { "edges": [] } } }""");
        });

        await service.SearchProductsAsync(businessId, "blue hoodie", CancellationToken.None);

        using var json = JsonDocument.Parse(capturedBody!);
        Assert.Equal("blue hoodie", json.RootElement.GetProperty("variables").GetProperty("query").GetString());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task SearchProductsAsync_Throws_ShopifyAuthFailureException_AndSetsReauthorizationRequiredAt(HttpStatusCode status)
    {
        var businessId = await CreateBusinessWithIntegrationAsync();
        await using var scope = _provider.CreateAsyncScope();
        var service = NewService(scope.ServiceProvider, _ => new HttpResponseMessage(status));

        await Assert.ThrowsAsync<ShopifyAuthFailureException>(
            () => service.SearchProductsAsync(businessId, "anything", CancellationToken.None));

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var integration = await db.Integrations.IgnoreQueryFilters().AsNoTracking().SingleAsync(i => i.BusinessId == businessId);
        Assert.NotNull(integration.ReauthorizationRequiredAt);
    }

    [Fact]
    public async Task SearchProductsAsync_Throws_InvalidOperationException_OnOtherNonSuccessStatus()
    {
        var businessId = await CreateBusinessWithIntegrationAsync();
        await using var scope = _provider.CreateAsyncScope();
        var service = NewService(scope.ServiceProvider, _ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SearchProductsAsync(businessId, "anything", CancellationToken.None));
    }

    [Fact]
    public async Task SearchProductsAsync_Throws_OnMalformedJson()
    {
        var businessId = await CreateBusinessWithIntegrationAsync();
        await using var scope = _provider.CreateAsyncScope();
        var service = NewService(scope.ServiceProvider, _ => JsonResponse("this is not json"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SearchProductsAsync(businessId, "anything", CancellationToken.None));
    }

    [Fact]
    public async Task SearchProductsAsync_Throws_OnGraphQlErrors()
    {
        var businessId = await CreateBusinessWithIntegrationAsync();
        await using var scope = _provider.CreateAsyncScope();
        var service = NewService(scope.ServiceProvider, _ => JsonResponse(
            """{ "errors": [ { "message": "Field 'products' doesn't exist" } ] }"""));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SearchProductsAsync(businessId, "anything", CancellationToken.None));
    }

    // ── Cross-tenant BusinessId→Integration selection (all three methods) ──
    //
    // Proves Nivora's own BusinessId-scoped Integration selection is
    // correct when more than one business's Integration row exists, not
    // merely that Shopify's own token-scoping would separately prevent
    // cross-tenant data — mirrors ShopifyOrderLookupServiceTests' own
    // established pattern for the identical reason: a single-integration
    // fixture can't distinguish "the right row was selected" from "the only
    // row was selected."

    [Fact]
    public async Task GetProductByGidAsync_UsesTheRequestedBusinesssOwnIntegration_NotAnotherBusinesss()
    {
        var shopA = $"shop-a-{Guid.NewGuid():N}";
        var shopB = $"shop-b-{Guid.NewGuid():N}";
        var businessAId = await CreateBusinessWithIntegrationAsync(
            externalAccountIdentifier: shopA, plaintextAccessTokenOverride: "token-for-business-a");
        var businessBId = await CreateBusinessWithIntegrationAsync(
            externalAccountIdentifier: shopB, plaintextAccessTokenOverride: "token-for-business-b");

        await using var scope = _provider.CreateAsyncScope();
        var handler = new FakeShopifyHttpMessageHandler(_ => JsonResponse("""{ "data": { "product": null } }"""));
        var service = NewServiceWithHandler(scope.ServiceProvider, handler);

        await service.GetProductByGidAsync(businessAId, "gid://shopify/Product/1", CancellationToken.None);

        Assert.Equal(
            $"https://{shopA}.myshopify.com/admin/api/{AdminApiVersion}/graphql.json",
            handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal("token-for-business-a", handler.LastRequest.Headers.GetValues("X-Shopify-Access-Token").Single());
        Assert.NotEqual(businessAId, businessBId);
    }

    [Fact]
    public async Task SearchProductsAsync_UsesTheRequestedBusinesssOwnIntegration_NotAnotherBusinesss()
    {
        var shopA = $"shop-a-{Guid.NewGuid():N}";
        var shopB = $"shop-b-{Guid.NewGuid():N}";
        var businessAId = await CreateBusinessWithIntegrationAsync(
            externalAccountIdentifier: shopA, plaintextAccessTokenOverride: "token-for-business-a");
        var businessBId = await CreateBusinessWithIntegrationAsync(
            externalAccountIdentifier: shopB, plaintextAccessTokenOverride: "token-for-business-b");

        await using var scope = _provider.CreateAsyncScope();
        var handler = new FakeShopifyHttpMessageHandler(_ => JsonResponse("""{ "data": { "products": { "edges": [] } } }"""));
        var service = NewServiceWithHandler(scope.ServiceProvider, handler);

        await service.SearchProductsAsync(businessAId, "anything", CancellationToken.None);

        Assert.Equal(
            $"https://{shopA}.myshopify.com/admin/api/{AdminApiVersion}/graphql.json",
            handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal("token-for-business-a", handler.LastRequest.Headers.GetValues("X-Shopify-Access-Token").Single());
        Assert.NotEqual(businessAId, businessBId);
    }

    private class FixedTenantAccessor(Guid? businessId) : ICurrentTenantAccessor
    {
        public Guid? BusinessId => businessId;
    }
}
