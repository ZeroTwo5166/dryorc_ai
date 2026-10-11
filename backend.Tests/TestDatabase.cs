using System.Data.Common;
using System.Diagnostics;
using backend.Data;
using backend.Interfaces;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace backend.Tests;

/// <summary>
/// The one source of a connection string for test code. It reads the test key from the
/// backend user secrets, refuses any database name other than <see cref="Name"/> before it
/// returns a value, and checks once per process that the applied migration history matches
/// this commit's migrations. It opens no connection until something calls
/// <see cref="ConnectionString"/>, <see cref="ConfigureHost"/> or the uncached check.
/// </summary>
internal static class TestDatabase
{
    public const string Name = "nivora_test";
    public const string SecretKey = "ConnectionStrings:TestConnection";
    public const string HostConnectionKey = "ConnectionStrings:DefaultConnection";

    private static readonly Lazy<Resolved> Resolver = new(
        () => Resolve(new ConfigurationBuilder().AddUserSecrets<Program>().Build()),
        LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<bool> Staleness = new(
        () => CheckSchemaCurrent(Resolver.Value),
        LazyThreadSafetyMode.ExecutionAndPublication);

    public static string ConnectionString => Ready().ConnectionString;

    public static void ConfigureHost(IWebHostBuilder builder)
    {
        var resolved = Ready();
        builder.UseSetting(HostConnectionKey, resolved.Uri);
        builder.ConfigureTestServices(services =>
            services.ConfigureDbContext<AppDbContext>(o => o.AddInterceptors(TestDatabaseConnectionGuard.Instance)));
    }

    public static Resolved Resolve(IConfiguration configuration)
    {
        var raw = configuration[SecretKey];
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new TestDatabaseConfigurationException(
                $"{SecretKey} is not set in the backend user secrets. Tests never fall back to the application's own connection string. Setup: tools/TestDatabase/README.md.");
        }

        string keyword;
        string database;
        try
        {
            keyword = PostgresConnectionString.FromUri(raw);
            database = new NpgsqlConnectionStringBuilder(keyword).Database ?? "";
        }
        catch (Exception)
        {
            // No inner exception: a parse error can carry part of the value.
            throw new TestDatabaseConfigurationException(
                $"{SecretKey} is not a connection URI that the test resolver accepts. The value is not shown.");
        }

        if (!string.Equals(database, Name, StringComparison.Ordinal))
        {
            throw new TestDatabaseConfigurationException(
                $"{SecretKey} names database '{Shown(database)}', not '{Name}'. Tests refuse to connect to any other database.");
        }

        return new Resolved(raw, keyword);
    }

    public static string? DescribeMigrationMismatch(IReadOnlyList<string> applied, IReadOnlyList<string> model)
    {
        var shared = Math.Min(applied.Count, model.Count);
        var position = -1;
        for (var i = 0; i < shared; i++)
        {
            if (!string.Equals(applied[i], model[i], StringComparison.Ordinal))
            {
                position = i;
                break;
            }
        }

        if (position < 0)
        {
            if (applied.Count == model.Count)
            {
                return null;
            }

            position = shared;
        }

        var appliedId = position < applied.Count ? applied[position] : "(none)";
        var modelId = position < model.Count ? model[position] : "(none)";
        return $"{Name}'s migration history does not match this commit's migrations " +
               $"(applied {applied.Count}, model {model.Count}; first difference at position {position}: '{appliedId}' vs '{modelId}'). " +
               $"Rebuild {Name}: dotnet run --project tools/TestDatabase -- rebuild.";
    }

    public static string DatabaseName(string keywordConnectionString) =>
        new NpgsqlConnectionStringBuilder(keywordConnectionString).Database ?? "";

    public static void RequireName(string keywordConnectionString)
    {
        var database = DatabaseName(keywordConnectionString);
        if (!string.Equals(database, Name, StringComparison.Ordinal))
        {
            throw new TestDatabaseConfigurationException(
                $"A connection to database '{Shown(database)}' was refused. Test code connects only to {Name}.");
        }
    }

    public static string KeywordFormOf(string uri) => PostgresConnectionString.FromUri(uri);

    public static TimeSpan CheckSchemaCurrentUncached()
    {
        var stopwatch = Stopwatch.StartNew();
        CheckSchemaCurrent(Resolver.Value);
        stopwatch.Stop();
        return stopwatch.Elapsed;
    }

    internal static bool IsSafeName(string name)
    {
        if (name.Length < 1 || name.Length > 63)
        {
            return false;
        }

        foreach (var c in name)
        {
            var safe = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_';
            if (!safe)
            {
                return false;
            }
        }

        return true;
    }

    private static string Shown(string database) => IsSafeName(database) ? database : "(not shown)";

    // 1. read the secret, 2. check the name, 3. staleness check on the checked value, 4. expose.
    private static Resolved Ready()
    {
        var resolved = Resolver.Value;
        _ = Staleness.Value;
        return resolved;
    }

    private static bool CheckSchemaCurrent(Resolved resolved)
    {
        var checkString = new NpgsqlConnectionStringBuilder(resolved.ConnectionString) { Timeout = 15, CommandTimeout = 15 }.ConnectionString;
        RequireName(checkString);

        var contextOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(checkString, x => x.UseVector())
            .Options;
        List<string> model;
        using (var db = new AppDbContext(contextOptions, new NullTenantAccessor()))
        {
            model = db.Database.GetMigrations().ToList();
        }

        List<string> applied;
        try
        {
            applied = ReadAppliedMigrations(checkString);
        }
        catch (PostgresException ex) when (ex.SqlState == "42P01")
        {
            applied = [];
        }
        catch (Exception ex)
        {
            // Connection errors can carry the host, so only the type and SQLSTATE are reported.
            throw new TestDatabaseStaleException(
                $"The staleness check could not read {Name} ({ex.GetType().Name}, SQLSTATE {(ex as DbException)?.SqlState ?? "none"}).");
        }

        var mismatch = DescribeMigrationMismatch(applied, model);
        if (mismatch is not null)
        {
            throw new TestDatabaseStaleException(mismatch);
        }

        return true;
    }

    private static List<string> ReadAppliedMigrations(string connectionString)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        Execute(connection, "BEGIN TRANSACTION READ ONLY");
        var applied = new List<string>();
        using (var command = new NpgsqlCommand("SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\" ORDER BY \"MigrationId\"", connection))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                applied.Add(reader.GetString(0));
            }
        }

        Execute(connection, "ROLLBACK");
        return applied;
    }

    private static void Execute(NpgsqlConnection connection, string sql)
    {
        using var command = new NpgsqlCommand(sql, connection);
        command.ExecuteNonQuery();
    }

    internal sealed record Resolved(string Uri, string ConnectionString)
    {
        public override string ToString() => "TestDatabase.Resolved (value not shown)";
    }

    private sealed class NullTenantAccessor : ICurrentTenantAccessor
    {
        public Guid? BusinessId => null;
    }
}

internal sealed class TestDatabaseConfigurationException(string message) : Exception(message);

internal sealed class TestDatabaseStaleException(string message) : Exception(message);

/// <summary>Refuses to open any connection whose database name is not the test database.</summary>
internal sealed class TestDatabaseConnectionGuard : DbConnectionInterceptor
{
    public static readonly TestDatabaseConnectionGuard Instance = new();

    public override InterceptionResult ConnectionOpening(
        DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
    {
        TestDatabase.RequireName(connection.ConnectionString);
        return result;
    }

    public override ValueTask<InterceptionResult> ConnectionOpeningAsync(
        DbConnection connection, ConnectionEventData eventData, InterceptionResult result,
        CancellationToken cancellationToken = default)
    {
        TestDatabase.RequireName(connection.ConnectionString);
        return new ValueTask<InterceptionResult>(result);
    }
}
