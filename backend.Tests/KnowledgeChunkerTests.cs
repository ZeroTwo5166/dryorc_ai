using backend.Services;
using Xunit;

namespace backend.Tests;

/// <summary>
/// Pure unit tests for KnowledgeChunker — no DB, no DI, no HTTP. Constants
/// under test: TargetChunkSize=800, MaxChunkSize=1200, OverlapSize=150.
/// </summary>
public class KnowledgeChunkerTests
{
    [Fact]
    public void Chunk_EmptyContent_ReturnsNoChunks()
    {
        Assert.Empty(KnowledgeChunker.Chunk(""));
    }

    [Fact]
    public void Chunk_WhitespaceOnlyContent_ReturnsNoChunks()
    {
        Assert.Empty(KnowledgeChunker.Chunk("   \n\n  \t \r\n  "));
    }

    [Fact]
    public void Chunk_ShortSingleParagraph_ReturnsOneChunkContainingIt()
    {
        var chunks = KnowledgeChunker.Chunk("We ship within 2 business days.");
        Assert.Single(chunks);
        Assert.Equal("We ship within 2 business days.", chunks[0]);
    }

    [Fact]
    public void Chunk_Deterministic_SameInputProducesIdenticalChunks()
    {
        var content = string.Join("\n\n", Enumerable.Range(0, 20).Select(i => $"Paragraph {i}: " + new string('x', 50)));
        var first = KnowledgeChunker.Chunk(content);
        var second = KnowledgeChunker.Chunk(content);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Chunk_MultipleShortParagraphs_MergedIntoOneChunkUnderTarget()
    {
        var content = "MARKER_A short paragraph.\n\nMARKER_B another short one.\n\nMARKER_C and a third.";
        var chunks = KnowledgeChunker.Chunk(content);

        Assert.Single(chunks);
        Assert.Contains("MARKER_A", chunks[0]);
        Assert.Contains("MARKER_B", chunks[0]);
        Assert.Contains("MARKER_C", chunks[0]);
    }

    [Fact]
    public void Chunk_ParagraphsExceedingTarget_SplitIntoMultipleChunks_PreservingOrder()
    {
        // Four paragraphs of 300 chars each (marked distinctly): greedy
        // merge fits two per chunk (300 + 2 + 300 = 602 <= 800; a third
        // would be 904 > 800) => exactly 2 chunks, each with 2 paragraphs,
        // and no paragraph split mid-content or reordered.
        string Paragraph(string marker) => marker + new string('a', 300 - marker.Length);
        var p1 = Paragraph("MARKER_1 ");
        var p2 = Paragraph("MARKER_2 ");
        var p3 = Paragraph("MARKER_3 ");
        var p4 = Paragraph("MARKER_4 ");
        var content = string.Join("\n\n", [p1, p2, p3, p4]);

        var chunks = KnowledgeChunker.Chunk(content);

        Assert.Equal(2, chunks.Count);
        Assert.Contains("MARKER_1", chunks[0]);
        Assert.Contains("MARKER_2", chunks[0]);
        Assert.DoesNotContain("MARKER_3", chunks[0]);
        Assert.Contains("MARKER_3", chunks[1]);
        Assert.Contains("MARKER_4", chunks[1]);
        Assert.DoesNotContain("MARKER_1", chunks[1]);
        foreach (var chunk in chunks)
        {
            Assert.True(chunk.Length <= KnowledgeChunker.TargetChunkSize + 10);
        }
    }

    [Fact]
    public void Chunk_NoOverlapBetweenOrdinaryMergedChunkBoundaries()
    {
        string Paragraph(string marker) => marker + new string('b', 300 - marker.Length);
        var content = string.Join("\n\n", new[] { "MARKER_A ", "MARKER_B ", "MARKER_C ", "MARKER_D " }.Select(Paragraph));

        var chunks = KnowledgeChunker.Chunk(content);

        // Every marker appears in exactly one chunk — no paragraph content
        // duplicated across a chunk boundary (overlap is reserved for
        // hard-split oversized paragraphs only, tested separately below).
        foreach (var marker in new[] { "MARKER_A", "MARKER_B", "MARKER_C", "MARKER_D" })
        {
            Assert.Single(chunks, c => c.Contains(marker));
        }
    }

    [Fact]
    public void Chunk_OversizedSingleParagraph_HardSplitsAtWhitespace_NeverMidWord()
    {
        // A single paragraph (no blank lines) of ~4500 chars — well over
        // MaxChunkSize (1200) — made of 500 uniquely-numbered whitespace-
        // separated tokens.
        var content = string.Join(" ", Enumerable.Range(0, 500).Select(i => $"word{i:D4}"));

        var chunks = KnowledgeChunker.Chunk(content);

        Assert.True(chunks.Count > 1, "expected the oversized paragraph to be hard-split into multiple chunks");
        foreach (var chunk in chunks)
        {
            Assert.True(chunk.Length <= KnowledgeChunker.MaxChunkSize, $"chunk of length {chunk.Length} exceeds MaxChunkSize");
            // No token appears truncated: every whitespace-separated piece
            // of this chunk is either a complete "wordNNNN" token or empty.
            foreach (var token in chunk.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                Assert.Matches("^word\\d{4}$", token);
            }
        }
    }

    [Fact]
    public void Chunk_HardSplit_CarriesOverlapIntoNextChunk()
    {
        var content = string.Join(" ", Enumerable.Range(0, 500).Select(i => $"word{i:D4}"));
        var chunks = KnowledgeChunker.Chunk(content);

        Assert.True(chunks.Count >= 2);
        var wordsInFirst = chunks[0].Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var wordsInSecond = chunks[1].Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();

        // Every "wordNNNN" token is unique across the whole source text, so
        // any word appearing in both chunks proves the ~150-char trailing
        // overlap was actually carried forward.
        var overlap = wordsInFirst.Intersect(wordsInSecond).ToList();
        Assert.NotEmpty(overlap);
    }

    [Fact]
    public void Chunk_ZeroBasedSequentialOrder_MatchesSourceOrder()
    {
        var content = string.Join("\n\n", Enumerable.Range(0, 10).Select(i => $"ORDER_MARKER_{i} " + new string('c', 100)));
        var chunks = KnowledgeChunker.Chunk(content);

        var positions = Enumerable.Range(0, 10)
            .Select(i => chunks.ToList().FindIndex(c => c.Contains($"ORDER_MARKER_{i} ")))
            .ToList();

        Assert.All(positions, p => Assert.True(p >= 0));
        Assert.Equal(positions.OrderBy(p => p), positions); // strictly non-decreasing => source order preserved
    }

    [Fact]
    public void Chunk_NormalizesLineEndingsAndCollapsesExcessBlankLines()
    {
        var content = "First paragraph.\r\n\r\n\r\n\r\n\r\nSecond paragraph.";
        var chunks = KnowledgeChunker.Chunk(content);

        Assert.Single(chunks);
        Assert.Contains("First paragraph.", chunks[0]);
        Assert.Contains("Second paragraph.", chunks[0]);
        Assert.DoesNotContain("\r", chunks[0]);
    }
}
