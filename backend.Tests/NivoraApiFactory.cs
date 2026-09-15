using Anthropic.Services;
using backend.Data;
using backend.Email;
using backend.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace backend.Tests;

/// <summary>
/// Hosts the real app (real Program.cs wiring, real Neon dev DB via the same
/// ConnectionStrings:DefaultConnection user-secret the app itself uses —
/// Development environment triggers ASP.NET Core's automatic user-secrets
/// loading for the entry assembly, same as running the app normally).
/// Deliberately not swapped to an in-memory/fake DB: the point of this
/// suite is proving the real EF Core global query filter + real Postgres
/// unique constraints + real cookie auth actually enforce tenant isolation,
/// not that a mock does.
///
/// IMessageService IS swapped, though — to a FakeMessageService, always,
/// regardless of whether Anthropic:ApiKey happens to be configured in this
/// environment. Every test using this factory predates Step 8 (or doesn't
/// care about Claude specifically) and must never incur a real, billed
/// Anthropic call just because it happens to POST a widget message. Tests
/// that genuinely want the real Claude integration use
/// LiveClaudeApiFactory instead, never this one.
///
/// Same reasoning, same pattern, for IVoyageEmbeddingClient as of Step 11 —
/// always swapped to FakeVoyageEmbeddingClient, regardless of whether
/// Voyage:ApiKey happens to be configured. Any test that creates or
/// content-edits a KnowledgeDocument through this factory (including every
/// pre-Step-11 KnowledgeDocumentsApiTests test) must never incur a real,
/// billed Voyage call just because Create/Update now indexes synchronously.
/// No live-Voyage-equivalent factory exists — Step 11 doesn't need one.
///
/// Same reasoning again for IEmailSender as of Step 21 — always swapped to
/// FakeEmailSender, regardless of whether Resend:ApiKey happens to be
/// configured. The widget OTP request endpoint is the first widget-
/// reachable path that sends a real email; no live-Resend-equivalent
/// factory exists here either.
/// </summary>
public class NivoraApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        // Step 24 Phase 7 diagnosis: the actual root cause of a suite-wide
        // regression that appeared only once a third real BackgroundService
        // went live was never database/connection-pool related (confirmed:
        // Npgsql's own client pool, MaxPoolSize=100, never got close to
        // exhausted — a direct pg_stat_activity poll during the heaviest
        // parallel run observed a peak of 42, and every one of those
        // connections was healthy/idle/"Client/ClientRead", i.e. Neon's
        // pooler's own normal warm-connection cache, not a leak). The real
        // cause: WebApplication.CreateBuilder's default host configuration
        // unconditionally adds Microsoft.Extensions.Logging.EventLog's
        // EventLogLoggerProvider on Windows (regardless of
        // UseWindowsService() detection — Program.cs never opted into or
        // configured this, it is purely a framework default), and every
        // Warning/Error-level log call across the WHOLE app (ASP.NET Core's
        // own request-pipeline logging included, not only this feature's)
        // funnels into it. Confirmed directly from a captured failure's own
        // stack trace: System.Diagnostics.EventLogInternal.OpenForWrite threw
        // ObjectDisposedException while DeveloperExceptionPageMiddleware was
        // logging an unrelated request's exception — the Windows Event Log
        // Win32 API is not safe under this many concurrent writers (24-way
        // default xUnit parallelism × many NivoraApiFactory hosts), and a
        // third live background service's own periodic LogWarning/LogError
        // calls were what tipped total concurrent Warning/Error-level log
        // volume high enough to make this latent instability manifest.
        // Fix: clear every provider the default host builder wired in
        // (Console, Debug, EventSource, EventLog) and re-add only Console +
        // Debug — the two actually useful for reading test output, neither
        // of which touches an OS-level, concurrency-fragile API the way
        // EventLog does. Purely a test-logging-sink change: nobody inspects
        // the Windows Event Log to read test results, the real
        // ScheduledReportBackgroundService still runs for real against the
        // real dev DB exactly as Phase 7 requires, and both xUnit
        // parallelism and Program.cs are untouched.
        builder.ConfigureLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddConsole();
            logging.AddDebug();
        });

        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IMessageService, FakeMessageService>();
            services.AddSingleton<IVoyageEmbeddingClient, FakeVoyageEmbeddingClient>();

            // Step 21: overrides the real AddHttpClient<IEmailSender,
            // ResendEmailSender> registration — same reasoning as the two
            // lines above, for the OTP request endpoint's email send.
            services.AddSingleton<IEmailSender, FakeEmailSender>();

            // Step 18: Program.cs registers zero IIntegrationOAuthProvider
            // implementations in production, by design. This is the one
            // test-only exception — added here (not Program.cs) so
            // IntegrationsApiTests can prove the full connect/callback/
            // disconnect flow end-to-end. TryAddEnumerable, not AddSingleton:
            // adds this as one entry in IEnumerable<IIntegrationOAuthProvider>
            // without displacing any other registration, mirroring how a
            // real Step 19 provider registration would coexist with others.
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IIntegrationOAuthProvider, FakeIntegrationOAuthProvider>());

            // Step 24 Phase 7 diagnosis (second, distinct mechanism from the
            // EventLog one above): ScheduledReportBackgroundService is now
            // genuinely live in every one of this factory's hosts, using
            // Program.cs's own AddSingleton(TimeProvider.System) — the real
            // wall clock. Its discovery query (design §8.3) is deliberately
            // cross-tenant (IgnoreQueryFilters(), no BusinessId scoping —
            // that is the whole point of Phase 5), so it can and does
            // discover ANY Business.ScheduledReportsEnabled=true row in the
            // shared dev DB, created by ANY OTHER concurrently-running
            // NivoraApiFactory-hosted test, not only this feature's own —
            // e.g. ScheduledReportsApiTests' own toggle-endpoint tests.
            // Confirmed directly from two distinct captured failures: (1)
            // Npgsql.PostgresException 23001 "violates RESTRICT setting of
            // foreign key constraint FK_ScheduledReportRuns_Businesses_
            // BusinessId" on a test's own cleanup DELETE FROM "Businesses" —
            // a real ScheduledReportRun row had been created for that
            // business by a scheduler tick this test never triggered and
            // has no way to know about; (2) ObjectDisposedException on
            // 'IServiceProvider' — some OTHER host's scheduler tick was
            // still mid-flight against a business when THAT host's own
            // unrelated test finished and disposed it.
            //
            // First attempt (reverted): freezing the SHARED, DI-registered
            // TimeProvider singleton itself. Wrong — TimeProvider is not
            // exclusive to this feature's own two services the way Phase 5's
            // own doc comment assumed (true for hand-written application
            // code, false for framework-internal consumers): ASP.NET Core
            // Identity's cookie authentication ALSO resolves the same
            // DI-registered TimeProvider for its ticket's IssuedUtc/
            // ExpiresUtc. Confirmed directly: freezing it broke
            // SessionExpirationTests (a session's absolute expiry came back
            // as the frozen instant instead of a real wall-clock-relative
            // one). Overriding the shared TimeProvider singleton is
            // therefore too broad a blast radius for this fix.
            //
            // Actual fix: override the two SERVICE registrations directly
            // instead, constructing each by hand with the frozen clock baked
            // in as a plain constructor argument, and resolving every OTHER
            // dependency normally from the container. Program.cs's own
            // AddSingleton(TimeProvider.System) registration is left
            // completely untouched — every other consumer of TimeProvider
            // (Identity's cookie auth included) keeps seeing the real system
            // clock, exactly as in production. Only these two services see
            // a frozen one.
            //
            // The frozen instant itself is a "now" that is honestly,
            // correctly not-yet-due for any business, in exactly the sense
            // Phase 5's own PollAsync_NotYetDueBusiness_ClaimsNothing test
            // already covers — a real, valid answer the real scheduler
            // computes for itself, never a fake/skipped/short-circuited one.
            // 2026-01-05 03:00 UTC is a Monday: local time is 03:00 for
            // "UTC" and 04:00-05:00 (DST-dependent) for "Europe/Copenhagen"
            // — the only two TimeZone values any test in this suite ever
            // seeds a Business with (confirmed by grep) — both comfortably
            // inside [00:00, 09:00) local, i.e. BEFORE that Monday's own
            // SendAtUtc, so IsDue is false for literally any business,
            // regardless of which test created it or when. The calendar
            // date itself is arbitrary and never needs updating: this value
            // is consumed only by BusinessWeekPeriodCalculator's own
            // relative "most recently completed week as of now" formula,
            // never compared against a real calendar date.
            //
            // A test that DOES want the live scheduler to find something
            // due (this feature's own ScheduledReportBackgroundServiceTests)
            // layers its own further override of these same two
            // registrations on top via its own WithWebHostBuilder call,
            // mirroring exactly how AnalystApiTests already layers a
            // SequencedFakeMessageService on top of this same factory's own
            // IMessageService default — last registration wins, so that
            // test's own override still takes effect for that one test
            // only, without needing to touch the shared TimeProvider either.
            var frozenNeverDueTimeProvider = new FrozenTimeProvider(new DateTimeOffset(2026, 1, 5, 3, 0, 0, TimeSpan.Zero));
            services.AddScoped<IScheduledReportSchedulerService>(sp => new ScheduledReportSchedulerService(
                sp.GetRequiredService<AppDbContext>(),
                frozenNeverDueTimeProvider,
                sp.GetRequiredService<ILogger<ScheduledReportSchedulerService>>()));
            services.AddScoped<IScheduledReportExecutionService>(sp => new ScheduledReportExecutionService(
                sp.GetRequiredService<AppDbContext>(),
                sp.GetRequiredService<IAnalyticsQueryService>(),
                sp.GetRequiredService<IEmailSender>(),
                frozenNeverDueTimeProvider,
                sp.GetRequiredService<ILogger<ScheduledReportExecutionService>>()));
        });
    }

    /// <summary>
    /// Minimal hand-rolled fake, matching this codebase's general
    /// preference for small, dependency-free test doubles over a third-
    /// party fake-clock package (mirrors ScheduledReportSchedulerServiceTests'
    /// own FixedTimeProvider).
    /// </summary>
    internal sealed class FrozenTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
