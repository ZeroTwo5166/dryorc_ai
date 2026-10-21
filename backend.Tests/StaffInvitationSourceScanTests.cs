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
/// Slice 6 adds T-I2.
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
