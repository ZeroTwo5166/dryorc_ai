using System.Net;
using System.Net.Http.Json;
using backend.Auth;
using backend.Controllers;
using backend.Data;
using backend.Models;
using backend.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace backend.Tests;

/// <summary>
/// HTTP-level tests for Step 18 — IntegrationsController, run against the
/// real app + real Neon dev DB via NivoraApiFactory, same pattern as
/// TicketsApiTests/TagModeMappingsApiTests. Covers the full connect ->
/// authorization URL -> callback -> token exchange -> encryption ->
/// persistence -> audit flow via the test-only FakeIntegrationOAuthProvider
/// (registered only in NivoraApiFactory, never in Program.cs), reconnect
/// semantics, tenant isolation, OwnerOnly enforcement, the OAuth state
/// token's cross-tenant-replay rejection, and the explicit
/// unregistered-platform 404 contract (Step 18 ships zero concrete
/// production providers).
/// </summary>
public class IntegrationsApiTests : IAsyncLifetime
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

            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"AuditLogs\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Integrations\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"AspNetUserRoles\" WHERE \"UserId\" IN (SELECT \"Id\" FROM \"AspNetUsers\" WHERE \"BusinessId\" = ANY({ids}))");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"AspNetUsers\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Businesses\" WHERE \"Id\" = ANY({ids})");
        }

        await _factory.DisposeAsync();
    }

    private HttpClient NewClient() => _factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });

    private HttpClient NewClientNoRedirect() => _factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"),
        AllowAutoRedirect = false,
    });

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

    private static async Task<string> ExtractStateAsync(HttpResponseMessage connectResponse)
    {
        Assert.Equal(HttpStatusCode.OK, connectResponse.StatusCode);
        var body = await connectResponse.Content.ReadFromJsonAsync<ConnectIntegrationResponse>();
        Assert.NotNull(body);
        var query = QueryHelpers.ParseQuery(new Uri(body!.AuthorizationUrl).Query);
        return query["state"].ToString();
    }

    // ── Full flow ────────────────────────────────────────────────────────

    [Fact]
    public async Task ConnectCallback_FullFlow_PersistsEncryptedIntegration_AuditsWithoutSecrets_AndNeverExposesTokens()
    {
        var (businessId, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Integration Full Flow Business");

        var connectResponse = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, $"/api/integrations/{FakeIntegrationOAuthProvider.PlatformName}/connect", ownerCookie),
            new ConnectIntegrationRequest("my-shop.example.com")));
        var state = await ExtractStateAsync(connectResponse);

        using var noRedirectClient = NewClientNoRedirect();
        var callbackResponse = await noRedirectClient.SendAsync(
            Authed(HttpMethod.Get,
                $"/api/integrations/{FakeIntegrationOAuthProvider.PlatformName}/callback?code={FakeIntegrationOAuthProvider.ValidCode}&state={Uri.EscapeDataString(state)}",
                ownerCookie));

        Assert.Equal(HttpStatusCode.Redirect, callbackResponse.StatusCode);
        Assert.Contains("connected=" + FakeIntegrationOAuthProvider.PlatformName, callbackResponse.Headers.Location!.ToString());

        // List() response never includes token fields — verified against the
        // raw JSON, not just the strongly-typed record shape, so a future
        // accidental property addition to IntegrationResponse would still be
        // caught here.
        var listResponse = await ownerClient.SendAsync(Authed(HttpMethod.Get, "/api/integrations", ownerCookie));
        var rawJson = await listResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Token", rawJson);
        var integrations = await listResponse.Content.ReadFromJsonAsync<List<IntegrationResponse>>();
        var integration = Assert.Single(integrations!);
        Assert.Equal(FakeIntegrationOAuthProvider.PlatformName, integration.Platform);
        Assert.Equal("fake-shop.example.com", integration.ExternalAccountIdentifier);
        Assert.True(integration.IsActive);
        Assert.Equal(["read_orders", "write_orders"], integration.Scopes);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var protector = scope.ServiceProvider.GetRequiredService<IIntegrationTokenProtector>();

        var row = await db.Integrations.IgnoreQueryFilters().SingleAsync(i => i.BusinessId == businessId);
        Assert.NotEqual("fake-access-token", row.EncryptedAccessToken);
        Assert.StartsWith("fake-access-token-", protector.Unprotect(FakeIntegrationOAuthProvider.PlatformName, row.EncryptedAccessToken));

        var auditLog = await db.AuditLogs.IgnoreQueryFilters()
            .Where(a => a.BusinessId == businessId && a.EventType == backend.Constants.AuditEventTypes.IntegrationConnected)
            .SingleAsync();
        Assert.DoesNotContain("fake-access-token", auditLog.NewValue);
        Assert.DoesNotContain(row.EncryptedAccessToken, auditLog.NewValue ?? "");
    }

    [Fact]
    public async Task Reconnect_UpdatesExistingRowInPlace_NoDuplicateRow()
    {
        var (businessId, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Integration Reconnect Business");

        async Task ConnectOnceAsync()
        {
            var connectResponse = await ownerClient.SendAsync(WithBody(
                Authed(HttpMethod.Post, $"/api/integrations/{FakeIntegrationOAuthProvider.PlatformName}/connect", ownerCookie),
                new ConnectIntegrationRequest("my-shop.example.com")));
            var state = await ExtractStateAsync(connectResponse);
            using var noRedirectClient = NewClientNoRedirect();
            var callbackResponse = await noRedirectClient.SendAsync(
                Authed(HttpMethod.Get,
                    $"/api/integrations/{FakeIntegrationOAuthProvider.PlatformName}/callback?code={FakeIntegrationOAuthProvider.ValidCode}&state={Uri.EscapeDataString(state)}",
                    ownerCookie));
            Assert.Equal(HttpStatusCode.Redirect, callbackResponse.StatusCode);
        }

        await ConnectOnceAsync();
        await ConnectOnceAsync();

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rows = await db.Integrations.IgnoreQueryFilters().Where(i => i.BusinessId == businessId).ToListAsync();
        Assert.Single(rows);
        Assert.True(rows[0].IsActive);
    }

    // ── Tenant isolation ────────────────────────────────────────────────

    [Fact]
    public async Task List_TenantIsolation_BusinessACannotSeeBusinessBsIntegration()
    {
        var (_, _, ownerClientA, ownerCookieA) = await SetUpBusinessWithOwnerAsync("Integration Tenant A");
        var (_, _, ownerClientB, ownerCookieB) = await SetUpBusinessWithOwnerAsync("Integration Tenant B");

        var connectResponse = await ownerClientB.SendAsync(WithBody(
            Authed(HttpMethod.Post, $"/api/integrations/{FakeIntegrationOAuthProvider.PlatformName}/connect", ownerCookieB),
            new ConnectIntegrationRequest("business-b-shop.example.com")));
        var state = await ExtractStateAsync(connectResponse);
        using (var noRedirectClient = NewClientNoRedirect())
        {
            await noRedirectClient.SendAsync(Authed(HttpMethod.Get,
                $"/api/integrations/{FakeIntegrationOAuthProvider.PlatformName}/callback?code={FakeIntegrationOAuthProvider.ValidCode}&state={Uri.EscapeDataString(state)}",
                ownerCookieB));
        }

        var listAsA = await ownerClientA.SendAsync(Authed(HttpMethod.Get, "/api/integrations", ownerCookieA));
        var integrationsA = await listAsA.Content.ReadFromJsonAsync<List<IntegrationResponse>>();
        Assert.Empty(integrationsA!);

        var disconnectAsA = await ownerClientA.SendAsync(
            Authed(HttpMethod.Post, $"/api/integrations/{FakeIntegrationOAuthProvider.PlatformName}/disconnect", ownerCookieA));
        Assert.Equal(HttpStatusCode.NotFound, disconnectAsA.StatusCode);
    }

    // ── Authorization tier: OwnerOnly ───────────────────────────────────

    [Fact]
    public async Task Connect_AgentForbidden()
    {
        var (businessId, _, ownerClient, _) = await SetUpBusinessWithOwnerAsync("Integration Agent Connect Business");

        var agentEmail = $"agent-{Guid.NewGuid():N}@example.com";
        await SeedStaffIntoBusinessAsync(businessId, agentEmail, RoleNames.Agent);
        using var agentClient = NewClient();
        var (_, _, agentCookie) = await LoginAsync(agentClient, agentEmail);

        var response = await agentClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, $"/api/integrations/{FakeIntegrationOAuthProvider.PlatformName}/connect", agentCookie!),
            new ConnectIntegrationRequest("my-shop.example.com")));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Connect_AdminForbidden()
    {
        var (businessId, _, ownerClient, _) = await SetUpBusinessWithOwnerAsync("Integration Admin Connect Business");

        var adminEmail = $"admin-{Guid.NewGuid():N}@example.com";
        await SeedStaffIntoBusinessAsync(businessId, adminEmail, RoleNames.Admin);
        using var adminClient = NewClient();
        var (_, _, adminCookie) = await LoginAsync(adminClient, adminEmail);

        var response = await adminClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, $"/api/integrations/{FakeIntegrationOAuthProvider.PlatformName}/connect", adminCookie!),
            new ConnectIntegrationRequest("my-shop.example.com")));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode); // OwnerOnly, deliberately not OwnerOrAdmin
    }

    [Fact]
    public async Task Disconnect_AdminForbidden()
    {
        var (businessId, _, ownerClient, _) = await SetUpBusinessWithOwnerAsync("Integration Admin Disconnect Business");

        var adminEmail = $"admin-{Guid.NewGuid():N}@example.com";
        await SeedStaffIntoBusinessAsync(businessId, adminEmail, RoleNames.Admin);
        using var adminClient = NewClient();
        var (_, _, adminCookie) = await LoginAsync(adminClient, adminEmail);

        var response = await adminClient.SendAsync(
            Authed(HttpMethod.Post, $"/api/integrations/{FakeIntegrationOAuthProvider.PlatformName}/disconnect", adminCookie!));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ── Unregistered platform: explicit 404 contract ────────────────────

    [Fact]
    public async Task UnregisteredPlatform_Connect_Returns404_NoIntegrationRowCreated()
    {
        var (businessId, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Integration Unregistered Connect Business");

        var response = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, "/api/integrations/shopify/connect", ownerCookie),
            new ConnectIntegrationRequest("my-shop.example.com")));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.Integrations.IgnoreQueryFilters().AnyAsync(i => i.BusinessId == businessId));
    }

    [Fact]
    public async Task UnregisteredPlatform_Callback_Returns404_NoIntegrationRowCreated()
    {
        var (businessId, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Integration Unregistered Callback Business");

        // No provider is registered for "shopify" in this test host either
        // (only "fake" is) — matching production, where zero providers are
        // registered at all. A hand-crafted, syntactically plausible
        // code/state pair is used since a real one can never be obtained for
        // a platform with no provider to issue it.
        var response = await ownerClient.SendAsync(Authed(HttpMethod.Get,
            "/api/integrations/shopify/callback?code=irrelevant&state=irrelevant", ownerCookie));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.Integrations.IgnoreQueryFilters().AnyAsync(i => i.BusinessId == businessId));
    }

    // ── OAuth state security ─────────────────────────────────────────────

    [Fact]
    public async Task Callback_StateIssuedForDifferentBusiness_Rejected_NoIntegrationRowCreated()
    {
        var (businessIdA, _, ownerClientA, ownerCookieA) = await SetUpBusinessWithOwnerAsync("Integration State Cross-Tenant A");
        var (businessIdB, _, ownerClientB, ownerCookieB) = await SetUpBusinessWithOwnerAsync("Integration State Cross-Tenant B");

        // Business A initiates connect and obtains a state token bound to
        // (BusinessA, UserA, "fake").
        var connectResponse = await ownerClientA.SendAsync(WithBody(
            Authed(HttpMethod.Post, $"/api/integrations/{FakeIntegrationOAuthProvider.PlatformName}/connect", ownerCookieA),
            new ConnectIntegrationRequest("business-a-shop.example.com")));
        var stateFromA = await ExtractStateAsync(connectResponse);

        // Business B's own authenticated session attempts to redeem that
        // state token — the live cookie's BusinessId (B) does not match the
        // state token's embedded BusinessId (A), so this must be rejected
        // regardless of the state token's own validity.
        using var noRedirectClient = NewClientNoRedirect();
        var callbackResponse = await noRedirectClient.SendAsync(Authed(HttpMethod.Get,
            $"/api/integrations/{FakeIntegrationOAuthProvider.PlatformName}/callback?code={FakeIntegrationOAuthProvider.ValidCode}&state={Uri.EscapeDataString(stateFromA)}",
            ownerCookieB));

        Assert.Equal(HttpStatusCode.Redirect, callbackResponse.StatusCode);
        Assert.Contains("integrationError=state_mismatch", callbackResponse.Headers.Location!.ToString());

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.Integrations.IgnoreQueryFilters().AnyAsync(i => i.BusinessId == businessIdA || i.BusinessId == businessIdB));
    }

    [Fact]
    public async Task Callback_TamperedState_Rejected_NoIntegrationRowCreated()
    {
        var (businessId, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Integration Tampered State Business");

        var connectResponse = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, $"/api/integrations/{FakeIntegrationOAuthProvider.PlatformName}/connect", ownerCookie),
            new ConnectIntegrationRequest("my-shop.example.com")));
        var state = await ExtractStateAsync(connectResponse);
        var tamperedState = state[..^1] + (state[^1] == 'A' ? 'B' : 'A');

        using var noRedirectClient = NewClientNoRedirect();
        var callbackResponse = await noRedirectClient.SendAsync(Authed(HttpMethod.Get,
            $"/api/integrations/{FakeIntegrationOAuthProvider.PlatformName}/callback?code={FakeIntegrationOAuthProvider.ValidCode}&state={Uri.EscapeDataString(tamperedState)}",
            ownerCookie));

        Assert.Equal(HttpStatusCode.Redirect, callbackResponse.StatusCode);
        Assert.Contains("integrationError=invalid_state", callbackResponse.Headers.Location!.ToString());

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.Integrations.IgnoreQueryFilters().AnyAsync(i => i.BusinessId == businessId));
    }

    [Fact]
    public async Task Callback_NoAuthenticatedCookie_Returns401()
    {
        using var anonClient = NewClient();

        var response = await anonClient.GetAsync(
            $"/api/integrations/{FakeIntegrationOAuthProvider.PlatformName}/callback?code=whatever&state=whatever");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Callback_ExchangeFails_RedirectsWithError_NoIntegrationRowCreated()
    {
        var (businessId, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Integration Exchange Failure Business");

        var connectResponse = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, $"/api/integrations/{FakeIntegrationOAuthProvider.PlatformName}/connect", ownerCookie),
            new ConnectIntegrationRequest("my-shop.example.com")));
        var state = await ExtractStateAsync(connectResponse);

        using var noRedirectClient = NewClientNoRedirect();
        var callbackResponse = await noRedirectClient.SendAsync(Authed(HttpMethod.Get,
            $"/api/integrations/{FakeIntegrationOAuthProvider.PlatformName}/callback?code=not-the-valid-code&state={Uri.EscapeDataString(state)}",
            ownerCookie));

        Assert.Equal(HttpStatusCode.Redirect, callbackResponse.StatusCode);
        Assert.Contains("integrationError=exchange_failed", callbackResponse.Headers.Location!.ToString());

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.Integrations.IgnoreQueryFilters().AnyAsync(i => i.BusinessId == businessId));
    }

    // ── Disconnect ───────────────────────────────────────────────────────

    [Fact]
    public async Task Disconnect_Idempotent_SecondCallIsNoOp_NoDuplicateAudit()
    {
        var (businessId, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Integration Disconnect Idempotent Business");

        var connectResponse = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, $"/api/integrations/{FakeIntegrationOAuthProvider.PlatformName}/connect", ownerCookie),
            new ConnectIntegrationRequest("my-shop.example.com")));
        var state = await ExtractStateAsync(connectResponse);
        using (var noRedirectClient = NewClientNoRedirect())
        {
            await noRedirectClient.SendAsync(Authed(HttpMethod.Get,
                $"/api/integrations/{FakeIntegrationOAuthProvider.PlatformName}/callback?code={FakeIntegrationOAuthProvider.ValidCode}&state={Uri.EscapeDataString(state)}",
                ownerCookie));
        }

        var firstDisconnect = await ownerClient.SendAsync(
            Authed(HttpMethod.Post, $"/api/integrations/{FakeIntegrationOAuthProvider.PlatformName}/disconnect", ownerCookie));
        Assert.Equal(HttpStatusCode.NoContent, firstDisconnect.StatusCode);

        var secondDisconnect = await ownerClient.SendAsync(
            Authed(HttpMethod.Post, $"/api/integrations/{FakeIntegrationOAuthProvider.PlatformName}/disconnect", ownerCookie));
        Assert.Equal(HttpStatusCode.NoContent, secondDisconnect.StatusCode);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.Integrations.IgnoreQueryFilters().SingleAsync(i => i.BusinessId == businessId);
        Assert.False(row.IsActive);
        Assert.NotNull(row.DisconnectedAt);

        var disconnectAuditCount = await db.AuditLogs.IgnoreQueryFilters().CountAsync(a =>
            a.BusinessId == businessId && a.EventType == backend.Constants.AuditEventTypes.IntegrationDisconnected);
        Assert.Equal(1, disconnectAuditCount);
    }
}
