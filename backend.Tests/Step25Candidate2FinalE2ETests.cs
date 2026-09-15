using System.Net;
using System.Net.Http.Json;
using backend.Auth;
using backend.Constants;
using backend.Controllers;
using backend.Data;
using backend.Enums;
using backend.Interfaces;
using backend.Models;
using backend.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace backend.Tests;

/// <summary>
/// Step 25 Candidate 2 — Final E2E verification. Not a new implementation
/// phase: proves Phases 1-4, each already independently designed/
/// implemented/verified, work together as one coherent pipeline against the
/// real, non-faked path — real registration, the real
/// PUT /api/business/conversation-auto-close and
/// PUT /api/business/conversation-inactivity-close-days endpoints (Phase 1),
/// the real DataLifecycleBackgroundService/ConversationInactivityCloseService
/// (Phases 2-4), run through the real, Program.cs-registered host
/// (NivoraApiFactory). No Conversation.Status transition or AuditLog row in
/// this file is ever hand-constructed to look like a close — every one is
/// produced exclusively by the real pipeline, exactly as production does it.
///
/// The production 24-hour PeriodicTimer interval is never shortened —
/// DataLifecycleBackgroundService has no TimeProvider seam (unlike Step 24's
/// ScheduledReportBackgroundService), so every test here relies on the same
/// property every other Step 25 Phase 2/4 test already relies on: the real
/// hosted service's first tick runs immediately on host start, before any
/// timer wait.
/// </summary>
[Collection("ConversationAutoClose")]
public class Step25Candidate2FinalE2ETests : IAsyncLifetime
{
    private readonly List<Guid> _businessIdsToClean = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_businessIdsToClean.Count == 0)
        {
            return;
        }

        await using var db = DbFor();
        var ids = _businessIdsToClean.ToArray();

        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"AuditLogs\" WHERE \"BusinessId\" = ANY({ids})");
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Tickets\" WHERE \"BusinessId\" = ANY({ids})");
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Conversations\" WHERE \"BusinessId\" = ANY({ids})");
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Customers\" WHERE \"BusinessId\" = ANY({ids})");
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Widgets\" WHERE \"BusinessId\" = ANY({ids})");
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"AspNetUserRoles\" WHERE \"UserId\" IN (SELECT \"Id\" FROM \"AspNetUsers\" WHERE \"BusinessId\" = ANY({ids}))");
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"AspNetUsers\" WHERE \"BusinessId\" = ANY({ids})");
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Businesses\" WHERE \"Id\" = ANY({ids})");
    }

    // ── Infra helpers ─────────────────────────────────────────────────────

    private static AppDbContext DbFor() =>
        new ServiceCollection()
            .AddSingleton<ICurrentTenantAccessor>(new FixedTenantAccessor(null))
            .AddDbContext<AppDbContext>(o => o.UseNpgsql(
                PostgresConnectionString.FromUri(new ConfigurationBuilder()
                    .AddUserSecrets<Program>().Build()
                    .GetConnectionString("DefaultConnection")!), x => x.UseVector()))
            .BuildServiceProvider()
            .GetRequiredService<AppDbContext>();

    private sealed class FixedTenantAccessor(Guid? businessId) : ICurrentTenantAccessor
    {
        public Guid? BusinessId => businessId;
    }

    private const string BusinessTimeZone = "Europe/Copenhagen";

    private static HttpClient NewClient(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });

    private static async Task<(HttpResponseMessage Response, RegisterResponse? Body)> RegisterAsync(
        HttpClient client, string businessName, string email, string password = "Passw0rd!!")
    {
        var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(businessName, BusinessTimeZone, email, password));
        var body = response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<RegisterResponse>() : null;
        return (response, body);
    }

    private static async Task ConfirmEmailAsync(WebApplicationFactory<Program> factory, HttpClient client, Guid userId)
    {
        string token;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await userManager.FindByIdAsync(userId.ToString());
            Assert.NotNull(user);
            token = await userManager.GenerateEmailConfirmationTokenAsync(user!);
        }

        var response = await client.PostAsJsonAsync("/api/auth/confirm-email", new ConfirmEmailRequest(userId, token));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static async Task<string> LoginAsync(WebApplicationFactory<Program> factory, string email, string password = "Passw0rd!!")
    {
        var client = NewClient(factory);
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.First().Split(';')[0]
            : throw new InvalidOperationException("No Set-Cookie header on a successful login response.");
    }

    /// <summary>The real production path for creating a Business + its sole
    /// Owner: POST /api/auth/register, confirmed, logged in. Never a raw
    /// SQL/EF insert of either row.</summary>
    private async Task<(Guid BusinessId, string OwnerCookie)> RegisterBusinessWithOwnerAsync(
        WebApplicationFactory<Program> factory, string businessName)
    {
        var email = $"e2e-owner-{Guid.NewGuid():N}@example.com";
        using var anonClient = NewClient(factory);
        var (registerResponse, registerBody) = await RegisterAsync(anonClient, businessName, email);
        Assert.Equal(HttpStatusCode.OK, registerResponse.StatusCode);
        Assert.NotNull(registerBody);
        _businessIdsToClean.Add(registerBody!.BusinessId);

        await ConfirmEmailAsync(factory, anonClient, registerBody.UserId);
        var cookie = await LoginAsync(factory, email);

        return (registerBody.BusinessId, cookie);
    }

    /// <summary>The real production path for enabling auto-close: PUT
    /// /api/business/conversation-auto-close (Phase 1). Never a raw EF/SQL
    /// flip of Business.ConversationAutoCloseEnabled.</summary>
    private static async Task EnableAutoCloseAsync(WebApplicationFactory<Program> factory, string ownerCookie)
    {
        using var client = NewClient(factory);
        var request = new HttpRequestMessage(HttpMethod.Put, "/api/business/conversation-auto-close")
        {
            Content = JsonContent.Create(new SetConversationAutoCloseEnabledRequest(true)),
        };
        request.Headers.Add("Cookie", ownerCookie);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>The real production path for setting the threshold: PUT
    /// /api/business/conversation-inactivity-close-days (Phase 1).</summary>
    private static async Task SetInactivityDaysAsync(WebApplicationFactory<Program> factory, string ownerCookie, int days)
    {
        using var client = NewClient(factory);
        var request = new HttpRequestMessage(HttpMethod.Put, "/api/business/conversation-inactivity-close-days")
        {
            Content = JsonContent.Create(new SetConversationInactivityCloseDaysRequest(days)),
        };
        request.Headers.Add("Cookie", ownerCookie);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<Guid> SeedWidgetAsync(Guid businessId)
    {
        await using var db = DbFor();
        var widget = new Widget
        {
            Id = Guid.NewGuid(), BusinessId = businessId, Name = "E2E Widget",
            PublicKey = $"e2e-{Guid.NewGuid():N}", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Widgets.Add(widget);
        await db.SaveChangesAsync();
        return widget.Id;
    }

    private static async Task<Guid> SeedConversationAsync(
        Guid businessId, Guid widgetId, DateTimeOffset createdAt, DateTimeOffset? lastMessageAt,
        ConversationStatus status = ConversationStatus.Open, bool isAnonymized = false)
    {
        await using var db = DbFor();
        var conversation = new Conversation
        {
            Id = Guid.NewGuid(), BusinessId = businessId, WidgetId = widgetId,
            VisitorSessionId = isAnonymized ? null : $"session-{Guid.NewGuid():N}", Channel = "Website",
            CreatedAt = createdAt, UpdatedAt = createdAt, LastMessageAt = lastMessageAt,
            Status = status, IsAnonymized = isAnonymized,
        };
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync();
        return conversation.Id;
    }

    private static async Task<(Guid CustomerId, Guid TicketId)> SeedActiveTicketAsync(Guid businessId, Guid conversationId, TicketStatus status)
    {
        await using var db = DbFor();
        var customer = new Customer
        {
            Id = Guid.NewGuid(), BusinessId = businessId, ExternalId = $"shopify-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Customers.Add(customer);

        var ticket = new Ticket
        {
            Id = Guid.NewGuid(), BusinessId = businessId, CustomerId = customer.Id, ConversationId = conversationId,
            Status = status, Priority = TicketPriority.Medium, PriorityReason = TicketPriorityReasons.ManualEscalation,
            Channel = "Website", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Tickets.Add(ticket);
        await db.SaveChangesAsync();
        return (customer.Id, ticket.Id);
    }

    private static async Task<bool> IsClosedAsync(Guid conversationId)
    {
        await using var db = DbFor();
        return await db.Conversations.IgnoreQueryFilters()
            .Where(c => c.Id == conversationId).Select(c => c.Status == ConversationStatus.Closed).SingleAsync();
    }

    private static async Task<int> AuditCountAsync(Guid conversationId)
    {
        await using var db = DbFor();
        return await db.AuditLogs.IgnoreQueryFilters()
            .CountAsync(a => a.ConversationId == conversationId && a.EventType == AuditEventTypes.ConversationAutoClosed);
    }

    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition, int timeoutSeconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(200);
        }

        return false;
    }

    // ── 1. Happy path: real registration → real enablement → real threshold
    //    → real claim → real audit, covering both staleness cases (design
    //    §2) plus the active-Ticket exemption (Decision 2), all in one
    //    business, all through the real host. ─────────────────────────────

    [Fact]
    public async Task RealPipeline_DueEnabledBusiness_ClosesEligibleConversations_ExemptsActiveTicket_WithCorrectAudit()
    {
        await using var setupFactory = new NivoraApiFactory();
        var (businessId, ownerCookie) = await RegisterBusinessWithOwnerAsync(setupFactory, "E2E Happy Path Business");
        await EnableAutoCloseAsync(setupFactory, ownerCookie);
        await SetInactivityDaysAsync(setupFactory, ownerCookie, 14);

        var widgetId = await SeedWidgetAsync(businessId);
        var now = DateTimeOffset.UtcNow;

        // Case 1 (design §2) — has at least one Message, stale by LastMessageAt.
        var hasMessageTargetId = await SeedConversationAsync(businessId, widgetId, now.AddDays(-90), now.AddDays(-20));

        // Case 2 (design §2) — zero Messages, stale by CreatedAt (LastMessageAt null).
        var zeroMessageTargetId = await SeedConversationAsync(businessId, widgetId, now.AddDays(-20), null);

        // Stale, but has an active linked Ticket — must be exempted (Decision 2).
        var exemptTargetId = await SeedConversationAsync(businessId, widgetId, now.AddDays(-90), now.AddDays(-20));
        await SeedActiveTicketAsync(businessId, exemptTargetId, TicketStatus.Open);

        // A second, unrelated, NEVER-enabled business — proves this pipeline
        // run never reaches across tenants.
        var (otherBusinessId, otherOwnerCookie) = await RegisterBusinessWithOwnerAsync(setupFactory, "E2E Unrelated Business");
        var otherWidgetId = await SeedWidgetAsync(otherBusinessId);
        var otherStaleTargetId = await SeedConversationAsync(otherBusinessId, otherWidgetId, now.AddDays(-90), now.AddDays(-20));
        _ = otherOwnerCookie;

        // NOW start the real verification host, for the first time, only
        // once every business already exists, is (or isn't) enabled, and
        // already has its real conversations seeded — triggering every
        // registered IHostedService, including DataLifecycleBackgroundService,
        // whose first tick runs immediately (before any 24h wait).
        await using var factory = new NivoraApiFactory();
        _ = factory.Services;

        var settled = await WaitUntilAsync(async () =>
            await IsClosedAsync(hasMessageTargetId) && await IsClosedAsync(zeroMessageTargetId));
        Assert.True(settled, "Auto-close did not settle within the timeout through the real host.");

        Assert.True(await IsClosedAsync(hasMessageTargetId));
        Assert.Equal(1, await AuditCountAsync(hasMessageTargetId));

        Assert.True(await IsClosedAsync(zeroMessageTargetId));
        Assert.Equal(1, await AuditCountAsync(zeroMessageTargetId));

        Assert.False(await IsClosedAsync(exemptTargetId)); // Decision 2 — active Ticket exemption
        Assert.Equal(0, await AuditCountAsync(exemptTargetId));

        Assert.False(await IsClosedAsync(otherStaleTargetId)); // never enabled — never touched
        Assert.Equal(0, await AuditCountAsync(otherStaleTargetId));

        // Audit content correctness.
        await using var verifyDb = DbFor();
        var entry = await verifyDb.AuditLogs.IgnoreQueryFilters()
            .SingleAsync(a => a.ConversationId == hasMessageTargetId && a.EventType == AuditEventTypes.ConversationAutoClosed);
        Assert.Equal(AuditEntityTypes.Conversation, entry.EntityType);
        Assert.Equal(ActorType.NivoraSystem, entry.Actor);
        Assert.Null(entry.ChangedByUserId);
    }

    // ── 2. Suppressed: feature not enabled ────────────────────────────────

    [Fact]
    public async Task RealPipeline_FeatureNotEnabled_NeverCloses()
    {
        await using var setupFactory = new NivoraApiFactory();
        var (businessId, _) = await RegisterBusinessWithOwnerAsync(setupFactory, "E2E Disabled Business");
        // Deliberately never call EnableAutoCloseAsync.

        var widgetId = await SeedWidgetAsync(businessId);
        var now = DateTimeOffset.UtcNow;
        var targetId = await SeedConversationAsync(businessId, widgetId, now.AddDays(-90), now.AddDays(-20));

        await using var factory = new NivoraApiFactory();
        _ = factory.Services;

        // No positive signal to wait on (nothing should happen) — use a
        // bounded grace window matching this codebase's own established
        // "prove absence" convention (Phase 2/4's own cancellation tests).
        await Task.Delay(TimeSpan.FromSeconds(3));

        Assert.False(await IsClosedAsync(targetId));
        Assert.Equal(0, await AuditCountAsync(targetId));
    }

    // ── 3. Suppressed: already anonymized (retention's own effect) ───────

    [Fact]
    public async Task RealPipeline_AlreadyAnonymizedConversation_NeverReprocessed()
    {
        await using var setupFactory = new NivoraApiFactory();
        var (businessId, ownerCookie) = await RegisterBusinessWithOwnerAsync(setupFactory, "E2E Anonymized Business");
        await EnableAutoCloseAsync(setupFactory, ownerCookie);
        await SetInactivityDaysAsync(setupFactory, ownerCookie, 14);

        var widgetId = await SeedWidgetAsync(businessId);
        var now = DateTimeOffset.UtcNow;
        // Already anonymized at seed time (simulating retention having
        // already purged it) — still reads Status == Open, the exact real
        // gap design §3/Phase 3's own discovery found.
        var targetId = await SeedConversationAsync(
            businessId, widgetId, now.AddDays(-90), now.AddDays(-20), isAnonymized: true);

        await using var factory = new NivoraApiFactory();
        _ = factory.Services;

        await Task.Delay(TimeSpan.FromSeconds(3));

        Assert.False(await IsClosedAsync(targetId)); // Status stays whatever it already was — Open, untouched
        Assert.Equal(0, await AuditCountAsync(targetId));
    }

    // ── 4. Suppressed: already closed ─────────────────────────────────────

    [Fact]
    public async Task RealPipeline_AlreadyClosedConversation_NeverReprocessed_NoDuplicateAudit()
    {
        await using var setupFactory = new NivoraApiFactory();
        var (businessId, ownerCookie) = await RegisterBusinessWithOwnerAsync(setupFactory, "E2E AlreadyClosed Business");
        await EnableAutoCloseAsync(setupFactory, ownerCookie);
        await SetInactivityDaysAsync(setupFactory, ownerCookie, 14);

        var widgetId = await SeedWidgetAsync(businessId);
        var now = DateTimeOffset.UtcNow;
        var targetId = await SeedConversationAsync(
            businessId, widgetId, now.AddDays(-90), now.AddDays(-20), status: ConversationStatus.Closed);

        await using var factory = new NivoraApiFactory();
        _ = factory.Services;

        await Task.Delay(TimeSpan.FromSeconds(3));

        Assert.True(await IsClosedAsync(targetId)); // unchanged — still closed, from seeding, not this pipeline
        Assert.Equal(0, await AuditCountAsync(targetId)); // no audit row — this pipeline never touched it
    }

    // ── 5. Concurrency: two genuinely separate real sweeps racing for the
    //    same conversation — exactly one wins, never a duplicate. ─────────

    [Fact]
    public async Task RealPipeline_TwoConcurrentSweeps_ExactlyOneWins_NoDuplicateAudit()
    {
        var businessId = Guid.NewGuid();
        await using (var seedDb = DbFor())
        {
            seedDb.Businesses.Add(new Business
            {
                Id = businessId, Name = $"E2E Concurrency {Guid.NewGuid():N}", TimeZone = BusinessTimeZone,
                ConversationAutoCloseEnabled = true, ConversationInactivityCloseDays = 14,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            await seedDb.SaveChangesAsync();
        }
        _businessIdsToClean.Add(businessId);

        var widgetId = await SeedWidgetAsync(businessId);
        var now = DateTimeOffset.UtcNow;
        var targetId = await SeedConversationAsync(businessId, widgetId, now.AddDays(-90), now.AddDays(-20));

        await using var dbA = DbFor();
        await using var dbB = DbFor();
        var serviceA = new ConversationInactivityCloseService(dbA, new AuditLogService(dbA), NullLogger<ConversationInactivityCloseService>.Instance);
        var serviceB = new ConversationInactivityCloseService(dbB, new AuditLogService(dbB), NullLogger<ConversationInactivityCloseService>.Instance);

        var resultsTask = Task.WhenAll(
            serviceA.CloseInactiveConversationsAsync(),
            serviceB.CloseInactiveConversationsAsync());
        var results = await resultsTask;

        Assert.Equal(1, results[0] + results[1]); // exactly one sweep actually closed it
        Assert.True(await IsClosedAsync(targetId));
        Assert.Equal(1, await AuditCountAsync(targetId)); // never a duplicate
    }

    // ── 6. Host-resilience: one conversation's claim fails mid-sweep — the
    //    other, healthy conversation in the SAME business/sweep still
    //    closes. Uses a selectively-throwing IAuditLogService (the same
    //    injection mechanism Phase 3's own transaction-atomicity test
    //    already established) to reproduce a genuine, real failure inside
    //    CloseInactiveConversationsAsync's own per-conversation loop,
    //    rather than a contrived one. ───────────────────────────────────

    private sealed class SelectivelyThrowingAuditLogService(AppDbContext db, Guid failForConversationId) : IAuditLogService
    {
        private readonly IAuditLogService _real = new AuditLogService(db);

        public Task LogAsync(
            Guid businessId, string entityType, string entityId, string eventType, ActorType actor,
            Guid? changedByUserId = null, string? oldValue = null, string? newValue = null,
            Guid? conversationId = null, Guid? correlationId = null, CancellationToken cancellationToken = default)
        {
            if (conversationId == failForConversationId)
            {
                throw new InvalidOperationException("Simulated per-conversation audit failure for host-resilience test.");
            }

            return _real.LogAsync(businessId, entityType, entityId, eventType, actor, changedByUserId, oldValue, newValue, conversationId, correlationId, cancellationToken);
        }
    }

    [Fact]
    public async Task RealPipeline_OneConversationClaimFails_HealthySiblingInSameSweepStillCloses()
    {
        var businessId = Guid.NewGuid();
        await using (var seedDb = DbFor())
        {
            seedDb.Businesses.Add(new Business
            {
                Id = businessId, Name = $"E2E HostResilience {Guid.NewGuid():N}", TimeZone = BusinessTimeZone,
                ConversationAutoCloseEnabled = true, ConversationInactivityCloseDays = 14,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            await seedDb.SaveChangesAsync();
        }
        _businessIdsToClean.Add(businessId);

        var widgetId = await SeedWidgetAsync(businessId);
        var now = DateTimeOffset.UtcNow;
        var failingTargetId = await SeedConversationAsync(businessId, widgetId, now.AddDays(-90), now.AddDays(-20));
        var healthyTargetId = await SeedConversationAsync(businessId, widgetId, now.AddDays(-90), now.AddDays(-20));

        await using var db = DbFor();
        var faultyAuditService = new SelectivelyThrowingAuditLogService(db, failingTargetId);
        var service = new ConversationInactivityCloseService(db, faultyAuditService, NullLogger<ConversationInactivityCloseService>.Instance);

        // Not expected to throw at this level — the per-conversation
        // try/catch inside CloseInactiveConversationsAsync's own loop must
        // absorb the failing conversation's exception and continue.
        var closedCount = await service.CloseInactiveConversationsAsync();

        Assert.Equal(1, closedCount); // only the healthy one counted

        Assert.False(await IsClosedAsync(failingTargetId)); // rolled back — transaction atomicity holds even here
        Assert.Equal(0, await AuditCountAsync(failingTargetId));

        Assert.True(await IsClosedAsync(healthyTargetId)); // sibling, unaffected
        Assert.Equal(1, await AuditCountAsync(healthyTargetId));
    }
}
