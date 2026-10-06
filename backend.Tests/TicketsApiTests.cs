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
using Npgsql;
using Xunit;

namespace backend.Tests;

/// <summary>
/// HTTP-level tests for Step 16 — TicketsController, run against the real
/// app + real Neon dev DB via NivoraApiFactory, same pattern as
/// CustomersApiTests/TagModeMappingsApiTests. Covers explicit creation,
/// the DB-enforced at-most-one-active-ticket-per-conversation invariant
/// (including genuine concurrent-request races, no advisory lock/barrier),
/// the full claim/unclaim/resolve/reopen lifecycle and its atomic
/// ExecuteUpdateAsync-based concurrency guards, priority changes, tenant
/// isolation, and audit-log atomicity.
/// </summary>
public class TicketsApiTests : IAsyncLifetime
{
    private readonly NivoraApiFactory _factory = new();
    private readonly List<Guid> _businessIdsToClean = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await TestBusinessCleanup.DeleteThenDisposeAsync(_businessIdsToClean, _factory);
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

    private static async Task<Guid> SeedCustomerAsync(AppDbContext db, Guid businessId)
    {
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            Email = "customer@example.com",
            Name = "Test Customer",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        return customer.Id;
    }

    private static async Task<Guid> SeedWidgetAsync(AppDbContext db, Guid businessId)
    {
        var widget = new Widget
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            Name = "Test Widget",
            PublicKey = Guid.NewGuid().ToString("N"),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Widgets.Add(widget);
        await db.SaveChangesAsync();
        return widget.Id;
    }

    private static async Task<Guid> SeedConversationAsync(AppDbContext db, Guid businessId, Guid widgetId, Guid? customerId, string channel = ConversationChannels.Website)
    {
        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            WidgetId = widgetId,
            CustomerId = customerId,
            Channel = channel,
            Status = ConversationStatus.Open,
            HandledBy = HandledBy.Ai,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync();
        return conversation.Id;
    }

    private static async Task SeedSlaTargetAsync(AppDbContext db, Guid businessId, TicketPriority priority, string? channel, int targetMinutes)
    {
        db.SlaTargets.Add(new SlaTarget
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            Priority = priority,
            Channel = channel,
            TargetMinutes = targetMinutes,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private record TicketResponseDto(
        Guid Id, Guid CustomerId, Guid? ConversationId, string Status, string Priority, string PriorityReason,
        string Channel, Guid? ClaimedByUserId, DateTimeOffset? ClaimedAt, Guid? ResolvedByUserId, DateTimeOffset? ResolvedAt,
        DateTimeOffset? FirstRespondedAt, DateTimeOffset? SlaDeadline, string? Summary,
        DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

    private record TicketListResponseDto(List<TicketResponseDto> Tickets, object? NextCursor);

    // ── Creation ──────────────────────────────────────────────────────

    [Fact]
    public async Task Create_Valid_Succeeds_WithSlaDeadline()
    {
        var (businessId, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Ticket Create Business");
        using var _ = ownerClient;

        Guid customerId, conversationId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            customerId = await SeedCustomerAsync(db, businessId);
            var widgetId = await SeedWidgetAsync(db, businessId);
            conversationId = await SeedConversationAsync(db, businessId, widgetId, customerId);
            await SeedSlaTargetAsync(db, businessId, TicketPriority.High, ConversationChannels.Website, 60);
        }

        var response = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, "/api/tickets", ownerCookie),
            new { CustomerId = customerId, ConversationId = conversationId, Priority = "High", PriorityReason = TicketPriorityReasons.ManualEscalation, Summary = "Needs attention" }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<TicketResponseDto>();
        Assert.NotNull(body);
        Assert.Equal("Open", body!.Status);
        Assert.Equal(ConversationChannels.Website, body.Channel);
        Assert.NotNull(body.SlaDeadline);

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var persisted = await verifyDb.Tickets.IgnoreQueryFilters().SingleAsync(t => t.Id == body.Id);
        Assert.Equal(businessId, persisted.BusinessId);
        Assert.Equal(TicketStatus.Open, persisted.Status);
    }

    [Fact]
    public async Task Create_Valid_NoMatchingSlaTarget_Succeeds_WithNullSlaDeadline()
    {
        var (businessId, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Ticket Create No SLA Business");
        using var _ = ownerClient;

        Guid customerId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            customerId = await SeedCustomerAsync(db, businessId);
        }

        var response = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, "/api/tickets", ownerCookie),
            new { CustomerId = customerId, ConversationId = (Guid?)null, Channel = ConversationChannels.Email, Priority = "Low", PriorityReason = TicketPriorityReasons.RefundDispute, Summary = (string?)null }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<TicketResponseDto>();
        Assert.NotNull(body);
        Assert.Null(body!.SlaDeadline); // no exact or fallback SlaTarget seeded — null, not invented
        Assert.Null(body.ConversationId);
        Assert.Equal(ConversationChannels.Email, body.Channel);
    }

    [Fact]
    public async Task Create_AgentCanCreate()
    {
        var (businessId, _, ownerClient, _) = await SetUpBusinessWithOwnerAsync("Ticket Create Agent Business");
        using var _ = ownerClient;

        Guid customerId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            customerId = await SeedCustomerAsync(db, businessId);
        }

        var agentEmail = $"agent-{Guid.NewGuid():N}@example.com";
        await SeedStaffIntoBusinessAsync(businessId, agentEmail, RoleNames.Agent);
        using var agentClient = NewClient();
        var (_, _, agentCookie) = await LoginAsync(agentClient, agentEmail);

        var response = await agentClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, "/api/tickets", agentCookie!),
            new { CustomerId = customerId, ConversationId = (Guid?)null, Channel = ConversationChannels.Website, Priority = "Low", PriorityReason = TicketPriorityReasons.ManualEscalation, Summary = (string?)null }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode); // AnyStaff — Agent is not restricted (Decision 3)
    }

    [Fact]
    public async Task Create_InvalidCustomer_ReturnsBadRequest()
    {
        var (_, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Ticket Create Invalid Customer Business");
        using var _ = ownerClient;

        var response = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, "/api/tickets", ownerCookie),
            new { CustomerId = Guid.NewGuid(), ConversationId = (Guid?)null, Channel = ConversationChannels.Website, Priority = "Low", PriorityReason = TicketPriorityReasons.ManualEscalation, Summary = (string?)null }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_CrossTenantCustomer_ReturnsBadRequest()
    {
        var (businessIdA, _, ownerClientA, _) = await SetUpBusinessWithOwnerAsync("Ticket Create Tenant A");
        using var _a = ownerClientA;
        Guid customerIdA;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            customerIdA = await SeedCustomerAsync(db, businessIdA);
        }

        var (_, _, ownerClientB, ownerCookieB) = await SetUpBusinessWithOwnerAsync("Ticket Create Tenant B");
        using var _b = ownerClientB;

        var response = await ownerClientB.SendAsync(WithBody(
            Authed(HttpMethod.Post, "/api/tickets", ownerCookieB),
            new { CustomerId = customerIdA, ConversationId = (Guid?)null, Channel = ConversationChannels.Website, Priority = "Low", PriorityReason = TicketPriorityReasons.ManualEscalation, Summary = (string?)null }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); // tenant filter means Business B never sees Business A's customer
    }

    [Fact]
    public async Task Create_InvalidConversation_ReturnsBadRequest()
    {
        var (businessId, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Ticket Create Invalid Conversation Business");
        using var _ = ownerClient;

        Guid customerId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            customerId = await SeedCustomerAsync(db, businessId);
        }

        var response = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, "/api/tickets", ownerCookie),
            new { CustomerId = customerId, ConversationId = Guid.NewGuid(), Priority = "Low", PriorityReason = TicketPriorityReasons.ManualEscalation, Summary = (string?)null }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_CrossTenantConversation_ReturnsBadRequest()
    {
        var (businessIdA, _, ownerClientA, _) = await SetUpBusinessWithOwnerAsync("Ticket Create Conv Tenant A");
        using var _a = ownerClientA;
        Guid conversationIdA;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var customerIdA = await SeedCustomerAsync(db, businessIdA);
            var widgetIdA = await SeedWidgetAsync(db, businessIdA);
            conversationIdA = await SeedConversationAsync(db, businessIdA, widgetIdA, customerIdA);
        }

        var (businessIdB, _, ownerClientB, ownerCookieB) = await SetUpBusinessWithOwnerAsync("Ticket Create Conv Tenant B");
        using var _b = ownerClientB;
        Guid customerIdB;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            customerIdB = await SeedCustomerAsync(db, businessIdB);
        }

        var response = await ownerClientB.SendAsync(WithBody(
            Authed(HttpMethod.Post, "/api/tickets", ownerCookieB),
            new { CustomerId = customerIdB, ConversationId = conversationIdA, Priority = "Low", PriorityReason = TicketPriorityReasons.ManualEscalation, Summary = (string?)null }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_ConversationCustomerMismatch_ReturnsBadRequest()
    {
        var (businessId, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Ticket Create Mismatch Business");
        using var _ = ownerClient;

        Guid otherCustomerId, conversationId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var conversationCustomerId = await SeedCustomerAsync(db, businessId);
            otherCustomerId = await SeedCustomerAsync(db, businessId);
            var widgetId = await SeedWidgetAsync(db, businessId);
            conversationId = await SeedConversationAsync(db, businessId, widgetId, conversationCustomerId);
        }

        var response = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, "/api/tickets", ownerCookie),
            new { CustomerId = otherCustomerId, ConversationId = conversationId, Priority = "Low", PriorityReason = TicketPriorityReasons.ManualEscalation, Summary = (string?)null }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_InvalidPriority_ReturnsBadRequest()
    {
        var (businessId, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Ticket Create Invalid Priority Business");
        using var _ = ownerClient;
        Guid customerId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            customerId = await SeedCustomerAsync(db, businessId);
        }

        var response = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, "/api/tickets", ownerCookie),
            new { CustomerId = customerId, ConversationId = (Guid?)null, Channel = ConversationChannels.Website, Priority = "NotAPriority", PriorityReason = TicketPriorityReasons.ManualEscalation, Summary = (string?)null }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_InvalidPriorityReason_ReturnsBadRequest()
    {
        var (businessId, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Ticket Create Invalid Reason Business");
        using var _ = ownerClient;
        Guid customerId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            customerId = await SeedCustomerAsync(db, businessId);
        }

        var response = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, "/api/tickets", ownerCookie),
            new { CustomerId = customerId, ConversationId = (Guid?)null, Channel = ConversationChannels.Website, Priority = "Low", PriorityReason = "NotARealReason", Summary = (string?)null }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_DuplicateActiveTicket_ReturnsConflict()
    {
        var (businessId, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Ticket Duplicate Business");
        using var _ = ownerClient;

        Guid customerId, conversationId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            customerId = await SeedCustomerAsync(db, businessId);
            var widgetId = await SeedWidgetAsync(db, businessId);
            conversationId = await SeedConversationAsync(db, businessId, widgetId, customerId);
        }

        var request = new { CustomerId = customerId, ConversationId = conversationId, Priority = "Low", PriorityReason = TicketPriorityReasons.ManualEscalation, Summary = (string?)null };

        var first = await ownerClient.SendAsync(WithBody(Authed(HttpMethod.Post, "/api/tickets", ownerCookie), request));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await ownerClient.SendAsync(WithBody(Authed(HttpMethod.Post, "/api/tickets", ownerCookie), request));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var count = await verifyDb.Tickets.IgnoreQueryFilters().CountAsync(t => t.ConversationId == conversationId);
        Assert.Equal(1, count);
    }

    /// <summary>
    /// Genuinely concurrent requests, no advisory lock/barrier — the
    /// database's IX_Tickets_ActiveTicketPerConversation partial unique
    /// index is the sole authority (Decision 2). Two independent
    /// HttpClient instances started via Task.WhenAll is sufficient: even
    /// if one request happens to fully complete before the other starts,
    /// the outcome the test verifies (exactly one winner, one loser, one
    /// row) is what the DB constraint guarantees regardless of timing.
    /// </summary>
    [Fact]
    public async Task Create_TwoConcurrentRequests_SameConversation_OnlyOneSucceeds()
    {
        var (businessId, _, ownerClient1, ownerCookie) = await SetUpBusinessWithOwnerAsync("Ticket Create Concurrent Business");
        using var _1 = ownerClient1;
        using var ownerClient2 = NewClient();

        Guid customerId, conversationId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            customerId = await SeedCustomerAsync(db, businessId);
            var widgetId = await SeedWidgetAsync(db, businessId);
            conversationId = await SeedConversationAsync(db, businessId, widgetId, customerId);
        }

        var request = new { CustomerId = customerId, ConversationId = conversationId, Priority = "Low", PriorityReason = TicketPriorityReasons.ManualEscalation, Summary = (string?)null };

        var task1 = ownerClient1.SendAsync(WithBody(Authed(HttpMethod.Post, "/api/tickets", ownerCookie), request));
        var task2 = ownerClient2.SendAsync(WithBody(Authed(HttpMethod.Post, "/api/tickets", ownerCookie), request));

        await Task.WhenAll(task1, task2);
        var statusCodes = new[] { (await task1).StatusCode, (await task2).StatusCode };

        Assert.Contains(HttpStatusCode.Created, statusCodes);
        Assert.Contains(HttpStatusCode.Conflict, statusCodes);

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var count = await verifyDb.Tickets.IgnoreQueryFilters().CountAsync(t => t.ConversationId == conversationId && t.Status != TicketStatus.Resolved);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Create_WritesAuditEvent_Atomically()
    {
        var (businessId, ownerId, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Ticket Create Audit Business");
        using var _ = ownerClient;
        Guid customerId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            customerId = await SeedCustomerAsync(db, businessId);
        }

        var response = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, "/api/tickets", ownerCookie),
            new { CustomerId = customerId, ConversationId = (Guid?)null, Channel = ConversationChannels.Website, Priority = "Low", PriorityReason = TicketPriorityReasons.ManualEscalation, Summary = "some free text that must never be audited" }));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<TicketResponseDto>();

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await verifyDb.Tickets.IgnoreQueryFilters().AnyAsync(t => t.Id == body!.Id));

        var auditLogs = await verifyDb.AuditLogs.IgnoreQueryFilters()
            .Where(a => a.BusinessId == businessId && a.EntityId == body!.Id.ToString())
            .ToListAsync();
        var entry = Assert.Single(auditLogs);
        Assert.Equal(AuditEntityTypes.Ticket, entry.EntityType);
        Assert.Equal(AuditEventTypes.TicketCreated, entry.EventType);
        Assert.Equal(ownerId, entry.ChangedByUserId);
        Assert.DoesNotContain("some free text", entry.NewValue);
    }

    // ── Claim ─────────────────────────────────────────────────────────

    private async Task<(Guid TicketId, Guid ConversationId)> SeedTicketAsync(
        Guid businessId, TicketStatus status = TicketStatus.Open, Guid? claimedByUserId = null, Guid? resolvedByUserId = null)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var customerId = await SeedCustomerAsync(db, businessId);
        var widgetId = await SeedWidgetAsync(db, businessId);
        var conversationId = await SeedConversationAsync(db, businessId, widgetId, customerId);

        var now = DateTimeOffset.UtcNow;
        var ticket = new Ticket
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            CustomerId = customerId,
            ConversationId = conversationId,
            Status = status,
            Priority = TicketPriority.Low,
            PriorityReason = TicketPriorityReasons.ManualEscalation,
            Channel = ConversationChannels.Website,
            ClaimedByUserId = claimedByUserId,
            ClaimedAt = claimedByUserId is not null ? now : null,
            ResolvedByUserId = resolvedByUserId,
            ResolvedAt = resolvedByUserId is not null ? now : null,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Tickets.Add(ticket);
        await db.SaveChangesAsync();
        return (ticket.Id, conversationId);
    }

    [Fact]
    public async Task Claim_Open_Succeeds_TransitionsToInProgress_WritesAudit()
    {
        var (businessId, ownerId, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Ticket Claim Business");
        using var _ = ownerClient;
        var (ticketId, _) = await SeedTicketAsync(businessId);

        var response = await ownerClient.SendAsync(Authed(HttpMethod.Post, $"/api/tickets/{ticketId}/claim", ownerCookie));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ticket = await verifyDb.Tickets.IgnoreQueryFilters().SingleAsync(t => t.Id == ticketId);
        Assert.Equal(TicketStatus.InProgress, ticket.Status);
        Assert.Equal(ownerId, ticket.ClaimedByUserId);
        Assert.NotNull(ticket.ClaimedAt);

        var auditLogs = await verifyDb.AuditLogs.IgnoreQueryFilters()
            .CountAsync(a => a.EntityId == ticketId.ToString() && a.EventType == AuditEventTypes.TicketClaimed);
        Assert.Equal(1, auditLogs);
    }

    [Fact]
    public async Task Claim_AlreadyClaimed_ReturnsConflict()
    {
        var (businessId, ownerId, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Ticket Claim Already Business");
        using var _ = ownerClient;
        var (ticketId, _) = await SeedTicketAsync(businessId, TicketStatus.InProgress, claimedByUserId: ownerId);

        var response = await ownerClient.SendAsync(Authed(HttpMethod.Post, $"/api/tickets/{ticketId}/claim", ownerCookie));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Claim_CrossTenant_ReturnsNotFound()
    {
        var (businessIdA, _, ownerClientA, _) = await SetUpBusinessWithOwnerAsync("Ticket Claim Tenant A");
        using var _a = ownerClientA;
        var (ticketId, _) = await SeedTicketAsync(businessIdA);

        var (_, _, ownerClientB, ownerCookieB) = await SetUpBusinessWithOwnerAsync("Ticket Claim Tenant B");
        using var _b = ownerClientB;

        var response = await ownerClientB.SendAsync(Authed(HttpMethod.Post, $"/api/tickets/{ticketId}/claim", ownerCookieB));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Claim_TwoConcurrentRequests_OnlyOneWins()
    {
        var (businessId, _, ownerClient1, ownerCookie) = await SetUpBusinessWithOwnerAsync("Ticket Claim Concurrent Business");
        using var _1 = ownerClient1;
        using var ownerClient2 = NewClient();
        var (ticketId, _) = await SeedTicketAsync(businessId);

        var task1 = ownerClient1.SendAsync(Authed(HttpMethod.Post, $"/api/tickets/{ticketId}/claim", ownerCookie));
        var task2 = ownerClient2.SendAsync(Authed(HttpMethod.Post, $"/api/tickets/{ticketId}/claim", ownerCookie));

        await Task.WhenAll(task1, task2);
        var statusCodes = new[] { (await task1).StatusCode, (await task2).StatusCode };

        Assert.Contains(HttpStatusCode.NoContent, statusCodes);
        Assert.Contains(HttpStatusCode.Conflict, statusCodes);

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ticket = await verifyDb.Tickets.IgnoreQueryFilters().SingleAsync(t => t.Id == ticketId);
        Assert.Equal(TicketStatus.InProgress, ticket.Status);
        Assert.NotNull(ticket.ClaimedByUserId);

        var auditCount = await verifyDb.AuditLogs.IgnoreQueryFilters()
            .CountAsync(a => a.EntityId == ticketId.ToString() && a.EventType == AuditEventTypes.TicketClaimed);
        Assert.Equal(1, auditCount); // only the winner audits
    }

    // ── Unclaim ───────────────────────────────────────────────────────

    [Fact]
    public async Task Unclaim_InProgress_Succeeds_ReturnsToOpen()
    {
        var (businessId, ownerId, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Ticket Unclaim Business");
        using var _ = ownerClient;
        var (ticketId, _) = await SeedTicketAsync(businessId, TicketStatus.InProgress, claimedByUserId: ownerId);

        var response = await ownerClient.SendAsync(Authed(HttpMethod.Post, $"/api/tickets/{ticketId}/unclaim", ownerCookie));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ticket = await verifyDb.Tickets.IgnoreQueryFilters().SingleAsync(t => t.Id == ticketId);
        Assert.Equal(TicketStatus.Open, ticket.Status);
        Assert.Null(ticket.ClaimedByUserId);
        Assert.Null(ticket.ClaimedAt);
    }

    [Fact]
    public async Task Unclaim_NotInProgress_ReturnsConflict()
    {
        var (businessId, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Ticket Unclaim Invalid Business");
        using var _ = ownerClient;
        var (ticketId, _) = await SeedTicketAsync(businessId); // Open, not InProgress

        var response = await ownerClient.SendAsync(Authed(HttpMethod.Post, $"/api/tickets/{ticketId}/unclaim", ownerCookie));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Unclaim_CrossTenant_ReturnsNotFound()
    {
        var (businessIdA, ownerIdA, ownerClientA, _) = await SetUpBusinessWithOwnerAsync("Ticket Unclaim Tenant A");
        using var _a = ownerClientA;
        var (ticketId, _) = await SeedTicketAsync(businessIdA, TicketStatus.InProgress, claimedByUserId: ownerIdA);

        var (_, _, ownerClientB, ownerCookieB) = await SetUpBusinessWithOwnerAsync("Ticket Unclaim Tenant B");
        using var _b = ownerClientB;

        var response = await ownerClientB.SendAsync(Authed(HttpMethod.Post, $"/api/tickets/{ticketId}/unclaim", ownerCookieB));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── Resolve ───────────────────────────────────────────────────────

    [Fact]
    public async Task Resolve_InProgress_Succeeds_WritesAudit()
    {
        var (businessId, ownerId, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Ticket Resolve Business");
        using var _ = ownerClient;
        var (ticketId, _) = await SeedTicketAsync(businessId, TicketStatus.InProgress, claimedByUserId: ownerId);

        var response = await ownerClient.SendAsync(Authed(HttpMethod.Post, $"/api/tickets/{ticketId}/resolve", ownerCookie));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ticket = await verifyDb.Tickets.IgnoreQueryFilters().SingleAsync(t => t.Id == ticketId);
        Assert.Equal(TicketStatus.Resolved, ticket.Status);
        Assert.Equal(ownerId, ticket.ResolvedByUserId);
        Assert.NotNull(ticket.ResolvedAt);

        var auditCount = await verifyDb.AuditLogs.IgnoreQueryFilters()
            .CountAsync(a => a.EntityId == ticketId.ToString() && a.EventType == AuditEventTypes.TicketResolved);
        Assert.Equal(1, auditCount);
    }

    [Fact]
    public async Task Resolve_SecondResolve_IsIdempotent_PreservesOriginalResolver_NoAdditionalAudit()
    {
        var (businessId, ownerId, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Ticket Resolve Idempotent Business");
        using var _ = ownerClient;
        var (ticketId, _) = await SeedTicketAsync(businessId, TicketStatus.InProgress, claimedByUserId: ownerId);

        var first = await ownerClient.SendAsync(Authed(HttpMethod.Post, $"/api/tickets/{ticketId}/resolve", ownerCookie));
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);

        var adminEmail = $"admin-{Guid.NewGuid():N}@example.com";
        await SeedStaffIntoBusinessAsync(businessId, adminEmail, RoleNames.Admin);
        using var adminClient = NewClient();
        var (_, _, adminCookie) = await LoginAsync(adminClient, adminEmail);

        var second = await adminClient.SendAsync(Authed(HttpMethod.Post, $"/api/tickets/{ticketId}/resolve", adminCookie!));
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ticket = await verifyDb.Tickets.IgnoreQueryFilters().SingleAsync(t => t.Id == ticketId);
        Assert.Equal(ownerId, ticket.ResolvedByUserId); // original resolver preserved, not overwritten by the second caller

        var auditCount = await verifyDb.AuditLogs.IgnoreQueryFilters()
            .CountAsync(a => a.EntityId == ticketId.ToString() && a.EventType == AuditEventTypes.TicketResolved);
        Assert.Equal(1, auditCount); // second, no-op resolve wrote nothing
    }

    [Fact]
    public async Task Resolve_NotInProgress_ReturnsConflict()
    {
        var (businessId, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Ticket Resolve Invalid Business");
        using var _ = ownerClient;
        var (ticketId, _) = await SeedTicketAsync(businessId); // Open, not InProgress

        var response = await ownerClient.SendAsync(Authed(HttpMethod.Post, $"/api/tickets/{ticketId}/resolve", ownerCookie));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Resolve_CrossTenant_ReturnsNotFound()
    {
        var (businessIdA, ownerIdA, ownerClientA, _) = await SetUpBusinessWithOwnerAsync("Ticket Resolve Tenant A");
        using var _a = ownerClientA;
        var (ticketId, _) = await SeedTicketAsync(businessIdA, TicketStatus.InProgress, claimedByUserId: ownerIdA);

        var (_, _, ownerClientB, ownerCookieB) = await SetUpBusinessWithOwnerAsync("Ticket Resolve Tenant B");
        using var _b = ownerClientB;

        var response = await ownerClientB.SendAsync(Authed(HttpMethod.Post, $"/api/tickets/{ticketId}/resolve", ownerCookieB));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// Two genuinely concurrent resolve requests. Proves the loser's
    /// affected-count-is-zero path correctly distinguishes "already
    /// resolved by the winner" (idempotent 204) from a genuine conflict —
    /// exactly one audit row must exist regardless of which request the
    /// server happened to process first.
    /// </summary>
    [Fact]
    public async Task Resolve_TwoConcurrentRequests_ExactlyOneAuditRow_BothReturnSuccess()
    {
        var (businessId, ownerId, ownerClient1, ownerCookie) = await SetUpBusinessWithOwnerAsync("Ticket Resolve Concurrent Business");
        using var _1 = ownerClient1;
        using var ownerClient2 = NewClient();
        var (ticketId, _) = await SeedTicketAsync(businessId, TicketStatus.InProgress, claimedByUserId: ownerId);

        var task1 = ownerClient1.SendAsync(Authed(HttpMethod.Post, $"/api/tickets/{ticketId}/resolve", ownerCookie));
        var task2 = ownerClient2.SendAsync(Authed(HttpMethod.Post, $"/api/tickets/{ticketId}/resolve", ownerCookie));

        await Task.WhenAll(task1, task2);
        Assert.Equal(HttpStatusCode.NoContent, (await task1).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await task2).StatusCode); // both succeed — resolve is idempotent, not a race to reject the loser

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var auditCount = await verifyDb.AuditLogs.IgnoreQueryFilters()
            .CountAsync(a => a.EntityId == ticketId.ToString() && a.EventType == AuditEventTypes.TicketResolved);
        Assert.Equal(1, auditCount);
    }

    // ── Reopen ────────────────────────────────────────────────────────

    [Fact]
    public async Task Reopen_Resolved_Succeeds_ClearsAllFourFields_PreservesResolveAudit()
    {
        var (businessId, ownerId, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Ticket Reopen Business");
        using var _ = ownerClient;
        var (ticketId, _) = await SeedTicketAsync(businessId, TicketStatus.InProgress, claimedByUserId: ownerId);

        var resolveResponse = await ownerClient.SendAsync(Authed(HttpMethod.Post, $"/api/tickets/{ticketId}/resolve", ownerCookie));
        Assert.Equal(HttpStatusCode.NoContent, resolveResponse.StatusCode);

        var reopenResponse = await ownerClient.SendAsync(Authed(HttpMethod.Post, $"/api/tickets/{ticketId}/reopen", ownerCookie));
        Assert.Equal(HttpStatusCode.NoContent, reopenResponse.StatusCode);

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ticket = await verifyDb.Tickets.IgnoreQueryFilters().SingleAsync(t => t.Id == ticketId);
        Assert.Equal(TicketStatus.Reopened, ticket.Status);
        Assert.Null(ticket.ClaimedByUserId);
        Assert.Null(ticket.ClaimedAt);
        Assert.Null(ticket.ResolvedByUserId);
        Assert.Null(ticket.ResolvedAt);

        // The historical resolve event is still visible in AuditLog even
        // though the live Ticket row's own fields were cleared.
        var resolveAuditCount = await verifyDb.AuditLogs.IgnoreQueryFilters()
            .CountAsync(a => a.EntityId == ticketId.ToString() && a.EventType == AuditEventTypes.TicketResolved);
        Assert.Equal(1, resolveAuditCount);
        var reopenAuditCount = await verifyDb.AuditLogs.IgnoreQueryFilters()
            .CountAsync(a => a.EntityId == ticketId.ToString() && a.EventType == AuditEventTypes.TicketReopened);
        Assert.Equal(1, reopenAuditCount);
    }

    [Fact]
    public async Task Reopen_NotResolved_ReturnsConflict()
    {
        var (businessId, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Ticket Reopen Invalid Business");
        using var _ = ownerClient;
        var (ticketId, _) = await SeedTicketAsync(businessId); // Open, not Resolved

        var response = await ownerClient.SendAsync(Authed(HttpMethod.Post, $"/api/tickets/{ticketId}/reopen", ownerCookie));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Reopen_CrossTenant_ReturnsNotFound()
    {
        var (businessIdA, ownerIdA, ownerClientA, _) = await SetUpBusinessWithOwnerAsync("Ticket Reopen Tenant A");
        using var _a = ownerClientA;
        var (ticketId, _) = await SeedTicketAsync(businessIdA, TicketStatus.Resolved, resolvedByUserId: ownerIdA);

        var (_, _, ownerClientB, ownerCookieB) = await SetUpBusinessWithOwnerAsync("Ticket Reopen Tenant B");
        using var _b = ownerClientB;

        var response = await ownerClientB.SendAsync(Authed(HttpMethod.Post, $"/api/tickets/{ticketId}/reopen", ownerCookieB));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// Reopening the previously-resolved ticket frees its ConversationId
    /// under the partial unique index (Status moves off Resolved), so a
    /// genuinely new active ticket cannot exist for the same conversation
    /// at the same time — but the reopened ticket itself now legitimately
    /// occupies that "active" slot, proving Decision 2's invariant still
    /// holds across a full resolve→reopen cycle.
    /// </summary>
    [Fact]
    public async Task Reopen_ThenDuplicateCreateAttempt_StillConflicts()
    {
        var (businessId, ownerId, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Ticket Reopen Duplicate Business");
        using var _ = ownerClient;
        var (ticketId, conversationId) = await SeedTicketAsync(businessId, TicketStatus.InProgress, claimedByUserId: ownerId);

        await ownerClient.SendAsync(Authed(HttpMethod.Post, $"/api/tickets/{ticketId}/resolve", ownerCookie));
        await ownerClient.SendAsync(Authed(HttpMethod.Post, $"/api/tickets/{ticketId}/reopen", ownerCookie));

        Guid customerId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            customerId = await db.Tickets.IgnoreQueryFilters().Where(t => t.Id == ticketId).Select(t => t.CustomerId).SingleAsync();
        }

        var response = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, "/api/tickets", ownerCookie),
            new { CustomerId = customerId, ConversationId = conversationId, Priority = "Low", PriorityReason = TicketPriorityReasons.ManualEscalation, Summary = (string?)null }));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode); // the Reopened ticket is still "active" (non-Resolved)
    }

    // ── Priority ──────────────────────────────────────────────────────

    [Fact]
    public async Task UpdatePriority_Succeeds_WritesAudit()
    {
        var (businessId, ownerId, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Ticket Priority Business");
        using var _ = ownerClient;
        var (ticketId, _) = await SeedTicketAsync(businessId);

        var response = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Put, $"/api/tickets/{ticketId}/priority", ownerCookie),
            new { Priority = "Urgent" }));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ticket = await verifyDb.Tickets.IgnoreQueryFilters().SingleAsync(t => t.Id == ticketId);
        Assert.Equal(TicketPriority.Urgent, ticket.Priority);

        var auditLogs = await verifyDb.AuditLogs.IgnoreQueryFilters()
            .Where(a => a.EntityId == ticketId.ToString() && a.EventType == AuditEventTypes.TicketPriorityChanged)
            .ToListAsync();
        var entry = Assert.Single(auditLogs);
        Assert.Equal(ownerId, entry.ChangedByUserId);
        using var oldDoc = JsonDocument.Parse(entry.OldValue!);
        Assert.Equal("Low", oldDoc.RootElement.GetProperty("Priority").GetString());
        using var newDoc = JsonDocument.Parse(entry.NewValue!);
        Assert.Equal("Urgent", newDoc.RootElement.GetProperty("Priority").GetString());
    }

    [Fact]
    public async Task UpdatePriority_InvalidValue_ReturnsBadRequest()
    {
        var (businessId, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Ticket Priority Invalid Business");
        using var _ = ownerClient;
        var (ticketId, _) = await SeedTicketAsync(businessId);

        var response = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Put, $"/api/tickets/{ticketId}/priority", ownerCookie),
            new { Priority = "SuperUrgent" }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UpdatePriority_AllowedAfterResolution()
    {
        var (businessId, ownerId, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Ticket Priority After Resolve Business");
        using var _ = ownerClient;
        var (ticketId, _) = await SeedTicketAsync(businessId, TicketStatus.Resolved, resolvedByUserId: ownerId);

        var response = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Put, $"/api/tickets/{ticketId}/priority", ownerCookie),
            new { Priority = "High" }));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ticket = await verifyDb.Tickets.IgnoreQueryFilters().SingleAsync(t => t.Id == ticketId);
        Assert.Equal(TicketPriority.High, ticket.Priority);
        Assert.Equal(TicketStatus.Resolved, ticket.Status); // resolution untouched by a priority change
    }

    // ── List / Get ────────────────────────────────────────────────────

    [Fact]
    public async Task List_ReturnsSameTenantTickets_NeverAnotherBusinesss()
    {
        var (businessIdA, _, ownerClientA, ownerCookieA) = await SetUpBusinessWithOwnerAsync("Ticket List Tenant A");
        using var _a = ownerClientA;
        var (ticketIdA, _) = await SeedTicketAsync(businessIdA);

        var (businessIdB, _, ownerClientB, _) = await SetUpBusinessWithOwnerAsync("Ticket List Tenant B");
        using var _b = ownerClientB;
        var (ticketIdB, _) = await SeedTicketAsync(businessIdB);

        var listA = await ownerClientA.SendAsync(Authed(HttpMethod.Get, "/api/tickets", ownerCookieA));
        Assert.Equal(HttpStatusCode.OK, listA.StatusCode);
        var bodyA = await listA.Content.ReadFromJsonAsync<TicketListResponseDto>();
        Assert.Contains(bodyA!.Tickets, t => t.Id == ticketIdA);
        Assert.DoesNotContain(bodyA.Tickets, t => t.Id == ticketIdB);
    }

    [Fact]
    public async Task List_StatusFilter_ReturnsOnlyMatchingStatus()
    {
        var (businessId, ownerId, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Ticket List Filter Business");
        using var _ = ownerClient;
        var (openId, _) = await SeedTicketAsync(businessId);
        var (resolvedId, _) = await SeedTicketAsync(businessId, TicketStatus.Resolved, resolvedByUserId: ownerId);

        var response = await ownerClient.SendAsync(Authed(HttpMethod.Get, "/api/tickets?status=Open", ownerCookie));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<TicketListResponseDto>();
        Assert.Contains(body!.Tickets, t => t.Id == openId);
        Assert.DoesNotContain(body.Tickets, t => t.Id == resolvedId);
    }

    [Fact]
    public async Task Get_SameTenant_Succeeds()
    {
        var (businessId, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Ticket Get Business");
        using var _ = ownerClient;
        var (ticketId, _) = await SeedTicketAsync(businessId);

        var response = await ownerClient.SendAsync(Authed(HttpMethod.Get, $"/api/tickets/{ticketId}", ownerCookie));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<TicketResponseDto>();
        Assert.Equal(ticketId, body!.Id);
    }

    [Fact]
    public async Task Get_CrossTenant_ReturnsNotFound()
    {
        var (businessIdA, _, ownerClientA, _) = await SetUpBusinessWithOwnerAsync("Ticket Get Tenant A");
        using var _a = ownerClientA;
        var (ticketId, _) = await SeedTicketAsync(businessIdA);

        var (_, _, ownerClientB, ownerCookieB) = await SetUpBusinessWithOwnerAsync("Ticket Get Tenant B");
        using var _b = ownerClientB;

        var response = await ownerClientB.SendAsync(Authed(HttpMethod.Get, $"/api/tickets/{ticketId}", ownerCookieB));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
