using Microsoft.Extensions.Configuration;
using Xunit.Abstractions;

namespace backend.Tests;

/// <summary>
/// Pure tests of the test-database resolver: in-memory configuration only, no host, no
/// connection, no file read. Every input is a made-up sentinel value.
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
}
