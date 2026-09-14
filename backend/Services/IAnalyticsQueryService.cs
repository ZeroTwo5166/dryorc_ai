using backend.Controllers;

namespace backend.Services;

/// <summary>
/// Step 23 Phase 2 — pure extraction of the query logic that used to live
/// directly inside AnalyticsController.Get (Step 21). Every method here
/// returns the exact same DTOs Step 21 already shipped (DayCountEntry,
/// StatusCountEntry — still declared in AnalyticsController.cs, unmoved) and
/// preserves the original code's exact semantics (tenant-filter predicate,
/// date-window computation, grouping, zero-fill, status mappings,
/// GetHandledBySnapshotAsync's all-history behavior) — this is not a
/// rewrite. AnalyticsController.Get and the Step 23 Phase 3 capability tools
/// are the two intended callers.
/// </summary>
public interface IAnalyticsQueryService
{
    /// <summary>
    /// The accepted `range` query values, in the same order
    /// AnalyticsController's own error message has always listed them.
    /// </summary>
    IReadOnlyCollection<string> ValidRangeKeys { get; }

    /// <summary>
    /// Parses and validates a `range` value into a computed window (calendar-
    /// day half-open [RangeStart, RangeEndExclusive) UTC window, per design
    /// §8 Decision 3). Returns false — with `window` left as `default` — for
    /// null or any value not in ValidRangeKeys, exactly matching
    /// AnalyticsController's original inline validation.
    /// </summary>
    bool TryParseRange(string? range, out AnalyticsRangeWindow window);

    Task<List<DayCountEntry>> GetConversationsByDayAsync(AnalyticsRangeWindow window, CancellationToken cancellationToken);

    Task<List<StatusCountEntry>> GetConversationStatusDistributionAsync(AnalyticsRangeWindow window, CancellationToken cancellationToken);

    /// <summary>
    /// Design §9 Decision 1 — ignores range/age entirely, an all-history
    /// current snapshot. Takes no window for exactly that reason.
    /// </summary>
    Task<List<StatusCountEntry>> GetHandledBySnapshotAsync(CancellationToken cancellationToken);

    Task<List<DayCountEntry>> GetHandoffsByDayAsync(AnalyticsRangeWindow window, CancellationToken cancellationToken);

    Task<List<StatusCountEntry>> GetTicketStatusDistributionAsync(AnalyticsRangeWindow window, CancellationToken cancellationToken);
}

/// <summary>
/// A parsed, validated `range` value plus its computed calendar-day window.
/// RangeStart/RangeEndExclusive are UTC midnight DateTimeOffsets for use in
/// EF Core predicates; RangeStartDate/RangeEndDateExclusive are the same
/// boundaries as DateOnly, for echoing back in AnalyticsResponse and for
/// building AllDays. AllDays is the dense list of every calendar day in
/// range, used by the zero-fill helpers.
/// </summary>
public readonly record struct AnalyticsRangeWindow(
    string Range,
    DateOnly RangeStartDate,
    DateOnly RangeEndDateExclusive,
    DateTimeOffset RangeStart,
    DateTimeOffset RangeEndExclusive,
    IReadOnlyList<DateOnly> AllDays);
