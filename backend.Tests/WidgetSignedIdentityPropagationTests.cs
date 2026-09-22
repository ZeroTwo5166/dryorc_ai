using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using backend.Auth;
using backend.Controllers;
using backend.Data;
using backend.Models;
using backend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace backend.Tests;

/// <summary>
/// End-to-end proof of the widget propagation path (implementation-ready
/// spec §7): signed token at Init -&gt; resolved CustomerId -&gt;
/// nivora:customer_id widget JWT claim -&gt; PostMessage reads the claim -&gt;
/// Conversation.CustomerId set at creation. Real HTTP pipeline via
/// NivoraApiFactory, same convention as WidgetAuthenticationTests.
/// </summary>
public class WidgetSignedIdentityPropagationTests : IAsyncLifetime
{
    private const long ExternalCustomerId = 987654;

    private readonly NivoraApiFactory _factory = new();
    private readonly List<Guid> _businessIdsToClean = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_businessIdsToClean.Count > 0)
        {
            await using var scope = _factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var ids = _businessIdsToClean.ToArray();

            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Messages\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Conversations\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"ConsumedSignedIdentityTokens\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"ExternalIdentities\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Customers\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"BusinessSigningKeys\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Widgets\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"AspNetUsers\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Businesses\" WHERE \"Id\" = ANY({ids})");
        }

        await _factory.DisposeAsync();
    }

    private HttpClient NewClient() => _factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });

    private static HttpRequestMessage WithJsonBody<T>(HttpRequestMessage request, T body)
    {
        request.Content = JsonContent.Create(body);
        return request;
    }

    private static HttpRequestMessage Bearer(HttpMethod method, string url, string token)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("Authorization", $"Bearer {token}");
        return request;
    }

    private async Task<(Guid BusinessId, Widget Widget)> CreateBusinessAndWidgetAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var business = new Business
        {
            Id = Guid.NewGuid(), Name = $"Signed Identity Widget Test {Guid.NewGuid():N}", TimeZone = "Europe/Copenhagen",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        var widget = new Widget
        {
            Id = Guid.NewGuid(), BusinessId = business.Id, Name = "Test Widget", PublicKey = Guid.NewGuid().ToString("N"),
            IsActive = true, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Businesses.Add(business);
        db.Widgets.Add(widget);
        await db.SaveChangesAsync();
        _businessIdsToClean.Add(business.Id);

        return (business.Id, widget);
    }

    private async Task<Guid> RegisterKeyAsync(Guid businessId, FakeSignedTokenSigner signer)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var email = $"signing-key-{Guid.NewGuid():N}@example.com";
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(), BusinessId = businessId, UserName = email, NormalizedUserName = email.ToUpperInvariant(),
            Email = email, NormalizedEmail = email.ToUpperInvariant(), CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Users.Add(user);

        var key = new BusinessSigningKey
        {
            Id = Guid.NewGuid(), BusinessId = businessId, PublicKeyPem = signer.PublicKeyPem, Algorithm = "RS256",
            CreatedByUserId = user.Id, CreatedAt = DateTimeOffset.UtcNow,
        };
        db.BusinessSigningKeys.Add(key);
        await db.SaveChangesAsync();
        return key.Id;
    }

    private static IEnumerable<System.Security.Claims.Claim> DecodeClaims(string jwt) =>
        new JwtSecurityTokenHandler().ReadJwtToken(jwt).Claims;

    [Fact]
    public async Task Init_ValidSignedToken_ProducesCustomerIdClaim_PropagatesToConversation()
    {
        var (businessId, widget) = await CreateBusinessAndWidgetAsync();
        using var signer = new FakeSignedTokenSigner();
        var kid = await RegisterKeyAsync(businessId, signer);
        var signedToken = signer.IssueToken(kid, ExternalCustomerId);

        using var client = NewClient();
        var initResponse = await client.PostAsJsonAsync(
            "/api/widget/init", new WidgetInitRequest(widget.PublicKey, null, signedToken));
        Assert.Equal(HttpStatusCode.OK, initResponse.StatusCode);
        var initBody = await initResponse.Content.ReadFromJsonAsync<WidgetInitResponse>();
        Assert.NotNull(initBody);

        var claims = DecodeClaims(initBody!.Token).ToList();
        var customerIdClaim = claims.SingleOrDefault(c => c.Type == NivoraClaimTypes.CustomerId);
        Assert.NotNull(customerIdClaim);

        var messageResponse = await client.SendAsync(WithJsonBody(
            Bearer(HttpMethod.Post, "/api/widget/messages", initBody.Token), new PostWidgetMessageRequest("Hello, signed-in customer")));
        Assert.Equal(HttpStatusCode.OK, messageResponse.StatusCode);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var conversation = await db.Conversations.IgnoreQueryFilters()
            .SingleAsync(c => c.BusinessId == businessId);
        Assert.Equal(Guid.Parse(customerIdClaim!.Value), conversation.CustomerId);
    }

    [Fact]
    public async Task Init_AbsentSignedToken_PreservesAnonymousBehavior()
    {
        var (businessId, widget) = await CreateBusinessAndWidgetAsync();

        using var client = NewClient();
        var initResponse = await client.PostAsJsonAsync("/api/widget/init", new WidgetInitRequest(widget.PublicKey, null));
        Assert.Equal(HttpStatusCode.OK, initResponse.StatusCode);
        var initBody = await initResponse.Content.ReadFromJsonAsync<WidgetInitResponse>();
        Assert.NotNull(initBody);

        var claims = DecodeClaims(initBody!.Token).ToList();
        Assert.DoesNotContain(claims, c => c.Type == NivoraClaimTypes.CustomerId);

        var messageResponse = await client.SendAsync(WithJsonBody(
            Bearer(HttpMethod.Post, "/api/widget/messages", initBody.Token), new PostWidgetMessageRequest("Anonymous hello")));
        Assert.Equal(HttpStatusCode.OK, messageResponse.StatusCode);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var conversation = await db.Conversations.IgnoreQueryFilters().SingleAsync(c => c.BusinessId == businessId);
        Assert.Null(conversation.CustomerId);
    }

    [Fact]
    public async Task Init_InvalidSignedToken_StillSucceedsAnonymously_NeverFailsInit()
    {
        var (businessId, widget) = await CreateBusinessAndWidgetAsync();

        using var client = NewClient();
        // A garbage token — a business-side integration bug or attempted
        // tamper must never block ordinary anonymous widget usage.
        var initResponse = await client.PostAsJsonAsync(
            "/api/widget/init", new WidgetInitRequest(widget.PublicKey, null, "not-a-real-jwt"));

        Assert.Equal(HttpStatusCode.OK, initResponse.StatusCode);
        var initBody = await initResponse.Content.ReadFromJsonAsync<WidgetInitResponse>();
        Assert.NotNull(initBody);
        Assert.DoesNotContain(DecodeClaims(initBody!.Token), c => c.Type == NivoraClaimTypes.CustomerId);

        _ = businessId; // seeded for DisposeAsync cleanup scoping only
    }

    /// <summary>
    /// Identity-binding/erasure-integrity Phase 5 sibling to
    /// Init_InvalidSignedToken_StillSucceedsAnonymously_NeverFailsInit
    /// above (design §5a; implementation-plan.md's Phase 5): proves the
    /// same silently-absorbed-rejection contract holds when
    /// ValidateAndResolveAsync's Rejected outcome is reached specifically
    /// via the new erasure-in-flight branch, not just the pre-existing
    /// invalid-token causes.
    /// </summary>
    [Fact]
    public async Task Init_SignedTokenForCustomerWithErasurePending_StillSucceedsAnonymously()
    {
        var (businessId, widget) = await CreateBusinessAndWidgetAsync();
        using var signer = new FakeSignedTokenSigner();
        var kid = await RegisterKeyAsync(businessId, signer);

        using var client = NewClient();

        var seedInit = await client.PostAsJsonAsync(
            "/api/widget/init", new WidgetInitRequest(widget.PublicKey, null, signer.IssueToken(kid, ExternalCustomerId)));
        Assert.Equal(HttpStatusCode.OK, seedInit.StatusCode);
        var seedBody = await seedInit.Content.ReadFromJsonAsync<WidgetInitResponse>();
        var seedClaims = DecodeClaims(seedBody!.Token).ToList();
        var customerId = Guid.Parse(seedClaims.Single(c => c.Type == NivoraClaimTypes.CustomerId).Value);

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Customers.IgnoreQueryFilters()
                .Where(c => c.Id == customerId)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.PendingErasureAt, DateTimeOffset.UtcNow));
        }

        var raceInit = await client.PostAsJsonAsync(
            "/api/widget/init", new WidgetInitRequest(widget.PublicKey, null, signer.IssueToken(kid, ExternalCustomerId)));

        Assert.Equal(HttpStatusCode.OK, raceInit.StatusCode);
        var raceBody = await raceInit.Content.ReadFromJsonAsync<WidgetInitResponse>();
        Assert.NotNull(raceBody);
        Assert.DoesNotContain(DecodeClaims(raceBody!.Token), c => c.Type == NivoraClaimTypes.CustomerId);

        var messageResponse = await client.SendAsync(WithJsonBody(
            Bearer(HttpMethod.Post, "/api/widget/messages", raceBody.Token), new PostWidgetMessageRequest("Erasure-pending hello")));
        Assert.Equal(HttpStatusCode.OK, messageResponse.StatusCode);

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Unchanged — no refresh/insert happened for the erasure-pending customer.
        var identityCount = await verifyDb.ExternalIdentities.IgnoreQueryFilters()
            .CountAsync(ei => ei.BusinessId == businessId && ei.CustomerId == customerId);
        Assert.Equal(1, identityCount);

        var conversation = await verifyDb.Conversations.IgnoreQueryFilters()
            .SingleAsync(c => c.BusinessId == businessId && c.VisitorSessionId == raceBody.VisitorSessionId);
        Assert.Null(conversation.CustomerId);
    }

    [Fact]
    public async Task ExistingConversation_CustomerIdNeverOverwrittenByLaterMessage()
    {
        var (businessId, widget) = await CreateBusinessAndWidgetAsync();
        using var signer = new FakeSignedTokenSigner();
        var kid = await RegisterKeyAsync(businessId, signer);

        using var client = NewClient();

        // First Init+message WITH a signed token — establishes CustomerId.
        var firstInit = await client.PostAsJsonAsync(
            "/api/widget/init", new WidgetInitRequest(widget.PublicKey, null, signer.IssueToken(kid, ExternalCustomerId)));
        var firstBody = await firstInit.Content.ReadFromJsonAsync<WidgetInitResponse>();
        await client.SendAsync(WithJsonBody(
            Bearer(HttpMethod.Post, "/api/widget/messages", firstBody!.Token), new PostWidgetMessageRequest("First message")));

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var conversation = await db.Conversations.IgnoreQueryFilters().SingleAsync(c => c.BusinessId == businessId);
            Assert.NotNull(conversation.CustomerId);
        }

        // Second Init for the SAME visitor session, this time WITHOUT a
        // token (e.g. the storefront simply didn't re-supply one on a later
        // page load) — re-uses the same open conversation via
        // VisitorSessionId; posting again must never null out or otherwise
        // change the already-set CustomerId.
        var secondInit = await client.PostAsJsonAsync(
            "/api/widget/init", new WidgetInitRequest(widget.PublicKey, firstBody.VisitorSessionId));
        var secondBody = await secondInit.Content.ReadFromJsonAsync<WidgetInitResponse>();
        await client.SendAsync(WithJsonBody(
            Bearer(HttpMethod.Post, "/api/widget/messages", secondBody!.Token), new PostWidgetMessageRequest("Second message")));

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var conversations = await verifyDb.Conversations.IgnoreQueryFilters()
            .Where(c => c.BusinessId == businessId)
            .ToListAsync();
        Assert.Single(conversations); // same conversation reused, not a second one
        Assert.NotNull(conversations[0].CustomerId);
    }
}
