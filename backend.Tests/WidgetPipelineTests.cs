using System.Net;
using System.Net.Http.Json;
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
/// HTTP-level tests for widget init + lazy conversation creation +
/// messaging + the staff read API, run against the real app + real Neon
/// dev DB via NivoraApiFactory. Auth-scheme-specific tests (positive/
/// negative WidgetBearer vs AnyStaff, tenant-accessor proof) live in
/// WidgetAuthenticationTests.cs instead. Concurrency and rate-limiting/
/// tenant-isolation tests live in WidgetConcurrencyAndIsolationTests.cs.
/// </summary>
public class WidgetPipelineTests : IAsyncLifetime
{
    private readonly NivoraApiFactory _factory = new();
    private readonly List<Guid> _businessIdsToClean = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await TestBusinessCleanup.DeleteThenDisposeAsync(_businessIdsToClean, _factory);
    }

    internal HttpClient NewClient() => _factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });

    internal async Task<(Business Business, Widget Widget)> CreateBusinessAndWidgetAsync(
        bool widgetActive = true, List<string>? allowedDomains = null)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var business = new Business
        {
            Id = Guid.NewGuid(),
            Name = $"Widget Pipeline Test {Guid.NewGuid():N}",
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
            AllowedDomains = allowedDomains ?? [],
            IsActive = widgetActive,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Businesses.Add(business);
        db.Widgets.Add(widget);
        await db.SaveChangesAsync();
        _businessIdsToClean.Add(business.Id);

        return (business, widget);
    }

    internal static HttpRequestMessage WithJsonBody<T>(HttpRequestMessage request, T body)
    {
        request.Content = JsonContent.Create(body);
        return request;
    }

    internal static HttpRequestMessage Bearer(HttpMethod method, string url, string token)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("Authorization", $"Bearer {token}");
        return request;
    }

    /// <summary>
    /// Test-only: Postgres's timestamptz column stores microsecond
    /// precision, one fractional digit less than .NET's 100-nanosecond
    /// tick precision. A value read straight off an HTTP response (never
    /// round-tripped through the DB) still carries full tick precision; the
    /// same logical instant read back via a fresh DbContext/query comes
    /// back truncated. Same helper as NotificationApiTests.cs's identical
    /// fix for the identical root cause.
    /// </summary>
    internal static DateTimeOffset TruncateToMicroseconds(DateTimeOffset value) =>
        new(value.Ticks - (value.Ticks % 10), value.Offset);

    // ── Widget initialization ───────────────────────────────────────

    [Fact]
    public async Task Init_ActiveWidget_Succeeds()
    {
        var (_, widget) = await CreateBusinessAndWidgetAsync();
        using var client = NewClient();

        var response = await client.PostAsJsonAsync("/api/widget/init", new WidgetInitRequest(widget.PublicKey, null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<WidgetInitResponse>();
        Assert.NotNull(body);
        Assert.False(string.IsNullOrWhiteSpace(body!.VisitorSessionId));
        Assert.False(string.IsNullOrWhiteSpace(body.Token));
    }

    [Fact]
    public async Task Init_InactiveWidget_Fails()
    {
        var (_, widget) = await CreateBusinessAndWidgetAsync(widgetActive: false);
        using var client = NewClient();

        var response = await client.PostAsJsonAsync("/api/widget/init", new WidgetInitRequest(widget.PublicKey, null));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Init_UnknownPublicKey_Fails()
    {
        using var client = NewClient();

        var response = await client.PostAsJsonAsync("/api/widget/init", new WidgetInitRequest(Guid.NewGuid().ToString("N"), null));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Init_ExistingOpenConversationForVisitorSession_ReusesSameVisitorSessionId()
    {
        var (business, widget) = await CreateBusinessAndWidgetAsync();
        var visitorSessionId = Guid.NewGuid().ToString("N");

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Conversations.Add(new Conversation
            {
                Id = Guid.NewGuid(),
                BusinessId = business.Id,
                WidgetId = widget.Id,
                VisitorSessionId = visitorSessionId,
                Channel = "Website",
                Status = ConversationStatus.Open,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        using var client = NewClient();
        var response = await client.PostAsJsonAsync("/api/widget/init", new WidgetInitRequest(widget.PublicKey, visitorSessionId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<WidgetInitResponse>();
        Assert.Equal(visitorSessionId, body!.VisitorSessionId);
    }

    [Fact]
    public async Task Init_UnknownVisitorSessionId_GeneratesNewOne()
    {
        var (_, widget) = await CreateBusinessAndWidgetAsync();
        var suppliedVisitorSessionId = Guid.NewGuid().ToString("N"); // no matching conversation exists for this
        using var client = NewClient();

        var response = await client.PostAsJsonAsync("/api/widget/init", new WidgetInitRequest(widget.PublicKey, suppliedVisitorSessionId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<WidgetInitResponse>();
        Assert.NotEqual(suppliedVisitorSessionId, body!.VisitorSessionId);
    }

    [Fact]
    public async Task Init_DoesNotCreateConversation()
    {
        var (business, widget) = await CreateBusinessAndWidgetAsync();
        using var client = NewClient();

        var response = await client.PostAsJsonAsync("/api/widget/init", new WidgetInitRequest(widget.PublicKey, null));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var conversationCount = await db.Conversations.IgnoreQueryFilters().CountAsync(c => c.BusinessId == business.Id);
        Assert.Equal(0, conversationCount);
    }

    // ── Messaging ────────────────────────────────────────────────────

    [Fact]
    public async Task PostMessage_FirstMessage_LazilyCreatesConversation()
    {
        var (business, widget) = await CreateBusinessAndWidgetAsync();
        using var client = NewClient();
        var initResponse = await client.PostAsJsonAsync("/api/widget/init", new WidgetInitRequest(widget.PublicKey, null));
        var initBody = await initResponse.Content.ReadFromJsonAsync<WidgetInitResponse>();

        var response = await client.SendAsync(WithJsonBody(
            Bearer(HttpMethod.Post, "/api/widget/messages", initBody!.Token), new PostWidgetMessageRequest("Hi there")));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var conversation = await db.Conversations.IgnoreQueryFilters().SingleAsync(c => c.BusinessId == business.Id);
        Assert.Equal(widget.Id, conversation.WidgetId);
        Assert.Equal(initBody.VisitorSessionId, conversation.VisitorSessionId);
        Assert.Equal(ConversationChannels.Website, conversation.Channel);
        Assert.Equal(HandledBy.Ai, conversation.HandledBy);
        Assert.Equal(ConversationStatus.Open, conversation.Status);
    }

    [Fact]
    public async Task PostMessage_SecondMessage_ReusesExistingOpenConversation_LastMessageAtUpdated()
    {
        var (business, widget) = await CreateBusinessAndWidgetAsync();
        using var client = NewClient();
        var initResponse = await client.PostAsJsonAsync("/api/widget/init", new WidgetInitRequest(widget.PublicKey, null));
        var initBody = await initResponse.Content.ReadFromJsonAsync<WidgetInitResponse>();

        var first = await client.SendAsync(WithJsonBody(
            Bearer(HttpMethod.Post, "/api/widget/messages", initBody!.Token), new PostWidgetMessageRequest("First message")));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await client.SendAsync(WithJsonBody(
            Bearer(HttpMethod.Post, "/api/widget/messages", initBody.Token), new PostWidgetMessageRequest("Second message")));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var conversationCount = await db.Conversations.IgnoreQueryFilters().CountAsync(c => c.BusinessId == business.Id);
        Assert.Equal(1, conversationCount); // reused, not a second Conversation

        var conversation = await db.Conversations.IgnoreQueryFilters().SingleAsync(c => c.BusinessId == business.Id);
        var customerMessageCount = await db.Messages.IgnoreQueryFilters()
            .CountAsync(m => m.ConversationId == conversation.Id && m.Role == MessageRole.Customer);
        Assert.Equal(2, customerMessageCount);
        // LastMessageAt-equals-second-customer-message-timestamp is no
        // longer assertable at this (full HTTP pipeline) layer now that
        // Step 8's ClaudeReplyService deterministically writes again,
        // synchronously, before this response returns — see
        // WidgetConversationServiceTests for the original Steps 6-7
        // guarantee, tested directly against WidgetConversationService
        // where that write is still the last one and observable.
    }

    [Fact]
    public async Task PostMessage_AssociatedWithCorrectTenantWidgetVisitor()
    {
        var (business, widget) = await CreateBusinessAndWidgetAsync();
        using var client = NewClient();
        var initResponse = await client.PostAsJsonAsync("/api/widget/init", new WidgetInitRequest(widget.PublicKey, null));
        var initBody = await initResponse.Content.ReadFromJsonAsync<WidgetInitResponse>();

        var response = await client.SendAsync(WithJsonBody(
            Bearer(HttpMethod.Post, "/api/widget/messages", initBody!.Token), new PostWidgetMessageRequest("Tenant check")));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var messageResponse = await response.Content.ReadFromJsonAsync<WidgetMessageResponse>();

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var message = await db.Messages.IgnoreQueryFilters().SingleAsync(m => m.Id == messageResponse!.Id);
        Assert.Equal(business.Id, message.BusinessId);
        Assert.Equal(MessageRole.Customer, message.Role);

        var conversation = await db.Conversations.IgnoreQueryFilters().SingleAsync(c => c.Id == message.ConversationId);
        Assert.Equal(widget.Id, conversation.WidgetId);
        Assert.Equal(initBody.VisitorSessionId, conversation.VisitorSessionId);
    }

    [Fact]
    public async Task PostMessage_WhitespaceOnlyContent_Rejected()
    {
        var (_, widget) = await CreateBusinessAndWidgetAsync();
        using var client = NewClient();
        var initResponse = await client.PostAsJsonAsync("/api/widget/init", new WidgetInitRequest(widget.PublicKey, null));
        var initBody = await initResponse.Content.ReadFromJsonAsync<WidgetInitResponse>();

        var response = await client.SendAsync(WithJsonBody(
            Bearer(HttpMethod.Post, "/api/widget/messages", initBody!.Token), new PostWidgetMessageRequest("   ")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── Staff read API ──────────────────────────────────────────────

    [Fact]
    public async Task StaffConversationList_And_MessageDetail_ReturnPersistedWidgetData()
    {
        var (business, widget) = await CreateBusinessAndWidgetAsync();
        using var widgetClient = NewClient();
        var initResponse = await widgetClient.PostAsJsonAsync("/api/widget/init", new WidgetInitRequest(widget.PublicKey, null));
        var initBody = await initResponse.Content.ReadFromJsonAsync<WidgetInitResponse>();
        await widgetClient.SendAsync(WithJsonBody(
            Bearer(HttpMethod.Post, "/api/widget/messages", initBody!.Token), new PostWidgetMessageRequest("Read via staff API")));

        // Seed a staff user directly into this business (no register flow
        // here since CreateBusinessAndWidgetAsync seeds the Business
        // directly, not via /api/auth/register) and log in for real.
        string email;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            email = $"staff-{Guid.NewGuid():N}@example.com";
            var user = new ApplicationUser
            {
                Id = Guid.NewGuid(),
                BusinessId = business.Id,
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            var createResult = await userManager.CreateAsync(user, "Passw0rd!!");
            Assert.True(createResult.Succeeded);
            var roleResult = await userManager.AddToRoleAsync(user, RoleNames.Owner);
            Assert.True(roleResult.Succeeded);
        }

        using var staffClient = NewClient();
        var loginResponse = await staffClient.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, "Passw0rd!!"));
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        var cookie = loginResponse.Headers.TryGetValues("Set-Cookie", out var values) ? values.First().Split(';')[0] : null;
        Assert.NotNull(cookie);

        var listRequest = new HttpRequestMessage(HttpMethod.Get, "/api/conversations");
        listRequest.Headers.Add("Cookie", cookie!);
        var listResponse = await staffClient.SendAsync(listRequest);
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var payload = await listResponse.Content.ReadFromJsonAsync<ConversationListResponse>();
        Assert.NotNull(payload);
        Assert.Single(payload!.Conversations);
        var conversationId = payload.Conversations[0].Id;
        var messagesRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/conversations/{conversationId}/messages");
        messagesRequest.Headers.Add("Cookie", cookie!);
        var messagesResponse = await staffClient.SendAsync(messagesRequest);
        Assert.Equal(HttpStatusCode.OK, messagesResponse.StatusCode);
        var detail = await messagesResponse.Content.ReadFromJsonAsync<ConversationDetailResponse>();
        Assert.NotNull(detail);
        var messages = detail!.Messages;
        Assert.Single(messages.Where(m => m.Role == MessageRole.Customer.ToString()));
        Assert.Equal("Read via staff API", messages[0].Content);
        Assert.Equal("Customer", messages[0].Role);
    }
}
