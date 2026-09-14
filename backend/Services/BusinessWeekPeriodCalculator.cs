namespace backend.Services;

/// <summary>
/// Step 24 Phase 2 — pure, DB-free calendar-period calculation for the
/// Scheduled AI Reports feature. See
/// docs/handoffs/step-24-scheduled-ai-reports-design.md §5.2-§5.4 for the
/// full design this implements.
///
/// Computes the most recently completed Monday-Sunday calendar week
/// ([PeriodStartDate 00:00 local, PeriodEndDateExclusive 00:00 local)) as
/// of a given reference instant, evaluated in a business's own IANA
/// timezone, plus the UTC instant corresponding to that period's Monday
/// 09:00 local ("SendAtUtc").
///
/// Deliberately does NOT determine whether a report is "due" — this class
/// returns period/instant data only, never a due/not-due verdict. That
/// comparison (comparing SendAtUtc against a clock) belongs to Phase 5's
/// scheduler, which owns TimeProvider - this class takes its reference
/// instant as an ordinary method argument and has no ambient clock
/// dependency at all.
/// </summary>
public static class BusinessWeekPeriodCalculator
{
    private static readonly TimeSpan SendTimeOfDayLocal = TimeSpan.FromHours(9);

    /// <summary>
    /// PeriodStartDate/PeriodEndDateExclusive are the period's boundary
    /// calendar dates in the business's own local timezone (never the
    /// reference instant's timezone, never server-local time).
    /// PeriodStartUtc/PeriodEndUtcExclusive are those same two boundaries
    /// (each local midnight) converted to UTC - the half-open
    /// [PeriodStartUtc, PeriodEndUtcExclusive) window design §5.5 requires
    /// analytics queries to use. SendAtUtc is PeriodEndDateExclusive's own
    /// Monday, 09:00 local, converted to UTC.
    /// </summary>
    public readonly record struct WeekPeriod(
        DateOnly PeriodStartDate,
        DateOnly PeriodEndDateExclusive,
        DateTimeOffset PeriodStartUtc,
        DateTimeOffset PeriodEndUtcExclusive,
        DateTimeOffset SendAtUtc);

    /// <param name="nowUtc">The reference instant. Always an explicit
    /// argument - never DateTimeOffset.UtcNow read internally - so this
    /// method is deterministic and testable without any clock control.</param>
    /// <param name="timeZoneId">An IANA timezone id, e.g.
    /// "Europe/Copenhagen" (Business.TimeZone's own format). No validation
    /// is performed here - TimeZoneInfo.FindSystemTimeZoneById throws
    /// TimeZoneNotFoundException/InvalidTimeZoneException for a bad value,
    /// left to propagate uncaught; per design §5.3/§19.1, classifying that
    /// as a per-business processing failure is a later phase's
    /// responsibility, not this pure calculator's.</param>
    public static WeekPeriod Compute(DateTimeOffset nowUtc, string timeZoneId)
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        var nowLocal = TimeZoneInfo.ConvertTime(nowUtc, tz);

        // Floor to the most recent Monday 00:00 local (ISO week: Monday =
        // start). Pure DateOnly/DateTime calendar arithmetic - never
        // crosses a DST boundary itself (subtracting calendar days, not
        // 24-hour spans), so month/year boundaries need no special-casing;
        // DST only enters once a boundary date is converted to a UTC
        // instant below.
        var dayOfWeek = (int)nowLocal.DayOfWeek; // Sunday=0 .. Saturday=6
        var daysSinceMonday = (dayOfWeek + 6) % 7;
        var periodEndDate = DateOnly.FromDateTime(nowLocal.Date.AddDays(-daysSinceMonday));
        var periodStartDate = periodEndDate.AddDays(-7);

        var periodStartUtc = ToUtc(periodStartDate, TimeSpan.Zero, tz);
        var periodEndUtc = ToUtc(periodEndDate, TimeSpan.Zero, tz);
        var sendAtUtc = ToUtc(periodEndDate, SendTimeOfDayLocal, tz);

        return new WeekPeriod(periodStartDate, periodEndDate, periodStartUtc, periodEndUtc, sendAtUtc);
    }

    /// <summary>
    /// Converts one local wall-clock instant (a calendar date plus a
    /// time-of-day) to its UTC equivalent, per design §5.4's two explicit
    /// DST policies:
    ///
    /// Invalid (spring-forward gap, e.g. Europe/Copenhagen's 02:00-03:00 on
    /// the DST-start Sunday): roll forward minute-by-minute to the first
    /// valid instant at/after the requested wall-clock time, then convert -
    /// never silently drop or duplicate a boundary/send instant.
    ///
    /// Ambiguous (fall-back, e.g. the same window occurring twice on the
    /// DST-end Sunday): design §5.4 specifies using the EARLIER of the two
    /// valid occurrences. This requires an explicit branch -
    /// TimeZoneInfo.ConvertTimeToUtc's default overload does NOT achieve
    /// this on its own. Verified empirically this phase (a throwaway
    /// console check against Europe/Copenhagen 2026-10-25 02:30 and
    /// Pacific/Auckland 2026-04-05 02:30, both real ambiguous instants):
    /// the default resolves to the STANDARD-time offset, which is actually
    /// the LATER of the two chronological occurrences, not the earlier one
    /// - design §5.4's own comment claiming "no extra branch is required
    /// here" was incorrect on this point (the policy itself - earlier
    /// occurrence - was correctly and explicitly specified; only the
    /// illustrative one-line implementation for achieving it was wrong).
    /// The earlier occurrence corresponds to the LARGER of the two
    /// candidate UTC offsets: since UTC = local - offset for a fixed local
    /// wall-clock value, a larger offset always yields a smaller (earlier)
    /// UTC instant - true for any ambiguous pair in any timezone,
    /// independent of hemisphere or offset sign.
    /// </summary>
    /// <summary>
    /// internal, not private: repository-wide scan this phase (141
    /// IANA/ICU zones known to this OS) found no zone whose DST transition
    /// lands on a Monday in 2026 or 2027 - the only local instants Compute
    /// ever converts are Monday 00:00/09:00, so the invalid/ambiguous
    /// branches below are unreachable through Compute's own public surface
    /// with any real zone/date pair available in this environment. Exposed
    /// to backend.Tests (see backend.csproj's existing InternalsVisibleTo,
    /// the same precedent VerifiedOrderOwnership's internal constructor
    /// already established) so the DST policy itself can be tested
    /// directly and exactly, rather than depending on finding a lucky
    /// real-world alignment. Compute's own DST-era tests instead verify
    /// this method's effect indirectly, through periods that CONTAIN a
    /// transition without landing exactly on it (a period's UTC duration
    /// is 167h/169h instead of 168h) - both kinds of coverage exist; see
    /// BusinessWeekPeriodCalculatorTests.cs.
    /// </summary>
    internal static DateTimeOffset ToUtc(DateOnly date, TimeSpan timeOfDay, TimeZoneInfo tz)
    {
        var local = DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue) + timeOfDay, DateTimeKind.Unspecified);

        if (tz.IsInvalidTime(local))
        {
            do
            {
                local = local.AddMinutes(1);
            } while (tz.IsInvalidTime(local));

            return TimeZoneInfo.ConvertTimeToUtc(local, tz);
        }

        if (tz.IsAmbiguousTime(local))
        {
            var earlierOccurrenceOffset = tz.GetAmbiguousTimeOffsets(local).Max();
            return new DateTimeOffset(local, earlierOccurrenceOffset).ToUniversalTime();
        }

        return TimeZoneInfo.ConvertTimeToUtc(local, tz);
    }
}
