using backend.Auth;
using backend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

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
public class AnalyticsController(IAnalyticsQueryService analyticsQueryService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<AnalyticsResponse>> Get([FromQuery] string? range, CancellationToken cancellationToken)
    {
        if (!analyticsQueryService.TryParseRange(range, out var window))
        {
            return BadRequest($"range must be one of: {string.Join(", ", analyticsQueryService.ValidRangeKeys)}");
        }

        var response = new AnalyticsResponse(
            Range: window.Range,
            RangeStart: window.RangeStartDate,
            RangeEnd: window.RangeEndDateExclusive,
            ConversationsByDay: await analyticsQueryService.GetConversationsByDayAsync(window, cancellationToken),
            ConversationStatusDistribution: await analyticsQueryService.GetConversationStatusDistributionAsync(window, cancellationToken),
            HandledBySnapshot: await analyticsQueryService.GetHandledBySnapshotAsync(cancellationToken),
            HandoffsByDay: await analyticsQueryService.GetHandoffsByDayAsync(window, cancellationToken),
            TicketStatusDistribution: await analyticsQueryService.GetTicketStatusDistributionAsync(window, cancellationToken));

        return Ok(response);
    }
}
