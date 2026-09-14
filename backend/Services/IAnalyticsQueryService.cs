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
/// are the two intended callers of the six members above the Step 24
/// section below.
///
/// Step 24 Phase 4 (design §6.2) adds BuildCalendarWindow and five
/// `...ForBusinessAsync` methods, purely additively — every member above
/// this comment keeps its exact original signature and ambient-tenant
/// behavior, unchanged, still relying entirely on AppDbContext's global
/// query filter exactly as before. The new members below exist because
/// those six cannot be safely called from a context with no HttpContext
/// (Step 24's background scheduler, Phase 5/6) — see AnalyticsQueryService.cs's
/// own doc comment and design §3.1/§6.1 for the full reasoning: the ambient
/// ICurrentTenantAccessor resolves to null outside a request, and the
/// existing methods would then silently return zero-filled analytics
/// rather than failing or returning real data.
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

    // ─────────────────────────────────────────────────────────────────
    // Step 24 Phase 4 (design §6.2) — additive only. See this interface's
    // own doc comment above.
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds an AnalyticsRangeWindow from explicit calendar-period
    /// boundaries (Phase 2's BusinessWeekPeriodCalculator output) instead
    /// of parsing a range key. Range is set to "calendar" — a label never
    /// validated against ValidRangeKeys by any caller of this method
    /// (design §6.2). Does not alter TryParseRange or the 7d/30d/90d
    /// semantics in any way.
    /// </summary>
    AnalyticsRangeWindow BuildCalendarWindow(
        DateOnly periodStartDate, DateOnly periodEndDateExclusive,
        DateTimeOffset periodStartUtc, DateTimeOffset periodEndUtcExclusive);

    /// <summary>
    /// Explicit-tenant counterpart of GetConversationsByDayAsync — for
    /// callers with no ambient HttpContext (design §6.2). businessId is
    /// authoritative: the query uses IgnoreQueryFilters() plus an explicit
    /// BusinessId predicate, never the ambient ICurrentTenantAccessor. See
    /// AnalyticsQueryService.cs for the concrete mechanism.
    /// </summary>
    Task<List<DayCountEntry>> GetConversationsByDayForBusinessAsync(Guid businessId, AnalyticsRangeWindow window, CancellationToken cancellationToken);

    /// <summary>Explicit-tenant counterpart of GetConversationStatusDistributionAsync. See GetConversationsByDayForBusinessAsync's doc comment.</summary>
    Task<List<StatusCountEntry>> GetConversationStatusDistributionForBusinessAsync(Guid businessId, AnalyticsRangeWindow window, CancellationToken cancellationToken);

    /// <summary>Explicit-tenant counterpart of GetHandledBySnapshotAsync — still takes no window (design §9 Decision 1 is unaffected). See GetConversationsByDayForBusinessAsync's doc comment.</summary>
    Task<List<StatusCountEntry>> GetHandledBySnapshotForBusinessAsync(Guid businessId, CancellationToken cancellationToken);

    /// <summary>Explicit-tenant counterpart of GetHandoffsByDayAsync. See GetConversationsByDayForBusinessAsync's doc comment.</summary>
    Task<List<DayCountEntry>> GetHandoffsByDayForBusinessAsync(Guid businessId, AnalyticsRangeWindow window, CancellationToken cancellationToken);

    /// <summary>Explicit-tenant counterpart of GetTicketStatusDistributionAsync. See GetConversationsByDayForBusinessAsync's doc comment.</summary>
    Task<List<StatusCountEntry>> GetTicketStatusDistributionForBusinessAsync(Guid businessId, AnalyticsRangeWindow window, CancellationToken cancellationToken);
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
