using System.Security.Cryptography;
using System.Text;

namespace backend.Services;

/// <summary>
/// Deterministic action-identity hashing for the Step 22 AI Action Safety
/// Model — see docs/handoffs/step-22-ai-action-safety-model-concrete-contract-design-v2.md
/// §13 for the action-identity contract and §5 for the future `Approval`
/// entity's `ActionHash` field this is intended to populate/recompute
/// against. Not yet called from anywhere — no `Approval` entity, proposal,
/// or execution interface exists yet (Step 23D implements this function in
/// isolation, per that pass's own explicitly narrow authorization).
///
/// Deliberately a separate type from <see cref="IdempotencyKeyDeriver"/>,
/// not a second method on it — decided, not defaulted, because the two
/// concepts (action identity, spanning an `Approval`'s entire lifetime, vs.
/// AI-turn idempotency, bounded to one (assistantMessageId, toolUseId)
/// dispatch-loop retry) are explicitly required to stay unconflated (22J
/// §5's "kept distinct" subsection, re-confirmed 22M/22N: approval identity,
/// action identity, execution identity, and AI-turn idempotency are four
/// distinct concepts, never substituted for one another). Colocating them
/// as two methods on a class named `IdempotencyKeyDeriver` would misname
/// half of that class's own contents and risk exactly the kind of
/// conflation this design has repeatedly warned against — the existing
/// precedent of `ToolArgumentCanonicalizer` and `IdempotencyKeyDeriver`
/// already being two separate small classes for two closely-related but
/// conceptually distinct pure-function concerns supports the same choice
/// here, not merging into either.
///
/// Hash algorithm: SHA-256, lowercase hex, length-prefixed concatenation —
/// identical construction to `IdempotencyKeyDeriver.Derive` and the
/// execution-leg identity function described in the design's §12 ("...
/// structurally similar in construction to IdempotencyKeyDeriver.Derive
/// (length-prefixed concatenation, SHA-256)..."). The design's §13 itself
/// does not independently re-state an algorithm for `ActionHash` — this is
/// inferred by consistency with §12's explicit specification for the
/// sibling execution-leg identity concept and with this codebase's only
/// existing precedent for an identity-hash of this shape, not itself an
/// independently-quoted requirement from §13's own text.
/// </summary>
public static class ActionHasher
{
    /// <param name="businessId">Server-derived tenant id — never model-supplied.</param>
    /// <param name="conversationId">The proposal's `ConversationId` — required
    /// non-null as an existing proposal-creation precondition (22A §13), so
    /// no null-handling policy is invented here.</param>
    /// <param name="customerId">The customer identity bound into the action
    /// hash — at proposal time, the newly-resolved value; at execution
    /// time, the freshly re-derived current value (never the value stored
    /// on `Approval`, per §14's re-derivation requirement) — this function
    /// itself is agnostic to which; the caller supplies whichever is
    /// correct for the moment it is invoked.</param>
    /// <param name="toolName">The dispatched tool's own Name — generic, not
    /// hardcoded to `orderCancel`, so this function is directly reusable by
    /// a future `refundOrder`/`updateCustomer` tool without modification.</param>
    /// <param name="resourceId">The tool's own resource identifier (e.g.
    /// `ExternalOrderId` for `orderCancel`) — passed through exactly as
    /// given. The two `ExternalOrderId` normalization sub-questions
    /// (whitespace/empty-string handling, Shopify-GID-equivalence) remain
    /// `UNRESOLVED — repository evidence insufficient` (22H §10/§19,
    /// unchanged) — this function does not invent a normalization rule; any
    /// eventual real caller inherits that open dependency, not this
    /// function.</param>
    /// <param name="canonicalArguments">The tool's own canonicalized
    /// argument representation — for `orderCancel`, the output of the
    /// already-fixed `ToolArgumentCanonicalizer.CanonicalizeOrderCancellationArgs`
    /// (Step 23B/23C), not re-derived or reimplemented here.</param>
    /// <returns>A lowercase hex-encoded SHA-256 digest (64 characters).</returns>
    public static string Derive(
        Guid businessId, Guid conversationId, Guid customerId, string toolName,
        string resourceId, string canonicalArguments)
    {
        // Length-prefixed concatenation, not a delimiter-joined string —
        // identical discipline to IdempotencyKeyDeriver.Derive and
        // ToolArgumentCanonicalizer, for the same reason: this is what
        // makes the encoding unambiguous regardless of what characters any
        // individual component contains, e.g. distinguishing
        // (toolName: "AB", resourceId: "C") from (toolName: "A",
        // resourceId: "BC") is impossible with plain concatenation but
        // always unambiguous here.
        var canonicalInput = string.Concat(
            LengthPrefixed(businessId.ToString("D")),
            LengthPrefixed(conversationId.ToString("D")),
            LengthPrefixed(customerId.ToString("D")),
            LengthPrefixed(toolName),
            LengthPrefixed(resourceId),
            LengthPrefixed(canonicalArguments));

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalInput));
        return Convert.ToHexStringLower(hash);
    }

    private static string LengthPrefixed(string value) => $"{value.Length}:{value}";
}
