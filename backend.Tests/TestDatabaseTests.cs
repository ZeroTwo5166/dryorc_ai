using System.Net.Sockets;
using backend.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit.Abstractions;

namespace backend.Tests;

/// <summary>
/// Tests of the test-database resolver. The resolver, mismatch and name tests are pure:
/// in-memory configuration only, no host, no connection, no file read, every input a made-up
/// sentinel value. The live tests (T4, T5, T6, T8-live) connect to nivora_test only.
/// </summary>
public class TestDatabaseTests(ITestOutputHelper output)
{
    private const string SentinelPrefix = "postgresql://sentinel-user:sentinel-pass@sentinel-host.invalid/";

    private static readonly string[] Sentinels =
        ["sentinel-host.invalid", "sentinel-user", "sentinel-pass", "sentinelmode"];

    private static string UriFor(string? name) =>
        name is null ? SentinelPrefix.TrimEnd('/') : SentinelPrefix + name;

    private static IConfiguration ConfigurationWith(string key, string? value) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })
            .Build();

    private static string ExpectedNotSetMessage() =>
        $"{TestDatabase.SecretKey} is not set in the backend user secrets. Tests never fall back to the application's own connection string. Setup: tools/TestDatabase/README.md.";

    private static string ExpectedWrongNameMessage(string? name) =>
        $"{TestDatabase.SecretKey} names database '{(string.IsNullOrEmpty(name) ? "(not shown)" : name)}', not '{TestDatabase.Name}'. Tests refuse to connect to any other database.";

    [Fact]
    public void Resolve_MissingTestConnection_Throws_NoFallback()
    {
        var configuration = ConfigurationWith(TestDatabase.HostConnectionKey, UriFor("nivora_test"));

        var ex = Assert.Throws<TestDatabaseConfigurationException>(() => TestDatabase.Resolve(configuration));

        Assert.Equal(ExpectedNotSetMessage(), ex.Message);
    }

    public static IEnumerable<object?[]> RefusedNames()
    {
        yield return new object?[] { "neondb" };
        yield return new object?[] { "postgres" };
        yield return new object?[] { "NIVORA_TEST" };
        yield return new object?[] { "nivora_test2" };
        yield return new object?[] { "nivora_tes" };
        yield return new object?[] { "" };
        yield return new object?[] { null };
    }

    [Theory]
    [MemberData(nameof(RefusedNames))]
    public void Resolve_DatabaseName_MustBeExactlyNivoraTest(string? name)
    {
        var configuration = ConfigurationWith(TestDatabase.SecretKey, UriFor(name));

        var ex = Assert.Throws<TestDatabaseConfigurationException>(() => TestDatabase.Resolve(configuration));

        Assert.Equal(ExpectedWrongNameMessage(name), ex.Message);
    }

    [Fact]
    public void Resolve_NivoraTest_IsAccepted()
    {
        var input = UriFor("nivora_test");

        var result = TestDatabase.Resolve(ConfigurationWith(TestDatabase.SecretKey, input));

        Assert.Equal("nivora_test", TestDatabase.DatabaseName(result.ConnectionString));
        Assert.Equal(input, result.Uri);

        var sameForm = TestDatabase.KeywordFormOf(UriFor(null)) == TestDatabase.KeywordFormOf(UriFor(""));
        output.WriteLine($"RV16 (b) keyword form of no-path equals keyword form of empty path: {sameForm}");
    }

    public static IEnumerable<object?[]> MessageCases()
    {
        yield return new object?[] { "missing", null };
        yield return new object?[] { "malformed", "not a uri" };
        yield return new object?[] { "bad sslmode", SentinelPrefix + "nivora_test?sslmode=sentinelmode" };
        yield return new object?[] { "wrong name", UriFor("neondb") };
        yield return new object?[] { "bad-character name", UriFor("bad-name") };
        yield return new object?[] { "over-length name", UriFor(new string('a', 64)) };
    }

    [Theory]
    [MemberData(nameof(MessageCases))]
    public void Resolve_Messages_NeverContainHostUserPasswordOrValue(string caseName, string? raw)
    {
        var configuration = ConfigurationWith(TestDatabase.SecretKey, raw);

        var ex = Assert.Throws<TestDatabaseConfigurationException>(() => TestDatabase.Resolve(configuration));

        var text = ex.ToString();
        foreach (var sentinel in Sentinels)
        {
            Assert.DoesNotContain(sentinel, text, StringComparison.Ordinal);
        }

        Assert.Null(ex.InnerException);
        if (caseName is "bad-character name" or "over-length name")
        {
            Assert.Contains("(not shown)", ex.Message, StringComparison.Ordinal);
        }

        var resolved = TestDatabase.Resolve(ConfigurationWith(TestDatabase.SecretKey, UriFor("nivora_test")));
        foreach (var sentinel in Sentinels)
        {
            Assert.DoesNotContain(sentinel, resolved.ToString(), StringComparison.Ordinal);
        }
    }

    private const string IdA = "20260101000000_A";
    private const string IdB = "20260102000000_B";
    private const string IdC = "20260103000000_C";

    public static IEnumerable<object?[]> MismatchCases()
    {
        yield return new object?[] { "equal", new[] { IdA, IdB, IdC }, new[] { IdA, IdB, IdC }, false };
        yield return new object?[] { "one missing", new[] { IdA, IdB }, new[] { IdA, IdB, IdC }, true };
        yield return new object?[] { "one extra", new[] { IdA, IdB, IdC }, new[] { IdA, IdB }, true };
        yield return new object?[] { "two swapped", new[] { IdA, IdC, IdB }, new[] { IdA, IdB, IdC }, true };
        yield return new object?[] { "empty applied", Array.Empty<string>(), new[] { IdA, IdB, IdC }, true };
    }

    [Theory]
    [MemberData(nameof(MismatchCases))]
    public void DescribeMigrationMismatch(string caseName, string[] applied, string[] model, bool mismatch)
    {
        var text = TestDatabase.DescribeMigrationMismatch(applied, model);

        Assert.True(mismatch == (text is not null), $"case '{caseName}'");
        if (!mismatch)
        {
            return;
        }

        Assert.NotNull(text);
        Assert.Contains("Rebuild nivora_test", text, StringComparison.Ordinal);
        Assert.Contains($"applied {applied.Length}, model {model.Length}", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("nivora_test", true)]
    [InlineData("guard_probe_db", false)]
    [InlineData("NIVORA_TEST", false)]
    [InlineData("", false)]
    public void RequireName_RefusesEverythingButNivoraTest(string name, bool accepted)
    {
        var keywordForm = TestDatabase.KeywordFormOf(UriFor(name));

        if (accepted)
        {
            TestDatabase.RequireName(keywordForm);
            return;
        }

        var ex = Assert.Throws<TestDatabaseConfigurationException>(() => TestDatabase.RequireName(keywordForm));
        var shown = name.Length == 0 ? "(not shown)" : name;
        Assert.Equal($"A connection to database '{shown}' was refused. Test code connects only to nivora_test.", ex.Message);
    }

    private const string GuardSentinel = "postgresql://guard:probe@127.0.0.1:1/guard_probe_db?sslmode=disable";

    // Walks InnerException and every member of an AggregateException, depth first.
    private static T? Find<T>(Exception? exception) where T : Exception
    {
        if (exception is null)
        {
            return null;
        }

        if (exception is T match)
        {
            return match;
        }

        if (exception is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions)
            {
                var found = Find<T>(inner);
                if (found is not null)
                {
                    return found;
                }
            }

            return null;
        }

        return Find<T>(exception.InnerException);
    }

    private static async Task<string> CurrentDatabaseAsync(System.Data.Common.DbConnection connection)
    {
        await using (var begin = connection.CreateCommand())
        {
            begin.CommandText = "BEGIN TRANSACTION READ ONLY";
            await begin.ExecuteNonQueryAsync();
        }

        string name;
        await using (var select = connection.CreateCommand())
        {
            select.CommandText = "SELECT current_database()";
            name = (string)(await select.ExecuteScalarAsync())!;
        }

        await using (var rollback = connection.CreateCommand())
        {
            rollback.CommandText = "ROLLBACK";
            await rollback.ExecuteNonQueryAsync();
        }

        return name;
    }

    [Fact]
    public async Task ConnectionString_ConnectsTo_NivoraTest()
    {
        await using var connection = new NpgsqlConnection(TestDatabase.ConnectionString);
        await connection.OpenAsync();

        Assert.Equal(TestDatabase.Name, await CurrentDatabaseAsync(connection));
    }

    [Theory]
    [InlineData("NivoraApiFactory")]
    [InlineData("PersistingInvocationRecorderApiFactory")]
    [InlineData("WithWebHostBuilder")]
    [InlineData("LiveClaudeApiFactory")]
    public async Task Host_ResolvesTo_NivoraTest(string factoryName)
    {
        WebApplicationFactory<Program> factory = factoryName switch
        {
            "NivoraApiFactory" => new NivoraApiFactory(),
            "PersistingInvocationRecorderApiFactory" => new PersistingInvocationRecorderApiFactory(),
            "WithWebHostBuilder" => new NivoraApiFactory().WithWebHostBuilder(b => b.ConfigureTestServices(_ => { })),
            "LiveClaudeApiFactory" => new LiveClaudeApiFactory(),
            _ => throw new ArgumentOutOfRangeException(nameof(factoryName)),
        };
        using (factory)
        {
            using var scope = factory.Services.CreateScope();
            var options = scope.ServiceProvider.GetRequiredService<DbContextOptions<AppDbContext>>();
            var extension = options.Extensions.OfType<RelationalOptionsExtension>().Single();
            Assert.NotNull(extension.ConnectionString);
            Assert.Equal(TestDatabase.Name, TestDatabase.DatabaseName(extension.ConnectionString!));

            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.OpenConnectionAsync();
            Assert.Equal(TestDatabase.Name, await CurrentDatabaseAsync(db.Database.GetDbConnection()));
        }
    }

    [Fact]
    public void Host_GuardRefuses_AnyOtherDatabase()
    {
        using var root = new NivoraApiFactory();
        using var factory = root.WithWebHostBuilder(b => b.UseSetting(TestDatabase.HostConnectionKey, GuardSentinel));

        var thrown = Record.Exception(() => factory.Services);

        Assert.NotNull(thrown);
        var refusal = Find<TestDatabaseConfigurationException>(thrown);
        Assert.NotNull(refusal);
        Assert.Contains("guard_probe_db", refusal.Message, StringComparison.Ordinal);
        Assert.Null(Find<NpgsqlException>(thrown));
        Assert.Null(Find<SocketException>(thrown));
        output.WriteLine($"RV11 shape: {(ReferenceEquals(thrown, refusal) ? "unwrapped" : "wrapped")}");
    }

    [Fact]
    public void StalenessCheck_PassesOnNivoraTest()
    {
        var elapsed = TestDatabase.CheckSchemaCurrentUncached();

        output.WriteLine($"RV8 staleness check duration: {elapsed.TotalMilliseconds:F0} ms");
    }
}
