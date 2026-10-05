using backend.Constants;
using backend.Data;
using backend.Enums;
using backend.Interfaces;
using backend.Models;
using backend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace backend.Tests;

/// <summary>
/// Step 25 Candidate 2, Phase 2 — per-job error-boundary proof for
/// DataLifecycleBackgroundService, mirroring
/// ScheduledReportBackgroundServiceTests' own two-layer shape exactly:
///
/// 1. Direct, fake-backed tests driven through the real
///    IHostedService.StartAsync/StopAsync surface (the only way to
///    exercise a BackgroundService's protected ExecuteAsync from outside
///    the class) — fast, deterministic, no DB, no real host. These prove
///    the exact failure-isolation and cancellation-vs-failure semantics
///    design §1.3/§1.4 requires, individually per job.
///
/// 2. One real-service, real-DB test proving the actual state transition
///    each job produces (retention purge, VerificationCode deletion,
///    ConsumedSignedIdentityToken deletion) is unchanged by this refactor
///    — the same outcomes CustomerDataLifecycleTests/
///    ConsumedSignedIdentityTokenCleanupServiceTests already prove for the
///    raw services, now proven through the actual, refactored
///    DataLifecycleBackgroundService composition instead of calling the
///    services directly.
/// </summary>
[Collection("ConversationAutoClose")]
public class DataLifecycleBackgroundServiceTests : IAsyncLifetime
{
    private readonly List<Guid> _businessIdsToClean = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_businessIdsToClean.Count == 0)
        {
            return;
        }

        await using var db = DbFor();
        var ids = _businessIdsToClean.ToArray();
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"AuditLogs\" WHERE \"BusinessId\" = ANY({ids})");
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"ConsumedSignedIdentityTokens\" WHERE \"BusinessId\" = ANY({ids})");
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"VerificationCodes\" WHERE \"BusinessId\" = ANY({ids})");
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Conversations\" WHERE \"BusinessId\" = ANY({ids})");
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Customers\" WHERE \"BusinessId\" = ANY({ids})");
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Widgets\" WHERE \"BusinessId\" = ANY({ids})");
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Businesses\" WHERE \"Id\" = ANY({ids})");
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

    // ── Fakes for the direct, fast tests — one small delegate-backed fake
    //    per interface, so each test supplies its own inline behavior. ──

    private sealed class FakeRetentionService(Func<CancellationToken, Task<int>> behavior) : IRetentionEnforcementService
    {
        public Task<int> EnforceRetentionAsync(CancellationToken cancellationToken = default) => behavior(cancellationToken);
    }

    private sealed class FakeVerificationCodeCleanupService(Func<CancellationToken, Task<int>> behavior) : IVerificationCodeCleanupService
    {
        public Task<int> CleanupExpiredAsync(CancellationToken cancellationToken = default) => behavior(cancellationToken);
    }

    private sealed class FakeSignedTokenCleanupService(Func<CancellationToken, Task<int>> behavior) : IConsumedSignedIdentityTokenCleanupService
    {
        public Task<int> CleanupExpiredAsync(CancellationToken cancellationToken = default) => behavior(cancellationToken);
    }

    private sealed class FakeConversationInactivityCloseService(Func<CancellationToken, Task<int>> behavior) : IConversationInactivityCloseService
    {
        public Task<int> CloseInactiveConversationsAsync(CancellationToken cancellationToken = default) => behavior(cancellationToken);
    }

    /// <summary>Signals `reached` the instant it's invoked (before any
    /// throw), then completes normally — lets a test await "this job was
    /// called" deterministically regardless of whether the job succeeds or
    /// is about to throw.</summary>
    private static Func<CancellationToken, Task<int>> Recording(TaskCompletionSource reached) => ct =>
    {
        reached.TrySetResult();
        return Task.FromResult(0);
    };

    private static Func<CancellationToken, Task<int>> Throwing(TaskCompletionSource reached, Exception exception) => ct =>
    {
        reached.TrySetResult();
        throw exception;
    };

    /// <summary>Cancels `upstream` (the same CancellationTokenSource driving
    /// stoppingToken) then throws OperationCanceledException tied to the
    /// received token — the exact "real shutdown, mid-job" scenario the
    /// locked `catch (OperationCanceledException) when
    /// (stoppingToken.IsCancellationRequested)` guard exists for.</summary>
    private static Func<CancellationToken, Task<int>> CancelsUpstreamThenThrows(TaskCompletionSource reached, CancellationTokenSource upstream) => ct =>
    {
        reached.TrySetResult();
        upstream.Cancel();
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(0);
    };

    /// <summary>Throws OperationCanceledException tied to an UNRELATED token
    /// — stoppingToken itself is never cancelled. Proves the guard is
    /// specifically `stoppingToken.IsCancellationRequested`, not a blanket
    /// catch of every OperationCanceledException.</summary>
    private static Func<CancellationToken, Task<int>> ThrowsUnrelatedCancellation(TaskCompletionSource reached) => ct =>
    {
        reached.TrySetResult();
        using var unrelated = new CancellationTokenSource();
        unrelated.Cancel();
        throw new OperationCanceledException(unrelated.Token);
    };

    private static ServiceProvider BuildProvider(
        IRetentionEnforcementService retention,
        IVerificationCodeCleanupService verificationCodeCleanup,
        IConsumedSignedIdentityTokenCleanupService signedTokenCleanup) =>
        new ServiceCollection()
            .AddScoped(_ => retention)
            .AddScoped(_ => verificationCodeCleanup)
            .AddScoped(_ => signedTokenCleanup)
            .BuildServiceProvider();

    /// <summary>Step 25 Candidate 2, Phase 4 — same shape as BuildProvider
    /// above, extended with the fourth job's fake. Kept as its own
    /// overload rather than changing BuildProvider's signature, so the
    /// five already-passing Phase 2 tests (which only know about three
    /// jobs) are untouched.</summary>
    private static ServiceProvider BuildProviderWithAutoClose(
        IRetentionEnforcementService retention,
        IVerificationCodeCleanupService verificationCodeCleanup,
        IConsumedSignedIdentityTokenCleanupService signedTokenCleanup,
        IConversationInactivityCloseService conversationInactivityClose) =>
        new ServiceCollection()
            .AddScoped(_ => retention)
            .AddScoped(_ => verificationCodeCleanup)
            .AddScoped(_ => signedTokenCleanup)
            .AddScoped(_ => conversationInactivityClose)
            .BuildServiceProvider();

    // ── 1. Retention throws — VerificationCodeCleanup/SignedTokenCleanup
    //      still run this same tick; the host is not torn down. ──────────

    [Fact]
    public async Task RetentionFailure_IsLogged_OtherTwoJobsStillRunThatTick()
    {
        var retentionReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var verifReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var signedReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var autoCloseReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var injected = new InvalidOperationException("Simulated retention failure for test.");

        var retention = new FakeRetentionService(Throwing(retentionReached, injected));
        var verif = new FakeVerificationCodeCleanupService(Recording(verifReached));
        var signed = new FakeSignedTokenCleanupService(Recording(signedReached));
        var autoClose = new FakeConversationInactivityCloseService(Recording(autoCloseReached));
        var provider = BuildProviderWithAutoClose(retention, verif, signed, autoClose);
        var logger = new CapturingLogger<DataLifecycleBackgroundService>();
        var backgroundService = new DataLifecycleBackgroundService(provider, logger);

        using var cts = new CancellationTokenSource();
        await ((IHostedService)backgroundService).StartAsync(cts.Token);
        await autoCloseReached.Task.WaitAsync(TimeSpan.FromSeconds(5)); // last job in sequence — proves the tick ran to completion
        await ((IHostedService)backgroundService).StopAsync(CancellationToken.None);

        Assert.True(verifReached.Task.IsCompletedSuccessfully);
        Assert.True(signedReached.Task.IsCompletedSuccessfully);
        Assert.True(autoCloseReached.Task.IsCompletedSuccessfully);

        var failureEntry = Assert.Single(logger.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains("RetentionEnforcement", failureEntry.Message);
        Assert.Same(injected, failureEntry.Exception);
    }

    // ── 2. VerificationCodeCleanup throws — Retention/SignedTokenCleanup
    //      still run this same tick; the host is not torn down. ──────────

    [Fact]
    public async Task VerificationCodeCleanupFailure_IsLogged_OtherTwoJobsStillRunThatTick()
    {
        var retentionReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var verifReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var signedReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var autoCloseReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var injected = new InvalidOperationException("Simulated verification-code cleanup failure for test.");

        var retention = new FakeRetentionService(Recording(retentionReached));
        var verif = new FakeVerificationCodeCleanupService(Throwing(verifReached, injected));
        var signed = new FakeSignedTokenCleanupService(Recording(signedReached));
        var autoClose = new FakeConversationInactivityCloseService(Recording(autoCloseReached));
        var provider = BuildProviderWithAutoClose(retention, verif, signed, autoClose);
        var logger = new CapturingLogger<DataLifecycleBackgroundService>();
        var backgroundService = new DataLifecycleBackgroundService(provider, logger);

        using var cts = new CancellationTokenSource();
        await ((IHostedService)backgroundService).StartAsync(cts.Token);
        await autoCloseReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await ((IHostedService)backgroundService).StopAsync(CancellationToken.None);

        Assert.True(retentionReached.Task.IsCompletedSuccessfully);
        Assert.True(signedReached.Task.IsCompletedSuccessfully);
        Assert.True(autoCloseReached.Task.IsCompletedSuccessfully);

        var failureEntry = Assert.Single(logger.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains("VerificationCodeCleanup", failureEntry.Message);
        Assert.Same(injected, failureEntry.Exception);
    }

    // ── 3. SignedTokenCleanup throws — Retention/VerificationCodeCleanup
    //      still run this same tick; the host is not torn down. ──────────

    [Fact]
    public async Task SignedTokenCleanupFailure_IsLogged_OtherTwoJobsStillRunThatTick()
    {
        var retentionReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var verifReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var signedReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var autoCloseReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var injected = new InvalidOperationException("Simulated signed-token cleanup failure for test.");

        var retention = new FakeRetentionService(Recording(retentionReached));
        var verif = new FakeVerificationCodeCleanupService(Recording(verifReached));
        var signed = new FakeSignedTokenCleanupService(Throwing(signedReached, injected));
        var autoClose = new FakeConversationInactivityCloseService(Recording(autoCloseReached));
        var provider = BuildProviderWithAutoClose(retention, verif, signed, autoClose);
        var logger = new CapturingLogger<DataLifecycleBackgroundService>();
        var backgroundService = new DataLifecycleBackgroundService(provider, logger);

        using var cts = new CancellationTokenSource();
        await ((IHostedService)backgroundService).StartAsync(cts.Token);
        await autoCloseReached.Task.WaitAsync(TimeSpan.FromSeconds(5)); // now the last job in sequence — proves the tick ran to completion
        await ((IHostedService)backgroundService).StopAsync(CancellationToken.None);

        Assert.True(retentionReached.Task.IsCompletedSuccessfully);
        Assert.True(verifReached.Task.IsCompletedSuccessfully);
        Assert.True(autoCloseReached.Task.IsCompletedSuccessfully);

        var failureEntry = Assert.Single(logger.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains("ConsumedSignedIdentityTokenCleanup", failureEntry.Message);
        Assert.Same(injected, failureEntry.Exception);
    }

    // ── 4. Host-shutdown cancellation mid-job: propagates, is never logged
    //      as an ordinary failure, and stops the cycle — later jobs in the
    //      same tick are never reached. ──────────────────────────────────

    [Fact]
    public async Task ShutdownCancellation_DuringRetention_StopsCycle_NeverLoggedAsFailure_LaterJobsNeverReached()
    {
        var retentionReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var verifReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var signedReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var cts = new CancellationTokenSource();
        var retention = new FakeRetentionService(CancelsUpstreamThenThrows(retentionReached, cts));
        var verif = new FakeVerificationCodeCleanupService(Recording(verifReached));
        var signed = new FakeSignedTokenCleanupService(Recording(signedReached));
        var provider = BuildProvider(retention, verif, signed);
        var logger = new CapturingLogger<DataLifecycleBackgroundService>();
        var backgroundService = new DataLifecycleBackgroundService(provider, logger);

        await ((IHostedService)backgroundService).StartAsync(cts.Token);
        await retentionReached.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // retentionReached firing means the cancellation+throw already
        // happened synchronously inside that call — give the loop a brief,
        // bounded moment to (incorrectly, if this regressed) proceed to the
        // next job, rather than asserting absence with zero wait at all.
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        await ((IHostedService)backgroundService).StopAsync(CancellationToken.None);

        Assert.False(verifReached.Task.IsCompleted); // never reached
        Assert.False(signedReached.Task.IsCompleted); // never reached
        Assert.Empty(logger.Entries); // never logged as an ordinary failure
    }

    // ── 5. An OperationCanceledException NOT tied to stoppingToken is an
    //      ordinary failure — logged, and the cycle continues. Proves the
    //      guard is `stoppingToken.IsCancellationRequested` specifically,
    //      not a blanket catch of every OperationCanceledException. ──────

    [Fact]
    public async Task UnrelatedOperationCanceledException_DuringRetention_IsOrdinaryFailure_OtherJobsStillRun()
    {
        var retentionReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var verifReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var signedReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var autoCloseReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var retention = new FakeRetentionService(ThrowsUnrelatedCancellation(retentionReached));
        var verif = new FakeVerificationCodeCleanupService(Recording(verifReached));
        var signed = new FakeSignedTokenCleanupService(Recording(signedReached));
        var autoClose = new FakeConversationInactivityCloseService(Recording(autoCloseReached));
        var provider = BuildProviderWithAutoClose(retention, verif, signed, autoClose);
        var logger = new CapturingLogger<DataLifecycleBackgroundService>();
        var backgroundService = new DataLifecycleBackgroundService(provider, logger);

        using var cts = new CancellationTokenSource(); // never cancelled — stoppingToken.IsCancellationRequested stays false throughout
        await ((IHostedService)backgroundService).StartAsync(cts.Token);
        await autoCloseReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await ((IHostedService)backgroundService).StopAsync(CancellationToken.None);

        Assert.True(verifReached.Task.IsCompletedSuccessfully);
        Assert.True(signedReached.Task.IsCompletedSuccessfully);
        Assert.True(autoCloseReached.Task.IsCompletedSuccessfully);

        var failureEntry = Assert.Single(logger.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains("RetentionEnforcement", failureEntry.Message);
        Assert.IsType<OperationCanceledException>(failureEntry.Exception);
    }

    // ── 6. Step 25 Candidate 2, Phase 4 — ConversationInactivityClose
    //      throws: the three original jobs (already sequenced before it)
    //      have already run; the failure is logged identifying it by name;
    //      the host is not torn down. ─────────────────────────────────────

    [Fact]
    public async Task ConversationInactivityCloseFailure_IsLogged_OtherThreeJobsAlreadyRan()
    {
        var retentionReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var verifReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var signedReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var autoCloseReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var injected = new InvalidOperationException("Simulated conversation auto-close failure for test.");

        var retention = new FakeRetentionService(Recording(retentionReached));
        var verif = new FakeVerificationCodeCleanupService(Recording(verifReached));
        var signed = new FakeSignedTokenCleanupService(Recording(signedReached));
        var autoClose = new FakeConversationInactivityCloseService(Throwing(autoCloseReached, injected));
        var provider = BuildProviderWithAutoClose(retention, verif, signed, autoClose);
        var logger = new CapturingLogger<DataLifecycleBackgroundService>();
        var backgroundService = new DataLifecycleBackgroundService(provider, logger);

        using var cts = new CancellationTokenSource();
        await ((IHostedService)backgroundService).StartAsync(cts.Token);
        await autoCloseReached.Task.WaitAsync(TimeSpan.FromSeconds(5)); // last job in sequence — proves the tick ran to completion
        await ((IHostedService)backgroundService).StopAsync(CancellationToken.None);

        Assert.True(retentionReached.Task.IsCompletedSuccessfully);
        Assert.True(verifReached.Task.IsCompletedSuccessfully);
        Assert.True(signedReached.Task.IsCompletedSuccessfully);

        var failureEntry = Assert.Single(logger.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains("ConversationInactivityClose", failureEntry.Message);
        Assert.Same(injected, failureEntry.Exception);
    }

    // ── 7. Retention throws — the other THREE jobs, including the new
    //      ConversationInactivityClose (now last in sequence), all still
    //      run that same tick — the "vice versa" half: a failure in any
    //      original job does not prevent the new job from running. ───────

    [Fact]
    public async Task RetentionFailure_AllThreeOtherJobs_IncludingConversationInactivityClose_StillRunThatTick()
    {
        var retentionReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var verifReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var signedReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var autoCloseReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var injected = new InvalidOperationException("Simulated retention failure for test.");

        var retention = new FakeRetentionService(Throwing(retentionReached, injected));
        var verif = new FakeVerificationCodeCleanupService(Recording(verifReached));
        var signed = new FakeSignedTokenCleanupService(Recording(signedReached));
        var autoClose = new FakeConversationInactivityCloseService(Recording(autoCloseReached));
        var provider = BuildProviderWithAutoClose(retention, verif, signed, autoClose);
        var logger = new CapturingLogger<DataLifecycleBackgroundService>();
        var backgroundService = new DataLifecycleBackgroundService(provider, logger);

        using var cts = new CancellationTokenSource();
        await ((IHostedService)backgroundService).StartAsync(cts.Token);
        await autoCloseReached.Task.WaitAsync(TimeSpan.FromSeconds(5)); // now the last job in sequence
        await ((IHostedService)backgroundService).StopAsync(CancellationToken.None);

        Assert.True(verifReached.Task.IsCompletedSuccessfully);
        Assert.True(signedReached.Task.IsCompletedSuccessfully);
        Assert.True(autoCloseReached.Task.IsCompletedSuccessfully); // the new job still ran despite retention's failure

        var failureEntry = Assert.Single(logger.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains("RetentionEnforcement", failureEntry.Message);
        Assert.Same(injected, failureEntry.Exception);
    }

    // ── 8. Real services, real DB, through the actual refactored
    //      DataLifecycleBackgroundService — proves the success path is
    //      behavior-preserving, not just "existing tests still pass". ────

    private async Task<Guid> SeedBusinessAsync(
        int retentionDays = 30, bool conversationAutoCloseEnabled = false, int conversationInactivityCloseDays = 14)
    {
        await using var db = DbFor();
        var business = new Business
        {
            Id = Guid.NewGuid(),
            Name = $"Phase2 Lifecycle {Guid.NewGuid():N}",
            TimeZone = "Europe/Copenhagen",
            RetentionDays = retentionDays,
            ConversationAutoCloseEnabled = conversationAutoCloseEnabled,
            ConversationInactivityCloseDays = conversationInactivityCloseDays,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Businesses.Add(business);
        await db.SaveChangesAsync();
        _businessIdsToClean.Add(business.Id);
        return business.Id;
    }

    private static async Task<Guid> SeedWidgetAsync(AppDbContext db, Guid businessId)
    {
        var widget = new Widget
        {
            Id = Guid.NewGuid(), BusinessId = businessId, Name = "Test Widget",
            PublicKey = Guid.NewGuid().ToString("N"), CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Widgets.Add(widget);
        return widget.Id;
    }

    private static Conversation MakeConversation(
        Guid businessId, Guid widgetId, DateTimeOffset createdAt, DateTimeOffset? lastMessageAt) => new()
    {
        Id = Guid.NewGuid(), BusinessId = businessId, WidgetId = widgetId,
        VisitorSessionId = $"session-{Guid.NewGuid():N}", Channel = "Website",
        CreatedAt = createdAt, UpdatedAt = createdAt, LastMessageAt = lastMessageAt,
    };

    [Fact]
    public async Task AllFourJobs_CoexistInOneCycle_ThroughTheRealBackgroundService_NoInterference_DisabledBusinessUntouched()
    {
        const int retentionDays = 30;
        const int inactivityDays = 14;
        var now = DateTimeOffset.UtcNow;
        var wellPastRetention = now.AddDays(-(retentionDays + 5));
        // Within retention's 30-day window (retention must NOT touch it)
        // but past auto-close's 14-day window (auto-close must close it) —
        // a dedicated, unambiguous target proving the two jobs don't
        // interfere with each other even though both act on Conversation
        // rows in the same tick.
        var pastAutoCloseOnly = now.AddDays(-20);

        // Business A — auto-close enabled, has a distinct target for
        // every one of the four jobs.
        var businessAId = await SeedBusinessAsync(retentionDays, conversationAutoCloseEnabled: true, inactivityDays);

        // Business B — auto-close NOT enabled (Phase 1's own default) —
        // the go-live safety proof: this business's own stale conversation
        // must stay untouched even though the job now runs live for real,
        // in the same process, in the same tick as Business A.
        var businessBId = await SeedBusinessAsync(retentionDays, conversationAutoCloseEnabled: false, inactivityDays);

        Guid retentionTargetId, autoCloseTargetId, disabledBusinessConversationId;
        Guid oldVerificationCodeId, oldSignedTokenId;
        await using (var seedDb = DbFor())
        {
            var widgetA = await SeedWidgetAsync(seedDb, businessAId);
            var widgetB = await SeedWidgetAsync(seedDb, businessBId);

            // Expired by CreatedAt (no messages) — retention's own fallback
            // path. Also past auto-close's threshold, but retention runs
            // first in sequence and anonymizes it, so auto-close's own
            // discovery correctly skips it (already anonymized) — proving
            // retention "wins" a genuine overlap cleanly, not a bug.
            var retentionTarget = MakeConversation(businessAId, widgetA, wellPastRetention, null);
            seedDb.Conversations.Add(retentionTarget);
            retentionTargetId = retentionTarget.Id;

            // Within retention's window, past auto-close's own — the
            // dedicated, unambiguous auto-close target.
            var autoCloseTarget = MakeConversation(businessAId, widgetA, now.AddDays(-90), pastAutoCloseOnly);
            seedDb.Conversations.Add(autoCloseTarget);
            autoCloseTargetId = autoCloseTarget.Id;

            // Business B — would be auto-close-eligible if enabled;
            // must stay untouched since ConversationAutoCloseEnabled is false.
            var disabledBusinessConversation = MakeConversation(businessBId, widgetB, now.AddDays(-90), pastAutoCloseOnly);
            seedDb.Conversations.Add(disabledBusinessConversation);
            disabledBusinessConversationId = disabledBusinessConversation.Id;

            var customer = new Customer
            {
                Id = Guid.NewGuid(), BusinessId = businessAId,
                ExternalId = $"shopify-{Guid.NewGuid():N}", CreatedAt = now, UpdatedAt = now,
            };
            seedDb.Customers.Add(customer);

            var oldCode = new VerificationCode
            {
                Id = Guid.NewGuid(), BusinessId = businessAId, ConversationId = retentionTarget.Id,
                CustomerId = customer.Id, Channel = "Website", CodeHash = "hash-old", ExpiresAt = now.AddDays(-40),
            };
            seedDb.VerificationCodes.Add(oldCode);
            oldVerificationCodeId = oldCode.Id;

            var oldToken = new ConsumedSignedIdentityToken
            {
                Id = Guid.NewGuid(), BusinessId = businessAId, Jti = Guid.NewGuid().ToString(),
                ConsumedAt = now.AddDays(-31),
            };
            seedDb.ConsumedSignedIdentityTokens.Add(oldToken);
            oldSignedTokenId = oldToken.Id;

            await seedDb.SaveChangesAsync();
        }

        var provider = new ServiceCollection()
            .AddSingleton<ICurrentTenantAccessor>(new FixedTenantAccessor(null))
            .AddDbContext<AppDbContext>(o => o.UseNpgsql(
                PostgresConnectionString.FromUri(new ConfigurationBuilder()
                    .AddUserSecrets<Program>().Build()
                    .GetConnectionString("DefaultConnection")!), x => x.UseVector()))
            .AddScoped<IConversationPurgeService, ConversationPurgeService>()
            .AddScoped<IPurgeFailurePoint, NoOpPurgeFailurePoint>()
            .AddScoped<IRetentionEnforcementService, RetentionEnforcementService>()
            .AddScoped<IVerificationCodeCleanupService, VerificationCodeCleanupService>()
            .AddScoped<IConsumedSignedIdentityTokenCleanupService, ConsumedSignedIdentityTokenCleanupService>()
            .AddScoped<IAuditLogService, AuditLogService>()
            .AddScoped<IConversationInactivityCloseService, ConversationInactivityCloseService>()
            .AddSingleton<Microsoft.Extensions.Logging.ILogger<ConversationInactivityCloseService>>(NullLogger<ConversationInactivityCloseService>.Instance)
            .BuildServiceProvider();

        var backgroundService = new DataLifecycleBackgroundService(provider, NullLogger<DataLifecycleBackgroundService>.Instance);

        using var cts = new CancellationTokenSource();
        await ((IHostedService)backgroundService).StartAsync(cts.Token);

        // First tick runs immediately inside ExecuteAsync's do-while, before
        // any real wait against the 24h PeriodicTimer interval — poll with a
        // bounded deadline for it to finish rather than assuming a fixed delay.
        var deadline = DateTime.UtcNow.AddSeconds(20);
        bool retentionTargetPurged = false, codeRemoved = false, tokenRemoved = false, autoCloseTargetClosed = false;
        while (DateTime.UtcNow < deadline)
        {
            await using var pollDb = DbFor();
            retentionTargetPurged = await pollDb.Conversations.IgnoreQueryFilters()
                .Where(c => c.Id == retentionTargetId).Select(c => c.IsAnonymized).SingleAsync();
            codeRemoved = !await pollDb.VerificationCodes.IgnoreQueryFilters().AnyAsync(v => v.Id == oldVerificationCodeId);
            tokenRemoved = !await pollDb.ConsumedSignedIdentityTokens.IgnoreQueryFilters().AnyAsync(t => t.Id == oldSignedTokenId);
            autoCloseTargetClosed = await pollDb.Conversations.IgnoreQueryFilters()
                .Where(c => c.Id == autoCloseTargetId).Select(c => c.Status == ConversationStatus.Closed).SingleAsync();

            if (retentionTargetPurged && codeRemoved && tokenRemoved && autoCloseTargetClosed)
            {
                break;
            }

            await Task.Delay(200);
        }

        await ((IHostedService)backgroundService).StopAsync(CancellationToken.None);

        Assert.True(retentionTargetPurged, "Retention job did not purge its own dedicated target through the real BackgroundService.");
        Assert.True(codeRemoved, "VerificationCodeCleanup job did not remove the expired VerificationCode through the real BackgroundService.");
        Assert.True(tokenRemoved, "SignedTokenCleanup job did not remove the old ConsumedSignedIdentityToken through the real BackgroundService.");
        Assert.True(autoCloseTargetClosed, "ConversationInactivityClose job did not close its own dedicated target through the real BackgroundService.");

        // No-interference proof: each job's dedicated target reflects only
        // that job's own effect, not the other's.
        await using var verifyDb = DbFor();
        var autoCloseTargetAfter = await verifyDb.Conversations.IgnoreQueryFilters().SingleAsync(c => c.Id == autoCloseTargetId);
        Assert.False(autoCloseTargetAfter.IsAnonymized); // retention never touched auto-close's own target
        var retentionTargetAfter = await verifyDb.Conversations.IgnoreQueryFilters().SingleAsync(c => c.Id == retentionTargetId);
        Assert.Equal(ConversationStatus.Open, retentionTargetAfter.Status); // auto-close never re-opened/closed retention's own target (already anonymized, correctly skipped)

        var autoCloseAudit = await verifyDb.AuditLogs.IgnoreQueryFilters()
            .CountAsync(a => a.ConversationId == autoCloseTargetId && a.EventType == AuditEventTypes.ConversationAutoClosed);
        Assert.Equal(1, autoCloseAudit);

        // Go-live safety: Business B's own auto-close-eligible conversation
        // is confirmed untouched — the tick above is already known to have
        // fully completed (all four Business A markers were satisfied
        // before StopAsync), so this is not a race against an unfinished tick.
        var disabledBusinessConversationAfter = await verifyDb.Conversations.IgnoreQueryFilters()
            .SingleAsync(c => c.Id == disabledBusinessConversationId);
        Assert.Equal(ConversationStatus.Open, disabledBusinessConversationAfter.Status);
        Assert.False(disabledBusinessConversationAfter.IsAnonymized);
        var disabledBusinessAudit = await verifyDb.AuditLogs.IgnoreQueryFilters()
            .CountAsync(a => a.ConversationId == disabledBusinessConversationId && a.EventType == AuditEventTypes.ConversationAutoClosed);
        Assert.Equal(0, disabledBusinessAudit);
    }

    // ── 9. Real Program.cs DI registration — every other test above uses a
    //      hand-built ServiceCollection, which cannot catch a missing
    //      registration in the actual composition root (a Scoped service is
    //      resolved lazily, per-scope, inside RunJobAsync's own try/catch —
    //      a missing registration would NOT crash host startup, it would
    //      silently become "just another ordinary job failure," swallowed
    //      and logged like any other, in a real running process). This test
    //      boots the REAL Program.cs host via NivoraApiFactory and proves
    //      the fourth job actually executes and closes a real conversation
    //      through it — the only test in this phase that can catch
    //      Program.cs's own registration being wrong. ────────────────────

    [Fact]
    public async Task RealProgramCsHost_SeededAutoCloseEnabledBusiness_ClosesStaleConversation()
    {
        await using var factory = new NivoraApiFactory { HostedServices = TestHostedServices.DataLifecycle };
        var now = DateTimeOffset.UtcNow;

        Guid conversationId;
        await using (var seedDb = DbFor())
        {
            var business = new Business
            {
                Id = Guid.NewGuid(),
                Name = $"Phase4 RealHost {Guid.NewGuid():N}",
                TimeZone = "Europe/Copenhagen",
                ConversationAutoCloseEnabled = true,
                ConversationInactivityCloseDays = 14,
                CreatedAt = now,
                UpdatedAt = now,
            };
            seedDb.Businesses.Add(business);
            _businessIdsToClean.Add(business.Id);

            var widgetId = await SeedWidgetAsync(seedDb, business.Id);
            var conversation = MakeConversation(business.Id, widgetId, now.AddDays(-90), now.AddDays(-20));
            seedDb.Conversations.Add(conversation);
            conversationId = conversation.Id;

            await seedDb.SaveChangesAsync();
        }

        // Accessing .Services builds and starts the REAL generic host from
        // the actual Program.cs — including every IHostedService it
        // registers, DataLifecycleBackgroundService (and, through it, the
        // real IConversationInactivityCloseService resolved from the real
        // container) among them. If that registration were missing or
        // wrong, this is the only test that would catch it — every other
        // test in this phase uses a hand-built ServiceCollection instead.
        _ = factory.Services;

        var deadline = DateTime.UtcNow.AddSeconds(20);
        bool closed = false;
        while (DateTime.UtcNow < deadline)
        {
            await using var pollDb = DbFor();
            closed = await pollDb.Conversations.IgnoreQueryFilters()
                .Where(c => c.Id == conversationId).Select(c => c.Status == ConversationStatus.Closed).SingleAsync();
            if (closed)
            {
                break;
            }

            await Task.Delay(200);
        }

        Assert.True(closed, "ConversationInactivityClose job did not close the conversation through the real Program.cs-registered host.");

        await using var verifyDb = DbFor();
        var auditCount = await verifyDb.AuditLogs.IgnoreQueryFilters()
            .CountAsync(a => a.ConversationId == conversationId && a.EventType == AuditEventTypes.ConversationAutoClosed);
        Assert.Equal(1, auditCount);
    }
}
