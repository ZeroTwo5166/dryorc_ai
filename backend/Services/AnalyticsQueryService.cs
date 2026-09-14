using backend.Controllers;
using backend.Data;
using backend.Enums;
using Microsoft.EntityFrameworkCore;

namespace backend.Services;

/// <summary>
/// Step 23 Phase 2 — verbatim extraction of the 5 query bodies and shared
/// range-parsing/zero-fill helpers that used to live directly inside
/// AnalyticsController.Get (Step 21, AnalyticsController.cs:59-131 pre-
/// extraction). Relies entirely on the ambient global tenant query filter
/// (AppDbContext.ApplyTenantFilter) exactly as the original inline code did
/// — no explicit BusinessId parameter, no IgnoreQueryFilters(). Both current
/// callers (AnalyticsController, request-scoped) and the Step 23 Phase 3
/// capability tools (also request-scoped, per design §6) run under a real
/// tenant-scoped AppDbContext, so this carries over the original code's own
/// tenant-isolation guarantee unchanged.
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

    // Metric 1
    public async Task<List<DayCountEntry>> GetConversationsByDayAsync(AnalyticsRangeWindow window, CancellationToken cancellationToken)
    {
        var raw = await db.Conversations
            .Where(c => c.CreatedAt >= window.RangeStart && c.CreatedAt < window.RangeEndExclusive)
            .GroupBy(c => c.CreatedAt.Date)
            .Select(g => new { Day = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        return ZeroFillDays(window.AllDays, raw.ToDictionary(x => DateOnly.FromDateTime(x.Day), x => x.Count));
    }

    // Metric 2
    public async Task<List<StatusCountEntry>> GetConversationStatusDistributionAsync(AnalyticsRangeWindow window, CancellationToken cancellationToken)
    {
        var raw = await db.Conversations
            .Where(c => c.CreatedAt >= window.RangeStart && c.CreatedAt < window.RangeEndExclusive)
            .GroupBy(c => c.Status)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        return ZeroFillEnum(AllConversationStatuses, raw.ToDictionary(x => x.Key, x => x.Count));
    }

    // Metric 3 — Decision 1 (§9): all conversations, no range filter at all.
    public async Task<List<StatusCountEntry>> GetHandledBySnapshotAsync(CancellationToken cancellationToken)
    {
        var raw = await db.Conversations
            .GroupBy(c => c.HandledBy)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        return ZeroFillEnum(AllHandledByValues, raw.ToDictionary(x => x.Key, x => x.Count));
    }

    // Metric 4
    public async Task<List<DayCountEntry>> GetHandoffsByDayAsync(AnalyticsRangeWindow window, CancellationToken cancellationToken)
    {
        var raw = await db.ConversationHandoffs
            .Where(h => h.CreatedAt >= window.RangeStart && h.CreatedAt < window.RangeEndExclusive)
            .GroupBy(h => h.CreatedAt.Date)
            .Select(g => new { Day = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        return ZeroFillDays(window.AllDays, raw.ToDictionary(x => DateOnly.FromDateTime(x.Day), x => x.Count));
    }

    // Metric 5
    public async Task<List<StatusCountEntry>> GetTicketStatusDistributionAsync(AnalyticsRangeWindow window, CancellationToken cancellationToken)
    {
        var raw = await db.Tickets
            .Where(t => t.CreatedAt >= window.RangeStart && t.CreatedAt < window.RangeEndExclusive)
            .GroupBy(t => t.Status)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        return ZeroFillEnum(AllTicketStatuses, raw.ToDictionary(x => x.Key, x => x.Count));
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
