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
/// Service-level tests for ShopifyRefundLookupService — design §8.3/§17,
/// research Q1/Q2/Q8. Real Postgres for Business/Integration rows,
/// FakeShopifyHttpMessageHandler for the outbound query.
/// </summary>
public class ShopifyRefundLookupServiceTests : IAsyncLifetime
{
    private const string AdminApiVersion = "2026-07";
    private const string AccessToken = "test-access-token";
    private const string OrderId = "gid://shopify/Order/1";

    private readonly string _shop = $"test-shop-{Guid.NewGuid():N}";

    private ServiceProvider _provider = null!;
    private readonly List<Guid> _businessIdsToClean = [];

    public Task InitializeAsync()
    {
        var configuration = new ConfigurationBuilder().AddUserSecrets<Program>().Build();
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

    private async Task<Guid> CreateBusinessWithIntegrationAsync(AppDbContext db, IServiceProvider scopedProvider)
    {
        var tokenProtector = scopedProvider.GetRequiredService<IIntegrationTokenProtector>();

        var business = new Business
        {
            Id = Guid.NewGuid(),
            Name = $"Refund Lookup Test {Guid.NewGuid():N}",
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
            EncryptedAccessToken = tokenProtector.Protect(ShopifyIntegrationOAuthProvider.PlatformName, AccessToken),
            ExternalAccountIdentifier = _shop,
            IsActive = true,
            ConnectedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync();
        _businessIdsToClean.Add(business.Id);
        return business.Id;
    }

    private static ShopifyRefundLookupService NewService(
        IServiceProvider scopedProvider, Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(
            new HttpClient(new FakeShopifyHttpMessageHandler(respond)),
            scopedProvider.GetRequiredService<AppDbContext>(),
            scopedProvider.GetRequiredService<IIntegrationTokenProtector>(),
            Options.Create(new ShopifyOptions
            {
                ClientId = "unused", ClientSecret = "unused", RedirectUri = "unused", AdminApiVersion = AdminApiVersion,
            }));

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string PreflightResponse(
        IEnumerable<(string Id, int RefundableQuantity)> lineItems,
        IEnumerable<(string Amount, string CurrencyCode)>? suggestedLines = null)
    {
        var edgesJson = string.Join(",", lineItems.Select(li =>
            $$"""{ "node": { "id": "{{li.Id}}", "refundableQuantity": {{li.RefundableQuantity}} } }"""));

        var suggested = suggestedLines?.ToList();
        var suggestedJson = suggested is null
            ? "null"
            : "{ \"refundLineItems\": [" + string.Join(",", suggested.Select(s =>
                $$"""{ "subtotalSet": { "presentmentMoney": { "amount": "{{s.Amount}}", "currencyCode": "{{s.CurrencyCode}}" } } }""")) + "] }";

        return $$"""
            { "data": { "order": { "id": "{{OrderId}}", "lineItems": { "edges": [{{edgesJson}}] }, "suggestedRefund": {{suggestedJson}} } } }
            """;
    }

    [Fact]
    public async Task GetRefundPreflightAsync_ReturnsRefundableQuantityAndResolvedAmount()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var businessId = await CreateBusinessWithIntegrationAsync(db, scope.ServiceProvider);

        var response = PreflightResponse(
            [("gid://shopify/LineItem/1", 5)],
            [("19.99", "EUR")]);
        var service = NewService(scope.ServiceProvider, _ => JsonResponse(response));

        var result = await service.GetRefundPreflightAsync(
            businessId, OrderId, [("gid://shopify/LineItem/1", 2)], CancellationToken.None);

        var line = Assert.Single(result.Lines);
        Assert.Equal("gid://shopify/LineItem/1", line.ExternalLineItemId);
        Assert.Equal(2, line.RequestedQuantity);
        Assert.Equal(5, line.RefundableQuantity);
        Assert.Equal(19.99m, line.ResolvedAmount);
        Assert.Equal("EUR", result.CurrencyCode);
    }

    /// <summary>
    /// Money-safety request-shape proof (design §17/§19, research Q1/Q2):
    /// the exact quantity the caller requests must be the exact quantity
    /// sent to Shopify's own suggestedRefund calculation — never hardcoded,
    /// defaulted, or otherwise transformed in transit. Deliberately uses
    /// quantity 2 (not 1): a quantity of 1 could accidentally pass even if
    /// the implementation incorrectly hardcoded or defaulted the value, so
    /// this asserts against the actual outgoing GraphQL request body/
    /// variables — not merely against a value read before the HTTP call —
    /// captured via the same FakeShopifyHttpMessageHandler.LastRequestBody
    /// mechanism ShopifyOrderCancellationExecutorTests' own request-shape
    /// tests already establish as this codebase's convention.
    /// </summary>
    [Fact]
    public async Task GetRefundPreflightAsync_SendsExactRequestedQuantityToSuggestedRefund()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var businessId = await CreateBusinessWithIntegrationAsync(db, scope.ServiceProvider);

        var response = PreflightResponse(
            [("gid://shopify/LineItem/1", 5)],
            [("19.99", "EUR")]);
        string? capturedBody = null;
        var handler = new FakeShopifyHttpMessageHandler(req =>
        {
            capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse(response);
        });
        var service = new ShopifyRefundLookupService(
            new HttpClient(handler),
            db,
            scope.ServiceProvider.GetRequiredService<IIntegrationTokenProtector>(),
            Options.Create(new ShopifyOptions
            {
                ClientId = "unused", ClientSecret = "unused", RedirectUri = "unused", AdminApiVersion = AdminApiVersion,
            }));

        // Requested quantity is 2 — deliberately not 1 and deliberately
        // different from the mocked RefundableQuantity (5) above, so a
        // hardcoded/defaulted/swapped value would be caught by either the
        // request-shape assertion below or the result-mapping assertions
        // in the sibling test above.
        await service.GetRefundPreflightAsync(
            businessId, OrderId, [("gid://shopify/LineItem/1", 2)], CancellationToken.None);

        Assert.NotNull(capturedBody);
        using var doc = JsonDocument.Parse(capturedBody!);
        var refundLineItems = doc.RootElement.GetProperty("variables").GetProperty("refundLineItems");
        var line = Assert.Single(refundLineItems.EnumerateArray());
        Assert.Equal("gid://shopify/LineItem/1", line.GetProperty("lineItemId").GetString());
        Assert.Equal(2, line.GetProperty("quantity").GetInt32());
    }

    [Fact]
    public async Task GetRefundPreflightAsync_RequestedLineItemNotOnOrder_ThrowsLineItemNotFoundOnOrderException()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var businessId = await CreateBusinessWithIntegrationAsync(db, scope.ServiceProvider);

        var response = PreflightResponse(
            [("gid://shopify/LineItem/999", 5)],
            [("19.99", "EUR")]);
        var service = NewService(scope.ServiceProvider, _ => JsonResponse(response));

        var ex = await Assert.ThrowsAsync<LineItemNotFoundOnOrderException>(() =>
            service.GetRefundPreflightAsync(businessId, OrderId, [("gid://shopify/LineItem/1", 2)], CancellationToken.None));
        Assert.Equal("gid://shopify/LineItem/1", ex.ExternalLineItemId);
    }

    [Fact]
    public async Task GetRefundPreflightAsync_SuggestedRefundCountMismatch_Throws()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var businessId = await CreateBusinessWithIntegrationAsync(db, scope.ServiceProvider);

        var response = PreflightResponse(
            [("gid://shopify/LineItem/1", 5), ("gid://shopify/LineItem/2", 5)],
            [("19.99", "EUR")]); // only one suggested line, but two requested
        var service = NewService(scope.ServiceProvider, _ => JsonResponse(response));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.GetRefundPreflightAsync(
                businessId, OrderId,
                [("gid://shopify/LineItem/1", 1), ("gid://shopify/LineItem/2", 1)],
                CancellationToken.None));
    }

    [Fact]
    public async Task GetRefundPreflightAsync_Throws_WhenIntegrationMissing()
    {
        await using var scope = _provider.CreateAsyncScope();
        var service = NewService(scope.ServiceProvider, _ => JsonResponse(PreflightResponse([], [])));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.GetRefundPreflightAsync(Guid.NewGuid(), OrderId, [("x", 1)], CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task GetRefundPreflightAsync_Throws_OnNonSuccessStatus(HttpStatusCode status)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var businessId = await CreateBusinessWithIntegrationAsync(db, scope.ServiceProvider);

        var service = NewService(scope.ServiceProvider, _ => new HttpResponseMessage(status));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.GetRefundPreflightAsync(businessId, OrderId, [("x", 1)], CancellationToken.None));
    }

    [Fact]
    public async Task GetRefundPreflightAsync_Throws_OnMalformedJson()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var businessId = await CreateBusinessWithIntegrationAsync(db, scope.ServiceProvider);

        var service = NewService(scope.ServiceProvider, _ => JsonResponse("not json"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.GetRefundPreflightAsync(businessId, OrderId, [("x", 1)], CancellationToken.None));
    }

    [Fact]
    public async Task GetRefundPreflightAsync_Throws_OnTopLevelGraphQlErrors()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var businessId = await CreateBusinessWithIntegrationAsync(db, scope.ServiceProvider);

        var service = NewService(scope.ServiceProvider, _ => JsonResponse(
            """{ "errors": [ { "message": "Field 'suggestedRefund' doesn't exist" } ] }"""));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.GetRefundPreflightAsync(businessId, OrderId, [("x", 1)], CancellationToken.None));
    }

    [Fact]
    public async Task GetRefundPreflightAsync_Throws_WhenOrderNotFound()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var businessId = await CreateBusinessWithIntegrationAsync(db, scope.ServiceProvider);

        var service = NewService(scope.ServiceProvider, _ => JsonResponse("""{ "data": { "order": null } }"""));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.GetRefundPreflightAsync(businessId, OrderId, [("x", 1)], CancellationToken.None));
    }

    [Fact]
    public async Task GetRefundPreflightAsync_SendsAccessTokenHeaderAndVersionedUrl()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var businessId = await CreateBusinessWithIntegrationAsync(db, scope.ServiceProvider);

        HttpRequestMessage? capturedRequest = null;
        var response = PreflightResponse([("x", 5)], [("19.99", "EUR")]);
        var service = NewService(scope.ServiceProvider, req =>
        {
            capturedRequest = req;
            return JsonResponse(response);
        });
        await service.GetRefundPreflightAsync(businessId, OrderId, [("x", 1)], CancellationToken.None);

        Assert.Equal(
            $"https://{_shop}.myshopify.com/admin/api/{AdminApiVersion}/graphql.json",
            capturedRequest!.RequestUri!.ToString());
        Assert.Equal(AccessToken, capturedRequest.Headers.GetValues("X-Shopify-Access-Token").Single());
    }

    private class FixedTenantAccessor(Guid? businessId) : ICurrentTenantAccessor
    {
        public Guid? BusinessId => businessId;
    }
}
