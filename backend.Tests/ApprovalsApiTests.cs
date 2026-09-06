using System.Net;
using System.Net.Http.Json;
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
/// HTTP-level tests for Step 47 — ApprovalsController, run against the real
/// app + real Neon dev DB via NivoraApiFactory, same pattern as
/// TicketsApiTests/CustomersApiTests. Covers list visibility/scoping,
/// the approve/deny guarded-update conflict handling (matching
/// TicketsController.Claim/Unclaim's own established idiom), tenant
/// isolation, and the OwnerOrAdmin-only authorization tier (step-47
/// addendum decision 1 — Agent is excluded from viewing the list, not
/// only from acting on it).
///
/// Deliberate scope boundary, stated explicitly rather than left implicit:
/// Execute's own business logic (steps 3-16, including every Shopify-
/// dependent branch — ExternallySatisfied/Failed/UnknownNeedsReconciliation)
/// is already exhaustively covered at the service level by
/// OrderCancelApprovedActionExecutorTests.cs (17 tests, real
/// IApprovedActionExecutor, fake Shopify HTTP handlers per NivoraApiFactory's
/// own established philosophy of faking genuinely-external I/O, not
/// Nivora's own internal business logic). Introducing a second, parallel
/// Shopify-HTTP-faking mechanism at the WebApplicationFactory level purely
/// to re-exercise those same branches through HTTP would duplicate that
/// coverage without proving anything new. This file instead verifies the
/// controller's own concerns — authorization, tenant isolation, request/
/// response wiring — using the real, unmodified IApprovedActionExecutor
/// throughout; its one Execute-outcome assertion uses a Denied Approval,
/// which refuses at step 6, before any Shopify call would ever be made,
/// so no Shopify fake is needed to prove the controller correctly threads
/// CurrentUserId through to the executor and correctly serializes a
/// Refused outcome.
/// </summary>
public class ApprovalsApiTests : IAsyncLifetime
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

            // AuditLogs first (Restrict FK to Businesses). Approvals next
            // (Restrict to Businesses/Customers, Cascade from Conversations —
            // deleting Conversations below would already remove them, but
            // deleting explicitly first keeps this ordering self-evident).
            // Conversations next. Customers/Widgets next. AspNetUserRoles/
            // AspNetUsers before Businesses, same ordering TicketsApiTests
            // already established.
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"AuditLogs\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Approvals\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Conversations\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Widgets\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Customers\" WHERE \"BusinessId\" = ANY({ids})");
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

    private async Task<(ApplicationUser User, string Cookie)> SeedStaffAndLoginAsync(Guid businessId, string role)
    {
        var email = $"staff-{Guid.NewGuid():N}@example.com";
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

        using var loginClient = NewClient();
        var (_, _, cookie) = await LoginAsync(loginClient, email);
        Assert.NotNull(cookie);
        return (user, cookie!);
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

    private async Task<Guid> SetUpBusinessAsync(string businessName)
    {
        var email = $"owner-{Guid.NewGuid():N}@example.com";
        using var anonClient = NewClient();
        var (registerResponse, registerBody) = await RegisterAsync(anonClient, businessName, email);
        Assert.Equal(HttpStatusCode.OK, registerResponse.StatusCode);
        Assert.NotNull(registerBody);
        _businessIdsToClean.Add(registerBody!.BusinessId);
        await ConfirmEmailAsync(anonClient, registerBody.UserId);
        return registerBody.BusinessId;
    }

    /// <summary>
    /// Seeds one Widget/Customer/Conversation/Approval graph directly via
    /// DbContext — same minimal shape as
    /// OrderCancelApprovedActionExecutorTests.SeedApprovalAsync, without
    /// that file's Level2/ownership/real-ActionHash concerns, since none of
    /// this controller's own tests exercise Execute past step 6 (see class
    /// doc comment for why). ActionHash is a syntactically valid but
    /// unverified placeholder — fine here, since Approve/Deny never read it
    /// and this file's one Execute test (Denied -> ApprovalNotApproved)
    /// refuses before step 11 ever recomputes it.
    /// </summary>
    private async Task<Approval> SeedApprovalAsync(
        Guid businessId, ApprovalStatus status = ApprovalStatus.Pending, DateTimeOffset? expiresAt = null)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTimeOffset.UtcNow;

        var widget = new Widget
        {
            Id = Guid.NewGuid(), BusinessId = businessId, Name = "Test Widget",
            PublicKey = Guid.NewGuid().ToString("N"), CreatedAt = now, UpdatedAt = now,
        };
        var customer = new Customer
        {
            Id = Guid.NewGuid(), BusinessId = businessId, Name = "Test Customer",
            Email = "test.customer@marker-test.invalid", CreatedAt = now, UpdatedAt = now,
        };
        var conversation = new Conversation
        {
            Id = Guid.NewGuid(), BusinessId = businessId, WidgetId = widget.Id, CustomerId = customer.Id,
            VisitorSessionId = Guid.NewGuid().ToString("N"), Channel = "Website", Status = ConversationStatus.Open,
            CreatedAt = now, UpdatedAt = now,
        };
        var approval = new Approval
        {
            Id = Guid.NewGuid(), BusinessId = businessId, ConversationId = conversation.Id, CustomerId = customer.Id,
            ToolName = "cancelOrder", ResourceId = "gid://shopify/Order/12345",
            CanonicalArguments = "placeholder", ActionHash = new string('a', 64),
            DisplaySummary = "Cancel order gid://shopify/Order/12345.",
            Status = status, CreatedAt = now, ExpiresAt = expiresAt ?? now.AddMinutes(15),
        };

        db.Widgets.Add(widget);
        db.Customers.Add(customer);
        db.Conversations.Add(conversation);
        db.Approvals.Add(approval);
        await db.SaveChangesAsync();
        return approval;
    }

    private async Task<Approval> ReadApprovalAsync(Guid id)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Approvals.IgnoreQueryFilters().AsNoTracking().FirstAsync(a => a.Id == id);
    }

    private async Task<List<AuditLog>> ReadAuditLogsAsync(Guid businessId, Guid entityId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.AuditLogs.IgnoreQueryFilters()
            .Where(a => a.BusinessId == businessId && a.EntityType == AuditEntityTypes.Approval && a.EntityId == entityId.ToString())
            .ToListAsync();
    }

    // ── List ─────────────────────────────────────────────────────────

    [Fact]
    public async Task List_ReturnsOnlyLivePendingApprovalsForOwnBusiness()
    {
        var businessId = await SetUpBusinessAsync("List Test Business");
        var (_, ownerCookie) = await SeedStaffAndLoginAsync(businessId, RoleNames.Owner);

        var pending = await SeedApprovalAsync(businessId);
        await SeedApprovalAsync(businessId, status: ApprovalStatus.Approved);
        await SeedApprovalAsync(businessId, status: ApprovalStatus.Denied);
        await SeedApprovalAsync(businessId, expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));

        using var client = NewClient();
        var response = await client.SendAsync(Authed(HttpMethod.Get, "/api/approvals", ownerCookie));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<List<ApprovalSummaryResponse>>();
        Assert.NotNull(body);
        var only = Assert.Single(body!);
        Assert.Equal(pending.Id, only.Id);
        Assert.Equal(pending.DisplaySummary, only.DisplaySummary);
    }

    [Fact]
    public async Task List_DoesNotReturnAnotherBusinesssApprovals()
    {
        var businessAId = await SetUpBusinessAsync("Tenant A");
        var businessBId = await SetUpBusinessAsync("Tenant B");
        var (_, ownerACookie) = await SeedStaffAndLoginAsync(businessAId, RoleNames.Owner);
        await SeedApprovalAsync(businessBId);

        using var client = NewClient();
        var response = await client.SendAsync(Authed(HttpMethod.Get, "/api/approvals", ownerACookie));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<List<ApprovalSummaryResponse>>();
        Assert.Empty(body!);
    }

    [Fact]
    public async Task List_Agent_Returns403()
    {
        var businessId = await SetUpBusinessAsync("Agent Visibility Test");
        var (_, agentCookie) = await SeedStaffAndLoginAsync(businessId, RoleNames.Agent);

        using var client = NewClient();
        var response = await client.SendAsync(Authed(HttpMethod.Get, "/api/approvals", agentCookie));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task List_Unauthenticated_Returns401()
    {
        using var client = NewClient();
        var response = await client.GetAsync("/api/approvals");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── Approve ──────────────────────────────────────────────────────

    [Fact]
    public async Task Approve_Pending_SetsApprovedAndWritesAuditLog()
    {
        var businessId = await SetUpBusinessAsync("Approve Success Business");
        var (owner, ownerCookie) = await SeedStaffAndLoginAsync(businessId, RoleNames.Owner);
        var approval = await SeedApprovalAsync(businessId);

        using var client = NewClient();
        var response = await client.SendAsync(Authed(HttpMethod.Post, $"/api/approvals/{approval.Id}/approve", ownerCookie));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var updated = await ReadApprovalAsync(approval.Id);
        Assert.Equal(ApprovalStatus.Approved, updated.Status);
        Assert.Equal(owner.Id, updated.ApprovedBy);
        Assert.NotNull(updated.ApprovedAt);

        var logs = await ReadAuditLogsAsync(businessId, approval.Id);
        var entry = Assert.Single(logs);
        Assert.Equal(AuditEventTypes.ApprovalApproved, entry.EventType);
        Assert.Equal(ActorType.NivoraBusinessStaff, entry.Actor);
        Assert.Equal(owner.Id, entry.ChangedByUserId);
    }

    [Fact]
    public async Task Approve_AlreadyApproved_Returns409AndWritesNoAuditLog()
    {
        var businessId = await SetUpBusinessAsync("Approve Conflict Business");
        var (_, ownerCookie) = await SeedStaffAndLoginAsync(businessId, RoleNames.Owner);
        var approval = await SeedApprovalAsync(businessId, status: ApprovalStatus.Approved);

        using var client = NewClient();
        var response = await client.SendAsync(Authed(HttpMethod.Post, $"/api/approvals/{approval.Id}/approve", ownerCookie));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Empty(await ReadAuditLogsAsync(businessId, approval.Id));
    }

    [Fact]
    public async Task Approve_CrossTenant_Returns404()
    {
        var businessAId = await SetUpBusinessAsync("Approve CrossTenant A");
        var businessBId = await SetUpBusinessAsync("Approve CrossTenant B");
        var (_, ownerACookie) = await SeedStaffAndLoginAsync(businessAId, RoleNames.Owner);
        var approvalB = await SeedApprovalAsync(businessBId);

        using var client = NewClient();
        var response = await client.SendAsync(Authed(HttpMethod.Post, $"/api/approvals/{approvalB.Id}/approve", ownerACookie));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ApprovalStatus.Pending, (await ReadApprovalAsync(approvalB.Id)).Status);
    }

    [Fact]
    public async Task Approve_Agent_Returns403()
    {
        var businessId = await SetUpBusinessAsync("Approve Agent Business");
        var (_, agentCookie) = await SeedStaffAndLoginAsync(businessId, RoleNames.Agent);
        var approval = await SeedApprovalAsync(businessId);

        using var client = NewClient();
        var response = await client.SendAsync(Authed(HttpMethod.Post, $"/api/approvals/{approval.Id}/approve", agentCookie));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(ApprovalStatus.Pending, (await ReadApprovalAsync(approval.Id)).Status);
    }

    // ── Deny ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Deny_Pending_WithReason_SetsDeniedReasonAndWritesAuditLog()
    {
        var businessId = await SetUpBusinessAsync("Deny Reason Business");
        var (owner, ownerCookie) = await SeedStaffAndLoginAsync(businessId, RoleNames.Owner);
        var approval = await SeedApprovalAsync(businessId);

        using var client = NewClient();
        var request = WithBody(
            Authed(HttpMethod.Post, $"/api/approvals/{approval.Id}/deny", ownerCookie),
            new DenyApprovalRequest("Customer called back and no longer wants this."));
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var updated = await ReadApprovalAsync(approval.Id);
        Assert.Equal(ApprovalStatus.Denied, updated.Status);
        Assert.Equal(owner.Id, updated.DeniedBy);
        Assert.Equal("Customer called back and no longer wants this.", updated.DeniedReason);

        var logs = await ReadAuditLogsAsync(businessId, approval.Id);
        var entry = Assert.Single(logs);
        Assert.Equal(AuditEventTypes.ApprovalDenied, entry.EventType);
        Assert.Equal(owner.Id, entry.ChangedByUserId);
    }

    [Fact]
    public async Task Deny_Pending_NoReason_LeavesDeniedReasonNull()
    {
        var businessId = await SetUpBusinessAsync("Deny No Reason Business");
        var (_, ownerCookie) = await SeedStaffAndLoginAsync(businessId, RoleNames.Owner);
        var approval = await SeedApprovalAsync(businessId);

        using var client = NewClient();
        // No body at all — DenyApprovalRequest's own Reason is optional
        // (step-47 addendum decision 3).
        var response = await client.SendAsync(Authed(HttpMethod.Post, $"/api/approvals/{approval.Id}/deny", ownerCookie));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Null((await ReadApprovalAsync(approval.Id)).DeniedReason);
    }

    [Fact]
    public async Task Deny_Approved_Succeeds_NoExpiryCheck()
    {
        var businessId = await SetUpBusinessAsync("Deny Approved Business");
        var (_, ownerCookie) = await SeedStaffAndLoginAsync(businessId, RoleNames.Owner);
        // Already expired AND already Approved — Deny must still succeed;
        // step-22z §5's own "no expiration check in either source state."
        var approval = await SeedApprovalAsync(
            businessId, status: ApprovalStatus.Approved, expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));

        using var client = NewClient();
        var response = await client.SendAsync(Authed(HttpMethod.Post, $"/api/approvals/{approval.Id}/deny", ownerCookie));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(ApprovalStatus.Denied, (await ReadApprovalAsync(approval.Id)).Status);
    }

    [Fact]
    public async Task Deny_AlreadyDenied_Returns409()
    {
        var businessId = await SetUpBusinessAsync("Deny Conflict Business");
        var (_, ownerCookie) = await SeedStaffAndLoginAsync(businessId, RoleNames.Owner);
        var approval = await SeedApprovalAsync(businessId, status: ApprovalStatus.Denied);

        using var client = NewClient();
        var response = await client.SendAsync(Authed(HttpMethod.Post, $"/api/approvals/{approval.Id}/deny", ownerCookie));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    // ── Execute ──────────────────────────────────────────────────────

    [Fact]
    public async Task Execute_DeniedApproval_ReturnsRefusedApprovalNotApproved()
    {
        var businessId = await SetUpBusinessAsync("Execute Refused Business");
        var (owner, ownerCookie) = await SeedStaffAndLoginAsync(businessId, RoleNames.Owner);
        var approval = await SeedApprovalAsync(businessId, status: ApprovalStatus.Denied);

        using var client = NewClient();
        var response = await client.SendAsync(Authed(HttpMethod.Post, $"/api/approvals/{approval.Id}/execute", ownerCookie));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ExecuteApprovalResponse>();
        Assert.NotNull(body);
        Assert.Equal("Refused", body!.Result);
        Assert.Equal(nameof(ExecutionRefusalReason.ApprovalNotApproved), body.RefusalReason);

        // The executor's own AuditLog write (real IApprovedActionExecutor,
        // not a fake) proves this genuinely went through the real steps 3-6,
        // not a controller-level shortcut — Actor/ChangedByUserId match the
        // logged-in owner, per step-45's identity-threading contract.
        var logs = await ReadAuditLogsAsync(businessId, approval.Id);
        var entry = Assert.Single(logs);
        Assert.Equal(AuditEventTypes.UnauthorizedActionBlocked, entry.EventType);
        Assert.Equal(owner.Id, entry.ChangedByUserId);
    }

    [Fact]
    public async Task Execute_CrossTenant_Returns404()
    {
        var businessAId = await SetUpBusinessAsync("Execute CrossTenant A");
        var businessBId = await SetUpBusinessAsync("Execute CrossTenant B");
        var (_, ownerACookie) = await SeedStaffAndLoginAsync(businessAId, RoleNames.Owner);
        var approvalB = await SeedApprovalAsync(businessBId, status: ApprovalStatus.Approved);

        using var client = NewClient();
        var response = await client.SendAsync(Authed(HttpMethod.Post, $"/api/approvals/{approvalB.Id}/execute", ownerACookie));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Execute_NoSuchApproval_Returns404()
    {
        var businessId = await SetUpBusinessAsync("Execute NotFound Business");
        var (_, ownerCookie) = await SeedStaffAndLoginAsync(businessId, RoleNames.Owner);

        using var client = NewClient();
        var response = await client.SendAsync(Authed(HttpMethod.Post, $"/api/approvals/{Guid.NewGuid()}/execute", ownerCookie));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Execute_Agent_Returns403()
    {
        var businessId = await SetUpBusinessAsync("Execute Agent Business");
        var (_, agentCookie) = await SeedStaffAndLoginAsync(businessId, RoleNames.Agent);
        var approval = await SeedApprovalAsync(businessId, status: ApprovalStatus.Approved);

        using var client = NewClient();
        var response = await client.SendAsync(Authed(HttpMethod.Post, $"/api/approvals/{approval.Id}/execute", agentCookie));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Execute_Unauthenticated_Returns401()
    {
        var businessId = await SetUpBusinessAsync("Execute Unauth Business");
        var approval = await SeedApprovalAsync(businessId, status: ApprovalStatus.Approved);

        using var client = NewClient();
        var response = await client.PostAsync($"/api/approvals/{approval.Id}/execute", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
