using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using backend.Auth;
using backend.Controllers;
using backend.Data;
using backend.Models;
using backend.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
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
/// unregistered-platform 404 contract. Also covers Step 19 — the real
/// ShopifyIntegrationOAuthProvider exercised through the real
/// IEnumerable&lt;IIntegrationOAuthProvider&gt; provider-selection path,
/// with only the outbound Shopify HTTP call faked (see
/// FakeShopifyHttpMessageHandler / NewShopifyFactory below). As of Step 19,
/// "shopify" is a genuinely registered platform — the unregistered-platform
/// tests use FakeIntegrationOAuthProvider.UnregisteredPlatformName instead.
/// </summary>
public class IntegrationsApiTests : IAsyncLifetime
{
    private readonly NivoraApiFactory _factory = new();
    private readonly List<Guid> _businessIdsToClean = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await TestBusinessCleanup.DeleteThenDisposeAsync(_businessIdsToClean, _factory);
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

    /// <summary>
    /// Step Shopify-auth-reauth (docs/handoffs/step-shopify-auth-reauth-design.md
    /// §4.2/§12/§14) — a successful reconnect must clear
    /// Integration.ReauthorizationRequiredAt, exactly like it already clears
    /// every other credential-related field on this row. Simulates a prior
    /// auth-classified failure by setting the column directly (the executor
    /// paths that actually set it are covered independently by
    /// ShopifyRefundExecutorTests/ShopifyOrderCancellationExecutorTests) —
    /// this test's own job is only to verify Callback's reconnect branch
    /// clears it, not to re-derive how it got set.
    /// </summary>
    [Fact]
    public async Task Reconnect_ClearsReauthorizationRequiredAt()
    {
        var (businessId, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Integration Reauth Clear Business");

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

        await using (var seedScope = _factory.Services.CreateAsyncScope())
        {
            var seedDb = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            await seedDb.Integrations
                .IgnoreQueryFilters()
                .Where(i => i.BusinessId == businessId)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.ReauthorizationRequiredAt, DateTimeOffset.UtcNow));
        }

        await using (var verifyBeforeScope = _factory.Services.CreateAsyncScope())
        {
            var verifyBeforeDb = verifyBeforeScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var before = await verifyBeforeDb.Integrations.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(i => i.BusinessId == businessId);
            Assert.NotNull(before.ReauthorizationRequiredAt);
        }

        await ConnectOnceAsync();

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.Integrations.IgnoreQueryFilters().AsNoTracking().SingleAsync(i => i.BusinessId == businessId);
        Assert.True(row.IsActive);
        Assert.Null(row.ReauthorizationRequiredAt);

        var listResponse = await ownerClient.SendAsync(Authed(HttpMethod.Get, "/api/integrations", ownerCookie));
        var integrations = await listResponse.Content.ReadFromJsonAsync<List<IntegrationResponse>>();
        var integration = Assert.Single(integrations!);
        Assert.Null(integration.ReauthorizationRequiredAt);
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
            Authed(HttpMethod.Post, $"/api/integrations/{FakeIntegrationOAuthProvider.UnregisteredPlatformName}/connect", ownerCookie),
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

        // No provider is registered for FakeIntegrationOAuthProvider.UnregisteredPlatformName
        // in this test host either (only "fake" and, as of Step 19,
        // "shopify" are) — matching production, where that platform string
        // has no registered provider either. A hand-crafted, syntactically
        // plausible code/state pair is used since a real one can never be
        // obtained for a platform with no provider to issue it.
        var response = await ownerClient.SendAsync(Authed(HttpMethod.Get,
            $"/api/integrations/{FakeIntegrationOAuthProvider.UnregisteredPlatformName}/callback?code=irrelevant&state=irrelevant", ownerCookie));

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

    // ── Step 19: Shopify, through the real provider-selection path ─────

    /// <summary>
    /// Clones _factory (same NivoraApiFactory base wiring, same Postgres DB
    /// and Data Protection key ring — see NivoraApiFactory's doc comment)
    /// and additionally fakes only ShopifyIntegrationOAuthProvider's
    /// underlying HttpClient handler. Real controller, real provider
    /// resolution, real ShopifyIntegrationOAuthProvider — only the outbound
    /// network call to Shopify's token endpoint is faked. See Program.cs's
    /// Step 19 registration comment for why overriding
    /// AddHttpClient&lt;ShopifyIntegrationOAuthProvider&gt; here (rather than
    /// AddHttpClient&lt;IIntegrationOAuthProvider, ...&gt;) cannot create a
    /// second IIntegrationOAuthProvider registration.
    /// </summary>
    private (WebApplicationFactory<Program> Factory, FakeShopifyHttpMessageHandler Handler) NewShopifyFactory(
        Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new FakeShopifyHttpMessageHandler(respond);
        var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.AddHttpClient<ShopifyIntegrationOAuthProvider>()
                    .ConfigurePrimaryHttpMessageHandler(() => handler);
            });
        });
        return (factory, handler);
    }

    // Independently implements Shopify's documented HMAC procedure — used
    // only to construct realistic test callbacks, never to validate them
    // (validation is ShopifyIntegrationOAuthProvider's own production code,
    // exercised for real through these tests).
    private static string ComputeShopifyHmacHex(string clientSecret, string code, string shop, string state, string timestamp)
    {
        var canonical = string.Join('&', new[]
        {
            $"code={code}", $"shop={shop}", $"state={state}", $"timestamp={timestamp}",
        }.OrderBy(s => s, StringComparer.Ordinal));

        var bytes = HMACSHA256.HashData(Encoding.UTF8.GetBytes(clientSecret), Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string BuildShopifyCallbackUrl(string code, string hmac, string shop, string state, string timestamp) =>
        "/api/integrations/shopify/callback" +
        $"?code={Uri.EscapeDataString(code)}" +
        $"&hmac={hmac}" +
        $"&shop={Uri.EscapeDataString(shop)}" +
        $"&state={Uri.EscapeDataString(state)}" +
        $"&timestamp={Uri.EscapeDataString(timestamp)}";

    private static HttpResponseMessage ShopifySuccessResponse() => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new
        {
            access_token = "shopify-access-token",
            refresh_token = "shopify-refresh-token",
            expires_in = 3600,
            scope = "read_orders,write_orders,read_customers,write_customers,read_products,read_inventory",
        }),
    };

    [Fact]
    public async Task ConnectCallback_Shopify_FullFlow_PersistsEncryptedIntegrationWithRefreshToken_AuditsWithoutSecrets()
    {
        var (businessId, _, _, ownerCookie) = await SetUpBusinessWithOwnerAsync("Shopify Full Flow Business");
        var (shopifyFactory, _) = NewShopifyFactory(_ => ShopifySuccessResponse());

        using var connectClient = shopifyFactory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        var connectResponse = await connectClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, "/api/integrations/shopify/connect", ownerCookie),
            new ConnectIntegrationRequest("my-real-shop")));
        var state = await ExtractStateAsync(connectResponse);

        string clientSecret;
        await using (var scope = shopifyFactory.Services.CreateAsyncScope())
        {
            clientSecret = scope.ServiceProvider.GetRequiredService<IOptions<ShopifyOptions>>().Value.ClientSecret;
        }

        const string code = "shopify-auth-code";
        const string shop = "my-real-shop";
        const string timestamp = "1700000000";
        var hmac = ComputeShopifyHmacHex(clientSecret, code, shop, state, timestamp);

        using var noRedirectClient = shopifyFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
        });
        var callbackResponse = await noRedirectClient.SendAsync(Authed(
            HttpMethod.Get, BuildShopifyCallbackUrl(code, hmac, shop, state, timestamp), ownerCookie));

        Assert.Equal(HttpStatusCode.Redirect, callbackResponse.StatusCode);
        Assert.Contains("connected=shopify", callbackResponse.Headers.Location!.ToString());

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var protector = verifyScope.ServiceProvider.GetRequiredService<IIntegrationTokenProtector>();

        var row = await db.Integrations.IgnoreQueryFilters().SingleAsync(i => i.BusinessId == businessId);
        Assert.Equal("shopify", row.Platform);
        Assert.Equal(shop, row.ExternalAccountIdentifier);
        Assert.Equal("shopify-access-token", protector.Unprotect("shopify", row.EncryptedAccessToken));
        Assert.NotNull(row.EncryptedRefreshToken);
        Assert.Equal("shopify-refresh-token", protector.Unprotect("shopify", row.EncryptedRefreshToken!));
        Assert.NotNull(row.TokenExpiresAt);
        Assert.Equal(6, row.Scopes.Count);

        var auditLog = await db.AuditLogs.IgnoreQueryFilters()
            .Where(a => a.BusinessId == businessId && a.EventType == backend.Constants.AuditEventTypes.IntegrationConnected)
            .SingleAsync();
        Assert.DoesNotContain("shopify-access-token", auditLog.NewValue);
        Assert.DoesNotContain("shopify-refresh-token", auditLog.NewValue);
        Assert.DoesNotContain(clientSecret, auditLog.NewValue ?? "");

        await shopifyFactory.DisposeAsync();
    }

    [Fact]
    public async Task Callback_Shopify_TamperedHmac_RedirectsWithHmacInvalid_NoIntegrationRowCreated()
    {
        var (businessId, _, _, ownerCookie) = await SetUpBusinessWithOwnerAsync("Shopify Tampered Hmac Business");
        var (shopifyFactory, _) = NewShopifyFactory(_ => ShopifySuccessResponse());

        using var connectClient = shopifyFactory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        var connectResponse = await connectClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, "/api/integrations/shopify/connect", ownerCookie),
            new ConnectIntegrationRequest("my-real-shop")));
        var state = await ExtractStateAsync(connectResponse);

        var wrongHmac = new string('a', 64);
        using var noRedirectClient = shopifyFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
        });
        var callbackResponse = await noRedirectClient.SendAsync(Authed(HttpMethod.Get,
            BuildShopifyCallbackUrl("shopify-auth-code", wrongHmac, "my-real-shop", state, "1700000000"), ownerCookie));

        Assert.Equal(HttpStatusCode.Redirect, callbackResponse.StatusCode);
        Assert.Contains("integrationError=hmac_invalid", callbackResponse.Headers.Location!.ToString());

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.Integrations.IgnoreQueryFilters().AnyAsync(i => i.BusinessId == businessId));

        await shopifyFactory.DisposeAsync();
    }

    [Fact]
    public async Task Callback_Shopify_DuplicateQueryParameter_RedirectsWithHmacInvalid_NoIntegrationRowCreated()
    {
        var (businessId, _, _, ownerCookie) = await SetUpBusinessWithOwnerAsync("Shopify Duplicate Param Business");
        var (shopifyFactory, _) = NewShopifyFactory(_ => ShopifySuccessResponse());

        using var connectClient = shopifyFactory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        var connectResponse = await connectClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, "/api/integrations/shopify/connect", ownerCookie),
            new ConnectIntegrationRequest("my-real-shop")));
        var state = await ExtractStateAsync(connectResponse);

        string clientSecret;
        await using (var scope = shopifyFactory.Services.CreateAsyncScope())
        {
            clientSecret = scope.ServiceProvider.GetRequiredService<IOptions<ShopifyOptions>>().Value.ClientSecret;
        }

        const string code = "shopify-auth-code";
        const string shop = "my-real-shop";
        const string timestamp = "1700000000";
        var hmac = ComputeShopifyHmacHex(clientSecret, code, shop, state, timestamp);

        // A structurally invalid callback — `shop` duplicated — must be
        // rejected before Nivora's own state-token validation even runs
        // (§ ValidateCallbackStructure's ordering guarantee), never reaching
        // token exchange.
        var callbackUrl = BuildShopifyCallbackUrl(code, hmac, shop, state, timestamp) + $"&shop={Uri.EscapeDataString(shop)}";

        using var noRedirectClient = shopifyFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
        });
        var callbackResponse = await noRedirectClient.SendAsync(Authed(HttpMethod.Get, callbackUrl, ownerCookie));

        Assert.Equal(HttpStatusCode.Redirect, callbackResponse.StatusCode);
        Assert.Contains("integrationError=hmac_invalid", callbackResponse.Headers.Location!.ToString());

        await using var scope2 = _factory.Services.CreateAsyncScope();
        var db = scope2.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.Integrations.IgnoreQueryFilters().AnyAsync(i => i.BusinessId == businessId));

        await shopifyFactory.DisposeAsync();
    }

    [Fact]
    public async Task Callback_Shopify_ExchangeFails_RedirectsWithExchangeFailed_NoIntegrationRowCreated()
    {
        var (businessId, _, _, ownerCookie) = await SetUpBusinessWithOwnerAsync("Shopify Exchange Failure Business");
        var (shopifyFactory, _) = NewShopifyFactory(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        using var connectClient = shopifyFactory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        var connectResponse = await connectClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, "/api/integrations/shopify/connect", ownerCookie),
            new ConnectIntegrationRequest("my-real-shop")));
        var state = await ExtractStateAsync(connectResponse);

        string clientSecret;
        await using (var scope = shopifyFactory.Services.CreateAsyncScope())
        {
            clientSecret = scope.ServiceProvider.GetRequiredService<IOptions<ShopifyOptions>>().Value.ClientSecret;
        }

        const string code = "shopify-auth-code";
        const string shop = "my-real-shop";
        const string timestamp = "1700000000";
        var hmac = ComputeShopifyHmacHex(clientSecret, code, shop, state, timestamp);

        using var noRedirectClient = shopifyFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
        });
        var callbackResponse = await noRedirectClient.SendAsync(Authed(
            HttpMethod.Get, BuildShopifyCallbackUrl(code, hmac, shop, state, timestamp), ownerCookie));

        Assert.Equal(HttpStatusCode.Redirect, callbackResponse.StatusCode);
        Assert.Contains("integrationError=exchange_failed", callbackResponse.Headers.Location!.ToString());

        await using var scope2 = _factory.Services.CreateAsyncScope();
        var db = scope2.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.Integrations.IgnoreQueryFilters().AnyAsync(i => i.BusinessId == businessId));

        await shopifyFactory.DisposeAsync();
    }

    [Fact]
    public async Task Connect_Shopify_InvalidShopName_ReturnsBadRequest_NoIntegrationRowCreated()
    {
        var (businessId, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Shopify Invalid Shop Business");

        var response = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, "/api/integrations/shopify/connect", ownerCookie),
            new ConnectIntegrationRequest("https://not-a-bare-shop-name.myshopify.com")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.Integrations.IgnoreQueryFilters().AnyAsync(i => i.BusinessId == businessId));
    }
}
