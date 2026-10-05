using backend.Constants;
using backend.Data;
using backend.Enums;
using backend.Interfaces;
using backend.Models;
using backend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace backend.Tests;

/// <summary>
/// Step 25 Candidate 2, Phase 3 — service-level tests for
/// ConversationInactivityCloseService, real Postgres, same pattern as
/// CustomerDataLifecycleTests (bare ServiceProvider, not the full HTTP
/// WebApplicationFactory — this service has no controller/HTTP surface at
/// all, matching design §10's explicit non-scope).
///
/// Two layers:
/// 1. Public-surface tests (CloseInactiveConversationsAsync) — eligibility
///    predicate, zero-message vs has-message staleness (design §2), the
///    Ticket exemption (Decision 2), and AuditLog content (Decision 4).
/// 2. Individual, per-condition negative-proof tests calling the internal
///    TryCloseConversationAsync directly (this project's own established
///    InternalsVisibleTo precedent), each simulating one specific
///    discovery-to-claim race via a second, independent AppDbContext —
///    exactly the design §3/plan §3.1-§3.5 blueprint. Each has its own
///    break/restore step performed during this phase's own adversarial
///    verification (not committed as a permanent test) — see the
///    accompanying handoff/commit message, not this file, for that record.
/// </summary>
[Collection("ConversationAutoClose")]
public class ConversationInactivityCloseServiceTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;
    private readonly List<Guid> _businessIdsToClean = [];

    public Task InitializeAsync()
    {
        var configuration = new ConfigurationBuilder()
            .AddUserSecrets<Program>()
            .Build();
        var connectionUri = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection not found via user-secrets.");

        _provider = new ServiceCollection()
            .AddSingleton<ICurrentTenantAccessor>(new FixedTenantAccessor(null))
            .AddDbContext<AppDbContext>(o => o.UseNpgsql(
                PostgresConnectionString.FromUri(connectionUri), x => x.UseVector()))
            .AddScoped<IAuditLogService, AuditLogService>()
            .AddScoped<IConversationInactivityCloseService, ConversationInactivityCloseService>()
            .AddSingleton<Microsoft.Extensions.Logging.ILogger<ConversationInactivityCloseService>>(NullLogger<ConversationInactivityCloseService>.Instance)
            .BuildServiceProvider();

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_businessIdsToClean.Count > 0)
        {
            await using var scope = _provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var ids = _businessIdsToClean.ToArray();

            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"AuditLogs\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Tickets\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Conversations\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Customers\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Widgets\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Businesses\" WHERE \"Id\" = ANY({ids})");
        }

        await _provider.DisposeAsync();
    }

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

    private sealed class ThrowingAuditLogService : IAuditLogService
    {
        public Task LogAsync(
            Guid businessId, string entityType, string entityId, string eventType, ActorType actor,
            Guid? changedByUserId = null, string? oldValue = null, string? newValue = null,
            Guid? conversationId = null, Guid? correlationId = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Simulated AuditLog write failure for test.");
    }

    // ── Fixture helpers ─────────────────────────────────────────────

    private async Task<Guid> CreateBusinessAsync(AppDbContext db, bool autoCloseEnabled = true, int inactivityDays = 14)
    {
        var business = new Business
        {
            Id = Guid.NewGuid(),
            Name = $"Phase3 Business {Guid.NewGuid():N}",
            TimeZone = "Europe/Copenhagen",
            ConversationAutoCloseEnabled = autoCloseEnabled,
            ConversationInactivityCloseDays = inactivityDays,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Businesses.Add(business);
        await db.SaveChangesAsync();
        _businessIdsToClean.Add(business.Id);
        return business.Id;
    }

    private static async Task<Guid> CreateWidgetAsync(AppDbContext db, Guid businessId)
    {
        var widget = new Widget
        {
            Id = Guid.NewGuid(), BusinessId = businessId, Name = "Test Widget",
            PublicKey = Guid.NewGuid().ToString("N"), CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Widgets.Add(widget);
        await db.SaveChangesAsync();
        return widget.Id;
    }

    private static async Task<Guid> CreateCustomerAsync(AppDbContext db, Guid businessId)
    {
        var customer = new Customer
        {
            Id = Guid.NewGuid(), BusinessId = businessId, ExternalId = $"shopify-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        return customer.Id;
    }

    private static async Task<Guid> CreateConversationAsync(
        AppDbContext db, Guid businessId, Guid widgetId, DateTimeOffset createdAt, DateTimeOffset? lastMessageAt,
        ConversationStatus status = ConversationStatus.Open, bool isAnonymized = false)
    {
        var conversation = new Conversation
        {
            Id = Guid.NewGuid(), BusinessId = businessId, WidgetId = widgetId,
            VisitorSessionId = isAnonymized ? null : $"session-{Guid.NewGuid():N}",
            Channel = "Website", CreatedAt = createdAt, UpdatedAt = createdAt,
            LastMessageAt = lastMessageAt, Status = status, IsAnonymized = isAnonymized,
        };
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync();
        return conversation.Id;
    }

    private static async Task<Guid> CreateTicketAsync(
        AppDbContext db, Guid businessId, Guid customerId, Guid conversationId, TicketStatus status)
    {
        var ticket = new Ticket
        {
            Id = Guid.NewGuid(), BusinessId = businessId, CustomerId = customerId, ConversationId = conversationId,
            Status = status, Priority = TicketPriority.Medium, PriorityReason = TicketPriorityReasons.ManualEscalation,
            Channel = "Website", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Tickets.Add(ticket);
        await db.SaveChangesAsync();
        return ticket.Id;
    }

    private async Task<bool> IsClosedAsync(Guid conversationId)
    {
        await using var db = DbFor();
        return await db.Conversations.IgnoreQueryFilters()
            .Where(c => c.Id == conversationId).Select(c => c.Status == ConversationStatus.Closed).SingleAsync();
    }

    private async Task<int> AuditCountAsync(Guid conversationId)
    {
        await using var db = DbFor();
        return await db.AuditLogs.IgnoreQueryFilters()
            .CountAsync(a => a.ConversationId == conversationId && a.EventType == AuditEventTypes.ConversationAutoClosed);
    }

    // ══════════════════════════════════════════════════════════════════
    // Part 1 — public-surface eligibility / staleness / audit tests
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CloseInactiveConversations_HasMessages_MeasuresStalenessFromLastMessageAt_NotCreatedAt()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IConversationInactivityCloseService>();

        var businessId = await CreateBusinessAsync(db, inactivityDays: 14);
        var widgetId = await CreateWidgetAsync(db, businessId);
        var now = DateTimeOffset.UtcNow;

        // Old CreatedAt, but a RECENT LastMessageAt — must survive, proving
        // staleness is measured from LastMessageAt, not CreatedAt, once at
        // least one message exists (design §2 Case 1).
        var freshByLastMessage = await CreateConversationAsync(db, businessId, widgetId, now.AddDays(-90), now.AddDays(-1));

        // Old LastMessageAt — must be closed.
        var staleByLastMessage = await CreateConversationAsync(db, businessId, widgetId, now.AddDays(-90), now.AddDays(-20));

        await service.CloseInactiveConversationsAsync();

        Assert.False(await IsClosedAsync(freshByLastMessage));
        Assert.True(await IsClosedAsync(staleByLastMessage));
    }

    [Fact]
    public async Task CloseInactiveConversations_ZeroMessages_MeasuresStalenessFromCreatedAt()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IConversationInactivityCloseService>();

        var businessId = await CreateBusinessAsync(db, inactivityDays: 14);
        var widgetId = await CreateWidgetAsync(db, businessId);
        var now = DateTimeOffset.UtcNow;

        // No messages (LastMessageAt null), recent CreatedAt — must survive.
        var freshByCreatedAt = await CreateConversationAsync(db, businessId, widgetId, now.AddDays(-1), null);

        // No messages, old CreatedAt — must be closed via the CreatedAt
        // fallback (design §2 Case 2). This is the real product semantic
        // the design flagged explicitly: a conversation can close having
        // never received a Message.
        var staleByCreatedAt = await CreateConversationAsync(db, businessId, widgetId, now.AddDays(-20), null);

        await service.CloseInactiveConversationsAsync();

        Assert.False(await IsClosedAsync(freshByCreatedAt));
        Assert.True(await IsClosedAsync(staleByCreatedAt));
    }

    [Fact]
    public async Task CloseInactiveConversations_ResolvedTicket_StillEligible()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IConversationInactivityCloseService>();

        var businessId = await CreateBusinessAsync(db);
        var widgetId = await CreateWidgetAsync(db, businessId);
        var customerId = await CreateCustomerAsync(db, businessId);
        var now = DateTimeOffset.UtcNow;
        var conversationId = await CreateConversationAsync(db, businessId, widgetId, now.AddDays(-90), now.AddDays(-20));
        await CreateTicketAsync(db, businessId, customerId, conversationId, TicketStatus.Resolved);

        await service.CloseInactiveConversationsAsync();

        Assert.True(await IsClosedAsync(conversationId)); // per Decision 2's own text — a Resolved ticket does not exempt
    }

    [Theory]
    [InlineData(TicketStatus.Open)]
    [InlineData(TicketStatus.InProgress)]
    [InlineData(TicketStatus.Reopened)]
    public async Task CloseInactiveConversations_ActiveTicket_Excluded(TicketStatus activeStatus)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IConversationInactivityCloseService>();

        var businessId = await CreateBusinessAsync(db);
        var widgetId = await CreateWidgetAsync(db, businessId);
        var customerId = await CreateCustomerAsync(db, businessId);
        var now = DateTimeOffset.UtcNow;
        var conversationId = await CreateConversationAsync(db, businessId, widgetId, now.AddDays(-90), now.AddDays(-20));
        await CreateTicketAsync(db, businessId, customerId, conversationId, activeStatus);

        await service.CloseInactiveConversationsAsync();

        Assert.False(await IsClosedAsync(conversationId)); // Decision 2 — exempted, not closed underneath an active Ticket
    }

    [Fact]
    public async Task CloseInactiveConversations_DisabledBusiness_NeverProcessed()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IConversationInactivityCloseService>();

        var businessId = await CreateBusinessAsync(db, autoCloseEnabled: false);
        var widgetId = await CreateWidgetAsync(db, businessId);
        var now = DateTimeOffset.UtcNow;
        var conversationId = await CreateConversationAsync(db, businessId, widgetId, now.AddDays(-90), now.AddDays(-90));

        await service.CloseInactiveConversationsAsync();

        Assert.False(await IsClosedAsync(conversationId));
    }

    [Fact]
    public async Task CloseInactiveConversations_AlreadyAnonymized_NeverReprocessed()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IConversationInactivityCloseService>();

        var businessId = await CreateBusinessAsync(db);
        var widgetId = await CreateWidgetAsync(db, businessId);
        var now = DateTimeOffset.UtcNow;
        var conversationId = await CreateConversationAsync(
            db, businessId, widgetId, now.AddDays(-90), now.AddDays(-90), isAnonymized: true);

        await service.CloseInactiveConversationsAsync();

        Assert.Equal(0, await AuditCountAsync(conversationId)); // never touched at all
    }

    [Fact]
    public async Task CloseInactiveConversations_SuccessfulClose_WritesAuditLogRow_WithCorrectFields()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IConversationInactivityCloseService>();

        var businessId = await CreateBusinessAsync(db, inactivityDays: 14);
        var widgetId = await CreateWidgetAsync(db, businessId);
        var now = DateTimeOffset.UtcNow;
        var lastMessageAt = now.AddDays(-20);
        var conversationId = await CreateConversationAsync(db, businessId, widgetId, now.AddDays(-90), lastMessageAt);

        await service.CloseInactiveConversationsAsync();

        await using var verifyDb = DbFor();
        Assert.Equal(ConversationStatus.Closed, await verifyDb.Conversations.IgnoreQueryFilters().Where(c => c.Id == conversationId).Select(c => c.Status).SingleAsync());
        var entry = await verifyDb.AuditLogs.IgnoreQueryFilters()
            .SingleAsync(a => a.ConversationId == conversationId && a.EventType == AuditEventTypes.ConversationAutoClosed);

        Assert.Equal(AuditEntityTypes.Conversation, entry.EntityType);
        Assert.Equal(conversationId.ToString(), entry.EntityId);
        Assert.Equal(ActorType.NivoraSystem, entry.Actor);
        Assert.Null(entry.ChangedByUserId); // only populated for NivoraBusinessStaff
        Assert.NotNull(entry.NewValue);
        Assert.Contains("\"Closed\"", entry.NewValue);
        Assert.Contains("14", entry.NewValue); // InactivityDays
    }

    // ══════════════════════════════════════════════════════════════════
    // Part 2 — five individual, per-condition negative-proof race tests
    // (internal TryCloseConversationAsync, design §3/plan §3.1-§3.5)
    // ══════════════════════════════════════════════════════════════════

    // ── Condition 1 — still Open ─────────────────────────────────────

    [Fact]
    public async Task TryCloseConversation_AlreadyClosedByAnotherWorker_NotReprocessed_NoDuplicateAudit()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = (ConversationInactivityCloseService)scope.ServiceProvider.GetRequiredService<IConversationInactivityCloseService>();

        var businessId = await CreateBusinessAsync(db, inactivityDays: 14);
        var widgetId = await CreateWidgetAsync(db, businessId);
        var now = DateTimeOffset.UtcNow;
        var conversationId = await CreateConversationAsync(db, businessId, widgetId, now.AddDays(-90), now.AddDays(-20));

        // Two GENUINELY separate claim attempts against the same guarded
        // UPDATE — not a raw state poke standing in for "already closed."
        // A second, independent ConversationInactivityCloseService (its
        // own AppDbContext, its own AuditLogService) performs a real claim
        // attempt first and must actually win it.
        await using var otherWorkerDb = DbFor();
        var otherWorkerService = new ConversationInactivityCloseService(
            otherWorkerDb, new AuditLogService(otherWorkerDb), NullLogger<ConversationInactivityCloseService>.Instance);
        var otherWorkerResult = await otherWorkerService.TryCloseConversationAsync(businessId, conversationId, now, CancellationToken.None);
        Assert.True(otherWorkerResult); // the other worker's own claim genuinely succeeded

        // THIS worker's own claim attempt, on the same conversation,
        // through the same guarded UPDATE — must lose the race.
        var result = await service.TryCloseConversationAsync(businessId, conversationId, now, CancellationToken.None);

        Assert.False(result);
        Assert.Equal(1, await AuditCountAsync(conversationId)); // exactly one — the winner's — never a duplicate
    }

    // ── Condition 2 — still past the inactivity threshold ────────────

    [Fact]
    public async Task TryCloseConversation_MessageArrivesBetweenDiscoveryAndClaim_NotClosed()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = (ConversationInactivityCloseService)scope.ServiceProvider.GetRequiredService<IConversationInactivityCloseService>();

        var businessId = await CreateBusinessAsync(db, inactivityDays: 14);
        var widgetId = await CreateWidgetAsync(db, businessId);
        var now = DateTimeOffset.UtcNow;
        // Zero-message conversation, stale by CreatedAt — a candidate at discovery time.
        var conversationId = await CreateConversationAsync(db, businessId, widgetId, now.AddDays(-90), null);

        // A new Message arrives (updates LastMessageAt to now) between
        // discovery and claim, via a second, independent context — exactly
        // matching the real production write paths' own
        // "LastMessageAt = now" update.
        await using (var otherWorkerDb = DbFor())
        {
            var conv = await otherWorkerDb.Conversations.IgnoreQueryFilters().SingleAsync(c => c.Id == conversationId);
            conv.LastMessageAt = now;
            conv.UpdatedAt = now;
            await otherWorkerDb.SaveChangesAsync();
        }

        var result = await service.TryCloseConversationAsync(businessId, conversationId, now, CancellationToken.None);

        Assert.False(result);
        Assert.False(await IsClosedAsync(conversationId));
        Assert.Equal(0, await AuditCountAsync(conversationId));
    }

    // ── Condition 3 — still no active linked Ticket ───────────────────

    [Fact]
    public async Task TryCloseConversation_TicketBecomesActiveBetweenDiscoveryAndClaim_NotClosed()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = (ConversationInactivityCloseService)scope.ServiceProvider.GetRequiredService<IConversationInactivityCloseService>();

        var businessId = await CreateBusinessAsync(db, inactivityDays: 14);
        var widgetId = await CreateWidgetAsync(db, businessId);
        var customerId = await CreateCustomerAsync(db, businessId);
        var now = DateTimeOffset.UtcNow;
        // No Ticket yet — a candidate at discovery time.
        var conversationId = await CreateConversationAsync(db, businessId, widgetId, now.AddDays(-90), now.AddDays(-20));

        // A Ticket becomes active between discovery and claim, via a
        // second, independent context.
        await using (var otherWorkerDb = DbFor())
        {
            await CreateTicketAsync(otherWorkerDb, businessId, customerId, conversationId, TicketStatus.Open);
        }

        var result = await service.TryCloseConversationAsync(businessId, conversationId, now, CancellationToken.None);

        Assert.False(result); // exactly the scenario Decision 2 exists to prevent
        Assert.False(await IsClosedAsync(conversationId));
        Assert.Equal(0, await AuditCountAsync(conversationId));
    }

    // ── Condition 4 — ConversationAutoCloseEnabled still enabled ──────

    [Fact]
    public async Task TryCloseConversation_FeatureDisabledBetweenDiscoveryAndClaim_NotClosed()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = (ConversationInactivityCloseService)scope.ServiceProvider.GetRequiredService<IConversationInactivityCloseService>();

        var businessId = await CreateBusinessAsync(db, autoCloseEnabled: true, inactivityDays: 14);
        var widgetId = await CreateWidgetAsync(db, businessId);
        var now = DateTimeOffset.UtcNow;
        var conversationId = await CreateConversationAsync(db, businessId, widgetId, now.AddDays(-90), now.AddDays(-20));

        // Feature disabled between discovery and claim, via a second,
        // independent context.
        await using (var otherWorkerDb = DbFor())
        {
            var biz = await otherWorkerDb.Businesses.IgnoreQueryFilters().SingleAsync(b => b.Id == businessId);
            biz.ConversationAutoCloseEnabled = false;
            biz.UpdatedAt = now;
            await otherWorkerDb.SaveChangesAsync();
        }

        var result = await service.TryCloseConversationAsync(businessId, conversationId, now, CancellationToken.None);

        Assert.False(result);
        Assert.False(await IsClosedAsync(conversationId));
        Assert.Equal(0, await AuditCountAsync(conversationId));
    }

    // ── Condition 5 — still not anonymized ────────────────────────────

    [Fact]
    public async Task TryCloseConversation_AnonymizedByRetentionBetweenDiscoveryAndClaim_NotClosed()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = (ConversationInactivityCloseService)scope.ServiceProvider.GetRequiredService<IConversationInactivityCloseService>();
        var purgeService = new ConversationPurgeService(db, new NoOpPurgeFailurePoint());

        var businessId = await CreateBusinessAsync(db, inactivityDays: 14);
        var widgetId = await CreateWidgetAsync(db, businessId);
        var now = DateTimeOffset.UtcNow;
        var conversationId = await CreateConversationAsync(db, businessId, widgetId, now.AddDays(-90), now.AddDays(-20));

        // Retention purges the conversation between discovery and claim,
        // via a second, independent context, using the REAL production
        // purge path — not a hand-rolled substitute.
        await using (var otherWorkerDb = DbFor())
        {
            var otherPurgeService = new ConversationPurgeService(otherWorkerDb, new NoOpPurgeFailurePoint());
            await otherPurgeService.PurgeConversationAsync(conversationId);
        }

        // Confirms the real gap the design discovery found: purge never
        // touches Status, so the row still reads Open — condition 1 alone
        // would NOT exclude it; condition 5 is genuinely load-bearing.
        await using (var checkDb = DbFor())
        {
            var stillOpen = await checkDb.Conversations.IgnoreQueryFilters()
                .Where(c => c.Id == conversationId).Select(c => c.Status).SingleAsync();
            Assert.Equal(ConversationStatus.Open, stillOpen);
        }

        var result = await service.TryCloseConversationAsync(businessId, conversationId, now, CancellationToken.None);

        Assert.False(result);
        Assert.Equal(0, await AuditCountAsync(conversationId));

        await using var finalDb = DbFor();
        var finalConv = await finalDb.Conversations.IgnoreQueryFilters().SingleAsync(c => c.Id == conversationId);
        Assert.True(finalConv.IsAnonymized); // purge's own effect, untouched by the claim attempt
    }

    // ══════════════════════════════════════════════════════════════════
    // Part 3 — audit-write transaction atomicity negative-proof
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task TryCloseConversation_AuditLogWriteFails_WholeTransactionRollsBack_ConversationStaysOpen_NoAuditRow()
    {
        await using var db = DbFor();
        var faultyService = new ConversationInactivityCloseService(db, new ThrowingAuditLogService(), NullLogger<ConversationInactivityCloseService>.Instance);

        var businessId = await CreateBusinessAsync(db, inactivityDays: 14);
        var widgetId = await CreateWidgetAsync(db, businessId);
        var now = DateTimeOffset.UtcNow;
        var conversationId = await CreateConversationAsync(db, businessId, widgetId, now.AddDays(-90), now.AddDays(-20));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => faultyService.TryCloseConversationAsync(businessId, conversationId, now, CancellationToken.None));

        // Re-read from a FRESH, independent context — never the same
        // db/change-tracker the failing call ran against — to confirm the
        // real, persisted state, not an in-memory artifact.
        await using var verifyDb = DbFor();
        var conv = await verifyDb.Conversations.IgnoreQueryFilters().SingleAsync(c => c.Id == conversationId);
        Assert.Equal(ConversationStatus.Open, conv.Status); // the guarded UPDATE's own effect was rolled back too

        var auditCount = await verifyDb.AuditLogs.IgnoreQueryFilters()
            .CountAsync(a => a.ConversationId == conversationId && a.EventType == AuditEventTypes.ConversationAutoClosed);
        Assert.Equal(0, auditCount);
    }
}
