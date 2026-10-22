namespace backend.Tests;

/// <summary>
/// Staff invite source scans over backend/*.cs (Migrations, bin and obj
/// excluded), no host and no database. The scope is every file whose path or
/// raw text contains "StaffInvitation" or "StaffInvite" (design Section T,
/// T-I2 scope), so the options, the switch and the filter are covered too.
/// Comments are blanked by the same lexer AT-2 uses
/// (ProviderBypassSourceScanTests.Lexer, implementation design G-3); string
/// literals are kept.
///
/// Slice 5b: T-D5b (D5: the invitation code never calls NormalizeEmail).
/// Slice 6: T-I2 (I2: exactly two IgnoreQueryFilters sites for invitations).
/// </summary>
public class StaffInvitationSourceScanTests
{
    private sealed record SourceFile(string Path, string Raw, string Stripped, string EndState);

    private const string CodeState = "code";

    private static readonly string[] ExcludedSegments = ["bin", "obj", "Migrations"];

    private static readonly string[] ScopeMarkers = ["StaffInvitation", "StaffInvite"];

    private static readonly Lazy<IReadOnlyList<SourceFile>> Tree = new(ReadTree);

    // T-D5b
    [Fact]
    public void D5_NoNormalizeEmailInInvitationCode()
    {
        var scope = Scope();

        // Guard against a vacuous pass: the scope is the invitation code.
        Assert.NotEmpty(scope);
        foreach (var expected in new[] { "Services/StaffInvitationService.cs", "Services/StaffInviteOptions.cs", "Auth/StaffInvitesEnabledFilter.cs" })
        {
            Assert.Contains(scope, f => f.Path == expected);
        }

        Assert.All(scope, f => Assert.True(f.EndState == CodeState, $"{f.Path}: the lexer ended in state {f.EndState}"));

        var offenders = scope.Where(f => f.Stripped.Contains("NormalizeEmail", StringComparison.Ordinal)).Select(f => f.Path).ToList();
        Assert.True(offenders.Count == 0, "NormalizeEmail in invitation code: " + string.Join(", ", offenders));
    }

    // T-I2 (DEC I2, D15 as amended by E1; design Section T). All text checks
    // run on the comment-stripped text, so doc comments that name
    // IgnoreQueryFilters do not count.
    [Fact]
    public void I2_ExactlyTwoIgnoreQueryFiltersSites()
    {
        const string ServiceFile = "Services/StaffInvitationService.cs";
        const string CleanupFile = "Services/StaffInvitationCleanupService.cs";
        const string Bypass = ".IgnoreQueryFilters(";

        var scope = Scope();
        Assert.All(scope, f => Assert.True(f.EndState == CodeState, $"{f.Path}: the lexer ended in state {f.EndState}"));
        var service = Assert.Single(scope, f => f.Path == ServiceFile);
        var cleanup = Assert.Single(scope, f => f.Path == CleanupFile);
        Assert.Contains(scope, f => f.Path == "Auth/StaffInvitesEnabledFilter.cs");

        // Site 1: exactly once in the service, inside LoadByTokenHashAsync's body.
        Assert.Equal(1, Occurrences(service.Stripped, Bypass));
        var (bodyStart, bodyEnd) = MethodBody(service.Stripped, "LoadByTokenHashAsync(byte[]");
        var siteIndex = service.Stripped.IndexOf(Bypass, StringComparison.Ordinal);
        Assert.True(siteIndex > bodyStart && siteIndex < bodyEnd, "the service's IgnoreQueryFilters call is not inside LoadByTokenHashAsync");

        // Site 2: exactly once in the cleanup service.
        Assert.Equal(1, Occurrences(cleanup.Stripped, Bypass));

        // Nowhere else in scope.
        var elsewhere = scope
            .Where(f => f.Path != ServiceFile && f.Path != CleanupFile && f.Stripped.Contains(Bypass, StringComparison.Ordinal))
            .Select(f => f.Path)
            .ToList();
        Assert.True(elsewhere.Count == 0, ".IgnoreQueryFilters( elsewhere in staff-invite scope: " + string.Join(", ", elsewhere));

        // Any backend file that names the StaffInvitations set and bypasses the filter is one of the two.
        var bypassingInvitationFiles = Tree.Value
            .Where(f => f.Stripped.Contains("StaffInvitations", StringComparison.Ordinal) && f.Stripped.Contains(Bypass, StringComparison.Ordinal))
            .Select(f => f.Path)
            .Where(p => p != ServiceFile && p != CleanupFile)
            .ToList();
        Assert.True(bypassingInvitationFiles.Count == 0, "StaffInvitations with .IgnoreQueryFilters( outside the two sites: " + string.Join(", ", bypassingInvitationFiles));

        // The cleanup's shape: one set-based DELETE, the D9 predicate, 30 days, no tenant input.
        Assert.Equal(1, Occurrences(cleanup.Stripped, "ExecuteDeleteAsync"));
        Assert.Contains("i.ExpiresAt <= cutoff", cleanup.Stripped, StringComparison.Ordinal);
        Assert.Contains("RetentionAfterExpiry = TimeSpan.FromDays(30)", cleanup.Stripped, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"\bBusinessId\b", cleanup.Stripped);
    }

    private static int Occurrences(string text, string token)
    {
        var count = 0;
        for (var i = text.IndexOf(token, StringComparison.Ordinal); i >= 0; i = text.IndexOf(token, i + token.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    /// <summary>The index of the opening and closing brace of the method body
    /// that follows the single occurrence of <paramref name="signature"/>.
    /// Brace counting over stripped text: comments are gone, and the braces
    /// left inside interpolated strings come in pairs.</summary>
    private static (int Start, int End) MethodBody(string stripped, string signature)
    {
        Assert.Equal(1, Occurrences(stripped, signature));
        var start = stripped.IndexOf('{', stripped.IndexOf(signature, StringComparison.Ordinal));
        Assert.True(start >= 0, $"no body after {signature}");
        var depth = 0;
        for (var i = start; i < stripped.Length; i++)
        {
            if (stripped[i] == '{')
            {
                depth++;
            }
            else if (stripped[i] == '}' && --depth == 0)
            {
                return (start, i);
            }
        }

        Assert.Fail($"unbalanced braces after {signature}");
        return default;
    }

    private static List<SourceFile> Scope() =>
        Tree.Value
            .Where(f => ScopeMarkers.Any(m => f.Path.Contains(m, StringComparison.Ordinal) || f.Raw.Contains(m, StringComparison.Ordinal)))
            .ToList();

    // Copied from ProviderBypassSourceScanTests.LocateBackend: walk up from
    // the test binaries to the test project folder, then take its sibling
    // backend/.
    private static string LocateBackend()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "backend.Tests.csproj")))
        {
            folder = folder.Parent;
        }

        Assert.True(folder is not null, $"no backend.Tests.csproj above {AppContext.BaseDirectory}.");
        Assert.True(folder.Parent is not null, $"{folder.FullName} has no parent folder.");
        var backend = Path.Combine(folder.Parent.FullName, "backend");
        Assert.True(File.Exists(Path.Combine(backend, "backend.csproj")), $"backend.csproj not found in {backend}.");
        return backend;
    }

    // Copied from ProviderBypassSourceScanTests.ReadTree; also keeps the raw
    // text, which decides the scope. Paths are relative with '/' separators.
    private static IReadOnlyList<SourceFile> ReadTree()
    {
        var root = LocateBackend();
        var files = new List<SourceFile>();
        foreach (var path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (DirectorySegments(relative).Any(s => ExcludedSegments.Contains(s)))
            {
                continue;
            }

            var raw = File.ReadAllText(path);
            var (stripped, endState) = ProviderBypassSourceScanTests.Lexer.Strip(raw);
            files.Add(new SourceFile(relative, raw, stripped, endState));
        }

        return files.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
    }

    private static IEnumerable<string> DirectorySegments(string relative)
    {
        var segments = relative.Split('/');
        return segments.Take(segments.Length - 1);
    }
}
