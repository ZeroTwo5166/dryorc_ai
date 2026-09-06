using System.Globalization;
using backend.Enums;

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
    /// Matches OrderCancellationService's own sameIntent fields exactly:
    /// Restock, Method, Reason (CustomerReason is deliberately excluded —
    /// free-text context, not operation-defining, same as that service's own
    /// sameIntent comparison). ExternalOrderId is deliberately NOT
    /// canonicalized here — it is the action identity's ResourceId, a
    /// separate component from CanonicalArguments (see
    /// IdempotencyKeyDeriver.Derive's own distinct resourceId parameter) —
    /// per docs/handoffs/step-22-ai-action-safety-model-canonicalization-design.md
    /// §7/§20.
    ///
    /// Method's three states (null / OriginalPaymentMethod / StoreCredit)
    /// each get a distinct representation — collapsing null into a boolean
    /// (the prior defect) made "cancel and refund to card" indistinguishable
    /// from "cancel and refund as store credit" or "cancel, no refund".
    /// Reason uses the enum member's own name — never
    /// ShopifyOrderCancellationExecutor's outbound wire-format strings
    /// (ToShopifyReason/ToShopifyRefundMethod), which exist only for the
    /// Shopify call and must not be conflated with this local
    /// representation.
    /// </summary>
    public static string CanonicalizeOrderCancellationArgs(bool restock, RefundMethod? method, OrderCancelReason reason) =>
        string.Concat(
            LengthPrefixed(restock ? "1" : "0"),
            LengthPrefixed(method?.ToString() ?? "null"),
            LengthPrefixed(reason.ToString()));

    /// <summary>
    /// Step 27/28/29 — the exact inverse of <see cref="CanonicalizeOrderCancellationArgs"/>,
    /// added specifically to let <c>IApprovedActionExecutor</c> recover typed
    /// values from <c>Approval.CanonicalArguments</c> at execution time, when
    /// no raw AI input is available (see docs/handoffs/step-27-toolargumentcanonicalizer-decode-gap-design-note.md
    /// for the full derivation and docs/handoffs/step-28-decode-gap-design-note-independent-verification.md
    /// for its independent verification — Option 1 of that note, the only
    /// option approved). A pure function of the input string alone: reverses
    /// the identical length-prefixed encoding <see cref="CanonicalizeOrderCancellationArgs"/>
    /// writes, field by field, so a value produced by that method always
    /// round-trips through this one unchanged.
    ///
    /// Throws <see cref="FormatException"/> on any input that is not a
    /// well-formed encoding of exactly these three fields — a truncated or
    /// malformed length prefix, a trailing/missing field, or a field value
    /// that does not match one of the finitely many values
    /// <see cref="CanonicalizeOrderCancellationArgs"/> could have produced
    /// (an unrecognized restock marker, <see cref="RefundMethod"/> name, or
    /// <see cref="OrderCancelReason"/> name). Never silently returns a
    /// best-guess value for a malformed input — per this class's own
    /// approval-gate purpose, a caller must never act on a value this
    /// function could not prove was exactly what was originally
    /// canonicalized.
    ///
    /// Step 30/31 — the RefundMethod/OrderCancelReason fields are validated
    /// with <see cref="Enum.IsDefined{TEnum}(TEnum)"/> after
    /// <see cref="Enum.TryParse{TEnum}(string, out TEnum)"/> succeeds, not
    /// <c>TryParse</c> alone. <c>TryParse</c> by itself silently accepts
    /// any numeric string — including one with no corresponding member at
    /// all (e.g. "99") — and converts it directly to an enum value with
    /// that underlying number, which this method could never have produced
    /// from a well-formed canonical string (the encoder always writes a
    /// member's own name, never its numeric value). Independently verified
    /// empirically (docs/handoffs/step-30-executor-skeleton-independent-verification.md
    /// §3) before this fix; regression-tested permanently (not just via the
    /// scratch program that first found it) by
    /// DecodeOrderCancellationArgs_OutOfRangeNumericRefundMethodValue_ThrowsFormatException
    /// / ...ReasonValue_ThrowsFormatException.
    /// </summary>
    public static (bool Restock, RefundMethod? Method, OrderCancelReason Reason) DecodeOrderCancellationArgs(
        string canonicalArguments)
    {
        var cursor = 0;
        var restockField = ReadLengthPrefixedField(canonicalArguments, ref cursor);
        var methodField = ReadLengthPrefixedField(canonicalArguments, ref cursor);
        var reasonField = ReadLengthPrefixedField(canonicalArguments, ref cursor);

        if (cursor != canonicalArguments.Length)
        {
            throw new FormatException(
                $"Malformed canonical order-cancellation arguments: {canonicalArguments.Length - cursor} " +
                "trailing character(s) after the three expected fields.");
        }

        var restock = restockField switch
        {
            "1" => true,
            "0" => false,
            _ => throw new FormatException(
                $"Malformed canonical order-cancellation arguments: unrecognized restock field '{restockField}'."),
        };

        RefundMethod? method;
        if (methodField == "null")
        {
            method = null;
        }
        else if (Enum.TryParse<RefundMethod>(methodField, out var parsedMethod) && Enum.IsDefined(parsedMethod))
        {
            method = parsedMethod;
        }
        else
        {
            throw new FormatException(
                $"Malformed canonical order-cancellation arguments: unrecognized RefundMethod field '{methodField}'.");
        }

        if (!Enum.TryParse<OrderCancelReason>(reasonField, out var reason) || !Enum.IsDefined(reason))
        {
            throw new FormatException(
                $"Malformed canonical order-cancellation arguments: unrecognized OrderCancelReason field '{reasonField}'.");
        }

        return (restock, method, reason);
    }

    /// <summary>
    /// Reads one <c>LengthPrefixed</c>-encoded field starting at
    /// <paramref name="cursor"/> and advances it past the field just read.
    /// The sole arbiter of "well-formed" for a single field — every
    /// malformed-input case <see cref="DecodeOrderCancellationArgs"/>'s own
    /// field-value checks do not already cover (a missing ':', a
    /// non-numeric or out-of-range length prefix, a declared length that
    /// overruns the remaining string) is caught here.
    /// </summary>
    private static string ReadLengthPrefixedField(string source, ref int cursor)
    {
        var colonIndex = source.IndexOf(':', cursor);
        if (colonIndex < 0)
        {
            throw new FormatException(
                $"Malformed canonical order-cancellation arguments: expected a ':'-terminated length " +
                $"prefix at position {cursor}.");
        }

        var lengthText = source[cursor..colonIndex];
        if (!int.TryParse(lengthText, NumberStyles.None, CultureInfo.InvariantCulture, out var length))
        {
            throw new FormatException(
                $"Malformed canonical order-cancellation arguments: invalid length prefix " +
                $"'{lengthText}' at position {cursor}.");
        }

        var valueStart = colonIndex + 1;
        if (valueStart + length > source.Length)
        {
            throw new FormatException(
                $"Malformed canonical order-cancellation arguments: declared length {length} at " +
                $"position {cursor} exceeds the remaining string length.");
        }

        var value = source.Substring(valueStart, length);
        cursor = valueStart + length;
        return value;
    }

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
