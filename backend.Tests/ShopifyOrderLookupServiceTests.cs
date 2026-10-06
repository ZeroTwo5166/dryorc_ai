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
        await TestBusinessCleanup.DeleteThenDisposeAsync(_businessIdsToClean, _provider);
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

    private static ShopifyOrderLookupService NewServiceWithHandler(
        IServiceProvider scopedProvider, FakeShopifyHttpMessageHandler handler) =>
        new(
            new HttpClient(handler),
            scopedProvider.GetRequiredService<AppDbContext>(),
            scopedProvider.GetRequiredService<IIntegrationTokenProtector>(),
            Options.Create(new ShopifyOptions
            {
                ClientId = "unused", ClientSecret = "unused", RedirectUri = "unused", AdminApiVersion = AdminApiVersion,
            }));

    private static string NormalizeGraphQl(string graphQlText) =>
        string.Join(' ', graphQlText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

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

    // ── Step getOrder: LookupOrderOwnerForReadToolAsync duplication-parity ──
    //
    // LookupOrderOwnerForReadToolAsync (design §2.1a) is a deliberate
    // near-duplicate of LookupOrderOwnerAsync above, added rather than
    // modifying the existing method (design §1.4). These tests prove the
    // duplication stayed faithful: identical output for the same response,
    // identical outgoing request semantics, and — separately — that only
    // the intended 401/403 branch actually differs.

    [Fact]
    public async Task LookupOrderOwnerForReadToolAsync_MatchesLookupOrderOwnerAsync_OutputAndRequestSemantics()
    {
        var businessId = await CreateBusinessWithIntegrationAsync();
        const string orderId = "gid://shopify/Order/1";
        const string customerId = "gid://shopify/Customer/2";

        await using var scope = _provider.CreateAsyncScope();

        var oldHandler = new FakeShopifyHttpMessageHandler(_ => JsonResponse(OrderResponse(orderId, customerId)));
        var oldService = NewServiceWithHandler(scope.ServiceProvider, oldHandler);
        var oldResult = await oldService.LookupOrderOwnerAsync(businessId, orderId, CancellationToken.None);

        var newHandler = new FakeShopifyHttpMessageHandler(_ => JsonResponse(OrderResponse(orderId, customerId)));
        var newService = NewServiceWithHandler(scope.ServiceProvider, newHandler);
        var newResult = await newService.LookupOrderOwnerForReadToolAsync(businessId, orderId, CancellationToken.None);

        // (a) output equivalence.
        Assert.Equal(oldResult.ExternalOrderId, newResult.ExternalOrderId);
        Assert.Equal(oldResult.OwnerExternalCustomerId, newResult.OwnerExternalCustomerId);

        // (b) outgoing request semantics — host, method, credential header,
        // and the meaningful GraphQL request contract (operation/selection
        // set, normalized for whitespace, plus variables), not merely the
        // same output.
        Assert.Equal(oldHandler.LastRequest!.RequestUri, newHandler.LastRequest!.RequestUri);
        Assert.Equal(oldHandler.LastRequest.Method, newHandler.LastRequest.Method);
        Assert.Equal(
            oldHandler.LastRequest.Headers.GetValues("X-Shopify-Access-Token").Single(),
            newHandler.LastRequest.Headers.GetValues("X-Shopify-Access-Token").Single());

        using var oldJson = JsonDocument.Parse(oldHandler.LastRequestBody!);
        using var newJson = JsonDocument.Parse(newHandler.LastRequestBody!);
        Assert.Equal(
            NormalizeGraphQl(oldJson.RootElement.GetProperty("query").GetString()!),
            NormalizeGraphQl(newJson.RootElement.GetProperty("query").GetString()!));
        Assert.Equal(
            oldJson.RootElement.GetProperty("variables").GetProperty("id").GetString(),
            newJson.RootElement.GetProperty("variables").GetProperty("id").GetString());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task LookupOrderOwnerForReadToolAsync_Throws_ShopifyAuthFailureException_AndSetsReauthorizationRequiredAt(HttpStatusCode status)
    {
        var businessId = await CreateBusinessWithIntegrationAsync();

        await using var scope = _provider.CreateAsyncScope();
        var service = NewService(scope.ServiceProvider, _ => new HttpResponseMessage(status));

        await Assert.ThrowsAsync<ShopifyAuthFailureException>(
            () => service.LookupOrderOwnerForReadToolAsync(businessId, "gid://shopify/Order/1", CancellationToken.None));

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var integration = await db.Integrations.IgnoreQueryFilters().AsNoTracking().SingleAsync(i => i.BusinessId == businessId);
        Assert.NotNull(integration.ReauthorizationRequiredAt);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task LookupOrderOwnerAsync_StillThrowsInvalidOperationException_UnchangedFromExistingMethod(HttpStatusCode status)
    {
        // Proves the duplication in LookupOrderOwnerForReadToolAsync did NOT
        // leak into LookupOrderOwnerAsync's own untouched body — the
        // existing method must keep throwing its original
        // InvalidOperationException for 401/403 (already pinned by
        // LookupOrderOwnerAsync_Throws_OnNonSuccessStatus above, which
        // includes these two statuses), never the new
        // ShopifyAuthFailureException, and must never write
        // ReauthorizationRequiredAt — it has no such branch.
        var businessId = await CreateBusinessWithIntegrationAsync();

        await using var scope = _provider.CreateAsyncScope();
        var service = NewService(scope.ServiceProvider, _ => new HttpResponseMessage(status));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.LookupOrderOwnerAsync(businessId, "gid://shopify/Order/1", CancellationToken.None));
        Assert.IsNotType<ShopifyAuthFailureException>(ex);

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var integration = await db.Integrations.IgnoreQueryFilters().AsNoTracking().SingleAsync(i => i.BusinessId == businessId);
        Assert.Null(integration.ReauthorizationRequiredAt);
    }

    // ── Step getOrder: cross-tenant BusinessId→Integration selection ──
    //
    // Tightened per independent review (design §7): proves Nivora's own
    // BusinessId-scoped Integration selection is correct when more than one
    // business's Integration row exists, not merely that Shopify's own
    // token-scoping would separately prevent cross-tenant data — a
    // query-filter regression (e.g. an accidentally dropped BusinessId
    // predicate) could otherwise pass every other test in this file, since a
    // single-integration fixture can't distinguish "the right row was
    // selected" from "the only row was selected."

    [Fact]
    public async Task LookupOrderOwnerForReadToolAsync_UsesTheRequestedBusinesssOwnIntegration_NotAnotherBusinesss()
    {
        var shopA = $"shop-a-{Guid.NewGuid():N}";
        var shopB = $"shop-b-{Guid.NewGuid():N}";
        var businessAId = await CreateBusinessWithIntegrationAsync(
            externalAccountIdentifier: shopA, plaintextAccessTokenOverride: "token-for-business-a");
        var businessBId = await CreateBusinessWithIntegrationAsync(
            externalAccountIdentifier: shopB, plaintextAccessTokenOverride: "token-for-business-b");

        await using var scope = _provider.CreateAsyncScope();
        var handler = new FakeShopifyHttpMessageHandler(_ => JsonResponse(OrderResponse("gid://shopify/Order/1", null)));
        var service = NewServiceWithHandler(scope.ServiceProvider, handler);

        await service.LookupOrderOwnerForReadToolAsync(businessAId, "gid://shopify/Order/1", CancellationToken.None);

        Assert.Equal(
            $"https://{shopA}.myshopify.com/admin/api/{AdminApiVersion}/graphql.json",
            handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal("token-for-business-a", handler.LastRequest.Headers.GetValues("X-Shopify-Access-Token").Single());
        // businessBId is only referenced to prove it exists concurrently in
        // the same table — never passed to the call above.
        Assert.NotEqual(businessAId, businessBId);
    }

    // ── ResolveOrderNameAsync (design §2.1) ──────────────────────────────

    [Fact]
    public async Task ResolveOrderNameAsync_ReturnsNotFound_OnZeroMatches()
    {
        var businessId = await CreateBusinessWithIntegrationAsync();
        await using var scope = _provider.CreateAsyncScope();
        var service = NewService(scope.ServiceProvider, _ => JsonResponse("""{ "data": { "orders": { "edges": [] } } }"""));

        var result = await service.ResolveOrderNameAsync(businessId, "#9999", CancellationToken.None);

        Assert.Equal(OrderNameResolutionOutcome.NotFound, result.Outcome);
        Assert.Null(result.ResolvedOrderGid);
    }

    [Fact]
    public async Task ResolveOrderNameAsync_ReturnsResolved_OnExactlyOneMatch()
    {
        var businessId = await CreateBusinessWithIntegrationAsync();
        await using var scope = _provider.CreateAsyncScope();
        var service = NewService(scope.ServiceProvider, _ => JsonResponse(
            """{ "data": { "orders": { "edges": [ { "node": { "id": "gid://shopify/Order/1" } } ] } } }"""));

        var result = await service.ResolveOrderNameAsync(businessId, "#1001", CancellationToken.None);

        Assert.Equal(OrderNameResolutionOutcome.Resolved, result.Outcome);
        Assert.Equal("gid://shopify/Order/1", result.ResolvedOrderGid);
    }

    [Fact]
    public async Task ResolveOrderNameAsync_ReturnsAmbiguous_OnMoreThanOneMatch()
    {
        var businessId = await CreateBusinessWithIntegrationAsync();
        await using var scope = _provider.CreateAsyncScope();
        var service = NewService(scope.ServiceProvider, _ => JsonResponse(
            """{ "data": { "orders": { "edges": [ { "node": { "id": "gid://shopify/Order/1" } }, { "node": { "id": "gid://shopify/Order/2" } } ] } } }"""));

        var result = await service.ResolveOrderNameAsync(businessId, "1001", CancellationToken.None);

        Assert.Equal(OrderNameResolutionOutcome.Ambiguous, result.Outcome);
        Assert.Null(result.ResolvedOrderGid);
    }

    [Fact]
    public async Task ResolveOrderNameAsync_SendsTheRawValueUnmodified_AsTheNameSearchTerm()
    {
        // Pins the UNVERIFIED default described in this method's own doc
        // comment (design §2.1's OPEN QUESTION, Q3 of the research doc) — a
        // regression pin for "no client-side normalization happens," not a
        // claim that this is confirmed correct against live Shopify.
        var businessId = await CreateBusinessWithIntegrationAsync();
        await using var scope = _provider.CreateAsyncScope();
        string? capturedBody = null;
        var service = NewService(scope.ServiceProvider, req =>
        {
            capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse("""{ "data": { "orders": { "edges": [] } } }""");
        });

        await service.ResolveOrderNameAsync(businessId, "#1001", CancellationToken.None);

        using var json = JsonDocument.Parse(capturedBody!);
        Assert.Equal("name:#1001", json.RootElement.GetProperty("variables").GetProperty("query").GetString());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task ResolveOrderNameAsync_Throws_ShopifyAuthFailureException_AndSetsReauthorizationRequiredAt(HttpStatusCode status)
    {
        var businessId = await CreateBusinessWithIntegrationAsync();
        await using var scope = _provider.CreateAsyncScope();
        var service = NewService(scope.ServiceProvider, _ => new HttpResponseMessage(status));

        await Assert.ThrowsAsync<ShopifyAuthFailureException>(
            () => service.ResolveOrderNameAsync(businessId, "#1001", CancellationToken.None));

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var integration = await db.Integrations.IgnoreQueryFilters().AsNoTracking().SingleAsync(i => i.BusinessId == businessId);
        Assert.NotNull(integration.ReauthorizationRequiredAt);
    }

    [Fact]
    public async Task ResolveOrderNameAsync_Throws_InvalidOperationException_OnOtherNonSuccessStatus()
    {
        var businessId = await CreateBusinessWithIntegrationAsync();
        await using var scope = _provider.CreateAsyncScope();
        var service = NewService(scope.ServiceProvider, _ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ResolveOrderNameAsync(businessId, "#1001", CancellationToken.None));
    }

    // ── GetOrderDetailAsync (design §2.2) ────────────────────────────────

    [Fact]
    public async Task GetOrderDetailAsync_MapsAllFields_IncludingEveryFulfillmentAndTrackingEntry()
    {
        // Per design §2.2a: every tracking entry across every fulfillment is
        // preserved, none selected/dropped.
        var businessId = await CreateBusinessWithIntegrationAsync();
        const string json = """
            {
              "data": {
                "order": {
                  "id": "gid://shopify/Order/1",
                  "name": "#1001",
                  "displayFinancialStatus": "PAID",
                  "displayFulfillmentStatus": "FULFILLED",
                  "cancelledAt": null,
                  "lineItems": { "edges": [
                    { "node": { "title": "Widget", "quantity": 2, "discountedUnitPriceSet": { "presentmentMoney": { "amount": "9.99" } } } }
                  ] },
                  "fulfillments": [
                    { "trackingInfo": [ { "number": "1Z1", "url": "https://a" }, { "number": "1Z2", "url": "https://b" } ] },
                    { "trackingInfo": [ { "number": "1Z3", "url": "https://c" } ] }
                  ]
                }
              }
            }
            """;
        await using var scope = _provider.CreateAsyncScope();
        var service = NewService(scope.ServiceProvider, _ => JsonResponse(json));

        var result = await service.GetOrderDetailAsync(businessId, "gid://shopify/Order/1", CancellationToken.None);

        Assert.Equal("#1001", result.Name);
        Assert.Equal("PAID", result.FinancialStatus);
        Assert.Equal("FULFILLED", result.FulfillmentStatus);
        Assert.Null(result.CancelledAt);
        var lineItem = Assert.Single(result.LineItems);
        Assert.Equal("Widget", lineItem.Title);
        Assert.Equal(2, lineItem.Quantity);
        Assert.Equal("9.99", lineItem.Price);
        Assert.Equal(3, result.Tracking.Count);
        Assert.Contains(result.Tracking, t => t.Number == "1Z1" && t.Url == "https://a");
        Assert.Contains(result.Tracking, t => t.Number == "1Z2" && t.Url == "https://b");
        Assert.Contains(result.Tracking, t => t.Number == "1Z3" && t.Url == "https://c");
    }

    [Fact]
    public async Task GetOrderDetailAsync_ReturnsEmptyTrackingAndLineItemLists_WhenNoneExist()
    {
        var businessId = await CreateBusinessWithIntegrationAsync();
        const string json = """
            { "data": { "order": { "id": "gid://shopify/Order/1", "name": "#1001",
              "displayFinancialStatus": null, "displayFulfillmentStatus": "UNFULFILLED", "cancelledAt": null,
              "lineItems": { "edges": [] }, "fulfillments": [] } } }
            """;
        await using var scope = _provider.CreateAsyncScope();
        var service = NewService(scope.ServiceProvider, _ => JsonResponse(json));

        var result = await service.GetOrderDetailAsync(businessId, "gid://shopify/Order/1", CancellationToken.None);

        Assert.Empty(result.Tracking);
        Assert.Empty(result.LineItems);
    }

    [Fact]
    public async Task GetOrderDetailAsync_RequestsLineItemAndFulfillmentCaps()
    {
        var businessId = await CreateBusinessWithIntegrationAsync();
        const string json = """
            { "data": { "order": { "id": "gid://shopify/Order/1", "name": "#1001",
              "displayFinancialStatus": null, "displayFulfillmentStatus": "UNFULFILLED", "cancelledAt": null,
              "lineItems": { "edges": [] }, "fulfillments": [] } } }
            """;
        await using var scope = _provider.CreateAsyncScope();
        string? capturedBody = null;
        var service = NewService(scope.ServiceProvider, req =>
        {
            capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse(json);
        });

        await service.GetOrderDetailAsync(businessId, "gid://shopify/Order/1", CancellationToken.None);

        Assert.Contains("lineItems(first: 50)", capturedBody);
        Assert.Contains("fulfillments(first: 10)", capturedBody);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task GetOrderDetailAsync_Throws_ShopifyAuthFailureException_AndSetsReauthorizationRequiredAt(HttpStatusCode status)
    {
        var businessId = await CreateBusinessWithIntegrationAsync();
        await using var scope = _provider.CreateAsyncScope();
        var service = NewService(scope.ServiceProvider, _ => new HttpResponseMessage(status));

        await Assert.ThrowsAsync<ShopifyAuthFailureException>(
            () => service.GetOrderDetailAsync(businessId, "gid://shopify/Order/1", CancellationToken.None));

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var integration = await db.Integrations.IgnoreQueryFilters().AsNoTracking().SingleAsync(i => i.BusinessId == businessId);
        Assert.NotNull(integration.ReauthorizationRequiredAt);
    }

    [Fact]
    public async Task GetOrderDetailAsync_Throws_InvalidOperationException_WhenOrderNotFound()
    {
        // A genuine anomaly here, not an ordinary outcome (design §2.2) —
        // unlike ResolveOrderNameAsync's zero-match case.
        var businessId = await CreateBusinessWithIntegrationAsync();
        await using var scope = _provider.CreateAsyncScope();
        var service = NewService(scope.ServiceProvider, _ => JsonResponse("""{ "data": { "order": null } }"""));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.GetOrderDetailAsync(businessId, "gid://shopify/Order/999", CancellationToken.None));
    }

    private class FixedTenantAccessor(Guid? businessId) : ICurrentTenantAccessor
    {
        public Guid? BusinessId => businessId;
    }
}
