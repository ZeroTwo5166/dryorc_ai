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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
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
    private string _adoConnectionString = null!;

    public Task InitializeAsync()
    {
        // Step 15 concurrency tests need a raw, independent connection (not
        // routed through the app's own DI-managed AppDbContext) to act as a
        // deterministic advisory-lock barrier — same technique
        // WidgetConversationServiceTests.cs established for the
        // notification-concurrency fix.
        var configuration = new ConfigurationBuilder()
            .AddUserSecrets<Program>()
            .Build();
        var connectionUri = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection not found via user-secrets.");
        _adoConnectionString = PostgresConnectionString.FromUri(connectionUri);

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_businessIdsToClean.Count > 0)
        {
            await using var scope = _factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var ids = _businessIdsToClean.ToArray();

            // Must run before Businesses is deleted — same order NotificationDataTests already establishes.
            // ConversationHandoffs (Step 15) has the same Restrict-on-Business FK shape.
            // Tickets (Step 16) is Restrict-on-Business AND Restrict-on-Customer, so it must
            // run before both Businesses and Customers below.
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"AuditLogs\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Tickets\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"ConversationHandoffs\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"NotificationRecipients\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Notifications\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Messages\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Conversations\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Customers\" WHERE \"BusinessId\" = ANY({ids})");
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

    private static HttpRequestMessage WithBody<T>(HttpRequestMessage request, T body)
    {
        request.Content = JsonContent.Create(body);
        return request;
    }

    /// <summary>Same as CreateBusinessAndLoginAsync but also returns the staff user's own id, needed to assert Message.AuthorUserId.</summary>
    private async Task<(Guid BusinessId, Guid UserId, string Cookie)> CreateBusinessAndLoginWithUserIdAsync(
        HttpClient anonClient, HttpClient staffClient, string businessName)
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

        return (registerBody.BusinessId, registerBody.UserId, cookie!);
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

    // ── Step 9: staff-reply composer ──────────────────────────────────

    [Fact]
    public async Task CreateMessage_AuthorizedStaff_PersistsAgentMessage_WithCorrectRoleAndAuthor()
    {
        using var anonClient = NewClient();
        using var staffClient = NewClient();
        var (businessId, userId, cookie) = await CreateBusinessAndLoginWithUserIdAsync(anonClient, staffClient, "Staff Reply Business");

        Guid conversationId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var widgetId = await SeedWidgetAsync(db, businessId);
            conversationId = await SeedConversationAsync(db, businessId, widgetId, DateTimeOffset.UtcNow, null);
        }

        var response = await staffClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, $"/api/conversations/{conversationId}/messages", cookie),
            new CreateConversationMessageRequest("Thanks for reaching out — let me take a look.")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ConversationMessageResponse>();
        Assert.NotNull(body);
        Assert.Equal("Agent", body!.Role);
        Assert.Equal("Thanks for reaching out — let me take a look.", body.Content);

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var message = await verifyDb.Messages.IgnoreQueryFilters().SingleAsync(m => m.Id == body.Id);
        Assert.Equal(MessageRole.Agent, message.Role);
        Assert.Equal(userId, message.AuthorUserId);
        Assert.Equal(conversationId, message.ConversationId);
        Assert.Equal(businessId, message.BusinessId);
    }

    [Fact]
    public async Task CreateMessage_BlankContent_ReturnsBadRequest()
    {
        using var anonClient = NewClient();
        using var staffClient = NewClient();
        var (businessId, cookie) = await CreateBusinessAndLoginAsync(anonClient, staffClient, "Staff Reply Blank Business");

        Guid conversationId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var widgetId = await SeedWidgetAsync(db, businessId);
            conversationId = await SeedConversationAsync(db, businessId, widgetId, DateTimeOffset.UtcNow, null);
        }

        var response = await staffClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, $"/api/conversations/{conversationId}/messages", cookie),
            new CreateConversationMessageRequest("   ")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateMessage_Unauthenticated_ReturnsUnauthorized()
    {
        using var anonClient = NewClient();

        var response = await anonClient.PostAsJsonAsync(
            $"/api/conversations/{Guid.NewGuid()}/messages",
            new CreateConversationMessageRequest("Hello"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CreateMessage_CrossTenantConversation_ReturnsNotFound()
    {
        using var anonClientA = NewClient();
        using var staffClientA = NewClient();
        var (businessIdA, _) = await CreateBusinessAndLoginAsync(anonClientA, staffClientA, "Staff Reply Isolation Business A");

        using var anonClientB = NewClient();
        using var staffClientB = NewClient();
        var (_, cookieB) = await CreateBusinessAndLoginAsync(anonClientB, staffClientB, "Staff Reply Isolation Business B");

        Guid conversationIdA;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var widgetId = await SeedWidgetAsync(db, businessIdA);
            conversationIdA = await SeedConversationAsync(db, businessIdA, widgetId, DateTimeOffset.UtcNow, null);
        }

        // Business B's staff attempting to reply to Business A's conversation
        // id must not succeed — proves the endpoint can't be used for
        // cross-tenant access merely by guessing/enumerating a conversationId.
        var response = await staffClientB.SendAsync(WithBody(
            Authed(HttpMethod.Post, $"/api/conversations/{conversationIdA}/messages", cookieB),
            new CreateConversationMessageRequest("Should never be accepted.")));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var messageCount = await verifyDb.Messages.IgnoreQueryFilters().CountAsync(m => m.ConversationId == conversationIdA);
        Assert.Equal(0, messageCount);

        // The new Step 15 handoff insert only runs after authorization
        // succeeds and inside the same transaction as the (never-reached)
        // message insert — a rejected cross-tenant request can't produce
        // a ConversationHandoff row any more than it can produce a Message.
        var handoffCount = await verifyDb.ConversationHandoffs.IgnoreQueryFilters().CountAsync(h => h.ConversationId == conversationIdA);
        Assert.Equal(0, handoffCount);
    }

    [Fact]
    public async Task CreateMessage_ClosedConversation_ReturnsConflict()
    {
        using var anonClient = NewClient();
        using var staffClient = NewClient();
        var (businessId, cookie) = await CreateBusinessAndLoginAsync(anonClient, staffClient, "Staff Reply Closed Business");

        Guid conversationId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var widgetId = await SeedWidgetAsync(db, businessId);
            conversationId = await SeedConversationAsync(db, businessId, widgetId, DateTimeOffset.UtcNow, null);

            var conversation = await db.Conversations.IgnoreQueryFilters().SingleAsync(c => c.Id == conversationId);
            conversation.Status = ConversationStatus.Closed;
            await db.SaveChangesAsync();
        }

        var response = await staffClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, $"/api/conversations/{conversationId}/messages", cookie),
            new CreateConversationMessageRequest("Reopening attempt.")));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var messageCount = await verifyDb.Messages.IgnoreQueryFilters().CountAsync(m => m.ConversationId == conversationId);
        Assert.Equal(0, messageCount);
        var conversation2 = await verifyDb.Conversations.IgnoreQueryFilters().SingleAsync(c => c.Id == conversationId);
        Assert.Equal(HandledBy.Ai, conversation2.HandledBy); // rejected before any mutation, HandledBy untouched
    }

    [Fact]
    public async Task CreateMessage_FirstAgentReply_ChangesHandledByFromAiToHuman()
    {
        using var anonClient = NewClient();
        using var staffClient = NewClient();
        var (businessId, cookie) = await CreateBusinessAndLoginAsync(anonClient, staffClient, "Staff Reply HandledBy Business");

        Guid conversationId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var widgetId = await SeedWidgetAsync(db, businessId);
            conversationId = await SeedConversationAsync(db, businessId, widgetId, DateTimeOffset.UtcNow, null);

            var conversationBefore = await db.Conversations.IgnoreQueryFilters().SingleAsync(c => c.Id == conversationId);
            Assert.Equal(HandledBy.Ai, conversationBefore.HandledBy);
        }

        var firstReply = await staffClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, $"/api/conversations/{conversationId}/messages", cookie),
            new CreateConversationMessageRequest("First reply.")));
        Assert.Equal(HttpStatusCode.OK, firstReply.StatusCode);

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var conversation = await db.Conversations.IgnoreQueryFilters().SingleAsync(c => c.Id == conversationId);
            Assert.Equal(HandledBy.Human, conversation.HandledBy);
        }

        // A second reply is idempotent — no error, HandledBy simply stays Human.
        var secondReply = await staffClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, $"/api/conversations/{conversationId}/messages", cookie),
            new CreateConversationMessageRequest("Second reply.")));
        Assert.Equal(HttpStatusCode.OK, secondReply.StatusCode);

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var finalConversation = await verifyDb.Conversations.IgnoreQueryFilters().SingleAsync(c => c.Id == conversationId);
        Assert.Equal(HandledBy.Human, finalConversation.HandledBy);
        var messageCount = await verifyDb.Messages.IgnoreQueryFilters().CountAsync(m => m.ConversationId == conversationId);
        Assert.Equal(2, messageCount); // exactly the two replies just sent, no duplicates
    }

    [Fact]
    public async Task CreateMessage_AgentMessage_AppearsInExistingMessagesEndpoint_NoDuplicates()
    {
        using var anonClient = NewClient();
        using var staffClient = NewClient();
        var (businessId, cookie) = await CreateBusinessAndLoginAsync(anonClient, staffClient, "Staff Reply Visibility Business");

        Guid conversationId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var widgetId = await SeedWidgetAsync(db, businessId);
            conversationId = await SeedConversationAsync(db, businessId, widgetId, DateTimeOffset.UtcNow, null);
            await SeedMessageAsync(db, businessId, conversationId, MessageRole.Customer, "Is my order shipped?", DateTimeOffset.UtcNow);
        }

        var reply = await staffClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, $"/api/conversations/{conversationId}/messages", cookie),
            new CreateConversationMessageRequest("Yes, it shipped yesterday!")));
        Assert.Equal(HttpStatusCode.OK, reply.StatusCode);

        var messagesResponse = await staffClient.SendAsync(Authed(HttpMethod.Get, $"/api/conversations/{conversationId}/messages", cookie));
        Assert.Equal(HttpStatusCode.OK, messagesResponse.StatusCode);
        var detail = await messagesResponse.Content.ReadFromJsonAsync<ConversationDetailResponse>();
        Assert.NotNull(detail);
        Assert.Equal(2, detail!.Messages.Count);
        Assert.Equal("Customer", detail.Messages[0].Role);
        Assert.Equal("Agent", detail.Messages[1].Role);
        Assert.Equal("Yes, it shipped yesterday!", detail.Messages[1].Content);
        // Step 15: the endpoint's response now also carries HandledBy —
        // the staff reply above is this conversation's first Agent message,
        // so it must already show Human here.
        Assert.Equal("Human", detail.HandledBy);
    }

    // ── Staff reply resolves the CustomerReplied notification ───────────

    [Fact]
    public async Task CreateMessage_StaffReply_MarksExistingUnreadCustomerRepliedNotificationRead()
    {
        using var anonClient = NewClient();
        using var staffClient = NewClient();
        var (businessId, userId, cookie) = await CreateBusinessAndLoginWithUserIdAsync(anonClient, staffClient, "Resolve Notification Business");

        Guid conversationId;
        Guid notificationId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var notificationService = scope.ServiceProvider.GetRequiredService<INotificationService>();
            var widgetId = await SeedWidgetAsync(db, businessId);
            conversationId = await SeedConversationAsync(db, businessId, widgetId, DateTimeOffset.UtcNow, null);

            await notificationService.NotifyAsync(
                businessId, NotificationTypes.CustomerReplied, new CustomerRepliedArgs(),
                ActorType.NivoraSystem, conversationId: conversationId);
            notificationId = await db.Notifications.IgnoreQueryFilters()
                .Where(n => n.ConversationId == conversationId && n.Type == NotificationTypes.CustomerReplied)
                .Select(n => n.Id)
                .SingleAsync();
        }

        var reply = await staffClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, $"/api/conversations/{conversationId}/messages", cookie),
            new CreateConversationMessageRequest("On it!")));
        Assert.Equal(HttpStatusCode.OK, reply.StatusCode);

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var recipient = await verifyDb.NotificationRecipients.IgnoreQueryFilters()
            .SingleAsync(r => r.NotificationId == notificationId && r.UserId == userId);
        Assert.True(recipient.IsRead);
        Assert.NotNull(recipient.ReadAt);
    }

    [Fact]
    public async Task CreateMessage_StaffReply_MarksNotificationRead_ForAllRecipients_NotJustTheReplyingStaffMember()
    {
        using var anonClient = NewClient();
        using var staffClient = NewClient();
        var (businessId, userId, cookie) = await CreateBusinessAndLoginWithUserIdAsync(anonClient, staffClient, "Resolve Notification Multi-Staff Business");

        Guid conversationId;
        Guid notificationId;
        Guid otherStaffId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var notificationService = scope.ServiceProvider.GetRequiredService<INotificationService>();
            var widgetId = await SeedWidgetAsync(db, businessId);
            conversationId = await SeedConversationAsync(db, businessId, widgetId, DateTimeOffset.UtcNow, null);

            // A second active staff member of the same business, not the one
            // who will send the reply below.
            var otherStaff = new ApplicationUser
            {
                Id = Guid.NewGuid(),
                BusinessId = businessId,
                UserName = $"other-staff-{Guid.NewGuid():N}@example.com",
                Email = $"other-staff-{Guid.NewGuid():N}@example.com",
                EmailConfirmed = true,
                IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Users.Add(otherStaff);
            await db.SaveChangesAsync();
            otherStaffId = otherStaff.Id;

            await notificationService.NotifyAsync(
                businessId, NotificationTypes.CustomerReplied, new CustomerRepliedArgs(),
                ActorType.NivoraSystem, conversationId: conversationId); // broadcast — both staff become recipients
            notificationId = await db.Notifications.IgnoreQueryFilters()
                .Where(n => n.ConversationId == conversationId && n.Type == NotificationTypes.CustomerReplied)
                .Select(n => n.Id)
                .SingleAsync();
        }

        var reply = await staffClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, $"/api/conversations/{conversationId}/messages", cookie),
            new CreateConversationMessageRequest("Handling this now.")));
        Assert.Equal(HttpStatusCode.OK, reply.StatusCode);

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var recipients = await verifyDb.NotificationRecipients.IgnoreQueryFilters()
            .Where(r => r.NotificationId == notificationId)
            .ToListAsync();

        Assert.Equal(2, recipients.Count);
        // Both the replying staff member and the other, non-replying staff
        // member's copy of the notification are resolved — the underlying
        // "needs a human" condition is conversation-wide, not per-viewer.
        Assert.All(recipients, r => Assert.True(r.IsRead));
        Assert.Contains(recipients, r => r.UserId == userId);
        Assert.Contains(recipients, r => r.UserId == otherStaffId);
    }

    [Fact]
    public async Task CreateMessage_StaffReply_DoesNotMarkUnrelatedNotificationsRead()
    {
        using var anonClient = NewClient();
        using var staffClient = NewClient();
        var (businessId, userId, cookie) = await CreateBusinessAndLoginWithUserIdAsync(anonClient, staffClient, "Resolve Notification Unrelated Business");

        Guid conversationId;
        Guid otherConversationId;
        Guid sameConversationDifferentTypeNotificationId;
        Guid differentConversationNotificationId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var notificationService = scope.ServiceProvider.GetRequiredService<INotificationService>();
            var widgetId = await SeedWidgetAsync(db, businessId);
            conversationId = await SeedConversationAsync(db, businessId, widgetId, DateTimeOffset.UtcNow, null);
            otherConversationId = await SeedConversationAsync(db, businessId, widgetId, DateTimeOffset.UtcNow, null);

            // Same conversation, different notification type — must stay untouched.
            await notificationService.NotifyAsync(
                businessId, NotificationTypes.ConversationAssigned, new ConversationAssignedArgs(),
                ActorType.NivoraSystem, conversationId: conversationId);
            sameConversationDifferentTypeNotificationId = await db.Notifications.IgnoreQueryFilters()
                .Where(n => n.ConversationId == conversationId && n.Type == NotificationTypes.ConversationAssigned)
                .Select(n => n.Id).SingleAsync();

            // Same type (CustomerReplied), different conversation — must stay untouched.
            await notificationService.NotifyAsync(
                businessId, NotificationTypes.CustomerReplied, new CustomerRepliedArgs(),
                ActorType.NivoraSystem, conversationId: otherConversationId);
            differentConversationNotificationId = await db.Notifications.IgnoreQueryFilters()
                .Where(n => n.ConversationId == otherConversationId && n.Type == NotificationTypes.CustomerReplied)
                .Select(n => n.Id).SingleAsync();
        }

        var reply = await staffClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, $"/api/conversations/{conversationId}/messages", cookie),
            new CreateConversationMessageRequest("Replying to the right conversation only.")));
        Assert.Equal(HttpStatusCode.OK, reply.StatusCode);

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();

        var unrelatedTypeRecipient = await verifyDb.NotificationRecipients.IgnoreQueryFilters()
            .SingleAsync(r => r.NotificationId == sameConversationDifferentTypeNotificationId && r.UserId == userId);
        Assert.False(unrelatedTypeRecipient.IsRead);

        var unrelatedConversationRecipient = await verifyDb.NotificationRecipients.IgnoreQueryFilters()
            .SingleAsync(r => r.NotificationId == differentConversationNotificationId && r.UserId == userId);
        Assert.False(unrelatedConversationRecipient.IsRead);
    }

    // ── Step 15 completion: CreateMessage's own Ai -> Human handoff row ──

    [Fact]
    public async Task CreateMessage_FirstAgentReply_WritesAiToHumanConversationHandoffRow_AndStillResolvesNotification()
    {
        using var anonClient = NewClient();
        using var staffClient = NewClient();
        var (businessId, userId, cookie) = await CreateBusinessAndLoginWithUserIdAsync(anonClient, staffClient, "Staff Takeover Handoff Business");

        Guid conversationId;
        Guid notificationId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var notificationService = scope.ServiceProvider.GetRequiredService<INotificationService>();
            var widgetId = await SeedWidgetAsync(db, businessId);
            // Deliberately Ai-handled (default) — this is the genuine
            // Ai -> Human transition this test is proving gets logged.
            conversationId = await SeedConversationAsync(db, businessId, widgetId, DateTimeOffset.UtcNow, null);

            // Also proves the pre-existing notification-resolve behavior
            // (ed136ec) is untouched by the new handoff insert sharing its
            // transaction — same seeding pattern as
            // CreateMessage_StaffReply_MarksExistingUnreadCustomerRepliedNotificationRead
            // above, folded into this test rather than duplicated per-task.
            await notificationService.NotifyAsync(
                businessId, NotificationTypes.CustomerReplied, new CustomerRepliedArgs(),
                ActorType.NivoraSystem, conversationId: conversationId);
            notificationId = await db.Notifications.IgnoreQueryFilters()
                .Where(n => n.ConversationId == conversationId && n.Type == NotificationTypes.CustomerReplied)
                .Select(n => n.Id)
                .SingleAsync();
        }

        var reply = await staffClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, $"/api/conversations/{conversationId}/messages", cookie),
            new CreateConversationMessageRequest("Taking over from here.")));
        Assert.Equal(HttpStatusCode.OK, reply.StatusCode);

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();

        var conversation = await verifyDb.Conversations.IgnoreQueryFilters().SingleAsync(c => c.Id == conversationId);
        Assert.Equal(HandledBy.Human, conversation.HandledBy);

        var handoff = await verifyDb.ConversationHandoffs.IgnoreQueryFilters().SingleAsync(h => h.ConversationId == conversationId);
        Assert.Equal(businessId, handoff.BusinessId);
        Assert.Equal(HandledBy.Ai, handoff.FromHandledBy);
        Assert.Equal(HandledBy.Human, handoff.ToHandledBy);
        Assert.Equal(HandoffReasons.StaffManualTakeover, handoff.Reason);
        Assert.Equal(ActorType.NivoraBusinessStaff, handoff.ActorType);
        Assert.Equal(userId, handoff.ActorId);

        var recipient = await verifyDb.NotificationRecipients.IgnoreQueryFilters()
            .SingleAsync(r => r.NotificationId == notificationId && r.UserId == userId);
        Assert.True(recipient.IsRead);
    }

    [Fact]
    public async Task CreateMessage_StaffReply_WhileAlreadyHuman_DoesNotCreateAnotherHandoffRow()
    {
        using var anonClient = NewClient();
        using var staffClient = NewClient();
        var (businessId, cookie) = await CreateBusinessAndLoginAsync(anonClient, staffClient, "Staff Reply Already Human Business");

        Guid conversationId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var widgetId = await SeedWidgetAsync(db, businessId);
            // Deliberately seeded Human-handled directly — no genuine
            // Ai -> Human transition ever occurs in this test, so no
            // handoff row should ever be written.
            conversationId = await SeedHumanHandledConversationAsync(db, businessId, widgetId);
        }

        var reply = await staffClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, $"/api/conversations/{conversationId}/messages", cookie),
            new CreateConversationMessageRequest("Following up.")));
        Assert.Equal(HttpStatusCode.OK, reply.StatusCode);

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();

        var conversation = await verifyDb.Conversations.IgnoreQueryFilters().SingleAsync(c => c.Id == conversationId);
        Assert.Equal(HandledBy.Human, conversation.HandledBy);

        var handoffCount = await verifyDb.ConversationHandoffs.IgnoreQueryFilters().CountAsync(h => h.ConversationId == conversationId);
        Assert.Equal(0, handoffCount);
    }

    /// <summary>
    /// Race: two simultaneous staff replies to the same Ai-handled
    /// conversation. Proves exactly one Ai -> Human ConversationHandoff row
    /// is written — never two — regardless of which request's lock
    /// acquisition actually wins, under real forced Postgres-level
    /// contention (same barrier technique as
    /// ReturnToAi_TwoSimultaneousReversals_EndsAi_ExactlyOneHandoffRow).
    /// This is narrower than the tracked Race D follow-up (which additionally
    /// wants CreateMessage-vs-customer-message coverage); this test exists
    /// only because the handoff insert itself is new production behavior
    /// whose duplicate-row risk must be checked before this task can be
    /// considered done — it does not attempt to close the broader Race D
    /// gap.
    /// </summary>
    [Fact]
    public async Task CreateMessage_TwoSimultaneousStaffReplies_CreatesExactlyOneHandoffRow()
    {
        using var anonClient = NewClient();
        using var staffClient1 = NewClient();
        using var staffClient2 = NewClient();
        var (businessId, cookie) = await CreateBusinessAndLoginAsync(anonClient, staffClient1, "Concurrent Staff Takeover Business");

        Guid conversationId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var widgetId = await SeedWidgetAsync(db, businessId);
            conversationId = await SeedConversationAsync(db, businessId, widgetId, DateTimeOffset.UtcNow, null);
        }

        var lockKey = AdvisoryLockKeyGenerator.ForConversationNotification(businessId, conversationId);
        var (barrierConnection, barrierTransaction) = await AcquireBarrierAsync(lockKey);
        await using var _bc = barrierConnection;
        await using var _bt = barrierTransaction;

        var task1 = staffClient1.SendAsync(WithBody(
            Authed(HttpMethod.Post, $"/api/conversations/{conversationId}/messages", cookie),
            new CreateConversationMessageRequest("Concurrent reply A")));
        var task2 = staffClient2.SendAsync(WithBody(
            Authed(HttpMethod.Post, $"/api/conversations/{conversationId}/messages", cookie),
            new CreateConversationMessageRequest("Concurrent reply B")));

        await WaitForAdvisoryLockWaitersAsync(barrierConnection, lockKey, 2, "two-simultaneous-staff-replies");
        await barrierTransaction.RollbackAsync();

        await Task.WhenAll(task1, task2);
        Assert.Equal(HttpStatusCode.OK, (await task1).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await task2).StatusCode);

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();

        var conversation = await verifyDb.Conversations.IgnoreQueryFilters().SingleAsync(c => c.Id == conversationId);
        Assert.Equal(HandledBy.Human, conversation.HandledBy);

        var messageCount = await verifyDb.Messages.IgnoreQueryFilters().CountAsync(m => m.ConversationId == conversationId && m.Role == MessageRole.Agent);
        Assert.Equal(2, messageCount); // both replies persisted, no message lost

        var handoffCount = await verifyDb.ConversationHandoffs.IgnoreQueryFilters().CountAsync(h => h.ConversationId == conversationId);
        Assert.Equal(1, handoffCount); // only the first to acquire the lock observed the genuine Ai -> Human transition
    }

    // ── Step 15: Human -> Ai reversal ────────────────────────────────────

    private static async Task<Guid> SeedHumanHandledConversationAsync(
        AppDbContext db, Guid businessId, Guid widgetId, string? visitorSessionId = null, Guid? id = null)
    {
        var conversation = new Conversation
        {
            Id = id ?? Guid.NewGuid(),
            BusinessId = businessId,
            WidgetId = widgetId,
            VisitorSessionId = visitorSessionId,
            Channel = "Website",
            Status = ConversationStatus.Open,
            HandledBy = HandledBy.Human,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync();
        return conversation.Id;
    }

    // ── Basic behavior ────────────────────────────────────────────────

    [Fact]
    public async Task ReturnToAi_HumanHandledConversation_SetsHandledByToAi_WritesConversationHandoffRow()
    {
        using var anonClient = NewClient();
        using var staffClient = NewClient();
        var (businessId, userId, cookie) = await CreateBusinessAndLoginWithUserIdAsync(anonClient, staffClient, "Return To AI Business");

        Guid conversationId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var widgetId = await SeedWidgetAsync(db, businessId);
            conversationId = await SeedHumanHandledConversationAsync(db, businessId, widgetId);
        }

        var response = await staffClient.SendAsync(Authed(HttpMethod.Post, $"/api/conversations/{conversationId}/return-to-ai", cookie));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var conversation = await verifyDb.Conversations.IgnoreQueryFilters().SingleAsync(c => c.Id == conversationId);
        Assert.Equal(HandledBy.Ai, conversation.HandledBy);

        var handoff = await verifyDb.ConversationHandoffs.IgnoreQueryFilters().SingleAsync(h => h.ConversationId == conversationId);
        Assert.Equal(businessId, handoff.BusinessId);
        Assert.Equal(HandledBy.Human, handoff.FromHandledBy);
        Assert.Equal(HandledBy.Ai, handoff.ToHandledBy);
        Assert.Equal(HandoffReasons.ResolvedReturnedToAi, handoff.Reason);
        Assert.Equal(ActorType.NivoraBusinessStaff, handoff.ActorType);
        Assert.Equal(userId, handoff.ActorId);
    }

    [Fact]
    public async Task ReturnToAi_AlreadyAiConversation_IsIdempotentNoOp_NoHandoffRow()
    {
        using var anonClient = NewClient();
        using var staffClient = NewClient();
        var (businessId, cookie) = await CreateBusinessAndLoginAsync(anonClient, staffClient, "Return To AI Idempotent Business");

        Guid conversationId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var widgetId = await SeedWidgetAsync(db, businessId);
            // Deliberately Ai-handled (default) — never touched by CreateMessage/HandledBy = Human.
            conversationId = await SeedConversationAsync(db, businessId, widgetId, DateTimeOffset.UtcNow, null);
        }

        var response = await staffClient.SendAsync(Authed(HttpMethod.Post, $"/api/conversations/{conversationId}/return-to-ai", cookie));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var conversation = await verifyDb.Conversations.IgnoreQueryFilters().SingleAsync(c => c.Id == conversationId);
        Assert.Equal(HandledBy.Ai, conversation.HandledBy);

        var handoffCount = await verifyDb.ConversationHandoffs.IgnoreQueryFilters().CountAsync(h => h.ConversationId == conversationId);
        Assert.Equal(0, handoffCount);
    }

    [Fact]
    public async Task ReturnToAi_CalledTwiceSequentially_SecondCallIsNoOp_NoDuplicateHandoffRow()
    {
        using var anonClient = NewClient();
        using var staffClient = NewClient();
        var (businessId, cookie) = await CreateBusinessAndLoginAsync(anonClient, staffClient, "Return To AI Repeat Business");

        Guid conversationId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var widgetId = await SeedWidgetAsync(db, businessId);
            conversationId = await SeedHumanHandledConversationAsync(db, businessId, widgetId);
        }

        var first = await staffClient.SendAsync(Authed(HttpMethod.Post, $"/api/conversations/{conversationId}/return-to-ai", cookie));
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);

        var second = await staffClient.SendAsync(Authed(HttpMethod.Post, $"/api/conversations/{conversationId}/return-to-ai", cookie));
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var conversation = await verifyDb.Conversations.IgnoreQueryFilters().SingleAsync(c => c.Id == conversationId);
        Assert.Equal(HandledBy.Ai, conversation.HandledBy);

        var handoffCount = await verifyDb.ConversationHandoffs.IgnoreQueryFilters().CountAsync(h => h.ConversationId == conversationId);
        Assert.Equal(1, handoffCount); // only the first call recorded a genuine transition
    }

    // ── Authorization ────────────────────────────────────────────────

    [Fact]
    public async Task ReturnToAi_Unauthenticated_ReturnsUnauthorized()
    {
        using var anonClient = NewClient();

        var response = await anonClient.PostAsync($"/api/conversations/{Guid.NewGuid()}/return-to-ai", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ReturnToAi_CrossTenantConversation_ReturnsNotFound()
    {
        using var anonClientA = NewClient();
        using var staffClientA = NewClient();
        var (businessIdA, _) = await CreateBusinessAndLoginAsync(anonClientA, staffClientA, "Return To AI Isolation Business A");

        using var anonClientB = NewClient();
        using var staffClientB = NewClient();
        var (_, cookieB) = await CreateBusinessAndLoginAsync(anonClientB, staffClientB, "Return To AI Isolation Business B");

        Guid conversationIdA;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var widgetId = await SeedWidgetAsync(db, businessIdA);
            conversationIdA = await SeedHumanHandledConversationAsync(db, businessIdA, widgetId);
        }

        // Business B's staff attempting to reverse Business A's conversation must not succeed.
        var response = await staffClientB.SendAsync(Authed(HttpMethod.Post, $"/api/conversations/{conversationIdA}/return-to-ai", cookieB));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var conversation = await verifyDb.Conversations.IgnoreQueryFilters().SingleAsync(c => c.Id == conversationIdA);
        Assert.Equal(HandledBy.Human, conversation.HandledBy); // untouched
        var handoffCount = await verifyDb.ConversationHandoffs.IgnoreQueryFilters().CountAsync(h => h.ConversationId == conversationIdA);
        Assert.Equal(0, handoffCount);
    }

    // ── Notifications ────────────────────────────────────────────────

    [Fact]
    public async Task ReturnToAi_MarksExistingUnreadCustomerRepliedNotification_ReadForAllRecipients()
    {
        using var anonClient = NewClient();
        using var staffClient = NewClient();
        var (businessId, userId, cookie) = await CreateBusinessAndLoginWithUserIdAsync(anonClient, staffClient, "Return To AI Resolve Notification Business");

        Guid conversationId;
        Guid notificationId;
        Guid otherStaffId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var notificationService = scope.ServiceProvider.GetRequiredService<INotificationService>();
            var widgetId = await SeedWidgetAsync(db, businessId);
            conversationId = await SeedHumanHandledConversationAsync(db, businessId, widgetId);

            var otherStaff = new ApplicationUser
            {
                Id = Guid.NewGuid(),
                BusinessId = businessId,
                UserName = $"other-staff-{Guid.NewGuid():N}@example.com",
                Email = $"other-staff-{Guid.NewGuid():N}@example.com",
                EmailConfirmed = true,
                IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Users.Add(otherStaff);
            await db.SaveChangesAsync();
            otherStaffId = otherStaff.Id;

            await notificationService.NotifyAsync(
                businessId, NotificationTypes.CustomerReplied, new CustomerRepliedArgs(),
                ActorType.NivoraSystem, conversationId: conversationId); // broadcast — both staff become recipients
            notificationId = await db.Notifications.IgnoreQueryFilters()
                .Where(n => n.ConversationId == conversationId && n.Type == NotificationTypes.CustomerReplied)
                .Select(n => n.Id).SingleAsync();
        }

        var response = await staffClient.SendAsync(Authed(HttpMethod.Post, $"/api/conversations/{conversationId}/return-to-ai", cookie));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var recipients = await verifyDb.NotificationRecipients.IgnoreQueryFilters()
            .Where(r => r.NotificationId == notificationId)
            .ToListAsync();

        Assert.Equal(2, recipients.Count);
        Assert.All(recipients, r => Assert.True(r.IsRead));
        Assert.Contains(recipients, r => r.UserId == userId);
        Assert.Contains(recipients, r => r.UserId == otherStaffId);
    }

    [Fact]
    public async Task ReturnToAi_DoesNotMarkUnrelatedNotificationTypeOrOtherConversationRead_AndCreatesNoNewNotification()
    {
        using var anonClient = NewClient();
        using var staffClient = NewClient();
        var (businessId, userId, cookie) = await CreateBusinessAndLoginWithUserIdAsync(anonClient, staffClient, "Return To AI Unrelated Notification Business");

        Guid conversationId;
        Guid otherConversationId;
        Guid sameConversationDifferentTypeNotificationId;
        Guid differentConversationNotificationId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var notificationService = scope.ServiceProvider.GetRequiredService<INotificationService>();
            var widgetId = await SeedWidgetAsync(db, businessId);
            conversationId = await SeedHumanHandledConversationAsync(db, businessId, widgetId);
            otherConversationId = await SeedHumanHandledConversationAsync(db, businessId, widgetId);

            // Same conversation, different notification type — must stay untouched.
            await notificationService.NotifyAsync(
                businessId, NotificationTypes.ConversationAssigned, new ConversationAssignedArgs(),
                ActorType.NivoraSystem, conversationId: conversationId);
            sameConversationDifferentTypeNotificationId = await db.Notifications.IgnoreQueryFilters()
                .Where(n => n.ConversationId == conversationId && n.Type == NotificationTypes.ConversationAssigned)
                .Select(n => n.Id).SingleAsync();

            // Same type (CustomerReplied), different conversation — must stay untouched.
            await notificationService.NotifyAsync(
                businessId, NotificationTypes.CustomerReplied, new CustomerRepliedArgs(),
                ActorType.NivoraSystem, conversationId: otherConversationId);
            differentConversationNotificationId = await db.Notifications.IgnoreQueryFilters()
                .Where(n => n.ConversationId == otherConversationId && n.Type == NotificationTypes.CustomerReplied)
                .Select(n => n.Id).SingleAsync();
        }

        var totalNotificationsBefore = await CountNotificationsAsync(businessId);

        var response = await staffClient.SendAsync(Authed(HttpMethod.Post, $"/api/conversations/{conversationId}/return-to-ai", cookie));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();

        var unrelatedTypeRecipient = await verifyDb.NotificationRecipients.IgnoreQueryFilters()
            .SingleAsync(r => r.NotificationId == sameConversationDifferentTypeNotificationId && r.UserId == userId);
        Assert.False(unrelatedTypeRecipient.IsRead);

        var unrelatedConversationRecipient = await verifyDb.NotificationRecipients.IgnoreQueryFilters()
            .SingleAsync(r => r.NotificationId == differentConversationNotificationId && r.UserId == userId);
        Assert.False(unrelatedConversationRecipient.IsRead);

        // Reversal creates no notification of any kind.
        var totalNotificationsAfter = await CountNotificationsAsync(businessId);
        Assert.Equal(totalNotificationsBefore, totalNotificationsAfter);
    }

    [Fact]
    public async Task ReturnToAi_DoesNotTouchAnotherBusinesssNotifications()
    {
        using var anonClientA = NewClient();
        using var staffClientA = NewClient();
        var (businessIdA, userIdA, cookieA) = await CreateBusinessAndLoginWithUserIdAsync(anonClientA, staffClientA, "Return To AI Cross-Business A");

        using var anonClientB = NewClient();
        using var staffClientB = NewClient();
        var (businessIdB, userIdB, _) = await CreateBusinessAndLoginWithUserIdAsync(anonClientB, staffClientB, "Return To AI Cross-Business B");

        Guid conversationIdA;
        Guid conversationIdB;
        Guid notificationIdB;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var notificationService = scope.ServiceProvider.GetRequiredService<INotificationService>();
            var widgetIdA = await SeedWidgetAsync(db, businessIdA);
            var widgetIdB = await SeedWidgetAsync(db, businessIdB);
            conversationIdA = await SeedHumanHandledConversationAsync(db, businessIdA, widgetIdA);
            conversationIdB = await SeedHumanHandledConversationAsync(db, businessIdB, widgetIdB);

            await notificationService.NotifyAsync(
                businessIdB, NotificationTypes.CustomerReplied, new CustomerRepliedArgs(),
                ActorType.NivoraSystem, conversationId: conversationIdB);
            notificationIdB = await db.Notifications.IgnoreQueryFilters()
                .Where(n => n.ConversationId == conversationIdB && n.Type == NotificationTypes.CustomerReplied)
                .Select(n => n.Id).SingleAsync();
        }

        // Business A reverses its own conversation — must have zero effect on Business B's notification.
        var response = await staffClientA.SendAsync(Authed(HttpMethod.Post, $"/api/conversations/{conversationIdA}/return-to-ai", cookieA));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var recipientB = await verifyDb.NotificationRecipients.IgnoreQueryFilters()
            .SingleAsync(r => r.NotificationId == notificationIdB && r.UserId == userIdB);
        Assert.False(recipientB.IsRead);
    }

    private async Task<int> CountNotificationsAsync(Guid businessId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Notifications.IgnoreQueryFilters().CountAsync(n => n.BusinessId == businessId);
    }

    // ── Customer/AI behavior ─────────────────────────────────────────

    [Fact]
    public async Task ReturnToAi_DoesNotItselfTriggerAnAiReply_NoNewMessageCreated()
    {
        using var anonClient = NewClient();
        using var staffClient = NewClient();
        var (businessId, cookie) = await CreateBusinessAndLoginAsync(anonClient, staffClient, "Return To AI No Immediate Reply Business");

        Guid conversationId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var widgetId = await SeedWidgetAsync(db, businessId);
            conversationId = await SeedHumanHandledConversationAsync(db, businessId, widgetId);
            await SeedMessageAsync(db, businessId, conversationId, MessageRole.Customer, "Are you still there?", DateTimeOffset.UtcNow);
        }

        var messageCountBefore = await CountMessagesAsync(conversationId);

        var response = await staffClient.SendAsync(Authed(HttpMethod.Post, $"/api/conversations/{conversationId}/return-to-ai", cookie));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // No IClaudeReplyService call site exists on this endpoint — proven
        // by absence, not by inspecting a mock: message count must be
        // unchanged (no Ai-role message appeared) after the reversal alone.
        var messageCountAfter = await CountMessagesAsync(conversationId);
        Assert.Equal(messageCountBefore, messageCountAfter);

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hasAiMessage = await verifyDb.Messages.IgnoreQueryFilters()
            .AnyAsync(m => m.ConversationId == conversationId && m.Role == MessageRole.Ai);
        Assert.False(hasAiMessage);

        // The pre-existing, still-unanswered customer message from the
        // Human-handled period remains in history, untouched.
        var stillPresent = await verifyDb.Messages.IgnoreQueryFilters()
            .AnyAsync(m => m.ConversationId == conversationId && m.Role == MessageRole.Customer && m.Content == "Are you still there?");
        Assert.True(stillPresent);
    }

    private async Task<int> CountMessagesAsync(Guid conversationId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Messages.IgnoreQueryFilters().CountAsync(m => m.ConversationId == conversationId);
    }

    /// <summary>Seeds a Widget with a known PublicKey and returns the full entity — the existing SeedWidgetAsync helper only returns the Id, which isn't enough to drive the real widget init/message HTTP flow these Step 15 tests need.</summary>
    private static async Task<Widget> SeedWidgetWithPublicKeyAsync(AppDbContext db, Guid businessId)
    {
        var widget = new Widget
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            Name = "Test Widget",
            PublicKey = Guid.NewGuid().ToString("N"),
            AllowedDomains = [],
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Widgets.Add(widget);
        await db.SaveChangesAsync();
        return widget;
    }

    private static async Task<(string Token, string VisitorSessionId)> InitWidgetAsync(HttpClient client, string publicKey, string visitorSessionId)
    {
        var response = await client.PostAsJsonAsync("/api/widget/init", new WidgetInitRequest(publicKey, visitorSessionId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<WidgetInitResponse>();
        Assert.NotNull(body);
        return (body!.Token, body.VisitorSessionId);
    }

    private static HttpRequestMessage WidgetBearer(HttpMethod method, string url, string token)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("Authorization", $"Bearer {token}");
        return request;
    }

    [Fact]
    public async Task ReturnToAi_ThenSubsequentCustomerMessage_TriggersNormalAiReply()
    {
        using var anonClient = NewClient();
        using var staffClient = NewClient();
        var (businessId, cookie) = await CreateBusinessAndLoginAsync(anonClient, staffClient, "Return To AI Then Customer Message Business");

        var visitorSessionId = Guid.NewGuid().ToString("N");
        Guid conversationId;
        Widget widget;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            widget = await SeedWidgetWithPublicKeyAsync(db, businessId);
            conversationId = await SeedHumanHandledConversationAsync(db, businessId, widget.Id, visitorSessionId);
            await SeedMessageAsync(db, businessId, conversationId, MessageRole.Customer, "Backlog message from the human-handled period", DateTimeOffset.UtcNow);
        }

        var reversal = await staffClient.SendAsync(Authed(HttpMethod.Post, $"/api/conversations/{conversationId}/return-to-ai", cookie));
        Assert.Equal(HttpStatusCode.NoContent, reversal.StatusCode);

        using var widgetClient = NewClient();
        var (token, _) = await InitWidgetAsync(widgetClient, widget.PublicKey, visitorSessionId);

        var messageResponse = await widgetClient.SendAsync(WithBody(
            WidgetBearer(HttpMethod.Post, "/api/widget/messages", token),
            new PostWidgetMessageRequest("Hello again after the reversal")));
        Assert.Equal(HttpStatusCode.OK, messageResponse.StatusCode);
        var body = await messageResponse.Content.ReadFromJsonAsync<WidgetMessageResponse>();
        Assert.NotNull(body);
        // The existing, unmodified customer-message -> ClaudeReplyService
        // pipeline generated a reply — proving the conversation really is
        // eligible for AI again, with zero new call sites added.
        Assert.NotNull(body!.AiReply);

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var messages = await verifyDb.Messages.IgnoreQueryFilters()
            .Where(m => m.ConversationId == conversationId)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync();

        // The Human-period backlog message is still present, in order,
        // never removed or rewritten by the reversal or the later reply.
        Assert.Contains(messages, m => m.Role == MessageRole.Customer && m.Content == "Backlog message from the human-handled period");
        Assert.Contains(messages, m => m.Role == MessageRole.Ai);
        // No synthetic customer-facing "returned to AI" system message of any kind was created.
        Assert.DoesNotContain(messages, m => m.Role != MessageRole.Customer && m.Role != MessageRole.Ai && m.Role != MessageRole.Agent);
    }

    // ── Concurrency ──────────────────────────────────────────────────

    private async Task<int> CountAdvisoryLockWaitersAsync(NpgsqlConnection barrierConnection, long lockKey)
    {
        var classId = unchecked((uint)(lockKey >> 32));
        var objId = unchecked((uint)lockKey);
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM pg_locks " +
            "WHERE locktype = 'advisory' AND NOT granted AND objsubid = 1 " +
            "AND classid::bigint = @classid AND objid::bigint = @objid",
            barrierConnection);
        cmd.Parameters.AddWithValue("classid", (long)classId);
        cmd.Parameters.AddWithValue("objid", (long)objId);
        return (int)(long)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// Deterministic wait-for-condition, not a fixed sleep — the barrier
    /// technique proven in the ae07e60 notification-concurrency fix. Fails
    /// loudly (not silently) if the expected number of real, distinct
    /// backends never becomes provably blocked on the exact advisory-lock
    /// key within the timeout, which is itself a meaningful signal that the
    /// concurrency protection under test regressed.
    /// </summary>
    private async Task WaitForAdvisoryLockWaitersAsync(NpgsqlConnection barrierConnection, long lockKey, int expected, string racName)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        int count;
        do
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail(
                    $"Expected {expected} concurrent advisory-lock waiters for the {racName} race within 10s, " +
                    "but they did not appear. If the concurrency protection under test has been removed or is " +
                    "no longer reached, this is the expected failure mode.");
            }

            count = await CountAdvisoryLockWaitersAsync(barrierConnection, lockKey);
            if (count < expected)
            {
                await Task.Delay(25);
            }
        } while (count < expected);
    }

    /// <summary>Opens the barrier connection and pre-acquires the given conversation's notification/HandledBy lock, holding it open until the caller disposes/rolls back the returned transaction.</summary>
    private async Task<(NpgsqlConnection Connection, NpgsqlTransaction Transaction)> AcquireBarrierAsync(long lockKey)
    {
        var connection = new NpgsqlConnection(_adoConnectionString);
        await connection.OpenAsync();
        var transaction = await connection.BeginTransactionAsync();
        await using (var lockCmd = new NpgsqlCommand($"SELECT pg_advisory_xact_lock({lockKey})", connection, transaction))
        {
            await lockCmd.ExecuteNonQueryAsync();
        }
        return (connection, transaction);
    }

    /// <summary>
    /// Race: reversal vs. staff reply — opposing HandledBy writes. Proves
    /// the outcome is deterministic and well-defined under genuine
    /// concurrency (both requests provably queued on the same advisory
    /// lock), not merely "both requests eventually returned 200": whichever
    /// side commits last determines the final HandledBy, but neither the
    /// Agent message nor the ConversationHandoff bookkeeping is ever lost,
    /// duplicated, or torn, in either possible outcome.
    ///
    /// Updated for Step 15's completion (CreateMessage now also writes its
    /// own Ai -> Human handoff row on a genuine transition, symmetric with
    /// ReturnToAi's Human -> Ai row): this conversation seeds Human, and
    /// only ReturnToAi ever writes Ai, so ReturnToAi's own handoff always
    /// fires exactly once regardless of ordering. The two orderings now
    /// produce two different, fully deterministic outcomes rather than one
    /// deterministic + one merely-bounded outcome: if the reply's
    /// lock-protected reload runs before ReturnToAi's transition has
    /// committed, it observes Human (no transition, no handoff of its own)
    /// and ReturnToAi — running after — is what determines the final Ai
    /// state (handoffCount 1); if the reply's reload instead runs after
    /// ReturnToAi already committed Human -> Ai, the reply correctly
    /// observes a genuine Ai -> Human transition and logs its own
    /// StaffManualTakeover row on top of ReturnToAi's already-committed one
    /// (handoffCount 2, final state Human).
    /// </summary>
    [Fact]
    public async Task ReturnToAi_ConcurrentWithStaffReply_ProducesDeterministicNonTornOutcome()
    {
        using var anonClient = NewClient();
        using var staffClient1 = NewClient();
        using var staffClient2 = NewClient();
        var (businessId, cookie) = await CreateBusinessAndLoginAsync(anonClient, staffClient1, "Return To AI Vs Staff Reply Business");

        Guid conversationId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var widgetId = await SeedWidgetAsync(db, businessId);
            conversationId = await SeedHumanHandledConversationAsync(db, businessId, widgetId);
        }

        var lockKey = AdvisoryLockKeyGenerator.ForConversationNotification(businessId, conversationId);
        var (barrierConnection, barrierTransaction) = await AcquireBarrierAsync(lockKey);
        await using var _bc = barrierConnection;
        await using var _bt = barrierTransaction;

        var reversalTask = staffClient1.SendAsync(Authed(HttpMethod.Post, $"/api/conversations/{conversationId}/return-to-ai", cookie));
        var replyTask = staffClient2.SendAsync(WithBody(
            Authed(HttpMethod.Post, $"/api/conversations/{conversationId}/messages", cookie),
            new CreateConversationMessageRequest("Racing staff reply")));

        await WaitForAdvisoryLockWaitersAsync(barrierConnection, lockKey, 2, "reversal-vs-staff-reply");
        await barrierTransaction.RollbackAsync();

        await Task.WhenAll(reversalTask, replyTask);
        Assert.Equal(HttpStatusCode.NoContent, (await reversalTask).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await replyTask).StatusCode);

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();

        // The staff reply's Agent message is always persisted exactly once,
        // regardless of which side won the lock race.
        var agentMessageCount = await verifyDb.Messages.IgnoreQueryFilters()
            .CountAsync(m => m.ConversationId == conversationId && m.Role == MessageRole.Agent && m.Content == "Racing staff reply");
        Assert.Equal(1, agentMessageCount);

        var conversation = await verifyDb.Conversations.IgnoreQueryFilters().SingleAsync(c => c.Id == conversationId);
        var handoffCount = await verifyDb.ConversationHandoffs.IgnoreQueryFilters().CountAsync(h => h.ConversationId == conversationId);

        if (conversation.HandledBy == HandledBy.Ai)
        {
            // Only ReturnToAi ever writes Ai, and only when it read Human —
            // it must have run, and run exactly once to completion, after
            // the staff reply (whose own write is unconditional Human).
            Assert.Equal(1, handoffCount);
        }
        else
        {
            Assert.Equal(HandledBy.Human, conversation.HandledBy);
            // The reply committed last in this ordering, after ReturnToAi's
            // own Human -> Ai transition already committed — the reply's
            // lock-protected reload therefore correctly observed Ai and
            // logged its own genuine Ai -> Human handoff on top of
            // ReturnToAi's already-committed one. Two real, distinct
            // transitions happened in sequence; two rows is the correct,
            // non-duplicated audit trail for that — not a bug.
            Assert.Equal(2, handoffCount);
        }
    }

    /// <summary>
    /// Race: two simultaneous reversals. Proves HandledBy always ends Ai
    /// and exactly one ConversationHandoff row is written — never two —
    /// regardless of which request's lock acquisition actually wins.
    /// </summary>
    [Fact]
    public async Task ReturnToAi_TwoSimultaneousReversals_EndsAi_ExactlyOneHandoffRow()
    {
        using var anonClient = NewClient();
        using var staffClient1 = NewClient();
        using var staffClient2 = NewClient();
        var (businessId, cookie) = await CreateBusinessAndLoginAsync(anonClient, staffClient1, "Return To AI Concurrent Reversals Business");

        Guid conversationId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var widgetId = await SeedWidgetAsync(db, businessId);
            conversationId = await SeedHumanHandledConversationAsync(db, businessId, widgetId);
        }

        var lockKey = AdvisoryLockKeyGenerator.ForConversationNotification(businessId, conversationId);
        var (barrierConnection, barrierTransaction) = await AcquireBarrierAsync(lockKey);
        await using var _bc = barrierConnection;
        await using var _bt = barrierTransaction;

        var task1 = staffClient1.SendAsync(Authed(HttpMethod.Post, $"/api/conversations/{conversationId}/return-to-ai", cookie));
        var task2 = staffClient2.SendAsync(Authed(HttpMethod.Post, $"/api/conversations/{conversationId}/return-to-ai", cookie));

        await WaitForAdvisoryLockWaitersAsync(barrierConnection, lockKey, 2, "two-simultaneous-reversals");
        await barrierTransaction.RollbackAsync();

        await Task.WhenAll(task1, task2);
        Assert.Equal(HttpStatusCode.NoContent, (await task1).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await task2).StatusCode);

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var conversation = await verifyDb.Conversations.IgnoreQueryFilters().SingleAsync(c => c.Id == conversationId);
        Assert.Equal(HandledBy.Ai, conversation.HandledBy);

        var handoffCount = await verifyDb.ConversationHandoffs.IgnoreQueryFilters().CountAsync(h => h.ConversationId == conversationId);
        Assert.Equal(1, handoffCount);
    }

    /// <summary>
    /// Race: reversal vs. a concurrent customer message on the same
    /// conversation (which itself races to create a CustomerReplied
    /// notification). Proves the two named invariants together: (a) no
    /// unread CustomerReplied notification ever survives past both
    /// operations completing, regardless of which side's lock acquisition
    /// wins — either the notification-creation check correctly sees the
    /// reversal already happened (WidgetConversationService's Step 15
    /// re-check) and never creates one, or it creates one and ReturnToAi's
    /// own resolve step immediately cleans it up; (b) HandledBy correctly
    /// ends Ai and the customer message is persisted exactly once, with no
    /// duplication or loss from the race. This is the combined "reversal
    /// vs customer-message processing" and "reversal vs notification
    /// creation/resolution" race the task specification calls out
    /// separately — both collapse to the same lock-guarded critical
    /// section in this architecture, so one deterministic test proves both.
    /// </summary>
    [Fact]
    public async Task ReturnToAi_ConcurrentWithCustomerMessage_NeverLeavesAnUnreadNotification()
    {
        using var anonClient = NewClient();
        using var staffClient = NewClient();
        var (businessId, cookie) = await CreateBusinessAndLoginAsync(anonClient, staffClient, "Return To AI Vs Customer Message Business");

        var visitorSessionId = Guid.NewGuid().ToString("N");
        Guid conversationId;
        Widget widget;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            widget = await SeedWidgetWithPublicKeyAsync(db, businessId);
            var user = await db.Users.IgnoreQueryFilters().SingleAsync(u => u.BusinessId == businessId);
            user.IsActive = true; // Owner is active by default; kept explicit since NotifyStaffOfCustomerReplyAsync requires it.
            conversationId = await SeedHumanHandledConversationAsync(db, businessId, widget.Id, visitorSessionId);
            await db.SaveChangesAsync();
        }

        using var widgetClient = NewClient();
        var (token, _) = await InitWidgetAsync(widgetClient, widget.PublicKey, visitorSessionId);

        var lockKey = AdvisoryLockKeyGenerator.ForConversationNotification(businessId, conversationId);
        var (barrierConnection, barrierTransaction) = await AcquireBarrierAsync(lockKey);
        await using var _bc = barrierConnection;
        await using var _bt = barrierTransaction;

        var reversalTask = staffClient.SendAsync(Authed(HttpMethod.Post, $"/api/conversations/{conversationId}/return-to-ai", cookie));
        var customerMessageTask = widgetClient.SendAsync(WithBody(
            WidgetBearer(HttpMethod.Post, "/api/widget/messages", token),
            new PostWidgetMessageRequest("Racing customer message")));

        await WaitForAdvisoryLockWaitersAsync(barrierConnection, lockKey, 2, "reversal-vs-customer-message");
        await barrierTransaction.RollbackAsync();

        await Task.WhenAll(reversalTask, customerMessageTask);
        Assert.Equal(HttpStatusCode.NoContent, (await reversalTask).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await customerMessageTask).StatusCode);

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();

        var conversation = await verifyDb.Conversations.IgnoreQueryFilters().SingleAsync(c => c.Id == conversationId);
        Assert.Equal(HandledBy.Ai, conversation.HandledBy); // only ReturnToAi writes HandledBy in this race — always wins eventually

        var customerMessageCount = await verifyDb.Messages.IgnoreQueryFilters()
            .CountAsync(m => m.ConversationId == conversationId && m.Role == MessageRole.Customer && m.Content == "Racing customer message");
        Assert.Equal(1, customerMessageCount); // persisted exactly once, regardless of ordering

        // The invariant under test: never an unread CustomerReplied
        // notification left behind, whichever side won the lock race.
        var unreadCount = await verifyDb.NotificationRecipients.IgnoreQueryFilters()
            .Join(verifyDb.Notifications.IgnoreQueryFilters(), r => r.NotificationId, n => n.Id, (r, n) => new { r, n })
            .CountAsync(x => x.n.ConversationId == conversationId
                && x.n.Type == NotificationTypes.CustomerReplied
                && !x.r.IsRead);
        Assert.Equal(0, unreadCount);

        // Dedup still holds under this new race too — never more than one
        // CustomerReplied notification for this conversation.
        var totalCustomerRepliedCount = await verifyDb.Notifications.IgnoreQueryFilters()
            .CountAsync(n => n.ConversationId == conversationId && n.Type == NotificationTypes.CustomerReplied);
        Assert.True(totalCustomerRepliedCount is 0 or 1);
    }

    // ── Step 16: FirstRespondedAt integration ────────────────────────────

    private static async Task<Guid> SeedTicketForFirstRespondedAsync(
        AppDbContext db, Guid businessId, Guid conversationId, TicketStatus status, DateTimeOffset createdAt)
    {
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            Email = "ticket-customer@example.com",
            Name = "Ticket Customer",
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        };
        db.Customers.Add(customer);

        var ticket = new Ticket
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            CustomerId = customer.Id,
            ConversationId = conversationId,
            Status = status,
            Priority = TicketPriority.Low,
            PriorityReason = TicketPriorityReasons.ManualEscalation,
            Channel = "Website",
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        };
        db.Tickets.Add(ticket);
        await db.SaveChangesAsync();
        return ticket.Id;
    }

    [Fact]
    public async Task CreateMessage_FirstAgentReply_SetsFirstRespondedAt_OnOpenTicket()
    {
        using var anonClient = NewClient();
        using var staffClient = NewClient();
        var (businessId, cookie) = await CreateBusinessAndLoginAsync(anonClient, staffClient, "FirstResponded Business");

        Guid conversationId, ticketId;
        var ticketCreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var widgetId = await SeedWidgetAsync(db, businessId);
            conversationId = await SeedConversationAsync(db, businessId, widgetId, ticketCreatedAt, ticketCreatedAt);
            ticketId = await SeedTicketForFirstRespondedAsync(db, businessId, conversationId, TicketStatus.Open, ticketCreatedAt);
        }

        var reply = await staffClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, $"/api/conversations/{conversationId}/messages", cookie),
            new CreateConversationMessageRequest("First human response.")));
        Assert.Equal(HttpStatusCode.OK, reply.StatusCode);

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ticket = await verifyDb.Tickets.IgnoreQueryFilters().SingleAsync(t => t.Id == ticketId);
        Assert.NotNull(ticket.FirstRespondedAt);
        Assert.True(ticket.FirstRespondedAt >= ticket.CreatedAt);
    }

    [Fact]
    public async Task CreateMessage_SecondAgentReply_DoesNotReplaceAlreadySetFirstRespondedAt()
    {
        using var anonClient = NewClient();
        using var staffClient = NewClient();
        var (businessId, cookie) = await CreateBusinessAndLoginAsync(anonClient, staffClient, "FirstResponded Second Reply Business");

        Guid conversationId, ticketId;
        var ticketCreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var widgetId = await SeedWidgetAsync(db, businessId);
            conversationId = await SeedConversationAsync(db, businessId, widgetId, ticketCreatedAt, ticketCreatedAt);
            ticketId = await SeedTicketForFirstRespondedAsync(db, businessId, conversationId, TicketStatus.Open, ticketCreatedAt);
        }

        var first = await staffClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, $"/api/conversations/{conversationId}/messages", cookie),
            new CreateConversationMessageRequest("First human response.")));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        DateTimeOffset? firstRespondedAtAfterFirstReply;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            firstRespondedAtAfterFirstReply = await db.Tickets.IgnoreQueryFilters()
                .Where(t => t.Id == ticketId).Select(t => t.FirstRespondedAt).SingleAsync();
        }
        Assert.NotNull(firstRespondedAtAfterFirstReply);

        var second = await staffClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, $"/api/conversations/{conversationId}/messages", cookie),
            new CreateConversationMessageRequest("Second human response.")));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ticket = await verifyDb.Tickets.IgnoreQueryFilters().SingleAsync(t => t.Id == ticketId);
        Assert.Equal(firstRespondedAtAfterFirstReply, ticket.FirstRespondedAt); // unchanged by the second reply
    }

    [Fact]
    public async Task CreateMessage_ResolvedTicket_IsIgnored_FirstRespondedAtNotSet()
    {
        using var anonClient = NewClient();
        using var staffClient = NewClient();
        var (businessId, cookie) = await CreateBusinessAndLoginAsync(anonClient, staffClient, "FirstResponded Resolved Ticket Business");

        Guid conversationId, ticketId;
        var ticketCreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var widgetId = await SeedWidgetAsync(db, businessId);
            conversationId = await SeedConversationAsync(db, businessId, widgetId, ticketCreatedAt, ticketCreatedAt);
            // Already Resolved — must be ignored by the FirstRespondedAt
            // lookup (Status != Resolved), same as it's excluded from the
            // active-ticket-per-conversation invariant.
            ticketId = await SeedTicketForFirstRespondedAsync(db, businessId, conversationId, TicketStatus.Resolved, ticketCreatedAt);
        }

        var reply = await staffClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, $"/api/conversations/{conversationId}/messages", cookie),
            new CreateConversationMessageRequest("Reply after resolution.")));
        Assert.Equal(HttpStatusCode.OK, reply.StatusCode);

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ticket = await verifyDb.Tickets.IgnoreQueryFilters().SingleAsync(t => t.Id == ticketId);
        Assert.Null(ticket.FirstRespondedAt);
    }

    [Fact]
    public async Task CreateMessage_NoActiveTicket_DoesNotThrow_NoTicketAffected()
    {
        using var anonClient = NewClient();
        using var staffClient = NewClient();
        var (businessId, cookie) = await CreateBusinessAndLoginAsync(anonClient, staffClient, "FirstResponded No Ticket Business");

        Guid conversationId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var widgetId = await SeedWidgetAsync(db, businessId);
            conversationId = await SeedConversationAsync(db, businessId, widgetId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        }

        var reply = await staffClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, $"/api/conversations/{conversationId}/messages", cookie),
            new CreateConversationMessageRequest("No ticket exists for this conversation.")));
        Assert.Equal(HttpStatusCode.OK, reply.StatusCode); // no exception from the missing-ticket lookup
    }

    /// <summary>
    /// AI-generated replies must never set FirstRespondedAt (the single
    /// most important non-obvious rule in the Ticket schema, per
    /// NIVORA_SCHEMA_AND_LOGIC_REFERENCE.md's own wording) — proven here
    /// through the real widget → ClaudeReplyService pipeline (not by
    /// seeding an Ai-role message directly, which would bypass the code
    /// path entirely), confirming ClaudeReplyService's own message-
    /// creation path — independently verified to contain zero Ticket
    /// references anywhere — never touches this ticket.
    /// </summary>
    [Fact]
    public async Task WidgetAiReply_NeverSetsFirstRespondedAt()
    {
        using var anonClient = NewClient();
        using var staffClient = NewClient();
        var (businessId, cookie) = await CreateBusinessAndLoginAsync(anonClient, staffClient, "FirstResponded AI Reply Business");

        var visitorSessionId = Guid.NewGuid().ToString("N");
        Widget widget;
        Guid conversationId, ticketId;
        var ticketCreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            widget = await SeedWidgetWithPublicKeyAsync(db, businessId);

            // Built directly (not via SeedConversationAsync, which has no
            // VisitorSessionId parameter) so WidgetConversationService's
            // lazy-conversation-creation finds this existing, still-Open,
            // still-Ai-handled conversation for this exact
            // (BusinessId, WidgetId, VisitorSessionId) tuple instead of
            // creating a fresh one this test couldn't attach a Ticket to
            // in advance.
            var conversation = new Conversation
            {
                Id = Guid.NewGuid(),
                BusinessId = businessId,
                WidgetId = widget.Id,
                VisitorSessionId = visitorSessionId,
                Channel = "Website",
                Status = ConversationStatus.Open,
                HandledBy = HandledBy.Ai,
                CreatedAt = ticketCreatedAt,
                UpdatedAt = ticketCreatedAt,
            };
            db.Conversations.Add(conversation);
            await db.SaveChangesAsync();
            conversationId = conversation.Id;

            ticketId = await SeedTicketForFirstRespondedAsync(db, businessId, conversationId, TicketStatus.Open, ticketCreatedAt);
        }

        using var widgetClient = NewClient();
        var (token, _) = await InitWidgetAsync(widgetClient, widget.PublicKey, visitorSessionId);

        var response = await widgetClient.SendAsync(WithBody(
            WidgetBearer(HttpMethod.Post, "/api/widget/messages", token),
            new PostWidgetMessageRequest("Customer message that will get an AI reply")));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Confirm an Ai-role message really was persisted for this
        // conversation — otherwise this test would trivially, vacuously
        // pass without ever exercising the AI reply path at all.
        var hasAiMessage = await verifyDb.Messages.IgnoreQueryFilters()
            .AnyAsync(m => m.ConversationId == conversationId && m.Role == MessageRole.Ai);
        Assert.True(hasAiMessage, "Expected the fake AI reply to have been persisted — otherwise this test proves nothing.");

        var ticket = await verifyDb.Tickets.IgnoreQueryFilters().SingleAsync(t => t.Id == ticketId);
        Assert.Null(ticket.FirstRespondedAt);
    }

    [Fact]
    public async Task CreateMessage_FirstAgentReply_TenantIsolation_OnlySameTenantTicketAffected()
    {
        using var anonClientA = NewClient();
        using var staffClientA = NewClient();
        var (businessIdA, cookieA) = await CreateBusinessAndLoginAsync(anonClientA, staffClientA, "FirstResponded Tenant A");

        using var anonClientB = NewClient();
        using var staffClientB = NewClient();
        var (businessIdB, cookieB) = await CreateBusinessAndLoginAsync(anonClientB, staffClientB, "FirstResponded Tenant B");

        Guid conversationIdA, ticketIdA, conversationIdB, ticketIdB;
        var createdAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var widgetIdA = await SeedWidgetAsync(db, businessIdA);
            conversationIdA = await SeedConversationAsync(db, businessIdA, widgetIdA, createdAt, createdAt);
            ticketIdA = await SeedTicketForFirstRespondedAsync(db, businessIdA, conversationIdA, TicketStatus.Open, createdAt);

            var widgetIdB = await SeedWidgetAsync(db, businessIdB);
            conversationIdB = await SeedConversationAsync(db, businessIdB, widgetIdB, createdAt, createdAt);
            ticketIdB = await SeedTicketForFirstRespondedAsync(db, businessIdB, conversationIdB, TicketStatus.Open, createdAt);
        }

        var reply = await staffClientA.SendAsync(WithBody(
            Authed(HttpMethod.Post, $"/api/conversations/{conversationIdA}/messages", cookieA),
            new CreateConversationMessageRequest("Business A's own reply.")));
        Assert.Equal(HttpStatusCode.OK, reply.StatusCode);

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ticketA = await verifyDb.Tickets.IgnoreQueryFilters().SingleAsync(t => t.Id == ticketIdA);
        Assert.NotNull(ticketA.FirstRespondedAt);

        var ticketB = await verifyDb.Tickets.IgnoreQueryFilters().SingleAsync(t => t.Id == ticketIdB);
        Assert.Null(ticketB.FirstRespondedAt); // Business B's ticket must never be touched by Business A's reply
    }
}
