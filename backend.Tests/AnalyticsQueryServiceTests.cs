using backend.Constants;
using backend.Data;
using backend.Enums;
using backend.Interfaces;
using backend.Models;
using backend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace backend.Tests;

/// <summary>
/// Step 23 Phase 2 correction. The locked implementation plan's §3 test
/// strategy for Phase 2 (docs/handoffs/step-23-ai-business-analyst-implementation-plan.md)
/// required a dedicated, additive AnalyticsQueryServiceTests.cs "directly
/// re-proving tenant isolation at the service layer (not just observed
/// indirectly through the controller)" — this file backfills that; the
/// original Phase 2 commit (fc62bb3) only proved the post-extraction
/// behavior indirectly, by re-running AnalyticsControllerTests.cs (which now
/// happens to call through AnalyticsQueryService, but never called the
/// service's own methods directly). Mirrors AnalyticsControllerTests.cs's
/// exact dual-direction methodology (seed two businesses with overlapping-
/// looking data, assert each tenant's own call excludes the other's rows,
/// both directions) with IAnalyticsQueryService's own methods in the call
/// path directly — no AnalyticsController anywhere in this file.
/// </summary>
public class AnalyticsQueryServiceTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;
    private readonly List<Guid> _businessIdsToClean = [];

    public Task InitializeAsync()
    {
        var services = new ServiceCollection()
            .AddSingleton<ICurrentTenantAccessor>(new FixedTenantAccessor(null))
            .AddDbContext<AppDbContext>(o => o.UseNpgsql(
                TestDatabase.ConnectionString, x => x.UseVector()));
        _provider = services.BuildServiceProvider();

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await TestBusinessCleanup.DeleteThenDisposeAsync(_businessIdsToClean, _provider);
    }

    // ── Seeding helpers ──────────────────────────────────────────────

    private async Task<(Guid BusinessId, Guid WidgetId, Guid CustomerId)> SeedBusinessAsync(string namePrefix)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var businessId = Guid.NewGuid();
        var widgetId = Guid.NewGuid();
        var customerId = Guid.NewGuid();

        db.Businesses.Add(new Business
        {
            Id = businessId,
            Name = $"{namePrefix} {Guid.NewGuid():N}",
            TimeZone = "UTC",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.Widgets.Add(new Widget
        {
            Id = widgetId,
            BusinessId = businessId,
            Name = $"{namePrefix} Widget",
            PublicKey = $"analytics-qs-test-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.Customers.Add(new Customer
        {
            Id = customerId,
            BusinessId = businessId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync();
        _businessIdsToClean.Add(businessId);
        return (businessId, widgetId, customerId);
    }

    private static async Task AddConversationAsync(
        AppDbContext db, Guid businessId, Guid widgetId,
        ConversationStatus status, HandledBy handledBy, DateTimeOffset createdAt)
    {
        db.Conversations.Add(new Conversation
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            WidgetId = widgetId,
            Status = status,
            HandledBy = handledBy,
            Channel = "widget",
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        });
        await db.SaveChangesAsync();
    }

    private static async Task AddTicketAsync(
        AppDbContext db, Guid businessId, Guid customerId, TicketStatus status, DateTimeOffset createdAt)
    {
        db.Tickets.Add(new Ticket
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            CustomerId = customerId,
            Status = status,
            Priority = TicketPriority.Medium,
            PriorityReason = TicketPriorityReasons.ManualEscalation,
            Channel = "widget",
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        });
        await db.SaveChangesAsync();
    }

    private static async Task AddHandoffAsync(AppDbContext db, Guid businessId, DateTimeOffset createdAt)
    {
        db.ConversationHandoffs.Add(new ConversationHandoff
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            ConversationId = null,
            FromHandledBy = HandledBy.Ai,
            ToHandledBy = HandledBy.Human,
            Reason = HandoffReasons.StaffManualTakeover,
            ActorType = ActorType.NivoraSystem,
            ActorId = null,
            CreatedAt = createdAt,
        });
        await db.SaveChangesAsync();
    }

    // DateTimeOffset.UtcNow.Date returns a DateTime with Kind=Unspecified —
    // its implicit conversion back to DateTimeOffset treats Unspecified as
    // local time, which would silently shift seeded rows across a UTC day
    // boundary depending on the test machine's timezone. Building the
    // DateTimeOffset explicitly at UTC midnight avoids that entirely.
    private static DateTimeOffset UtcMidnight(DateOnly date) =>
        new(date.Year, date.Month, date.Day, 0, 0, 0, TimeSpan.Zero);

    private AppDbContext DbFor(Guid businessId) =>
        new ServiceCollection()
            .AddSingleton<ICurrentTenantAccessor>(new FixedTenantAccessor(businessId))
            .AddDbContext<AppDbContext>(o => o.UseNpgsql(
                TestDatabase.ConnectionString, x => x.UseVector()))
            .BuildServiceProvider()
            .GetRequiredService<AppDbContext>();

    private static AnalyticsRangeWindow WindowFor(IAnalyticsQueryService service, string range)
    {
        Assert.True(service.TryParseRange(range, out var window));
        return window;
    }

    // ── Metric 1 — Conversations over time ───────────────────────────

    [Fact]
    public async Task GetConversationsByDayAsync_IsolatesPerTenant_BothDirections()
    {
        var (bizA, widgetA, _) = await SeedBusinessAsync("Analytics QS Biz A M1");
        var (bizB, widgetB, _) = await SeedBusinessAsync("Analytics QS Biz B M1");

        var todayOnly = DateOnly.FromDateTime(DateTime.UtcNow);
        var today = UtcMidnight(todayOnly);
        await using (var seedDb = DbFor(bizA))
        {
            // Business A: 3 today, 2 yesterday = 5 total.
            await AddConversationAsync(seedDb, bizA, widgetA, ConversationStatus.Open, HandledBy.Ai, today);
            await AddConversationAsync(seedDb, bizA, widgetA, ConversationStatus.Open, HandledBy.Ai, today);
            await AddConversationAsync(seedDb, bizA, widgetA, ConversationStatus.Closed, HandledBy.Human, today);
            await AddConversationAsync(seedDb, bizA, widgetA, ConversationStatus.Open, HandledBy.Ai, today.AddDays(-1));
            await AddConversationAsync(seedDb, bizA, widgetA, ConversationStatus.Open, HandledBy.Ai, today.AddDays(-1));
        }
        await using (var seedDb = DbFor(bizB))
        {
            // Business B: 1 today = 1 total. Different shape than A entirely.
            await AddConversationAsync(seedDb, bizB, widgetB, ConversationStatus.Open, HandledBy.Ai, today);
        }

        await using var dbA = DbFor(bizA);
        var serviceA = new AnalyticsQueryService(dbA);
        var resultA = await serviceA.GetConversationsByDayAsync(WindowFor(serviceA, "7d"), CancellationToken.None);

        await using var dbB = DbFor(bizB);
        var serviceB = new AnalyticsQueryService(dbB);
        var resultB = await serviceB.GetConversationsByDayAsync(WindowFor(serviceB, "7d"), CancellationToken.None);

        Assert.Equal(5, resultA.Sum(d => d.Count));
        Assert.Equal(1, resultB.Sum(d => d.Count));

        var yesterdayOnly = todayOnly.AddDays(-1);
        Assert.Equal(3, resultA.Single(d => d.Day == todayOnly).Count);
        Assert.Equal(2, resultA.Single(d => d.Day == yesterdayOnly).Count);
        Assert.Equal(1, resultB.Single(d => d.Day == todayOnly).Count);
        Assert.Equal(0, resultB.Single(d => d.Day == yesterdayOnly).Count);
    }

    // ── Metric 2 — Conversation status distribution ──────────────────

    [Fact]
    public async Task GetConversationStatusDistributionAsync_IsolatesPerTenant_OverlappingStatuses()
    {
        var (bizA, widgetA, _) = await SeedBusinessAsync("Analytics QS Biz A M2");
        var (bizB, widgetB, _) = await SeedBusinessAsync("Analytics QS Biz B M2");
        var now = DateTimeOffset.UtcNow;

        await using (var seedDb = DbFor(bizA))
        {
            // Both businesses have Open AND Closed rows — a naive cross-tenant
            // union would still look plausible if it merged them.
            await AddConversationAsync(seedDb, bizA, widgetA, ConversationStatus.Open, HandledBy.Ai, now);
            await AddConversationAsync(seedDb, bizA, widgetA, ConversationStatus.Open, HandledBy.Ai, now);
            await AddConversationAsync(seedDb, bizA, widgetA, ConversationStatus.Closed, HandledBy.Human, now);
        }
        await using (var seedDb = DbFor(bizB))
        {
            await AddConversationAsync(seedDb, bizB, widgetB, ConversationStatus.Open, HandledBy.Ai, now);
            await AddConversationAsync(seedDb, bizB, widgetB, ConversationStatus.Closed, HandledBy.Human, now);
            await AddConversationAsync(seedDb, bizB, widgetB, ConversationStatus.Closed, HandledBy.Human, now);
        }

        await using var dbA = DbFor(bizA);
        var serviceA = new AnalyticsQueryService(dbA);
        var resultA = await serviceA.GetConversationStatusDistributionAsync(WindowFor(serviceA, "7d"), CancellationToken.None);

        await using var dbB = DbFor(bizB);
        var serviceB = new AnalyticsQueryService(dbB);
        var resultB = await serviceB.GetConversationStatusDistributionAsync(WindowFor(serviceB, "7d"), CancellationToken.None);

        Assert.Equal(2, resultA.Single(s => s.Status == "Open").Count);
        Assert.Equal(1, resultA.Single(s => s.Status == "Closed").Count);
        Assert.Equal(1, resultB.Single(s => s.Status == "Open").Count);
        Assert.Equal(2, resultB.Single(s => s.Status == "Closed").Count);
    }

    // ── Metric 3 — HandledBy snapshot (no range filter, Decision 1) ──

    [Fact]
    public async Task GetHandledBySnapshotAsync_IsolatesPerTenant_AndIgnoresAge()
    {
        var (bizA, widgetA, _) = await SeedBusinessAsync("Analytics QS Biz A M3");
        var (bizB, widgetB, _) = await SeedBusinessAsync("Analytics QS Biz B M3");
        var now = DateTimeOffset.UtcNow;

        await using (var seedDb = DbFor(bizA))
        {
            // Business A: mostly Ai, including one very old conversation —
            // GetHandledBySnapshotAsync takes no window/range argument at
            // all, so there is nothing to leak a date filter from; this
            // still proves the old row is not somehow excluded by an
            // incidental filter elsewhere in the query.
            await AddConversationAsync(seedDb, bizA, widgetA, ConversationStatus.Open, HandledBy.Ai, now);
            await AddConversationAsync(seedDb, bizA, widgetA, ConversationStatus.Open, HandledBy.Ai, now);
            await AddConversationAsync(seedDb, bizA, widgetA, ConversationStatus.Open, HandledBy.Ai, now.AddDays(-250));
            await AddConversationAsync(seedDb, bizA, widgetA, ConversationStatus.Closed, HandledBy.Human, now);
        }
        await using (var seedDb = DbFor(bizB))
        {
            // Business B: mostly Human, also with an old row, different split.
            await AddConversationAsync(seedDb, bizB, widgetB, ConversationStatus.Open, HandledBy.Ai, now.AddDays(-300));
            await AddConversationAsync(seedDb, bizB, widgetB, ConversationStatus.Closed, HandledBy.Human, now);
            await AddConversationAsync(seedDb, bizB, widgetB, ConversationStatus.Closed, HandledBy.Human, now);
        }

        await using var dbA = DbFor(bizA);
        var resultA = await new AnalyticsQueryService(dbA).GetHandledBySnapshotAsync(CancellationToken.None);

        await using var dbB = DbFor(bizB);
        var resultB = await new AnalyticsQueryService(dbB).GetHandledBySnapshotAsync(CancellationToken.None);

        Assert.Equal(3, resultA.Single(h => h.Status == "Ai").Count);
        Assert.Equal(1, resultA.Single(h => h.Status == "Human").Count);
        Assert.Equal(1, resultB.Single(h => h.Status == "Ai").Count);
        Assert.Equal(2, resultB.Single(h => h.Status == "Human").Count);
    }

    // ── Metric 4 — Handoff events over time ──────────────────────────

    [Fact]
    public async Task GetHandoffsByDayAsync_IsolatesPerTenant_SameCalendarDay()
    {
        var (bizA, _, _) = await SeedBusinessAsync("Analytics QS Biz A M4");
        var (bizB, _, _) = await SeedBusinessAsync("Analytics QS Biz B M4");
        var todayOnly = DateOnly.FromDateTime(DateTime.UtcNow);
        var today = UtcMidnight(todayOnly);

        await using (var seedDb = DbFor(bizA))
        {
            // Both on the SAME calendar day — a bug that grouped by day
            // without also filtering by BusinessId would merge these.
            await AddHandoffAsync(seedDb, bizA, today);
            await AddHandoffAsync(seedDb, bizA, today);
            await AddHandoffAsync(seedDb, bizA, today);
        }
        await using (var seedDb = DbFor(bizB))
        {
            await AddHandoffAsync(seedDb, bizB, today);
        }

        await using var dbA = DbFor(bizA);
        var serviceA = new AnalyticsQueryService(dbA);
        var resultA = await serviceA.GetHandoffsByDayAsync(WindowFor(serviceA, "7d"), CancellationToken.None);

        await using var dbB = DbFor(bizB);
        var serviceB = new AnalyticsQueryService(dbB);
        var resultB = await serviceB.GetHandoffsByDayAsync(WindowFor(serviceB, "7d"), CancellationToken.None);

        Assert.Equal(3, resultA.Single(d => d.Day == todayOnly).Count);
        Assert.Equal(1, resultB.Single(d => d.Day == todayOnly).Count);
        Assert.Equal(3, resultA.Sum(d => d.Count));
        Assert.Equal(1, resultB.Sum(d => d.Count));
    }

    // ── Metric 5 — Ticket status distribution ────────────────────────

    [Fact]
    public async Task GetTicketStatusDistributionAsync_IsolatesPerTenant_AllStatusesRepresented()
    {
        var (bizA, _, customerA) = await SeedBusinessAsync("Analytics QS Biz A M5");
        var (bizB, _, customerB) = await SeedBusinessAsync("Analytics QS Biz B M5");
        var now = DateTimeOffset.UtcNow;

        await using (var seedDb = DbFor(bizA))
        {
            await AddTicketAsync(seedDb, bizA, customerA, TicketStatus.Open, now);
            await AddTicketAsync(seedDb, bizA, customerA, TicketStatus.InProgress, now);
            await AddTicketAsync(seedDb, bizA, customerA, TicketStatus.Resolved, now);
            await AddTicketAsync(seedDb, bizA, customerA, TicketStatus.Reopened, now);
            await AddTicketAsync(seedDb, bizA, customerA, TicketStatus.Open, now);
        }
        await using (var seedDb = DbFor(bizB))
        {
            await AddTicketAsync(seedDb, bizB, customerB, TicketStatus.Open, now);
            await AddTicketAsync(seedDb, bizB, customerB, TicketStatus.Resolved, now);
        }

        await using var dbA = DbFor(bizA);
        var serviceA = new AnalyticsQueryService(dbA);
        var resultA = await serviceA.GetTicketStatusDistributionAsync(WindowFor(serviceA, "7d"), CancellationToken.None);

        await using var dbB = DbFor(bizB);
        var serviceB = new AnalyticsQueryService(dbB);
        var resultB = await serviceB.GetTicketStatusDistributionAsync(WindowFor(serviceB, "7d"), CancellationToken.None);

        Assert.Equal(2, resultA.Single(s => s.Status == "Open").Count);
        Assert.Equal(1, resultA.Single(s => s.Status == "InProgress").Count);
        Assert.Equal(1, resultA.Single(s => s.Status == "Resolved").Count);
        Assert.Equal(1, resultA.Single(s => s.Status == "Reopened").Count);

        Assert.Equal(1, resultB.Single(s => s.Status == "Open").Count);
        Assert.Equal(0, resultB.Single(s => s.Status == "InProgress").Count);
        Assert.Equal(1, resultB.Single(s => s.Status == "Resolved").Count);
        Assert.Equal(0, resultB.Single(s => s.Status == "Reopened").Count);
    }

    private sealed class FixedTenantAccessor(Guid? businessId) : ICurrentTenantAccessor
    {
        public Guid? BusinessId => businessId;
    }
}
