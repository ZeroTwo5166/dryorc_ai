using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using backend.Auth;
using backend.Constants;
using backend.Controllers;
using backend.Data;
using backend.Enums;
using backend.Models;
using backend.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace backend.Tests;

/// <summary>
/// HTTP-level tests for BusinessController (step 49b), run against the real
/// app + real dev DB via NivoraApiFactory — same pattern as
/// TagModeMappingsApiTests. Covers OwnerOnly write / AnyStaff read
/// authorization, tenant isolation, the Shopify Integration.IsActive
/// write-time precondition (including the true→true-while-disconnected
/// interpretation), the rejected-write-leaves-flag-unchanged guarantee, and
/// audit behavior (audited on real change, not audited on idempotent no-op
/// or on a 409 precondition rejection). See
/// docs/handoffs/step-49b-cancelordertoolenabled-backend-contract-dashboard-toggle-design.md.
/// </summary>
public class BusinessApiTests : IAsyncLifetime
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

    /// <summary>Directly seeds an Integration row — there is no test-reachable
    /// OAuth flow, same reasoning as OrderCancelApprovedActionExecutorTests's
    /// own seeding helper. EncryptedAccessToken is never decrypted by
    /// anything BusinessController touches, so a plain placeholder is fine.</summary>
    private async Task SeedShopifyIntegrationAsync(Guid businessId, bool isActive)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Integrations.Add(new Integration
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            Platform = ShopifyIntegrationOAuthProvider.PlatformName,
            EncryptedAccessToken = "test-encrypted-token",
            ExternalAccountIdentifier = $"test-shop-{Guid.NewGuid():N}",
            IsActive = isActive,
            ConnectedAt = DateTimeOffset.UtcNow,
            DisconnectedAt = isActive ? null : DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task<bool> GetPersistedFlagAsync(Guid businessId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var business = await db.Businesses.SingleAsync(b => b.Id == businessId);
        return business.CancelOrderToolEnabled;
    }

    private async Task<bool> GetPersistedConversationAutoCloseEnabledAsync(Guid businessId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var business = await db.Businesses.SingleAsync(b => b.Id == businessId);
        return business.ConversationAutoCloseEnabled;
    }

    private async Task<int> GetPersistedConversationInactivityCloseDaysAsync(Guid businessId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var business = await db.Businesses.SingleAsync(b => b.Id == businessId);
        return business.ConversationInactivityCloseDays;
    }

    private async Task<int> AuditCountAsync(Guid businessId) =>
        await GetScopedDbAsync(async db => await db.AuditLogs.IgnoreQueryFilters()
            .CountAsync(a => a.BusinessId == businessId && a.EntityType == AuditEntityTypes.Business));

    private async Task<T> GetScopedDbAsync<T>(Func<AppDbContext, Task<T>> query)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await query(db);
    }

    // ── Read: AnyStaff, via Me() ──────────────────────────────────────

    [Fact]
    public async Task Me_ExposesCancelOrderToolEnabled_ForAnyStaff()
    {
        var (businessId, _, ownerClient, _) = await SetUpBusinessWithOwnerAsync("Business Read Me Business");
        using (ownerClient) { }

        var agentEmail = $"agent-{Guid.NewGuid():N}@example.com";
        await SeedStaffIntoBusinessAsync(businessId, agentEmail, RoleNames.Agent);
        using var agentClient = NewClient();
        var (_, _, agentCookie) = await LoginAsync(agentClient, agentEmail);

        var meResponse = await agentClient.SendAsync(Authed(HttpMethod.Get, "/api/auth/me", agentCookie!));
        Assert.Equal(HttpStatusCode.OK, meResponse.StatusCode);
        var me = await meResponse.Content.ReadFromJsonAsync<MeResponse>();
        Assert.NotNull(me);
        Assert.False(me!.CancelOrderToolEnabled); // default false, never touched yet
    }

    // ── Authorization: write is OwnerOnly ─────────────────────────────

    [Fact]
    public async Task SetCancelOrderToolEnabled_AgentForbidden()
    {
        var (businessId, _, ownerClient, _) = await SetUpBusinessWithOwnerAsync("Business Agent Write Business");
        using (ownerClient) { }

        var agentEmail = $"agent-{Guid.NewGuid():N}@example.com";
        await SeedStaffIntoBusinessAsync(businessId, agentEmail, RoleNames.Agent);
        using var agentClient = NewClient();
        var (_, _, agentCookie) = await LoginAsync(agentClient, agentEmail);

        var response = await agentClient.SendAsync(WithBody(
            Authed(HttpMethod.Put, "/api/business/cancel-order-tool", agentCookie!),
            new SetCancelOrderToolEnabledRequest(false)));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task SetCancelOrderToolEnabled_AdminForbidden()
    {
        var (businessId, _, ownerClient, _) = await SetUpBusinessWithOwnerAsync("Business Admin Write Business");
        using (ownerClient) { }

        var adminEmail = $"admin-{Guid.NewGuid():N}@example.com";
        await SeedStaffIntoBusinessAsync(businessId, adminEmail, RoleNames.Admin);
        using var adminClient = NewClient();
        var (_, _, adminCookie) = await LoginAsync(adminClient, adminEmail);

        var response = await adminClient.SendAsync(WithBody(
            Authed(HttpMethod.Put, "/api/business/cancel-order-tool", adminCookie!),
            new SetCancelOrderToolEnabledRequest(false)));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode); // OwnerOnly, deliberately not OwnerOrAdmin
    }

    // ── Shopify precondition ───────────────────────────────────────────

    [Fact]
    public async Task Enable_WithActiveShopifyIntegration_Succeeds_AndPersists()
    {
        var (businessId, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Business Enable Active Business");
        using var _ = ownerClient;
        await SeedShopifyIntegrationAsync(businessId, isActive: true);

        var response = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Put, "/api/business/cancel-order-tool", ownerCookie),
            new SetCancelOrderToolEnabledRequest(true)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<SetCancelOrderToolEnabledResponse>();
        Assert.True(body!.CancelOrderToolEnabled);
        Assert.True(await GetPersistedFlagAsync(businessId));
    }

    [Fact]
    public async Task Enable_WithInactiveShopifyIntegration_Returns409_AndLeavesFlagUnchanged()
    {
        var (businessId, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Business Enable Inactive Business");
        using var _ = ownerClient;
        await SeedShopifyIntegrationAsync(businessId, isActive: false);

        var response = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Put, "/api/business/cancel-order-tool", ownerCookie),
            new SetCancelOrderToolEnabledRequest(true)));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.False(await GetPersistedFlagAsync(businessId));
    }

    [Fact]
    public async Task Enable_WithNoShopifyIntegrationRowAtAll_Returns409_AndLeavesFlagUnchanged()
    {
        var (businessId, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Business Enable No Integration Business");
        using var _ = ownerClient;
        // No SeedShopifyIntegrationAsync call — zero Integration rows for this business.

        var response = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Put, "/api/business/cancel-order-tool", ownerCookie),
            new SetCancelOrderToolEnabledRequest(true)));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.False(await GetPersistedFlagAsync(businessId));
    }

    [Fact]
    public async Task Disable_WithInactiveShopifyIntegration_Succeeds()
    {
        var (businessId, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Business Disable Inactive Business");
        using var _ = ownerClient;
        await SeedShopifyIntegrationAsync(businessId, isActive: true);

        // Enable while connected first, so there is a true value to disable.
        var enableResponse = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Put, "/api/business/cancel-order-tool", ownerCookie),
            new SetCancelOrderToolEnabledRequest(true)));
        Assert.Equal(HttpStatusCode.OK, enableResponse.StatusCode);

        // Now disconnect (flip IsActive false) and confirm disable still works.
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var integration = await db.Integrations.IgnoreQueryFilters().SingleAsync(i => i.BusinessId == businessId);
            integration.IsActive = false;
            await db.SaveChangesAsync();
        }

        var disableResponse = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Put, "/api/business/cancel-order-tool", ownerCookie),
            new SetCancelOrderToolEnabledRequest(false)));

        Assert.Equal(HttpStatusCode.OK, disableResponse.StatusCode);
        Assert.False(await GetPersistedFlagAsync(businessId));
    }

    [Fact]
    public async Task Disable_WithNoShopifyIntegrationRowAtAll_Succeeds()
    {
        var (businessId, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Business Disable No Integration Business");
        using var _ = ownerClient;

        var response = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Put, "/api/business/cancel-order-tool", ownerCookie),
            new SetCancelOrderToolEnabledRequest(false)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(await GetPersistedFlagAsync(businessId));
    }

    [Fact]
    public async Task Enable_TrueToTrue_WhileDisconnected_StillRejected()
    {
        var (businessId, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Business True To True Business");
        using var _ = ownerClient;
        await SeedShopifyIntegrationAsync(businessId, isActive: true);

        var firstEnable = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Put, "/api/business/cancel-order-tool", ownerCookie),
            new SetCancelOrderToolEnabledRequest(true)));
        Assert.Equal(HttpStatusCode.OK, firstEnable.StatusCode);

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var integration = await db.Integrations.IgnoreQueryFilters().SingleAsync(i => i.BusinessId == businessId);
            integration.IsActive = false;
            await db.SaveChangesAsync();
        }

        // Flag is already true; Shopify is now disconnected. A repeated
        // Enabled=true request must still be rejected — the precondition is
        // evaluated even for a same-value write (design doc §5).
        var secondEnable = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Put, "/api/business/cancel-order-tool", ownerCookie),
            new SetCancelOrderToolEnabledRequest(true)));

        Assert.Equal(HttpStatusCode.Conflict, secondEnable.StatusCode);
        Assert.True(await GetPersistedFlagAsync(businessId)); // unchanged — still true, per §6
    }

    // ── Tenant isolation ───────────────────────────────────────────────

    [Fact]
    public async Task Enable_NeverAffectsAnotherBusiness()
    {
        var (businessIdA, _, ownerClientA, ownerCookieA) = await SetUpBusinessWithOwnerAsync("Business Tenant A");
        using var _a = ownerClientA;
        await SeedShopifyIntegrationAsync(businessIdA, isActive: true);

        var (businessIdB, _, ownerClientB, ownerCookieB) = await SetUpBusinessWithOwnerAsync("Business Tenant B");
        using var _b = ownerClientB;
        // Business B has no Shopify integration at all.

        var responseA = await ownerClientA.SendAsync(WithBody(
            Authed(HttpMethod.Put, "/api/business/cancel-order-tool", ownerCookieA),
            new SetCancelOrderToolEnabledRequest(true)));
        Assert.Equal(HttpStatusCode.OK, responseA.StatusCode);

        // Business B's owner writing on their own session must only ever
        // affect Business B — there is no BusinessId parameter to send, so
        // this also proves SetCancelOrderToolEnabledRequest cannot be used
        // to target another tenant even if a caller tried.
        var responseB = await ownerClientB.SendAsync(WithBody(
            Authed(HttpMethod.Put, "/api/business/cancel-order-tool", ownerCookieB),
            new SetCancelOrderToolEnabledRequest(true)));
        Assert.Equal(HttpStatusCode.Conflict, responseB.StatusCode); // B has no active integration

        Assert.True(await GetPersistedFlagAsync(businessIdA));
        Assert.False(await GetPersistedFlagAsync(businessIdB));
    }

    // ── Audit logging ──────────────────────────────────────────────────

    [Fact]
    public async Task Enable_WritesExactlyOneAuditLogRow_WithCorrectFields()
    {
        var (businessId, ownerId, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Business Audit Enable Business");
        using var _ = ownerClient;
        await SeedShopifyIntegrationAsync(businessId, isActive: true);

        var response = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Put, "/api/business/cancel-order-tool", ownerCookie),
            new SetCancelOrderToolEnabledRequest(true)));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var auditLogs = await db.AuditLogs.IgnoreQueryFilters()
            .Where(a => a.BusinessId == businessId && a.EntityType == AuditEntityTypes.Business)
            .ToListAsync();

        var entry = Assert.Single(auditLogs);
        Assert.Equal(businessId.ToString(), entry.EntityId);
        Assert.Equal(AuditEventTypes.BusinessCancelOrderToolEnabled, entry.EventType);
        Assert.Equal(ActorType.NivoraBusinessStaff, entry.Actor);
        Assert.Equal(ownerId, entry.ChangedByUserId);
        Assert.NotNull(entry.OldValue);
        Assert.NotNull(entry.NewValue);

        using var oldValueDoc = JsonDocument.Parse(entry.OldValue!);
        Assert.False(oldValueDoc.RootElement.GetProperty("CancelOrderToolEnabled").GetBoolean());
        using var newValueDoc = JsonDocument.Parse(entry.NewValue!);
        Assert.True(newValueDoc.RootElement.GetProperty("CancelOrderToolEnabled").GetBoolean());
        // Never the full Business entity — only the one field that matters.
        Assert.Equal(1, newValueDoc.RootElement.EnumerateObject().Count());
    }

    [Fact]
    public async Task Disable_WritesExactlyOneAuditLogRow_WithDisabledEventType()
    {
        var (businessId, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Business Audit Disable Business");
        using var _ = ownerClient;
        await SeedShopifyIntegrationAsync(businessId, isActive: true);

        await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Put, "/api/business/cancel-order-tool", ownerCookie),
            new SetCancelOrderToolEnabledRequest(true)));

        var disableResponse = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Put, "/api/business/cancel-order-tool", ownerCookie),
            new SetCancelOrderToolEnabledRequest(false)));
        Assert.Equal(HttpStatusCode.OK, disableResponse.StatusCode);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var auditLogs = await db.AuditLogs.IgnoreQueryFilters()
            .Where(a => a.BusinessId == businessId && a.EntityType == AuditEntityTypes.Business)
            .OrderBy(a => a.CreatedAt)
            .ToListAsync();

        Assert.Equal(2, auditLogs.Count); // one for enable, one for disable
        Assert.Equal(AuditEventTypes.BusinessCancelOrderToolDisabled, auditLogs[1].EventType);
    }

    [Fact]
    public async Task IdempotentDisable_WritesNoAuditLogRow()
    {
        var (businessId, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Business Idempotent Disable Business");
        using var _ = ownerClient;
        // Flag already defaults to false — disabling again is a pure no-op.

        var response = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Put, "/api/business/cancel-order-tool", ownerCookie),
            new SetCancelOrderToolEnabledRequest(false)));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var auditCount = await db.AuditLogs.IgnoreQueryFilters()
            .CountAsync(a => a.BusinessId == businessId && a.EntityType == AuditEntityTypes.Business);
        Assert.Equal(0, auditCount);
    }

    [Fact]
    public async Task RejectedEnable_WritesNoAuditLogRow()
    {
        var (businessId, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Business Rejected Enable Audit Business");
        using var _ = ownerClient;
        // No Shopify integration seeded — the enable attempt below is rejected.

        var response = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Put, "/api/business/cancel-order-tool", ownerCookie),
            new SetCancelOrderToolEnabledRequest(true)));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var auditCount = await db.AuditLogs.IgnoreQueryFilters()
            .CountAsync(a => a.BusinessId == businessId && a.EntityType == AuditEntityTypes.Business);
        Assert.Equal(0, auditCount); // matches Step 48's own un-audited ShopifyIntegrationUnavailable refusal
    }

    // ═════════════════════════════════════════════════════════════════
    // Step 25 Candidate 2, Phase 1 — ConversationAutoCloseEnabled /
    // ConversationInactivityCloseDays (implementation plan §1)
    // ═════════════════════════════════════════════════════════════════

    // ── ConversationAutoCloseEnabled: authorization ────────────────────

    [Fact]
    public async Task SetConversationAutoCloseEnabled_AgentForbidden()
    {
        var (businessId, _, ownerClient, _) = await SetUpBusinessWithOwnerAsync("ConvAutoClose Agent Business");
        using (ownerClient) { }

        var agentEmail = $"agent-{Guid.NewGuid():N}@example.com";
        await SeedStaffIntoBusinessAsync(businessId, agentEmail, RoleNames.Agent);
        using var agentClient = NewClient();
        var (_, _, agentCookie) = await LoginAsync(agentClient, agentEmail);

        var response = await agentClient.SendAsync(WithBody(
            Authed(HttpMethod.Put, "/api/business/conversation-auto-close", agentCookie!),
            new SetConversationAutoCloseEnabledRequest(true)));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(await GetPersistedConversationAutoCloseEnabledAsync(businessId));
    }

    [Fact]
    public async Task SetConversationAutoCloseEnabled_AdminForbidden()
    {
        var (businessId, _, ownerClient, _) = await SetUpBusinessWithOwnerAsync("ConvAutoClose Admin Business");
        using (ownerClient) { }

        var adminEmail = $"admin-{Guid.NewGuid():N}@example.com";
        await SeedStaffIntoBusinessAsync(businessId, adminEmail, RoleNames.Admin);
        using var adminClient = NewClient();
        var (_, _, adminCookie) = await LoginAsync(adminClient, adminEmail);

        var response = await adminClient.SendAsync(WithBody(
            Authed(HttpMethod.Put, "/api/business/conversation-auto-close", adminCookie!),
            new SetConversationAutoCloseEnabledRequest(true)));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode); // OwnerOnly, deliberately not OwnerOrAdmin
        Assert.False(await GetPersistedConversationAutoCloseEnabledAsync(businessId));
    }

    [Fact]
    public async Task SetConversationAutoCloseEnabled_Owner_Allowed()
    {
        var (businessId, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("ConvAutoClose Owner Business");
        using var _ = ownerClient;

        var response = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Put, "/api/business/conversation-auto-close", ownerCookie),
            new SetConversationAutoCloseEnabledRequest(true)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<SetConversationAutoCloseEnabledResponse>();
        Assert.True(body!.ConversationAutoCloseEnabled);
        Assert.True(await GetPersistedConversationAutoCloseEnabledAsync(businessId));
    }

    // ── ConversationAutoCloseEnabled: audit transition/no-op ───────────

    [Fact]
    public async Task SetConversationAutoCloseEnabled_FalseToTrue_WritesAuditLogRow()
    {
        var (businessId, ownerId, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("ConvAutoClose F2T Business");
        using var _ = ownerClient;

        var response = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Put, "/api/business/conversation-auto-close", ownerCookie),
            new SetConversationAutoCloseEnabledRequest(true)));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var auditLogs = await GetScopedDbAsync(async db => await db.AuditLogs.IgnoreQueryFilters()
            .Where(a => a.BusinessId == businessId && a.EntityType == AuditEntityTypes.Business).ToListAsync());
        var entry = Assert.Single(auditLogs);
        Assert.Equal(AuditEventTypes.BusinessConversationAutoCloseEnabled, entry.EventType);
        Assert.Equal(ActorType.NivoraBusinessStaff, entry.Actor);
        Assert.Equal(ownerId, entry.ChangedByUserId);
        using var oldValueDoc = JsonDocument.Parse(entry.OldValue!);
        Assert.False(oldValueDoc.RootElement.GetProperty("ConversationAutoCloseEnabled").GetBoolean());
        using var newValueDoc = JsonDocument.Parse(entry.NewValue!);
        Assert.True(newValueDoc.RootElement.GetProperty("ConversationAutoCloseEnabled").GetBoolean());
    }

    [Fact]
    public async Task SetConversationAutoCloseEnabled_TrueToFalse_WritesAuditLogRow()
    {
        var (businessId, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("ConvAutoClose T2F Business");
        using var _ = ownerClient;

        await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Put, "/api/business/conversation-auto-close", ownerCookie),
            new SetConversationAutoCloseEnabledRequest(true)));

        var response = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Put, "/api/business/conversation-auto-close", ownerCookie),
            new SetConversationAutoCloseEnabledRequest(false)));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var auditLogs = await GetScopedDbAsync(async db => await db.AuditLogs.IgnoreQueryFilters()
            .Where(a => a.BusinessId == businessId && a.EntityType == AuditEntityTypes.Business)
            .OrderBy(a => a.CreatedAt).ToListAsync());
        Assert.Equal(2, auditLogs.Count); // one for true, one for false
        Assert.Equal(AuditEventTypes.BusinessConversationAutoCloseDisabled, auditLogs[1].EventType);
    }

    [Fact]
    public async Task SetConversationAutoCloseEnabled_TrueToTrue_WritesNoAuditLogRow()
    {
        var (businessId, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("ConvAutoClose T2T Business");
        using var _ = ownerClient;

        await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Put, "/api/business/conversation-auto-close", ownerCookie),
            new SetConversationAutoCloseEnabledRequest(true)));
        Assert.Equal(1, await AuditCountAsync(businessId));

        var response = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Put, "/api/business/conversation-auto-close", ownerCookie),
            new SetConversationAutoCloseEnabledRequest(true)));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal(1, await AuditCountAsync(businessId)); // no second row
        Assert.True(await GetPersistedConversationAutoCloseEnabledAsync(businessId));
    }

    [Fact]
    public async Task SetConversationAutoCloseEnabled_FalseToFalse_WritesNoAuditLogRow()
    {
        var (businessId, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("ConvAutoClose F2F Business");
        using var _ = ownerClient;
        // Flag already defaults to false — disabling again is a pure no-op.

        var response = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Put, "/api/business/conversation-auto-close", ownerCookie),
            new SetConversationAutoCloseEnabledRequest(false)));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal(0, await AuditCountAsync(businessId));
        Assert.False(await GetPersistedConversationAutoCloseEnabledAsync(businessId));
    }

    // ── ConversationAutoCloseEnabled: tenant isolation ─────────────────

    [Fact]
    public async Task SetConversationAutoCloseEnabled_NeverAffectsAnotherBusiness()
    {
        var (businessIdA, _, ownerClientA, ownerCookieA) = await SetUpBusinessWithOwnerAsync("ConvAutoClose Tenant A");
        using var _a = ownerClientA;
        var (businessIdB, _, ownerClientB, _) = await SetUpBusinessWithOwnerAsync("ConvAutoClose Tenant B");
        using var _b = ownerClientB;

        var responseA = await ownerClientA.SendAsync(WithBody(
            Authed(HttpMethod.Put, "/api/business/conversation-auto-close", ownerCookieA),
            new SetConversationAutoCloseEnabledRequest(true)));
        Assert.Equal(HttpStatusCode.OK, responseA.StatusCode);

        Assert.True(await GetPersistedConversationAutoCloseEnabledAsync(businessIdA));
        Assert.False(await GetPersistedConversationAutoCloseEnabledAsync(businessIdB)); // untouched
    }

    // ── ConversationInactivityCloseDays: default ───────────────────────

    [Fact]
    public async Task NewBusiness_ConversationInactivityCloseDays_DefaultsToFourteen()
    {
        var (businessId, _, ownerClient, _) = await SetUpBusinessWithOwnerAsync("ConvInactivity Default Business");
        using (ownerClient) { }

        Assert.Equal(14, await GetPersistedConversationInactivityCloseDaysAsync(businessId));
    }

    // ── ConversationInactivityCloseDays: authorization ─────────────────

    [Fact]
    public async Task SetConversationInactivityCloseDays_AgentForbidden()
    {
        var (businessId, _, ownerClient, _) = await SetUpBusinessWithOwnerAsync("ConvInactivity Agent Business");
        using (ownerClient) { }

        var agentEmail = $"agent-{Guid.NewGuid():N}@example.com";
        await SeedStaffIntoBusinessAsync(businessId, agentEmail, RoleNames.Agent);
        using var agentClient = NewClient();
        var (_, _, agentCookie) = await LoginAsync(agentClient, agentEmail);

        var response = await agentClient.SendAsync(WithBody(
            Authed(HttpMethod.Put, "/api/business/conversation-inactivity-close-days", agentCookie!),
            new SetConversationInactivityCloseDaysRequest(30)));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(14, await GetPersistedConversationInactivityCloseDaysAsync(businessId));
    }

    [Fact]
    public async Task SetConversationInactivityCloseDays_AdminForbidden()
    {
        var (businessId, _, ownerClient, _) = await SetUpBusinessWithOwnerAsync("ConvInactivity Admin Business");
        using (ownerClient) { }

        var adminEmail = $"admin-{Guid.NewGuid():N}@example.com";
        await SeedStaffIntoBusinessAsync(businessId, adminEmail, RoleNames.Admin);
        using var adminClient = NewClient();
        var (_, _, adminCookie) = await LoginAsync(adminClient, adminEmail);

        var response = await adminClient.SendAsync(WithBody(
            Authed(HttpMethod.Put, "/api/business/conversation-inactivity-close-days", adminCookie!),
            new SetConversationInactivityCloseDaysRequest(30)));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(14, await GetPersistedConversationInactivityCloseDaysAsync(businessId));
    }

    [Fact]
    public async Task SetConversationInactivityCloseDays_Owner_Allowed()
    {
        var (businessId, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("ConvInactivity Owner Business");
        using var _ = ownerClient;

        var response = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Put, "/api/business/conversation-inactivity-close-days", ownerCookie),
            new SetConversationInactivityCloseDaysRequest(30)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<SetConversationInactivityCloseDaysResponse>();
        Assert.Equal(30, body!.ConversationInactivityCloseDays);
        Assert.Equal(30, await GetPersistedConversationInactivityCloseDaysAsync(businessId));
    }

    // ── ConversationInactivityCloseDays: range validation ──────────────

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(366)]
    [InlineData(1000)]
    public async Task SetConversationInactivityCloseDays_OutOfRange_Returns400_NoWrite_NoAudit(int invalidDays)
    {
        var (businessId, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync($"ConvInactivity OOR {invalidDays} Business");
        using var _ = ownerClient;

        var response = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Put, "/api/business/conversation-inactivity-close-days", ownerCookie),
            new SetConversationInactivityCloseDaysRequest(invalidDays)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(14, await GetPersistedConversationInactivityCloseDaysAsync(businessId)); // untouched
        Assert.Equal(0, await AuditCountAsync(businessId));
    }

    [Theory]
    [InlineData(1)]   // MinDays boundary
    [InlineData(365)] // MaxDays boundary
    public async Task SetConversationInactivityCloseDays_AtBoundary_Succeeds(int boundaryDays)
    {
        var (businessId, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync($"ConvInactivity Boundary {boundaryDays} Business");
        using var _ = ownerClient;

        var response = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Put, "/api/business/conversation-inactivity-close-days", ownerCookie),
            new SetConversationInactivityCloseDaysRequest(boundaryDays)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(boundaryDays, await GetPersistedConversationInactivityCloseDaysAsync(businessId));
    }

    // ── ConversationInactivityCloseDays: audit transition/no-op ────────

    [Fact]
    public async Task SetConversationInactivityCloseDays_ChangingValue_WritesAuditLogRow()
    {
        var (businessId, ownerId, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("ConvInactivity Audit Business");
        using var _ = ownerClient;

        var response = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Put, "/api/business/conversation-inactivity-close-days", ownerCookie),
            new SetConversationInactivityCloseDaysRequest(45)));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var auditLogs = await GetScopedDbAsync(async db => await db.AuditLogs.IgnoreQueryFilters()
            .Where(a => a.BusinessId == businessId && a.EntityType == AuditEntityTypes.Business).ToListAsync());
        var entry = Assert.Single(auditLogs);
        Assert.Equal(AuditEventTypes.BusinessConversationInactivityCloseDaysChanged, entry.EventType);
        Assert.Equal(ActorType.NivoraBusinessStaff, entry.Actor);
        Assert.Equal(ownerId, entry.ChangedByUserId);
        using var oldValueDoc = JsonDocument.Parse(entry.OldValue!);
        Assert.Equal(14, oldValueDoc.RootElement.GetProperty("ConversationInactivityCloseDays").GetInt32());
        using var newValueDoc = JsonDocument.Parse(entry.NewValue!);
        Assert.Equal(45, newValueDoc.RootElement.GetProperty("ConversationInactivityCloseDays").GetInt32());
    }

    [Fact]
    public async Task SetConversationInactivityCloseDays_SettingCurrentValueAgain_WritesNoAuditLogRow()
    {
        var (businessId, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("ConvInactivity Idempotent Business");
        using var _ = ownerClient;

        await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Put, "/api/business/conversation-inactivity-close-days", ownerCookie),
            new SetConversationInactivityCloseDaysRequest(45)));
        Assert.Equal(1, await AuditCountAsync(businessId));

        var response = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Put, "/api/business/conversation-inactivity-close-days", ownerCookie),
            new SetConversationInactivityCloseDaysRequest(45)));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal(1, await AuditCountAsync(businessId)); // no second row
        Assert.Equal(45, await GetPersistedConversationInactivityCloseDaysAsync(businessId));
    }

    [Fact]
    public async Task SetConversationInactivityCloseDays_DefaultValueSetAgain_WritesNoAuditLogRow()
    {
        var (businessId, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("ConvInactivity Default NoOp Business");
        using var _ = ownerClient;
        // Already 14 (the default) — setting it to 14 again is a pure no-op.

        var response = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Put, "/api/business/conversation-inactivity-close-days", ownerCookie),
            new SetConversationInactivityCloseDaysRequest(14)));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal(0, await AuditCountAsync(businessId));
    }

    // ── ConversationInactivityCloseDays: tenant isolation ──────────────

    [Fact]
    public async Task SetConversationInactivityCloseDays_NeverAffectsAnotherBusiness()
    {
        var (businessIdA, _, ownerClientA, ownerCookieA) = await SetUpBusinessWithOwnerAsync("ConvInactivity Tenant A");
        using var _a = ownerClientA;
        var (businessIdB, _, ownerClientB, _) = await SetUpBusinessWithOwnerAsync("ConvInactivity Tenant B");
        using var _b = ownerClientB;

        var responseA = await ownerClientA.SendAsync(WithBody(
            Authed(HttpMethod.Put, "/api/business/conversation-inactivity-close-days", ownerCookieA),
            new SetConversationInactivityCloseDaysRequest(90)));
        Assert.Equal(HttpStatusCode.OK, responseA.StatusCode);

        Assert.Equal(90, await GetPersistedConversationInactivityCloseDaysAsync(businessIdA));
        Assert.Equal(14, await GetPersistedConversationInactivityCloseDaysAsync(businessIdB)); // untouched, still default
    }

    // ── Cross-field independence (design §5 / plan §1.5) ───────────────

    [Fact]
    public async Task SetConversationInactivityCloseDays_DoesNotEnableAutoClose()
    {
        var (businessId, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("ConvInactivity No CrossEnable Business");
        using var _ = ownerClient;

        var response = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Put, "/api/business/conversation-inactivity-close-days", ownerCookie),
            new SetConversationInactivityCloseDaysRequest(200)));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.False(await GetPersistedConversationAutoCloseEnabledAsync(businessId)); // still default false
    }

    [Fact]
    public async Task SetConversationAutoCloseEnabled_DoesNotChangeInactivityCloseDays()
    {
        var (businessId, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("ConvAutoClose No CrossChange Business");
        using var _ = ownerClient;

        var response = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Put, "/api/business/conversation-auto-close", ownerCookie),
            new SetConversationAutoCloseEnabledRequest(true)));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal(14, await GetPersistedConversationInactivityCloseDaysAsync(businessId)); // still default
    }
}
