using System.Net;
using System.Net.Http.Json;
using backend.Auth;
using backend.Constants;
using backend.Controllers;
using backend.Data;
using backend.Email;
using backend.Enums;
using backend.Interfaces;
using backend.Models;
using backend.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace backend.Tests;

/// <summary>
/// Step 24 — Final E2E verification. Not a new implementation phase: proves
/// Phases 1-7, each already independently designed/implemented/verified, work
/// together as one coherent pipeline against the real, non-faked path -
/// real registration (Step 4), the real SetScheduledReportsEnabled endpoint
/// (Phase 1), the real BusinessWeekPeriodCalculator-derived period, the real
/// ScheduledReportBackgroundService/ScheduledReportSchedulerService/
/// ScheduledReportExecutionService (Phases 5-7), the real Phase 4 explicit-
/// tenant analytics methods, and real report rendering. The ScheduledReportRun
/// row itself is never hand-constructed - it is created exclusively by the
/// real scheduler's own atomic claim (design §12.2), triggered by the real
/// hosted BackgroundService, exactly as production does it.
///
/// The single remaining test seam, in every test in this file, is the
/// external email-delivery boundary: NivoraApiFactory's own base
/// ConfigureWebHost already swaps IEmailSender to FakeEmailSender for every
/// test using it (Step 21's own established convention - no test in this
/// codebase sends a real, billed email), and one test additionally swaps it
/// to the existing ThrowingEmailSender to force a genuine, otherwise
/// infrastructure-dependent delivery failure. Nothing else in the pipeline is
/// faked: the scheduler, execution service, all five analytics calls, report
/// rendering, and every persisted state transition are the real production
/// code, run through the real DI container, against the real dev DB.
///
/// TimeProvider is overridden per-test the same way Phase 7's own E2E test
/// does it - constructing the two Step 24 services by hand with a frozen
/// clock baked in, never touching the shared TimeProvider singleton Identity's
/// cookie auth also depends on (see NivoraApiFactory's own doc comment for
/// why that distinction matters). The production 15-minute PeriodicTimer
/// interval is never shortened or modified - the real hosted service's first
/// tick always runs immediately on host start (before any timer wait), which
/// is what every test in this file relies on for a fast, deterministic run.
/// </summary>
[Collection("ScheduledReports")]
public class ScheduledReportsFinalE2ETests : IAsyncLifetime
{
    private readonly List<Guid> _businessIdsToClean = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await TestBusinessCleanup.DeleteThenDisposeAsync(_businessIdsToClean, null);
    }

    // ── Infra helpers ─────────────────────────────────────────────────────

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

    // A calendar week distinct from every other Step 24 test file's own
    // fixture instant (Phase 5: 2026-08-26; Phase 6: 2026-08-17/24; Phase 7:
    // 2027-03-01) - not required for correctness now that all four Step 24
    // files share one xUnit collection (never running in parallel with each
    // other), but kept distinct anyway as a second, redundant layer of
    // isolation.
    private static readonly DateTimeOffset DueReferenceInstant = new(2028, 1, 10, 10, 0, 0, TimeSpan.Zero);
    private const string BusinessTimeZone = "Europe/Copenhagen";

    private static HttpClient NewClient(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });

    private static async Task<(HttpResponseMessage Response, RegisterResponse? Body)> RegisterAsync(
        HttpClient client, string businessName, string email, string password = "Passw0rd!!") =>
        await PostRegisterAsync(client, new RegisterRequest(businessName, BusinessTimeZone, email, password));

    private static async Task<(HttpResponseMessage Response, RegisterResponse? Body)> PostRegisterAsync(
        HttpClient client, RegisterRequest request)
    {
        var response = await client.PostAsJsonAsync("/api/auth/register", request);
        var body = response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<RegisterResponse>() : null;
        return (response, body);
    }

    private static async Task ConfirmEmailAsync(WebApplicationFactory<Program> factory, HttpClient client, Guid userId)
    {
        string token;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await userManager.FindByIdAsync(userId.ToString());
            Assert.NotNull(user);
            token = await userManager.GenerateEmailConfirmationTokenAsync(user!);
        }

        var response = await client.PostAsJsonAsync("/api/auth/confirm-email", new ConfirmEmailRequest(userId, token));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static async Task<string> LoginAsync(WebApplicationFactory<Program> factory, string email, string password = "Passw0rd!!")
    {
        var client = NewClient(factory);
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.First().Split(';')[0]
            : throw new InvalidOperationException("No Set-Cookie header on a successful login response.");
    }

    /// <summary>
    /// The real production path for creating a Business + its sole Owner:
    /// POST /api/auth/register (Step 4), confirmed, logged in. Never a raw
    /// SQL/EF insert of either row.
    /// </summary>
    private async Task<(Guid BusinessId, Guid OwnerId, string OwnerEmail, string OwnerCookie)> RegisterBusinessWithOwnerAsync(
        WebApplicationFactory<Program> factory, string businessName)
    {
        var email = $"e2e-owner-{Guid.NewGuid():N}@example.com";
        using var anonClient = NewClient(factory);
        var (registerResponse, registerBody) = await RegisterAsync(anonClient, businessName, email);
        Assert.Equal(HttpStatusCode.OK, registerResponse.StatusCode);
        Assert.NotNull(registerBody);
        _businessIdsToClean.Add(registerBody!.BusinessId);

        await ConfirmEmailAsync(factory, anonClient, registerBody.UserId);
        var cookie = await LoginAsync(factory, email);

        return (registerBody.BusinessId, registerBody.UserId, email, cookie);
    }

    /// <summary>
    /// The real production path for enabling scheduled reports: PUT
    /// /api/business/scheduled-reports (Phase 1). Never a raw EF/SQL flip of
    /// Business.ScheduledReportsEnabled.
    /// </summary>
    private static async Task EnableScheduledReportsAsync(WebApplicationFactory<Program> factory, string ownerCookie)
    {
        using var client = NewClient(factory);
        var request = new HttpRequestMessage(HttpMethod.Put, "/api/business/scheduled-reports")
        {
            Content = JsonContent.Create(new SetScheduledReportsEnabledRequest(true)),
        };
        request.Headers.Add("Cookie", ownerCookie);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// No production endpoint exists anywhere in this codebase to deactivate
    /// a staff member (confirmed by the design doc's own §3.5 finding: "no
    /// staff-invite or role-management endpoint exists anywhere") - this is
    /// test-fixture precondition setup, not a pipeline fake, exactly like
    /// Phase 6's own SeedOwnerAsync(isActive: false) convention.
    /// </summary>
    private static async Task SetOwnerInactiveAsync(Guid ownerId)
    {
        await using var db = DbFor();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"AspNetUsers\" SET \"IsActive\" = false WHERE \"Id\" = {ownerId}");
    }

    private async Task<(Guid WidgetId, Guid CustomerId)> SeedCommerceFixtureAsync(Guid businessId, string label)
    {
        await using var db = DbFor();
        var widgetId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        db.Widgets.Add(new Widget
        {
            Id = widgetId,
            BusinessId = businessId,
            Name = $"{label} Widget",
            PublicKey = $"e2e-{Guid.NewGuid():N}",
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

    private static async Task SeedKnownAnalyticsDataAsync(
        Guid businessId, Guid widgetId, Guid customerId, DateTimeOffset inPeriod,
        int openAiConversations, int closedHumanConversations, int handoffs, int openTickets, int resolvedTickets)
    {
        await using var db = DbFor();

        for (var i = 0; i < openAiConversations; i++)
        {
            db.Conversations.Add(new Conversation
            {
                Id = Guid.NewGuid(), BusinessId = businessId, WidgetId = widgetId,
                Status = ConversationStatus.Open, HandledBy = HandledBy.Ai, Channel = "widget",
                CreatedAt = inPeriod, UpdatedAt = inPeriod,
            });
        }

        for (var i = 0; i < closedHumanConversations; i++)
        {
            db.Conversations.Add(new Conversation
            {
                Id = Guid.NewGuid(), BusinessId = businessId, WidgetId = widgetId,
                Status = ConversationStatus.Closed, HandledBy = HandledBy.Human, Channel = "widget",
                CreatedAt = inPeriod, UpdatedAt = inPeriod,
            });
        }

        for (var i = 0; i < handoffs; i++)
        {
            db.ConversationHandoffs.Add(new ConversationHandoff
            {
                Id = Guid.NewGuid(), BusinessId = businessId, ConversationId = null,
                FromHandledBy = HandledBy.Ai, ToHandledBy = HandledBy.Human,
                Reason = HandoffReasons.StaffManualTakeover, ActorType = ActorType.NivoraSystem, ActorId = null,
                CreatedAt = inPeriod,
            });
        }

        for (var i = 0; i < openTickets; i++)
        {
            db.Tickets.Add(new Ticket
            {
                Id = Guid.NewGuid(), BusinessId = businessId, CustomerId = customerId,
                Status = TicketStatus.Open, Priority = TicketPriority.Medium,
                PriorityReason = TicketPriorityReasons.ManualEscalation, Channel = "widget",
                CreatedAt = inPeriod, UpdatedAt = inPeriod,
            });
        }

        for (var i = 0; i < resolvedTickets; i++)
        {
            db.Tickets.Add(new Ticket
            {
                Id = Guid.NewGuid(), BusinessId = businessId, CustomerId = customerId,
                Status = TicketStatus.Resolved, Priority = TicketPriority.Medium,
                PriorityReason = TicketPriorityReasons.ManualEscalation, Channel = "widget",
                CreatedAt = inPeriod, UpdatedAt = inPeriod,
            });
        }

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Builds a NivoraApiFactory whose two Step 24 service registrations are
    /// overridden with a frozen, DUE TimeProvider - layered on top of the
    /// base factory's own default (permanently not-due, see that factory's
    /// doc comment), mirroring Phase 7's own established per-test override
    /// convention exactly. optionalEmailSender, when supplied, additionally
    /// overrides IEmailSender for this one host only (the single test-seam
    /// this file ever introduces, always at the external delivery boundary).
    /// </summary>
    private static WebApplicationFactory<Program> FactoryWithDueScheduler(IEmailSender? optionalEmailSender = null) =>
        new NivoraApiFactory { HostedServices = TestHostedServices.ScheduledReport }.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                var dueTimeProvider = new FixedTimeProvider(DueReferenceInstant);
                services.AddScoped<IScheduledReportSchedulerService>(sp => new ScheduledReportSchedulerService(
                    sp.GetRequiredService<AppDbContext>(),
                    dueTimeProvider,
                    sp.GetRequiredService<ILogger<ScheduledReportSchedulerService>>()));
                services.AddScoped<IScheduledReportExecutionService>(sp => new ScheduledReportExecutionService(
                    sp.GetRequiredService<AppDbContext>(),
                    sp.GetRequiredService<IAnalyticsQueryService>(),
                    sp.GetRequiredService<IEmailSender>(),
                    dueTimeProvider,
                    sp.GetRequiredService<ILogger<ScheduledReportExecutionService>>()));

                if (optionalEmailSender is not null)
                {
                    services.AddSingleton(optionalEmailSender);
                }
            });
        });

    private static async Task<ScheduledReportRun> WaitForTerminalStatusAsync(Guid businessId, int timeoutSeconds = 20)
    {
        ScheduledReportRun? run = null;
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            await using var db = DbFor();
            run = await db.ScheduledReportRuns.IgnoreQueryFilters()
                .SingleOrDefaultAsync(r => r.BusinessId == businessId);
            if (run is { Status: not ScheduledReportRunStatus.Processing })
            {
                return run;
            }

            await Task.Delay(200);
        }

        Assert.NotNull(run); // surfaces "never claimed at all" distinctly from "stuck Processing"
        return run!;
    }

    // ── 1. Happy path: real registration -> real enablement -> real claim ->
    //    real execution -> real Sent, with real analytics content and real
    //    tenant isolation against a second, unrelated business ────────────

    [Fact]
    public async Task RealPipeline_DueEnabledBusiness_ClaimsExecutesAndSends_WithCorrectContent_NeverLeakingAnotherBusiness()
    {
        // Setup happens on its OWN, plain NivoraApiFactory (its two Step 24
        // service registrations are safely frozen at "never due" by that
        // factory's own base default) - deliberately never the same factory
        // instance as the one under verification below. Both factories'
        // HTTP calls/DB writes land in the same shared dev DB regardless of
        // which host issued them; only the VERIFY factory's own hosted
        // BackgroundService matters for this test. This ordering matters:
        // any HttpClient/Services touch on a WebApplicationFactory starts
        // its real host immediately, including the first (and, within this
        // test's short wait window, only) tick of each hosted service that
        // factory opts in to (HostedServices; here only the verify factory
        // opts in, to ScheduledReportBackgroundService) - if
        // registration/enablement happened on the SAME factory being
        // verified, that first tick would race ahead of the business ever
        // being enabled, and (per the production 15-minute interval, never
        // shortened for this phase) there would be no second chance to
        // observe it within any reasonable test timeout.
        await using var setupFactory = new NivoraApiFactory();

        var (businessId, ownerId, ownerEmail, ownerCookie) = await RegisterBusinessWithOwnerAsync(setupFactory, "E2E Happy Path Business");
        await EnableScheduledReportsAsync(setupFactory, ownerCookie);

        var period = BusinessWeekPeriodCalculator.Compute(DueReferenceInstant, BusinessTimeZone);
        var inPeriod = period.PeriodStartUtc.AddDays(1);

        var (widgetId, customerId) = await SeedCommerceFixtureAsync(businessId, "Happy Path");
        await SeedKnownAnalyticsDataAsync(
            businessId, widgetId, customerId, inPeriod,
            openAiConversations: 2, closedHumanConversations: 1, handoffs: 2, openTickets: 1, resolvedTickets: 1);

        // A second, unrelated business - never enabled, never touched by the
        // scheduler, but seeded with large, differently-shaped data so any
        // analytics leak would be immediately and unambiguously visible.
        var (otherBusinessId, _, _, _) = await RegisterBusinessWithOwnerAsync(setupFactory, "E2E Unrelated Business");
        var (otherWidgetId, otherCustomerId) = await SeedCommerceFixtureAsync(otherBusinessId, "Unrelated");
        await SeedKnownAnalyticsDataAsync(
            otherBusinessId, otherWidgetId, otherCustomerId, inPeriod,
            openAiConversations: 0, closedHumanConversations: 9, handoffs: 7, openTickets: 6, resolvedTickets: 0);

        // NOW start the real verification host, for the first time, only
        // once the business already exists, is enabled, and has real
        // analytics data behind it - starting ScheduledReportBackgroundService
        // (opted in by FactoryWithDueScheduler), whose first tick runs
        // immediately (before any 15-minute wait).
        await using var factory = FactoryWithDueScheduler();
        var emailSender = (FakeEmailSender)factory.Services.GetRequiredService<IEmailSender>();

        var run = await WaitForTerminalStatusAsync(businessId);

        // ── Happy-path final state ──
        Assert.Equal(ScheduledReportRunStatus.Sent, run.Status);
        Assert.Equal(ownerId, run.RecipientOwnerId);
        Assert.NotNull(run.SentAt);
        Assert.Null(run.FailureReason);
        Assert.Equal(period.PeriodStartUtc, run.ReportPeriodStart);
        Assert.Equal(period.PeriodEndUtcExclusive, run.ReportPeriodEnd);

        var sent = Assert.Single(emailSender.SentEmails, e => e.To == ownerEmail);
        var html = sent.HtmlBody;

        // ── Content correctness: exactly this business's 5 metrics ──
        Assert.Contains("<p>Total: 3</p>", html); // 2 Open/Ai + 1 Closed/Human conversations
        Assert.Contains("<li>Open: 2</li>", html);
        Assert.Contains("<li>Closed: 1</li>", html);
        Assert.Contains("<li>Ai: 2</li>", html);
        Assert.Contains("<li>Human: 1</li>", html);
        Assert.Contains("<li>Resolved: 1</li>", html);

        // ── Tenant isolation: the other business's much larger numbers
        //    never appear ──
        Assert.DoesNotContain("<p>Total: 9</p>", html);
        Assert.DoesNotContain("<li>Closed: 9</li>", html);
        Assert.DoesNotContain("<li>Open: 6</li>", html);

        // The unrelated business was never enabled, so the scheduler must
        // never have created a run for it at all.
        await using var verifyDb = DbFor();
        Assert.False(await verifyDb.ScheduledReportRuns.IgnoreQueryFilters().AnyAsync(r => r.BusinessId == otherBusinessId));
    }

    // ── 2. Must not be sent: zero eligible active Owners ─────────────────

    [Fact]
    public async Task RealPipeline_NoActiveOwnerAtExecutionTime_NeverSends_ReachesFailedWithCorrectReason()
    {
        await using var setupFactory = new NivoraApiFactory();

        var (businessId, ownerId, ownerEmail, ownerCookie) = await RegisterBusinessWithOwnerAsync(setupFactory, "E2E Zero Owner Business");
        await EnableScheduledReportsAsync(setupFactory, ownerCookie);
        await SetOwnerInactiveAsync(ownerId); // the business's only Owner - now zero *active* Owners

        await using var factory = FactoryWithDueScheduler();
        var emailSender = (FakeEmailSender)factory.Services.GetRequiredService<IEmailSender>();

        var run = await WaitForTerminalStatusAsync(businessId);

        Assert.Equal(ScheduledReportRunStatus.Failed, run.Status);
        Assert.Equal("NoActiveOwner", run.FailureReason);
        Assert.Null(run.RecipientOwnerId);
        Assert.Null(run.SentAt);
        Assert.DoesNotContain(emailSender.SentEmails, e => e.To == ownerEmail);
    }

    // ── 3. Genuine execution failure: email delivery ──────────────────────

    [Fact]
    public async Task RealPipeline_EmailDeliveryFails_ReachesFailedWithReason_NeverSent_RecipientOwnerIdStaysNull()
    {
        await using var setupFactory = new NivoraApiFactory();
        var (businessId, _, _, ownerCookie) = await RegisterBusinessWithOwnerAsync(setupFactory, "E2E Email Failure Business");
        await EnableScheduledReportsAsync(setupFactory, ownerCookie);

        // The single test-seam this specific test adds, exactly at the
        // external delivery boundary, per this phase's own instructions -
        // scheduler, execution service, owner resolution, all five analytics
        // calls, and rendering are all still the real production code.
        var throwingSender = new ThrowingEmailSender();
        await using var factory = FactoryWithDueScheduler(throwingSender);
        _ = factory.Services; // force real host startup, only now that setup is complete

        var run = await WaitForTerminalStatusAsync(businessId);

        // FailureReason is EmailSendFailed (not NoActiveOwner/MultipleActiveOwners),
        // which itself proves Owner resolution succeeded before the real
        // ThrowingEmailSender was ever reached.
        Assert.Equal(ScheduledReportRunStatus.Failed, run.Status);
        Assert.StartsWith("EmailSendFailed:", run.FailureReason);
        Assert.Null(run.RecipientOwnerId); // per design: never written on any Failed path, even though Owner resolution itself succeeded
        Assert.Null(run.SentAt);
    }
}
