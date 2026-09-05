using System.Net;
using System.Text;
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
/// Service-level tests for ShopifyOrderLookupService — real Postgres for the
/// Integration/Business rows (same convention as ConversationModeServiceTests:
/// each test opens its own scope via _provider.CreateAsyncScope()),
/// FakeShopifyHttpMessageHandler for the outbound Shopify call (same
/// convention as ShopifyIntegrationOAuthProviderTests). No production
/// caller exists yet — deliberate, per the Step 20 handoff.
/// </summary>
public class ShopifyOrderLookupServiceTests : IAsyncLifetime
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
        bool isActive = true, string? externalAccountIdentifier = null, string? encryptedAccessTokenOverride = null)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tokenProtector = scope.ServiceProvider.GetRequiredService<IIntegrationTokenProtector>();

        var business = new Business
        {
            Id = Guid.NewGuid(),
            Name = $"Shopify Lookup Test {Guid.NewGuid():N}",
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
                ?? tokenProtector.Protect(ShopifyIntegrationOAuthProvider.PlatformName, AccessToken),
            ExternalAccountIdentifier = externalAccountIdentifier ?? Shop,
            IsActive = isActive,
            ConnectedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync();
        _businessIdsToClean.Add(business.Id);
        return business.Id;
    }

    private static ShopifyOrderLookupService NewService(
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

    private static string OrderResponse(string orderId, string? customerId) =>
        customerId is null
            ? $$"""{ "data": { "order": { "id": "{{orderId}}", "customer": null } } }"""
            : $$"""{ "data": { "order": { "id": "{{orderId}}", "customer": { "id": "{{customerId}}" } } } }""";

    // ── Success cases ────────────────────────────────────────────────

    [Fact]
    public async Task LookupOrderOwnerAsync_ReturnsOwner_OnSuccessfulResponse()
    {
        var businessId = await CreateBusinessWithIntegrationAsync();
        const string orderId = "gid://shopify/Order/1";
        const string customerId = "gid://shopify/Customer/2";

        await using var scope = _provider.CreateAsyncScope();
        var service = NewService(scope.ServiceProvider, _ => JsonResponse(OrderResponse(orderId, customerId)));

        var result = await service.LookupOrderOwnerAsync(businessId, orderId, CancellationToken.None);

        Assert.Equal(orderId, result.ExternalOrderId);
        Assert.Equal(customerId, result.OwnerExternalCustomerId);
    }

    [Fact]
    public async Task LookupOrderOwnerAsync_ReturnsNullOwner_OnGuestCheckoutOrder()
    {
        var businessId = await CreateBusinessWithIntegrationAsync();
        const string orderId = "gid://shopify/Order/1";

        await using var scope = _provider.CreateAsyncScope();
        var service = NewService(scope.ServiceProvider, _ => JsonResponse(OrderResponse(orderId, null)));

        var result = await service.LookupOrderOwnerAsync(businessId, orderId, CancellationToken.None);

        Assert.Equal(orderId, result.ExternalOrderId);
        Assert.Null(result.OwnerExternalCustomerId);
    }

    [Fact]
    public async Task LookupOrderOwnerAsync_SendsAccessTokenHeaderAndVersionedUrl()
    {
        var businessId = await CreateBusinessWithIntegrationAsync();
        const string orderId = "gid://shopify/Order/1";
        HttpRequestMessage? capturedRequest = null;

        await using var scope = _provider.CreateAsyncScope();
        var service = NewService(scope.ServiceProvider, req =>
        {
            capturedRequest = req;
            return JsonResponse(OrderResponse(orderId, null));
        });

        await service.LookupOrderOwnerAsync(businessId, orderId, CancellationToken.None);

        Assert.NotNull(capturedRequest);
        Assert.Equal(
            $"https://{Shop}.myshopify.com/admin/api/{AdminApiVersion}/graphql.json",
            capturedRequest!.RequestUri!.ToString());
        Assert.Equal(AccessToken, capturedRequest.Headers.GetValues("X-Shopify-Access-Token").Single());
    }

    // ── Failure cases — must throw, never construct VerifiedOrderOwnership ──

    [Fact]
    public async Task LookupOrderOwnerAsync_Throws_WhenIntegrationMissing()
    {
        await using var scope = _provider.CreateAsyncScope();
        var service = NewService(scope.ServiceProvider, _ => JsonResponse(OrderResponse("id", null)));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.LookupOrderOwnerAsync(Guid.NewGuid(), "gid://shopify/Order/1", CancellationToken.None));
    }

    [Fact]
    public async Task LookupOrderOwnerAsync_Throws_WhenIntegrationInactive()
    {
        var businessId = await CreateBusinessWithIntegrationAsync(isActive: false);

        await using var scope = _provider.CreateAsyncScope();
        var service = NewService(scope.ServiceProvider, _ => JsonResponse(OrderResponse("id", null)));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.LookupOrderOwnerAsync(businessId, "gid://shopify/Order/1", CancellationToken.None));
    }

    [Fact]
    public async Task LookupOrderOwnerAsync_Throws_WhenOrderNotFound()
    {
        var businessId = await CreateBusinessWithIntegrationAsync();

        await using var scope = _provider.CreateAsyncScope();
        var service = NewService(scope.ServiceProvider, _ => JsonResponse("""{ "data": { "order": null } }"""));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.LookupOrderOwnerAsync(businessId, "gid://shopify/Order/999", CancellationToken.None));
    }

    [Fact]
    public async Task LookupOrderOwnerAsync_Throws_OnGraphQlErrors()
    {
        var businessId = await CreateBusinessWithIntegrationAsync();

        await using var scope = _provider.CreateAsyncScope();
        var service = NewService(scope.ServiceProvider, _ => JsonResponse(
            """{ "errors": [ { "message": "Field 'order' doesn't exist" } ] }"""));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.LookupOrderOwnerAsync(businessId, "gid://shopify/Order/1", CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task LookupOrderOwnerAsync_Throws_OnNonSuccessStatus(HttpStatusCode status)
    {
        var businessId = await CreateBusinessWithIntegrationAsync();

        await using var scope = _provider.CreateAsyncScope();
        var service = NewService(scope.ServiceProvider, _ => new HttpResponseMessage(status));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.LookupOrderOwnerAsync(businessId, "gid://shopify/Order/1", CancellationToken.None));
    }

    [Fact]
    public async Task LookupOrderOwnerAsync_Throws_OnMalformedJson()
    {
        var businessId = await CreateBusinessWithIntegrationAsync();

        await using var scope = _provider.CreateAsyncScope();
        var service = NewService(scope.ServiceProvider, _ => JsonResponse("this is not json"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.LookupOrderOwnerAsync(businessId, "gid://shopify/Order/1", CancellationToken.None));
    }

    [Fact]
    public async Task LookupOrderOwnerAsync_Throws_WhenAccessTokenCannotBeDecrypted()
    {
        var businessId = await CreateBusinessWithIntegrationAsync(encryptedAccessTokenOverride: "not-a-valid-protected-payload");

        await using var scope = _provider.CreateAsyncScope();
        var service = NewService(scope.ServiceProvider, _ => JsonResponse(OrderResponse("id", null)));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.LookupOrderOwnerAsync(businessId, "gid://shopify/Order/1", CancellationToken.None));
    }

    [Fact]
    public async Task LookupOrderOwnerAsync_PropagatesCancellation()
    {
        var businessId = await CreateBusinessWithIntegrationAsync();

        await using var scope = _provider.CreateAsyncScope();
        var service = NewService(scope.ServiceProvider, _ => JsonResponse(OrderResponse("id", null)));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.LookupOrderOwnerAsync(businessId, "gid://shopify/Order/1", cts.Token));
    }

    private class FixedTenantAccessor(Guid? businessId) : ICurrentTenantAccessor
    {
        public Guid? BusinessId => businessId;
    }
}
