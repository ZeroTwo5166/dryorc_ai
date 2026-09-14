using backend.Constants;
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
/// Step 24 Phase 4 — adversarial tenant-isolation proof for the five
/// `...ForBusinessAsync` explicit-tenant analytics methods and
/// BuildCalendarWindow (design §6.2/§6.3; implementation plan §3.1).
///
/// Mirrors AnalyticsQueryServiceTests.cs's exact seeding pattern
/// (SeedBusinessAsync/AddConversationAsync/AddTicketAsync/AddHandoffAsync),
/// but the property under test here is materially different and stronger:
/// AnalyticsQueryServiceTests.cs proves the EXISTING (ambient-filtered)
/// methods isolate tenants correctly when each caller supplies its own
/// correct ambient tenant. This file proves the NEW methods' explicit
/// businessId parameter is authoritative REGARDLESS of what the ambient
/// tenant accessor says — including when it is null (no HttpContext, the
/// real Phase 5/6 shape) and when it is actively wrong (the adversarial
/// case: an ambient tenant pointing at a different business entirely).
///
/// Business A and Business B are seeded with deliberately large,
/// non-overlapping counts (not just "different") so that any leakage,
/// partial mixing, or zero-fill is immediately and unambiguously visible
/// in the assertions, never merely plausible-looking.
/// </summary>
public class AnalyticsQueryServiceExplicitTenantTests : IAsyncLifetime
{
    private readonly List<Guid> _businessIdsToClean = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_businessIdsToClean.Count > 0)
        {
            await using var db = DbFor(null);
            var ids = _businessIdsToClean.ToArray();

            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"ConversationHandoffs\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Tickets\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Conversations\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Customers\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Widgets\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Businesses\" WHERE \"Id\" = ANY({ids})");
        }
    }

    // ── Seeding helpers — identical shape to AnalyticsQueryServiceTests.cs ──

    private async Task<(Guid BusinessId, Guid WidgetId, Guid CustomerId)> SeedBusinessAsync(string namePrefix)
    {
        await using var db = DbFor(null);
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
            PublicKey = $"analytics-et-test-{Guid.NewGuid():N}",
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

    private static DateTimeOffset UtcMidnight(DateOnly date) =>
        new(date.Year, date.Month, date.Day, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Builds an AppDbContext whose ICurrentTenantAccessor.BusinessId is
    /// fixed to exactly the given value - null (no HttpContext, the real
    /// background-scheduler shape), a wrong business, or the right one.
    /// This is the entire mechanism by which each test controls "the
    /// ambient tenant" independently of the explicit businessId argument
    /// passed to the method under test.
    /// </summary>
    private static AppDbContext DbFor(Guid? ambientTenant) =>
        new ServiceCollection()
            .AddSingleton<ICurrentTenantAccessor>(new FixedTenantAccessor(ambientTenant))
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

    // ── Shared fixture: two businesses, deliberately large,
    //    non-overlapping datasets across all five metrics ───────────────

    private sealed record Fixture(
        Guid BizA, Guid WidgetA, Guid CustomerA,
        Guid BizB, Guid WidgetB, Guid CustomerB,
        DateOnly TodayOnly, DateTimeOffset Today, DateTimeOffset Yesterday,
        AnalyticsRangeWindow WindowA, AnalyticsRangeWindow WindowB);

    private async Task<Fixture> SeedFixtureAsync(string label)
    {
        var (bizA, widgetA, customerA) = await SeedBusinessAsync($"Explicit Tenant {label} A");
        var (bizB, widgetB, customerB) = await SeedBusinessAsync($"Explicit Tenant {label} B");
        var todayOnly = DateOnly.FromDateTime(DateTime.UtcNow);
        var today = UtcMidnight(todayOnly);
        var yesterday = today.AddDays(-1);

        await using (var seedDb = DbFor(null))
        {
            // Business A: 3 Open/Ai today, 2 Open/Ai yesterday, 1 Closed/Human
            // today = 6 conversations, distribution Open=5/Closed=1, HandledBy
            // Ai=5/Human=1. 3 handoffs today. Tickets: Open=2/InProgress=1/
            // Resolved=1/Reopened=1.
            await AddConversationAsync(seedDb, bizA, widgetA, ConversationStatus.Open, HandledBy.Ai, today);
            await AddConversationAsync(seedDb, bizA, widgetA, ConversationStatus.Open, HandledBy.Ai, today);
            await AddConversationAsync(seedDb, bizA, widgetA, ConversationStatus.Open, HandledBy.Ai, today);
            await AddConversationAsync(seedDb, bizA, widgetA, ConversationStatus.Open, HandledBy.Ai, yesterday);
            await AddConversationAsync(seedDb, bizA, widgetA, ConversationStatus.Open, HandledBy.Ai, yesterday);
            await AddConversationAsync(seedDb, bizA, widgetA, ConversationStatus.Closed, HandledBy.Human, today);
            await AddHandoffAsync(seedDb, bizA, today);
            await AddHandoffAsync(seedDb, bizA, today);
            await AddHandoffAsync(seedDb, bizA, today);
            await AddTicketAsync(seedDb, bizA, customerA, TicketStatus.Open, today);
            await AddTicketAsync(seedDb, bizA, customerA, TicketStatus.Open, today);
            await AddTicketAsync(seedDb, bizA, customerA, TicketStatus.InProgress, today);
            await AddTicketAsync(seedDb, bizA, customerA, TicketStatus.Resolved, today);
            await AddTicketAsync(seedDb, bizA, customerA, TicketStatus.Reopened, today);
        }

        await using (var seedDb = DbFor(null))
        {
            // Business B: a deliberately much larger, differently-shaped
            // dataset - 12 conversations today, all Human/Closed except one,
            // 0 yesterday. 9 handoffs today. Tickets: Open=6/Resolved=3.
            // Every count below is chosen so it cannot be confused with any
            // of Business A's counts above, in either direction.
            for (var i = 0; i < 11; i++)
            {
                await AddConversationAsync(seedDb, bizB, widgetB, ConversationStatus.Closed, HandledBy.Human, today);
            }
            await AddConversationAsync(seedDb, bizB, widgetB, ConversationStatus.Open, HandledBy.Ai, today);
            for (var i = 0; i < 9; i++)
            {
                await AddHandoffAsync(seedDb, bizB, today);
            }
            for (var i = 0; i < 6; i++)
            {
                await AddTicketAsync(seedDb, bizB, customerB, TicketStatus.Open, today);
            }
            for (var i = 0; i < 3; i++)
            {
                await AddTicketAsync(seedDb, bizB, customerB, TicketStatus.Resolved, today);
            }
        }

        // Built via BuildCalendarWindow itself (the real Phase 5/6
        // consumption shape), not TryParseRange - this doubles as
        // BuildCalendarWindow's own correctness/compatibility proof
        // (exercised through every test in this file).
        await using var probeDb = DbFor(null);
        var probeService = new AnalyticsQueryService(probeDb);
        var window = probeService.BuildCalendarWindow(
            periodStartDate: todayOnly.AddDays(-1),
            periodEndDateExclusive: todayOnly.AddDays(1),
            periodStartUtc: yesterday,
            periodEndUtcExclusive: today.AddDays(1));

        return new Fixture(bizA, widgetA, customerA, bizB, widgetB, customerB, todayOnly, today, yesterday, window, window);
    }

    // ── BuildCalendarWindow correctness (also exercised indirectly by
    //    every test below, since all of them use its output) ────────────

    [Fact]
    public async Task BuildCalendarWindow_ProducesExpectedShape_ExclusiveEnd_CompatibleWithExistingQueryMethods()
    {
        var f = await SeedFixtureAsync("CalWin");

        Assert.Equal("calendar", f.WindowA.Range);
        Assert.Equal(f.TodayOnly.AddDays(-1), f.WindowA.RangeStartDate);
        Assert.Equal(f.TodayOnly.AddDays(1), f.WindowA.RangeEndDateExclusive);
        Assert.Equal(f.Yesterday, f.WindowA.RangeStart);
        Assert.Equal(f.Today.AddDays(1), f.WindowA.RangeEndExclusive);
        Assert.Equal(2, f.WindowA.AllDays.Count); // [yesterday, today] - exclusive of the day after today
        Assert.Contains(f.TodayOnly.AddDays(-1), f.WindowA.AllDays);
        Assert.Contains(f.TodayOnly, f.WindowA.AllDays);
        Assert.DoesNotContain(f.TodayOnly.AddDays(1), f.WindowA.AllDays);

        // Compatible with the existing (ambient-filtered) query methods too
        // - BuildCalendarWindow's output is a real AnalyticsRangeWindow, not
        // a parallel type the old methods can't consume.
        await using var dbA = DbFor(f.BizA);
        var resultA = await new AnalyticsQueryService(dbA).GetConversationsByDayAsync(f.WindowA, CancellationToken.None);
        Assert.Equal(6, resultA.Sum(d => d.Count));
    }

    // ── Metric 1 — Conversations/day ─────────────────────────────────

    [Fact]
    public async Task GetConversationsByDayForBusinessAsync_NoAmbientTenant_ReturnsRealNonZeroBusinessAData()
    {
        var f = await SeedFixtureAsync("M1-NoAmbient");
        await using var db = DbFor(null);
        var result = await new AnalyticsQueryService(db).GetConversationsByDayForBusinessAsync(f.BizA, f.WindowA, CancellationToken.None);

        Assert.Equal(6, result.Sum(d => d.Count));
        Assert.Equal(4, result.Single(d => d.Day == f.TodayOnly).Count); // 3 Open/Ai + 1 Closed/Human, same day
        Assert.Equal(2, result.Single(d => d.Day == f.TodayOnly.AddDays(-1)).Count);
    }

    [Fact]
    public async Task GetConversationsByDayForBusinessAsync_WrongAmbientTenant_StillReturnsBusinessA_NeverBusinessB()
    {
        var f = await SeedFixtureAsync("M1-WrongAmbient");
        await using var db = DbFor(f.BizB); // ambient = B, explicit = A
        var result = await new AnalyticsQueryService(db).GetConversationsByDayForBusinessAsync(f.BizA, f.WindowA, CancellationToken.None);

        Assert.Equal(6, result.Sum(d => d.Count)); // A's total, never B's 12
        Assert.Equal(4, result.Single(d => d.Day == f.TodayOnly).Count); // A's 4 (3 Open + 1 Closed), never B's 12
    }

    [Fact]
    public async Task GetConversationsByDayForBusinessAsync_ReverseDirection_AmbientA_ExplicitB_ReturnsB()
    {
        var f = await SeedFixtureAsync("M1-Reverse");
        await using var db = DbFor(f.BizA); // ambient = A, explicit = B
        var result = await new AnalyticsQueryService(db).GetConversationsByDayForBusinessAsync(f.BizB, f.WindowA, CancellationToken.None);

        Assert.Equal(12, result.Sum(d => d.Count)); // B's total, never A's 6
    }

    // ── Metric 2 — Conversation status distribution ──────────────────

    [Fact]
    public async Task GetConversationStatusDistributionForBusinessAsync_NoAmbientTenant_ReturnsRealBusinessAData()
    {
        var f = await SeedFixtureAsync("M2-NoAmbient");
        await using var db = DbFor(null);
        var result = await new AnalyticsQueryService(db).GetConversationStatusDistributionForBusinessAsync(f.BizA, f.WindowA, CancellationToken.None);

        Assert.Equal(5, result.Single(s => s.Status == "Open").Count);
        Assert.Equal(1, result.Single(s => s.Status == "Closed").Count);
    }

    [Fact]
    public async Task GetConversationStatusDistributionForBusinessAsync_WrongAmbientTenant_StillReturnsBusinessA()
    {
        var f = await SeedFixtureAsync("M2-WrongAmbient");
        await using var db = DbFor(f.BizB);
        var result = await new AnalyticsQueryService(db).GetConversationStatusDistributionForBusinessAsync(f.BizA, f.WindowA, CancellationToken.None);

        Assert.Equal(5, result.Single(s => s.Status == "Open").Count); // never B's 1
        Assert.Equal(1, result.Single(s => s.Status == "Closed").Count); // never B's 11
    }

    [Fact]
    public async Task GetConversationStatusDistributionForBusinessAsync_ReverseDirection_ReturnsB()
    {
        var f = await SeedFixtureAsync("M2-Reverse");
        await using var db = DbFor(f.BizA);
        var result = await new AnalyticsQueryService(db).GetConversationStatusDistributionForBusinessAsync(f.BizB, f.WindowA, CancellationToken.None);

        Assert.Equal(1, result.Single(s => s.Status == "Open").Count);
        Assert.Equal(11, result.Single(s => s.Status == "Closed").Count);
    }

    // ── Metric 3 — HandledBy snapshot (no window) ─────────────────────

    [Fact]
    public async Task GetHandledBySnapshotForBusinessAsync_NoAmbientTenant_ReturnsRealBusinessAData()
    {
        var f = await SeedFixtureAsync("M3-NoAmbient");
        await using var db = DbFor(null);
        var result = await new AnalyticsQueryService(db).GetHandledBySnapshotForBusinessAsync(f.BizA, CancellationToken.None);

        Assert.Equal(5, result.Single(h => h.Status == "Ai").Count);
        Assert.Equal(1, result.Single(h => h.Status == "Human").Count);
    }

    [Fact]
    public async Task GetHandledBySnapshotForBusinessAsync_WrongAmbientTenant_StillReturnsBusinessA()
    {
        var f = await SeedFixtureAsync("M3-WrongAmbient");
        await using var db = DbFor(f.BizB);
        var result = await new AnalyticsQueryService(db).GetHandledBySnapshotForBusinessAsync(f.BizA, CancellationToken.None);

        Assert.Equal(5, result.Single(h => h.Status == "Ai").Count); // never B's 1
        Assert.Equal(1, result.Single(h => h.Status == "Human").Count); // never B's 11
    }

    [Fact]
    public async Task GetHandledBySnapshotForBusinessAsync_ReverseDirection_ReturnsB()
    {
        var f = await SeedFixtureAsync("M3-Reverse");
        await using var db = DbFor(f.BizA);
        var result = await new AnalyticsQueryService(db).GetHandledBySnapshotForBusinessAsync(f.BizB, CancellationToken.None);

        Assert.Equal(1, result.Single(h => h.Status == "Ai").Count);
        Assert.Equal(11, result.Single(h => h.Status == "Human").Count);
    }

    // ── Metric 4 — Handoffs/day ────────────────────────────────────────

    [Fact]
    public async Task GetHandoffsByDayForBusinessAsync_NoAmbientTenant_ReturnsRealBusinessAData()
    {
        var f = await SeedFixtureAsync("M4-NoAmbient");
        await using var db = DbFor(null);
        var result = await new AnalyticsQueryService(db).GetHandoffsByDayForBusinessAsync(f.BizA, f.WindowA, CancellationToken.None);

        Assert.Equal(3, result.Single(d => d.Day == f.TodayOnly).Count);
        Assert.Equal(3, result.Sum(d => d.Count));
    }

    [Fact]
    public async Task GetHandoffsByDayForBusinessAsync_WrongAmbientTenant_StillReturnsBusinessA()
    {
        var f = await SeedFixtureAsync("M4-WrongAmbient");
        await using var db = DbFor(f.BizB);
        var result = await new AnalyticsQueryService(db).GetHandoffsByDayForBusinessAsync(f.BizA, f.WindowA, CancellationToken.None);

        Assert.Equal(3, result.Single(d => d.Day == f.TodayOnly).Count); // never B's 9
    }

    [Fact]
    public async Task GetHandoffsByDayForBusinessAsync_ReverseDirection_ReturnsB()
    {
        var f = await SeedFixtureAsync("M4-Reverse");
        await using var db = DbFor(f.BizA);
        var result = await new AnalyticsQueryService(db).GetHandoffsByDayForBusinessAsync(f.BizB, f.WindowA, CancellationToken.None);

        Assert.Equal(9, result.Single(d => d.Day == f.TodayOnly).Count);
    }

    // ── Metric 5 — Ticket status distribution ─────────────────────────

    [Fact]
    public async Task GetTicketStatusDistributionForBusinessAsync_NoAmbientTenant_ReturnsRealBusinessAData()
    {
        var f = await SeedFixtureAsync("M5-NoAmbient");
        await using var db = DbFor(null);
        var result = await new AnalyticsQueryService(db).GetTicketStatusDistributionForBusinessAsync(f.BizA, f.WindowA, CancellationToken.None);

        Assert.Equal(2, result.Single(s => s.Status == "Open").Count);
        Assert.Equal(1, result.Single(s => s.Status == "InProgress").Count);
        Assert.Equal(1, result.Single(s => s.Status == "Resolved").Count);
        Assert.Equal(1, result.Single(s => s.Status == "Reopened").Count);
    }

    [Fact]
    public async Task GetTicketStatusDistributionForBusinessAsync_WrongAmbientTenant_StillReturnsBusinessA()
    {
        var f = await SeedFixtureAsync("M5-WrongAmbient");
        await using var db = DbFor(f.BizB);
        var result = await new AnalyticsQueryService(db).GetTicketStatusDistributionForBusinessAsync(f.BizA, f.WindowA, CancellationToken.None);

        Assert.Equal(2, result.Single(s => s.Status == "Open").Count); // never B's 6
        Assert.Equal(1, result.Single(s => s.Status == "Resolved").Count); // never B's 3
    }

    [Fact]
    public async Task GetTicketStatusDistributionForBusinessAsync_ReverseDirection_ReturnsB()
    {
        var f = await SeedFixtureAsync("M5-Reverse");
        await using var db = DbFor(f.BizA);
        var result = await new AnalyticsQueryService(db).GetTicketStatusDistributionForBusinessAsync(f.BizB, f.WindowA, CancellationToken.None);

        Assert.Equal(6, result.Single(s => s.Status == "Open").Count);
        Assert.Equal(3, result.Single(s => s.Status == "Resolved").Count);
    }

    // ── The "silent zero-fill" failure mode, closed directly ───────────
    // (the specific bug design §3.1 found: calling an AMBIENT-filtered
    // method from a no-tenant scope comes back zero-filled, not erroring -
    // this is the negative control proving THIS test harness itself can
    // detect that failure mode, distinguishing it from the new methods'
    // correctly non-zero behavior above.)

    [Fact]
    public async Task NegativeControl_AmbientFilteredMethod_FromNoTenantScope_IsSilentlyZeroFilled_NotExplicitTenantMethod()
    {
        var f = await SeedFixtureAsync("NegControl");

        await using var db = DbFor(null);
        var service = new AnalyticsQueryService(db);

        var ambientResult = await service.GetConversationsByDayAsync(f.WindowA, CancellationToken.None);
        Assert.Equal(0, ambientResult.Sum(d => d.Count)); // the bug this phase exists to route around

        var explicitResult = await service.GetConversationsByDayForBusinessAsync(f.BizA, f.WindowA, CancellationToken.None);
        Assert.Equal(6, explicitResult.Sum(d => d.Count)); // the fix
    }
}
