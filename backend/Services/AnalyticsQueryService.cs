using backend.Controllers;
using backend.Data;
using backend.Enums;
using backend.Models;
using Microsoft.EntityFrameworkCore;

namespace backend.Services;

/// <summary>
/// Step 23 Phase 2 — verbatim extraction of the 5 query bodies and shared
/// range-parsing/zero-fill helpers that used to live directly inside
/// AnalyticsController.Get (Step 21, AnalyticsController.cs:59-131 pre-
/// extraction). The five original public methods (GetConversationsByDayAsync
/// etc.) still rely entirely on the ambient global tenant query filter
/// (AppDbContext.ApplyTenantFilter) exactly as the original inline code did
/// — no explicit BusinessId parameter, no IgnoreQueryFilters(), unchanged by
/// Step 24. Both current callers (AnalyticsController, request-scoped) and
/// the Step 23 Phase 3 capability tools (also request-scoped, per design §6)
/// run under a real tenant-scoped AppDbContext, so this carries over the
/// original code's own tenant-isolation guarantee unchanged.
///
/// Step 24 Phase 4 (design §6.2/§6.3) adds five `...ForBusinessAsync`
/// counterparts, for callers with no ambient HttpContext (the Step 24
/// background scheduler). Rather than duplicate each metric's
/// grouping/projection/zero-fill logic a second time, each original
/// method's body is refactored into a private `...CoreAsync` helper taking
/// an already-scoped `IQueryable&lt;T&gt;` — both the ambient-filtered public
/// method and the new explicit-tenant public method call the identical
/// core, differing only in how that base queryable is built (implicit
/// ambient filter vs. `IgnoreQueryFilters()` + an explicit `BusinessId ==`
/// predicate, mirroring NotificationService.ResolveRecipientsAsync's own
/// established precedent for exactly this shape). This is an internal-only
/// change — every existing public method's signature, return type, and
/// observable behavior (including its exact ambient-tenant reliance) is
/// unchanged; only where the grouping/projection code text physically
/// lives moved, not what it does for the six pre-existing members.
/// </summary>
public class AnalyticsQueryService(AppDbContext db) : IAnalyticsQueryService
{
    private static readonly Dictionary<string, int> ValidRanges = new()
    {
        ["7d"] = 7,
        ["30d"] = 30,
        ["90d"] = 90,
    };

    private static readonly ConversationStatus[] AllConversationStatuses = Enum.GetValues<ConversationStatus>();
    private static readonly HandledBy[] AllHandledByValues = Enum.GetValues<HandledBy>();
    private static readonly TicketStatus[] AllTicketStatuses = Enum.GetValues<TicketStatus>();

    public IReadOnlyCollection<string> ValidRangeKeys => ValidRanges.Keys;

    public bool TryParseRange(string? range, out AnalyticsRangeWindow window)
    {
        if (range is null || !ValidRanges.TryGetValue(range, out var days))
        {
            window = default;
            return false;
        }

        // Decision 3 (§8): calendar-day windows, half-open [start, end),
        // all boundaries in UTC.
        var todayUtc = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime);
        var rangeStartDate = todayUtc.AddDays(-(days - 1));
        var rangeEndExclusiveDate = todayUtc.AddDays(1);
        var allDays = Enumerable.Range(0, days).Select(rangeStartDate.AddDays).ToList();

        window = new AnalyticsRangeWindow(
            Range: range,
            RangeStartDate: rangeStartDate,
            RangeEndDateExclusive: rangeEndExclusiveDate,
            RangeStart: ToUtcMidnight(rangeStartDate),
            RangeEndExclusive: ToUtcMidnight(rangeEndExclusiveDate),
            AllDays: allDays);
        return true;
    }

    // Metric 1 — ambient-filtered (existing callers: AnalyticsController,
    // Step 23 Analyst capability). db.Conversations here relies on
    // AppDbContext's global tenant query filter, exactly as before Step 24.
    public Task<List<DayCountEntry>> GetConversationsByDayAsync(AnalyticsRangeWindow window, CancellationToken cancellationToken) =>
        GetConversationsByDayCoreAsync(db.Conversations, window, cancellationToken);

    // Metric 1 — Step 24 Phase 4 explicit-tenant counterpart. IgnoreQueryFilters()
    // bypasses the ambient (HttpContext-only) global filter entirely; the
    // explicit BusinessId predicate immediately after is the only tenant
    // boundary this query has — mirrors NotificationService.ResolveRecipientsAsync's
    // own established shape. The ambient ICurrentTenantAccessor is never
    // consulted anywhere in this method or in GetConversationsByDayCoreAsync.
    public Task<List<DayCountEntry>> GetConversationsByDayForBusinessAsync(Guid businessId, AnalyticsRangeWindow window, CancellationToken cancellationToken) =>
        GetConversationsByDayCoreAsync(
            db.Conversations.IgnoreQueryFilters().Where(c => c.BusinessId == businessId), window, cancellationToken);

    private static async Task<List<DayCountEntry>> GetConversationsByDayCoreAsync(
        IQueryable<Conversation> source, AnalyticsRangeWindow window, CancellationToken cancellationToken)
    {
        var raw = await source
            .Where(c => c.CreatedAt >= window.RangeStart && c.CreatedAt < window.RangeEndExclusive)
            .GroupBy(c => c.CreatedAt.Date)
            .Select(g => new { Day = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        return ZeroFillDays(window.AllDays, raw.ToDictionary(x => DateOnly.FromDateTime(x.Day), x => x.Count));
    }

    // Metric 2 — see Metric 1's comments for the ambient-vs-explicit shape;
    // identical reasoning applies to every metric below, not restated per-metric.
    public Task<List<StatusCountEntry>> GetConversationStatusDistributionAsync(AnalyticsRangeWindow window, CancellationToken cancellationToken) =>
        GetConversationStatusDistributionCoreAsync(db.Conversations, window, cancellationToken);

    public Task<List<StatusCountEntry>> GetConversationStatusDistributionForBusinessAsync(Guid businessId, AnalyticsRangeWindow window, CancellationToken cancellationToken) =>
        GetConversationStatusDistributionCoreAsync(
            db.Conversations.IgnoreQueryFilters().Where(c => c.BusinessId == businessId), window, cancellationToken);

    private static async Task<List<StatusCountEntry>> GetConversationStatusDistributionCoreAsync(
        IQueryable<Conversation> source, AnalyticsRangeWindow window, CancellationToken cancellationToken)
    {
        var raw = await source
            .Where(c => c.CreatedAt >= window.RangeStart && c.CreatedAt < window.RangeEndExclusive)
            .GroupBy(c => c.Status)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        return ZeroFillEnum(AllConversationStatuses, raw.ToDictionary(x => x.Key, x => x.Count));
    }

    // Metric 3 — Decision 1 (§9): all conversations, no range filter at all.
    public Task<List<StatusCountEntry>> GetHandledBySnapshotAsync(CancellationToken cancellationToken) =>
        GetHandledBySnapshotCoreAsync(db.Conversations, cancellationToken);

    public Task<List<StatusCountEntry>> GetHandledBySnapshotForBusinessAsync(Guid businessId, CancellationToken cancellationToken) =>
        GetHandledBySnapshotCoreAsync(
            db.Conversations.IgnoreQueryFilters().Where(c => c.BusinessId == businessId), cancellationToken);

    private static async Task<List<StatusCountEntry>> GetHandledBySnapshotCoreAsync(
        IQueryable<Conversation> source, CancellationToken cancellationToken)
    {
        var raw = await source
            .GroupBy(c => c.HandledBy)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        return ZeroFillEnum(AllHandledByValues, raw.ToDictionary(x => x.Key, x => x.Count));
    }

    // Metric 4
    public Task<List<DayCountEntry>> GetHandoffsByDayAsync(AnalyticsRangeWindow window, CancellationToken cancellationToken) =>
        GetHandoffsByDayCoreAsync(db.ConversationHandoffs, window, cancellationToken);

    public Task<List<DayCountEntry>> GetHandoffsByDayForBusinessAsync(Guid businessId, AnalyticsRangeWindow window, CancellationToken cancellationToken) =>
        GetHandoffsByDayCoreAsync(
            db.ConversationHandoffs.IgnoreQueryFilters().Where(h => h.BusinessId == businessId), window, cancellationToken);

    private static async Task<List<DayCountEntry>> GetHandoffsByDayCoreAsync(
        IQueryable<ConversationHandoff> source, AnalyticsRangeWindow window, CancellationToken cancellationToken)
    {
        var raw = await source
            .Where(h => h.CreatedAt >= window.RangeStart && h.CreatedAt < window.RangeEndExclusive)
            .GroupBy(h => h.CreatedAt.Date)
            .Select(g => new { Day = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        return ZeroFillDays(window.AllDays, raw.ToDictionary(x => DateOnly.FromDateTime(x.Day), x => x.Count));
    }

    // Metric 5
    public Task<List<StatusCountEntry>> GetTicketStatusDistributionAsync(AnalyticsRangeWindow window, CancellationToken cancellationToken) =>
        GetTicketStatusDistributionCoreAsync(db.Tickets, window, cancellationToken);

    public Task<List<StatusCountEntry>> GetTicketStatusDistributionForBusinessAsync(Guid businessId, AnalyticsRangeWindow window, CancellationToken cancellationToken) =>
        GetTicketStatusDistributionCoreAsync(
            db.Tickets.IgnoreQueryFilters().Where(t => t.BusinessId == businessId), window, cancellationToken);

    private static async Task<List<StatusCountEntry>> GetTicketStatusDistributionCoreAsync(
        IQueryable<Ticket> source, AnalyticsRangeWindow window, CancellationToken cancellationToken)
    {
        var raw = await source
            .Where(t => t.CreatedAt >= window.RangeStart && t.CreatedAt < window.RangeEndExclusive)
            .GroupBy(t => t.Status)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        return ZeroFillEnum(AllTicketStatuses, raw.ToDictionary(x => x.Key, x => x.Count));
    }

    // Step 24 Phase 4 (design §6.2) — builds a window from explicit UTC
    // calendar-period boundaries (Phase 2's BusinessWeekPeriodCalculator
    // output) instead of parsing a range key. Purely additive: does not
    // touch TryParseRange or any 7d/30d/90d semantics. AllDays is built the
    // same way TryParseRange builds it (every calendar day in
    // [periodStartDate, periodEndDateExclusive)), so the existing
    // ZeroFillDays helper works unchanged for calendar-period callers too.
    public AnalyticsRangeWindow BuildCalendarWindow(
        DateOnly periodStartDate, DateOnly periodEndDateExclusive,
        DateTimeOffset periodStartUtc, DateTimeOffset periodEndUtcExclusive)
    {
        var dayCount = periodEndDateExclusive.DayNumber - periodStartDate.DayNumber;
        var allDays = Enumerable.Range(0, dayCount).Select(periodStartDate.AddDays).ToList();

        return new AnalyticsRangeWindow(
            Range: "calendar",
            RangeStartDate: periodStartDate,
            RangeEndDateExclusive: periodEndDateExclusive,
            RangeStart: periodStartUtc,
            RangeEndExclusive: periodEndUtcExclusive,
            AllDays: allDays);
    }

    private static DateTimeOffset ToUtcMidnight(DateOnly date) =>
        new(date.Year, date.Month, date.Day, 0, 0, 0, TimeSpan.Zero);

    // Dense-array guarantee (§7, Correction 2): every calendar day in range
    // is present, Count: 0 substituted for any day the raw query didn't
    // return — never a sparse result left for the caller to fill in.
    private static List<DayCountEntry> ZeroFillDays(IReadOnlyList<DateOnly> days, Dictionary<DateOnly, int> counts) =>
        days.Select(d => new DayCountEntry(d, counts.GetValueOrDefault(d))).ToList();

    // Same guarantee, for the enum-distribution metrics: every defined enum
    // value is present, Count: 0 for any value the raw query didn't return.
    private static List<StatusCountEntry> ZeroFillEnum<TEnum>(TEnum[] values, Dictionary<TEnum, int> counts)
        where TEnum : struct, Enum =>
        values.Select(v => new StatusCountEntry(v.ToString(), counts.GetValueOrDefault(v))).ToList();
}
