namespace backend.Tests;

/// <summary>
/// Pure tests of Program.EnsureMinimumPgvectorVersion, the three refusals of
/// the pgvector startup check in backend/Program.cs: no host, no connection,
/// no configuration. Each message is compared whole.
///
/// Not covered here: the SELECT that produces the value, that startup calls
/// the method, and the minimum that startup passes to it. A test host
/// starting covers the passing path only; the live no-row path has been
/// observed by temporary edits only
/// (docs/handoffs/separate-test-database-design-v3-supplement-14.md, errata
/// item 3).
/// </summary>
public class PgvectorStartupCheckTests
{
    private static readonly Version Minimum = new(0, 8, 0);

    [Fact]
    public void NoRow_Throws()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => Program.EnsureMinimumPgvectorVersion(null, Minimum));

        Assert.Equal(
            "No 'vector' extension version was found in this database (extversion: missing). " +
            "pgvector 0.8.0 or later is required.",
            exception.Message);
    }

    [Fact]
    public void UnparsableVersion_Throws()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => Program.EnsureMinimumPgvectorVersion("not.a.version", Minimum));

        Assert.Equal(
            "The 'vector' extension version could not be read (extversion: 'not.a.version'). " +
            "pgvector 0.8.0 or later is required.",
            exception.Message);
    }

    [Fact]
    public void BelowMinimum_Throws()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => Program.EnsureMinimumPgvectorVersion("0.7.9", Minimum));

        Assert.Equal(
            "pgvector 0.7.9 is installed. " +
            "pgvector 0.8.0 or later is required.",
            exception.Message);
    }

    [Theory]
    [InlineData("0.8.0")]
    [InlineData("0.8.6")]
    public void AtOrAboveMinimum_Returns(string extversion)
    {
        Program.EnsureMinimumPgvectorVersion(extversion, Minimum);
    }
}
