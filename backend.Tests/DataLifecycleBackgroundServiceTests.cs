using backend.Data;
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

    // ── 1. Retention throws — VerificationCodeCleanup/SignedTokenCleanup
    //      still run this same tick; the host is not torn down. ──────────

    [Fact]
    public async Task RetentionFailure_IsLogged_OtherTwoJobsStillRunThatTick()
    {
        var retentionReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var verifReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var signedReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var injected = new InvalidOperationException("Simulated retention failure for test.");

        var retention = new FakeRetentionService(Throwing(retentionReached, injected));
        var verif = new FakeVerificationCodeCleanupService(Recording(verifReached));
        var signed = new FakeSignedTokenCleanupService(Recording(signedReached));
        var provider = BuildProvider(retention, verif, signed);
        var logger = new CapturingLogger<DataLifecycleBackgroundService>();
        var backgroundService = new DataLifecycleBackgroundService(provider, logger);

        using var cts = new CancellationTokenSource();
        await ((IHostedService)backgroundService).StartAsync(cts.Token);
        await signedReached.Task.WaitAsync(TimeSpan.FromSeconds(5)); // last job in sequence — proves the tick ran to completion
        await ((IHostedService)backgroundService).StopAsync(CancellationToken.None);

        Assert.True(verifReached.Task.IsCompletedSuccessfully);
        Assert.True(signedReached.Task.IsCompletedSuccessfully);

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
        var injected = new InvalidOperationException("Simulated verification-code cleanup failure for test.");

        var retention = new FakeRetentionService(Recording(retentionReached));
        var verif = new FakeVerificationCodeCleanupService(Throwing(verifReached, injected));
        var signed = new FakeSignedTokenCleanupService(Recording(signedReached));
        var provider = BuildProvider(retention, verif, signed);
        var logger = new CapturingLogger<DataLifecycleBackgroundService>();
        var backgroundService = new DataLifecycleBackgroundService(provider, logger);

        using var cts = new CancellationTokenSource();
        await ((IHostedService)backgroundService).StartAsync(cts.Token);
        await signedReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await ((IHostedService)backgroundService).StopAsync(CancellationToken.None);

        Assert.True(retentionReached.Task.IsCompletedSuccessfully);
        Assert.True(signedReached.Task.IsCompletedSuccessfully);

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
        var injected = new InvalidOperationException("Simulated signed-token cleanup failure for test.");

        var retention = new FakeRetentionService(Recording(retentionReached));
        var verif = new FakeVerificationCodeCleanupService(Recording(verifReached));
        var signed = new FakeSignedTokenCleanupService(Throwing(signedReached, injected));
        var provider = BuildProvider(retention, verif, signed);
        var logger = new CapturingLogger<DataLifecycleBackgroundService>();
        var backgroundService = new DataLifecycleBackgroundService(provider, logger);

        using var cts = new CancellationTokenSource();
        await ((IHostedService)backgroundService).StartAsync(cts.Token);
        await signedReached.Task.WaitAsync(TimeSpan.FromSeconds(5)); // signed-token IS the failing job here — its own "reached" fires before it throws
        await ((IHostedService)backgroundService).StopAsync(CancellationToken.None);

        Assert.True(retentionReached.Task.IsCompletedSuccessfully);
        Assert.True(verifReached.Task.IsCompletedSuccessfully);

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

        var retention = new FakeRetentionService(ThrowsUnrelatedCancellation(retentionReached));
        var verif = new FakeVerificationCodeCleanupService(Recording(verifReached));
        var signed = new FakeSignedTokenCleanupService(Recording(signedReached));
        var provider = BuildProvider(retention, verif, signed);
        var logger = new CapturingLogger<DataLifecycleBackgroundService>();
        var backgroundService = new DataLifecycleBackgroundService(provider, logger);

        using var cts = new CancellationTokenSource(); // never cancelled — stoppingToken.IsCancellationRequested stays false throughout
        await ((IHostedService)backgroundService).StartAsync(cts.Token);
        await signedReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await ((IHostedService)backgroundService).StopAsync(CancellationToken.None);

        Assert.True(verifReached.Task.IsCompletedSuccessfully);
        Assert.True(signedReached.Task.IsCompletedSuccessfully);

        var failureEntry = Assert.Single(logger.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains("RetentionEnforcement", failureEntry.Message);
        Assert.IsType<OperationCanceledException>(failureEntry.Exception);
    }

    // ── 6. Real services, real DB, through the actual refactored
    //      DataLifecycleBackgroundService — proves the success path is
    //      behavior-preserving, not just "existing tests still pass". ────

    private async Task<Guid> SeedBusinessAsync(int retentionDays = 30)
    {
        await using var db = DbFor();
        var business = new Business
        {
            Id = Guid.NewGuid(),
            Name = $"Phase2 Lifecycle {Guid.NewGuid():N}",
            TimeZone = "Europe/Copenhagen",
            RetentionDays = retentionDays,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Businesses.Add(business);
        await db.SaveChangesAsync();
        _businessIdsToClean.Add(business.Id);
        return business.Id;
    }

    [Fact]
    public async Task RealServices_SuccessPath_AllThreeJobsProduceTheirExpectedStateChange_ThroughTheRealBackgroundService()
    {
        const int retentionDays = 30;
        var businessId = await SeedBusinessAsync(retentionDays);
        var now = DateTimeOffset.UtcNow;
        var wellPastRetention = now.AddDays(-(retentionDays + 5));

        Guid conversationId, widgetId;
        Guid oldVerificationCodeId, oldSignedTokenId;
        await using (var seedDb = DbFor())
        {
            var widget = new Widget
            {
                Id = Guid.NewGuid(), BusinessId = businessId, Name = "Test Widget",
                PublicKey = Guid.NewGuid().ToString("N"), CreatedAt = now, UpdatedAt = now,
            };
            seedDb.Widgets.Add(widget);
            widgetId = widget.Id;

            // Expired by CreatedAt (no messages) — retention's own fallback path.
            var conversation = new Conversation
            {
                Id = Guid.NewGuid(), BusinessId = businessId, WidgetId = widget.Id,
                VisitorSessionId = $"session-{Guid.NewGuid():N}", Channel = "Website",
                CreatedAt = wellPastRetention, UpdatedAt = wellPastRetention, LastMessageAt = null,
            };
            seedDb.Conversations.Add(conversation);
            conversationId = conversation.Id;

            var customer = new Customer
            {
                Id = Guid.NewGuid(), BusinessId = businessId,
                ExternalId = $"shopify-{Guid.NewGuid():N}", CreatedAt = now, UpdatedAt = now,
            };
            seedDb.Customers.Add(customer);

            var oldCode = new VerificationCode
            {
                Id = Guid.NewGuid(), BusinessId = businessId, ConversationId = conversation.Id,
                CustomerId = customer.Id, Channel = "Website", CodeHash = "hash-old", ExpiresAt = now.AddDays(-40),
            };
            seedDb.VerificationCodes.Add(oldCode);
            oldVerificationCodeId = oldCode.Id;

            var oldToken = new ConsumedSignedIdentityToken
            {
                Id = Guid.NewGuid(), BusinessId = businessId, Jti = Guid.NewGuid().ToString(),
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
            .BuildServiceProvider();

        var backgroundService = new DataLifecycleBackgroundService(provider, NullLogger<DataLifecycleBackgroundService>.Instance);

        using var cts = new CancellationTokenSource();
        await ((IHostedService)backgroundService).StartAsync(cts.Token);

        // First tick runs immediately inside ExecuteAsync's do-while, before
        // any real wait against the 24h PeriodicTimer interval — poll with a
        // bounded deadline for it to finish rather than assuming a fixed delay.
        var deadline = DateTime.UtcNow.AddSeconds(20);
        bool conversationPurged = false, codeRemoved = false, tokenRemoved = false;
        while (DateTime.UtcNow < deadline)
        {
            await using var pollDb = DbFor();
            conversationPurged = await pollDb.Conversations.IgnoreQueryFilters()
                .Where(c => c.Id == conversationId).Select(c => c.IsAnonymized).SingleAsync();
            codeRemoved = !await pollDb.VerificationCodes.IgnoreQueryFilters().AnyAsync(v => v.Id == oldVerificationCodeId);
            tokenRemoved = !await pollDb.ConsumedSignedIdentityTokens.IgnoreQueryFilters().AnyAsync(t => t.Id == oldSignedTokenId);

            if (conversationPurged && codeRemoved && tokenRemoved)
            {
                break;
            }

            await Task.Delay(200);
        }

        await ((IHostedService)backgroundService).StopAsync(CancellationToken.None);

        Assert.True(conversationPurged, "Retention job did not purge the expired Conversation through the real BackgroundService.");
        Assert.True(codeRemoved, "VerificationCodeCleanup job did not remove the expired VerificationCode through the real BackgroundService.");
        Assert.True(tokenRemoved, "SignedTokenCleanup job did not remove the old ConsumedSignedIdentityToken through the real BackgroundService.");
    }
}
