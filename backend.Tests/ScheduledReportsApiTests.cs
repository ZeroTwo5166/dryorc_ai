using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using backend.Auth;
using backend.Constants;
using backend.Controllers;
using backend.Data;
using backend.Enums;
using backend.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace backend.Tests;

/// <summary>
/// HTTP-level tests for BusinessController.SetScheduledReportsEnabled (Step
/// 24 Phase 1), run against the real app + real dev DB via NivoraApiFactory —
/// same pattern as AiBusinessAnalystApiTests's own coverage, which this file
/// mirrors structurally. Like AiBusinessAnalystEnabled and unlike
/// CancelOrderToolEnabled, this flag has no external-integration
/// precondition, so this file's scope is the same narrower shape: OwnerOnly
/// authorization, idempotent no-op vs. real-transition audit behavior, Me()
/// exposure, tenant isolation, and a direct real-DB confirmation of the
/// migration itself. This phase (design doc's Phase 1) creates only the flag
/// and this toggle — no scheduler, execution, or background-service
/// assertions belong in this file; those are Phases 4-7's own scope.
/// </summary>
public class ScheduledReportsApiTests : IAsyncLifetime
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

    private async Task<bool> GetPersistedFlagAsync(Guid businessId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var business = await db.Businesses.SingleAsync(b => b.Id == businessId);
        return business.ScheduledReportsEnabled;
    }

    // ── Migration correctness — direct real-DB confirmation ──────────────

    [Fact]
    public async Task Migration_ScheduledReportsEnabledColumn_IsBooleanNotNull_AndDefaultsFalseOnExistingRows()
    {
        // "Existing rows" = a business created before this test touches the
        // flag at all — confirms the column's own DEFAULT FALSE applied
        // migration-side, not merely that the model's C# default does.
        var (businessId, _, ownerClient, _) = await SetUpBusinessWithOwnerAsync("Migration Confirmation Business (Reports)");
        using (ownerClient) { }

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync();

        await using (var columnCmd = connection.CreateCommand())
        {
            columnCmd.CommandText =
                "SELECT data_type, is_nullable FROM information_schema.columns " +
                "WHERE table_name = 'Businesses' AND column_name = 'ScheduledReportsEnabled'";
            await using var reader = await columnCmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync(), "ScheduledReportsEnabled column does not exist on Businesses.");
            Assert.Equal("boolean", reader.GetString(0));
            Assert.Equal("NO", reader.GetString(1));
        }

        await using (var valueCmd = connection.CreateCommand())
        {
            valueCmd.CommandText = "SELECT \"ScheduledReportsEnabled\" FROM \"Businesses\" WHERE \"Id\" = @id";
            var param = valueCmd.CreateParameter();
            param.ParameterName = "@id";
            param.Value = businessId;
            valueCmd.Parameters.Add(param);
            var value = await valueCmd.ExecuteScalarAsync();
            Assert.Equal(false, value);
        }
    }

    // ── Read: AnyStaff, via Me() ──────────────────────────────────────

    [Fact]
    public async Task Me_ExposesScheduledReportsEnabled_ForAnyStaff_DefaultFalse()
    {
        var (businessId, _, ownerClient, _) = await SetUpBusinessWithOwnerAsync("Reports Read Me Business");
        using (ownerClient) { }

        var agentEmail = $"agent-{Guid.NewGuid():N}@example.com";
        await SeedStaffIntoBusinessAsync(businessId, agentEmail, RoleNames.Agent);
        using var agentClient = NewClient();
        var (_, _, agentCookie) = await LoginAsync(agentClient, agentEmail);

        var meResponse = await agentClient.SendAsync(Authed(HttpMethod.Get, "/api/auth/me", agentCookie!));
        Assert.Equal(HttpStatusCode.OK, meResponse.StatusCode);
        var me = await meResponse.Content.ReadFromJsonAsync<MeResponse>();
        Assert.NotNull(me);
        Assert.False(me!.ScheduledReportsEnabled); // default false, never touched yet
    }

    [Fact]
    public async Task Me_ReflectsScheduledReportsEnabled_AfterToggle()
    {
        var (businessId, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Reports Me Reflects Toggle Business");
        using var _ = ownerClient;

        var toggleResponse = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Put, "/api/business/scheduled-reports", ownerCookie),
            new SetScheduledReportsEnabledRequest(true)));
        Assert.Equal(HttpStatusCode.OK, toggleResponse.StatusCode);

        var meResponse = await ownerClient.SendAsync(Authed(HttpMethod.Get, "/api/auth/me", ownerCookie));
        Assert.Equal(HttpStatusCode.OK, meResponse.StatusCode);
        var me = await meResponse.Content.ReadFromJsonAsync<MeResponse>();
        Assert.NotNull(me);
        Assert.True(me!.ScheduledReportsEnabled);
    }

    // ── Authorization: write is OwnerOnly ─────────────────────────────

    [Fact]
    public async Task SetScheduledReportsEnabled_AgentForbidden()
    {
        var (businessId, _, ownerClient, _) = await SetUpBusinessWithOwnerAsync("Reports Agent Write Business");
        using (ownerClient) { }

        var agentEmail = $"agent-{Guid.NewGuid():N}@example.com";
        await SeedStaffIntoBusinessAsync(businessId, agentEmail, RoleNames.Agent);
        using var agentClient = NewClient();
        var (_, _, agentCookie) = await LoginAsync(agentClient, agentEmail);

        var response = await agentClient.SendAsync(WithBody(
            Authed(HttpMethod.Put, "/api/business/scheduled-reports", agentCookie!),
            new SetScheduledReportsEnabledRequest(true)));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(await GetPersistedFlagAsync(businessId)); // rejected write leaves flag unchanged
    }

    [Fact]
    public async Task SetScheduledReportsEnabled_AdminForbidden()
    {
        var (businessId, _, ownerClient, _) = await SetUpBusinessWithOwnerAsync("Reports Admin Write Business");
        using (ownerClient) { }

        var adminEmail = $"admin-{Guid.NewGuid():N}@example.com";
        await SeedStaffIntoBusinessAsync(businessId, adminEmail, RoleNames.Admin);
        using var adminClient = NewClient();
        var (_, _, adminCookie) = await LoginAsync(adminClient, adminEmail);

        var response = await adminClient.SendAsync(WithBody(
            Authed(HttpMethod.Put, "/api/business/scheduled-reports", adminCookie!),
            new SetScheduledReportsEnabledRequest(true)));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode); // OwnerOnly, deliberately not OwnerOrAdmin
    }

    [Fact]
    public async Task SetScheduledReportsEnabled_UnauthenticatedRejected()
    {
        using var anonClient = NewClient();

        var response = await anonClient.PutAsJsonAsync(
            "/api/business/scheduled-reports", new SetScheduledReportsEnabledRequest(true));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── Real transitions ────────────────────────────────────────────────

    [Fact]
    public async Task Enable_Succeeds_AndPersists()
    {
        var (businessId, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Reports Enable Business");
        using var _ = ownerClient;

        var response = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Put, "/api/business/scheduled-reports", ownerCookie),
            new SetScheduledReportsEnabledRequest(true)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<SetScheduledReportsEnabledResponse>();
        Assert.True(body!.ScheduledReportsEnabled);
        Assert.True(await GetPersistedFlagAsync(businessId));
    }

    [Fact]
    public async Task Disable_AfterEnable_Succeeds_AndPersists()
    {
        var (businessId, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Reports Disable Business");
        using var _ = ownerClient;

        var enableResponse = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Put, "/api/business/scheduled-reports", ownerCookie),
            new SetScheduledReportsEnabledRequest(true)));
        Assert.Equal(HttpStatusCode.OK, enableResponse.StatusCode);

        var disableResponse = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Put, "/api/business/scheduled-reports", ownerCookie),
            new SetScheduledReportsEnabledRequest(false)));

        Assert.Equal(HttpStatusCode.OK, disableResponse.StatusCode);
        Assert.False(await GetPersistedFlagAsync(businessId));
    }

    // ── Tenant isolation ───────────────────────────────────────────────

    [Fact]
    public async Task Enable_NeverAffectsAnotherBusiness()
    {
        var (businessIdA, _, ownerClientA, ownerCookieA) = await SetUpBusinessWithOwnerAsync("Reports Tenant A");
        using var _a = ownerClientA;

        var (businessIdB, _, ownerClientB, _) = await SetUpBusinessWithOwnerAsync("Reports Tenant B");
        using var _b = ownerClientB;

        var responseA = await ownerClientA.SendAsync(WithBody(
            Authed(HttpMethod.Put, "/api/business/scheduled-reports", ownerCookieA),
            new SetScheduledReportsEnabledRequest(true)));
        Assert.Equal(HttpStatusCode.OK, responseA.StatusCode);

        Assert.True(await GetPersistedFlagAsync(businessIdA));
        Assert.False(await GetPersistedFlagAsync(businessIdB));
    }

    [Fact]
    public async Task OwnerOfBusinessB_CannotToggleBusinessA_EvenWithValidBSession()
    {
        // Business resolution in the endpoint is exclusively via
        // ICurrentTenantAccessor.BusinessId, never a client-supplied id (no
        // id is even accepted in the request body) - this test confirms
        // there is no route/body parameter through which a valid,
        // authenticated Owner of one business could target another
        // business's flag at all; Owner B's own toggle can only ever affect
        // Business B, proven the same way Enable_NeverAffectsAnotherBusiness
        // proves it from the opposite direction.
        var (businessIdA, _, ownerClientA, _) = await SetUpBusinessWithOwnerAsync("Reports Cross-Tenant A");
        using var _a = ownerClientA;

        var (businessIdB, _, ownerClientB, ownerCookieB) = await SetUpBusinessWithOwnerAsync("Reports Cross-Tenant B");
        using var _b = ownerClientB;

        var responseB = await ownerClientB.SendAsync(WithBody(
            Authed(HttpMethod.Put, "/api/business/scheduled-reports", ownerCookieB),
            new SetScheduledReportsEnabledRequest(true)));
        Assert.Equal(HttpStatusCode.OK, responseB.StatusCode);

        Assert.False(await GetPersistedFlagAsync(businessIdA));
        Assert.True(await GetPersistedFlagAsync(businessIdB));
    }

    // ── Audit logging ──────────────────────────────────────────────────

    [Fact]
    public async Task Enable_WritesExactlyOneAuditLogRow_WithCorrectFields()
    {
        var (businessId, ownerId, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Reports Audit Enable Business");
        using var _ = ownerClient;

        var response = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Put, "/api/business/scheduled-reports", ownerCookie),
            new SetScheduledReportsEnabledRequest(true)));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var auditLogs = await db.AuditLogs.IgnoreQueryFilters()
            .Where(a => a.BusinessId == businessId && a.EntityType == AuditEntityTypes.Business)
            .ToListAsync();

        var entry = Assert.Single(auditLogs);
        Assert.Equal(businessId.ToString(), entry.EntityId);
        Assert.Equal(AuditEventTypes.BusinessScheduledReportsEnabled, entry.EventType);
        Assert.Equal(ActorType.NivoraBusinessStaff, entry.Actor);
        Assert.Equal(ownerId, entry.ChangedByUserId);
        Assert.NotNull(entry.OldValue);
        Assert.NotNull(entry.NewValue);

        using var oldValueDoc = JsonDocument.Parse(entry.OldValue!);
        Assert.False(oldValueDoc.RootElement.GetProperty("ScheduledReportsEnabled").GetBoolean());
        using var newValueDoc = JsonDocument.Parse(entry.NewValue!);
        Assert.True(newValueDoc.RootElement.GetProperty("ScheduledReportsEnabled").GetBoolean());
        // Never the full Business entity — only the one field that matters.
        Assert.Equal(1, newValueDoc.RootElement.EnumerateObject().Count());
    }

    [Fact]
    public async Task Disable_WritesExactlyOneAuditLogRow_WithDisabledEventType()
    {
        var (businessId, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Reports Audit Disable Business");
        using var _ = ownerClient;

        await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Put, "/api/business/scheduled-reports", ownerCookie),
            new SetScheduledReportsEnabledRequest(true)));

        var disableResponse = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Put, "/api/business/scheduled-reports", ownerCookie),
            new SetScheduledReportsEnabledRequest(false)));
        Assert.Equal(HttpStatusCode.OK, disableResponse.StatusCode);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var auditLogs = await db.AuditLogs.IgnoreQueryFilters()
            .Where(a => a.BusinessId == businessId && a.EntityType == AuditEntityTypes.Business)
            .OrderBy(a => a.CreatedAt)
            .ToListAsync();

        Assert.Equal(2, auditLogs.Count); // one for enable, one for disable
        Assert.Equal(AuditEventTypes.BusinessScheduledReportsDisabled, auditLogs[1].EventType);
    }

    [Fact]
    public async Task IdempotentDisable_WritesNoAuditLogRow()
    {
        var (businessId, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Reports Idempotent Disable Business");
        using var _ = ownerClient;
        // Flag already defaults to false — disabling again is a pure no-op.

        var response = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Put, "/api/business/scheduled-reports", ownerCookie),
            new SetScheduledReportsEnabledRequest(false)));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var auditCount = await db.AuditLogs.IgnoreQueryFilters()
            .CountAsync(a => a.BusinessId == businessId && a.EntityType == AuditEntityTypes.Business);
        Assert.Equal(0, auditCount);
    }

    [Fact]
    public async Task IdempotentEnable_WritesNoSecondAuditLogRow()
    {
        var (businessId, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Reports Idempotent Enable Business");
        using var _ = ownerClient;

        var firstEnable = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Put, "/api/business/scheduled-reports", ownerCookie),
            new SetScheduledReportsEnabledRequest(true)));
        Assert.Equal(HttpStatusCode.OK, firstEnable.StatusCode);

        var secondEnable = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Put, "/api/business/scheduled-reports", ownerCookie),
            new SetScheduledReportsEnabledRequest(true)));
        Assert.Equal(HttpStatusCode.OK, secondEnable.StatusCode);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var auditCount = await db.AuditLogs.IgnoreQueryFilters()
            .CountAsync(a => a.BusinessId == businessId && a.EntityType == AuditEntityTypes.Business);
        Assert.Equal(1, auditCount); // only the first, real transition
    }
}
