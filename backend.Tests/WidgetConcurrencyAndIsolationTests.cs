using System.Net;
using System.Net.Http.Json;
using backend.Auth;
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
/// Concurrency (the required advisory-lock race test), tenant isolation,
/// and rate-limiting tests — all run against the real app + real Neon dev
/// DB via NivoraApiFactory. Shares no code with WidgetPipelineTests.cs
/// (small, self-contained helpers duplicated per-file, matching this test
/// suite's existing convention — see CrossTenantIsolationTests.cs/
/// NotificationApiTests.cs, which do the same).
/// </summary>
public class WidgetConcurrencyAndIsolationTests : IAsyncLifetime
{
    private readonly NivoraApiFactory _factory = new();
    private readonly List<Guid> _businessIdsToClean = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await TestBusinessCleanup.DeleteThenDisposeAsync(_businessIdsToClean, _factory);
    }

    private HttpClient NewClient() => _factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });

    private async Task<(Business Business, Widget Widget)> CreateBusinessAndWidgetAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var business = new Business
        {
            Id = Guid.NewGuid(),
            Name = $"Widget Concurrency/Isolation Test {Guid.NewGuid():N}",
            TimeZone = "Europe/Copenhagen",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        var widget = new Widget
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id,
            Name = "Test Widget",
            PublicKey = Guid.NewGuid().ToString("N"),
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Businesses.Add(business);
        db.Widgets.Add(widget);
        await db.SaveChangesAsync();
        _businessIdsToClean.Add(business.Id);

        return (business, widget);
    }

    private static HttpRequestMessage WithJsonBody<T>(HttpRequestMessage request, T body)
    {
        request.Content = JsonContent.Create(body);
        return request;
    }

    private static HttpRequestMessage Bearer(HttpMethod method, string url, string token)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("Authorization", $"Bearer {token}");
        return request;
    }

    private async Task<WidgetInitResponse> InitAsync(HttpClient client, string publicKey, string? visitorSessionId = null)
    {
        var response = await client.PostAsJsonAsync("/api/widget/init", new WidgetInitRequest(publicKey, visitorSessionId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<WidgetInitResponse>();
        Assert.NotNull(body);
        return body!;
    }

    // ── Concurrency (Section 9 — required) ──────────────────────────

    [Fact]
    public async Task TwoConcurrentFirstMessages_SameVisitor_CreateExactlyOneConversation_ContainingBothMessages()
    {
        var (business, widget) = await CreateBusinessAndWidgetAsync();
        using var client = NewClient();
        var initBody = await InitAsync(client, widget.PublicKey);

        // Two genuinely concurrent first-message requests for the SAME
        // (BusinessId, WidgetId, VisitorSessionId) tuple — this is exactly
        // the race PostgreSQL's pg_advisory_xact_lock must serialize.
        var task1 = client.SendAsync(WithJsonBody(
            Bearer(HttpMethod.Post, "/api/widget/messages", initBody.Token), new PostWidgetMessageRequest("Concurrent message 1")));
        var task2 = client.SendAsync(WithJsonBody(
            Bearer(HttpMethod.Post, "/api/widget/messages", initBody.Token), new PostWidgetMessageRequest("Concurrent message 2")));

        var responses = await Task.WhenAll(task1, task2);
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var conversations = await db.Conversations.IgnoreQueryFilters()
            .Where(c => c.BusinessId == business.Id)
            .ToListAsync();
        Assert.Single(conversations); // exactly one Conversation — no race-created duplicate

        var conversation = conversations[0];
        Assert.Equal(ConversationStatus.Open, conversation.Status);

        var messages = await db.Messages.IgnoreQueryFilters()
            .Where(m => m.ConversationId == conversation.Id)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync();
        Assert.Equal(2, messages.Count(m => m.Role == MessageRole.Customer));
        Assert.Contains(messages, m => m.Content == "Concurrent message 1");
        Assert.Contains(messages, m => m.Content == "Concurrent message 2");

        Assert.Equal(messages.Max(m => m.CreatedAt), conversation.LastMessageAt);
    }

    // ── Tenant isolation ─────────────────────────────────────────────

    [Fact]
    public async Task StaffCannotReadAnotherTenantsConversation()
    {
        var (businessA, widgetA) = await CreateBusinessAndWidgetAsync();
        var (businessB, widgetB) = await CreateBusinessAndWidgetAsync();

        using var widgetClient = NewClient();
        var initA = await InitAsync(widgetClient, widgetA.PublicKey);
        await widgetClient.SendAsync(WithJsonBody(
            Bearer(HttpMethod.Post, "/api/widget/messages", initA.Token), new PostWidgetMessageRequest("Business A's message")));

        Guid conversationIdA;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            conversationIdA = (await db.Conversations.IgnoreQueryFilters().SingleAsync(c => c.BusinessId == businessA.Id)).Id;
        }

        // Staff belonging to Business B.
        string email;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            email = $"staffB-{Guid.NewGuid():N}@example.com";
            var user = new ApplicationUser
            {
                Id = Guid.NewGuid(),
                BusinessId = businessB.Id,
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            await userManager.CreateAsync(user, "Passw0rd!!");
            await userManager.AddToRoleAsync(user, RoleNames.Owner);
        }

        using var staffClient = NewClient();
        var loginResponse = await staffClient.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, "Passw0rd!!"));
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        var cookie = loginResponse.Headers.TryGetValues("Set-Cookie", out var values) ? values.First().Split(';')[0] : null;

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/conversations/{conversationIdA}/messages");
        request.Headers.Add("Cookie", cookie!);
        var response = await staffClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode); // Business B staff, Business A's conversation
    }

    [Fact]
    public async Task WidgetClaims_CannotCrossTenantBoundaries()
    {
        var (businessA, widgetA) = await CreateBusinessAndWidgetAsync();
        var (businessB, widgetB) = await CreateBusinessAndWidgetAsync();

        using var client = NewClient();
        var initA = await InitAsync(client, widgetA.PublicKey);
        var initB = await InitAsync(client, widgetB.PublicKey);

        await client.SendAsync(WithJsonBody(
            Bearer(HttpMethod.Post, "/api/widget/messages", initA.Token), new PostWidgetMessageRequest("From widget A")));
        await client.SendAsync(WithJsonBody(
            Bearer(HttpMethod.Post, "/api/widget/messages", initB.Token), new PostWidgetMessageRequest("From widget B")));

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Each business's conversation carries only its own widget's
        // message — the widget JWT's claims (derived server-side at init,
        // never client-supplied) never let one business's traffic leak
        // into the other's tenant scope.
        var conversationA = await db.Conversations.IgnoreQueryFilters().SingleAsync(c => c.BusinessId == businessA.Id);
        var messagesA = await db.Messages.IgnoreQueryFilters().Where(m => m.ConversationId == conversationA.Id).ToListAsync();
        Assert.Single(messagesA.Where(m => m.Role == MessageRole.Customer));
        var customerMessageA = messagesA.Single(m => m.Role == MessageRole.Customer);
        Assert.Equal("From widget A", customerMessageA.Content);
        Assert.Equal(businessA.Id, customerMessageA.BusinessId);

        var conversationB = await db.Conversations.IgnoreQueryFilters().SingleAsync(c => c.BusinessId == businessB.Id);
        var messagesB = await db.Messages.IgnoreQueryFilters().Where(m => m.ConversationId == conversationB.Id).ToListAsync();
        Assert.Single(messagesB.Where(m => m.Role == MessageRole.Customer));
        var customerMessageB = messagesB.Single(m => m.Role == MessageRole.Customer);
        Assert.Equal("From widget B", customerMessageB.Content);
        Assert.Equal(businessB.Id, customerMessageB.BusinessId);
    }

    // ── Rate limiting ────────────────────────────────────────────────

    [Fact]
    public async Task WidgetInitRateLimiter_Returns429AfterLimitExceeded()
    {
        var (_, widget) = await CreateBusinessAndWidgetAsync();
        using var client = NewClient();

        HttpResponseMessage? last = null;
        for (var i = 0; i < 31; i++) // placeholder limit is 30/minute
        {
            last = await client.PostAsJsonAsync("/api/widget/init", new WidgetInitRequest(widget.PublicKey, null));
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, last!.StatusCode);
    }

    [Fact]
    public async Task WidgetMessageRateLimiter_PartitionsByVisitorSession_NotSharedAcrossVisitors()
    {
        var (_, widget) = await CreateBusinessAndWidgetAsync();
        using var client = NewClient();

        var initVisitorA = await InitAsync(client, widget.PublicKey);
        var initVisitorB = await InitAsync(client, widget.PublicKey);

        HttpResponseMessage? lastForA = null;
        for (var i = 0; i < 21; i++) // placeholder limit is 20/minute
        {
            lastForA = await client.SendAsync(WithJsonBody(
                Bearer(HttpMethod.Post, "/api/widget/messages", initVisitorA.Token), new PostWidgetMessageRequest($"msg {i}")));
        }
        Assert.Equal(HttpStatusCode.TooManyRequests, lastForA!.StatusCode);

        // Visitor B, a completely different VisitorSessionId, must be
        // unaffected by A having exhausted their own limit.
        var responseForB = await client.SendAsync(WithJsonBody(
            Bearer(HttpMethod.Post, "/api/widget/messages", initVisitorB.Token), new PostWidgetMessageRequest("still fine")));
        Assert.Equal(HttpStatusCode.OK, responseForB.StatusCode);
    }
}
