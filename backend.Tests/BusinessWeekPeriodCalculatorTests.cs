using backend.Services;
using Xunit;

namespace backend.Tests;

/// <summary>
/// Pure unit tests — no DB, no HTTP, no clock (every reference instant is
/// an explicit argument). Covers the Step 24 Phase 2 calendar-period
/// calculation (docs/handoffs/step-24-scheduled-ai-reports-design.md
/// §5.2-§5.4).
///
/// Two layers of coverage, deliberately: Compute-level tests exercise the
/// public API's ordinary calendar-floor behavior (normal/month/year
/// boundaries, reference-instant edges, a non-CET zone); ToUtc-level tests
/// (internal, see that method's own doc comment for why) exercise the two
/// DST policies directly and exactly, since a repository-wide scan this
/// phase found no real IANA/ICU zone whose transition lands on a Monday in
/// 2026/2027 — the only local instants Compute itself ever converts — so
/// those branches are unreachable through Compute's own public surface
/// with any real zone/date pair. Compute's own DST-era tests instead prove
/// the same code path is exercised correctly *indirectly*, via periods
/// that contain (without landing exactly on) a transition, asserting the
/// resulting UTC duration is 167h/169h rather than the ordinary 168h.
/// </summary>
public class BusinessWeekPeriodCalculatorTests
{
    private const string Copenhagen = "Europe/Copenhagen";
    private const string Auckland = "Pacific/Auckland";

    // ── Normal week ──────────────────────────────────────────────────

    [Fact]
    public void Compute_NormalWeek_Copenhagen_ReturnsExactBoundariesAndUtcInstants()
    {
        // Wednesday 2026-08-26, deep in CEST (+02:00) - the week AFTER the
        // targeted period, since a period is only ever reported once it has
        // fully completed (Compute always returns the most recently
        // COMPLETED week as of "now", never the week "now" itself falls
        // inside - design §5.3/§8.4). No month/year/DST boundary anywhere
        // near the targeted period.
        var nowUtc = new DateTimeOffset(2026, 8, 26, 10, 0, 0, TimeSpan.Zero);

        var period = BusinessWeekPeriodCalculator.Compute(nowUtc, Copenhagen);

        Assert.Equal(new DateOnly(2026, 8, 17), period.PeriodStartDate);
        Assert.Equal(new DateOnly(2026, 8, 24), period.PeriodEndDateExclusive);
        Assert.Equal(DateTimeOffset.Parse("2026-08-16T22:00:00Z"), period.PeriodStartUtc);
        Assert.Equal(DateTimeOffset.Parse("2026-08-23T22:00:00Z"), period.PeriodEndUtcExclusive);
        Assert.Equal(DateTimeOffset.Parse("2026-08-24T07:00:00Z"), period.SendAtUtc);

        // Half-open, and exactly one calendar week (168h) - no DST in this window.
        Assert.Equal(TimeSpan.FromHours(168), period.PeriodEndUtcExclusive - period.PeriodStartUtc);
        Assert.True(period.PeriodStartUtc < period.PeriodEndUtcExclusive);
    }

    // ── Month boundary ──────────────────────────────────────────────

    [Fact]
    public void Compute_MonthBoundary_Copenhagen_ReturnsExactBoundariesAndUtcInstants()
    {
        // Friday 2026-02-06, inside the week AFTER Mon 2026-01-26 - Mon
        // 2026-02-02 (the targeted, already-completed period).
        var nowUtc = new DateTimeOffset(2026, 2, 6, 12, 0, 0, TimeSpan.Zero);

        var period = BusinessWeekPeriodCalculator.Compute(nowUtc, Copenhagen);

        Assert.Equal(new DateOnly(2026, 1, 26), period.PeriodStartDate);
        Assert.Equal(new DateOnly(2026, 2, 2), period.PeriodEndDateExclusive);
        Assert.Equal(DateTimeOffset.Parse("2026-01-25T23:00:00Z"), period.PeriodStartUtc);
        Assert.Equal(DateTimeOffset.Parse("2026-02-01T23:00:00Z"), period.PeriodEndUtcExclusive);
        Assert.Equal(DateTimeOffset.Parse("2026-02-02T08:00:00Z"), period.SendAtUtc);
        Assert.Equal(TimeSpan.FromHours(168), period.PeriodEndUtcExclusive - period.PeriodStartUtc);
    }

    // ── Year boundary ─────────────────────────────────────────────────

    [Fact]
    public void Compute_YearBoundary_Copenhagen_ReturnsExactBoundariesAndUtcInstants()
    {
        // Thursday 2027-01-07, inside the week AFTER Mon 2026-12-28 - Mon
        // 2027-01-04 (the targeted, already-completed period).
        var nowUtc = new DateTimeOffset(2027, 1, 7, 12, 0, 0, TimeSpan.Zero);

        var period = BusinessWeekPeriodCalculator.Compute(nowUtc, Copenhagen);

        Assert.Equal(new DateOnly(2026, 12, 28), period.PeriodStartDate);
        Assert.Equal(new DateOnly(2027, 1, 4), period.PeriodEndDateExclusive);
        Assert.Equal(DateTimeOffset.Parse("2026-12-27T23:00:00Z"), period.PeriodStartUtc);
        Assert.Equal(DateTimeOffset.Parse("2027-01-03T23:00:00Z"), period.PeriodEndUtcExclusive);
        Assert.Equal(DateTimeOffset.Parse("2027-01-04T08:00:00Z"), period.SendAtUtc);
        Assert.Equal(TimeSpan.FromHours(168), period.PeriodEndUtcExclusive - period.PeriodStartUtc);
    }

    // ── DST — spring-forward, at the Compute level (period CONTAINS the
    //    transition; the Monday boundaries themselves are not invalid) ──

    [Fact]
    public void Compute_WeekContainingSpringForwardTransition_Copenhagen_UtcDurationIs167Hours()
    {
        // Targeted period: Mon 2026-03-23 - Mon 2026-03-30; EU spring-forward
        // is Sunday 2026-03-29 (01:00 UTC clocks jump 02:00->03:00
        // CET->CEST) - inside this period, not on either Monday boundary.
        // Reference instant is Wednesday 2026-04-01, the week AFTER the
        // targeted (now-completed) period.
        var nowUtc = new DateTimeOffset(2026, 4, 1, 11, 0, 0, TimeSpan.Zero);

        var period = BusinessWeekPeriodCalculator.Compute(nowUtc, Copenhagen);

        Assert.Equal(new DateOnly(2026, 3, 23), period.PeriodStartDate);
        Assert.Equal(new DateOnly(2026, 3, 30), period.PeriodEndDateExclusive);
        Assert.Equal(DateTimeOffset.Parse("2026-03-22T23:00:00Z"), period.PeriodStartUtc); // still CET, +01:00
        Assert.Equal(DateTimeOffset.Parse("2026-03-29T22:00:00Z"), period.PeriodEndUtcExclusive); // now CEST, +02:00
        Assert.Equal(DateTimeOffset.Parse("2026-03-30T07:00:00Z"), period.SendAtUtc); // CEST, +02:00

        // The week LOST an hour to the transition - 167h, not 168h. A
        // fixed-offset or non-DST-aware implementation would get 168h here.
        Assert.Equal(TimeSpan.FromHours(167), period.PeriodEndUtcExclusive - period.PeriodStartUtc);
    }

    // ── DST — fall-back, at the Compute level (period CONTAINS the
    //    transition) ──────────────────────────────────────────────────

    [Fact]
    public void Compute_WeekContainingFallBackTransition_Copenhagen_UtcDurationIs169Hours()
    {
        // Targeted period: Mon 2026-10-19 - Mon 2026-10-26; EU fall-back is
        // Sunday 2026-10-25 (01:00 UTC clocks fall 03:00->02:00 CEST->CET).
        // Reference instant is Wednesday 2026-10-28, the week AFTER the
        // targeted (now-completed) period.
        var nowUtc = new DateTimeOffset(2026, 10, 28, 13, 0, 0, TimeSpan.Zero);

        var period = BusinessWeekPeriodCalculator.Compute(nowUtc, Copenhagen);

        Assert.Equal(new DateOnly(2026, 10, 19), period.PeriodStartDate);
        Assert.Equal(new DateOnly(2026, 10, 26), period.PeriodEndDateExclusive);
        Assert.Equal(DateTimeOffset.Parse("2026-10-18T22:00:00Z"), period.PeriodStartUtc); // still CEST, +02:00
        Assert.Equal(DateTimeOffset.Parse("2026-10-25T23:00:00Z"), period.PeriodEndUtcExclusive); // now CET, +01:00
        Assert.Equal(DateTimeOffset.Parse("2026-10-26T08:00:00Z"), period.SendAtUtc); // CET, +01:00

        // The week GAINED an hour from the transition - 169h, not 168h.
        Assert.Equal(TimeSpan.FromHours(169), period.PeriodEndUtcExclusive - period.PeriodStartUtc);
    }

    // ── DST policy, direct and exact (internal ToUtc — see its own doc
    //    comment for why this is necessary) ────────────────────────────

    [Fact]
    public void ToUtc_SpringForwardGap_Copenhagen_RollsForwardToFirstValidInstant()
    {
        // 02:00-03:00 local does not exist on 2026-03-29 (EU spring-forward).
        // Requesting 02:30 must roll forward to the first valid instant
        // at/after it - 03:00 local, already CEST (+02:00) - never throw,
        // never silently pick an arbitrary nearby instant.
        var tz = TimeZoneInfo.FindSystemTimeZoneById(Copenhagen);
        var requested = new DateOnly(2026, 3, 29);

        var result = BusinessWeekPeriodCalculator.ToUtc(requested, TimeSpan.FromHours(2.5), tz);

        Assert.Equal(DateTimeOffset.Parse("2026-03-29T01:00:00Z"), result); // 03:00 local @ +02:00
    }

    [Fact]
    public void ToUtc_AmbiguousFallBack_Copenhagen_ResolvesToEarlierOccurrence()
    {
        // 02:00-03:00 local occurs twice on 2026-10-25 (EU fall-back): once
        // at CEST (+02:00, the earlier real-world occurrence) and once at
        // CET (+01:00, the later one, after the clock actually falls back).
        // Design §5.4's policy is the earlier occurrence - verified this
        // phase that TimeZoneInfo.ConvertTimeToUtc's own default resolves
        // to the LATER one (standard time), so this must NOT match what an
        // unguarded call to that method alone would produce.
        var tz = TimeZoneInfo.FindSystemTimeZoneById(Copenhagen);
        var requested = new DateOnly(2026, 10, 25);

        var result = BusinessWeekPeriodCalculator.ToUtc(requested, TimeSpan.FromHours(2.5), tz);

        Assert.Equal(DateTimeOffset.Parse("2026-10-25T00:30:00Z"), result); // earlier: CEST, +02:00

        var whatUnguardedDefaultWouldGive = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(new DateTime(2026, 10, 25, 2, 30, 0), DateTimeKind.Unspecified), tz);
        Assert.NotEqual(whatUnguardedDefaultWouldGive, result); // proves the explicit branch is load-bearing
        Assert.Equal(DateTimeOffset.Parse("2026-10-25T01:30:00Z"), whatUnguardedDefaultWouldGive); // later: CET, +01:00
    }

    [Fact]
    public void ToUtc_AmbiguousFallBack_NonCetZone_ResolvesToEarlierOccurrence()
    {
        // Pacific/Auckland's own fall-back (NZDT->NZST, opposite-hemisphere
        // calendar timing, +13:00/+12:00 offsets rather than Copenhagen's
        // +02:00/+01:00) - proves the "earlier occurrence = larger offset"
        // reasoning generalizes, not a Copenhagen-specific coincidence.
        var tz = TimeZoneInfo.FindSystemTimeZoneById(Auckland);
        var requested = new DateOnly(2026, 4, 5);

        var result = BusinessWeekPeriodCalculator.ToUtc(requested, TimeSpan.FromHours(2.5), tz);

        Assert.Equal(DateTimeOffset.Parse("2026-04-04T13:30:00Z"), result); // earlier: NZDT, +13:00
    }

    [Fact]
    public void ToUtc_SpringForwardGap_NonCetZone_RollsForwardToFirstValidInstant()
    {
        // Pacific/Auckland's own spring-forward (NZST->NZDT), a materially
        // different calendar date/offset pair than Copenhagen's.
        var tz = TimeZoneInfo.FindSystemTimeZoneById(Auckland);
        var requested = new DateOnly(2026, 9, 27);

        var result = BusinessWeekPeriodCalculator.ToUtc(requested, TimeSpan.FromHours(2.5), tz);

        Assert.Equal(DateTimeOffset.Parse("2026-09-26T14:00:00Z"), result); // 03:00 local @ +13:00
    }

    // ── Non-CET timezone, ordinary Compute-level coverage ──────────────

    [Fact]
    public void Compute_NonCetZone_StandardTime_Auckland_ReturnsExactBoundariesAndUtcInstants()
    {
        // Targeted period is NZ winter - standard time (NZST, +12:00), no
        // DST active. Reference instant (2026-06-24) is the week AFTER the
        // targeted (now-completed) period.
        var nowUtc = new DateTimeOffset(2026, 6, 24, 12, 0, 0, TimeSpan.Zero);

        var period = BusinessWeekPeriodCalculator.Compute(nowUtc, Auckland);

        Assert.Equal(new DateOnly(2026, 6, 15), period.PeriodStartDate);
        Assert.Equal(new DateOnly(2026, 6, 22), period.PeriodEndDateExclusive);
        Assert.Equal(DateTimeOffset.Parse("2026-06-14T12:00:00Z"), period.PeriodStartUtc);
        Assert.Equal(DateTimeOffset.Parse("2026-06-21T12:00:00Z"), period.PeriodEndUtcExclusive);
        Assert.Equal(DateTimeOffset.Parse("2026-06-21T21:00:00Z"), period.SendAtUtc);
        Assert.Equal(TimeSpan.FromHours(168), period.PeriodEndUtcExclusive - period.PeriodStartUtc);
    }

    [Fact]
    public void Compute_NonCetZone_DaylightTime_Auckland_ReturnsExactBoundariesAndUtcInstants()
    {
        // Targeted period is NZ summer - daylight time (NZDT, +13:00)
        // active. Deliberately a different offset AND a different calendar
        // season than the Copenhagen tests above, to catch any fixed-offset
        // or European-DST-calendar assumption. Reference instant
        // (2026-01-29) is the week AFTER the targeted (now-completed) period.
        var nowUtc = new DateTimeOffset(2026, 1, 29, 12, 0, 0, TimeSpan.Zero);

        var period = BusinessWeekPeriodCalculator.Compute(nowUtc, Auckland);

        Assert.Equal(new DateOnly(2026, 1, 19), period.PeriodStartDate);
        Assert.Equal(new DateOnly(2026, 1, 26), period.PeriodEndDateExclusive);
        Assert.Equal(DateTimeOffset.Parse("2026-01-18T11:00:00Z"), period.PeriodStartUtc);
        Assert.Equal(DateTimeOffset.Parse("2026-01-25T11:00:00Z"), period.PeriodEndUtcExclusive);
        Assert.Equal(DateTimeOffset.Parse("2026-01-25T20:00:00Z"), period.SendAtUtc);
        Assert.Equal(TimeSpan.FromHours(168), period.PeriodEndUtcExclusive - period.PeriodStartUtc);
    }

    // ── Reference-instant boundaries ────────────────────────────────────
    // All resolve within the same Copenhagen normal week (Mon 2026-08-17 -
    // Mon 2026-08-24, constant +02:00 offset throughout) so only the
    // reference instant varies between cases - isolating the calendar-floor
    // logic itself from any DST interaction. Confirms there is no "if
    // before 09:00 use the previous week" logic anywhere: a Monday at any
    // time of day floors to itself, never the prior week.

    public static readonly TheoryData<string, DateTimeOffset, DateOnly, DateOnly> ReferenceInstantCases = new()
    {
        { "Sunday just before midnight (still the prior week)",
          new DateTimeOffset(2026, 8, 23, 21, 59, 0, TimeSpan.Zero), new DateOnly(2026, 8, 10), new DateOnly(2026, 8, 17) },
        { "Monday exactly midnight local",
          new DateTimeOffset(2026, 8, 23, 22, 0, 0, TimeSpan.Zero), new DateOnly(2026, 8, 17), new DateOnly(2026, 8, 24) },
        { "Monday just before 09:00 local",
          new DateTimeOffset(2026, 8, 24, 6, 59, 0, TimeSpan.Zero), new DateOnly(2026, 8, 17), new DateOnly(2026, 8, 24) },
        { "Monday exactly 09:00 local",
          new DateTimeOffset(2026, 8, 24, 7, 0, 0, TimeSpan.Zero), new DateOnly(2026, 8, 17), new DateOnly(2026, 8, 24) },
        { "Monday just after 09:00 local",
          new DateTimeOffset(2026, 8, 24, 7, 1, 0, TimeSpan.Zero), new DateOnly(2026, 8, 17), new DateOnly(2026, 8, 24) },
        { "Monday just before midnight local (still the same Monday)",
          new DateTimeOffset(2026, 8, 24, 21, 59, 0, TimeSpan.Zero), new DateOnly(2026, 8, 17), new DateOnly(2026, 8, 24) },
        { "Tuesday exactly midnight local (rolled into the next week already)",
          new DateTimeOffset(2026, 8, 24, 22, 0, 0, TimeSpan.Zero), new DateOnly(2026, 8, 17), new DateOnly(2026, 8, 24) },
    };

    [Theory]
    [MemberData(nameof(ReferenceInstantCases))]
    public void Compute_ReferenceInstantBoundaries_Copenhagen_FloorsToCorrectMonday(
        string _, DateTimeOffset nowUtc, DateOnly expectedStart, DateOnly expectedEnd)
    {
        var period = BusinessWeekPeriodCalculator.Compute(nowUtc, Copenhagen);

        Assert.Equal(expectedStart, period.PeriodStartDate);
        Assert.Equal(expectedEnd, period.PeriodEndDateExclusive);
    }

    // ── Invalid timezone id ──────────────────────────────────────────

    [Fact]
    public void Compute_UnrecognizedTimeZoneId_ThrowsTimeZoneNotFoundException()
    {
        // No write-time Business.TimeZone validation exists anywhere in
        // this codebase (design §19.1) - this pure calculator performs
        // none either, letting TimeZoneInfo's own exception propagate
        // uncaught. Classifying this as a per-business processing failure
        // is a later phase's responsibility (design §5.3), not this one's.
        var nowUtc = new DateTimeOffset(2026, 8, 19, 10, 0, 0, TimeSpan.Zero);

        Assert.Throws<TimeZoneNotFoundException>(() =>
            BusinessWeekPeriodCalculator.Compute(nowUtc, "Not/A/Real/Zone"));
    }
}
