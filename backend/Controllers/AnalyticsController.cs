using backend.Auth;
using backend.Data;
using backend.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace backend.Controllers;

public record DayCountEntry(DateOnly Day, int Count);

public record StatusCountEntry(string Status, int Count);

/// <summary>
/// Step 21 — Basic Analytics. Shape and semantics locked by
/// docs/handoffs/step-21-analytics-design.md §7. RangeStart/RangeEnd echo
/// the half-open [RangeStart, RangeEnd) UTC calendar-day window (§8,
/// Decision 3) — RangeEnd is the exclusive upper bound, one day past the
/// last included calendar date, not the last included date itself.
///
/// HandledBySnapshot (Metric 3) is the one field here that ignores Range/
/// RangeStart/RangeEnd entirely — an all-history current snapshot, locked
/// by Decision 1 (§9). It is included in this same response for UI
/// convenience only.
/// </summary>
public record AnalyticsResponse(
    string Range,
    DateOnly RangeStart,
    DateOnly RangeEnd,
    List<DayCountEntry> ConversationsByDay,
    List<StatusCountEntry> ConversationStatusDistribution,
    List<StatusCountEntry> HandledBySnapshot,
    List<DayCountEntry> HandoffsByDay,
    List<StatusCountEntry> TicketStatusDistribution);

/// <summary>
/// Single combined endpoint per design §7 — one round trip for the whole
/// dashboard view, not a per-metric split. Relies entirely on the ambient
/// global tenant query filter (AppDbContext.ApplyTenantFilter); no
/// per-row SameTenant check is needed, the same reasoning already
/// established for every other list/aggregate read in this codebase.
/// </summary>
[ApiController]
[Route("api/analytics")]
[Authorize(Policy = NivoraPolicies.AnyStaff)]
public class AnalyticsController(AppDbContext db) : ControllerBase
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

    [HttpGet]
    public async Task<ActionResult<AnalyticsResponse>> Get([FromQuery] string? range, CancellationToken cancellationToken)
    {
        if (range is null || !ValidRanges.TryGetValue(range, out var days))
        {
            return BadRequest($"range must be one of: {string.Join(", ", ValidRanges.Keys)}");
        }

        // Decision 3 (§8): calendar-day windows, half-open [start, end),
        // all boundaries in UTC.
        var todayUtc = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime);
        var rangeStartDate = todayUtc.AddDays(-(days - 1));
        var rangeEndExclusiveDate = todayUtc.AddDays(1);
        var rangeStart = ToUtcMidnight(rangeStartDate);
        var rangeEndExclusive = ToUtcMidnight(rangeEndExclusiveDate);

        // Metric 1
        var conversationsByDayRaw = await db.Conversations
            .Where(c => c.CreatedAt >= rangeStart && c.CreatedAt < rangeEndExclusive)
            .GroupBy(c => c.CreatedAt.Date)
            .Select(g => new { Day = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        // Metric 2
        var conversationStatusRaw = await db.Conversations
            .Where(c => c.CreatedAt >= rangeStart && c.CreatedAt < rangeEndExclusive)
            .GroupBy(c => c.Status)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        // Metric 3 — Decision 1 (§9): all conversations, no range filter at all.
        var handledByRaw = await db.Conversations
            .GroupBy(c => c.HandledBy)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        // Metric 4
        var handoffsByDayRaw = await db.ConversationHandoffs
            .Where(h => h.CreatedAt >= rangeStart && h.CreatedAt < rangeEndExclusive)
            .GroupBy(h => h.CreatedAt.Date)
            .Select(g => new { Day = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        // Metric 5
        var ticketStatusRaw = await db.Tickets
            .Where(t => t.CreatedAt >= rangeStart && t.CreatedAt < rangeEndExclusive)
            .GroupBy(t => t.Status)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var allDays = Enumerable.Range(0, days).Select(rangeStartDate.AddDays).ToList();

        var response = new AnalyticsResponse(
            Range: range,
            RangeStart: rangeStartDate,
            RangeEnd: rangeEndExclusiveDate,
            ConversationsByDay: ZeroFillDays(allDays, conversationsByDayRaw.ToDictionary(x => DateOnly.FromDateTime(x.Day), x => x.Count)),
            ConversationStatusDistribution: ZeroFillEnum(AllConversationStatuses, conversationStatusRaw.ToDictionary(x => x.Key, x => x.Count)),
            HandledBySnapshot: ZeroFillEnum(AllHandledByValues, handledByRaw.ToDictionary(x => x.Key, x => x.Count)),
            HandoffsByDay: ZeroFillDays(allDays, handoffsByDayRaw.ToDictionary(x => DateOnly.FromDateTime(x.Day), x => x.Count)),
            TicketStatusDistribution: ZeroFillEnum(AllTicketStatuses, ticketStatusRaw.ToDictionary(x => x.Key, x => x.Count)));

        return Ok(response);
    }

    private static DateTimeOffset ToUtcMidnight(DateOnly date) =>
        new(date.Year, date.Month, date.Day, 0, 0, 0, TimeSpan.Zero);

    // Dense-array guarantee (§7, Correction 2): every calendar day in range
    // is present, Count: 0 substituted for any day the raw query didn't
    // return — never a sparse result left for the caller to fill in.
    private static List<DayCountEntry> ZeroFillDays(List<DateOnly> days, Dictionary<DateOnly, int> counts) =>
        days.Select(d => new DayCountEntry(d, counts.GetValueOrDefault(d))).ToList();

    // Same guarantee, for the enum-distribution metrics: every defined enum
    // value is present, Count: 0 for any value the raw query didn't return.
    private static List<StatusCountEntry> ZeroFillEnum<TEnum>(TEnum[] values, Dictionary<TEnum, int> counts)
        where TEnum : struct, Enum =>
        values.Select(v => new StatusCountEntry(v.ToString(), counts.GetValueOrDefault(v))).ToList();
}
