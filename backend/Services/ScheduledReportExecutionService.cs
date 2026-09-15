using System.Globalization;
using System.Text;
using backend.Auth;
using backend.Controllers;
using backend.Data;
using backend.Email;
using backend.Enums;
using backend.Models;
using Microsoft.EntityFrameworkCore;

namespace backend.Services;

/// <summary>
/// Step 24 Phase 6 — see IScheduledReportExecutionService's own doc comment
/// and design §9/§10/§11/§13 for the full contract. Explicitly cross-tenant
/// by nature for the same reason Phase 5 is (a single call processes one
/// business, but that business's identity is a parameter, never the ambient
/// ICurrentTenantAccessor, which has nothing to resolve in this
/// no-HttpContext, scheduled-job context) — every query here uses
/// IgnoreQueryFilters() with an explicit BusinessId filter, mirroring
/// NotificationService.ResolveRecipientsAsync's own established shape for
/// Owner resolution and Phase 4's own explicit-tenant methods for analytics.
///
/// TimeProvider is injected here for this service's own SentAt timestamp —
/// the same abstraction ScheduledReportSchedulerService already introduced
/// for ClaimedAt (Phase 5), reused rather than a second, inconsistent time
/// source, so both of this run's own timestamps are deterministically
/// testable through the same mechanism.
///
/// Step ordering below is design §9's exact sequence, deliberately not
/// reordered: only steps 3 (analytics) and 5 (email) are wrapped in their
/// own try/catch, converting a failure into a Failed finalize — steps 1
/// (enablement re-check), 2 (Owner resolution query itself, as opposed to
/// its 0/1/&gt;1 branching), and 6 (the terminal-state persist) are
/// deliberately NOT wrapped: an unexpected exception there propagates
/// uncaught out of ExecuteAsync, leaving the run stuck in Processing —
/// exactly the crash-equivalent outcome design §10.3's own crash-point table
/// describes for those same boundaries. This is what makes the crash-window
/// test possible without any test-only hook in this file: cancelling the
/// caller's own CancellationToken immediately after step 5's email call
/// returns reproduces "crash between send and finalize" faithfully, because
/// step 6's ExecuteUpdateAsync below honors that same token and throws
/// before ever touching the row.
/// </summary>
public class ScheduledReportExecutionService(
    AppDbContext db,
    IAnalyticsQueryService analyticsQueryService,
    IEmailSender emailSender,
    TimeProvider timeProvider,
    ILogger<ScheduledReportExecutionService> logger) : IScheduledReportExecutionService
{
    public async Task ExecuteAsync(ScheduledReportRun claimedRun, CancellationToken cancellationToken)
    {
        // Step 1 (design §9) — authoritative enablement re-check. Never the
        // value read at Phase 5's discovery time, which may be stale; this
        // is the one and only authoritative check, per the locked "checked
        // at send/execution time" requirement (design §2 Decision 3, §8.2).
        var business = await db.Businesses
            .IgnoreQueryFilters()
            .Where(b => b.Id == claimedRun.BusinessId)
            .Select(b => new { b.Name, b.ScheduledReportsEnabled })
            .SingleAsync(cancellationToken);

        if (!business.ScheduledReportsEnabled)
        {
            logger.LogInformation(
                "Scheduled report skipped (disabled at execution time) for business {BusinessId}, period {ReportPeriodStart}.",
                claimedRun.BusinessId, claimedRun.ReportPeriodStart);
            await FinalizeAsync(claimedRun, ScheduledReportRunStatus.Skipped, sentAt: null, failureReason: null, recipientOwnerId: null, cancellationToken);
            return;
        }

        // Step 2 (design §9/§11) — Owner resolution, before report
        // generation, so a run that cannot be sent regardless of content
        // never pays the analytics-query cost.
        var activeOwners = await ResolveActiveOwnersAsync(claimedRun.BusinessId, cancellationToken);

        if (activeOwners.Count == 0)
        {
            logger.LogWarning(
                "Scheduled report has no active Owner for business {BusinessId}, period {ReportPeriodStart}.",
                claimedRun.BusinessId, claimedRun.ReportPeriodStart);
            await FinalizeAsync(claimedRun, ScheduledReportRunStatus.Failed, sentAt: null, failureReason: "NoActiveOwner", recipientOwnerId: null, cancellationToken);
            return;
        }

        if (activeOwners.Count > 1)
        {
            logger.LogWarning(
                "Scheduled report has {Count} active Owners (expected exactly 1) for business {BusinessId}, period {ReportPeriodStart}.",
                activeOwners.Count, claimedRun.BusinessId, claimedRun.ReportPeriodStart);
            await FinalizeAsync(claimedRun, ScheduledReportRunStatus.Failed, sentAt: null, failureReason: "MultipleActiveOwners", recipientOwnerId: null, cancellationToken);
            return;
        }

        var owner = activeOwners[0];

        // Step 3 (design §9) — report content, via Phase 4's explicit-tenant
        // methods only, re-deriving the window from this run's OWN persisted
        // UTC boundaries (never recomputing the business-local period again
        // here — the run row is the durable source of truth for what period
        // it covers, not a fresh BusinessWeekPeriodCalculator call).
        string subject;
        string htmlBody;
        try
        {
            var window = analyticsQueryService.BuildCalendarWindow(
                periodStartDate: DateOnly.FromDateTime(claimedRun.ReportPeriodStart.UtcDateTime),
                periodEndDateExclusive: DateOnly.FromDateTime(claimedRun.ReportPeriodEnd.UtcDateTime),
                periodStartUtc: claimedRun.ReportPeriodStart,
                periodEndUtcExclusive: claimedRun.ReportPeriodEnd);

            var conversationsByDay = await analyticsQueryService.GetConversationsByDayForBusinessAsync(
                claimedRun.BusinessId, window, cancellationToken);
            var conversationStatusDistribution = await analyticsQueryService.GetConversationStatusDistributionForBusinessAsync(
                claimedRun.BusinessId, window, cancellationToken);
            var handledBySnapshot = await analyticsQueryService.GetHandledBySnapshotForBusinessAsync(
                claimedRun.BusinessId, cancellationToken);
            var handoffsByDay = await analyticsQueryService.GetHandoffsByDayForBusinessAsync(
                claimedRun.BusinessId, window, cancellationToken);
            var ticketStatusDistribution = await analyticsQueryService.GetTicketStatusDistributionForBusinessAsync(
                claimedRun.BusinessId, window, cancellationToken);

            // Step 4 (design §9) — pure, side-effect-free rendering.
            subject = BuildSubject(business.Name, window.RangeStartDate, window.RangeEndDateExclusive);
            htmlBody = BuildHtmlBody(
                business.Name, window.RangeStartDate, window.RangeEndDateExclusive,
                conversationsByDay, conversationStatusDistribution, handledBySnapshot,
                handoffsByDay, ticketStatusDistribution);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Scheduled report analytics generation failed for business {BusinessId}, period {ReportPeriodStart}.",
                claimedRun.BusinessId, claimedRun.ReportPeriodStart);
            await FinalizeAsync(claimedRun, ScheduledReportRunStatus.Failed, sentAt: null, failureReason: "AnalyticsQueryFailed", recipientOwnerId: null, cancellationToken);
            return;
        }

        // Step 5 (design §9/§13) — the one step with an unavoidable external
        // side effect. Directly awaited, explicit try/catch, never
        // fire-and-forget (design §3.6/§14 — the AuthController calling
        // convention, never VerificationCodeService's detached one).
        try
        {
            await emailSender.SendEmailAsync(owner.Email, subject, htmlBody);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Scheduled report email send failed for business {BusinessId}, period {ReportPeriodStart}.",
                claimedRun.BusinessId, claimedRun.ReportPeriodStart);
            await FinalizeAsync(claimedRun, ScheduledReportRunStatus.Failed, sentAt: null, failureReason: $"EmailSendFailed:{ex.Message}", recipientOwnerId: null, cancellationToken);
            return;
        }

        // Step 6 (design §9/§10.2/§12.3) — terminal-state persist, always
        // AFTER the email call has returned successfully, never before
        // (design §13.B's deliberately-rejected shape). Deliberately not
        // wrapped in a try/catch: a failure here (including a cancelled
        // CancellationToken, simulating a process crash between send and
        // finalize) must propagate uncaught, leaving the row stuck in
        // Processing — the exact §13.A/§13.C crash window this design names
        // and accepts, never silently reinterpreted as some other outcome.
        logger.LogInformation(
            "Scheduled report sent for business {BusinessId}, period {ReportPeriodStart}, recipient {RecipientOwnerId}.",
            claimedRun.BusinessId, claimedRun.ReportPeriodStart, owner.Id);
        await FinalizeAsync(
            claimedRun, ScheduledReportRunStatus.Sent,
            sentAt: timeProvider.GetUtcNow(), failureReason: null, recipientOwnerId: owner.Id, cancellationToken);
    }

    /// <summary>
    /// Design §11 — mirrors NotificationService.ResolveRecipientsAsync's own
    /// established shape (IgnoreQueryFilters() + explicit BusinessId +
    /// explicit IsActive), joined against the Owner role via ASP.NET
    /// Identity's own AspNetRoles/AspNetUserRoles tables (neither is
    /// ITenantOwned, so IgnoreQueryFilters() is meaningless there and not
    /// applied) — never the ambient ICurrentTenantAccessor, which resolves
    /// to nothing outside HttpContext (design §3.1/§17).
    /// </summary>
    private async Task<List<(Guid Id, string Email)>> ResolveActiveOwnersAsync(Guid businessId, CancellationToken cancellationToken)
    {
        var ownerRoleId = await db.Roles
            .Where(r => r.Name == RoleNames.Owner)
            .Select(r => r.Id)
            .SingleAsync(cancellationToken);

        var owners = await db.Users
            .IgnoreQueryFilters()
            .Where(u => u.BusinessId == businessId && u.IsActive)
            .Join(db.UserRoles.Where(ur => ur.RoleId == ownerRoleId), u => u.Id, ur => ur.UserId, (u, _) => u)
            .Select(u => new { u.Id, u.Email })
            .ToListAsync(cancellationToken);

        return owners.Select(o => (o.Id, o.Email!)).ToList();
    }

    /// <summary>
    /// Design §9 step 6 / §10.2 / §12.3 — the sole terminal-state write, a
    /// single guarded ExecuteUpdateAsync (WHERE Id == run.Id AND Status ==
    /// Processing), IgnoreQueryFilters() applied for the same reason every
    /// other query in this class needs it (design §3.1/§17): the ambient
    /// ICurrentTenantAccessor resolves to null in this no-HttpContext
    /// context, which the global tenant filter treats as "match zero rows"
    /// — without this, the guarded update would silently affect 0 rows
    /// every time, never actually finalizing anything. RecipientOwnerId is
    /// written ONLY on the Sent path — per
    /// ScheduledReportRun.cs's own explicit nullability correction ("set
    /// only on the path that reaches a resolved single Owner; left null on
    /// every Failed/Skipped outcome"), it is never persisted for a
    /// NoActiveOwner/MultipleActiveOwners/AnalyticsQueryFailed/
    /// EmailSendFailed/Skipped outcome even when Owner resolution itself
    /// succeeded earlier in the same run. A 0-row result (design §12.3 — the
    /// second, currently-vacuous guard) is logged as a warning, never
    /// thrown, never re-attempted.
    /// </summary>
    private async Task FinalizeAsync(
        ScheduledReportRun run,
        ScheduledReportRunStatus status,
        DateTimeOffset? sentAt,
        string? failureReason,
        Guid? recipientOwnerId,
        CancellationToken cancellationToken)
    {
        var affected = await db.ScheduledReportRuns
            .IgnoreQueryFilters()
            .Where(r => r.Id == run.Id && r.Status == ScheduledReportRunStatus.Processing)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, status)
                .SetProperty(r => r.SentAt, sentAt)
                .SetProperty(r => r.FailureReason, failureReason)
                .SetProperty(r => r.RecipientOwnerId, recipientOwnerId), cancellationToken);

        if (affected == 0)
        {
            logger.LogWarning(
                "ScheduledReportRun {RunId} for business {BusinessId}, period {ReportPeriodStart} finalize affected 0 rows - it was no longer Processing.",
                run.Id, run.BusinessId, run.ReportPeriodStart);
        }
    }

    // ── Report rendering (design §9 step 4) — pure, in-memory, no I/O. ────

    private static string BuildSubject(string businessName, DateOnly periodStartDate, DateOnly periodEndDateExclusive)
    {
        var lastDay = periodEndDateExclusive.AddDays(-1);
        return $"Nivora weekly report for {businessName}: {periodStartDate:MMM d} - {lastDay:MMM d}";
    }

    private static string BuildHtmlBody(
        string businessName,
        DateOnly periodStartDate,
        DateOnly periodEndDateExclusive,
        List<DayCountEntry> conversationsByDay,
        List<StatusCountEntry> conversationStatusDistribution,
        List<StatusCountEntry> handledBySnapshot,
        List<DayCountEntry> handoffsByDay,
        List<StatusCountEntry> ticketStatusDistribution)
    {
        var lastDay = periodEndDateExclusive.AddDays(-1);
        var sb = new StringBuilder();

        sb.Append(CultureInfo.InvariantCulture, $"<h1>Weekly report - {Escape(businessName)}</h1>");
        sb.Append(CultureInfo.InvariantCulture, $"<p>Period: {periodStartDate:yyyy-MM-dd} to {lastDay:yyyy-MM-dd}</p>");

        AppendDaySection(sb, "Conversations per day", conversationsByDay);
        AppendStatusSection(sb, "Conversation status distribution", conversationStatusDistribution);
        AppendStatusSection(sb, "AI vs. human handled (all time)", handledBySnapshot);
        AppendDaySection(sb, "Handoffs per day", handoffsByDay);
        AppendStatusSection(sb, "Ticket status distribution", ticketStatusDistribution);

        return sb.ToString();
    }

    private static void AppendDaySection(StringBuilder sb, string heading, List<DayCountEntry> entries)
    {
        sb.Append(CultureInfo.InvariantCulture, $"<h2>{Escape(heading)}</h2><ul>");
        foreach (var entry in entries)
        {
            sb.Append(CultureInfo.InvariantCulture, $"<li>{entry.Day:yyyy-MM-dd}: {entry.Count}</li>");
        }

        sb.Append(CultureInfo.InvariantCulture, $"</ul><p>Total: {entries.Sum(e => e.Count)}</p>");
    }

    private static void AppendStatusSection(StringBuilder sb, string heading, List<StatusCountEntry> entries)
    {
        sb.Append(CultureInfo.InvariantCulture, $"<h2>{Escape(heading)}</h2><ul>");
        foreach (var entry in entries)
        {
            sb.Append(CultureInfo.InvariantCulture, $"<li>{Escape(entry.Status)}: {entry.Count}</li>");
        }

        sb.Append("</ul>");
    }

    private static string Escape(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
