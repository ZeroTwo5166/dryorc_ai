namespace backend.Services;

/// <summary>
/// Per-tool, known-shape argument canonicalization for idempotency-key
/// derivation — see docs/handoffs/step-19-idempotency-key-derivation-approved.md
/// §5. Deliberately NOT a generic recursive/deep-JSON canonicalizer: the two
/// currently relevant argument shapes (mirroring
/// OrderCancellationService/CustomerUpdateActionService's own existing
/// operation-defining fields) are flat and fully known today. A generic
/// canonicalizer is deferred, not prohibited — build one only when a future
/// write tool actually needs nested or variable-shape arguments.
///
/// These methods produce a canonical *string for hashing*, not the actual
/// arguments passed to the underlying idempotent operation service — callers
/// must pass the tool's real, unmodified arguments to
/// OrderCancellationService/CustomerUpdateActionService separately.
/// </summary>
public static class ToolArgumentCanonicalizer
{
    /// <summary>
    /// Matches OrderCancellationService's own intent fields exactly:
    /// ExternalOrderId, Restock, Refund (CustomerReason is deliberately
    /// excluded — free-text context, not operation-defining, same as that
    /// service's own sameIntent comparison).
    /// </summary>
    public static string CanonicalizeOrderCancellationArgs(string externalOrderId, bool restock, bool refund) =>
        string.Concat(
            LengthPrefixed(externalOrderId),
            LengthPrefixed(restock ? "1" : "0"),
            LengthPrefixed(refund ? "1" : "0"));

    /// <summary>
    /// Matches CustomerUpdateActionService's own intent field exactly: the
    /// SET of FieldsChanged names (never values — see CustomerUpdateAction's
    /// own PII-discipline doc comment). Sorted and de-duplicated before
    /// hashing so that two field lists the existing service's own
    /// HashSet&lt;string&gt;.SetEquals already treats as the same intent
    /// (different order, or incidental duplicates) always canonicalize
    /// identically — otherwise they could derive different keys and never
    /// reach that comparison at all.
    /// </summary>
    public static string CanonicalizeCustomerUpdateArgs(IReadOnlyList<string> fieldsChanged)
    {
        var canonicalFields = fieldsChanged
            .Distinct(StringComparer.Ordinal)
            .OrderBy(f => f, StringComparer.Ordinal);

        return string.Concat(canonicalFields.Select(LengthPrefixed));
    }

    private static string LengthPrefixed(string value) => $"{value.Length}:{value}";
}
