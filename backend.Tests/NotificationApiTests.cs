using System.Net;
using System.Net.Http.Json;
using backend.Auth;
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
/// HTTP-level tests for NotificationsController, run against the real app +
/// real Neon dev DB via NivoraApiFactory — same pattern as
/// CrossTenantIsolationTests. Notifications are seeded directly through
/// AppDbContext/INotificationService inside the DI container (there is no
/// HTTP endpoint that creates one — no live trigger exists), then read back
/// through the real controller endpoints.
///
/// NOTE: requires the AddNotificationSystem migration to be applied —
/// otherwise every test here fails with Postgres 42P01, expected and
/// reported as such.
/// </summary>
public class NotificationApiTests : IAsyncLifetime
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

    /// <summary>Seeds a second staff user directly into an existing business — same pattern CrossTenantIsolationTests uses for its Agent-role case, since /api/auth/register always creates a brand-new business.</summary>
    private async Task<ApplicationUser> SeedStaffIntoBusinessAsync(Guid businessId, string email)
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
        var roleResult = await userManager.AddToRoleAsync(user, RoleNames.Agent);
        Assert.True(roleResult.Succeeded, string.Join("; ", roleResult.Errors.Select(e => e.Description)));
        return user;
    }

    private static HttpRequestMessage Authed(HttpMethod method, string url, string cookie)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("Cookie", cookie);
        return request;
    }

    /// <summary>
    /// Test-only: Postgres's timestamptz column stores microsecond precision,
    /// one fractional digit less than .NET's 100-nanosecond tick precision —
    /// a raw DateTimeOffset.UtcNow write-then-read round trip through the
    /// real DB silently drops that 7th digit. Any test that captures a
    /// timestamp in memory and later asserts exact equality against the same
    /// value after a real Postgres round trip must truncate it first, so
    /// both sides are already identical rather than comparing with a
    /// tolerance or re-fetching to paper over the mismatch. 1 microsecond =
    /// 10 ticks, so truncating to the nearest 10-tick boundary is exactly
    /// truncating to microsecond precision.
    /// </summary>
    private static DateTimeOffset TruncateToMicroseconds(DateTimeOffset value) =>
        new(value.Ticks - (value.Ticks % 10), value.Offset);

    /// <summary>Directly inserts one Notification + one NotificationRecipient row for a single recipient, with a caller-controlled CreatedAt/recipient Id — used where the tests need precise, deterministic ordering that NotifyAsync's own DateTimeOffset.UtcNow capture doesn't allow.</summary>
    private static async Task<Guid> SeedRecipientRowAsync(
        AppDbContext db, Guid businessId, Guid userId, DateTimeOffset createdAt, Guid recipientId)
    {
        var notification = new Notification
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            Category = NotificationCategory.Team,
            Type = "TeamMemberJoined",
            Title = "Test notification",
            ActorType = ActorType.NivoraSystem,
            CreatedAt = createdAt,
        };
        db.Notifications.Add(notification);
        db.NotificationRecipients.Add(new NotificationRecipient
        {
            Id = recipientId,
            BusinessId = businessId,
            NotificationId = notification.Id,
            UserId = userId,
            IsRead = false,
            CreatedAt = createdAt,
        });
        await db.SaveChangesAsync();
        return recipientId;
    }

    // ── Test 7 — cross-user isolation ───────────────────────────────

    [Fact]
    public async Task CrossUserIsolation_UserBCannotReadCountOrMarkUserAsNotification()
    {
        var emailA = $"staffA-{Guid.NewGuid():N}@example.com";
        using var anonClient = NewClient();
        var (registerResponse, registerBody) = await RegisterAsync(anonClient, "Notif Business", emailA);
        Assert.Equal(HttpStatusCode.OK, registerResponse.StatusCode);
        Assert.NotNull(registerBody);
        _businessIdsToClean.Add(registerBody!.BusinessId);
        await ConfirmEmailAsync(anonClient, registerBody.UserId);

        var emailB = $"staffB-{Guid.NewGuid():N}@example.com";
        var userB = await SeedStaffIntoBusinessAsync(registerBody.BusinessId, emailB);

        using var clientA = NewClient();
        var (loginAResponse, _, cookieA) = await LoginAsync(clientA, emailA);
        Assert.Equal(HttpStatusCode.OK, loginAResponse.StatusCode);

        using var clientB = NewClient();
        var (loginBResponse, _, cookieB) = await LoginAsync(clientB, emailB);
        Assert.Equal(HttpStatusCode.OK, loginBResponse.StatusCode);

        // Notification targeted at A only.
        Guid recipientIdA;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            recipientIdA = await SeedRecipientRowAsync(
                db, registerBody.BusinessId, registerBody.UserId, DateTimeOffset.UtcNow, Guid.NewGuid());
        }

        // B: same business, but not the recipient — must see nothing.
        var listB = await clientB.SendAsync(Authed(HttpMethod.Get, "/api/notifications", cookieB!));
        Assert.Equal(HttpStatusCode.OK, listB.StatusCode);
        var listBBody = await listB.Content.ReadFromJsonAsync<NotificationListResponse>();
        Assert.NotNull(listBBody);
        Assert.DoesNotContain(listBBody!.Notifications, n => n.Id == recipientIdA);

        var countB = await clientB.SendAsync(Authed(HttpMethod.Get, "/api/notifications/unread-count", cookieB!));
        var countBBody = await countB.Content.ReadFromJsonAsync<UnreadCountResponse>();
        Assert.Equal(0, countBBody!.Count);

        // B attempts to mark A's notification read — must not succeed (404, collapsed with "doesn't exist").
        var markB = await clientB.SendAsync(Authed(HttpMethod.Post, $"/api/notifications/{recipientIdA}/read", cookieB!));
        Assert.Equal(HttpStatusCode.NotFound, markB.StatusCode);

        // Positive control: A can see and mark their own.
        var listA = await clientA.SendAsync(Authed(HttpMethod.Get, "/api/notifications", cookieA!));
        var listABody = await listA.Content.ReadFromJsonAsync<NotificationListResponse>();
        Assert.Contains(listABody!.Notifications, n => n.Id == recipientIdA);

        var markA = await clientA.SendAsync(Authed(HttpMethod.Post, $"/api/notifications/{recipientIdA}/read", cookieA!));
        Assert.Equal(HttpStatusCode.NoContent, markA.StatusCode);

        // Confirm B's failed attempt truly left A's row untouched in state (not just returned 404 while secretly succeeding).
        await using (var verifyScope = _factory.Services.CreateAsyncScope())
        {
            var db = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var recipient = await db.NotificationRecipients.IgnoreQueryFilters().SingleAsync(r => r.Id == recipientIdA);
            Assert.True(recipient.IsRead); // read by A's own successful call above, not by B's rejected attempt
        }
    }

    // ── Test 10 — read-all UUID tie protection ──────────────────────

    [Fact]
    public async Task ReadAll_ProtectsSameCreatedAtTieBrokenByRealPostgresUuidOrdering()
    {
        using var anonClient = NewClient();
        var email = $"staff-{Guid.NewGuid():N}@example.com";
        var (registerResponse, registerBody) = await RegisterAsync(anonClient, "Notif Tie Business", email);
        Assert.Equal(HttpStatusCode.OK, registerResponse.StatusCode);
        _businessIdsToClean.Add(registerBody!.BusinessId);
        await ConfirmEmailAsync(anonClient, registerBody.UserId);

        using var client = NewClient();
        var (loginResponse, _, cookie) = await LoginAsync(client, email);
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);

        var sharedCreatedAt = TruncateToMicroseconds(DateTimeOffset.UtcNow);

        // Candidate ordering, not Guid.NewGuid(): chosen so the first byte
        // group differs maximally (0x00... vs 0xff...) — verified for real
        // against Postgres below, not just assumed.
        var firstId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var cursorId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var afterCursorId = Guid.Parse("ffffffff-0000-0000-0000-000000000003");

        // .NET's Guid.CompareTo does NOT use the same byte layout Postgres
        // uses for its uuid type (Postgres compares the canonical
        // string-form byte order; .NET's internal Guid layout reverses the
        // first three field groups) — they are not guaranteed to agree, so
        // this is verified against the real database, not assumed from
        // .NET ordering.
        var netOrderingSaysAfterCursorIsGreater = afterCursorId.CompareTo(cursorId) > 0;

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var postgresOrderingSaysAfterCursorIsGreater = await db.Database
                .SqlQuery<bool>($"SELECT ({afterCursorId}::uuid > {cursorId}::uuid) AS \"Value\"")
                .SingleAsync();

            Assert.True(
                postgresOrderingSaysAfterCursorIsGreater,
                $"Chosen afterCursorId ({afterCursorId}) must sort after cursorId ({cursorId}) under PostgreSQL's " +
                $"real uuid ordering for this test's premise to hold — Postgres said it does not. " +
                $"(.NET Guid.CompareTo agreement: {netOrderingSaysAfterCursorIsGreater})");

            await SeedRecipientRowAsync(db, registerBody.BusinessId, registerBody.UserId, sharedCreatedAt, firstId);
            await SeedRecipientRowAsync(db, registerBody.BusinessId, registerBody.UserId, sharedCreatedAt, cursorId);
        }

        // Capture readAllUpTo BEFORE the third row exists — proves the
        // computed cursor is a real snapshot, not recomputed at apply time.
        var listResponse = await client.SendAsync(Authed(HttpMethod.Get, "/api/notifications", cookie!));
        var listBody = await listResponse.Content.ReadFromJsonAsync<NotificationListResponse>();
        Assert.NotNull(listBody!.ReadAllUpTo);
        Assert.Equal(sharedCreatedAt, listBody.ReadAllUpTo!.CreatedAt);
        Assert.Equal(cursorId, listBody.ReadAllUpTo.Id);

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await SeedRecipientRowAsync(db, registerBody.BusinessId, registerBody.UserId, sharedCreatedAt, afterCursorId);
        }

        var readAllResponse = await client.SendAsync(WithBody(Authed(HttpMethod.Post, "/api/notifications/read-all", cookie!), listBody.ReadAllUpTo));
        Assert.Equal(HttpStatusCode.NoContent, readAllResponse.StatusCode);

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.True((await db.NotificationRecipients.IgnoreQueryFilters().SingleAsync(r => r.Id == firstId)).IsRead);
            Assert.True((await db.NotificationRecipients.IgnoreQueryFilters().SingleAsync(r => r.Id == cursorId)).IsRead);
            Assert.False((await db.NotificationRecipients.IgnoreQueryFilters().SingleAsync(r => r.Id == afterCursorId)).IsRead);
        }
    }

    // ── Test 11 — pagination independence ───────────────────────────

    [Fact]
    public async Task ReadAllUpTo_ReflectsNewestUnreadAcrossAllRows_NotJustCurrentPage()
    {
        using var anonClient = NewClient();
        var email = $"staff-{Guid.NewGuid():N}@example.com";
        var (registerResponse, registerBody) = await RegisterAsync(anonClient, "Notif Pagination Business", email);
        Assert.Equal(HttpStatusCode.OK, registerResponse.StatusCode);
        _businessIdsToClean.Add(registerBody!.BusinessId);
        await ConfirmEmailAsync(anonClient, registerBody.UserId);

        using var client = NewClient();
        var (loginResponse, _, cookie) = await LoginAsync(client, email);
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);

        var baseTime = TruncateToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(-10));
        Guid newestId = Guid.Empty;
        var newestCreatedAt = baseTime;

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            for (var i = 0; i < 5; i++)
            {
                var createdAt = baseTime.AddMinutes(i);
                var id = Guid.NewGuid();
                await SeedRecipientRowAsync(db, registerBody.BusinessId, registerBody.UserId, createdAt, id);
                if (createdAt > newestCreatedAt || newestId == Guid.Empty)
                {
                    newestCreatedAt = createdAt;
                    newestId = id;
                }
            }
        }

        // Fetch an OLDER page — take=2, skipping the 3 newest via a cursor
        // that starts just after the oldest two.
        var firstPage = await client.SendAsync(Authed(HttpMethod.Get, "/api/notifications?take=2", cookie!));
        var firstPageBody = await firstPage.Content.ReadFromJsonAsync<NotificationListResponse>();
        Assert.NotNull(firstPageBody!.NextCursor);

        var olderPageUrl = $"/api/notifications?take=2&beforeCreatedAt={Uri.EscapeDataString(firstPageBody.NextCursor!.CreatedAt.ToString("O"))}&beforeId={firstPageBody.NextCursor.Id}";
        var olderPage = await client.SendAsync(Authed(HttpMethod.Get, olderPageUrl, cookie!));
        var olderPageBody = await olderPage.Content.ReadFromJsonAsync<NotificationListResponse>();

        Assert.NotNull(olderPageBody!.ReadAllUpTo);
        Assert.Equal(newestCreatedAt, olderPageBody.ReadAllUpTo!.CreatedAt);
        Assert.Equal(newestId, olderPageBody.ReadAllUpTo.Id);

        // Sanity: the older page's own items are indeed the older ones, not the newest.
        Assert.DoesNotContain(olderPageBody.Notifications, n => n.Id == newestId);
    }

    private static HttpRequestMessage WithBody<T>(HttpRequestMessage request, T body)
    {
        request.Content = JsonContent.Create(body);
        return request;
    }
}
