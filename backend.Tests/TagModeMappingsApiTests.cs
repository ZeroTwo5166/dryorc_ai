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
/// HTTP-level tests for TagModeMappingsController, run against the real app
/// + real Neon dev DB via NivoraApiFactory — same pattern as
/// NotificationApiTests/CrossTenantIsolationTests. Covers read (AnyStaff) vs
/// write (OwnerOnly) authorization, tenant isolation, the audit-log write
/// established by this controller (the first production AuditLog writer in
/// the codebase), and that no Delete endpoint exists.
/// </summary>
public class TagModeMappingsApiTests : IAsyncLifetime
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
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"TagModeMappings\" WHERE \"BusinessId\" = ANY({ids})");
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

    /// <summary>Seeds a second staff user directly into an existing business with a caller-chosen role — /api/auth/register always creates a brand-new Owner+business, so non-Owner roles must be seeded directly.</summary>
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

    private record TagModeMappingResponseDto(Guid Id, string Tag, string Mode, bool IsMinimumFloor, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

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

    // ── Read: AnyStaff ────────────────────────────────────────────────

    [Fact]
    public async Task List_AgentCanRead()
    {
        var (businessId, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("TagMode Read Business");
        using (ownerClient) { }

        var agentEmail = $"agent-{Guid.NewGuid():N}@example.com";
        await SeedStaffIntoBusinessAsync(businessId, agentEmail, RoleNames.Agent);

        using var agentClient = NewClient();
        var (loginResponse, _, agentCookie) = await LoginAsync(agentClient, agentEmail);
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);

        var listResponse = await agentClient.SendAsync(Authed(HttpMethod.Get, "/api/tag-mode-mappings", agentCookie!));
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
    }

    // ── Write: OwnerOnly ──────────────────────────────────────────────

    [Fact]
    public async Task Create_AgentForbidden()
    {
        var (businessId, _, ownerClient, _) = await SetUpBusinessWithOwnerAsync("TagMode Agent Write Business");
        using (ownerClient) { }

        var agentEmail = $"agent-{Guid.NewGuid():N}@example.com";
        await SeedStaffIntoBusinessAsync(businessId, agentEmail, RoleNames.Agent);
        using var agentClient = NewClient();
        var (_, _, agentCookie) = await LoginAsync(agentClient, agentEmail);

        var response = await agentClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, "/api/tag-mode-mappings", agentCookie!),
            new CreateTagModeMappingRequest("refund-dispute", "Manual", false)));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Create_AdminForbidden()
    {
        var (businessId, _, ownerClient, _) = await SetUpBusinessWithOwnerAsync("TagMode Admin Write Business");
        using (ownerClient) { }

        var adminEmail = $"admin-{Guid.NewGuid():N}@example.com";
        await SeedStaffIntoBusinessAsync(businessId, adminEmail, RoleNames.Admin);
        using var adminClient = NewClient();
        var (_, _, adminCookie) = await LoginAsync(adminClient, adminEmail);

        var response = await adminClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, "/api/tag-mode-mappings", adminCookie!),
            new CreateTagModeMappingRequest("refund-dispute", "Manual", false)));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode); // OwnerOnly, deliberately not OwnerOrAdmin
    }

    [Fact]
    public async Task Create_AsOwner_Succeeds_AndPersists()
    {
        var (businessId, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("TagMode Owner Create Business");
        using var _ = ownerClient;

        var response = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, "/api/tag-mode-mappings", ownerCookie),
            new CreateTagModeMappingRequest("billing-question", "Assist", false)));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<TagModeMappingResponseDto>();
        Assert.NotNull(body);
        Assert.Equal("billing-question", body!.Tag);
        Assert.Equal("Assist", body.Mode);
        Assert.False(body.IsMinimumFloor);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var persisted = await db.TagModeMappings.IgnoreQueryFilters().SingleAsync(m => m.Id == body.Id);
        Assert.Equal(businessId, persisted.BusinessId);
        Assert.Equal(ConversationMode.Assist, persisted.Mode);
    }

    [Fact]
    public async Task Update_AgentForbidden()
    {
        var (businessId, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("TagMode Update Forbidden Business");
        using var _ = ownerClient;

        var createResponse = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, "/api/tag-mode-mappings", ownerCookie),
            new CreateTagModeMappingRequest("shipping", "Auto", false)));
        var created = await createResponse.Content.ReadFromJsonAsync<TagModeMappingResponseDto>();

        var agentEmail = $"agent-{Guid.NewGuid():N}@example.com";
        await SeedStaffIntoBusinessAsync(businessId, agentEmail, RoleNames.Agent);
        using var agentClient = NewClient();
        var (_, _, agentCookie) = await LoginAsync(agentClient, agentEmail);

        var updateResponse = await agentClient.SendAsync(WithBody(
            Authed(HttpMethod.Put, $"/api/tag-mode-mappings/{created!.Id}", agentCookie!),
            new UpdateTagModeMappingRequest("shipping", "Manual", true)));

        Assert.Equal(HttpStatusCode.Forbidden, updateResponse.StatusCode);
    }

    [Fact]
    public async Task Update_AsOwner_Succeeds_AndPersists()
    {
        var (_, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("TagMode Update Owner Business");
        using var _ = ownerClient;

        var createResponse = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, "/api/tag-mode-mappings", ownerCookie),
            new CreateTagModeMappingRequest("legal", "Auto", false)));
        var created = await createResponse.Content.ReadFromJsonAsync<TagModeMappingResponseDto>();

        var updateResponse = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Put, $"/api/tag-mode-mappings/{created!.Id}", ownerCookie),
            new UpdateTagModeMappingRequest("legal-threat", "Manual", true)));

        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        var updated = await updateResponse.Content.ReadFromJsonAsync<TagModeMappingResponseDto>();
        Assert.Equal("legal-threat", updated!.Tag);
        Assert.Equal("Manual", updated.Mode);
        Assert.True(updated.IsMinimumFloor);
    }

    // ── Tenant isolation ──────────────────────────────────────────────

    [Fact]
    public async Task Get_CrossTenant_ReturnsNotFound()
    {
        var (_, _, ownerClientA, ownerCookieA) = await SetUpBusinessWithOwnerAsync("TagMode Tenant A");
        using var _a = ownerClientA;
        var createResponse = await ownerClientA.SendAsync(WithBody(
            Authed(HttpMethod.Post, "/api/tag-mode-mappings", ownerCookieA),
            new CreateTagModeMappingRequest("a-only-tag", "Manual", false)));
        var created = await createResponse.Content.ReadFromJsonAsync<TagModeMappingResponseDto>();

        var (_, _, ownerClientB, ownerCookieB) = await SetUpBusinessWithOwnerAsync("TagMode Tenant B");
        using var _b = ownerClientB;

        var getResponse = await ownerClientB.SendAsync(Authed(HttpMethod.Get, $"/api/tag-mode-mappings/{created!.Id}", ownerCookieB));
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
    }

    [Fact]
    public async Task List_NeverReturnsAnotherBusinesssMappings()
    {
        var (_, _, ownerClientA, ownerCookieA) = await SetUpBusinessWithOwnerAsync("TagMode List Tenant A");
        using var _a = ownerClientA;
        await ownerClientA.SendAsync(WithBody(
            Authed(HttpMethod.Post, "/api/tag-mode-mappings", ownerCookieA),
            new CreateTagModeMappingRequest("a-only-list-tag", "Manual", false)));

        var (_, _, ownerClientB, ownerCookieB) = await SetUpBusinessWithOwnerAsync("TagMode List Tenant B");
        using var _b = ownerClientB;

        var listResponse = await ownerClientB.SendAsync(Authed(HttpMethod.Get, "/api/tag-mode-mappings", ownerCookieB));
        var body = await listResponse.Content.ReadFromJsonAsync<List<TagModeMappingResponseDto>>();
        Assert.NotNull(body);
        Assert.DoesNotContain(body!, m => m.Tag == "a-only-list-tag");
    }

    // ── Audit logging ─────────────────────────────────────────────────

    [Fact]
    public async Task Create_WritesExactlyOneAuditLogRow_WithCorrectFields()
    {
        var (businessId, ownerId, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("TagMode Audit Create Business");
        using var _ = ownerClient;

        var response = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, "/api/tag-mode-mappings", ownerCookie),
            new CreateTagModeMappingRequest("refund-dispute", "Manual", true)));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<TagModeMappingResponseDto>();

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var auditLogs = await db.AuditLogs.IgnoreQueryFilters()
            .Where(a => a.BusinessId == businessId && a.EntityId == created!.Id.ToString())
            .ToListAsync();

        var entry = Assert.Single(auditLogs);
        Assert.Equal(AuditEntityTypes.TagModeMapping, entry.EntityType);
        Assert.Equal(AuditEventTypes.TagModeMappingChanged, entry.EventType);
        Assert.Equal(ActorType.NivoraBusinessStaff, entry.Actor);
        Assert.Equal(ownerId, entry.ChangedByUserId);
        Assert.Null(entry.OldValue);
        Assert.NotNull(entry.NewValue);

        using var newValueDoc = JsonDocument.Parse(entry.NewValue!);
        Assert.Equal("refund-dispute", newValueDoc.RootElement.GetProperty("Tag").GetString());
        Assert.Equal("Manual", newValueDoc.RootElement.GetProperty("Mode").GetString());
        Assert.True(newValueDoc.RootElement.GetProperty("IsMinimumFloor").GetBoolean());
        // Establishes the convention: only the fields that matter for this
        // table, never Id/BusinessId/timestamps, never the full entity.
        Assert.Equal(3, newValueDoc.RootElement.EnumerateObject().Count());
    }

    [Fact]
    public async Task Update_WritesExactlyOneAuditLogRow_WithOldAndNewValues()
    {
        var (businessId, ownerId, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("TagMode Audit Update Business");
        using var _ = ownerClient;

        var createResponse = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, "/api/tag-mode-mappings", ownerCookie),
            new CreateTagModeMappingRequest("general", "Auto", false)));
        var created = await createResponse.Content.ReadFromJsonAsync<TagModeMappingResponseDto>();

        var updateResponse = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Put, $"/api/tag-mode-mappings/{created!.Id}", ownerCookie),
            new UpdateTagModeMappingRequest("general-support", "Assist", true)));
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var auditLogs = await db.AuditLogs.IgnoreQueryFilters()
            .Where(a => a.BusinessId == businessId && a.EntityId == created.Id.ToString() && a.EventType == AuditEventTypes.TagModeMappingChanged)
            .OrderBy(a => a.CreatedAt)
            .ToListAsync();

        // One from Create, one from Update.
        Assert.Equal(2, auditLogs.Count);
        var updateEntry = auditLogs[1];
        Assert.Equal(ownerId, updateEntry.ChangedByUserId);

        using var oldValueDoc = JsonDocument.Parse(updateEntry.OldValue!);
        Assert.Equal("general", oldValueDoc.RootElement.GetProperty("Tag").GetString());
        Assert.Equal("Auto", oldValueDoc.RootElement.GetProperty("Mode").GetString());

        using var newValueDoc = JsonDocument.Parse(updateEntry.NewValue!);
        Assert.Equal("general-support", newValueDoc.RootElement.GetProperty("Tag").GetString());
        Assert.Equal("Assist", newValueDoc.RootElement.GetProperty("Mode").GetString());
        Assert.True(newValueDoc.RootElement.GetProperty("IsMinimumFloor").GetBoolean());
    }

    /// <summary>
    /// Forces a real Postgres unique-constraint violation (BusinessId, Tag)
    /// on the second Create's SaveChangesAsync — both the TagModeMapping Add
    /// and the AuditLog Add for that second attempt share one change
    /// tracker/one SaveChangesAsync call, so proving the failed attempt left
    /// zero AuditLog rows (only the first, successful Create's row exists)
    /// proves the two writes commit atomically together, not independently.
    /// </summary>
    [Fact]
    public async Task Create_DuplicateTag_FailsAtomically_NoOrphanedAuditLogFromFailedAttempt()
    {
        var (businessId, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("TagMode Atomicity Business");
        using var _ = ownerClient;

        var firstResponse = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, "/api/tag-mode-mappings", ownerCookie),
            new CreateTagModeMappingRequest("dup-tag", "Auto", false)));
        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);

        try
        {
            await ownerClient.SendAsync(WithBody(
                Authed(HttpMethod.Post, "/api/tag-mode-mappings", ownerCookie),
                new CreateTagModeMappingRequest("dup-tag", "Manual", false)));
        }
        catch
        {
            // The unique-constraint violation may surface as a thrown
            // exception rather than an HTTP response, depending on the test
            // host's exception handling — either way, what matters is the DB
            // state asserted below, not how the failure was reported.
        }

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var mappingCount = await db.TagModeMappings.IgnoreQueryFilters()
            .CountAsync(m => m.BusinessId == businessId && m.Tag == "dup-tag");
        Assert.Equal(1, mappingCount); // only the first Create's row

        var auditCount = await db.AuditLogs.IgnoreQueryFilters()
            .CountAsync(a => a.BusinessId == businessId && a.EntityType == AuditEntityTypes.TagModeMapping);
        Assert.Equal(1, auditCount); // only the first Create's audit row — the failed second attempt left none
    }

    // ── No Delete endpoint ────────────────────────────────────────────

    [Fact]
    public async Task Delete_EndpointDoesNotExist()
    {
        var (_, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("TagMode No Delete Business");
        using var _ = ownerClient;

        var createResponse = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, "/api/tag-mode-mappings", ownerCookie),
            new CreateTagModeMappingRequest("no-delete-tag", "Auto", false)));
        var created = await createResponse.Content.ReadFromJsonAsync<TagModeMappingResponseDto>();

        var deleteResponse = await ownerClient.SendAsync(Authed(HttpMethod.Delete, $"/api/tag-mode-mappings/{created!.Id}", ownerCookie));

        Assert.True(
            deleteResponse.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed,
            $"Expected no Delete route to exist, got {deleteResponse.StatusCode}.");

        // Confirm the row genuinely still exists — a 404 here must mean
        // "no route", not "route existed and (incorrectly) reported not found".
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.TagModeMappings.IgnoreQueryFilters().AnyAsync(m => m.Id == created.Id));
    }

    // ── Validation ────────────────────────────────────────────────────

    [Fact]
    public async Task Create_InvalidMode_ReturnsBadRequest()
    {
        var (_, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("TagMode Invalid Mode Business");
        using var _ = ownerClient;

        var response = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, "/api/tag-mode-mappings", ownerCookie),
            new CreateTagModeMappingRequest("some-tag", "NotARealMode", false)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
