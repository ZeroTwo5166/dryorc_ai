using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace backend.Tests;

/// <summary>
/// Source scans K0 to K9 for the test-database design. Regression checks against accidental
/// bypass, not a prevention. No database access: they read the test project's *.cs files as
/// whole text (comments count) and, for K9, reflect over the test assembly.
/// </summary>
public class TestDatabaseUsageTests
{
    private sealed record SourceFile(string Path, string Text);

    private sealed record Hit(string File, int Index, string Line, string Text);

    private static readonly Lazy<IReadOnlyList<SourceFile>> Tree = new(ReadTree);

    private static IReadOnlyList<SourceFile> ReadTree()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(System.IO.Path.Combine(folder.FullName, "backend.Tests.csproj")))
        {
            folder = folder.Parent;
        }

        Assert.NotNull(folder);
        var files = new List<SourceFile>();
        foreach (var path in Directory.EnumerateFiles(folder.FullName, "*.cs", SearchOption.AllDirectories))
        {
            var relative = System.IO.Path.GetRelativePath(folder.FullName, path).Replace('\\', '/');
            var segments = relative.Split('/');
            if (segments.Take(segments.Length - 1).Any(s => s is "bin" or "obj"))
            {
                continue;
            }

            files.Add(new SourceFile(relative, File.ReadAllText(path)));
        }

        return files;
    }

    private static string LineOf(string text, int index)
    {
        var start = text.LastIndexOf('\n', Math.Max(index - 1, 0));
        start = index == 0 ? 0 : start + 1;
        var end = text.IndexOf('\n', index);
        if (end < 0)
        {
            end = text.Length;
        }

        var line = text.Substring(start, end - start);
        return line.EndsWith('\r') ? line[..^1] : line;
    }

    private static List<Hit> Scan(string pattern, IEnumerable<SourceFile>? files = null)
    {
        var regex = new Regex(pattern, RegexOptions.CultureInvariant);
        var hits = new List<Hit>();
        foreach (var file in files ?? Tree.Value)
        {
            foreach (Match match in regex.Matches(file.Text))
            {
                hits.Add(new Hit(file.Path, match.Index, LineOf(file.Text, match.Index), file.Text));
            }
        }

        return hits;
    }

    private static Func<List<Hit>, bool> Exactly(int count, Func<Hit, bool>? each = null) =>
        hits => hits.Count == count && (each is null || hits.All(each));

    private static Func<List<Hit>, bool> Any(Func<Hit, bool>? each = null) =>
        hits => each is null || hits.All(each);

    private static void Check(string scan, string pattern, IReadOnlyDictionary<string, Func<List<Hit>, bool>>? exemptions = null,
        IEnumerable<SourceFile>? files = null)
    {
        var failures = new List<string>();
        foreach (var group in Scan(pattern, files).GroupBy(h => h.File))
        {
            var hits = group.ToList();
            if (exemptions is not null && exemptions.TryGetValue(group.Key, out var rule))
            {
                if (!rule(hits))
                {
                    failures.Add($"{scan}: {group.Key}: {hits.Count} match(es) do not meet the exemption, at line(s) " +
                                 string.Join(", ", hits.Select(h => h.Text.Take(h.Index).Count(c => c == '\n') + 1)));
                }
            }
            else
            {
                failures.Add($"{scan}: {group.Key}: {hits.Count} match(es), no exemption, at line(s) " +
                             string.Join(", ", hits.Select(h => h.Text.Take(h.Index).Count(c => c == '\n') + 1)));
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static Dictionary<string, Func<List<Hit>, bool>> Only(params (string File, Func<List<Hit>, bool> Rule)[] rules) =>
        rules.ToDictionary(r => r.File, r => r.Rule);

    [Fact]
    public void K0_ScanSeesTheTree()
    {
        var files = Tree.Value;
        Assert.True(files.Count >= 100, $"the scan found {files.Count} files");
        foreach (var name in new[] { "TestDatabase.cs", "TestDatabaseTests.cs", "TestDatabaseUsageTests.cs", "NivoraApiFactory.cs", "LiveClaudeApiFactory.cs" })
        {
            Assert.Contains(files, f => f.Path == name);
        }
    }

    [Fact]
    public void K1_NoDevelopmentKeyName()
    {
        Check("K1", @"Default[C]onnection", Only(
            ("TestDatabase.cs", Exactly(1, h => h.Line.Contains("HostConnectionKey", StringComparison.Ordinal)))));
    }

    [Fact]
    public void K2_NoConfigurationLookupOfAConnectionString()
    {
        Check("K2", @"GetConnectionString\s*\(\s*[^)\s]");
    }

    [Fact]
    public void K3_NoConnectionSectionName()
    {
        Check("K3", @"Connection[S]trings", Only(
            ("TestDatabase.cs", Exactly(2, h =>
                h.Line.Contains("SecretKey", StringComparison.Ordinal) || h.Line.Contains("HostConnectionKey", StringComparison.Ordinal)))));
    }

    [Fact]
    public void K4_NoUriConversionOutsideTheResolver()
    {
        Check("K4", @"Postgres[C]onnectionString|\bFrom[U]ri\b", Only(("TestDatabase.cs", Any())));
    }

    [Fact]
    public void K5_FactoriesAreOnlyTheThreeRoots()
    {
        Check("K5a", @"\bclass\s+\w+[^{;=]*:[^{;=]*\bWebApplication[F]actory\b", Only(
            ("NivoraApiFactory.cs", Exactly(1)),
            ("LiveClaudeApiFactory.cs", Exactly(1))));
        Check("K5b", @"\bnew\s+WebApplication[F]actory\b|\bWebApplication[F]actory\s*<[^>;]*>\s+\w+\s*=\s*new\s*\(");
        Check("K5c", @"\bclass\s+\w+[^{;=:]*:\s*(?:global::)?(?:[\w.]+\.)?(Nivora[A]piFactory|Live[C]laudeApiFactory|PersistingInvocationRecorder[A]piFactory)\b(?!\s*<)", Only(
            ("PersistingInvocationRecorderApiFactory.cs", Exactly(1))));
    }

    [Fact]
    public void K6_RootFactoriesApplyConfigureHost()
    {
        var regex = new Regex(@"TestDatabase\s*\.\s*ConfigureHost\s*\(\s*builder\s*\)", RegexOptions.CultureInvariant);
        foreach (var name in new[] { "NivoraApiFactory.cs", "LiveClaudeApiFactory.cs" })
        {
            var file = Tree.Value.Single(f => f.Path == name);
            var matches = regex.Matches(file.Text);
            Assert.True(matches.Count == 1, $"K6: {name}: {matches.Count} match(es), expected exactly 1");
            var anchor = file.Text.IndexOf("ConfigureWebHost(", StringComparison.Ordinal);
            Assert.True(anchor >= 0 && matches[0].Index > anchor, $"K6: {name}: the call is not after the text ConfigureWebHost(");
        }
    }

    [Fact]
    public void K7_NoOtherHostOrConfigurationRoute()
    {
        Check("K7a", @"\bWebApplication\s*\.\s*Create\w*\s*\(");
        Check("K7b", @"\bHost\s*\.\s*Create\w*\s*\(");
        Check("K7c", @"\bnew\s+(HostBuilder|WebHostBuilder|HostApplicationBuilder|WebApplicationBuilder|TestServer)\b|\bWebHost\s*\.\s*Create\w*\s*\(|\bTestServer\s*\(");
        Check("K7d", @"\bUse[S]etting\s*\(", Only(
            ("TestDatabase.cs", Exactly(1)),
            ("TestDatabaseTests.cs", Exactly(1))));
        Check("K7e", @"\bConfigure[A]ppConfiguration\b|\bConfigure[H]ostConfiguration\b");
        Check("K7f1", @"\bAddIn[M]emoryCollection\b", Only(("TestDatabaseTests.cs", Exactly(1))));
        Check("K7f2", @"\bAddEnvironment[V]ariables\b|\bAdd[J]sonFile\b|\bAddCommand[L]ine\b|\bAddIni[F]ile\b|\bAddXml[F]ile\b|\bAddKeyPer[F]ile\b");
        Check("K7g", @"\b(Get|Set)Environment[V]ariable\b");
        Check("K7h", @"\bNpgsql[D]ataSource");
    }

    [Fact]
    public void K8_NoLiteralOrRetargetedConnectionString()
    {
        var sentinel = new Regex(@"^postgre[s]ql://(sentinel-user:sentinel-pass@sentinel-host\.invalid|guard:probe@127\.0\.0\.1:1)/[A-Za-z0-9_]*(\?sslmode=[A-Za-z0-9]+)?(?=[""&])",
            RegexOptions.CultureInvariant);
        Check("K8a", @"\bpostgre[s](ql)?://", Only(
            ("TestDatabaseTests.cs", Any(h => sentinel.IsMatch(h.Text.Substring(h.Index))))));

        var placeholder = new Regex(@"^\s*private const string NeverOpenedConnectionString = ""Hos[t]=(127\.0\.0\.1|localhost);Port=1;Databas[e]=never_opened"";\s*$",
            RegexOptions.CultureInvariant);
        Check("K8b", @"""[^""\r\n]*\b(Host|Server|Database|Username|User ?Id|Password)\s*=[^""\r\n]*""", Only(
            ("TestBusinessCleanupModelGuardTests.cs", Exactly(1, h => placeholder.IsMatch(h.Line)))));

        const string builderPattern = @"\bNpgsqlConnectionString[B]uilder\b";
        Check("K8c", builderPattern, Only(
            ("AiInvocationRecorderTests.cs", Exactly(1)),
            ("UnauthorizedActionAuditWriterTests.cs", Exactly(1)),
            ("TestBusinessCleanup.cs", Exactly(1)),
            ("TestDatabase.cs", Any())));

        var builderFiles = Scan(builderPattern).Select(h => h.File).ToHashSet();
        Check("K8d", @"\b(Database|Host|Username|Password|Port|Service|Passfile|Options)\s*=(?![=>])",
            files: Tree.Value.Where(f => builderFiles.Contains(f.Path)));
    }

    [Fact]
    public void K9_FactoryTypesAndHooks()
    {
        var factoryBase = typeof(WebApplicationFactory<Program>);
        var assembly = typeof(TestDatabaseUsageTests).Assembly;

        var roots = assembly.GetTypes()
            .Where(t => !t.IsAbstract && factoryBase.IsAssignableFrom(t))
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(new[] { "LiveClaudeApiFactory", "NivoraApiFactory", "PersistingInvocationRecorderApiFactory" }, roots);

        Assert.True(typeof(LiveClaudeApiFactory).IsSealed);
        Assert.True(typeof(PersistingInvocationRecorderApiFactory).IsSealed);

        var expectedDeclaring = new Dictionary<Type, Type>
        {
            [typeof(NivoraApiFactory)] = typeof(NivoraApiFactory),
            [typeof(PersistingInvocationRecorderApiFactory)] = typeof(NivoraApiFactory),
            [typeof(LiveClaudeApiFactory)] = typeof(LiveClaudeApiFactory),
        };
        const BindingFlags all = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (var (type, declaring) in expectedDeclaring)
        {
            var methods = type.GetMethods(all).Where(m => m.Name == "ConfigureWebHost").ToArray();
            var method = Assert.Single(methods);
            Assert.True(method.DeclaringType == declaring, $"{type.Name}.ConfigureWebHost is declared on {method.DeclaringType?.Name}");
            Assert.True(method.IsFinal, $"{type.Name}.ConfigureWebHost is not final");
        }

        foreach (var name in new[] { "CreateHost", "CreateHostBuilder", "CreateWebHostBuilder", "CreateServer", "GetTestAssemblies" })
        {
            foreach (var type in expectedDeclaring.Keys)
            {
                foreach (var method in type.GetMethods(all).Where(m => m.Name == name))
                {
                    Assert.True(method.DeclaringType == factoryBase, $"{type.Name}.{name} is overridden on {method.DeclaringType?.Name}");
                }
            }
        }
    }
}
