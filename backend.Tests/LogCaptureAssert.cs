using System.Net;

namespace backend.Tests;

/// <summary>A value no captured entry may hold, with the label its failure names.</summary>
internal sealed record DeniedValue(string Label, string Value, bool AllowShort = false);

/// <summary>
/// Log-capture assertion (D-13, D-5): a failure names the label, the entry
/// index and the entry length only, never the entry text or the value.
/// </summary>
internal static class LogCaptureAssert
{
    private const int MinimumLength = 8;

    /// <summary>
    /// Fails at the first captured entry whose message or exception text holds
    /// a denied value, raw or URL-encoded.
    /// </summary>
    internal static void NoneContain(IReadOnlyList<CapturedLog> entries, params DeniedValue[] denied)
    {
        if (denied.Length == 0)
        {
            throw new ArgumentException("no denied value", nameof(denied));
        }

        foreach (var d in denied)
        {
            if (string.IsNullOrWhiteSpace(d.Value))
            {
                throw new ArgumentException($"{d.Label}: empty or whitespace value");
            }

            if (d.Value.Length < MinimumLength && !d.AllowShort)
            {
                throw new ArgumentException($"{d.Label}: shorter than 8 characters and AllowShort is false");
            }
        }

        foreach (var d in denied)
        {
            var encoded = WebUtility.UrlEncode(d.Value);
            string[] forms = string.Equals(encoded, d.Value, StringComparison.Ordinal) ? [d.Value] : [d.Value, encoded];

            for (var i = 0; i < entries.Count; i++)
            {
                foreach (var form in forms)
                {
                    if (entries[i].Message.Contains(form, StringComparison.Ordinal)
                        || (entries[i].Exception ?? "").Contains(form, StringComparison.Ordinal))
                    {
                        var length = entries[i].Message.Length + (entries[i].Exception?.Length ?? 0);
                        Assert.Fail($"{d.Label} found in captured entry {i} (length {length})");
                    }
                }
            }
        }
    }
}
