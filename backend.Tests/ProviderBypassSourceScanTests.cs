using System.Text.RegularExpressions;

namespace backend.Tests;

/// <summary>
/// Step 27 Slice A1, Slice 5 — AT-2, the source-scan half of the provider-bypass
/// rule (design docs/handoffs/billing-usage-a1-slice5-design.md §3; rulings F1,
/// F2, F3, F11). Every *.cs file under backend/ (Migrations, bin and obj
/// excluded) is read, its comments are blanked, and eight tokens are counted
/// per file. A file may match a token only if it is listed for that token,
/// and a listed file must match exactly its count; Program.cs is listed per
/// registration line, never blanket. No host, no database, no package. The
/// signature half is ProviderBypassArchitectureTests (AT-1).
///
/// Name-based, not symbol-based. Not seen: a provider host or path assembled
/// at run time ("v1/" + "embeddings"); a raw client reached through dynamic,
/// reflection by a computed name, or a member of another type that returns it
/// under a different static type; anything in Migrations/; anything in a
/// non-.cs file. A match inside a string literal counts on purpose. Counts
/// are per file, so moving a use within an allowlisted file is not seen.
/// backend.Tests is out of scope.
/// </summary>
public class ProviderBypassSourceScanTests
{
    private sealed record SourceFile(string Path, string Stripped, string EndState);

    private const string CodeState = "code";

    private const string ProgramFile = "Program.cs";

    // Design §3.6: enough to notice a scan that lost most of the tree. 221
    // files at the C5 build.
    private const int FileCountFloor = 200;

    private static readonly string[] ExcludedSegments = ["bin", "obj", "Migrations"];

    private static readonly string[] Anchors =
    [
        ProgramFile,
        "Services/AnthropicInvoker.cs",
        "Services/VoyageEmbeddingInvoker.cs",
        "Services/VoyageEmbeddingClient.cs",
        "Services/IVoyageEmbeddingClient.cs",
    ];

    // Design §3.5 rule 3 (F2: per registration line via exact counts, never
    // blanket). Every line of Program.cs that matches a token must, trimmed,
    // be one of these six. A reformat of one of them fails the test; that is
    // the cost of the pin.
    private static readonly string[] PinnedProgramLines =
    [
        "using Anthropic.Services;",
        "builder.Services.AddSingleton(new AnthropicClient { ApiKey = anthropicApiKey });",
        "builder.Services.AddScoped<IMessageService>(sp => sp.GetRequiredService<AnthropicClient>().Messages);",
        "builder.Services.AddScoped<IMessageService, MissingAnthropicApiKeyMessageService>();",
        "builder.Services.AddHttpClient<IVoyageEmbeddingClient, VoyageEmbeddingClient>(client =>",
        "builder.Services.AddScoped<IVoyageEmbeddingClient, MissingVoyageApiKeyEmbeddingClient>();",
    ];

    private static readonly Lazy<IReadOnlyList<SourceFile>> Tree = new(ReadTree);

    // Design §3.1 steps 1 and 2: walk up from the test binaries to the test
    // project folder, then take its sibling backend/. Never the repo root and
    // never backend.Tests (F3).
    private static string LocateBackend()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "backend.Tests.csproj")))
        {
            folder = folder.Parent;
        }

        Assert.True(folder is not null, $"AT2: no backend.Tests.csproj above {AppContext.BaseDirectory}.");
        Assert.True(folder.Parent is not null, $"AT2: {folder.FullName} has no parent folder.");
        var backend = Path.Combine(folder.Parent.FullName, "backend");
        Assert.True(File.Exists(Path.Combine(backend, "backend.csproj")), $"AT2: backend.csproj not found in {backend}.");
        return backend;
    }

    // Design §3.1 step 3. Paths are relative to the scan root with '/'
    // separators.
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

            var (stripped, endState) = Lexer.Strip(File.ReadAllText(path));
            files.Add(new SourceFile(relative, stripped, endState));
        }

        return files.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
    }

    private static IEnumerable<string> DirectorySegments(string relative)
    {
        var segments = relative.Split('/');
        return segments.Take(segments.Length - 1);
    }

    private static int LineNumber(string text, int index) => text.Take(index).Count(c => c == '\n') + 1;

    private static string LineOf(string text, int index)
    {
        var start = index == 0 ? 0 : text.LastIndexOf('\n', index - 1) + 1;
        var end = text.IndexOf('\n', index);
        if (end < 0)
        {
            end = text.Length;
        }

        return text.Substring(start, end - start);
    }

    // Design §3.2: a single forward pass. Comment characters become spaces;
    // '\r' and '\n' are kept, so offsets and line numbers survive. Everything
    // else, every string literal included, is left as it is. The end state is
    // reported, not thrown: a file that does not end in the code state has
    // desynchronised the lexer and fails the guard fact by name.
    private sealed class Lexer
    {
        private readonly string _text;
        private readonly char[] _kept;
        private string _open = "";

        private Lexer(string text)
        {
            _text = text;
            _kept = text.ToCharArray();
        }

        public static (string Stripped, string EndState) Strip(string text)
        {
            var lexer = new Lexer(text);
            lexer.Code(0, 0);
            return (new string(lexer._kept), lexer._open.Length == 0 ? CodeState : lexer._open);
        }

        private char At(int index) => index < _text.Length ? _text[index] : '\0';

        private int Run(int index, char c)
        {
            var end = index;
            while (end < _text.Length && _text[end] == c)
            {
                end++;
            }

            return end - index;
        }

        private void Blank(int from, int to)
        {
            for (var i = from; i < to; i++)
            {
                if (_kept[i] is not ('\r' or '\n'))
                {
                    _kept[i] = ' ';
                }
            }
        }

        // The innermost construct still open at the end of the text names
        // the end state.
        private int Unterminated(string what)
        {
            if (_open.Length == 0)
            {
                _open = what;
            }

            return _text.Length;
        }

        // Code, at the top level (holeClose 0) or inside an interpolation
        // hole that a run of holeClose '}' closes at bracket depth 0. Returns
        // the index after the hole, or the text length.
        private int Code(int i, int holeClose)
        {
            var depth = 0;
            while (i < _text.Length)
            {
                var c = _text[i];
                if (c == '/' && At(i + 1) == '/')
                {
                    var end = i;
                    while (end < _text.Length && _text[end] is not ('\r' or '\n'))
                    {
                        end++;
                    }

                    Blank(i, end);
                    i = end;
                }
                else if (c == '/' && At(i + 1) == '*')
                {
                    var close = _text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    if (close < 0)
                    {
                        Blank(i, _text.Length);
                        return Unterminated("block comment");
                    }

                    Blank(i, close + 2);
                    i = close + 2;
                }
                else if (c == '\'')
                {
                    i = CharLiteral(i);
                }
                else if (c == '@' && At(i + 1) == '"')
                {
                    i = Quoted(i + 1, verbatim: true, interpolated: false);
                }
                else if ((c == '@' && At(i + 1) == '$' && At(i + 2) == '"') || (c == '$' && At(i + 1) == '@' && At(i + 2) == '"'))
                {
                    i = Quoted(i + 2, verbatim: true, interpolated: true);
                }
                else if (c == '$' && At(i + Run(i, '$')) == '"' && Run(i + Run(i, '$'), '"') >= 3)
                {
                    i = Raw(i + Run(i, '$'), Run(i, '$'));
                }
                else if (c == '$' && At(i + 1) == '"')
                {
                    i = Quoted(i + 1, verbatim: false, interpolated: true);
                }
                else if (c == '"')
                {
                    i = Run(i, '"') >= 3 ? Raw(i, 0) : Quoted(i, verbatim: false, interpolated: false);
                }
                else
                {
                    if (c == '{')
                    {
                        depth++;
                    }
                    else if (c == '}')
                    {
                        if (depth > 0)
                        {
                            depth--;
                        }
                        else if (holeClose > 0 && Run(i, '}') >= holeClose)
                        {
                            return i + holeClose;
                        }
                    }

                    i++;
                }
            }

            return holeClose > 0 ? Unterminated("interpolation hole") : i;
        }

        // 'x', '"' and '\'' — a quote character inside must not open a string.
        private int CharLiteral(int open)
        {
            var i = open + 1;
            while (i < _text.Length && _text[i] != '\'')
            {
                i += _text[i] == '\\' ? 2 : 1;
            }

            return i < _text.Length ? i + 1 : Unterminated("char literal");
        }

        // A regular, verbatim or interpolated string, from its opening quote.
        // Regular: a backslash skips the next character. Verbatim: "" is an
        // escaped quote and a backslash escapes nothing. Interpolated: {{ and
        // }} are text, a single { opens a hole lexed as code.
        private int Quoted(int quote, bool verbatim, bool interpolated)
        {
            var i = quote + 1;
            while (i < _text.Length)
            {
                var c = _text[i];
                if (c == '"')
                {
                    if (verbatim && At(i + 1) == '"')
                    {
                        i += 2;
                        continue;
                    }

                    return i + 1;
                }

                if (c == '\\' && !verbatim)
                {
                    i += 2;
                }
                else if (interpolated && c == '{')
                {
                    i = At(i + 1) == '{' ? i + 2 : Code(i + 1, 1);
                }
                else if (interpolated && c == '}' && At(i + 1) == '}')
                {
                    i += 2;
                }
                else
                {
                    i++;
                }
            }

            return Unterminated(verbatim ? "verbatim string" : interpolated ? "interpolated string" : "regular string");
        }

        // A raw string: three or more quotes, closed by the same number. With
        // N leading '$', a run of exactly N '{' opens a hole.
        private int Raw(int firstQuote, int dollars)
        {
            var quotes = Run(firstQuote, '"');
            var i = firstQuote + quotes;
            while (i < _text.Length)
            {
                if (_text[i] == '"')
                {
                    var run = Run(i, '"');
                    if (run >= quotes)
                    {
                        return i + run;
                    }

                    i += run;
                }
                else if (dollars > 0 && _text[i] == '{')
                {
                    var run = Run(i, '{');
                    i = run == dollars ? Code(i + run, dollars) : i + run;
                }
                else
                {
                    i++;
                }
            }

            return Unterminated("raw string");
        }
    }

    // Design §3.5 rules 1 to 3, for one token over the stripped text of every
    // inventoried file. Unknown file with a match: fails. Listed file with
    // more or fewer matches than its count, zero included: fails. A matching
    // line of Program.cs that is not one of the six pinned lines: fails.
    private static void CheckToken(string token, string pattern, params (string File, int Count)[] allowed)
    {
        var regex = new Regex(pattern, RegexOptions.CultureInvariant);
        var expected = allowed.ToDictionary(a => a.File, a => a.Count);
        var failures = new List<string>();
        foreach (var file in Tree.Value)
        {
            var matches = regex.Matches(file.Stripped).Select(m => m.Index).ToList();
            var listed = expected.TryGetValue(file.Path, out var count);
            if (matches.Count == 0 && !listed)
            {
                continue;
            }

            var lines = string.Join(", ", matches.Select(index => LineNumber(file.Stripped, index)));
            if (!listed)
            {
                failures.Add($"{token}: {file.Path}: {matches.Count} match(es), file not allowed, at line(s) {lines}");
            }
            else if (matches.Count != count)
            {
                failures.Add($"{token}: {file.Path}: {matches.Count} match(es), expected exactly {count}, at line(s) {lines}");
            }

            if (file.Path == ProgramFile)
            {
                foreach (var index in matches)
                {
                    var line = LineOf(file.Stripped, index).Trim();
                    if (!PinnedProgramLines.Contains(line))
                    {
                        failures.Add($"{token}: {ProgramFile}:{LineNumber(file.Stripped, index)}: not a pinned registration line: {line}");
                    }
                }
            }
        }

        foreach (var file in expected.Keys.Where(f => Tree.Value.All(t => t.Path != f)))
        {
            failures.Add($"{token}: {file}: listed, but not in the scanned tree");
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    // Guard: the scan sees what it must see, conditions in the design's order
    // (§3.6). The anchors come before the file floor so that the mechanism
    // mutant G2 (the scan rooted at backend/Services, design §7.4) fails at
    // the Program.cs anchor: backend/Services alone holds more than half the
    // floor, and a floor checked first would not be the named assertion.
    [Fact]
    public void AT2_ScanSeesTheBackendTree()
    {
        // backend.csproj found: asserted inside, against backend/ itself.
        LocateBackend();

        var files = Tree.Value;
        foreach (var anchor in Anchors)
        {
            Assert.True(files.Any(f => f.Path == anchor),
                $"AT2 guard: {anchor} is absent from the scanned tree — the scan is not rooted at backend/.");
        }

        Assert.True(files.Count >= FileCountFloor,
            $"AT2 guard: the scan found {files.Count} files, below the floor of {FileCountFloor}.");

        var excluded = files.Where(f => DirectorySegments(f.Path).Any(s => ExcludedSegments.Contains(s))).Select(f => f.Path).ToList();
        Assert.True(excluded.Count == 0,
            "AT2 guard: scanned a path with a bin, obj or Migrations segment: " + string.Join(", ", excluded));

        var open = files.Where(f => f.EndState != CodeState).Select(f => $"{f.Path} ({f.EndState})").ToList();
        Assert.True(open.Count == 0,
            "AT2 guard: the lexer did not end in the code state for: " + string.Join(", ", open));
    }

    // The tree barely exercises block comments and verbatim strings, so the
    // stripper has its own table (design §3.6). The third row is the only one
    // on which a lexer without verbatim state returns different text — it
    // reads \" as an escaped quote inside one regular string and blanks
    // nothing — and is the row the mechanism mutant G3 targets (design §7.4).
    [Fact]
    public void AT2_CommentStripper_KeepsLiteralsAndDropsComments()
    {
        static string Blanks(int count) => new(' ', count);

        (string Input, string Expected)[] rows =
        [
            // A regular string containing "//", then a comment.
            ("\"http://x\" // c", "\"http://x\" " + Blanks(4)),

            // Verbatim: "" is an escaped quote, both "//" stay inside the string.
            ("@\"a \"\"//\"\" b\"", "@\"a \"\"//\"\" b\""),

            // Verbatim: a backslash escapes nothing, so the string closes at
            // the quote after it; the space before the comment is kept and
            // the five comment characters are blanked.
            ("@\"a \\\" // b\"\n", "@\"a \\\"" + Blanks(6) + "\n"),

            // Interpolated: text after a hole is still string text.
            ("$\"{x} // kept\"", "$\"{x} // kept\""),

            // A string nested inside an interpolation hole.
            ("$\"{(a ? \"//\" : \"b\")}\"", "$\"{(a ? \"//\" : \"b\")}\""),

            // A raw string containing both comment openers.
            ("\"\"\"a // b /* c\"\"\"", "\"\"\"a // b /* c\"\"\""),

            // A raw string with two '$': {{hole}} is a hole, the rest is text.
            ("$$\"\"\"a // {{hole}} /* b\"\"\"", "$$\"\"\"a // {{hole}} /* b\"\"\""),

            // A quote character in a char literal does not open a string.
            ("'\"' // c", "'\"' " + Blanks(4)),

            // An escaped apostrophe in a char literal.
            ("'\\''", "'\\''"),

            // A block comment containing a quote.
            ("/* \" */ x", Blanks(7) + " x"),

            // A line comment containing a quote ends at the line break.
            ("// \"\nvar s = \"a\";", Blanks(4) + "\nvar s = \"a\";"),
        ];

        var failures = new List<string>();
        foreach (var (input, expected) in rows)
        {
            var (stripped, _) = Lexer.Strip(input);
            if (stripped != expected)
            {
                failures.Add($"AT2 stripper: input [{input}] expected [{expected}] but was [{stripped}]");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    // Token facts (design §3.3 tokens, §3.4 counts after F11 and F18). One
    // fact per token, so a mutant goes red at the fact named for the rule it
    // breaks. Three files are listed because they declare or implement a
    // scanned name rather than use a raw client: IVoyageEmbeddingClient.cs,
    // IAiInvocationRecorder.cs and AiInvocationRecorder.cs.

    // Mutation (design §7.2 S1): typeof(Anthropic.AnthropicClient) in a
    // service is a match in a file that is not listed.
    [Fact]
    public void AT2_T1_AnthropicClient() =>
        CheckToken("T1", @"\bAnthropicClient\b",
            (ProgramFile, 2));

    // The boundary keeps IMessageServiceWithRawResponse out.
    [Fact]
    public void AT2_T2_IMessageService() =>
        CheckToken("T2", @"\bIMessageService\b",
            (ProgramFile, 2),
            ("Services/AnthropicInvoker.cs", 1),
            ("Services/MissingAnthropicApiKeyMessageService.cs", 2));

    [Fact]
    public void AT2_T3_IVoyageEmbeddingClient() =>
        CheckToken("T3", @"\bIVoyageEmbeddingClient\b",
            (ProgramFile, 2),
            ("Services/IVoyageEmbeddingClient.cs", 1),
            ("Services/VoyageEmbeddingClient.cs", 1),
            ("Services/VoyageEmbeddingInvoker.cs", 1),
            ("Services/MissingVoyageApiKeyEmbeddingClient.cs", 1));

    // The boundary keeps IVoyageEmbeddingClient out. Mutation (design §7.2
    // S4): a second registration in Program.cs is one match too many and a
    // line that is not pinned.
    [Fact]
    public void AT2_T4_VoyageEmbeddingClient() =>
        CheckToken("T4", @"\bVoyageEmbeddingClient\b",
            (ProgramFile, 1),
            ("Services/VoyageEmbeddingClient.cs", 1));

    // The name, not the name followed by '(': a method-group conversion and
    // nameof are matches too.
    [Fact]
    public void AT2_T5_EmbedWithUsageAsync() =>
        CheckToken("T5", @"\bEmbedWithUsageAsync\b",
            ("Services/IVoyageEmbeddingClient.cs", 1),
            ("Services/VoyageEmbeddingClient.cs", 1),
            ("Services/VoyageEmbeddingInvoker.cs", 2),
            ("Services/MissingVoyageApiKeyEmbeddingClient.cs", 1));

    // The namespace anywhere, not only after using: a fully qualified name
    // and a using alias are matches too.
    [Fact]
    public void AT2_T6_AnthropicServices() =>
        CheckToken("T6", @"\bAnthropic\s*\.\s*Services\b",
            (ProgramFile, 1),
            ("Services/AnthropicInvoker.cs", 1),
            ("Services/MissingAnthropicApiKeyMessageService.cs", 2));

    // A string literal on purpose: the path is what identifies the call.
    [Fact]
    public void AT2_T7_VoyageEndpoint() =>
        CheckToken("T7", @"v1/embeddings",
            ("Services/VoyageEmbeddingClient.cs", 1));

    // One recorder call per wrapper; the other three matches in each are the
    // wrapper's private helper. Mutation (design §7.2 S8): a duplicated call
    // is a fifth match.
    [Fact]
    public void AT2_T8_RecordAsync() =>
        CheckToken("T8", @"\bRecordAsync\b",
            ("Services/IAiInvocationRecorder.cs", 1),
            ("Services/AiInvocationRecorder.cs", 1),
            ("Services/AnthropicInvoker.cs", 4),
            ("Services/VoyageEmbeddingInvoker.cs", 4));
}
