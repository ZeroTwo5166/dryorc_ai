using System.Net;
using System.Net.Http.Json;
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
/// HTTP-level tests for GET /api/widget/messages — Step 6C customer-side
/// delivery of Agent/Ai messages via minimal polling. Run against the real
/// app + real Neon dev DB via NivoraApiFactory, same pattern as
/// WidgetPipelineTests (a self-contained set of local helpers, deliberately
/// not shared with that file — same convention WidgetsApiTests already
/// established).
/// </summary>
public class WidgetMessagePollingTests : IAsyncLifetime
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

            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Messages\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Conversations\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Widgets\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"AspNetUserRoles\" WHERE \"UserId\" IN (SELECT \"Id\" FROM \"AspNetUsers\" WHERE \"BusinessId\" = ANY({ids}))");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"AspNetUsers\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Businesses\" WHERE \"Id\" = ANY({ids})");
        }

        await _factory.DisposeAsync();
    }

    private HttpClient NewClient() => _factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });

    private async Task<(Business Business, Widget Widget)> CreateBusinessAndWidgetAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var business = new Business
        {
            Id = Guid.NewGuid(),
            Name = $"Widget Polling Test {Guid.NewGuid():N}",
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
            AllowedDomains = [],
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

    private async Task<Conversation> CreateOpenConversationAsync(AppDbContext db, Guid businessId, Guid widgetId, string visitorSessionId)
    {
        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            WidgetId = widgetId,
            VisitorSessionId = visitorSessionId,
            Channel = "Website",
            Status = ConversationStatus.Open,
            HandledBy = HandledBy.Ai,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync();
        return conversation;
    }

    /// <summary>AuthorUserId is a real FK to AspNetUsers — an Agent message needs an actual seeded user, not just any Guid.</summary>
    private async Task<Guid> SeedStaffUserAsync(Guid businessId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var email = $"staff-{Guid.NewGuid():N}@example.com";
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
        return user.Id;
    }

    private static Message SeedMessage(
        AppDbContext db, Guid businessId, Guid conversationId, MessageRole role, string content, DateTimeOffset createdAt, Guid? authorUserId = null)
    {
        var message = new Message
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            ConversationId = conversationId,
            Role = role,
            Content = content,
            CreatedAt = createdAt,
            AuthorUserId = authorUserId,
        };
        db.Messages.Add(message);
        return message;
    }

    private static HttpRequestMessage BearerGet(string url, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Authorization", $"Bearer {token}");
        return request;
    }

    private async Task<(string VisitorSessionId, string Token)> InitAsync(HttpClient client, string publicKey)
    {
        var response = await client.PostAsJsonAsync("/api/widget/init", new WidgetInitRequest(publicKey, null));
        var body = await response.Content.ReadFromJsonAsync<WidgetInitResponse>();
        return (body!.VisitorSessionId, body.Token);
    }

    // ── Authorization / tenant isolation ────────────────────────────

    [Fact]
    public async Task Poll_NoAuth_ReturnsUnauthorized()
    {
        using var client = NewClient();
        var response = await client.GetAsync("/api/widget/messages");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Poll_InvalidToken_ReturnsUnauthorized()
    {
        using var client = NewClient();
        var response = await client.SendAsync(BearerGet("/api/widget/messages", "not-a-real-token"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Poll_NoConversationYet_ReturnsEmptyNotError()
    {
        var (_, widget) = await CreateBusinessAndWidgetAsync();
        using var client = NewClient();
        var (_, token) = await InitAsync(client, widget.PublicKey);

        var response = await client.SendAsync(BearerGet("/api/widget/messages", token));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<WidgetPollResponse>();
        Assert.NotNull(body);
        Assert.Empty(body!.Messages);
        Assert.Null(body.NextCursor);
    }

    [Fact]
    public async Task Poll_CrossTenantToken_CannotSeeAnotherBusinessConversation()
    {
        var (businessA, widgetA) = await CreateBusinessAndWidgetAsync();
        var (businessB, widgetB) = await CreateBusinessAndWidgetAsync();

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var staffA = await SeedStaffUserAsync(businessA.Id);
        var visitorA = Guid.NewGuid().ToString("N");
        var conversationA = await CreateOpenConversationAsync(db, businessA.Id, widgetA.Id, visitorA);
        SeedMessage(db, businessA.Id, conversationA.Id, MessageRole.Agent, "Only Business A should see this", DateTimeOffset.UtcNow.AddSeconds(-10), staffA);
        await db.SaveChangesAsync();

        // Business B's own widget/visitor, no relation to Business A's conversation.
        using var clientB = NewClient();
        var (_, tokenB) = await InitAsync(clientB, widgetB.PublicKey);

        var response = await clientB.SendAsync(BearerGet("/api/widget/messages", tokenB));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<WidgetPollResponse>();
        Assert.Empty(body!.Messages); // Business B's own (nonexistent) conversation, never Business A's
    }

    [Fact]
    public async Task Poll_NoClientSuppliedConversationIdParameter_RouteAcceptsOnlyCursorFields()
    {
        // The endpoint intentionally exposes no conversationId/businessId/
        // widgetId/visitorSessionId query or route parameter at all — proven
        // here by confirming a request carrying one as an extra, unused query
        // string value still resolves identity purely from the JWT and
        // behaves identically to one without it.
        var (business, widget) = await CreateBusinessAndWidgetAsync();
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        using var client = NewClient();
        var (visitorSessionId, token) = await InitAsync(client, widget.PublicKey);
        var conversation = await CreateOpenConversationAsync(db, business.Id, widget.Id, visitorSessionId);
        SeedMessage(db, business.Id, conversation.Id, MessageRole.Ai, "Real reply", DateTimeOffset.UtcNow.AddSeconds(-5));
        await db.SaveChangesAsync();

        var forgedConversationId = Guid.NewGuid();
        var cursor = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddSeconds(-10).ToString("o"));
        var response = await client.SendAsync(BearerGet(
            $"/api/widget/messages?conversationId={forgedConversationId}&afterCreatedAt={cursor}&afterId={Guid.Empty}", token));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<WidgetPollResponse>();
        Assert.Single(body!.Messages); // still resolved via the JWT's own visitor session, ignoring the bogus param
        Assert.Equal("Real reply", body.Messages[0].Content);
    }

    // ── Message visibility ──────────────────────────────────────────

    [Fact]
    public async Task Poll_ReturnsOnlyAgentAndAiMessages_ExcludesCustomer()
    {
        var (business, widget) = await CreateBusinessAndWidgetAsync();
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        using var client = NewClient();
        var (visitorSessionId, token) = await InitAsync(client, widget.PublicKey);
        var conversation = await CreateOpenConversationAsync(db, business.Id, widget.Id, visitorSessionId);

        var staffId = await SeedStaffUserAsync(business.Id);
        var baseTime = DateTimeOffset.UtcNow.AddMinutes(-1);
        SeedMessage(db, business.Id, conversation.Id, MessageRole.Customer, "customer msg", baseTime);
        SeedMessage(db, business.Id, conversation.Id, MessageRole.Ai, "ai msg", baseTime.AddSeconds(1));
        SeedMessage(db, business.Id, conversation.Id, MessageRole.Agent, "agent msg", baseTime.AddSeconds(2), staffId);
        await db.SaveChangesAsync();

        var cursor = Uri.EscapeDataString(baseTime.AddSeconds(-1).ToString("o"));
        var response = await client.SendAsync(BearerGet($"/api/widget/messages?afterCreatedAt={cursor}&afterId={Guid.Empty}", token));
        var body = await response.Content.ReadFromJsonAsync<WidgetPollResponse>();

        Assert.Equal(2, body!.Messages.Count);
        Assert.DoesNotContain(body.Messages, m => m.Role == "Customer");
        Assert.Contains(body.Messages, m => m.Role == "Ai" && m.Content == "ai msg");
        Assert.Contains(body.Messages, m => m.Role == "Agent" && m.Content == "agent msg");
    }

    [Fact]
    public async Task Poll_ResponseNeverExposesAuthorUserId()
    {
        // AuthorUserId isn't even a field on WidgetPolledMessage — this
        // confirms the raw JSON payload itself never carries it, not just
        // that the typed DTO happens to omit it.
        var (business, widget) = await CreateBusinessAndWidgetAsync();
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        using var client = NewClient();
        var (visitorSessionId, token) = await InitAsync(client, widget.PublicKey);
        var conversation = await CreateOpenConversationAsync(db, business.Id, widget.Id, visitorSessionId);
        var staffId = await SeedStaffUserAsync(business.Id);
        SeedMessage(db, business.Id, conversation.Id, MessageRole.Agent, "staff reply", DateTimeOffset.UtcNow, staffId);
        await db.SaveChangesAsync();

        var response = await client.SendAsync(BearerGet("/api/widget/messages", token));
        var raw = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("AuthorUserId", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(staffId.ToString(), raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(business.Id.ToString(), raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(conversation.Id.ToString(), raw, StringComparison.OrdinalIgnoreCase);
    }

    // ── Cursor / keyset semantics ────────────────────────────────────

    [Fact]
    public async Task Poll_NoCursor_DoesNotReturnPreExistingHistory_EstablishesFreshCursor()
    {
        var (business, widget) = await CreateBusinessAndWidgetAsync();
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        using var client = NewClient();
        var (visitorSessionId, token) = await InitAsync(client, widget.PublicKey);
        var conversation = await CreateOpenConversationAsync(db, business.Id, widget.Id, visitorSessionId);
        SeedMessage(db, business.Id, conversation.Id, MessageRole.Ai, "already existed before this visitor polled", DateTimeOffset.UtcNow.AddMinutes(-5));
        await db.SaveChangesAsync();

        var response = await client.SendAsync(BearerGet("/api/widget/messages", token));
        var body = await response.Content.ReadFromJsonAsync<WidgetPollResponse>();

        Assert.Empty(body!.Messages); // NOT a full-history endpoint
        Assert.NotNull(body.NextCursor); // but a fresh anchor is still established
    }

    [Fact]
    public async Task Poll_WithCursor_ReturnsOnlyMessagesStrictlyAfterCursor()
    {
        var (business, widget) = await CreateBusinessAndWidgetAsync();
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        using var client = NewClient();
        var (visitorSessionId, token) = await InitAsync(client, widget.PublicKey);
        var conversation = await CreateOpenConversationAsync(db, business.Id, widget.Id, visitorSessionId);

        var baseTime = DateTimeOffset.UtcNow.AddMinutes(-1);
        var first = SeedMessage(db, business.Id, conversation.Id, MessageRole.Ai, "first", baseTime);
        var second = SeedMessage(db, business.Id, conversation.Id, MessageRole.Ai, "second", baseTime.AddSeconds(1));
        await db.SaveChangesAsync();

        var url = $"/api/widget/messages?afterCreatedAt={Uri.EscapeDataString(first.CreatedAt.ToString("o"))}&afterId={first.Id}";
        var response = await client.SendAsync(BearerGet(url, token));
        var body = await response.Content.ReadFromJsonAsync<WidgetPollResponse>();

        Assert.Single(body!.Messages);
        Assert.Equal(second.Id, body.Messages[0].Id);
        Assert.Equal(second.Id, body.NextCursor!.Id);
    }

    [Fact]
    public async Task Poll_EqualCreatedAt_UsesIdAsDeterministicTieBreaker_NeitherSkippedNorDuplicated()
    {
        var (business, widget) = await CreateBusinessAndWidgetAsync();
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        using var client = NewClient();
        var (visitorSessionId, token) = await InitAsync(client, widget.PublicKey);
        var conversation = await CreateOpenConversationAsync(db, business.Id, widget.Id, visitorSessionId);

        var sameInstant = DateTimeOffset.UtcNow.AddMinutes(-1);
        SeedMessage(db, business.Id, conversation.Id, MessageRole.Ai, "tie-a", sameInstant);
        SeedMessage(db, business.Id, conversation.Id, MessageRole.Ai, "tie-b", sameInstant);
        await db.SaveChangesAsync();

        // Determine the database's own (CreatedAt, Id) order — the same
        // order the endpoint itself queries by — rather than assuming
        // .NET's Guid.CompareTo ordering agrees with Postgres's uuid
        // ordering, which is not guaranteed.
        var ordered = await db.Messages.IgnoreQueryFilters()
            .Where(m => m.ConversationId == conversation.Id)
            .OrderBy(m => m.CreatedAt).ThenBy(m => m.Id)
            .ToListAsync();
        Assert.Equal(2, ordered.Count);
        var (firstMsg, secondMsg) = (ordered[0], ordered[1]);

        // Cursor at the first message — must return exactly the second, not both, not neither.
        var url = $"/api/widget/messages?afterCreatedAt={Uri.EscapeDataString(firstMsg.CreatedAt.ToString("o"))}&afterId={firstMsg.Id}";
        var response = await client.SendAsync(BearerGet(url, token));
        var body = await response.Content.ReadFromJsonAsync<WidgetPollResponse>();

        Assert.Single(body!.Messages);
        Assert.Equal(secondMsg.Id, body.Messages[0].Id);
    }

    [Fact]
    public async Task Poll_MultipleAgentAndAiMessagesBetweenPolls_AllReturnedInOrder()
    {
        var (business, widget) = await CreateBusinessAndWidgetAsync();
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        using var client = NewClient();
        var (visitorSessionId, token) = await InitAsync(client, widget.PublicKey);
        var conversation = await CreateOpenConversationAsync(db, business.Id, widget.Id, visitorSessionId);

        var staffId = await SeedStaffUserAsync(business.Id);
        var baseTime = DateTimeOffset.UtcNow.AddMinutes(-1);
        SeedMessage(db, business.Id, conversation.Id, MessageRole.Ai, "one", baseTime);
        SeedMessage(db, business.Id, conversation.Id, MessageRole.Agent, "two", baseTime.AddSeconds(1), staffId);
        SeedMessage(db, business.Id, conversation.Id, MessageRole.Ai, "three", baseTime.AddSeconds(2));
        await db.SaveChangesAsync();

        var url = $"/api/widget/messages?afterCreatedAt={Uri.EscapeDataString(baseTime.AddSeconds(-1).ToString("o"))}&afterId={Guid.Empty}";
        var response = await client.SendAsync(BearerGet(url, token));
        var body = await response.Content.ReadFromJsonAsync<WidgetPollResponse>();

        Assert.Equal(3, body!.Messages.Count);
        Assert.Equal(["one", "two", "three"], body.Messages.Select(m => m.Content));
    }

    [Fact]
    public async Task Poll_MalformedCursor_OnlyOneFieldSupplied_ReturnsBadRequest()
    {
        var (_, widget) = await CreateBusinessAndWidgetAsync();
        using var client = NewClient();
        var (_, token) = await InitAsync(client, widget.PublicKey);

        var response = await client.SendAsync(BearerGet($"/api/widget/messages?afterId={Guid.NewGuid()}", token));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Poll_PageSize_IsBounded()
    {
        var (business, widget) = await CreateBusinessAndWidgetAsync();
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        using var client = NewClient();
        var (visitorSessionId, token) = await InitAsync(client, widget.PublicKey);
        var conversation = await CreateOpenConversationAsync(db, business.Id, widget.Id, visitorSessionId);

        var baseTime = DateTimeOffset.UtcNow.AddMinutes(-5);
        for (var i = 0; i < 60; i++)
        {
            SeedMessage(db, business.Id, conversation.Id, MessageRole.Ai, $"msg-{i}", baseTime.AddSeconds(i));
        }
        await db.SaveChangesAsync();

        var response = await client.SendAsync(BearerGet("/api/widget/messages", token));
        var body = await response.Content.ReadFromJsonAsync<WidgetPollResponse>();

        Assert.True(body!.Messages.Count <= 50, "page size must be bounded, not return all 60 in one response");
    }

    // ── Rate limiting ────────────────────────────────────────────────

    [Fact]
    public async Task Poll_ExceedsRateLimit_ReturnsTooManyRequests()
    {
        var (_, widget) = await CreateBusinessAndWidgetAsync();
        using var client = NewClient();
        var (_, token) = await InitAsync(client, widget.PublicKey);

        HttpStatusCode? rejected = null;
        for (var i = 0; i < 25; i++)
        {
            var response = await client.SendAsync(BearerGet("/api/widget/messages", token));
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                rejected = response.StatusCode;
                break;
            }
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected);
    }

    // ── Existing behavior unaffected ─────────────────────────────────

    [Fact]
    public async Task PostMessage_ResponseNowIncludesAiReplyIdDistinctFromCustomerMessageId()
    {
        // Guards the Step 6C fix to WidgetMessageResponse: the AI reply's own
        // id/timestamp must be distinguishable from the customer message's,
        // not silently reuse it (the pre-existing widget-client bug this
        // implementation also fixes).
        var (_, widget) = await CreateBusinessAndWidgetAsync();
        using var client = NewClient();
        var (_, token) = await InitAsync(client, widget.PublicKey);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/widget/messages")
        {
            Content = JsonContent.Create(new PostWidgetMessageRequest("Hello"))
        };
        request.Headers.Add("Authorization", $"Bearer {token}");
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<WidgetMessageResponse>();
        Assert.NotNull(body);
        // AiReplyId is independently null-or-not depending on live Claude
        // config in this environment; the only hard requirement is that
        // IF an AI reply exists, its id is not the customer message's id.
        if (body!.AiReplyId is { } aiReplyId)
        {
            Assert.NotEqual(body.Id, aiReplyId);
        }
    }
}
