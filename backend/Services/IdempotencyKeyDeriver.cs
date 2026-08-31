using System.Security.Cryptography;
using System.Text;

namespace backend.Services;

/// <summary>
/// Deterministic idempotency-key derivation — see the approved design at
/// docs/handoffs/step-19-idempotency-key-derivation-approved.md. Owned by
/// the dispatch layer (Decisions 11/12): OrderCancellationService/
/// CustomerUpdateActionService/etc. never derive their own keys, they only
/// validate and consume whatever key the caller supplies.
///
/// The retry boundary is the (assistantMessageId, toolUseId) pair — the
/// same already-received tool_use block being retried by the dispatch loop
/// reuses the identical key; a fresh model-generated tool_use invocation
/// (a new customer message, or any other new turn) always derives a new
/// key. This is a fixed, approved design decision — not to be changed to
/// "reuse across messages" without a separate, explicit product decision.
/// </summary>
public static class IdempotencyKeyDeriver
{
    /// <param name="businessId">Server-derived tenant id — never model-supplied.</param>
    /// <param name="toolName">The dispatched tool's own Name.</param>
    /// <param name="resourceId">The tool's own resource identifier, from
    /// IWriteDispatchTool.DescribeIdempotencyInputs.</param>
    /// <param name="canonicalArguments">The tool's own canonicalized
    /// argument representation, from IWriteDispatchTool.DescribeIdempotencyInputs
    /// — see ToolArgumentCanonicalizer for the currently known shapes.</param>
    /// <param name="assistantMessageId">The Anthropic assistant Message.ID
    /// that produced the tool_use block being dispatched.</param>
    /// <param name="toolUseId">The ToolUseBlock.ID of the specific tool_use
    /// block being dispatched.</param>
    /// <returns>A lowercase hex-encoded SHA-256 digest (64 characters) —
    /// well within the ≤256-character limit validated by
    /// OrderCancellationService/CustomerUpdateActionService.</returns>
    public static string Derive(
        Guid businessId, string toolName, string resourceId, string canonicalArguments,
        string assistantMessageId, string toolUseId)
    {
        // Length-prefixed concatenation, not a delimiter-joined string:
        // this is what makes the encoding unambiguous regardless of what
        // characters (including a chosen delimiter itself) appear inside
        // any individual component — e.g. distinguishing ("ab","c") from
        // ("a","bc") is impossible with plain delimiter-joining if either
        // component could itself contain the delimiter, but is always
        // unambiguous here.
        var canonicalInput = string.Concat(
            LengthPrefixed(businessId.ToString("D")),
            LengthPrefixed(toolName),
            LengthPrefixed(resourceId),
            LengthPrefixed(canonicalArguments),
            LengthPrefixed(assistantMessageId),
            LengthPrefixed(toolUseId));

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalInput));
        return Convert.ToHexStringLower(hash);
    }

    private static string LengthPrefixed(string value) => $"{value.Length}:{value}";
}
