using System.Diagnostics;
using backend.Constants;
using backend.Data;
using backend.Enums;
using backend.Interfaces;
using backend.Models;
using backend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace backend.Tests;

/// <summary>
/// Step 27 Slice A1, Slice 2 — UnauthorizedActionAuditWriter. Design
/// docs/handoffs/billing-usage-a1-invocation-capture-design-v11.md §5, rule
/// (c). This writer's own contract only — rule (c) step 1's audit row, step
/// 2's isolated persistence, and fail-open. ClaudeReplyService's own
/// mismatch detection (rule (b)'s check) and its throw (rule (c), step 3)
/// are out of this slice's scope.
/// </summary>
public class UnauthorizedActionAuditWriterTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;
    private UnauthorizedActionAuditWriter _writer = null!;
    private EventCapturingLogger<UnauthorizedActionAuditWriter> _logger = null!;
    private readonly List<Guid> _businessIdsToClean = [];
    private string _connectionString = null!;

    public Task InitializeAsync()
    {
        _connectionString = TestDatabase.ConnectionString;
        _provider = new ServiceCollection()
            .AddSingleton<ICurrentTenantAccessor>(new FixedTenantAccessor(null))
            .AddDbContext<AppDbContext>(o => o.UseNpgsql(_connectionString, x => x.UseVector()))
            .AddScoped<IAuditLogService, AuditLogService>()
            .BuildServiceProvider();

        _logger = new EventCapturingLogger<UnauthorizedActionAuditWriter>();
        // Constructed directly here; DI registration is covered by
        // AiProviderWrapperRegistrationTests.
        _writer = new UnauthorizedActionAuditWriter(_provider.GetRequiredService<IServiceScopeFactory>(), _logger);

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await TestBusinessCleanup.DeleteThenDisposeAsync(_businessIdsToClean, _provider);
    }

    private async Task<Guid> SeedBusinessAsync(string name)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var businessId = Guid.NewGuid();
        db.Businesses.Add(new Business
        {
            Id = businessId,
            Name = name,
            TimeZone = "Europe/Copenhagen",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        _businessIdsToClean.Add(businessId);
        return businessId;
    }

    private async Task<AuditLog?> FindRowAsync(Guid businessId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.AuditLogs.IgnoreQueryFilters()
            .SingleOrDefaultAsync(a => a.BusinessId == businessId && a.EventType == AuditEventTypes.UnauthorizedActionBlocked);
    }

    // ── v11 test 13(d), writer level: non-enlistment and rollback
    //    survival (v11 §5 "Isolation", via rule (c) step 2) ─────────────
    // Covers the writer's own isolation only. Does NOT cover 13(d) through
    // ClaudeReplyService's actual mismatch check and throw — that lands with
    // the slice that wires rule (c) into ClaudeReplyService.

    [Fact]
    public async Task WriteAsync_DuringOpenCallerTransaction_IsVisibleBeforeCommit_AndSurvivesCallerRollback()
    {
        var businessId = await SeedBusinessAsync("Audit Writer Isolation Business");

        await using var callerScope = _provider.CreateAsyncScope();
        var callerDb = callerScope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var callerTx = await callerDb.Database.BeginTransactionAsync();

        var unrelatedId = Guid.NewGuid();
        callerDb.Businesses.Add(new Business
        {
            Id = unrelatedId,
            Name = "Caller Unrelated Work",
            TimeZone = "Europe/Copenhagen",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await callerDb.SaveChangesAsync();

        await _writer.WriteConversationBusinessMismatchBlockedAsync(businessId);

        // Stage 1: visible from a third, independent connection while the
        // caller's transaction is STILL open.
        var visibleDuringCallerTx = await FindRowAsync(businessId);
        Assert.NotNull(visibleDuringCallerTx);

        await callerTx.RollbackAsync();

        await using var afterScope = _provider.CreateAsyncScope();
        var afterDb = afterScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await afterDb.Businesses.IgnoreQueryFilters().AnyAsync(b => b.Id == unrelatedId));

        // Stage 2: still exists after the caller's rollback.
        var visibleAfterRollback = await FindRowAsync(businessId);
        Assert.NotNull(visibleAfterRollback);
    }

    // ── v11 test 13(c), writer level: audit row shape ───────────────────
    // Covers the row's fields only. Does NOT cover 13(a)/(b) (no provider
    // call; the operation throws) or "exactly one row" through
    // ClaudeReplyService — that lands with the slice that wires rule (c)
    // into ClaudeReplyService.

    [Fact]
    public async Task WriteAsync_RowShape_MatchesRuleC_Exactly()
    {
        var businessId = await SeedBusinessAsync("Audit Row Shape Business");

        await _writer.WriteConversationBusinessMismatchBlockedAsync(businessId);

        var row = await FindRowAsync(businessId);
        Assert.NotNull(row);
        Assert.Equal(businessId, row!.BusinessId);
        Assert.Equal(AuditEntityTypes.Business, row.EntityType);
        Assert.Equal(businessId.ToString(), row.EntityId);
        Assert.Equal(AuditEventTypes.UnauthorizedActionBlocked, row.EventType);
        Assert.Equal(ActorType.Customer, row.Actor);
        Assert.Null(row.ConversationId);
        Assert.Null(row.ChangedByUserId);
        Assert.Null(row.CorrelationId);
        Assert.Null(row.OldValue);
        Assert.Equal("ConversationBusinessMismatch", row.NewValue);

        // No reference anywhere in the row to a foreign conversation — the
        // only place one could hide is EntityId/OldValue/NewValue, all
        // asserted above to be exactly the fixed businessId/null/marker
        // string, and ConversationId itself is null.
    }

    // ── Owner decision D3 (post-v11, pending errata): audit write fails
    //    open with a dedicated alertable event. No v11 §8 test covers this.

    [Fact]
    public async Task WriteAsync_BusinessIdWithNoBusinessRow_FailsOpen_NoThrow_DiagnosticLogged()
    {
        var businessId = Guid.NewGuid(); // no seeded Business for this Id — FK Restrict violation on save

        var ex = await Record.ExceptionAsync(() => _writer.WriteConversationBusinessMismatchBlockedAsync(businessId));
        Assert.Null(ex);

        Assert.Null(await FindRowAsync(businessId));

        var entry = Assert.Single(_logger.Entries);
        var exceptionType = AssertD3Event(entry, businessId);
        Assert.Equal(nameof(DbUpdateException), exceptionType);
    }

    // ── Owner decision D2 (post-v11, pending errata): the writer's own
    //    bounded timeout, IsolatedWriteLimits.WriteTimeout. No v11 §8 test
    //    covers this.

    [Fact]
    public async Task WriteAsync_BlockedByLockOnReferencedBusiness_TimesOutBounded_FailsOpen_D3EventLogged()
    {
        var businessId = await SeedBusinessAsync("Audit Writer Timeout Business");
        var writeTimeout = IsolatedWriteLimits.WriteTimeout;
        var bound = writeTimeout + AiInvocationRecorderTests.TimeoutTestMargin;

        // A competing connection holds FOR UPDATE on exactly this test's own
        // Business row. The AuditLogs -> Businesses FK check on the writer's
        // insert takes FOR KEY SHARE on that row, which conflicts with FOR
        // UPDATE — so the insert blocks until this transaction resolves. Row
        // lock on this test's own row only; no table-wide lock.
        await using var blockerScope = _provider.CreateAsyncScope();
        var blockerDb = blockerScope.ServiceProvider.GetRequiredService<AppDbContext>();

        // The watchdog must fire before either of Npgsql's own timeouts could
        // end the blocked insert, or it could not tell the writer's timeout
        // apart from the framework's.
        var npgsql = new NpgsqlConnectionStringBuilder(_connectionString);
        var commandTimeout = TimeSpan.FromSeconds(blockerDb.Database.GetCommandTimeout() ?? npgsql.CommandTimeout);
        Assert.True(bound < commandTimeout, $"Watchdog {bound} must be below CommandTimeout {commandTimeout}.");
        Assert.True(bound < TimeSpan.FromSeconds(npgsql.Timeout), $"Watchdog {bound} must be below connection Timeout {npgsql.Timeout}s.");

        var blockerTx = await blockerDb.Database.BeginTransactionAsync();
        var sw = new Stopwatch();
        Task writeTask;
        Task winner;
        try
        {
            await blockerDb.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM \"Businesses\" WHERE \"Id\" = {businessId} FOR UPDATE");

            sw.Start();
            writeTask = _writer.WriteConversationBusinessMismatchBlockedAsync(businessId);

            // Watchdog at the same bound as the upper-bound assertion — a
            // broken or longer internal timeout loses to it.
            winner = await Task.WhenAny(writeTask, Task.Delay(bound));
            sw.Stop();
        }
        finally
        {
            // Released unconditionally, whichever branch won or threw.
            await blockerTx.RollbackAsync();
            await blockerTx.DisposeAsync();
        }

        Exception? ex = null;
        try { await writeTask.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (Exception caught) { ex = caught; }

        Assert.Same(writeTask, winner);
        Assert.Null(ex); // fails open — never throws to the caller

        Assert.True(sw.Elapsed >= writeTimeout,
            $"Expected >= {writeTimeout} (IsolatedWriteLimits.WriteTimeout), was {sw.Elapsed}.");
        Assert.True(sw.Elapsed < bound,
            $"Expected below {bound} (WriteTimeout + TimeoutTestMargin), was {sw.Elapsed}.");

        var entry = Assert.Single(_logger.Entries);
        var exceptionType = AssertD3Event(entry, businessId);
        // D3 logs only the type's simple name, so compare names directly:
        // OperationCanceledException or its one BCL subtype
        // TaskCanceledException — both mean the writer's own token fired.
        Assert.Contains(exceptionType, new[] { nameof(OperationCanceledException), nameof(TaskCanceledException) });

        // Lock released: the writer's own cancelled insert left nothing behind.
        await using var afterScope = _provider.CreateAsyncScope();
        var afterDb = afterScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await afterDb.AuditLogs.IgnoreQueryFilters().CountAsync(a => a.BusinessId == businessId));
    }

    // D3: exactly one dedicated event — stable EventId, Error level, the
    // writer's exact template, exactly the four fields, no exception object.
    // Returns the logged ExceptionType for the caller's own assertion.
    private static string AssertD3Event(
        (LogLevel Level, EventId EventId, IReadOnlyList<KeyValuePair<string, object?>> State, Exception? Exception) entry,
        Guid businessId)
    {
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Equal(27001, entry.EventId.Id);
        Assert.Equal("UnauthorizedActionAuditWriteFailed", entry.EventId.Name);
        Assert.Null(entry.Exception);

        var template = Assert.Single(entry.State, kv => kv.Key == "{OriginalFormat}").Value;
        Assert.Equal(
            "UnauthorizedActionBlocked audit row lost: isolated write failed. BusinessId={BusinessId} EventType={EventType} NewValue={NewValue} ExceptionType={ExceptionType}",
            template);

        var fields = entry.State
            .Where(kv => kv.Key != "{OriginalFormat}")
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        Assert.Equal(
            new[] { "BusinessId", "EventType", "ExceptionType", "NewValue" },
            fields.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
        Assert.Equal(businessId, fields["BusinessId"]);
        Assert.Equal(AuditEventTypes.UnauthorizedActionBlocked, fields["EventType"]);
        Assert.Equal("ConversationBusinessMismatch", fields["NewValue"]);
        return Assert.IsType<string>(fields["ExceptionType"]);
    }

    // Captures EventId and structured state, which the shared
    // CapturingLogger<T> does not, so D3's event (and the recorder's
    // Decision 1 diagnostic) can be asserted exactly.
    internal sealed class EventCapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, EventId EventId, IReadOnlyList<KeyValuePair<string, object?>> State, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var fields = state as IReadOnlyList<KeyValuePair<string, object?>> ?? [];
            Entries.Add((logLevel, eventId, fields.ToList(), exception));
        }
    }

    private class FixedTenantAccessor(Guid? businessId) : ICurrentTenantAccessor
    {
        public Guid? BusinessId => businessId;
    }
}
