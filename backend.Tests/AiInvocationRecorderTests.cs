using System.Data.Common;
using System.Diagnostics;
using System.Reflection;
using backend.Constants;
using backend.Data;
using backend.Interfaces;
using backend.Models;
using backend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace backend.Tests;

/// <summary>
/// Step 27 Slice A1, Slice 2 — AiInvocationRecorder. Design
/// docs/handoffs/billing-usage-a1-invocation-capture-design-v11.md §5. Same
/// bare-ServiceProvider, real-dev-DB pattern as
/// AiProviderInvocationPersistenceTests/AuditLogServiceTests.
/// </summary>
public class AiInvocationRecorderTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;
    private AiInvocationRecorder _recorder = null!;
    private UnauthorizedActionAuditWriterTests.EventCapturingLogger<AiInvocationRecorder> _logger = null!;
    private readonly List<Guid> _businessIdsToClean = [];
    private string _connectionString = null!;

    public Task InitializeAsync()
    {
        var configuration = new ConfigurationBuilder()
            .AddUserSecrets<Program>()
            .Build();
        var connectionUri = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection not found via user-secrets.");

        _connectionString = PostgresConnectionString.FromUri(connectionUri);
        _provider = new ServiceCollection()
            .AddSingleton<ICurrentTenantAccessor>(new FixedTenantAccessor(null))
            .AddDbContext<AppDbContext>(o => o.UseNpgsql(_connectionString, x => x.UseVector()))
            .BuildServiceProvider();

        _logger = new UnauthorizedActionAuditWriterTests.EventCapturingLogger<AiInvocationRecorder>();
        // Not DI-registered: v11 does not assign DI registration to this
        // slice — registration lands with the recorder's first consumer.
        // Constructed directly here.
        _recorder = new AiInvocationRecorder(_provider.GetRequiredService<IServiceScopeFactory>(), _logger);

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_businessIdsToClean.Count > 0)
        {
            await using var scope = _provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var ids = _businessIdsToClean.ToArray();

            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"AiProviderInvocations\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Businesses\" WHERE \"Id\" = ANY({ids})");
        }

        await _provider.DisposeAsync();
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

    private static AiInvocationRecordRequest BuildValidRequest(Guid businessId, Guid? correlationId = null, int sequence = 0) => new()
    {
        BusinessId = businessId,
        Provider = AiProviders.Anthropic,
        Model = AnthropicModels.Default,
        Operation = AiOperations.CustomerReply,
        CorrelationId = correlationId ?? Guid.NewGuid(),
        Sequence = sequence,
        InputTokens = 100,
        OutputTokens = 50,
        Outcome = AiOutcomes.Succeeded,
        IsUsagePartial = false,
    };

    private async Task<AiProviderInvocation?> FindRowAsync(Guid correlationId, int sequence)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.AiProviderInvocations.IgnoreQueryFilters()
            .SingleOrDefaultAsync(x => x.CorrelationId == correlationId && x.Sequence == sequence);
    }

    // Decision 1 (v11 §5): the diagnostic carries BusinessId, Operation,
    // CorrelationId, Sequence, and the exception type — compared as the
    // logged structured field values, exactly.
    private static void AssertDecision1Diagnostic(
        (LogLevel Level, EventId EventId, IReadOnlyList<KeyValuePair<string, object?>> State, Exception? Exception) entry,
        AiInvocationRecordRequest request, string expectedExceptionTypeName)
    {
        Assert.Equal(LogLevel.Error, entry.Level);
        var fields = entry.State
            .Where(kv => kv.Key != "{OriginalFormat}")
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        Assert.Equal(
            new[] { "BusinessId", "CorrelationId", "ExceptionType", "Operation", "Sequence" },
            fields.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
        Assert.Equal(request.BusinessId, fields["BusinessId"]);
        Assert.Equal(request.Operation, fields["Operation"]);
        Assert.Equal(request.CorrelationId, fields["CorrelationId"]);
        Assert.Equal(request.Sequence, fields["Sequence"]);
        Assert.Equal(expectedExceptionTypeName, fields["ExceptionType"]);
    }

    // ── v11 §5 "Isolation": non-enlistment and rollback survival ────────

    [Fact]
    public async Task RecordAsync_DuringOpenCallerTransaction_IsVisibleBeforeCommit_AndSurvivesCallerRollback()
    {
        var businessId = await SeedBusinessAsync("Recorder Isolation Business");
        var request = BuildValidRequest(businessId);

        await using var callerScope = _provider.CreateAsyncScope();
        var callerDb = callerScope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var callerTx = await callerDb.Database.BeginTransactionAsync();

        // The caller's own unrelated work, inside its still-open
        // transaction, so the transaction is genuinely active — not merely
        // begun and left idle.
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

        await _recorder.RecordAsync(request);

        // Stage 1: visible from a third, independent connection while the
        // caller's transaction is STILL open.
        var visibleDuringCallerTx = await FindRowAsync(request.CorrelationId, request.Sequence);
        Assert.NotNull(visibleDuringCallerTx);
        Assert.Equal(businessId, visibleDuringCallerTx!.BusinessId);

        await callerTx.RollbackAsync();

        // The caller's own unrelated work really was rolled back — confirms
        // callerTx was live and the rollback was real, not a no-op.
        await using var afterScope = _provider.CreateAsyncScope();
        var afterDb = afterScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await afterDb.Businesses.IgnoreQueryFilters().AnyAsync(b => b.Id == unrelatedId));

        // Stage 2: the recorder's own row still exists after the caller's
        // rollback — it was never part of that transaction.
        var visibleAfterRollback = await FindRowAsync(request.CorrelationId, request.Sequence);
        Assert.NotNull(visibleAfterRollback);
    }

    // ── v11 test 10 (partial): fail-open on a natural FK failure ────────
    // Covers the recorder-level half only: no throw, no row, and the full
    // Decision 1 diagnostic. Does NOT cover "does not fail the caller's AI
    // operation" end to end — no provider wrapper exists yet; that half
    // lands with the wrapper slices (Slice 3/4).

    [Fact]
    public async Task RecordAsync_BusinessIdWithNoBusinessRow_FailsOpen_NoThrow_DiagnosticLogged()
    {
        // No seeded Business for this Id. Operation and Sequence deliberately
        // differ from every other test's CustomerReply/0, so a diagnostic that
        // hardcoded either value (or prefix-matched Sequence=0) would fail.
        var request = BuildValidRequest(Guid.NewGuid(), sequence: 5) with { Operation = AiOperations.AnalystSynthesis };

        var ex = await Record.ExceptionAsync(() => _recorder.RecordAsync(request));
        Assert.Null(ex);

        Assert.Null(await FindRowAsync(request.CorrelationId, request.Sequence));

        var entry = Assert.Single(_logger.Entries);
        AssertDecision1Diagnostic(entry, request, nameof(DbUpdateException));
    }

    // ── Errata 5 + Decision 1: duplicate (BusinessId, CorrelationId,
    //    Sequence) fails open ───────────────────────────────────────────

    [Fact]
    public async Task RecordAsync_DuplicateCorrelationSequence_FailsOpen_FirstRowIntact_NoThrow()
    {
        var businessId = await SeedBusinessAsync("Duplicate Key Business");
        var correlationId = Guid.NewGuid();
        var first = BuildValidRequest(businessId, correlationId, sequence: 0);

        await _recorder.RecordAsync(first);
        var firstRow = await FindRowAsync(correlationId, 0);
        Assert.NotNull(firstRow);

        var second = BuildValidRequest(businessId, correlationId, sequence: 0) with { InputTokens = 999 };
        var ex = await Record.ExceptionAsync(() => _recorder.RecordAsync(second));
        Assert.Null(ex);

        var afterRow = await FindRowAsync(correlationId, 0);
        Assert.NotNull(afterRow);
        Assert.Equal(firstRow!.Id, afterRow!.Id);
        Assert.Equal(100, afterRow.InputTokens); // unchanged — the duplicate write never landed

        var entry = Assert.Single(_logger.Entries);
        AssertDecision1Diagnostic(entry, second, nameof(DbUpdateException));
    }

    // ── v11 §5 "Tenant boundary": "The recorder performs no ownership
    //    comparison and no database read of its own." ──────────────────
    // A command interceptor on a provider built for this test alone (the
    // shared _provider has none) captures every command observed through
    // EF Core's command interception for the recorder's isolated context
    // during one RecordAsync call: exactly one, and it is the INSERT.

    [Fact]
    public async Task RecordAsync_ExecutesOnlyItsInsert_NoDatabaseRead()
    {
        var businessId = await SeedBusinessAsync("Recorder No-Read Business");
        var request = BuildValidRequest(businessId);

        var interceptor = new CommandCapturingInterceptor();
        await using var interceptedProvider = new ServiceCollection()
            .AddSingleton<ICurrentTenantAccessor>(new FixedTenantAccessor(null))
            .AddDbContext<AppDbContext>(o => o
                .UseNpgsql(_connectionString, x => x.UseVector())
                .AddInterceptors(interceptor))
            .BuildServiceProvider();
        var recorder = new AiInvocationRecorder(interceptedProvider.GetRequiredService<IServiceScopeFactory>(), _logger);

        await recorder.RecordAsync(request);

        Assert.Empty(_logger.Entries); // the write succeeded — not a failed-and-logged call
        Assert.NotNull(await FindRowAsync(request.CorrelationId, request.Sequence));

        var command = Assert.Single(interceptor.CommandTexts);
        Assert.StartsWith("INSERT INTO \"AiProviderInvocations\"", command.TrimStart());
        Assert.DoesNotContain("SELECT", command, StringComparison.OrdinalIgnoreCase);
    }

    // Records the text of every command EF executes through this context,
    // whichever execution method (reader, non-query, scalar) it uses.
    private sealed class CommandCapturingInterceptor : DbCommandInterceptor
    {
        public List<string> CommandTexts { get; } = [];

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            CommandTexts.Add(command.CommandText);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            CommandTexts.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            CommandTexts.Add(command.CommandText);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            CommandTexts.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
        {
            CommandTexts.Add(command.CommandText);
            return result;
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            CommandTexts.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    // ── v11 test 5 (partial) / §5 "Cancellation": no caller token ───────
    // Structural guard, so a caller's token has no path to the write. Covers:
    // every public method (declared or inherited) and public constructor on
    // the 4 listed types, and every public property of
    // AiInvocationRecordRequest — none may be CancellationToken-typed,
    // including one wrapped as ref/out/in or Nullable<CancellationToken>.
    // Still does NOT cover: delegate-typed parameters (e.g.
    // Func<CancellationToken>), extension methods, or explicit
    // implementations of unrelated interfaces. Nor: members of an
    // interface's own base interfaces (Type.GetMethods on an interface type
    // does not return those), fields or non-public members, or a token
    // nested inside a generic type argument (e.g. Task<CancellationToken>).
    // Also does NOT cover v11 test 5's Failed/IsUsagePartial recording
    // assertions — those need the provider wrappers (Slice 3/4).

    [Fact]
    public void IsolatedWriters_NoPublicMethodTakesCancellationToken()
    {
        Type[] types =
        [
            typeof(IAiInvocationRecorder),
            typeof(AiInvocationRecorder),
            typeof(IUnauthorizedActionAuditWriter),
            typeof(UnauthorizedActionAuditWriter),
        ];

        static bool IsCancellationToken(Type type)
        {
            var unwrapped = type.IsByRef ? type.GetElementType()! : type;
            unwrapped = Nullable.GetUnderlyingType(unwrapped) ?? unwrapped;
            return unwrapped == typeof(CancellationToken);
        }

        foreach (var type in types)
        {
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);
            Assert.NotEmpty(methods); // non-vacuous: each type really has its public write method

            MethodBase[] members = [.. methods, .. type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)];
            foreach (var parameter in members.SelectMany(m => m.GetParameters()))
            {
                Assert.False(IsCancellationToken(parameter.ParameterType),
                    $"{type.Name}.{parameter.Member.Name} takes a CancellationToken parameter '{parameter.Name}'.");
            }
        }

        var requestProperties = typeof(AiInvocationRecordRequest).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        Assert.NotEmpty(requestProperties);
        foreach (var property in requestProperties)
        {
            Assert.False(IsCancellationToken(property.PropertyType),
                $"{nameof(AiInvocationRecordRequest)}.{property.Name} is CancellationToken-typed.");
        }
    }

    // ── v11 §5 "Cancellation": own bounded timeout ──────────────────────

    // The one test-side margin above IsolatedWriteLimits.WriteTimeout, shared
    // by both isolated-writer timeout tests (this class and
    // UnauthorizedActionAuditWriterTests) — covers Npgsql's post-cancellation
    // work, which the timeout itself does not bound. Tight enough that a 5s
    // internal timeout in either class exceeds WriteTimeout + margin.
    internal static readonly TimeSpan TimeoutTestMargin = TimeSpan.FromMilliseconds(1500);

    [Fact]
    public async Task RecordAsync_BlockedByCompetingUncommittedRow_TimesOutBounded_FailsOpen_NoThrow()
    {
        var businessId = await SeedBusinessAsync("Timeout Business");
        var correlationId = Guid.NewGuid();
        var writeTimeout = IsolatedWriteLimits.WriteTimeout;
        var watchdogAfter = writeTimeout + TimeoutTestMargin;

        // A competing connection holds an uncommitted row with the SAME
        // (BusinessId, CorrelationId, Sequence) — Postgres blocks any second
        // inserter targeting the same unique-index key until this
        // transaction resolves. Scoped to exactly this test's own data; no
        // table-wide lock, so it cannot delay unrelated tests running
        // concurrently.
        await using var blockerScope = _provider.CreateAsyncScope();
        var blockerDb = blockerScope.ServiceProvider.GetRequiredService<AppDbContext>();

        // The watchdog must fire before either of Npgsql's own timeouts could
        // end the blocked insert, or it could not tell the recorder's timeout
        // apart from the framework's.
        var npgsql = new NpgsqlConnectionStringBuilder(_connectionString);
        var commandTimeout = TimeSpan.FromSeconds(blockerDb.Database.GetCommandTimeout() ?? npgsql.CommandTimeout);
        Assert.True(watchdogAfter < commandTimeout, $"Watchdog {watchdogAfter} must be below CommandTimeout {commandTimeout}.");
        Assert.True(watchdogAfter < TimeSpan.FromSeconds(npgsql.Timeout), $"Watchdog {watchdogAfter} must be below connection Timeout {npgsql.Timeout}s.");

        var blockerTx = await blockerDb.Database.BeginTransactionAsync();
        var request = BuildValidRequest(businessId, correlationId, sequence: 0);
        var sw = new Stopwatch();
        Task recordTask;
        Task winner;
        try
        {
            blockerDb.AiProviderInvocations.Add(new AiProviderInvocation
            {
                Id = Guid.NewGuid(),
                BusinessId = businessId,
                Provider = AiProviders.Anthropic,
                Model = AnthropicModels.Default,
                Operation = AiOperations.CustomerReply,
                CorrelationId = correlationId,
                Sequence = 0,
                InputTokens = 1,
                Outcome = AiOutcomes.Succeeded,
                IsUsagePartial = false,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await blockerDb.SaveChangesAsync(); // pending in blockerTx, not yet committed

            sw.Start();
            recordTask = _recorder.RecordAsync(request);

            // A watchdog, not the assertion mechanism itself: if the
            // recorder's own internal timeout is broken (e.g. never wired
            // into the write), recordTask could otherwise stay pending until
            // the blocker is released — this bounds this test's own worst
            // case instead of relying on the very timeout under test.
            winner = await Task.WhenAny(recordTask, Task.Delay(watchdogAfter));
            sw.Stop();
        }
        finally
        {
            // Released unconditionally, whichever branch won or threw, so
            // nothing lingers and a still-pending recordTask can proceed.
            await blockerTx.RollbackAsync();
            await blockerTx.DisposeAsync();
        }

        Exception? ex = null;
        try { await recordTask.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (Exception caught) { ex = caught; }

        // The recorder's own internal timeout resolved this, not the
        // watchdog — proves boundedness is real, not merely eventually
        // released by this test's own cleanup.
        Assert.Same(recordTask, winner);
        Assert.Null(ex); // fails open — never throws to the caller

        // At least the configured timeout (it genuinely fired, not an
        // instantaneous failure) and below the watchdog.
        Assert.True(sw.Elapsed >= writeTimeout,
            $"Expected >= {writeTimeout} (IsolatedWriteLimits.WriteTimeout), was {sw.Elapsed}.");
        Assert.True(sw.Elapsed < watchdogAfter,
            $"Expected below the {watchdogAfter} watchdog, was {sw.Elapsed}.");

        var entry = Assert.Single(_logger.Entries);
        Assert.IsAssignableFrom<OperationCanceledException>(entry.Exception);
        AssertDecision1Diagnostic(entry, request, entry.Exception!.GetType().Name);

        // The recorder's own blocked insert never completed — nothing was
        // left behind once the competing row's own (rolled-back) key
        // vacated the index.
        Assert.Null(await FindRowAsync(correlationId, 0));
    }

    private class FixedTenantAccessor(Guid? businessId) : ICurrentTenantAccessor
    {
        public Guid? BusinessId => businessId;
    }
}
