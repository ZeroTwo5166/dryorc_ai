using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using backend.Auth;
using backend.Data;
using backend.Email;
using backend.Interfaces;
using backend.Models;
using backend.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace backend.Tests;

/// <summary>
/// Staff invite slice 5b: race safety of the cap, the resend cooldown, double
/// accept and the row locks (DEC D10, D12, I5; design Section T rows T-I5a,
/// T-I5b, T-I5c, T-I5d, T-I5f; slice 6 adds T-I5e, cleanup versus resend,
/// D9). Service level, two operations in parallel, each in its own scope and
/// so on its own connection.
///
/// In the ConversationAutoClose collection (slice 6) because T-I5e seeds
/// 31-day-expired rows, which the global StaffInvitationCleanup delete of a
/// real DataLifecycle host could remove; see that collection's doc comment.
///
/// Waiting is deterministic, never a fixed sleep (implementation design
/// RY-2): the paused operation's IStaffInvitationLockCheckpoint double reads
/// pg_backend_pid() inside the operation's transaction and waits for a
/// release; the test then polls pg_stat_activity until another backend is
/// blocked by that pid, or the competitor completed, or the competitor
/// reached its own checkpoint, with a 30 s bound (helpers copied from
/// TestBusinessCleanupTests.cs). The 50 ms poll interval is only the loop
/// delay.
/// </summary>
[Collection("ConversationAutoClose")]
public class StaffInvitationConcurrencyTests : IAsyncLifetime
{
    private const string Password = "Passw0rd!!";

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private static readonly AsyncLocal<Guid?> Tenant = new();

    private readonly NivoraApiFactory _factory = new();
    private readonly List<Guid> _businessIdsToClean = [];
    private string _connectionString = null!;

    public Task InitializeAsync()
    {
        _connectionString = TestDatabase.ConnectionString;
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await TestBusinessCleanup.DeleteThenDisposeAsync(_businessIdsToClean, _factory);
    }

    // T-I5a
    [Fact]
    public async Task Cap_TwoParallelCreatesAt49_ExactlyOneSucceeds()
    {
        var host = BuildHost();
        var (businessId, owner) = await SeedBusinessWithOwnerAsync(host);
        await SeedRowsAsync(host, businessId, owner.UserId, 49);

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Task.Run(async () =>
        {
            await start.Task;
            return await RunAsync(host, businessId, s => s.CreateAsync(owner, NewEmail(), RoleNames.Agent, CancellationToken.None));
        });
        var second = Task.Run(async () =>
        {
            await start.Task;
            return await RunAsync(host, businessId, s => s.CreateAsync(owner, NewEmail(), RoleNames.Agent, CancellationToken.None));
        });
        start.SetResult();
        var results = await Task.WhenAll(first, second).WaitAsync(Bound);

        Assert.Equal(50, await CountRowsAsync(host, businessId));
        Assert.Single(results, r => r.Outcome == StaffInvitationOutcome.Created);
        Assert.Single(results, r => r.Outcome == StaffInvitationOutcome.Conflict);
    }

    // T-I5b
    [Fact]
    public async Task Cooldown_TwoParallelResends_OneSucceeds()
    {
        var host = BuildHost();
        var (businessId, owner) = await SeedBusinessWithOwnerAsync(host);
        var email = NewEmail();
        var invitation = await CreateInvitationAsync(host, owner, email);
        await MoveBackAsync(host, invitation.Id, TimeSpan.FromSeconds(61));
        var hashBefore = (await LoadInvitationAsync(host, invitation.Id)).TokenHash;

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Task.Run(async () =>
        {
            await start.Task;
            return await RunAsync(host, businessId, s => s.ResendAsync(owner, invitation.Id, CancellationToken.None));
        });
        var second = Task.Run(async () =>
        {
            await start.Task;
            return await RunAsync(host, businessId, s => s.ResendAsync(owner, invitation.Id, CancellationToken.None));
        });
        start.SetResult();
        var results = await Task.WhenAll(first, second).WaitAsync(Bound);

        Assert.Single(results, r => r.Outcome == StaffInvitationOutcome.Ok);
        Assert.Single(results, r => r.Outcome == StaffInvitationOutcome.TooManyRequests);

        // Exactly one new TokenHash: one resend email, and the stored hash is its token's.
        var emails = EmailsTo(host, email);
        Assert.Equal(2, emails.Count);
        var hashAfter = (await LoadInvitationAsync(host, invitation.Id)).TokenHash;
        Assert.NotEqual(hashBefore, hashAfter);
        Assert.Equal(HashOf(TokenIn(emails[1].HtmlBody)), hashAfter);
    }

    // T-I5c (implementation design G-2: two-party barrier at the Accept checkpoint)
    [Fact]
    public async Task DoubleAccept_OneUser()
    {
        var gate = new LockGate(StaffInvitationLockOperation.Accept);
        var host = BuildHost(gate);
        var (_, owner) = await SeedBusinessWithOwnerAsync(host);
        var email = NewEmail();
        await CreateInvitationAsync(host, owner, email);
        var token = TokenIn(EmailsTo(host, email).Last().HtmlBody);

        var first = Task.Run(() => RunAsync(host, null, s => s.AcceptAsync(token, Password, CancellationToken.None)));
        AcceptResult[] results;
        try
        {
            var firstPid = await gate.FirstPaused.Task.WaitAsync(Bound);
            var second = Task.Run(() => RunAsync(host, null, s => s.AcceptAsync(token, Password, CancellationToken.None)));

            // Release when both accepts reached the barrier, or the second is
            // blocked by the first one's row lock.
            Assert.True(
                await WaitUntilAsync(Bound, async () => gate.SecondPaused.Task.IsCompleted || await AnyBackendBlockedByAsync(firstPid)),
                "the second accept neither reached the barrier nor was blocked by the first one's row lock");

            gate.Release.TrySetResult();
            results = await Task.WhenAll(first, second).WaitAsync(Bound);
        }
        finally
        {
            gate.Release.TrySetResult();
        }

        Assert.Single(results, r => r.Outcome == StaffInvitationOutcome.Ok);
        Assert.Single(results, r => r.Outcome is StaffInvitationOutcome.InvalidLink or StaffInvitationOutcome.Conflict);
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.Users.IgnoreQueryFilters().CountAsync(u => u.UserName == email));
    }

    // T-I5d
    [Fact]
    public async Task AcceptVersusRevoke_FirstCommitWins()
    {
        var gate = new LockGate(StaffInvitationLockOperation.Revoke);
        var host = BuildHost(gate);
        var (businessId, owner) = await SeedBusinessWithOwnerAsync(host);
        var email = NewEmail();
        var invitation = await CreateInvitationAsync(host, owner, email);
        var token = TokenIn(EmailsTo(host, email).Last().HtmlBody);

        var revoke = Task.Run(() => RunAsync(host, businessId, s => s.RevokeAsync(owner, invitation.Id, CancellationToken.None)));
        Task<AcceptResult> accept;
        bool acceptReachedCheckpoint;
        bool acceptCompleted;
        var blocked = false;
        try
        {
            var revokePid = await gate.FirstPaused.Task.WaitAsync(Bound);
            accept = Task.Run(() => RunAsync(host, null, s => s.AcceptAsync(token, Password, CancellationToken.None)));

            await WaitUntilAsync(Bound, async () =>
                accept.IsCompleted
                || gate.ReachedOf(StaffInvitationLockOperation.Accept).Task.IsCompleted
                || (blocked = await AnyBackendBlockedByAsync(revokePid)));
            acceptReachedCheckpoint = gate.ReachedOf(StaffInvitationLockOperation.Accept).Task.IsCompleted;
            acceptCompleted = accept.IsCompleted;
        }
        finally
        {
            gate.Release.TrySetResult();
        }

        var revokeResult = await revoke.WaitAsync(Bound);

        // Accept calls its checkpoint right after its row lock, so while
        // revoke holds the lock it must not have got there.
        Assert.False(acceptReachedCheckpoint, "accept reached its checkpoint while revoke held the row lock");
        Assert.False(acceptCompleted, "accept completed while revoke held the row lock");
        Assert.True(blocked, "accept was not seen blocked by revoke's row lock");

        Assert.Equal(StaffInvitationOutcome.NoContent, revokeResult.Outcome);
        Assert.Equal(StaffInvitationOutcome.InvalidLink, (await accept.WaitAsync(Bound)).Outcome);
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.Users.IgnoreQueryFilters().AnyAsync(u => u.UserName == email));
    }

    // T-I5f
    [Fact]
    public async Task ResendVersusRevoke_StaffLockBlocks()
    {
        var gate = new LockGate(StaffInvitationLockOperation.Resend);
        var host = BuildHost(gate);
        var (businessId, owner) = await SeedBusinessWithOwnerAsync(host);
        var invitation = await CreateInvitationAsync(host, owner, NewEmail());
        await MoveBackAsync(host, invitation.Id, TimeSpan.FromSeconds(61));

        var resend = Task.Run(() => RunAsync(host, businessId, s => s.ResendAsync(owner, invitation.Id, CancellationToken.None)));
        Task<RevokeResult> revoke;
        bool revokeCompleted;
        var blocked = false;
        try
        {
            var resendPid = await gate.FirstPaused.Task.WaitAsync(Bound);
            revoke = Task.Run(() => RunAsync(host, businessId, s => s.RevokeAsync(owner, invitation.Id, CancellationToken.None)));

            await WaitUntilAsync(Bound, async () => revoke.IsCompleted || (blocked = await AnyBackendBlockedByAsync(resendPid)));
            revokeCompleted = revoke.IsCompleted;
        }
        finally
        {
            gate.Release.TrySetResult();
        }

        var resendResult = await resend.WaitAsync(Bound);
        var revokeResult = await revoke.WaitAsync(Bound);

        Assert.False(revokeCompleted, "revoke completed while resend held the row lock");
        Assert.True(blocked, "revoke was not seen blocked by resend's row lock");
        Assert.Equal(StaffInvitationOutcome.Ok, resendResult.Outcome);
        Assert.Equal(StaffInvitationOutcome.NoContent, revokeResult.Outcome);
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.StaffInvitations.IgnoreQueryFilters().AnyAsync(i => i.Id == invitation.Id));
    }

    // T-I5e (D9; design Section 12 "Resend interaction")
    [Fact]
    public async Task CleanupVersusResend_ResentRowSurvives()
    {
        var gate = new LockGate(StaffInvitationLockOperation.Resend);
        var host = BuildHost(gate);
        var (businessId, owner) = await SeedBusinessWithOwnerAsync(host);

        // Order 1: resend holds the row lock; the cleanup DELETE waits for it,
        // then re-evaluates its WHERE on the resent row and leaves it.
        var resentId = await SeedExpiredRowAsync(host, businessId, owner.UserId, TimeSpan.FromDays(31));
        var before = DateTimeOffset.UtcNow;
        var resend = Task.Run(() => RunAsync(host, businessId, s => s.ResendAsync(owner, resentId, CancellationToken.None)));
        Task<int> cleanup;
        bool cleanupCompleted;
        var blocked = false;
        try
        {
            var resendPid = await gate.FirstPaused.Task.WaitAsync(Bound);
            cleanup = Task.Run(() => RunCleanupAsync(host));

            await WaitUntilAsync(Bound, async () => cleanup.IsCompleted || (blocked = await AnyBackendBlockedByAsync(resendPid)));
            cleanupCompleted = cleanup.IsCompleted;
        }
        finally
        {
            gate.Release.TrySetResult();
        }

        var resendResult = await resend.WaitAsync(Bound);
        await cleanup.WaitAsync(Bound);
        var after = DateTimeOffset.UtcNow;

        Assert.False(cleanupCompleted, "cleanup completed while resend held the row lock");
        Assert.True(blocked, "cleanup was not seen blocked by resend's row lock");
        Assert.Equal(StaffInvitationOutcome.Ok, resendResult.Outcome);
        var survivor = await FindInvitationAsync(host, resentId);
        Assert.True(survivor is not null, "the cleanup deleted the resent row");
        Assert.InRange(survivor.ExpiresAt, before.AddDays(7).AddSeconds(-1), after.AddDays(7).AddSeconds(1));

        // Order 2: the cleanup commits first; resend's row lock then finds no
        // row and answers NotFound. The gate is released, so nothing pauses.
        var deletedId = await SeedExpiredRowAsync(host, businessId, owner.UserId, TimeSpan.FromDays(31));
        var deleted = await RunCleanupAsync(host);
        Assert.True(deleted >= 1, $"the cleanup returned {deleted}, expected at least the seeded expired row");
        Assert.Null(await FindInvitationAsync(host, deletedId));
        var lateResend = await RunAsync(host, businessId, s => s.ResendAsync(owner, deletedId, CancellationToken.None));
        Assert.Equal(StaffInvitationOutcome.NotFound, lateResend.Outcome);
    }

    // ── Harness (private, per-file doubles; CustomersApiTests.cs convention) ──

    /// <summary>Reads the static AsyncLocal, so each parallel flow carries its own tenant (design V8 "no tenant" = null).</summary>
    private sealed class AsyncLocalTenantAccessor : ICurrentTenantAccessor
    {
        public Guid? BusinessId => Tenant.Value;
    }

    /// <summary>
    /// Shared state of the gated checkpoint. Every operation completes its own
    /// Reached task; the paused operation, until Release, records its backend
    /// pid (first arrival) or signals SecondPaused (a second arrival) and waits.
    /// </summary>
    private sealed class LockGate(StaffInvitationLockOperation pausedOperation)
    {
        private readonly ConcurrentDictionary<StaffInvitationLockOperation, TaskCompletionSource> _reached = new();
        private int _pausedArrivals;

        public StaffInvitationLockOperation PausedOperation { get; } = pausedOperation;

        public TaskCompletionSource<int> FirstPaused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SecondPaused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReachedOf(StaffInvitationLockOperation operation) =>
            _reached.GetOrAdd(operation, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        public int NextPausedArrival() => Interlocked.Increment(ref _pausedArrivals);
    }

    /// <summary>
    /// Scoped, so it shares the operation's AppDbContext and therefore its
    /// connection and open transaction: pg_backend_pid() is the pid that
    /// holds the row lock (Npgsql's reported process id is not used; it does
    /// not match on the pooled connection, ConversationTagCorrectionServiceTests.cs).
    /// </summary>
    private sealed class GatedLockCheckpoint(LockGate gate, AppDbContext db) : IStaffInvitationLockCheckpoint
    {
        public async Task AfterRowLockAsync(StaffInvitationLockOperation op, CancellationToken ct)
        {
            gate.ReachedOf(op).TrySetResult();
            if (op != gate.PausedOperation || gate.Release.Task.IsCompleted)
            {
                return;
            }

            var pid = await db.Database.SqlQuery<int>($"SELECT pg_backend_pid() AS \"Value\"").SingleAsync(ct);
            if (gate.NextPausedArrival() == 1)
            {
                gate.FirstPaused.TrySetResult(pid);
            }
            else
            {
                gate.SecondPaused.TrySetResult();
            }

            await gate.Release.Task;
        }
    }

    private WebApplicationFactory<Program> BuildHost(LockGate? gate = null) =>
        _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.AddSingleton<ICurrentTenantAccessor, AsyncLocalTenantAccessor>();
            if (gate is not null)
            {
                s.AddScoped<IStaffInvitationLockCheckpoint>(sp => new GatedLockCheckpoint(gate, sp.GetRequiredService<AppDbContext>()));
            }
        }));

    private static async Task<T> RunAsync<T>(WebApplicationFactory<Program> host, Guid? tenant, Func<IStaffInvitationService, Task<T>> operation)
    {
        Tenant.Value = tenant;
        await using var scope = host.Services.CreateAsyncScope();
        return await operation(scope.ServiceProvider.GetRequiredService<IStaffInvitationService>());
    }

    private static string NewEmail() => $"staff-invite-{Guid.NewGuid():N}@example.test";

    private async Task<(Guid BusinessId, StaffCaller Owner)> SeedBusinessWithOwnerAsync(WebApplicationFactory<Program> host)
    {
        var businessId = Guid.NewGuid();
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Businesses.Add(new Business
        {
            Id = businessId,
            Name = $"Invite Co {businessId:N}",
            TimeZone = "Europe/Copenhagen",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        _businessIdsToClean.Add(businessId);

        // BusinessApiTests.SeedStaffIntoBusinessAsync shape.
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var email = NewEmail();
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var createResult = await userManager.CreateAsync(user, Password);
        Assert.True(createResult.Succeeded, string.Join("; ", createResult.Errors.Select(e => e.Description)));
        var roleResult = await userManager.AddToRoleAsync(user, RoleNames.Owner);
        Assert.True(roleResult.Succeeded, string.Join("; ", roleResult.Errors.Select(e => e.Description)));

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(NivoraClaimTypes.BusinessId, businessId.ToString()),
            new Claim(ClaimTypes.Role, RoleNames.Owner),
        ], "staff-invite-test"));
        return (businessId, new StaffCaller(user.Id, businessId, true, false, principal));
    }

    private static async Task<StaffInvitationResponse> CreateInvitationAsync(WebApplicationFactory<Program> host, StaffCaller owner, string email)
    {
        var result = await RunAsync(host, owner.BusinessId, s => s.CreateAsync(owner, email, RoleNames.Agent, CancellationToken.None));
        Assert.Equal(StaffInvitationOutcome.Created, result.Outcome);
        return result.Invitation!;
    }

    /// <summary>Seeds rows directly; every slice 2 CHECK holds.</summary>
    private static async Task SeedRowsAsync(WebApplicationFactory<Program> host, Guid businessId, Guid invitedBy, int count)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < count; i++)
        {
            var email = NewEmail();
            db.StaffInvitations.Add(new StaffInvitation
            {
                Id = Guid.NewGuid(),
                BusinessId = businessId,
                Email = email,
                NormalizedKey = userManager.NormalizeName(email),
                Role = RoleNames.Agent,
                TokenHash = SHA256.HashData(RandomNumberGenerator.GetBytes(32)),
                InvitedByUserId = invitedBy,
                CreatedAt = now,
                LastSentAt = now,
                ExpiresAt = now.AddDays(7),
            });
        }

        await db.SaveChangesAsync();
    }

    private static async Task<int> CountRowsAsync(WebApplicationFactory<Program> host, Guid businessId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.StaffInvitations.IgnoreQueryFilters().CountAsync(i => i.BusinessId == businessId);
    }

    private static async Task<StaffInvitation> LoadInvitationAsync(WebApplicationFactory<Program> host, Guid id)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.StaffInvitations.IgnoreQueryFilters().AsNoTracking().SingleAsync(i => i.Id == id);
    }

    private static async Task<StaffInvitation?> FindInvitationAsync(WebApplicationFactory<Program> host, Guid id)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.StaffInvitations.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(i => i.Id == id);
    }

    /// <summary>The real cleanup service (Program.cs registration) in its own scope, so on its own connection; no tenant, as in the background job.</summary>
    private static async Task<int> RunCleanupAsync(WebApplicationFactory<Program> host)
    {
        Tenant.Value = null;
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IStaffInvitationCleanupService>().DeleteExpiredAsync();
    }

    /// <summary>Seeds one row whose ExpiresAt is `expiredFor` in the past; CreatedAt = LastSentAt = ExpiresAt - 7 days, so every slice 2 CHECK holds and the resend cooldown has passed.</summary>
    private static async Task<Guid> SeedExpiredRowAsync(WebApplicationFactory<Program> host, Guid businessId, Guid invitedBy, TimeSpan expiredFor)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var email = NewEmail();
        var expiresAt = DateTimeOffset.UtcNow - expiredFor;
        var row = new StaffInvitation
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            Email = email,
            NormalizedKey = userManager.NormalizeName(email),
            Role = RoleNames.Agent,
            TokenHash = SHA256.HashData(RandomNumberGenerator.GetBytes(32)),
            InvitedByUserId = invitedBy,
            CreatedAt = expiresAt.AddDays(-7),
            LastSentAt = expiresAt.AddDays(-7),
            ExpiresAt = expiresAt,
        };
        db.StaffInvitations.Add(row);
        await db.SaveChangesAsync();
        return row.Id;
    }

    /// <summary>Moves CreatedAt and LastSentAt back together (cooldown passed; CK_StaffInvitations_LastSentAt_Range holds).</summary>
    private static async Task MoveBackAsync(WebApplicationFactory<Program> host, Guid id, TimeSpan by)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var updated = await db.StaffInvitations.IgnoreQueryFilters()
            .Where(i => i.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(i => i.CreatedAt, i => i.CreatedAt - by)
                .SetProperty(i => i.LastSentAt, i => i.LastSentAt - by));
        Assert.Equal(1, updated);
    }

    private static List<(string To, string Subject, string HtmlBody)> EmailsTo(WebApplicationFactory<Program> host, string email) =>
        ((FakeEmailSender)host.Services.GetRequiredService<IEmailSender>()).SentEmails.Where(e => e.To == email).ToList();

    private static string TokenIn(string html)
    {
        var match = Regex.Match(html, "#token=([A-Za-z0-9_-]{43})\"");
        Assert.True(match.Success, "no invitation link in the email");
        return match.Groups[1].Value;
    }

    private static byte[] HashOf(string token) => SHA256.HashData(System.Buffers.Text.Base64Url.DecodeFromChars(token));

    // ── Copied from TestBusinessCleanupTests.cs (the DB-level "is it blocked" proof) ──

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        return connection;
    }

    private async Task<bool> AnyBackendBlockedByAsync(int blockerPid)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM pg_stat_activity WHERE wait_event_type = 'Lock' AND @blocker = ANY(pg_blocking_pids(pid))",
            connection);
        command.Parameters.AddWithValue("blocker", blockerPid);
        return (long)(await command.ExecuteScalarAsync())! > 0;
    }

    private static async Task<bool> WaitUntilAsync(TimeSpan deadline, Func<Task<bool>> condition)
    {
        var until = DateTime.UtcNow + deadline;
        while (DateTime.UtcNow < until)
        {
            if (await condition())
            {
                return true;
            }
            await Task.Delay(50);
        }
        return false;
    }
}
