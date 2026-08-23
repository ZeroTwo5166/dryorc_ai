using System.Text.RegularExpressions;

namespace backend.Services;

/// <summary>
/// Deterministic, pure MVP chunker for KnowledgeDocument.Content — no DI, no
/// external calls; the same input always produces the same output. Splits on
/// paragraph boundaries, greedily merges consecutive paragraphs up to
/// TargetChunkSize, and hard-splits any single paragraph that alone exceeds
/// MaxChunkSize at a whitespace boundary, carrying OverlapSize characters of
/// trailing context into the next sub-chunk. Character-based, not
/// token-based — no tokenizer dependency for MVP. Deliberately simple: no
/// semantic chunking, no heading detection (a short line followed by a blank
/// line already becomes its own paragraph naturally).
/// </summary>
public static class KnowledgeChunker
{
    public const int TargetChunkSize = 800;
    public const int MaxChunkSize = 1200;
    public const int OverlapSize = 150;

    private static readonly Regex ExcessBlankLines = new("\n{3,}", RegexOptions.Compiled);

    public static IReadOnlyList<string> Chunk(string content)
    {
        var normalized = Normalize(content);
        if (normalized.Length == 0)
        {
            return [];
        }

        var paragraphs = SplitParagraphs(normalized);
        var chunks = new List<string>();
        var buffer = new List<string>();
        var bufferLength = 0;

        void FlushBuffer()
        {
            if (buffer.Count == 0)
            {
                return;
            }
            chunks.Add(string.Join("\n\n", buffer));
            buffer.Clear();
            bufferLength = 0;
        }

        foreach (var paragraph in paragraphs)
        {
            if (paragraph.Length > MaxChunkSize)
            {
                // Oversized single paragraph: flush whatever's buffered so
                // far first, then hard-split this paragraph on its own —
                // overlap only ever applies here, never at an ordinary
                // paragraph-merge boundary.
                FlushBuffer();
                chunks.AddRange(HardSplit(paragraph));
                continue;
            }

            var separatorLength = buffer.Count > 0 ? 2 : 0; // "\n\n" join, only once buffer is non-empty
            if (buffer.Count > 0 && bufferLength + separatorLength + paragraph.Length > TargetChunkSize)
            {
                FlushBuffer();
                separatorLength = 0;
            }

            buffer.Add(paragraph);
            bufferLength += separatorLength + paragraph.Length;
        }

        FlushBuffer();
        return chunks;
    }

    private static string Normalize(string content)
    {
        var normalized = content.Replace("\r\n", "\n").Replace("\r", "\n").Trim();
        // Collapse 3+ consecutive newlines (2+ blank lines) down to exactly
        // one blank line — a single paragraph separator.
        return ExcessBlankLines.Replace(normalized, "\n\n");
    }

    private static List<string> SplitParagraphs(string normalized) =>
        normalized
            .Split("\n\n", StringSplitOptions.None)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();

    // Hard-splits a single paragraph that alone exceeds MaxChunkSize into
    // TargetChunkSize-ish pieces, splitting only at whitespace (never
    // mid-word unless no whitespace exists to split on at all) and carrying
    // OverlapSize characters of trailing context from each piece into the
    // start of the next, so a chunk near a hard-split boundary still has
    // surrounding context.
    private static List<string> HardSplit(string paragraph)
    {
        var pieces = new List<string>();
        var start = 0;

        while (start < paragraph.Length)
        {
            var remaining = paragraph.Length - start;
            if (remaining <= MaxChunkSize)
            {
                pieces.Add(paragraph[start..].Trim());
                break;
            }

            var cut = start + TargetChunkSize;
            var splitAt = FindWhitespaceBoundary(paragraph, cut, start);
            pieces.Add(paragraph[start..splitAt].Trim());

            // The next piece starts ~OverlapSize characters back from where
            // this one ended, carrying trailing context forward — but that
            // raw offset is then snapped forward to the next whole-word
            // start (never mid-word), bounded so it can't walk past splitAt.
            // Math.Max(..., start + 1) guarantees forward progress even in
            // the degenerate case where no word boundary exists in the
            // overlap window, so this loop can never spin indefinitely.
            var overlapStart = Math.Max(splitAt - OverlapSize, start);
            var nextStart = FindWordStartAtOrAfter(paragraph, overlapStart, splitAt);
            start = Math.Max(nextStart, start + 1);
        }

        return pieces;
    }

    // Walks backward from `cut` to the nearest preceding whitespace so a
    // split never lands mid-word; falls back to the exact cut point only if
    // no whitespace exists anywhere between `lowerBound` and `cut` (an
    // unbroken run of non-whitespace longer than the target size, e.g. a
    // long URL).
    private static int FindWhitespaceBoundary(string text, int cut, int lowerBound)
    {
        var searchFrom = Math.Min(cut, text.Length - 1);
        for (var i = searchFrom; i > lowerBound; i--)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                return i;
            }
        }
        return Math.Min(cut, text.Length);
    }

    // Scans forward from `from` to the nearest position that begins a whole
    // word (index 0, or immediately after a whitespace character) — used so
    // the overlap carried into the next hard-split piece never starts
    // mid-word. Bounded by `upperBound` (the previous piece's own end) so it
    // can never walk past where that piece stopped; if no word boundary
    // exists in that window at all, returns `upperBound` (degenerate
    // zero-overlap fallback, acceptable for MVP).
    private static int FindWordStartAtOrAfter(string text, int from, int upperBound)
    {
        var i = Math.Min(from, upperBound);
        while (i < upperBound && !(i == 0 || char.IsWhiteSpace(text[i - 1])))
        {
            i++;
        }
        return i;
    }
}
