using backend.Constants;
using backend.Controllers;
using backend.Data;
using backend.Enums;
using backend.Interfaces;
using backend.Models;
using backend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace backend.Tests;

/// <summary>
/// Step 21 (Basic Analytics) — implements the security verification
/// strategy design §10 describes, plus the dense/zero-fill guarantee (§7)
/// and the exact zero-fill cardinality at the 7/30/90-day boundaries (§8
/// Decision 3). Each tenant-isolation test asserts both directions
/// (A excludes B, B excludes A) against known seeded totals, mirroring the
/// discipline the design's own scratch verification (§11) used — never
/// just "the call succeeded."
/// </summary>
public class AnalyticsControllerTests : IAsyncLifetime
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

        var services = new ServiceCollection()
            .AddSingleton<ICurrentTenantAccessor>(new FixedTenantAccessor(null))
            .AddDbContext<AppDbContext>(o => o.UseNpgsql(
                PostgresConnectionString.FromUri(connectionUri), x => x.UseVector()));
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
            PublicKey = $"analytics-test-{Guid.NewGuid():N}",
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
                PostgresConnectionString.FromUri(new ConfigurationBuilder()
                    .AddUserSecrets<Program>().Build()
                    .GetConnectionString("DefaultConnection")!), x => x.UseVector()))
            .BuildServiceProvider()
            .GetRequiredService<AppDbContext>();

    // ── Metric 1 — Conversations over time ───────────────────────────

    [Fact]
    public async Task Metric1_ConversationsByDay_IsolatesPerTenant_BothDirections()
    {
        var (bizA, widgetA, _) = await SeedBusinessAsync("Analytics Biz A M1");
        var (bizB, widgetB, _) = await SeedBusinessAsync("Analytics Biz B M1");

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
        var resultA = await new AnalyticsController(new AnalyticsQueryService(dbA)).Get("7d", CancellationToken.None);
        var responseA = Assert.IsType<AnalyticsResponse>(Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(resultA.Result).Value);

        await using var dbB = DbFor(bizB);
        var resultB = await new AnalyticsController(new AnalyticsQueryService(dbB)).Get("7d", CancellationToken.None);
        var responseB = Assert.IsType<AnalyticsResponse>(Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(resultB.Result).Value);

        Assert.Equal(5, responseA.ConversationsByDay.Sum(d => d.Count));
        Assert.Equal(1, responseB.ConversationsByDay.Sum(d => d.Count));

        var yesterdayOnly = todayOnly.AddDays(-1);
        Assert.Equal(3, responseA.ConversationsByDay.Single(d => d.Day == todayOnly).Count);
        Assert.Equal(2, responseA.ConversationsByDay.Single(d => d.Day == yesterdayOnly).Count);
        Assert.Equal(1, responseB.ConversationsByDay.Single(d => d.Day == todayOnly).Count);
        Assert.Equal(0, responseB.ConversationsByDay.Single(d => d.Day == yesterdayOnly).Count);
    }

    // ── Metric 2 — Conversation status distribution ──────────────────

    [Fact]
    public async Task Metric2_ConversationStatusDistribution_IsolatesPerTenant_OverlappingStatuses()
    {
        var (bizA, widgetA, _) = await SeedBusinessAsync("Analytics Biz A M2");
        var (bizB, widgetB, _) = await SeedBusinessAsync("Analytics Biz B M2");
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
        var responseA = OkValue(await new AnalyticsController(new AnalyticsQueryService(dbA)).Get("7d", CancellationToken.None));
        await using var dbB = DbFor(bizB);
        var responseB = OkValue(await new AnalyticsController(new AnalyticsQueryService(dbB)).Get("7d", CancellationToken.None));

        Assert.Equal(2, responseA.ConversationStatusDistribution.Single(s => s.Status == "Open").Count);
        Assert.Equal(1, responseA.ConversationStatusDistribution.Single(s => s.Status == "Closed").Count);
        Assert.Equal(1, responseB.ConversationStatusDistribution.Single(s => s.Status == "Open").Count);
        Assert.Equal(2, responseB.ConversationStatusDistribution.Single(s => s.Status == "Closed").Count);
    }

    // ── Metric 3 — HandledBy snapshot (no range filter, Decision 1) ──

    [Fact]
    public async Task Metric3_HandledBySnapshot_IsolatesPerTenant_AndIgnoresRangeAndAge()
    {
        var (bizA, widgetA, _) = await SeedBusinessAsync("Analytics Biz A M3");
        var (bizB, widgetB, _) = await SeedBusinessAsync("Analytics Biz B M3");
        var now = DateTimeOffset.UtcNow;

        await using (var seedDb = DbFor(bizA))
        {
            // Business A: mostly Ai, including one very old conversation —
            // if the query accidentally inherited a date-range filter, this
            // old row would silently disappear from a 7d call.
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

        foreach (var range in new[] { "7d", "90d" })
        {
            await using var dbA = DbFor(bizA);
            var responseA = OkValue(await new AnalyticsController(new AnalyticsQueryService(dbA)).Get(range, CancellationToken.None));
            await using var dbB = DbFor(bizB);
            var responseB = OkValue(await new AnalyticsController(new AnalyticsQueryService(dbB)).Get(range, CancellationToken.None));

            // Decision 1: identical regardless of range — the 250/300-day-old
            // rows still count, proving no range filter leaked in.
            Assert.Equal(3, responseA.HandledBySnapshot.Single(h => h.Status == "Ai").Count);
            Assert.Equal(1, responseA.HandledBySnapshot.Single(h => h.Status == "Human").Count);
            Assert.Equal(1, responseB.HandledBySnapshot.Single(h => h.Status == "Ai").Count);
            Assert.Equal(2, responseB.HandledBySnapshot.Single(h => h.Status == "Human").Count);
        }
    }

    // ── Metric 4 — Handoff events over time ──────────────────────────

    [Fact]
    public async Task Metric4_HandoffsByDay_IsolatesPerTenant_SameCalendarDay()
    {
        var (bizA, _, _) = await SeedBusinessAsync("Analytics Biz A M4");
        var (bizB, _, _) = await SeedBusinessAsync("Analytics Biz B M4");
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
        var responseA = OkValue(await new AnalyticsController(new AnalyticsQueryService(dbA)).Get("7d", CancellationToken.None));
        await using var dbB = DbFor(bizB);
        var responseB = OkValue(await new AnalyticsController(new AnalyticsQueryService(dbB)).Get("7d", CancellationToken.None));

        Assert.Equal(3, responseA.HandoffsByDay.Single(d => d.Day == todayOnly).Count);
        Assert.Equal(1, responseB.HandoffsByDay.Single(d => d.Day == todayOnly).Count);
        Assert.Equal(3, responseA.HandoffsByDay.Sum(d => d.Count));
        Assert.Equal(1, responseB.HandoffsByDay.Sum(d => d.Count));
    }

    // ── Metric 5 — Ticket status distribution ────────────────────────

    [Fact]
    public async Task Metric5_TicketStatusDistribution_IsolatesPerTenant_AllStatusesRepresented()
    {
        var (bizA, _, customerA) = await SeedBusinessAsync("Analytics Biz A M5");
        var (bizB, _, customerB) = await SeedBusinessAsync("Analytics Biz B M5");
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
        var responseA = OkValue(await new AnalyticsController(new AnalyticsQueryService(dbA)).Get("7d", CancellationToken.None));
        await using var dbB = DbFor(bizB);
        var responseB = OkValue(await new AnalyticsController(new AnalyticsQueryService(dbB)).Get("7d", CancellationToken.None));

        Assert.Equal(2, responseA.TicketStatusDistribution.Single(s => s.Status == "Open").Count);
        Assert.Equal(1, responseA.TicketStatusDistribution.Single(s => s.Status == "InProgress").Count);
        Assert.Equal(1, responseA.TicketStatusDistribution.Single(s => s.Status == "Resolved").Count);
        Assert.Equal(1, responseA.TicketStatusDistribution.Single(s => s.Status == "Reopened").Count);

        Assert.Equal(1, responseB.TicketStatusDistribution.Single(s => s.Status == "Open").Count);
        Assert.Equal(0, responseB.TicketStatusDistribution.Single(s => s.Status == "InProgress").Count);
        Assert.Equal(1, responseB.TicketStatusDistribution.Single(s => s.Status == "Resolved").Count);
        Assert.Equal(0, responseB.TicketStatusDistribution.Single(s => s.Status == "Reopened").Count);
    }

    // ── Dense/zero-fill guarantee (§7) ────────────────────────────────

    [Fact]
    public async Task ZeroFill_EmptyBusiness_StillReturnsFullDenseShape()
    {
        var (bizA, _, _) = await SeedBusinessAsync("Analytics Biz Empty");

        await using var dbA = DbFor(bizA);
        var response = OkValue(await new AnalyticsController(new AnalyticsQueryService(dbA)).Get("7d", CancellationToken.None));

        Assert.Equal(7, response.ConversationsByDay.Count);
        Assert.All(response.ConversationsByDay, d => Assert.Equal(0, d.Count));

        Assert.Equal(7, response.HandoffsByDay.Count);
        Assert.All(response.HandoffsByDay, d => Assert.Equal(0, d.Count));

        Assert.Equal(2, response.ConversationStatusDistribution.Count);
        Assert.Contains(response.ConversationStatusDistribution, s => s.Status == "Open" && s.Count == 0);
        Assert.Contains(response.ConversationStatusDistribution, s => s.Status == "Closed" && s.Count == 0);

        Assert.Equal(2, response.HandledBySnapshot.Count);
        Assert.Contains(response.HandledBySnapshot, s => s.Status == "Ai" && s.Count == 0);
        Assert.Contains(response.HandledBySnapshot, s => s.Status == "Human" && s.Count == 0);

        Assert.Equal(4, response.TicketStatusDistribution.Count);
        Assert.Contains(response.TicketStatusDistribution, s => s.Status == "Open" && s.Count == 0);
        Assert.Contains(response.TicketStatusDistribution, s => s.Status == "InProgress" && s.Count == 0);
        Assert.Contains(response.TicketStatusDistribution, s => s.Status == "Resolved" && s.Count == 0);
        Assert.Contains(response.TicketStatusDistribution, s => s.Status == "Reopened" && s.Count == 0);
    }

    [Fact]
    public async Task ZeroFill_OneDayHasData_OtherDaysStillPresentWithZero()
    {
        var (bizA, widgetA, _) = await SeedBusinessAsync("Analytics Biz Sparse");
        var todayOnly = DateOnly.FromDateTime(DateTime.UtcNow);
        var today = UtcMidnight(todayOnly);

        await using (var seedDb = DbFor(bizA))
        {
            await AddConversationAsync(seedDb, bizA, widgetA, ConversationStatus.Open, HandledBy.Ai, today);
        }

        await using var dbA = DbFor(bizA);
        var response = OkValue(await new AnalyticsController(new AnalyticsQueryService(dbA)).Get("30d", CancellationToken.None));

        Assert.Equal(30, response.ConversationsByDay.Count);
        Assert.Equal(1, response.ConversationsByDay.Single(d => d.Day == todayOnly).Count);
        Assert.Equal(29, response.ConversationsByDay.Count(d => d.Day != todayOnly && d.Count == 0));
    }

    // ── Boundary cardinality (§8 Decision 3) ─────────────────────────

    [Theory]
    [InlineData("7d", 7)]
    [InlineData("30d", 30)]
    [InlineData("90d", 90)]
    public async Task RangePresets_ProduceExactlyNDayBucketEntries_NoOffByOne(string range, int expectedDays)
    {
        var (bizA, _, _) = await SeedBusinessAsync($"Analytics Biz Boundary {range}");

        await using var dbA = DbFor(bizA);
        var response = OkValue(await new AnalyticsController(new AnalyticsQueryService(dbA)).Get(range, CancellationToken.None));

        Assert.Equal(expectedDays, response.ConversationsByDay.Count);
        Assert.Equal(expectedDays, response.HandoffsByDay.Count);

        var todayUtc = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime);
        Assert.Equal(todayUtc.AddDays(-(expectedDays - 1)), response.RangeStart);
        Assert.Equal(todayUtc.AddDays(1), response.RangeEnd);

        // No duplicate/missing dates: the set of returned days is exactly
        // the contiguous [RangeStart, RangeEnd) calendar-day run.
        var expectedDates = Enumerable.Range(0, expectedDays).Select(response.RangeStart.AddDays).ToHashSet();
        var actualDates = response.ConversationsByDay.Select(d => d.Day).ToHashSet();
        Assert.Equal(expectedDates, actualDates);
    }

    [Fact]
    public async Task InvalidRange_ReturnsBadRequest()
    {
        var (bizA, _, _) = await SeedBusinessAsync("Analytics Biz Invalid Range");
        await using var dbA = DbFor(bizA);

        var result = await new AnalyticsController(new AnalyticsQueryService(dbA)).Get("14d", CancellationToken.None);

        Assert.IsType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>(result.Result);
    }

    private static AnalyticsResponse OkValue(Microsoft.AspNetCore.Mvc.ActionResult<AnalyticsResponse> result) =>
        Assert.IsType<AnalyticsResponse>(Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(result.Result).Value);

    private sealed class FixedTenantAccessor(Guid? businessId) : ICurrentTenantAccessor
    {
        public Guid? BusinessId => businessId;
    }
}
