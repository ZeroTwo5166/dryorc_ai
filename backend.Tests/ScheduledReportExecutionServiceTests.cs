using backend.Auth;
using backend.Constants;
using backend.Controllers;
using backend.Data;
using backend.Email;
using backend.Enums;
using backend.Interfaces;
using backend.Models;
using backend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace backend.Tests;

/// <summary>
/// Step 24 Phase 6 — direct tests for ScheduledReportExecutionService
/// against the real dev DB. See
/// docs/handoffs/step-24-scheduled-ai-reports-design.md §9/§10/§11/§13 and
/// docs/handoffs/step-24-scheduled-ai-reports-implementation-plan.md's Phase
/// 6 section for the full contract this proves.
///
/// Every test here constructs ScheduledReportExecutionService directly and
/// calls ExecuteAsync against a manually-inserted, already-Processing
/// ScheduledReportRun row - never through ScheduledReportSchedulerService or
/// ScheduledReportBackgroundService, matching Phase 5's own established
/// "scoped service testable without the timer wrapper" convention. Phase 5's
/// own claim logic is not exercised or modified anywhere in this file.
/// </summary>
public class ScheduledReportExecutionServiceTests : IAsyncLifetime
{
    private readonly List<Guid> _businessIdsToClean = [];
    private readonly List<Guid> _userIdsToClean = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_businessIdsToClean.Count == 0)
        {
            return;
        }

        await using var db = DbFor();
        var bizIds = _businessIdsToClean.ToArray();

        if (_userIdsToClean.Count > 0)
        {
            var userIds = _userIdsToClean.ToArray();
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"AspNetUserRoles\" WHERE \"UserId\" = ANY({userIds})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"AspNetUsers\" WHERE \"Id\" = ANY({userIds})");
        }

        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"ScheduledReportRuns\" WHERE \"BusinessId\" = ANY({bizIds})");
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"ConversationHandoffs\" WHERE \"BusinessId\" = ANY({bizIds})");
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Tickets\" WHERE \"BusinessId\" = ANY({bizIds})");
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Conversations\" WHERE \"BusinessId\" = ANY({bizIds})");
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Customers\" WHERE \"BusinessId\" = ANY({bizIds})");
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Widgets\" WHERE \"BusinessId\" = ANY({bizIds})");
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Businesses\" WHERE \"Id\" = ANY({bizIds})");
    }

    // ── Infra helpers — same shape as ScheduledReportSchedulerServiceTests.cs
    //    and AnalyticsQueryServiceExplicitTenantTests.cs ──────────────────

    private static AppDbContext DbFor() =>
        new ServiceCollection()
            .AddSingleton<ICurrentTenantAccessor>(new FixedTenantAccessor(null))
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

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static ScheduledReportExecutionService ServiceFor(
        AppDbContext db, IEmailSender emailSender, DateTimeOffset nowUtc, IAnalyticsQueryService? analytics = null) =>
        new(db, analytics ?? new AnalyticsQueryService(db), emailSender, new FixedTimeProvider(nowUtc),
            NullLogger<ScheduledReportExecutionService>.Instance);

    private static readonly DateTimeOffset PeriodStart = new(2026, 8, 17, 0, 0, 0, TimeSpan.Zero); // Monday, UTC midnight
    private static readonly DateTimeOffset PeriodEnd = PeriodStart.AddDays(7);
    private static readonly DateTimeOffset ExecutionInstant = new(2026, 8, 24, 9, 5, 0, TimeSpan.Zero);

    private async Task<Guid> SeedBusinessAsync(string namePrefix, string timeZone = "Europe/Copenhagen")
    {
        await using var db = DbFor();
        var businessId = Guid.NewGuid();
        db.Businesses.Add(new Business
        {
            Id = businessId,
            Name = $"{namePrefix} {Guid.NewGuid():N}",
            TimeZone = timeZone,
            ScheduledReportsEnabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        _businessIdsToClean.Add(businessId);
        return businessId;
    }

    private async Task<Guid> SeedOwnerAsync(Guid businessId, bool isActive, string emailPrefix)
    {
        await using var db = DbFor();
        var ownerRoleId = await db.Roles
            .Where(r => r.Name == RoleNames.Owner)
            .Select(r => r.Id)
            .SingleAsync();

        var userId = Guid.NewGuid();
        var email = $"{emailPrefix}-{Guid.NewGuid():N}@example.com".ToLowerInvariant();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "AspNetUsers"
                ("Id", "BusinessId", "UserName", "NormalizedUserName", "Email", "NormalizedEmail",
                 "EmailConfirmed", "IsActive", "CreatedAt", "SecurityStamp", "ConcurrencyStamp",
                 "PhoneNumberConfirmed", "TwoFactorEnabled", "LockoutEnabled", "AccessFailedCount")
            VALUES
                ({userId}, {businessId}, {email}, {email.ToUpperInvariant()}, {email}, {email.ToUpperInvariant()},
                 true, {isActive}, {DateTimeOffset.UtcNow}, {Guid.NewGuid().ToString()}, {Guid.NewGuid().ToString()},
                 false, false, true, 0)
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "AspNetUserRoles" ("UserId", "RoleId") VALUES ({userId}, {ownerRoleId})
            """);

        _userIdsToClean.Add(userId);
        return userId;
    }

    private static async Task<string> OwnerEmailAsync(Guid ownerId)
    {
        await using var db = DbFor();
        return (await db.Users.IgnoreQueryFilters().Where(u => u.Id == ownerId).Select(u => u.Email).SingleAsync())!;
    }

    private async Task<ScheduledReportRun> SeedClaimedRunAsync(Guid businessId)
    {
        await using var db = DbFor();
        var run = new ScheduledReportRun
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            ReportPeriodStart = PeriodStart,
            ReportPeriodEnd = PeriodEnd,
            Status = ScheduledReportRunStatus.Processing,
            ClaimedAt = PeriodStart.AddDays(7).AddHours(9),
        };
        db.ScheduledReportRuns.Add(run);
        await db.SaveChangesAsync();
        return run;
    }

    private static async Task<ScheduledReportRun> ReloadRunAsync(Guid runId)
    {
        await using var db = DbFor();
        return await db.ScheduledReportRuns.IgnoreQueryFilters().SingleAsync(r => r.Id == runId);
    }

    // ── Content-fixture seeding — same shape as
    //    AnalyticsQueryServiceExplicitTenantTests.cs ───────────────────────

    private async Task<(Guid WidgetId, Guid CustomerId)> SeedCommerceFixtureAsync(Guid businessId)
    {
        await using var db = DbFor();
        var widgetId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        db.Widgets.Add(new Widget
        {
            Id = widgetId,
            BusinessId = businessId,
            Name = "Exec Test Widget",
            PublicKey = $"exec-test-{Guid.NewGuid():N}",
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
        return (widgetId, customerId);
    }

    private static async Task SeedKnownAnalyticsDataAsync(Guid businessId, Guid widgetId, Guid customerId)
    {
        await using var db = DbFor();
        var inPeriod = PeriodStart.AddDays(1); // safely inside [PeriodStart, PeriodEnd)

        // 3 conversations in period: 2 Open/Ai, 1 Closed/Human.
        db.Conversations.AddRange(
            new Conversation { Id = Guid.NewGuid(), BusinessId = businessId, WidgetId = widgetId, Status = ConversationStatus.Open, HandledBy = HandledBy.Ai, Channel = "widget", CreatedAt = inPeriod, UpdatedAt = inPeriod },
            new Conversation { Id = Guid.NewGuid(), BusinessId = businessId, WidgetId = widgetId, Status = ConversationStatus.Open, HandledBy = HandledBy.Ai, Channel = "widget", CreatedAt = inPeriod, UpdatedAt = inPeriod },
            new Conversation { Id = Guid.NewGuid(), BusinessId = businessId, WidgetId = widgetId, Status = ConversationStatus.Closed, HandledBy = HandledBy.Human, Channel = "widget", CreatedAt = inPeriod, UpdatedAt = inPeriod });

        // 2 handoffs in period.
        db.ConversationHandoffs.AddRange(
            new ConversationHandoff { Id = Guid.NewGuid(), BusinessId = businessId, ConversationId = null, FromHandledBy = HandledBy.Ai, ToHandledBy = HandledBy.Human, Reason = HandoffReasons.StaffManualTakeover, ActorType = ActorType.NivoraSystem, ActorId = null, CreatedAt = inPeriod },
            new ConversationHandoff { Id = Guid.NewGuid(), BusinessId = businessId, ConversationId = null, FromHandledBy = HandledBy.Ai, ToHandledBy = HandledBy.Human, Reason = HandoffReasons.StaffManualTakeover, ActorType = ActorType.NivoraSystem, ActorId = null, CreatedAt = inPeriod });

        // 2 tickets in period: 1 Open, 1 Resolved.
        db.Tickets.AddRange(
            new Ticket { Id = Guid.NewGuid(), BusinessId = businessId, CustomerId = customerId, Status = TicketStatus.Open, Priority = TicketPriority.Medium, PriorityReason = TicketPriorityReasons.ManualEscalation, Channel = "widget", CreatedAt = inPeriod, UpdatedAt = inPeriod },
            new Ticket { Id = Guid.NewGuid(), BusinessId = businessId, CustomerId = customerId, Status = TicketStatus.Resolved, Priority = TicketPriority.Medium, PriorityReason = TicketPriorityReasons.ManualEscalation, Channel = "widget", CreatedAt = inPeriod, UpdatedAt = inPeriod });

        await db.SaveChangesAsync();
    }

    // ── Test-double IAnalyticsQueryService — the real AnalyticsQueryService
    //    has no reachable exception path beyond genuine DB/infra failure
    //    (no validation/throw branches in its own body); this fake is the
    //    only realistic way to exercise the AnalyticsQueryFailed branch
    //    deterministically (test 5, per this phase's own explicit
    //    instruction to state this rather than fabricate an artificial
    //    failure mode elsewhere). Every other member delegates to a real
    //    AnalyticsQueryService instance.
    private sealed class ThrowingAnalyticsQueryService(AppDbContext db) : IAnalyticsQueryService
    {
        private readonly AnalyticsQueryService _inner = new(db);

        public IReadOnlyCollection<string> ValidRangeKeys => _inner.ValidRangeKeys;
        public bool TryParseRange(string? range, out AnalyticsRangeWindow window) => _inner.TryParseRange(range, out window);
        public Task<List<DayCountEntry>> GetConversationsByDayAsync(AnalyticsRangeWindow window, CancellationToken ct) => _inner.GetConversationsByDayAsync(window, ct);
        public Task<List<StatusCountEntry>> GetConversationStatusDistributionAsync(AnalyticsRangeWindow window, CancellationToken ct) => _inner.GetConversationStatusDistributionAsync(window, ct);
        public Task<List<StatusCountEntry>> GetHandledBySnapshotAsync(CancellationToken ct) => _inner.GetHandledBySnapshotAsync(ct);
        public Task<List<DayCountEntry>> GetHandoffsByDayAsync(AnalyticsRangeWindow window, CancellationToken ct) => _inner.GetHandoffsByDayAsync(window, ct);
        public Task<List<StatusCountEntry>> GetTicketStatusDistributionAsync(AnalyticsRangeWindow window, CancellationToken ct) => _inner.GetTicketStatusDistributionAsync(window, ct);
        public AnalyticsRangeWindow BuildCalendarWindow(DateOnly periodStartDate, DateOnly periodEndDateExclusive, DateTimeOffset periodStartUtc, DateTimeOffset periodEndUtcExclusive) =>
            _inner.BuildCalendarWindow(periodStartDate, periodEndDateExclusive, periodStartUtc, periodEndUtcExclusive);
        public Task<List<DayCountEntry>> GetConversationsByDayForBusinessAsync(Guid businessId, AnalyticsRangeWindow window, CancellationToken ct) =>
            throw new InvalidOperationException("Simulated analytics failure for test.");
        public Task<List<StatusCountEntry>> GetConversationStatusDistributionForBusinessAsync(Guid businessId, AnalyticsRangeWindow window, CancellationToken ct) => _inner.GetConversationStatusDistributionForBusinessAsync(businessId, window, ct);
        public Task<List<StatusCountEntry>> GetHandledBySnapshotForBusinessAsync(Guid businessId, CancellationToken ct) => _inner.GetHandledBySnapshotForBusinessAsync(businessId, ct);
        public Task<List<DayCountEntry>> GetHandoffsByDayForBusinessAsync(Guid businessId, AnalyticsRangeWindow window, CancellationToken ct) => _inner.GetHandoffsByDayForBusinessAsync(businessId, window, ct);
        public Task<List<StatusCountEntry>> GetTicketStatusDistributionForBusinessAsync(Guid businessId, AnalyticsRangeWindow window, CancellationToken ct) => _inner.GetTicketStatusDistributionForBusinessAsync(businessId, window, ct);
    }

    /// <summary>
    /// Design §13.A/§13.C's crash window, reproduced faithfully with a real
    /// mechanism rather than a test-only production hook: this fake records
    /// the email as genuinely sent (proving the provider call itself
    /// succeeded) and then cancels the SAME CancellationToken ExecuteAsync
    /// passes into step 6's ExecuteUpdateAsync - simulating "the process is
    /// killed between the email call returning and the finalize write
    /// committing" without needing to fabricate a separate code path in
    /// ScheduledReportExecutionService itself.
    /// </summary>
    private sealed class CrashAfterSendEmailSender(CancellationTokenSource cts) : IEmailSender
    {
        public List<(string To, string Subject, string HtmlBody)> SentEmails { get; } = [];

        public Task SendEmailAsync(string to, string subject, string htmlBody)
        {
            SentEmails.Add((to, subject, htmlBody));
            cts.Cancel();
            return Task.CompletedTask;
        }
    }

    // ── 1. Happy path ──────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_HappyPath_SendsToSoleActiveOwner_AndFinalizesSent()
    {
        var businessId = await SeedBusinessAsync("Happy Path");
        var ownerId = await SeedOwnerAsync(businessId, isActive: true, "happy-owner");
        var ownerEmail = await OwnerEmailAsync(ownerId);
        var (widgetId, customerId) = await SeedCommerceFixtureAsync(businessId);
        await SeedKnownAnalyticsDataAsync(businessId, widgetId, customerId);
        var run = await SeedClaimedRunAsync(businessId);

        await using var db = DbFor();
        var emailSender = new FakeEmailSender();
        var service = ServiceFor(db, emailSender, ExecutionInstant);

        await service.ExecuteAsync(run, CancellationToken.None);

        var sent = Assert.Single(emailSender.SentEmails);
        Assert.Equal(ownerEmail, sent.To);

        var reloaded = await ReloadRunAsync(run.Id);
        Assert.Equal(ScheduledReportRunStatus.Sent, reloaded.Status);
        Assert.Equal(ExecutionInstant, reloaded.SentAt);
        Assert.Equal(ownerId, reloaded.RecipientOwnerId);
        Assert.Null(reloaded.FailureReason);
    }

    // ── 2. Zero active Owners ────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_ZeroActiveOwners_FailsWithNoActiveOwner_NeverSends()
    {
        var businessId = await SeedBusinessAsync("Zero Owners");
        var run = await SeedClaimedRunAsync(businessId);

        await using var db = DbFor();
        var emailSender = new FakeEmailSender();
        var service = ServiceFor(db, emailSender, ExecutionInstant);

        await service.ExecuteAsync(run, CancellationToken.None);

        Assert.Empty(emailSender.SentEmails);
        var reloaded = await ReloadRunAsync(run.Id);
        Assert.Equal(ScheduledReportRunStatus.Failed, reloaded.Status);
        Assert.Equal("NoActiveOwner", reloaded.FailureReason);
        Assert.Null(reloaded.SentAt);
        Assert.Null(reloaded.RecipientOwnerId);
    }

    // ── 3. Multiple active Owners ────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_MultipleActiveOwners_FailsWithMultipleActiveOwners_NeverSends()
    {
        var businessId = await SeedBusinessAsync("Multiple Owners");
        await SeedOwnerAsync(businessId, isActive: true, "multi-owner-a");
        await SeedOwnerAsync(businessId, isActive: true, "multi-owner-b");
        var run = await SeedClaimedRunAsync(businessId);

        await using var db = DbFor();
        var emailSender = new FakeEmailSender();
        var service = ServiceFor(db, emailSender, ExecutionInstant);

        await service.ExecuteAsync(run, CancellationToken.None);

        Assert.Empty(emailSender.SentEmails);
        var reloaded = await ReloadRunAsync(run.Id);
        Assert.Equal(ScheduledReportRunStatus.Failed, reloaded.Status);
        Assert.Equal("MultipleActiveOwners", reloaded.FailureReason);
        Assert.Null(reloaded.SentAt);
        Assert.Null(reloaded.RecipientOwnerId);
    }

    // ── 4. Inactive Owner excluded ───────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_OneActiveOneInactiveOwner_ResolvesToActiveOnly_Sends()
    {
        var businessId = await SeedBusinessAsync("Inactive Excluded");
        var activeOwnerId = await SeedOwnerAsync(businessId, isActive: true, "inactive-test-active");
        await SeedOwnerAsync(businessId, isActive: false, "inactive-test-inactive");
        var activeOwnerEmail = await OwnerEmailAsync(activeOwnerId);
        var run = await SeedClaimedRunAsync(businessId);

        await using var db = DbFor();
        var emailSender = new FakeEmailSender();
        var service = ServiceFor(db, emailSender, ExecutionInstant);

        await service.ExecuteAsync(run, CancellationToken.None);

        var sent = Assert.Single(emailSender.SentEmails);
        Assert.Equal(activeOwnerEmail, sent.To);

        var reloaded = await ReloadRunAsync(run.Id);
        Assert.Equal(ScheduledReportRunStatus.Sent, reloaded.Status);
        Assert.Equal(activeOwnerId, reloaded.RecipientOwnerId);
    }

    // ── 5. Analytics failure ─────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_AnalyticsQueryThrows_FailsWithAnalyticsQueryFailed_NeverSends()
    {
        // The real AnalyticsQueryService has no reachable exception path
        // short of a genuine DB/infra failure (no validation or throw
        // branches anywhere in its own body) - a fake IAnalyticsQueryService
        // double is the only realistic, deterministic way to exercise this
        // branch, stated explicitly per this phase's own instruction rather
        // than fabricating an artificial failure mode inside the real
        // service.
        var businessId = await SeedBusinessAsync("Analytics Failure");
        await SeedOwnerAsync(businessId, isActive: true, "analytics-fail-owner");
        var run = await SeedClaimedRunAsync(businessId);

        await using var db = DbFor();
        var emailSender = new FakeEmailSender();
        var service = ServiceFor(db, emailSender, ExecutionInstant, new ThrowingAnalyticsQueryService(db));

        await service.ExecuteAsync(run, CancellationToken.None);

        Assert.Empty(emailSender.SentEmails);
        var reloaded = await ReloadRunAsync(run.Id);
        Assert.Equal(ScheduledReportRunStatus.Failed, reloaded.Status);
        Assert.Equal("AnalyticsQueryFailed", reloaded.FailureReason);
        Assert.Null(reloaded.SentAt);
    }

    // ── 6. Email send failure ─────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_EmailSendThrows_FailsWithEmailSendFailed_SentAtStaysNull_ExecuteAsyncItselfDoesNotThrow()
    {
        var businessId = await SeedBusinessAsync("Email Failure");
        await SeedOwnerAsync(businessId, isActive: true, "email-fail-owner");
        var run = await SeedClaimedRunAsync(businessId);

        await using var db = DbFor();
        // ThrowingEmailSender (backend.Tests) throws InvalidOperationException,
        // matching ResendEmailSender's own real exception type (design §3.6).
        var emailSender = new ThrowingEmailSender();
        var service = ServiceFor(db, emailSender, ExecutionInstant);

        // ExecuteAsync itself must not let this escape - the explicit
        // try/catch around step 5 (design §9 step 5) is what makes a future
        // Phase 7 poll-loop caller safe to call this for every business
        // without one business's email failure aborting the rest.
        await service.ExecuteAsync(run, CancellationToken.None);

        await emailSender.Called; // proves the real send path was actually invoked
        var reloaded = await ReloadRunAsync(run.Id);
        Assert.Equal(ScheduledReportRunStatus.Failed, reloaded.Status);
        Assert.StartsWith("EmailSendFailed:", reloaded.FailureReason);
        Assert.Null(reloaded.SentAt); // CK_ScheduledReportRuns_SentAt never violated
        Assert.Null(reloaded.RecipientOwnerId);
    }

    // ── 7. Crash-window scenario proof (design §13) ──────────────────────

    [Fact]
    public async Task ExecuteAsync_CrashBetweenSendAndFinalize_EmailActuallySent_ButRowStaysProcessing_AtMostOnceSemantics()
    {
        var businessId = await SeedBusinessAsync("Crash Window");
        await SeedOwnerAsync(businessId, isActive: true, "crash-window-owner");
        var run = await SeedClaimedRunAsync(businessId);

        await using var db = DbFor();
        using var cts = new CancellationTokenSource();
        var emailSender = new CrashAfterSendEmailSender(cts);
        var service = ServiceFor(db, emailSender, ExecutionInstant);

        // The cancellation is triggered by the fake FROM INSIDE
        // SendEmailAsync (step 5) - by the time ExecuteAsync reaches step
        // 6's ExecuteUpdateAsync, the SAME token it was given is already
        // cancelled, reproducing "the process is killed between the
        // provider accepting the email and the finalize write committing"
        // (design §13.A/§13.C) with a real, honored CancellationToken, not
        // a fabricated hook.
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.ExecuteAsync(run, cts.Token));

        // The email was genuinely "sent" (accepted by the fake provider) -
        Assert.Single(emailSender.SentEmails);

        // ...but the local record never reflects it: the row is stuck in
        // Processing, never Sent, per design §10.3's own crash-point table
        // ("After step 5 returns... but before step 6's ExecuteUpdateAsync
        // commits") and §13.E's chosen semantic (at-most-once from this
        // application's own automatic behavior, with a narrow, named,
        // under-reporting residual risk in exactly this window - never an
        // over-sending risk, since nothing here ever re-attempts step 5 for
        // this run).
        var reloaded = await ReloadRunAsync(run.Id);
        Assert.Equal(ScheduledReportRunStatus.Processing, reloaded.Status);
        Assert.Null(reloaded.SentAt);
        Assert.Null(reloaded.RecipientOwnerId);
    }

    // ── 8. TimeProvider/timestamp correctness ────────────────────────────

    [Fact]
    public async Task ExecuteAsync_Sent_SentAt_ComesFromInjectedTimeProvider_NotWallClock()
    {
        var businessId = await SeedBusinessAsync("TimeProvider Correctness");
        await SeedOwnerAsync(businessId, isActive: true, "timeprovider-owner");
        var run = await SeedClaimedRunAsync(businessId);

        // Deliberately far from the real wall-clock "now" at test-execution
        // time, so this assertion cannot pass by coincidence the way
        // asserting against DateTimeOffset.UtcNow with a tolerance window
        // could.
        var fixedInstant = new DateTimeOffset(2031, 3, 3, 3, 3, 3, TimeSpan.Zero);

        await using var db = DbFor();
        var emailSender = new FakeEmailSender();
        var service = ServiceFor(db, emailSender, fixedInstant);

        await service.ExecuteAsync(run, CancellationToken.None);

        var reloaded = await ReloadRunAsync(run.Id);
        Assert.Equal(ScheduledReportRunStatus.Sent, reloaded.Status);
        Assert.Equal(fixedInstant, reloaded.SentAt);
    }

    // ── 9. Content correctness ───────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_EmailBody_ReflectsTheFiveStep21Metrics_ForCorrectBusinessAndPeriod_NeverAnotherBusinesss()
    {
        var businessId = await SeedBusinessAsync("Content Correctness A");
        var ownerId = await SeedOwnerAsync(businessId, isActive: true, "content-owner-a");
        var (widgetId, customerId) = await SeedCommerceFixtureAsync(businessId);
        await SeedKnownAnalyticsDataAsync(businessId, widgetId, customerId);

        // A second, unrelated business with DIFFERENT known data in the
        // SAME period - proves the rendered content is this run's own
        // business's data, never a leak from another tenant (design §17).
        var otherBusinessId = await SeedBusinessAsync("Content Correctness B (other tenant)");
        var (otherWidgetId, otherCustomerId) = await SeedCommerceFixtureAsync(otherBusinessId);
        await using (var seedDb = DbFor())
        {
            var inPeriod = PeriodStart.AddDays(1);
            for (var i = 0; i < 9; i++)
            {
                seedDb.Conversations.Add(new Conversation
                {
                    Id = Guid.NewGuid(), BusinessId = otherBusinessId, WidgetId = otherWidgetId,
                    Status = ConversationStatus.Closed, HandledBy = HandledBy.Human, Channel = "widget",
                    CreatedAt = inPeriod, UpdatedAt = inPeriod,
                });
            }
            await seedDb.SaveChangesAsync();
        }

        var run = await SeedClaimedRunAsync(businessId);

        await using var db = DbFor();
        var emailSender = new FakeEmailSender();
        var service = ServiceFor(db, emailSender, ExecutionInstant);

        await service.ExecuteAsync(run, CancellationToken.None);

        var sent = Assert.Single(emailSender.SentEmails);
        var html = sent.HtmlBody;

        // Metric 1 — conversations/day: 3 total in period (2 Open/Ai + 1 Closed/Human).
        Assert.Contains("<h2>Conversations per day</h2>", html);
        Assert.Contains("<p>Total: 3</p>", html);

        // Metric 2 — conversation status distribution: Open=2, Closed=1.
        Assert.Contains("<li>Open: 2</li>", html);
        Assert.Contains("<li>Closed: 1</li>", html);

        // Metric 3 — AI/human split (all-time snapshot): Ai=2, Human=1.
        Assert.Contains("<li>Ai: 2</li>", html);
        Assert.Contains("<li>Human: 1</li>", html);

        // Metric 4 — handoffs/day: 2 total in period.
        Assert.Contains("<h2>Handoffs per day</h2>", html);

        // Metric 5 — ticket status distribution: Open=1, Resolved=1.
        Assert.Contains("<li>Open: 1</li>", html);
        Assert.Contains("<li>Resolved: 1</li>", html);

        // Never Business B's leaked 9 - the only conversation-day total in
        // this email is the 3 asserted above, never 12 (3+9) or 9 alone.
        Assert.DoesNotContain("<p>Total: 12</p>", html);
        Assert.DoesNotContain("<p>Total: 9</p>", html);
    }
}
