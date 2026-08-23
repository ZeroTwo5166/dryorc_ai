using System.Net;
using System.Net.Http.Json;
using backend.Auth;
using backend.Controllers;
using backend.Data;
using backend.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace backend.Tests;

/// <summary>
/// HTTP-level tests for WidgetsController — the Step 6 staff-facing
/// provisioning surface (api/widgets, plural), run against the real app +
/// real Neon dev DB via NivoraApiFactory, same pattern as
/// TagModeMappingsApiTests. Covers read (AnyStaff) vs write (OwnerOnly)
/// authorization, tenant isolation, CSPRNG PublicKey generation, and that
/// the new production creation path doesn't disturb the existing widget
/// init/auth/messages pipeline (WidgetPipelineTests/WidgetAuthenticationTests/
/// WidgetConcurrencyAndIsolationTests cover that pipeline directly; this
/// file only re-runs those suites in the full-run verification, not here).
/// </summary>
public class WidgetsApiTests : IAsyncLifetime
{
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

            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Widgets\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"AspNetUserRoles\" WHERE \"UserId\" IN (SELECT \"Id\" FROM \"AspNetUsers\" WHERE \"BusinessId\" = ANY({ids}))");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"AspNetUsers\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Businesses\" WHERE \"Id\" = ANY({ids})");
        }

        await _factory.DisposeAsync();
    }

    private HttpClient NewClient() => _factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });

    private async Task<(HttpResponseMessage Response, RegisterResponse? Body)> RegisterAsync(
        HttpClient client, string businessName, string email, string password = "Passw0rd!!")
    {
        var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(businessName, "Europe/Copenhagen", email, password));
        var body = response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<RegisterResponse>() : null;
        return (response, body);
    }

    private async Task ConfirmEmailAsync(HttpClient client, Guid userId)
    {
        string token;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await userManager.FindByIdAsync(userId.ToString());
            Assert.NotNull(user);
            token = await userManager.GenerateEmailConfirmationTokenAsync(user!);
        }

        var response = await client.PostAsJsonAsync("/api/auth/confirm-email", new ConfirmEmailRequest(userId, token));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static async Task<(HttpResponseMessage Response, LoginResponse? Body, string? Cookie)> LoginAsync(
        HttpClient client, string email, string password = "Passw0rd!!")
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, password));
        LoginResponse? body = null;
        string? cookie = null;
        if (response.IsSuccessStatusCode)
        {
            body = await response.Content.ReadFromJsonAsync<LoginResponse>();
            cookie = response.Headers.TryGetValues("Set-Cookie", out var values) ? values.First().Split(';')[0] : null;
        }
        return (response, body, cookie);
    }

    private async Task<ApplicationUser> SeedStaffIntoBusinessAsync(Guid businessId, string email, string role)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var createResult = await userManager.CreateAsync(user, "Passw0rd!!");
        Assert.True(createResult.Succeeded, string.Join("; ", createResult.Errors.Select(e => e.Description)));
        var roleResult = await userManager.AddToRoleAsync(user, role);
        Assert.True(roleResult.Succeeded, string.Join("; ", roleResult.Errors.Select(e => e.Description)));
        return user;
    }

    private static HttpRequestMessage Authed(HttpMethod method, string url, string cookie)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("Cookie", cookie);
        return request;
    }

    private static HttpRequestMessage WithBody<T>(HttpRequestMessage request, T body)
    {
        request.Content = JsonContent.Create(body);
        return request;
    }

    private record WidgetResponseDto(Guid Id, string Name, string PublicKey, List<string> AllowedDomains, bool IsActive, DateTimeOffset CreatedAt, string EmbedSnippet);

    private async Task<(Guid BusinessId, Guid OwnerId, HttpClient OwnerClient, string OwnerCookie)> SetUpBusinessWithOwnerAsync(string businessName)
    {
        var email = $"owner-{Guid.NewGuid():N}@example.com";
        using var anonClient = NewClient();
        var (registerResponse, registerBody) = await RegisterAsync(anonClient, businessName, email);
        Assert.Equal(HttpStatusCode.OK, registerResponse.StatusCode);
        Assert.NotNull(registerBody);
        _businessIdsToClean.Add(registerBody!.BusinessId);
        await ConfirmEmailAsync(anonClient, registerBody.UserId);

        var client = NewClient();
        var (loginResponse, _, cookie) = await LoginAsync(client, email);
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);

        return (registerBody.BusinessId, registerBody.UserId, client, cookie!);
    }

    // ── Write: OwnerOnly ──────────────────────────────────────────────

    [Fact]
    public async Task Create_AgentForbidden()
    {
        var (businessId, _, ownerClient, _) = await SetUpBusinessWithOwnerAsync("Widget Agent Write Business");
        using (ownerClient) { }

        var agentEmail = $"agent-{Guid.NewGuid():N}@example.com";
        await SeedStaffIntoBusinessAsync(businessId, agentEmail, RoleNames.Agent);
        using var agentClient = NewClient();
        var (_, _, agentCookie) = await LoginAsync(agentClient, agentEmail);

        var response = await agentClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, "/api/widgets", agentCookie!),
            new CreateWidgetRequest("Storefront Widget", null)));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Create_AdminForbidden()
    {
        var (businessId, _, ownerClient, _) = await SetUpBusinessWithOwnerAsync("Widget Admin Write Business");
        using (ownerClient) { }

        var adminEmail = $"admin-{Guid.NewGuid():N}@example.com";
        await SeedStaffIntoBusinessAsync(businessId, adminEmail, RoleNames.Admin);
        using var adminClient = NewClient();
        var (_, _, adminCookie) = await LoginAsync(adminClient, adminEmail);

        var response = await adminClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, "/api/widgets", adminCookie!),
            new CreateWidgetRequest("Storefront Widget", null)));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode); // OwnerOnly, deliberately not OwnerOrAdmin
    }

    [Fact]
    public async Task Create_Unauthenticated_Returns401()
    {
        using var anonClient = NewClient();

        var response = await anonClient.PostAsJsonAsync("/api/widgets", new CreateWidgetRequest("Storefront Widget", null));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Create_AsOwner_Succeeds_AndPersists()
    {
        var (businessId, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Widget Owner Create Business");
        using var _ = ownerClient;

        var response = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, "/api/widgets", ownerCookie),
            new CreateWidgetRequest("Storefront Widget", ["nike.com", " adidas.com "])));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<WidgetResponseDto>();
        Assert.NotNull(body);
        Assert.Equal("Storefront Widget", body!.Name);
        Assert.True(body.IsActive);
        Assert.Equal(["nike.com", "adidas.com"], body.AllowedDomains);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var persisted = await db.Widgets.IgnoreQueryFilters().SingleAsync(w => w.Id == body.Id);
        Assert.Equal(businessId, persisted.BusinessId);
        Assert.Equal(body.PublicKey, persisted.PublicKey);
        Assert.Equal(["nike.com", "adidas.com"], persisted.AllowedDomains);
        Assert.True(persisted.IsActive);
    }

    [Fact]
    public async Task Create_NoAllowedDomains_DefaultsToEmptyList()
    {
        var (_, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Widget No Domains Business");
        using var _ = ownerClient;

        var response = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, "/api/widgets", ownerCookie),
            new CreateWidgetRequest("Storefront Widget", null)));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<WidgetResponseDto>();
        Assert.NotNull(body);
        Assert.Empty(body!.AllowedDomains);
    }

    [Fact]
    public async Task Create_BlankName_ReturnsBadRequest()
    {
        var (_, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Widget Blank Name Business");
        using var _ = ownerClient;

        var response = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, "/api/widgets", ownerCookie),
            new CreateWidgetRequest("   ", null)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── PublicKey generation ─────────────────────────────────────────

    [Fact]
    public async Task Create_PublicKey_IsPresentAndNotGuidShaped()
    {
        var (_, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Widget Key Shape Business");
        using var _ = ownerClient;

        var response = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, "/api/widgets", ownerCookie),
            new CreateWidgetRequest("Storefront Widget", null)));
        var body = await response.Content.ReadFromJsonAsync<WidgetResponseDto>();

        Assert.NotNull(body);
        Assert.False(string.IsNullOrWhiteSpace(body!.PublicKey));
        // 32 bytes of RandomNumberGenerator output, lowercase-hex-encoded —
        // 64 hex characters. A Guid.NewGuid().ToString("N") would be exactly
        // 32 characters, so the length itself distinguishes the two; this
        // also rules out any 36-character "D"-format Guid string.
        Assert.Equal(64, body.PublicKey.Length);
        Assert.Matches("^[0-9a-f]{64}$", body.PublicKey);
        Assert.DoesNotContain("-", body.PublicKey);
        Assert.False(Guid.TryParse(body.PublicKey, out Guid parsedAsGuid));
    }

    [Fact]
    public async Task Create_RepeatedCreations_ProduceDistinctPublicKeys()
    {
        var (_, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Widget Distinct Keys Business");
        using var _ = ownerClient;

        var firstResponse = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, "/api/widgets", ownerCookie),
            new CreateWidgetRequest("Widget One", null)));
        var secondResponse = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, "/api/widgets", ownerCookie),
            new CreateWidgetRequest("Widget Two", null)));

        var first = await firstResponse.Content.ReadFromJsonAsync<WidgetResponseDto>();
        var second = await secondResponse.Content.ReadFromJsonAsync<WidgetResponseDto>();

        Assert.NotEqual(first!.PublicKey, second!.PublicKey);
    }

    // ── Tenant isolation ──────────────────────────────────────────────

    [Fact]
    public async Task Get_CrossTenant_ReturnsNotFound()
    {
        var (_, _, ownerClientA, ownerCookieA) = await SetUpBusinessWithOwnerAsync("Widget Tenant A");
        using var _a = ownerClientA;
        var createResponse = await ownerClientA.SendAsync(WithBody(
            Authed(HttpMethod.Post, "/api/widgets", ownerCookieA),
            new CreateWidgetRequest("Tenant A Widget", null)));
        var created = await createResponse.Content.ReadFromJsonAsync<WidgetResponseDto>();

        var (_, _, ownerClientB, ownerCookieB) = await SetUpBusinessWithOwnerAsync("Widget Tenant B");
        using var _b = ownerClientB;

        var getResponse = await ownerClientB.SendAsync(Authed(HttpMethod.Get, $"/api/widgets/{created!.Id}", ownerCookieB));
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
    }

    [Fact]
    public async Task Create_OwnerOfBusinessA_CannotCreateWidgetForBusinessB()
    {
        // There is no BusinessId field on CreateWidgetRequest at all, so
        // there is nothing for a client to override — this test proves the
        // resulting widget is tied to the authenticated caller's own
        // tenant regardless of which business's data the caller might try
        // to reference elsewhere in the same session.
        var (businessIdA, _, ownerClientA, ownerCookieA) = await SetUpBusinessWithOwnerAsync("Widget Cross-Create Tenant A");
        using var _a = ownerClientA;
        var (businessIdB, _, ownerClientB, _) = await SetUpBusinessWithOwnerAsync("Widget Cross-Create Tenant B");
        using var _b = ownerClientB;

        var response = await ownerClientA.SendAsync(WithBody(
            Authed(HttpMethod.Post, "/api/widgets", ownerCookieA),
            new CreateWidgetRequest("Should Belong To A", null)));
        var created = await response.Content.ReadFromJsonAsync<WidgetResponseDto>();

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var persisted = await db.Widgets.IgnoreQueryFilters().SingleAsync(w => w.Id == created!.Id);
        Assert.Equal(businessIdA, persisted.BusinessId);
        Assert.NotEqual(businessIdB, persisted.BusinessId);
    }

    // ── Get round-trip ────────────────────────────────────────────────

    [Fact]
    public async Task Get_AfterCreate_ReturnsSamePublicKey()
    {
        var (_, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Widget Get Roundtrip Business");
        using var _ = ownerClient;

        var createResponse = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, "/api/widgets", ownerCookie),
            new CreateWidgetRequest("Roundtrip Widget", ["shop.example.com"])));
        var created = await createResponse.Content.ReadFromJsonAsync<WidgetResponseDto>();

        var getResponse = await ownerClient.SendAsync(Authed(HttpMethod.Get, $"/api/widgets/{created!.Id}", ownerCookie));
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var fetched = await getResponse.Content.ReadFromJsonAsync<WidgetResponseDto>();

        Assert.Equal(created.PublicKey, fetched!.PublicKey);
        Assert.Equal(created.AllowedDomains, fetched.AllowedDomains);
    }

    [Fact]
    public async Task Get_AgentCanRead()
    {
        var (businessId, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Widget Agent Read Business");
        using var _ = ownerClient;

        var createResponse = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, "/api/widgets", ownerCookie),
            new CreateWidgetRequest("Agent Readable Widget", null)));
        var created = await createResponse.Content.ReadFromJsonAsync<WidgetResponseDto>();

        var agentEmail = $"agent-{Guid.NewGuid():N}@example.com";
        await SeedStaffIntoBusinessAsync(businessId, agentEmail, RoleNames.Agent);
        using var agentClient = NewClient();
        var (_, _, agentCookie) = await LoginAsync(agentClient, agentEmail);

        var getResponse = await agentClient.SendAsync(Authed(HttpMethod.Get, $"/api/widgets/{created!.Id}", agentCookie!));
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
    }

    // ── Step 6B: embed snippet ──────────────────────────────────────────

    [Fact]
    public async Task Create_EmbedSnippet_ContainsScriptTagWithPublicKey()
    {
        var (_, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Widget Snippet Business");
        using var _ = ownerClient;

        var response = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, "/api/widgets", ownerCookie),
            new CreateWidgetRequest("Storefront Widget", null)));
        var body = await response.Content.ReadFromJsonAsync<WidgetResponseDto>();

        Assert.NotNull(body);
        Assert.StartsWith("<script ", body!.EmbedSnippet);
        Assert.Contains("nivora-widget.js", body.EmbedSnippet);
        Assert.Contains($"data-public-key=\"{body.PublicKey}\"", body.EmbedSnippet);
        Assert.Contains("async", body.EmbedSnippet);
        Assert.EndsWith("</script>", body.EmbedSnippet);
    }

    [Fact]
    public async Task Create_EmbedSnippet_IncludesBusinessName()
    {
        var (_, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Widget Snippet Name Business");
        using var _ = ownerClient;

        var response = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, "/api/widgets", ownerCookie),
            new CreateWidgetRequest("Storefront Widget", null)));
        var body = await response.Content.ReadFromJsonAsync<WidgetResponseDto>();

        Assert.NotNull(body);
        Assert.Contains("data-business-name=\"Widget Snippet Name Business\"", body!.EmbedSnippet);
    }

    [Fact]
    public async Task Create_EmbedSnippet_HtmlEncodesBusinessNameWithSpecialCharacters()
    {
        var (_, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Nike & \"Friends\" Co");
        using var _ = ownerClient;

        var response = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, "/api/widgets", ownerCookie),
            new CreateWidgetRequest("Storefront Widget", null)));
        var body = await response.Content.ReadFromJsonAsync<WidgetResponseDto>();

        Assert.NotNull(body);
        Assert.Contains("Nike &amp; &quot;Friends&quot; Co", body!.EmbedSnippet);
        // The raw, unescaped name must never appear — that would mean the
        // attribute value could be broken out of by an embedded `"`.
        Assert.DoesNotContain("data-business-name=\"Nike & \"Friends\"", body.EmbedSnippet);
    }

    [Fact]
    public async Task Get_EmbedSnippet_MatchesCreateResponse()
    {
        var (_, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Widget Snippet Get Business");
        using var _ = ownerClient;

        var createResponse = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, "/api/widgets", ownerCookie),
            new CreateWidgetRequest("Storefront Widget", null)));
        var created = await createResponse.Content.ReadFromJsonAsync<WidgetResponseDto>();

        var getResponse = await ownerClient.SendAsync(Authed(HttpMethod.Get, $"/api/widgets/{created!.Id}", ownerCookie));
        var fetched = await getResponse.Content.ReadFromJsonAsync<WidgetResponseDto>();

        Assert.Equal(created.EmbedSnippet, fetched!.EmbedSnippet);
    }
}
