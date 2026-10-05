using System.Reflection;
using backend.Auth;
using backend.Data;
using backend.Email;
using backend.Enums;
using backend.Interfaces;
using backend.Models;
using backend.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace backend.Tests;

/// <summary>
/// Step 24 Phase 7 — wiring proof for ScheduledReportBackgroundService, per
/// this phase's own "still needs adversarial proof it's actually connected"
/// requirement. Two layers:
///
/// 1. Direct, fake-backed tests of ScheduledReportBackgroundService itself,
///    driven through its public IHostedService.StartAsync/StopAsync
///    surface (the only way to exercise a BackgroundService's protected
///    ExecuteAsync from outside the class) — fast, deterministic, no DB, no
///    real host. These target the exact "looks wired but isn't" failure
///    modes named for this phase: PollAsync's claimed-run result silently
///    discarded rather than handed to ExecuteAsync, the CancellationToken
///    silently replaced with CancellationToken.None or an unrelated token,
///    and one claimed run's failure aborting the rest of that tick (or the
///    loop itself).
///
/// 2. One true end-to-end test through the real, DI-registered host
///    (NivoraApiFactory — real Program.cs wiring, real dev DB), proving the
///    actual composition root works, not just that a hand-built
///    ServiceCollection does. TimeProvider is overridden per-test via
///    WithWebHostBuilder, mirroring AnalystApiTests'/IntegrationsApiTests'
///    own established per-test DI-override convention, so the seeded run's
///    due-ness is deterministic, never dependent on real wall-clock timing.
/// </summary>
[Collection("ScheduledReports")]
public class ScheduledReportBackgroundServiceTests : IAsyncLifetime
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
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Businesses\" WHERE \"Id\" = ANY({bizIds})");
    }

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

    private static ScheduledReportRun BuildFakeRun() => new()
    {
        Id = Guid.NewGuid(),
        BusinessId = Guid.NewGuid(),
        ReportPeriodStart = new DateTimeOffset(2026, 8, 17, 0, 0, 0, TimeSpan.Zero),
        ReportPeriodEnd = new DateTimeOffset(2026, 8, 24, 0, 0, 0, TimeSpan.Zero),
        Status = ScheduledReportRunStatus.Processing,
        ClaimedAt = DateTimeOffset.UtcNow,
    };

    // ── Fakes for the direct, fast tests ─────────────────────────────────

    private sealed class RecordingSchedulerService(IReadOnlyList<ScheduledReportRun> toReturn) : IScheduledReportSchedulerService
    {
        public CancellationToken ReceivedToken { get; private set; }

        public Task<IReadOnlyList<ScheduledReportRun>> PollAsync(CancellationToken cancellationToken)
        {
            ReceivedToken = cancellationToken;
            return Task.FromResult(toReturn);
        }
    }

    private sealed class RecordingExecutionService : IScheduledReportExecutionService
    {
        public CancellationToken ReceivedToken { get; private set; }
        public ScheduledReportRun? ReceivedRun { get; private set; }
        public TaskCompletionSource Called { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ExecuteAsync(ScheduledReportRun claimedRun, CancellationToken cancellationToken)
        {
            ReceivedRun = claimedRun;
            ReceivedToken = cancellationToken;
            Called.TrySetResult();
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowsOnceThenRecordsExecutionService : IScheduledReportExecutionService
    {
        private int _callCount;
        public List<Guid> ReceivedRunIds { get; } = [];
        public TaskCompletionSource SecondCallCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ExecuteAsync(ScheduledReportRun claimedRun, CancellationToken cancellationToken)
        {
            ReceivedRunIds.Add(claimedRun.Id);
            if (++_callCount == 1)
            {
                throw new InvalidOperationException("Simulated ExecuteAsync failure for test.");
            }

            SecondCallCompleted.TrySetResult();
            return Task.CompletedTask;
        }
    }

    private sealed class CancelsUpstreamThenThrowsExecutionService(CancellationTokenSource upstream) : IScheduledReportExecutionService
    {
        public List<Guid> ReceivedRunIds { get; } = [];

        public Task ExecuteAsync(ScheduledReportRun claimedRun, CancellationToken cancellationToken)
        {
            ReceivedRunIds.Add(claimedRun.Id);
            upstream.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private static ServiceProvider BuildProvider(
        IScheduledReportSchedulerService scheduler, IScheduledReportExecutionService executor) =>
        new ServiceCollection()
            .AddScoped(_ => scheduler)
            .AddScoped(_ => executor)
            .BuildServiceProvider();

    // ── 1. Claimed run is actually handed to the execution service ───────

    [Fact]
    public async Task ExecuteAsync_HandsClaimedRun_ToExecutionService_NeverDiscardsPollResult()
    {
        var fakeRun = BuildFakeRun();
        var scheduler = new RecordingSchedulerService([fakeRun]);
        var executor = new RecordingExecutionService();
        var provider = BuildProvider(scheduler, executor);
        var backgroundService = new ScheduledReportBackgroundService(provider, NullLogger<ScheduledReportBackgroundService>.Instance);

        using var cts = new CancellationTokenSource();
        await ((IHostedService)backgroundService).StartAsync(cts.Token);
        await executor.Called.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await ((IHostedService)backgroundService).StopAsync(CancellationToken.None);

        Assert.Equal(fakeRun.Id, executor.ReceivedRun?.Id);
    }

    // ── 2. CancellationToken genuinely propagates end-to-end ─────────────

    [Fact]
    public async Task ExecuteAsync_CancellationToken_PropagatesToSchedulerAndExecutor_NeverNoneOrUnrelated()
    {
        var fakeRun = BuildFakeRun();
        var scheduler = new RecordingSchedulerService([fakeRun]);
        var executor = new RecordingExecutionService();
        var provider = BuildProvider(scheduler, executor);
        var backgroundService = new ScheduledReportBackgroundService(provider, NullLogger<ScheduledReportBackgroundService>.Instance);

        using var cts = new CancellationTokenSource();
        await ((IHostedService)backgroundService).StartAsync(cts.Token);
        await executor.Called.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Rules out CancellationToken.None outright - it can never be
        // cancelled, so CanBeCanceled is always false for it.
        Assert.True(scheduler.ReceivedToken.CanBeCanceled);
        Assert.True(executor.ReceivedToken.CanBeCanceled);
        Assert.False(scheduler.ReceivedToken.IsCancellationRequested);
        Assert.False(executor.ReceivedToken.IsCancellationRequested);

        cts.Cancel();

        // The SAME token instances captured above must observe this
        // cancellation - if production code had instead passed
        // CancellationToken.None, or an unrelated token, to either call,
        // these would stay false forever regardless of this Cancel() call.
        Assert.True(scheduler.ReceivedToken.IsCancellationRequested);
        Assert.True(executor.ReceivedToken.IsCancellationRequested);

        await ((IHostedService)backgroundService).StopAsync(CancellationToken.None);
    }

    // ── 3. One claimed run's failure does not abort the rest of that tick ─

    [Fact]
    public async Task ExecuteAsync_OneClaimedRunThrows_StillProcessesTheRestOfThatTicksClaimedRuns()
    {
        var runA = BuildFakeRun();
        var runB = BuildFakeRun();
        var scheduler = new RecordingSchedulerService([runA, runB]);
        var executor = new ThrowsOnceThenRecordsExecutionService();
        var provider = BuildProvider(scheduler, executor);
        var backgroundService = new ScheduledReportBackgroundService(provider, NullLogger<ScheduledReportBackgroundService>.Instance);

        using var cts = new CancellationTokenSource();
        await ((IHostedService)backgroundService).StartAsync(cts.Token);
        await executor.SecondCallCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await ((IHostedService)backgroundService).StopAsync(CancellationToken.None);

        Assert.Equal(2, executor.ReceivedRunIds.Count);
        Assert.Contains(runA.Id, executor.ReceivedRunIds);
        Assert.Contains(runB.Id, executor.ReceivedRunIds);
    }

    // ── 4. Real shutdown cancellation is never swallowed as an ordinary
    //    per-run failure - it stops this tick's loop, not just that one run ─

    [Fact]
    public async Task ExecuteAsync_ShutdownCancellation_StopsProcessingFurtherRunsInThatTick_NeverTreatedAsOrdinaryFailure()
    {
        var runA = BuildFakeRun();
        var runB = BuildFakeRun();
        using var cts = new CancellationTokenSource();
        var scheduler = new RecordingSchedulerService([runA, runB]);
        var executor = new CancelsUpstreamThenThrowsExecutionService(cts);
        var provider = BuildProvider(scheduler, executor);
        var backgroundService = new ScheduledReportBackgroundService(provider, NullLogger<ScheduledReportBackgroundService>.Instance);

        await ((IHostedService)backgroundService).StartAsync(cts.Token);

        // runA's own ExecuteAsync call cancels cts and throws immediately -
        // give the background task a brief moment to observe that and
        // (correctly) stop, rather than proceeding to runB.
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        await ((IHostedService)backgroundService).StopAsync(CancellationToken.None);

        Assert.Single(executor.ReceivedRunIds); // only runA - runB never attempted
    }

    // ── 5. Poll interval matches design §8.1 exactly ─────────────────────

    [Fact]
    public void Interval_Is15Minutes_PerDesignSection8Point1()
    {
        var field = typeof(ScheduledReportBackgroundService)
            .GetField("Interval", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("ScheduledReportBackgroundService.Interval field not found - rename?");

        var interval = Assert.IsType<TimeSpan>(field.GetValue(null));
        Assert.Equal(TimeSpan.FromMinutes(15), interval);
    }

    // ── 6. True end-to-end: the real, DI-registered host ─────────────────

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

    private async Task<(Guid Id, string Email)> SeedOwnerAsync(Guid businessId)
    {
        await using var db = DbFor();
        var ownerRoleId = await db.Roles
            .Where(r => r.Name == RoleNames.Owner)
            .Select(r => r.Id)
            .SingleAsync();

        var userId = Guid.NewGuid();
        var email = $"phase7-wiring-owner-{Guid.NewGuid():N}@example.com".ToLowerInvariant();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "AspNetUsers"
                ("Id", "BusinessId", "UserName", "NormalizedUserName", "Email", "NormalizedEmail",
                 "EmailConfirmed", "IsActive", "CreatedAt", "SecurityStamp", "ConcurrencyStamp",
                 "PhoneNumberConfirmed", "TwoFactorEnabled", "LockoutEnabled", "AccessFailedCount")
            VALUES
                ({userId}, {businessId}, {email}, {email.ToUpperInvariant()}, {email}, {email.ToUpperInvariant()},
                 true, true, {DateTimeOffset.UtcNow}, {Guid.NewGuid().ToString()}, {Guid.NewGuid().ToString()},
                 false, false, true, 0)
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "AspNetUserRoles" ("UserId", "RoleId") VALUES ({userId}, {ownerRoleId})
            """);

        _userIdsToClean.Add(userId);
        return (userId, email);
    }

    // Deliberately NOT the same literal instant
    // ScheduledReportSchedulerServiceTests.cs uses (2026-08-26): this test's
    // scheduler is REAL and LIVE against the shared dev DB, and its
    // cross-tenant discovery query (design §8.3) would otherwise also
    // discover and race that OTHER file's own freshly-seeded, real-time-
    // enabled businesses whenever both happen to compute the same
    // ReportPeriodStart for "Europe/Copenhagen" - confirmed empirically
    // this phase (Phase 5's own suite is 100% clean in isolation, 3/3 runs,
    // and only flakes when this file's E2E test runs concurrently with a
    // colliding reference instant). A distant, distinct month/year, safely
    // past a known Monday 09:00 local, with no DST/month/year boundary
    // nearby, removes any chance of overlap with this or any other Step 24
    // test file's own fixture dates.
    private static readonly DateTimeOffset DueReferenceInstant = new(2027, 3, 1, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RealHostedBackgroundService_SeededDueBusiness_ClaimsExecutesAndSends_EndToEnd()
    {
        var businessId = await SeedBusinessAsync("Phase7 E2E Wiring");
        var (ownerId, ownerEmail) = await SeedOwnerAsync(businessId);

        // Per-test override of the two Step 24 service registrations
        // (layered on top of NivoraApiFactory's own base default, which
        // freezes them to a permanently-not-due instant for every OTHER
        // test - see that factory's own doc comment for why the shared,
        // DI-registered TimeProvider singleton itself is never touched:
        // ASP.NET Core Identity's cookie auth also resolves it, so
        // overriding it globally broke session-expiry tests). Last
        // registration wins, mirroring AnalystApiTests'/IntegrationsApiTests'
        // own established WithWebHostBuilder per-test-DI-override convention -
        // this proves THIS composition root (not a hand-built one) resolves
        // the fixed clock into both real scoped services.
        var dueTimeProvider = new FixedTimeProvider(DueReferenceInstant);
        await using var factory = new NivoraApiFactory { HostedServices = TestHostedServices.ScheduledReport }.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
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
            });
        });

        // Accessing .Services builds and starts the real generic host,
        // which starts ScheduledReportBackgroundService, the one hosted
        // service this host opts in to, whose do-while loop runs its
        // first PollAsync/ExecuteAsync tick immediately, before any
        // 15-minute wait. FakeEmailSender comes from NivoraApiFactory's own
        // base ConfigureWebHost override (unmodified here).
        var emailSender = (FakeEmailSender)factory.Services.GetRequiredService<IEmailSender>();

        ScheduledReportRun? finalRun = null;
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            await using var pollDb = DbFor();
            finalRun = await pollDb.ScheduledReportRuns.IgnoreQueryFilters()
                .SingleOrDefaultAsync(r => r.BusinessId == businessId);
            if (finalRun is { Status: not ScheduledReportRunStatus.Processing })
            {
                break;
            }

            await Task.Delay(200);
        }

        Assert.NotNull(finalRun);
        Assert.Equal(ScheduledReportRunStatus.Sent, finalRun.Status);
        Assert.Equal(ownerId, finalRun.RecipientOwnerId);
        Assert.NotNull(finalRun.SentAt);

        Assert.Single(emailSender.SentEmails, e => e.To == ownerEmail);
    }
}
