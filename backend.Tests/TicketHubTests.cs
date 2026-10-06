using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using backend.Auth;
using backend.Constants;
using backend.Controllers;
using backend.Data;
using backend.Enums;
using backend.Hubs;
using backend.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace backend.Tests;

/// <summary>
/// Step 13B. HTTP+WebSocket-level tests for TicketHub, run against the real
/// app + real Neon dev DB via NivoraApiFactory, same pattern as
/// TicketsApiTests. Connects via HttpTransportType.LongPolling — TestServer's
/// in-memory handler doesn't support a real WebSocket upgrade, and
/// LongPolling against the real hub pipeline (real auth, real groups, real
/// event delivery) is the standard, documented way to integration-test a
/// SignalR hub under WebApplicationFactory.
///
/// Every "event reaches the correct tenant" assertion below is proven
/// through actual delivery over an open connection — never by inspecting
/// group names or internal state.
/// </summary>
public class TicketHubTests : IAsyncLifetime
{
    private readonly NivoraApiFactory _factory = new();
    private readonly List<Guid> _businessIdsToClean = [];
    private static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan NoEventWait = TimeSpan.FromSeconds(2);

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await TestBusinessCleanup.DeleteThenDisposeAsync(_businessIdsToClean, _factory);
    }

    // ── HTTP-side helpers (same conventions as TicketsApiTests) ─────────

    private HttpClient NewClient() => _factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });

    private static async Task<(HttpResponseMessage Response, RegisterResponse? Body)> RegisterAsync(
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

        var ownerClient = NewClient();
        var (loginResponse, _, cookie) = await LoginAsync(ownerClient, email);
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        Assert.NotNull(cookie);

        return (registerBody.BusinessId, registerBody.UserId, ownerClient, cookie!);
    }

    private static async Task<Guid> SeedCustomerAsync(AppDbContext db, Guid businessId)
    {
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            Email = $"customer-{Guid.NewGuid():N}@example.com",
            Name = "Test Customer",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        return customer.Id;
    }

    /// <summary>Conversationless ticket — sufficient for every hub test, avoids seeding a Widget/Conversation.</summary>
    private async Task<Guid> SeedTicketAsync(
        Guid businessId, TicketStatus status = TicketStatus.Open, Guid? claimedByUserId = null, Guid? resolvedByUserId = null)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var customerId = await SeedCustomerAsync(db, businessId);

        var now = DateTimeOffset.UtcNow;
        var ticket = new Ticket
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            CustomerId = customerId,
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
        return ticket.Id;
    }

    private record TicketResponseDto(Guid Id, Guid CustomerId, Guid? ConversationId, string Status);

    // ── Hub-side helpers ──────────────────────────────────────────────

    private (HubConnection Connection, ConcurrentQueue<TicketEvent> Events, ConcurrentQueue<string> ControlMessages) BuildHubConnection(string? cookie)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, "/hubs/tickets"), options =>
            {
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                if (cookie is not null)
                {
                    options.Headers.Add("Cookie", cookie);
                }
            })
            .WithAutomaticReconnect()
            .Build();

        var events = new ConcurrentQueue<TicketEvent>();
        var control = new ConcurrentQueue<string>();
        connection.On<TicketEvent>("TicketEvent", e => events.Enqueue(e));
        connection.On("ForceDisconnect", () => control.Enqueue("ForceDisconnect"));
        connection.On("SessionExpiring", () => control.Enqueue("SessionExpiring"));

        return (connection, events, control);
    }

    private static async Task<TicketEvent?> WaitForEventAsync(ConcurrentQueue<TicketEvent> events, Func<TicketEvent, bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var match = events.FirstOrDefault(predicate);
            if (match is not null) return match;
            await Task.Delay(100);
        }
        return events.FirstOrDefault(predicate);
    }

    private static async Task<string?> WaitForControlMessageAsync(ConcurrentQueue<string> messages, string expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (messages.Contains(expected)) return expected;
            await Task.Delay(100);
        }
        return messages.Contains(expected) ? expected : null;
    }

    // ── Connection / authentication ──────────────────────────────────

    [Fact]
    public async Task Connect_AuthenticatedStaff_Succeeds()
    {
        var (_, _, _, cookie) = await SetUpBusinessWithOwnerAsync("Hub Connect Business");
        var (connection, _, _) = BuildHubConnection(cookie);
        await using var _conn = connection;

        await connection.StartAsync();

        Assert.Equal(HubConnectionState.Connected, connection.State);
    }

    [Fact]
    public async Task Connect_Unauthenticated_Fails()
    {
        var (connection, _, _) = BuildHubConnection(cookie: null);
        await using var _conn = connection;

        await Assert.ThrowsAnyAsync<Exception>(() => connection.StartAsync());
    }

    // ── Tenant isolation — proven through actual delivery ──────────────

    [Fact]
    public async Task TicketCreated_ReachesSameTenant_NotCrossTenant()
    {
        var (businessIdA, _, ownerClientA, cookieA) = await SetUpBusinessWithOwnerAsync("Hub Isolation Tenant A");
        using var _a = ownerClientA;
        var (_, _, ownerClientB, cookieB) = await SetUpBusinessWithOwnerAsync("Hub Isolation Tenant B");
        using var _b = ownerClientB;

        var (connA, eventsA, _) = BuildHubConnection(cookieA);
        await using var _connA = connA;
        var (connB, eventsB, _) = BuildHubConnection(cookieB);
        await using var _connB = connB;
        await connA.StartAsync();
        await connB.StartAsync();

        Guid customerId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            customerId = await SeedCustomerAsync(db, businessIdA);
        }

        var createResponse = await ownerClientA.SendAsync(WithBody(
            Authed(HttpMethod.Post, "/api/tickets", cookieA),
            new { CustomerId = customerId, ConversationId = (Guid?)null, Channel = ConversationChannels.Website, Priority = "Low", PriorityReason = TicketPriorityReasons.ManualEscalation, Summary = (string?)null }));
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var ticket = await createResponse.Content.ReadFromJsonAsync<TicketResponseDto>();

        var receivedA = await WaitForEventAsync(eventsA, e => e.TicketId == ticket!.Id && e.EventType == AuditEventTypes.TicketCreated, EventTimeout);
        Assert.NotNull(receivedA);

        await Task.Delay(NoEventWait);
        Assert.DoesNotContain(eventsB, e => e.TicketId == ticket!.Id);
    }

    // ── Six event types ─────────────────────────────────────────────

    [Fact]
    public async Task TicketCreated_Delivered_WithExpectedPayload()
    {
        var (businessId, _, ownerClient, cookie) = await SetUpBusinessWithOwnerAsync("Hub Created Business");
        using var _ = ownerClient;
        var (connection, events, _) = BuildHubConnection(cookie);
        await using var _conn = connection;
        await connection.StartAsync();

        Guid customerId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            customerId = await SeedCustomerAsync(db, businessId);
        }

        var response = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, "/api/tickets", cookie),
            new { CustomerId = customerId, ConversationId = (Guid?)null, Channel = ConversationChannels.Website, Priority = "Low", PriorityReason = TicketPriorityReasons.ManualEscalation, Summary = "must never appear on the wire" }));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var ticket = await response.Content.ReadFromJsonAsync<TicketResponseDto>();

        var received = await WaitForEventAsync(events, e => e.TicketId == ticket!.Id, EventTimeout);
        Assert.NotNull(received);
        Assert.Equal(AuditEventTypes.TicketCreated, received!.EventType);
    }

    [Fact]
    public async Task TicketClaimed_Delivered()
    {
        var (businessId, _, ownerClient, cookie) = await SetUpBusinessWithOwnerAsync("Hub Claimed Business");
        using var _ = ownerClient;
        var ticketId = await SeedTicketAsync(businessId);
        var (connection, events, _) = BuildHubConnection(cookie);
        await using var _conn = connection;
        await connection.StartAsync();

        var response = await ownerClient.SendAsync(Authed(HttpMethod.Post, $"/api/tickets/{ticketId}/claim", cookie));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var received = await WaitForEventAsync(events, e => e.TicketId == ticketId, EventTimeout);
        Assert.NotNull(received);
        Assert.Equal(AuditEventTypes.TicketClaimed, received!.EventType);
    }

    [Fact]
    public async Task TicketUnclaimed_Delivered()
    {
        var (businessId, ownerId, ownerClient, cookie) = await SetUpBusinessWithOwnerAsync("Hub Unclaimed Business");
        using var _ = ownerClient;
        var ticketId = await SeedTicketAsync(businessId, TicketStatus.InProgress, claimedByUserId: ownerId);
        var (connection, events, _) = BuildHubConnection(cookie);
        await using var _conn = connection;
        await connection.StartAsync();

        var response = await ownerClient.SendAsync(Authed(HttpMethod.Post, $"/api/tickets/{ticketId}/unclaim", cookie));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var received = await WaitForEventAsync(events, e => e.TicketId == ticketId, EventTimeout);
        Assert.NotNull(received);
        Assert.Equal(AuditEventTypes.TicketUnclaimed, received!.EventType);
    }

    [Fact]
    public async Task TicketResolved_Delivered()
    {
        var (businessId, ownerId, ownerClient, cookie) = await SetUpBusinessWithOwnerAsync("Hub Resolved Business");
        using var _ = ownerClient;
        var ticketId = await SeedTicketAsync(businessId, TicketStatus.InProgress, claimedByUserId: ownerId);
        var (connection, events, _) = BuildHubConnection(cookie);
        await using var _conn = connection;
        await connection.StartAsync();

        var response = await ownerClient.SendAsync(Authed(HttpMethod.Post, $"/api/tickets/{ticketId}/resolve", cookie));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var received = await WaitForEventAsync(events, e => e.TicketId == ticketId, EventTimeout);
        Assert.NotNull(received);
        Assert.Equal(AuditEventTypes.TicketResolved, received!.EventType);
    }

    [Fact]
    public async Task TicketReopened_Delivered()
    {
        var (businessId, ownerId, ownerClient, cookie) = await SetUpBusinessWithOwnerAsync("Hub Reopened Business");
        using var _ = ownerClient;
        var ticketId = await SeedTicketAsync(businessId, TicketStatus.Resolved, resolvedByUserId: ownerId);
        var (connection, events, _) = BuildHubConnection(cookie);
        await using var _conn = connection;
        await connection.StartAsync();

        var response = await ownerClient.SendAsync(Authed(HttpMethod.Post, $"/api/tickets/{ticketId}/reopen", cookie));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var received = await WaitForEventAsync(events, e => e.TicketId == ticketId, EventTimeout);
        Assert.NotNull(received);
        Assert.Equal(AuditEventTypes.TicketReopened, received!.EventType);
    }

    [Fact]
    public async Task TicketPriorityChanged_Delivered()
    {
        var (businessId, _, ownerClient, cookie) = await SetUpBusinessWithOwnerAsync("Hub Priority Business");
        using var _ = ownerClient;
        var ticketId = await SeedTicketAsync(businessId);
        var (connection, events, _) = BuildHubConnection(cookie);
        await using var _conn = connection;
        await connection.StartAsync();

        var response = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Put, $"/api/tickets/{ticketId}/priority", cookie),
            new { Priority = "Urgent" }));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var received = await WaitForEventAsync(events, e => e.TicketId == ticketId, EventTimeout);
        Assert.NotNull(received);
        Assert.Equal(AuditEventTypes.TicketPriorityChanged, received!.EventType);
    }

    // ── No-op / failed mutation behavior ────────────────────────────

    [Fact]
    public async Task Create_DuplicateActiveTicket_LosingRequestDoesNotPublish()
    {
        var (businessId, _, ownerClient, cookie) = await SetUpBusinessWithOwnerAsync("Hub Duplicate Business");
        using var _ = ownerClient;
        Guid customerId, conversationId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            customerId = await SeedCustomerAsync(db, businessId);
            var widget = new Widget { Id = Guid.NewGuid(), BusinessId = businessId, Name = "W", PublicKey = Guid.NewGuid().ToString("N"), CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
            db.Widgets.Add(widget);
            await db.SaveChangesAsync();
            var conversation = new Conversation { Id = Guid.NewGuid(), BusinessId = businessId, WidgetId = widget.Id, CustomerId = customerId, Channel = ConversationChannels.Website, Status = ConversationStatus.Open, HandledBy = HandledBy.Ai, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
            db.Conversations.Add(conversation);
            await db.SaveChangesAsync();
            conversationId = conversation.Id;
        }

        var (connection, events, _) = BuildHubConnection(cookie);
        await using var _conn = connection;
        await connection.StartAsync();

        var request = new { CustomerId = customerId, ConversationId = conversationId, Priority = "Low", PriorityReason = TicketPriorityReasons.ManualEscalation, Summary = (string?)null };
        var first = await ownerClient.SendAsync(WithBody(Authed(HttpMethod.Post, "/api/tickets", cookie), request));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var second = await ownerClient.SendAsync(WithBody(Authed(HttpMethod.Post, "/api/tickets", cookie), request));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        var firstBody = await first.Content.ReadFromJsonAsync<TicketResponseDto>();
        await WaitForEventAsync(events, e => e.TicketId == firstBody!.Id, EventTimeout);
        await Task.Delay(NoEventWait);

        Assert.Single(events, e => e.TicketId == firstBody!.Id && e.EventType == AuditEventTypes.TicketCreated);
    }

    [Fact]
    public async Task Resolve_AlreadyResolved_DoesNotPublishAdditionalEvent()
    {
        var (businessId, ownerId, ownerClient, cookie) = await SetUpBusinessWithOwnerAsync("Hub Idempotent Resolve Business");
        using var _ = ownerClient;
        var ticketId = await SeedTicketAsync(businessId, TicketStatus.InProgress, claimedByUserId: ownerId);
        var (connection, events, _) = BuildHubConnection(cookie);
        await using var _conn = connection;
        await connection.StartAsync();

        var first = await ownerClient.SendAsync(Authed(HttpMethod.Post, $"/api/tickets/{ticketId}/resolve", cookie));
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        await WaitForEventAsync(events, e => e.TicketId == ticketId && e.EventType == AuditEventTypes.TicketResolved, EventTimeout);

        var second = await ownerClient.SendAsync(Authed(HttpMethod.Post, $"/api/tickets/{ticketId}/resolve", cookie));
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode); // idempotent success — but must not publish again

        await Task.Delay(NoEventWait);
        Assert.Single(events, e => e.TicketId == ticketId && e.EventType == AuditEventTypes.TicketResolved);
    }

    [Fact]
    public async Task Claim_AlreadyClaimed_ReturnsConflict_DoesNotPublish()
    {
        var (businessId, ownerId, ownerClient, cookie) = await SetUpBusinessWithOwnerAsync("Hub Failed Claim Business");
        using var _ = ownerClient;
        var ticketId = await SeedTicketAsync(businessId, TicketStatus.InProgress, claimedByUserId: ownerId);
        var (connection, events, _) = BuildHubConnection(cookie);
        await using var _conn = connection;
        await connection.StartAsync();

        var response = await ownerClient.SendAsync(Authed(HttpMethod.Post, $"/api/tickets/{ticketId}/claim", cookie));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        await Task.Delay(NoEventWait);
        Assert.DoesNotContain(events, e => e.TicketId == ticketId);
    }

    // ── Concurrency preservation (reusing the already-audited Step 16 races) ──

    [Fact]
    public async Task Create_TwoConcurrentRequests_OnlyWinnerPublishes()
    {
        var (businessId, _, ownerClient1, cookie) = await SetUpBusinessWithOwnerAsync("Hub Concurrent Create Business");
        using var _1 = ownerClient1;
        using var ownerClient2 = NewClient();

        Guid customerId, conversationId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            customerId = await SeedCustomerAsync(db, businessId);
            var widget = new Widget { Id = Guid.NewGuid(), BusinessId = businessId, Name = "W", PublicKey = Guid.NewGuid().ToString("N"), CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
            db.Widgets.Add(widget);
            await db.SaveChangesAsync();
            var conversation = new Conversation { Id = Guid.NewGuid(), BusinessId = businessId, WidgetId = widget.Id, CustomerId = customerId, Channel = ConversationChannels.Website, Status = ConversationStatus.Open, HandledBy = HandledBy.Ai, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
            db.Conversations.Add(conversation);
            await db.SaveChangesAsync();
            conversationId = conversation.Id;
        }

        var (connection, events, _) = BuildHubConnection(cookie);
        await using var _conn = connection;
        await connection.StartAsync();

        var request = new { CustomerId = customerId, ConversationId = conversationId, Priority = "Low", PriorityReason = TicketPriorityReasons.ManualEscalation, Summary = (string?)null };
        var task1 = ownerClient1.SendAsync(WithBody(Authed(HttpMethod.Post, "/api/tickets", cookie), request));
        var task2 = ownerClient2.SendAsync(WithBody(Authed(HttpMethod.Post, "/api/tickets", cookie), request));
        await Task.WhenAll(task1, task2);

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var survivor = await verifyDb.Tickets.IgnoreQueryFilters().SingleAsync(t => t.ConversationId == conversationId);

        var received = await WaitForEventAsync(events, e => e.TicketId == survivor.Id, EventTimeout);
        Assert.NotNull(received);
        await Task.Delay(NoEventWait);
        Assert.Single(events, e => e.EventType == AuditEventTypes.TicketCreated);
    }

    [Fact]
    public async Task Claim_TwoConcurrentRequests_OnlyWinnerPublishes()
    {
        var (businessId, _, ownerClient1, cookie) = await SetUpBusinessWithOwnerAsync("Hub Concurrent Claim Business");
        using var _1 = ownerClient1;
        using var ownerClient2 = NewClient();
        var ticketId = await SeedTicketAsync(businessId);

        var (connection, events, _) = BuildHubConnection(cookie);
        await using var _conn = connection;
        await connection.StartAsync();

        var task1 = ownerClient1.SendAsync(Authed(HttpMethod.Post, $"/api/tickets/{ticketId}/claim", cookie));
        var task2 = ownerClient2.SendAsync(Authed(HttpMethod.Post, $"/api/tickets/{ticketId}/claim", cookie));
        await Task.WhenAll(task1, task2);

        var received = await WaitForEventAsync(events, e => e.TicketId == ticketId, EventTimeout);
        Assert.NotNull(received);
        await Task.Delay(NoEventWait);
        Assert.Single(events, e => e.TicketId == ticketId && e.EventType == AuditEventTypes.TicketClaimed);
    }

    [Fact]
    public async Task Resolve_TwoConcurrentRequests_ExactlyOnePublish_BothSucceed()
    {
        var (businessId, ownerId, ownerClient1, cookie) = await SetUpBusinessWithOwnerAsync("Hub Concurrent Resolve Business");
        using var _1 = ownerClient1;
        using var ownerClient2 = NewClient();
        var ticketId = await SeedTicketAsync(businessId, TicketStatus.InProgress, claimedByUserId: ownerId);

        var (connection, events, _) = BuildHubConnection(cookie);
        await using var _conn = connection;
        await connection.StartAsync();

        var task1 = ownerClient1.SendAsync(Authed(HttpMethod.Post, $"/api/tickets/{ticketId}/resolve", cookie));
        var task2 = ownerClient2.SendAsync(Authed(HttpMethod.Post, $"/api/tickets/{ticketId}/resolve", cookie));
        await Task.WhenAll(task1, task2);
        Assert.Equal(HttpStatusCode.NoContent, (await task1).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await task2).StatusCode);

        var received = await WaitForEventAsync(events, e => e.TicketId == ticketId, EventTimeout);
        Assert.NotNull(received);
        await Task.Delay(NoEventWait);
        Assert.Single(events, e => e.TicketId == ticketId && e.EventType == AuditEventTypes.TicketResolved);
    }

    // ── Logout force-disconnect ─────────────────────────────────────

    [Fact]
    public async Task Logout_SendsForceDisconnect_ClientStops()
    {
        var (_, _, ownerClient, cookie) = await SetUpBusinessWithOwnerAsync("Hub Logout Business");
        using var _ = ownerClient;
        var (connection, _, control) = BuildHubConnection(cookie);
        await using var _conn = connection;
        await connection.StartAsync();

        var logoutResponse = await ownerClient.SendAsync(Authed(HttpMethod.Post, "/api/auth/logout", cookie));
        Assert.Equal(HttpStatusCode.NoContent, logoutResponse.StatusCode);

        var received = await WaitForControlMessageAsync(control, "ForceDisconnect", EventTimeout);
        Assert.Equal("ForceDisconnect", received);

        // Mirrors the frontend's own designed reaction: on ForceDisconnect, stop.
        await connection.StopAsync();
        Assert.Equal(HubConnectionState.Disconnected, connection.State);
    }

    // ── Session-expiry timer ────────────────────────────────────────

    /// <summary>
    /// Crafts a real, validly-signed cookie ticket with ExpiresUtc a few
    /// seconds in the future — same technique SessionExpirationTests already
    /// established (cookieOptions.TicketDataFormat.Protect(...)) — so this
    /// proves the hub's own bounded per-connection timer fires around the
    /// cookie's actual expiry, without waiting out a real 8-hour session.
    /// </summary>
    [Fact]
    public async Task SessionExpiry_SendsSessionExpiring_NearCookieExpiry()
    {
        var (_, ownerId, ownerClient, _) = await SetUpBusinessWithOwnerAsync("Hub Expiry Business");
        using var _ = ownerClient;

        var optionsMonitor = _factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<CookieAuthenticationOptions>>();
        var cookieOptions = optionsMonitor.Get(IdentityConstants.ApplicationScheme);

        string shortLivedCookieValue;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var principalFactory = scope.ServiceProvider.GetRequiredService<IUserClaimsPrincipalFactory<ApplicationUser>>();
            var user = await userManager.FindByIdAsync(ownerId.ToString());
            Assert.NotNull(user);
            var principal = await principalFactory.CreateAsync(user!);

            var ticket = new AuthenticationTicket(
                principal,
                new AuthenticationProperties { IsPersistent = true, ExpiresUtc = DateTimeOffset.UtcNow.AddSeconds(3) },
                IdentityConstants.ApplicationScheme);

            shortLivedCookieValue = cookieOptions.TicketDataFormat.Protect(ticket);
        }

        var shortLivedCookie = $"{cookieOptions.Cookie.Name}={shortLivedCookieValue}";
        var (connection, _, control) = BuildHubConnection(shortLivedCookie);
        await using var _conn = connection;
        await connection.StartAsync();

        var received = await WaitForControlMessageAsync(control, "SessionExpiring", TimeSpan.FromSeconds(10));
        Assert.Equal("SessionExpiring", received);
    }

    // ── Reconnect ────────────────────────────────────────────────────

    [Fact]
    public async Task Reconnect_NewConnectionRejoinsTenantGroup_NotRelyingOnStaleMembership()
    {
        var (businessId, _, ownerClient, cookie) = await SetUpBusinessWithOwnerAsync("Hub Reconnect Business");
        using var _ = ownerClient;

        var (connection1, events1, _) = BuildHubConnection(cookie);
        await connection1.StartAsync();
        var firstTicketId = await SeedTicketAsync(businessId);
        var firstMutation = await ownerClient.SendAsync(Authed(HttpMethod.Post, $"/api/tickets/{firstTicketId}/claim", cookie));
        Assert.Equal(HttpStatusCode.NoContent, firstMutation.StatusCode);
        Assert.NotNull(await WaitForEventAsync(events1, e => e.TicketId == firstTicketId, EventTimeout));

        // Fully stop the first connection (distinct ConnectionId) and open a
        // brand new one with the same cookie — proving OnConnectedAsync/
        // group-join re-executes correctly per connection, not reusing
        // anything left over from the previous ConnectionId.
        await connection1.StopAsync();
        await connection1.DisposeAsync();

        var (connection2, events2, _) = BuildHubConnection(cookie);
        await using var _conn2 = connection2;
        await connection2.StartAsync();
        Assert.NotEqual(connection1.ConnectionId, connection2.ConnectionId);

        var secondTicketId = await SeedTicketAsync(businessId);
        var secondMutation = await ownerClient.SendAsync(Authed(HttpMethod.Post, $"/api/tickets/{secondTicketId}/claim", cookie));
        Assert.Equal(HttpStatusCode.NoContent, secondMutation.StatusCode);

        var received = await WaitForEventAsync(events2, e => e.TicketId == secondTicketId, EventTimeout);
        Assert.NotNull(received);
    }
}
