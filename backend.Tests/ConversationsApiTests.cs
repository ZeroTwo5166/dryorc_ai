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
/// HTTP-level tests for ConversationsController.List (Step 9 — Business
/// Inbox pagination + LastMessagePreview), run against the real app + real
/// Neon dev DB via NivoraApiFactory — same pattern as NotificationApiTests.
/// Conversations/Messages are seeded directly through AppDbContext (there is
/// no HTTP endpoint that creates one with caller-controlled timestamps/ids —
/// the widget flow always uses DateTimeOffset.UtcNow), then read back
/// through the real controller endpoint.
///
/// GET /api/conversations/{id}/messages is untouched by Step 9 and already
/// covered by WidgetPipelineTests/WidgetConcurrencyAndIsolationTests — not
/// re-tested here.
/// </summary>
public class ConversationsApiTests : IAsyncLifetime
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

    /// <summary>Full register + confirm + login for a fresh business/Owner staff user. Returns the business id and an authed cookie.</summary>
    private async Task<(Guid BusinessId, string Cookie)> CreateBusinessAndLoginAsync(HttpClient anonClient, HttpClient staffClient, string businessName)
    {
        var email = $"staff-{Guid.NewGuid():N}@example.com";
        var (registerResponse, registerBody) = await RegisterAsync(anonClient, businessName, email);
        Assert.Equal(HttpStatusCode.OK, registerResponse.StatusCode);
        Assert.NotNull(registerBody);
        _businessIdsToClean.Add(registerBody!.BusinessId);
        await ConfirmEmailAsync(anonClient, registerBody.UserId);

        var (loginResponse, _, cookie) = await LoginAsync(staffClient, email);
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        Assert.NotNull(cookie);

        return (registerBody.BusinessId, cookie!);
    }

    private static HttpRequestMessage Authed(HttpMethod method, string url, string cookie)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("Cookie", cookie);
        return request;
    }

    private static string PageUrl(int take, ConversationCursor cursor) =>
        $"/api/conversations?take={take}&beforeActivityAt={Uri.EscapeDataString(cursor.ActivityAt.ToString("O"))}&beforeId={cursor.Id}";

    // Same rationale as NotificationApiTests.TruncateToMicroseconds —
    // Postgres timestamptz stores microsecond precision, one fractional
    // digit less than .NET's 100ns ticks.
    private static DateTimeOffset TruncateToMicroseconds(DateTimeOffset value) =>
        new(value.Ticks - (value.Ticks % 10), value.Offset);

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

    private static async Task<Guid> SeedConversationAsync(
        AppDbContext db, Guid businessId, Guid widgetId, DateTimeOffset createdAt, DateTimeOffset? lastMessageAt, Guid? id = null)
    {
        var conversation = new Conversation
        {
            Id = id ?? Guid.NewGuid(),
            BusinessId = businessId,
            WidgetId = widgetId,
            Channel = "Website",
            Status = ConversationStatus.Open,
            HandledBy = HandledBy.Ai,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            LastMessageAt = lastMessageAt,
        };
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync();
        return conversation.Id;
    }

    private static async Task SeedMessageAsync(
        AppDbContext db, Guid businessId, Guid conversationId, MessageRole role, string content, DateTimeOffset createdAt, Guid? id = null)
    {
        db.Messages.Add(new Message
        {
            Id = id ?? Guid.NewGuid(),
            BusinessId = businessId,
            ConversationId = conversationId,
            Role = role,
            Content = content,
            CreatedAt = createdAt,
        });
        await db.SaveChangesAsync();
    }

    // ── Pagination ───────────────────────────────────────────────────

    [Fact]
    public async Task List_PaginatesByLastActivity_NewestFirst_NoDuplicatesAcrossPages_EndsCorrectly()
    {
        using var anonClient = NewClient();
        using var staffClient = NewClient();
        var (businessId, cookie) = await CreateBusinessAndLoginAsync(anonClient, staffClient, "Inbox Pagination Business");

        var baseTime = TruncateToMicroseconds(DateTimeOffset.UtcNow.AddHours(-1));
        var ids = new Guid[5];

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var widgetId = await SeedWidgetAsync(db, businessId);
            for (var i = 0; i < 5; i++)
            {
                var activity = baseTime.AddMinutes(i); // strictly increasing — index 4 is newest
                ids[i] = await SeedConversationAsync(db, businessId, widgetId, activity, activity);
            }
        }

        var newestFirst = ids.Reverse().ToArray(); // [4, 3, 2, 1, 0]
        var seen = new List<Guid>();

        var firstPage = await staffClient.SendAsync(Authed(HttpMethod.Get, "/api/conversations?take=2", cookie));
        Assert.Equal(HttpStatusCode.OK, firstPage.StatusCode);
        var firstBody = await firstPage.Content.ReadFromJsonAsync<ConversationListResponse>();
        Assert.NotNull(firstBody);
        Assert.Equal(new[] { newestFirst[0], newestFirst[1] }, firstBody!.Conversations.Select(c => c.Id));
        Assert.NotNull(firstBody.NextCursor);
        seen.AddRange(firstBody.Conversations.Select(c => c.Id));

        var secondPageUrl = PageUrl(2, firstBody.NextCursor!);
        var secondPage = await staffClient.SendAsync(Authed(HttpMethod.Get, secondPageUrl, cookie));
        var secondBody = await secondPage.Content.ReadFromJsonAsync<ConversationListResponse>();
        Assert.NotNull(secondBody);
        Assert.Equal(new[] { newestFirst[2], newestFirst[3] }, secondBody!.Conversations.Select(c => c.Id));
        Assert.NotNull(secondBody.NextCursor);
        seen.AddRange(secondBody.Conversations.Select(c => c.Id));

        var thirdPageUrl = PageUrl(2, secondBody.NextCursor!);
        var thirdPage = await staffClient.SendAsync(Authed(HttpMethod.Get, thirdPageUrl, cookie));
        var thirdBody = await thirdPage.Content.ReadFromJsonAsync<ConversationListResponse>();
        Assert.NotNull(thirdBody);
        Assert.Equal(new[] { newestFirst[4] }, thirdBody!.Conversations.Select(c => c.Id));
        Assert.Null(thirdBody.NextCursor); // end of feed
        seen.AddRange(thirdBody.Conversations.Select(c => c.Id));

        Assert.Equal(newestFirst.OrderBy(x => x), seen.Distinct().OrderBy(x => x)); // all 5, no duplicates
    }

    [Fact]
    public async Task List_TieBreaksBySameActivityTimestamp_OrderedByIdDescending_RealPostgresUuidOrdering()
    {
        using var anonClient = NewClient();
        using var staffClient = NewClient();
        var (businessId, cookie) = await CreateBusinessAndLoginAsync(anonClient, staffClient, "Inbox Tie Business");

        var sharedTime = TruncateToMicroseconds(DateTimeOffset.UtcNow);
        var lowerId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var higherId = Guid.Parse("ffffffff-0000-0000-0000-000000000002");

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // Verified against real Postgres uuid ordering, not assumed from
            // .NET's Guid.CompareTo — same rationale as NotificationApiTests'
            // tie-break test; .NET's internal Guid layout does not agree
            // with Postgres's byte ordering for uuid comparisons.
            var postgresOrderingSaysHigherIsGreater = await db.Database
                .SqlQuery<bool>($"SELECT ({higherId}::uuid > {lowerId}::uuid) AS \"Value\"")
                .SingleAsync();
            Assert.True(postgresOrderingSaysHigherIsGreater, "Chosen higherId must sort after lowerId under PostgreSQL's real uuid ordering.");

            var widgetId = await SeedWidgetAsync(db, businessId);
            await SeedConversationAsync(db, businessId, widgetId, sharedTime, sharedTime, lowerId);
            await SeedConversationAsync(db, businessId, widgetId, sharedTime, sharedTime, higherId);
        }

        var response = await staffClient.SendAsync(Authed(HttpMethod.Get, "/api/conversations?take=2", cookie));
        var body = await response.Content.ReadFromJsonAsync<ConversationListResponse>();
        Assert.NotNull(body);
        Assert.Equal(new[] { higherId, lowerId }, body!.Conversations.Select(c => c.Id));
        Assert.Null(body.NextCursor);
    }

    // ── LastMessagePreview ───────────────────────────────────────────

    [Fact]
    public async Task List_LastMessagePreview_ReflectsMostRecentMessage_RegardlessOfRole()
    {
        using var anonClient = NewClient();
        using var staffClient = NewClient();
        var (businessId, cookie) = await CreateBusinessAndLoginAsync(anonClient, staffClient, "Inbox Preview Business");

        var now = TruncateToMicroseconds(DateTimeOffset.UtcNow);
        Guid conversationWithAiReply, conversationCustomerOnly;

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var widgetId = await SeedWidgetAsync(db, businessId);

            conversationWithAiReply = await SeedConversationAsync(db, businessId, widgetId, now.AddMinutes(-10), now.AddMinutes(-5));
            await SeedMessageAsync(db, businessId, conversationWithAiReply, MessageRole.Customer, "Older customer message", now.AddMinutes(-10));
            await SeedMessageAsync(db, businessId, conversationWithAiReply, MessageRole.Ai, "Newest AI reply", now.AddMinutes(-5));

            conversationCustomerOnly = await SeedConversationAsync(db, businessId, widgetId, now.AddMinutes(-8), now.AddMinutes(-8));
            await SeedMessageAsync(db, businessId, conversationCustomerOnly, MessageRole.Customer, "Only customer message", now.AddMinutes(-8));
        }

        var response = await staffClient.SendAsync(Authed(HttpMethod.Get, "/api/conversations?take=10", cookie));
        var body = await response.Content.ReadFromJsonAsync<ConversationListResponse>();
        Assert.NotNull(body);

        var withAiReply = body!.Conversations.Single(c => c.Id == conversationWithAiReply);
        Assert.Equal("Newest AI reply", withAiReply.LastMessagePreview);

        var customerOnly = body.Conversations.Single(c => c.Id == conversationCustomerOnly);
        Assert.Equal("Only customer message", customerOnly.LastMessagePreview);
    }

    [Fact]
    public async Task List_LastMessagePreview_TieBrokenByMessageIdDescending_WhenMessageTimestampsCollide()
    {
        using var anonClient = NewClient();
        using var staffClient = NewClient();
        var (businessId, cookie) = await CreateBusinessAndLoginAsync(anonClient, staffClient, "Inbox Message Tie Business");

        var sharedTime = TruncateToMicroseconds(DateTimeOffset.UtcNow);
        var lowerId = Guid.Parse("00000000-0000-0000-0000-000000000010");
        var higherId = Guid.Parse("ffffffff-0000-0000-0000-000000000020");
        Guid conversationId;

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var postgresOrderingSaysHigherIsGreater = await db.Database
                .SqlQuery<bool>($"SELECT ({higherId}::uuid > {lowerId}::uuid) AS \"Value\"")
                .SingleAsync();
            Assert.True(postgresOrderingSaysHigherIsGreater, "Chosen higherId must sort after lowerId under PostgreSQL's real uuid ordering.");

            var widgetId = await SeedWidgetAsync(db, businessId);
            conversationId = await SeedConversationAsync(db, businessId, widgetId, sharedTime, sharedTime);
            await SeedMessageAsync(db, businessId, conversationId, MessageRole.Customer, "Lower id message", sharedTime, lowerId);
            await SeedMessageAsync(db, businessId, conversationId, MessageRole.Ai, "Higher id message", sharedTime, higherId);
        }

        var response = await staffClient.SendAsync(Authed(HttpMethod.Get, "/api/conversations?take=10", cookie));
        var body = await response.Content.ReadFromJsonAsync<ConversationListResponse>();
        Assert.NotNull(body);

        var conversation = body!.Conversations.Single(c => c.Id == conversationId);
        Assert.Equal("Higher id message", conversation.LastMessagePreview);
    }

    [Fact]
    public async Task List_LastMessagePreview_NullWhenConversationHasNoMessages()
    {
        using var anonClient = NewClient();
        using var staffClient = NewClient();
        var (businessId, cookie) = await CreateBusinessAndLoginAsync(anonClient, staffClient, "Inbox No Messages Business");

        var now = TruncateToMicroseconds(DateTimeOffset.UtcNow);
        Guid conversationId;

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var widgetId = await SeedWidgetAsync(db, businessId);
            conversationId = await SeedConversationAsync(db, businessId, widgetId, now, null);
        }

        var response = await staffClient.SendAsync(Authed(HttpMethod.Get, "/api/conversations?take=10", cookie));
        var body = await response.Content.ReadFromJsonAsync<ConversationListResponse>();
        Assert.NotNull(body);

        var conversation = body!.Conversations.Single(c => c.Id == conversationId);
        Assert.Null(conversation.LastMessagePreview);
    }

    [Fact]
    public async Task List_LastMessagePreview_ShortMessageUnchanged_LongMessageTruncatedToExactly120Chars()
    {
        using var anonClient = NewClient();
        using var staffClient = NewClient();
        var (businessId, cookie) = await CreateBusinessAndLoginAsync(anonClient, staffClient, "Inbox Truncation Business");

        var now = TruncateToMicroseconds(DateTimeOffset.UtcNow);
        var shortContent = new string('a', 120); // exactly at the boundary — must be unchanged
        var longContent = new string('b', 200);
        Guid shortConversationId, longConversationId;

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var widgetId = await SeedWidgetAsync(db, businessId);

            shortConversationId = await SeedConversationAsync(db, businessId, widgetId, now.AddMinutes(-2), now.AddMinutes(-2));
            await SeedMessageAsync(db, businessId, shortConversationId, MessageRole.Customer, shortContent, now.AddMinutes(-2));

            longConversationId = await SeedConversationAsync(db, businessId, widgetId, now.AddMinutes(-1), now.AddMinutes(-1));
            await SeedMessageAsync(db, businessId, longConversationId, MessageRole.Customer, longContent, now.AddMinutes(-1));
        }

        var response = await staffClient.SendAsync(Authed(HttpMethod.Get, "/api/conversations?take=10", cookie));
        var body = await response.Content.ReadFromJsonAsync<ConversationListResponse>();
        Assert.NotNull(body);

        var shortConversation = body!.Conversations.Single(c => c.Id == shortConversationId);
        Assert.Equal(shortContent, shortConversation.LastMessagePreview);

        var longConversation = body.Conversations.Single(c => c.Id == longConversationId);
        Assert.NotNull(longConversation.LastMessagePreview);
        Assert.Equal(120, longConversation.LastMessagePreview!.Length);
        Assert.EndsWith("...", longConversation.LastMessagePreview);
        Assert.Equal(longContent[..117], longConversation.LastMessagePreview[..117]);
    }

    // ── Empty state / tenant isolation ──────────────────────────────

    [Fact]
    public async Task List_EmptyForBusinessWithNoConversations()
    {
        using var anonClient = NewClient();
        using var staffClient = NewClient();
        var (_, cookie) = await CreateBusinessAndLoginAsync(anonClient, staffClient, "Inbox Empty Business");

        var response = await staffClient.SendAsync(Authed(HttpMethod.Get, "/api/conversations", cookie));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ConversationListResponse>();
        Assert.NotNull(body);
        Assert.Empty(body!.Conversations);
        Assert.Null(body.NextCursor);
    }

    [Fact]
    public async Task List_NeverReturnsAnotherBusinesssConversations()
    {
        using var anonClientA = NewClient();
        using var staffClientA = NewClient();
        var (businessIdA, _) = await CreateBusinessAndLoginAsync(anonClientA, staffClientA, "Inbox Isolation Business A");

        using var anonClientB = NewClient();
        using var staffClientB = NewClient();
        var (_, cookieB) = await CreateBusinessAndLoginAsync(anonClientB, staffClientB, "Inbox Isolation Business B");

        Guid conversationIdA;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var widgetId = await SeedWidgetAsync(db, businessIdA);
            conversationIdA = await SeedConversationAsync(db, businessIdA, widgetId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        }

        var response = await staffClientB.SendAsync(Authed(HttpMethod.Get, "/api/conversations", cookieB));
        var body = await response.Content.ReadFromJsonAsync<ConversationListResponse>();
        Assert.NotNull(body);
        Assert.DoesNotContain(body!.Conversations, c => c.Id == conversationIdA);
    }

    // ── Cursor contract correction (ActivityAt) ─────────────────────

    [Fact]
    public async Task List_ContinuationAtEqualActivityTimestamp_CorrectlyPagesThroughTiedRowsByIdWithoutSkipOrDuplicate()
    {
        using var anonClient = NewClient();
        using var staffClient = NewClient();
        var (businessId, cookie) = await CreateBusinessAndLoginAsync(anonClient, staffClient, "Inbox Tied Continuation Business");

        var sharedTime = TruncateToMicroseconds(DateTimeOffset.UtcNow);
        var highId = Guid.Parse("ffffffff-0000-0000-0000-000000000001");
        var midId = Guid.Parse("80000000-0000-0000-0000-000000000002");
        var lowId = Guid.Parse("00000000-0000-0000-0000-000000000003");

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // Verified against real Postgres uuid ordering, same rationale as
            // the single-page tie-break test above — .NET's Guid.CompareTo
            // does not necessarily agree with Postgres's uuid byte ordering.
            var highVsMid = await db.Database.SqlQuery<bool>($"SELECT ({highId}::uuid > {midId}::uuid) AS \"Value\"").SingleAsync();
            var midVsLow = await db.Database.SqlQuery<bool>($"SELECT ({midId}::uuid > {lowId}::uuid) AS \"Value\"").SingleAsync();
            Assert.True(highVsMid && midVsLow, "Chosen ids must sort high > mid > low under PostgreSQL's real uuid ordering.");

            var widgetId = await SeedWidgetAsync(db, businessId);
            await SeedConversationAsync(db, businessId, widgetId, sharedTime, sharedTime, highId);
            await SeedConversationAsync(db, businessId, widgetId, sharedTime, sharedTime, midId);
            await SeedConversationAsync(db, businessId, widgetId, sharedTime, sharedTime, lowId);
        }

        var firstPage = await staffClient.SendAsync(Authed(HttpMethod.Get, "/api/conversations?take=1", cookie));
        var firstBody = await firstPage.Content.ReadFromJsonAsync<ConversationListResponse>();
        Assert.NotNull(firstBody);
        Assert.Equal(new[] { highId }, firstBody!.Conversations.Select(c => c.Id));
        Assert.NotNull(firstBody.NextCursor);
        Assert.Equal(sharedTime, firstBody.NextCursor!.ActivityAt);
        Assert.Equal(highId, firstBody.NextCursor.Id);

        var secondPage = await staffClient.SendAsync(Authed(HttpMethod.Get, PageUrl(1, firstBody.NextCursor), cookie));
        var secondBody = await secondPage.Content.ReadFromJsonAsync<ConversationListResponse>();
        Assert.NotNull(secondBody);
        Assert.Equal(new[] { midId }, secondBody!.Conversations.Select(c => c.Id)); // not skipped, not high again
        Assert.NotNull(secondBody.NextCursor);

        var thirdPage = await staffClient.SendAsync(Authed(HttpMethod.Get, PageUrl(1, secondBody.NextCursor!), cookie));
        var thirdBody = await thirdPage.Content.ReadFromJsonAsync<ConversationListResponse>();
        Assert.NotNull(thirdBody);
        Assert.Equal(new[] { lowId }, thirdBody!.Conversations.Select(c => c.Id));
        Assert.Null(thirdBody.NextCursor); // end of feed — low was never omitted or duplicated
    }

    [Fact]
    public async Task List_FallsBackToCreatedAtForNullLastMessageAt_AndCursorRoundTripsCorrectly()
    {
        using var anonClient = NewClient();
        using var staffClient = NewClient();
        var (businessId, cookie) = await CreateBusinessAndLoginAsync(anonClient, staffClient, "Inbox Null Activity Business");

        var now = TruncateToMicroseconds(DateTimeOffset.UtcNow);
        var withActivityCreatedAt = now.AddMinutes(-10);
        var withActivityLastMessageAt = now.AddMinutes(-1); // newer — must sort first
        var noActivityCreatedAt = now.AddMinutes(-5); // older than withActivityLastMessageAt — must sort second

        Guid withActivityId, noActivityId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var widgetId = await SeedWidgetAsync(db, businessId);
            withActivityId = await SeedConversationAsync(db, businessId, widgetId, withActivityCreatedAt, withActivityLastMessageAt);
            noActivityId = await SeedConversationAsync(db, businessId, widgetId, noActivityCreatedAt, null); // LastMessageAt null — falls back to CreatedAt
        }

        var firstPage = await staffClient.SendAsync(Authed(HttpMethod.Get, "/api/conversations?take=1", cookie));
        var firstBody = await firstPage.Content.ReadFromJsonAsync<ConversationListResponse>();
        Assert.NotNull(firstBody);
        Assert.Equal(new[] { withActivityId }, firstBody!.Conversations.Select(c => c.Id));
        Assert.NotNull(firstBody.NextCursor);
        // Cursor for a row WITH a LastMessageAt carries that value, not CreatedAt.
        Assert.Equal(withActivityLastMessageAt, firstBody.NextCursor!.ActivityAt);

        var secondPage = await staffClient.SendAsync(Authed(HttpMethod.Get, PageUrl(1, firstBody.NextCursor), cookie));
        var secondBody = await secondPage.Content.ReadFromJsonAsync<ConversationListResponse>();
        Assert.NotNull(secondBody);
        Assert.Equal(new[] { noActivityId }, secondBody!.Conversations.Select(c => c.Id));
        Assert.Null(secondBody.Conversations[0].LastMessageAt);
        Assert.Null(secondBody.NextCursor); // end of feed — correct ordering already proves the CreatedAt fallback was used for the sort key
    }

    /// <summary>
    /// Documents and deterministically verifies the accepted Step 9 v1
    /// tradeoff: LastMessageAt is mutable and only ever moves forward, so a
    /// conversation that receives new activity between two paginated
    /// requests can cross from "older than the current cursor" to "newer
    /// than it" and be omitted from the REST of that in-progress walk — but
    /// never duplicated, and always visible again on a fresh, cursor-less
    /// request. Deterministic without sleeps/polling: the "new activity" is
    /// a direct, timestamp-controlled DbContext write (same seeding
    /// convention as the rest of this file), sequenced to land strictly
    /// between the two HTTP calls.
    /// </summary>
    [Fact]
    public async Task List_ConversationThatBecomesMoreActiveMidPagination_IsOmittedFromInProgressWalk_ButVisibleOnFreshRequest()
    {
        using var anonClient = NewClient();
        using var staffClient = NewClient();
        var (businessId, cookie) = await CreateBusinessAndLoginAsync(anonClient, staffClient, "Inbox Mutable Activity Business");

        var baseTime = TruncateToMicroseconds(DateTimeOffset.UtcNow.AddHours(-1));
        var newestId = Guid.NewGuid(); // page 1
        var aboutToBecomeActiveId = Guid.NewGuid(); // would naturally land on page 2
        var oldestId = Guid.NewGuid(); // page 2, after the mid-pagination write

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var widgetId = await SeedWidgetAsync(db, businessId);
            await SeedConversationAsync(db, businessId, widgetId, baseTime.AddMinutes(2), baseTime.AddMinutes(2), newestId);
            await SeedConversationAsync(db, businessId, widgetId, baseTime.AddMinutes(1), baseTime.AddMinutes(1), aboutToBecomeActiveId);
            await SeedConversationAsync(db, businessId, widgetId, baseTime, baseTime, oldestId);
        }

        var firstPage = await staffClient.SendAsync(Authed(HttpMethod.Get, "/api/conversations?take=1", cookie));
        var firstBody = await firstPage.Content.ReadFromJsonAsync<ConversationListResponse>();
        Assert.NotNull(firstBody);
        Assert.Equal(new[] { newestId }, firstBody!.Conversations.Select(c => c.Id));
        Assert.NotNull(firstBody.NextCursor);

        // New activity lands on aboutToBecomeActiveId strictly between the
        // two paginated requests — moving it past the cursor the client is
        // about to continue from.
        var newActivity = TruncateToMicroseconds(DateTimeOffset.UtcNow);
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var conversation = await db.Conversations.IgnoreQueryFilters().SingleAsync(c => c.Id == aboutToBecomeActiveId);
            conversation.LastMessageAt = newActivity;
            await db.SaveChangesAsync();
        }

        // Continuing the in-progress walk from the page-1 cursor: the row
        // that just became newer-than-cursor is omitted, not duplicated —
        // only oldestId remains reachable from this cursor.
        var secondPage = await staffClient.SendAsync(Authed(HttpMethod.Get, PageUrl(10, firstBody.NextCursor!), cookie));
        var secondBody = await secondPage.Content.ReadFromJsonAsync<ConversationListResponse>();
        Assert.NotNull(secondBody);
        Assert.Equal(new[] { oldestId }, secondBody!.Conversations.Select(c => c.Id));
        Assert.DoesNotContain(secondBody.Conversations, c => c.Id == aboutToBecomeActiveId);

        // A fresh, cursor-less request immediately shows it again, now at
        // the top — nothing was lost, only omitted from that one walk.
        var freshRequest = await staffClient.SendAsync(Authed(HttpMethod.Get, "/api/conversations?take=1", cookie));
        var freshBody = await freshRequest.Content.ReadFromJsonAsync<ConversationListResponse>();
        Assert.NotNull(freshBody);
        Assert.Equal(new[] { aboutToBecomeActiveId }, freshBody!.Conversations.Select(c => c.Id));
    }
}
