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
/// HTTP-level tests for KnowledgeDocumentsController (Step 10 — Knowledge
/// Base CRUD), run against the real app + real Neon dev DB via
/// NivoraApiFactory — same pattern as NotificationApiTests/ConversationsApiTests.
/// No KnowledgeChunk/embedding/RAG behavior exists to test yet — those are
/// Steps 11-12.
/// </summary>
public class KnowledgeDocumentsApiTests : IAsyncLifetime
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

            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"KnowledgeDocuments\" WHERE \"BusinessId\" = ANY({ids})");
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

    /// <summary>Seeds a second staff user directly into an existing business, with a caller-chosen role — same pattern NotificationApiTests uses.</summary>
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

    /// <summary>Registers a fresh business (Owner), confirms email, logs in. Returns the business id and the Owner's cookie.</summary>
    private async Task<(Guid BusinessId, string OwnerCookie)> CreateBusinessAndLoginAsOwnerAsync(
        HttpClient anonClient, HttpClient ownerClient, string businessName)
    {
        var email = $"owner-{Guid.NewGuid():N}@example.com";
        var (registerResponse, registerBody) = await RegisterAsync(anonClient, businessName, email);
        Assert.Equal(HttpStatusCode.OK, registerResponse.StatusCode);
        Assert.NotNull(registerBody);
        _businessIdsToClean.Add(registerBody!.BusinessId);
        await ConfirmEmailAsync(anonClient, registerBody.UserId);

        var (loginResponse, _, cookie) = await LoginAsync(ownerClient, email);
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        Assert.NotNull(cookie);

        return (registerBody.BusinessId, cookie!);
    }

    // ── Full CRUD lifecycle (Owner) ─────────────────────────────────

    [Fact]
    public async Task Create_List_Get_Update_Deactivate_FullLifecycle()
    {
        using var anonClient = NewClient();
        using var ownerClient = NewClient();
        var (_, ownerCookie) = await CreateBusinessAndLoginAsOwnerAsync(anonClient, ownerClient, "Knowledge Lifecycle Business");

        var createResponse = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, "/api/knowledge", ownerCookie),
            new CreateKnowledgeDocumentRequest("Faq", "Shipping times", "We ship within 2 business days.")));
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<KnowledgeDocumentResponse>();
        Assert.NotNull(created);
        Assert.Equal("Faq", created!.Type);
        Assert.True(created.IsActive);

        var listResponse = await ownerClient.SendAsync(Authed(HttpMethod.Get, "/api/knowledge", ownerCookie));
        var listBody = await listResponse.Content.ReadFromJsonAsync<List<KnowledgeDocumentResponse>>();
        Assert.Contains(listBody!, d => d.Id == created.Id);

        var getResponse = await ownerClient.SendAsync(Authed(HttpMethod.Get, $"/api/knowledge/{created.Id}", ownerCookie));
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var fetched = await getResponse.Content.ReadFromJsonAsync<KnowledgeDocumentResponse>();
        Assert.Equal("We ship within 2 business days.", fetched!.Content);

        var updateResponse = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Put, $"/api/knowledge/{created.Id}", ownerCookie),
            new UpdateKnowledgeDocumentRequest("Policy", "Shipping policy", "We ship within 3 business days now.", true)));
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        var updated = await updateResponse.Content.ReadFromJsonAsync<KnowledgeDocumentResponse>();
        Assert.Equal("Policy", updated!.Type);
        Assert.Equal("Shipping policy", updated.Title);
        Assert.Equal("We ship within 3 business days now.", updated.Content);

        var deleteResponse = await ownerClient.SendAsync(Authed(HttpMethod.Delete, $"/api/knowledge/{created.Id}", ownerCookie));
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        // Soft-delete only — the row survives with IsActive = false, never
        // physically removed (KnowledgeDocument.IsActive's own contract).
        var afterDeleteGet = await ownerClient.SendAsync(Authed(HttpMethod.Get, $"/api/knowledge/{created.Id}", ownerCookie));
        Assert.Equal(HttpStatusCode.OK, afterDeleteGet.StatusCode);
        var afterDelete = await afterDeleteGet.Content.ReadFromJsonAsync<KnowledgeDocumentResponse>();
        Assert.False(afterDelete!.IsActive);

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.KnowledgeDocuments.IgnoreQueryFilters().SingleOrDefaultAsync(d => d.Id == created.Id);
            Assert.NotNull(row); // physically still present
            Assert.False(row!.IsActive);
        }
    }

    [Fact]
    public async Task Update_ReactivatesADeactivatedDocument()
    {
        using var anonClient = NewClient();
        using var ownerClient = NewClient();
        var (_, ownerCookie) = await CreateBusinessAndLoginAsOwnerAsync(anonClient, ownerClient, "Knowledge Reactivation Business");

        var createResponse = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, "/api/knowledge", ownerCookie),
            new CreateKnowledgeDocumentRequest("Document", "Return policy", "30-day returns.")));
        var created = await createResponse.Content.ReadFromJsonAsync<KnowledgeDocumentResponse>();

        var deleteResponse = await ownerClient.SendAsync(Authed(HttpMethod.Delete, $"/api/knowledge/{created!.Id}", ownerCookie));
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var reactivateResponse = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Put, $"/api/knowledge/{created.Id}", ownerCookie),
            new UpdateKnowledgeDocumentRequest("Document", "Return policy", "30-day returns.", true)));
        Assert.Equal(HttpStatusCode.OK, reactivateResponse.StatusCode);
        var reactivated = await reactivateResponse.Content.ReadFromJsonAsync<KnowledgeDocumentResponse>();
        Assert.True(reactivated!.IsActive);

        var getResponse = await ownerClient.SendAsync(Authed(HttpMethod.Get, $"/api/knowledge/{created.Id}", ownerCookie));
        var fetched = await getResponse.Content.ReadFromJsonAsync<KnowledgeDocumentResponse>();
        Assert.True(fetched!.IsActive);
    }

    [Fact]
    public async Task Create_InvalidType_ReturnsBadRequest()
    {
        using var anonClient = NewClient();
        using var ownerClient = NewClient();
        var (_, ownerCookie) = await CreateBusinessAndLoginAsOwnerAsync(anonClient, ownerClient, "Knowledge Invalid Type Business");

        var response = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, "/api/knowledge", ownerCookie),
            new CreateKnowledgeDocumentRequest("NotARealType", "Title", "Content")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── Role-based write authorization (Agent) ──────────────────────

    [Fact]
    public async Task Agent_CanReadListAndDetail()
    {
        using var anonClient = NewClient();
        using var ownerClient = NewClient();
        var (businessId, ownerCookie) = await CreateBusinessAndLoginAsOwnerAsync(anonClient, ownerClient, "Knowledge Agent Read Business");

        var createResponse = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, "/api/knowledge", ownerCookie),
            new CreateKnowledgeDocumentRequest("Faq", "Refunds", "Refunds take 5-7 days.")));
        var created = await createResponse.Content.ReadFromJsonAsync<KnowledgeDocumentResponse>();

        var agentEmail = $"agent-{Guid.NewGuid():N}@example.com";
        await SeedStaffIntoBusinessAsync(businessId, agentEmail, RoleNames.Agent);
        using var agentClient = NewClient();
        var (agentLoginResponse, _, agentCookie) = await LoginAsync(agentClient, agentEmail);
        Assert.Equal(HttpStatusCode.OK, agentLoginResponse.StatusCode);

        var listResponse = await agentClient.SendAsync(Authed(HttpMethod.Get, "/api/knowledge", agentCookie!));
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var listBody = await listResponse.Content.ReadFromJsonAsync<List<KnowledgeDocumentResponse>>();
        Assert.Contains(listBody!, d => d.Id == created!.Id);

        var getResponse = await agentClient.SendAsync(Authed(HttpMethod.Get, $"/api/knowledge/{created!.Id}", agentCookie!));
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
    }

    [Fact]
    public async Task Agent_CannotCreateUpdateOrDelete_ReturnsForbidden()
    {
        using var anonClient = NewClient();
        using var ownerClient = NewClient();
        var (businessId, ownerCookie) = await CreateBusinessAndLoginAsOwnerAsync(anonClient, ownerClient, "Knowledge Agent Write Business");

        var createByOwnerResponse = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, "/api/knowledge", ownerCookie),
            new CreateKnowledgeDocumentRequest("Faq", "Warranty", "1 year warranty.")));
        var existing = await createByOwnerResponse.Content.ReadFromJsonAsync<KnowledgeDocumentResponse>();

        var agentEmail = $"agent-{Guid.NewGuid():N}@example.com";
        await SeedStaffIntoBusinessAsync(businessId, agentEmail, RoleNames.Agent);
        using var agentClient = NewClient();
        var (agentLoginResponse, _, agentCookie) = await LoginAsync(agentClient, agentEmail);
        Assert.Equal(HttpStatusCode.OK, agentLoginResponse.StatusCode);

        var createResponse = await agentClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, "/api/knowledge", agentCookie!),
            new CreateKnowledgeDocumentRequest("Faq", "Agent attempt", "Should be rejected.")));
        Assert.Equal(HttpStatusCode.Forbidden, createResponse.StatusCode);

        var updateResponse = await agentClient.SendAsync(WithBody(
            Authed(HttpMethod.Put, $"/api/knowledge/{existing!.Id}", agentCookie!),
            new UpdateKnowledgeDocumentRequest("Faq", "Warranty", "Tampered.", true)));
        Assert.Equal(HttpStatusCode.Forbidden, updateResponse.StatusCode);

        var deleteResponse = await agentClient.SendAsync(Authed(HttpMethod.Delete, $"/api/knowledge/{existing.Id}", agentCookie!));
        Assert.Equal(HttpStatusCode.Forbidden, deleteResponse.StatusCode);

        // Confirm the rejected attempts truly left the document untouched.
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.KnowledgeDocuments.IgnoreQueryFilters().SingleAsync(d => d.Id == existing.Id);
            Assert.Equal("Warranty", row.Title);
            Assert.Equal("1 year warranty.", row.Content);
            Assert.True(row.IsActive);
        }
    }

    // ── Tenant isolation ─────────────────────────────────────────────

    [Fact]
    public async Task List_NeverReturnsAnotherBusinesssDocuments()
    {
        using var anonClientA = NewClient();
        using var ownerClientA = NewClient();
        var (_, ownerCookieA) = await CreateBusinessAndLoginAsOwnerAsync(anonClientA, ownerClientA, "Knowledge Isolation Business A");

        var createResponse = await ownerClientA.SendAsync(WithBody(
            Authed(HttpMethod.Post, "/api/knowledge", ownerCookieA),
            new CreateKnowledgeDocumentRequest("Faq", "Business A only", "Should not leak to Business B.")));
        var createdA = await createResponse.Content.ReadFromJsonAsync<KnowledgeDocumentResponse>();

        using var anonClientB = NewClient();
        using var ownerClientB = NewClient();
        var (_, ownerCookieB) = await CreateBusinessAndLoginAsOwnerAsync(anonClientB, ownerClientB, "Knowledge Isolation Business B");

        var listResponse = await ownerClientB.SendAsync(Authed(HttpMethod.Get, "/api/knowledge", ownerCookieB));
        var listBody = await listResponse.Content.ReadFromJsonAsync<List<KnowledgeDocumentResponse>>();
        Assert.DoesNotContain(listBody!, d => d.Id == createdA!.Id);
    }

    [Fact]
    public async Task Get_AnotherBusinesssDocument_ReturnsNotFound()
    {
        using var anonClientA = NewClient();
        using var ownerClientA = NewClient();
        var (_, ownerCookieA) = await CreateBusinessAndLoginAsOwnerAsync(anonClientA, ownerClientA, "Knowledge Cross-Tenant Business A");

        var createResponse = await ownerClientA.SendAsync(WithBody(
            Authed(HttpMethod.Post, "/api/knowledge", ownerCookieA),
            new CreateKnowledgeDocumentRequest("Faq", "Business A only", "Should 404 for Business B.")));
        var createdA = await createResponse.Content.ReadFromJsonAsync<KnowledgeDocumentResponse>();

        using var anonClientB = NewClient();
        using var ownerClientB = NewClient();
        var (_, ownerCookieB) = await CreateBusinessAndLoginAsOwnerAsync(anonClientB, ownerClientB, "Knowledge Cross-Tenant Business B");

        var getResponse = await ownerClientB.SendAsync(Authed(HttpMethod.Get, $"/api/knowledge/{createdA!.Id}", ownerCookieB));
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
    }
}
