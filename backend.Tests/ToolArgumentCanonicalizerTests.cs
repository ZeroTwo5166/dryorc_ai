using backend.Enums;
using backend.Services;
using Xunit;

namespace backend.Tests;

/// <summary>
/// Pure unit tests — no DB, no HTTP. Covers only the two currently known
/// argument shapes (OrderCancellation, CustomerUpdateAction) per
/// docs/handoffs/step-19-idempotency-key-derivation-approved.md §5 — no
/// generic canonicalizer exists to test.
///
/// The OrderCancellation-argument tests below are regression tests for the
/// specific collision defect described in
/// docs/handoffs/step-22-ai-action-safety-model-canonicalization-design.md
/// §6: the pre-fix `CanonicalizeOrderCancellationArgs(string externalOrderId,
/// bool restock, bool refund)` collapsed `RefundMethod?` into a single
/// `bool refund`, so `OriginalPaymentMethod` and `StoreCredit` both
/// canonicalized as `refund: true` and produced an *identical* string
/// (Example A), and had no parameter for `Reason` at all, so every
/// `OrderCancelReason` value produced an identical string regardless
/// (Example B). Hand-verified against the exact pre-fix implementation
/// (`LengthPrefixed(externalOrderId) + LengthPrefixed(restock) +
/// LengthPrefixed(refund)`): for a fixed `externalOrderId`/`restock`,
/// `Method = OriginalPaymentMethod` and `Method = StoreCredit` both mapped
/// to `refund = true` and produced the byte-identical canonical string
/// `"5:ORD-11:11:1"` — the old signature could not even be called with a
/// `Reason` argument, so no `Reason` value could ever change its output.
/// Every test below that asserts `Method`- or `Reason`-only variance
/// produces a different canonical string would have failed (or, for
/// `Reason`, been impossible to express at all) against that implementation.
/// </summary>
public class ToolArgumentCanonicalizerTests
{
    [Fact]
    public void CanonicalizeOrderCancellationArgs_IsDeterministic()
    {
        var canonical1 = ToolArgumentCanonicalizer.CanonicalizeOrderCancellationArgs(
            restock: true, method: RefundMethod.OriginalPaymentMethod, reason: OrderCancelReason.Customer);
        var canonical2 = ToolArgumentCanonicalizer.CanonicalizeOrderCancellationArgs(
            restock: true, method: RefundMethod.OriginalPaymentMethod, reason: OrderCancelReason.Customer);

        Assert.Equal(canonical1, canonical2);
    }

    [Fact]
    public void CanonicalizeOrderCancellationArgs_DifferentRestock_ProducesDifferentOutput()
    {
        var restockTrue = ToolArgumentCanonicalizer.CanonicalizeOrderCancellationArgs(
            restock: true, method: RefundMethod.OriginalPaymentMethod, reason: OrderCancelReason.Customer);
        var restockFalse = ToolArgumentCanonicalizer.CanonicalizeOrderCancellationArgs(
            restock: false, method: RefundMethod.OriginalPaymentMethod, reason: OrderCancelReason.Customer);

        Assert.NotEqual(restockTrue, restockFalse);
    }

    /// <summary>
    /// Regression test for Example A (§6): under the pre-fix implementation,
    /// both pairs below canonicalized identically because Method collapsed
    /// to a single bool. Each pair here must now canonicalize differently.
    /// </summary>
    [Theory]
    [InlineData(null, RefundMethod.OriginalPaymentMethod)]
    [InlineData(null, RefundMethod.StoreCredit)]
    [InlineData(RefundMethod.OriginalPaymentMethod, RefundMethod.StoreCredit)]
    public void CanonicalizeOrderCancellationArgs_DifferentMethod_ProducesDifferentOutput(RefundMethod? methodA, RefundMethod? methodB)
    {
        var a = ToolArgumentCanonicalizer.CanonicalizeOrderCancellationArgs(
            restock: true, method: methodA, reason: OrderCancelReason.Customer);
        var b = ToolArgumentCanonicalizer.CanonicalizeOrderCancellationArgs(
            restock: true, method: methodB, reason: OrderCancelReason.Customer);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void CanonicalizeOrderCancellationArgs_AllThreeMethodStates_ArePairwiseDistinct()
    {
        RefundMethod?[] methods = [null, RefundMethod.OriginalPaymentMethod, RefundMethod.StoreCredit];

        var canonicalForms = methods
            .Select(m => ToolArgumentCanonicalizer.CanonicalizeOrderCancellationArgs(
                restock: true, method: m, reason: OrderCancelReason.Customer))
            .ToList();

        Assert.Equal(methods.Length, canonicalForms.Distinct().Count());
    }

    /// <summary>
    /// Regression test for Example B (§6): the pre-fix signature had no
    /// Reason parameter at all, so no Reason value could ever change the
    /// output. Each pair here must now canonicalize differently.
    /// </summary>
    [Theory]
    [InlineData(OrderCancelReason.Customer, OrderCancelReason.Fraud)]
    [InlineData(OrderCancelReason.Payment, OrderCancelReason.StaffError)]
    [InlineData(OrderCancelReason.Inventory, OrderCancelReason.Other)]
    public void CanonicalizeOrderCancellationArgs_DifferentReason_ProducesDifferentOutput(OrderCancelReason reasonA, OrderCancelReason reasonB)
    {
        var a = ToolArgumentCanonicalizer.CanonicalizeOrderCancellationArgs(
            restock: true, method: RefundMethod.OriginalPaymentMethod, reason: reasonA);
        var b = ToolArgumentCanonicalizer.CanonicalizeOrderCancellationArgs(
            restock: true, method: RefundMethod.OriginalPaymentMethod, reason: reasonB);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void CanonicalizeOrderCancellationArgs_AllSixReasonValues_ArePairwiseDistinct()
    {
        var allReasons = Enum.GetValues<OrderCancelReason>();

        var canonicalForms = allReasons
            .Select(r => ToolArgumentCanonicalizer.CanonicalizeOrderCancellationArgs(
                restock: true, method: RefundMethod.OriginalPaymentMethod, reason: r))
            .ToList();

        Assert.Equal(allReasons.Length, canonicalForms.Distinct().Count());
    }

    /// <summary>
    /// Step 29 — exhaustive round-trip coverage for
    /// DecodeOrderCancellationArgs, the exact inverse added per Step 27/28's
    /// verified Option 1. Covers every valid combination the encoder could
    /// ever produce: 2 restock states x 3 RefundMethod? states x 6
    /// OrderCancelReason values = 36, re-confirmed against the live enums
    /// (not assumed from any prior handoff's count) via Enum.GetValues and
    /// an explicit iteration-count assertion, so a future change to either
    /// enum's membership would shrink or grow this test's own coverage
    /// visibly rather than silently.
    /// </summary>
    [Fact]
    public void DecodeOrderCancellationArgs_RoundTrips_ForEveryValidCombination()
    {
        bool[] restockValues = [true, false];
        RefundMethod?[] methodValues = [null, RefundMethod.OriginalPaymentMethod, RefundMethod.StoreCredit];
        var reasonValues = Enum.GetValues<OrderCancelReason>();

        Assert.Equal(2, restockValues.Length);
        Assert.Equal(3, methodValues.Length);
        Assert.Equal(6, reasonValues.Length);

        var combinationsChecked = 0;
        foreach (var restock in restockValues)
        {
            foreach (var method in methodValues)
            {
                foreach (var reason in reasonValues)
                {
                    var canonical = ToolArgumentCanonicalizer.CanonicalizeOrderCancellationArgs(restock, method, reason);
                    var decoded = ToolArgumentCanonicalizer.DecodeOrderCancellationArgs(canonical);

                    Assert.Equal(restock, decoded.Restock);
                    Assert.Equal(method, decoded.Method);
                    Assert.Equal(reason, decoded.Reason);
                    combinationsChecked++;
                }
            }
        }

        Assert.Equal(36, combinationsChecked);
    }

    [Fact]
    public void DecodeOrderCancellationArgs_EmptyString_ThrowsFormatException()
    {
        Assert.Throws<FormatException>(() => ToolArgumentCanonicalizer.DecodeOrderCancellationArgs(""));
    }

    [Fact]
    public void DecodeOrderCancellationArgs_MissingColon_ThrowsFormatException()
    {
        Assert.Throws<FormatException>(() => ToolArgumentCanonicalizer.DecodeOrderCancellationArgs("11"));
    }

    [Fact]
    public void DecodeOrderCancellationArgs_NonNumericLengthPrefix_ThrowsFormatException()
    {
        Assert.Throws<FormatException>(() => ToolArgumentCanonicalizer.DecodeOrderCancellationArgs("x:1" + "4:null" + "8:Customer"));
    }

    [Fact]
    public void DecodeOrderCancellationArgs_TruncatedMidField_ThrowsFormatException()
    {
        var valid = ToolArgumentCanonicalizer.CanonicalizeOrderCancellationArgs(
            restock: true, method: RefundMethod.StoreCredit, reason: OrderCancelReason.Fraud);
        var truncated = valid[..(valid.Length - 3)];

        Assert.Throws<FormatException>(() => ToolArgumentCanonicalizer.DecodeOrderCancellationArgs(truncated));
    }

    [Fact]
    public void DecodeOrderCancellationArgs_TrailingGarbage_ThrowsFormatException()
    {
        var valid = ToolArgumentCanonicalizer.CanonicalizeOrderCancellationArgs(
            restock: true, method: RefundMethod.StoreCredit, reason: OrderCancelReason.Fraud);
        var corrupted = valid + "extra";

        Assert.Throws<FormatException>(() => ToolArgumentCanonicalizer.DecodeOrderCancellationArgs(corrupted));
    }

    [Fact]
    public void DecodeOrderCancellationArgs_UnrecognizedRestockValue_ThrowsFormatException()
    {
        // "2" is neither "1" nor "0" -- a corrupted restock field.
        var canonical = "1:2" + "4:null" + "8:Customer";

        Assert.Throws<FormatException>(() => ToolArgumentCanonicalizer.DecodeOrderCancellationArgs(canonical));
    }

    [Fact]
    public void DecodeOrderCancellationArgs_UnrecognizedRefundMethodValue_ThrowsFormatException()
    {
        // "Bitcoin" is neither "null" nor a real RefundMethod member name.
        var canonical = "1:1" + "7:Bitcoin" + "8:Customer";

        Assert.Throws<FormatException>(() => ToolArgumentCanonicalizer.DecodeOrderCancellationArgs(canonical));
    }

    [Fact]
    public void DecodeOrderCancellationArgs_UnrecognizedReasonValue_ThrowsFormatException()
    {
        // "Unknown" is not a real OrderCancelReason member name.
        var canonical = "1:1" + "4:null" + "7:Unknown";

        Assert.Throws<FormatException>(() => ToolArgumentCanonicalizer.DecodeOrderCancellationArgs(canonical));
    }

    /// <summary>
    /// Step 31 — regression test for the gap step-30 independently verified
    /// empirically (via a throwaway scratch program, reproduced here
    /// permanently instead): Enum.TryParse alone accepts any numeric
    /// string, including one with no corresponding member at all -- "99" is
    /// neither 0 (OriginalPaymentMethod) nor 1 (StoreCredit). Before the
    /// Enum.IsDefined fix, this canonical string decoded "successfully" to
    /// a RefundMethod value that could never have been produced by
    /// CanonicalizeOrderCancellationArgs itself (which always writes the
    /// member's own name, never its numeric value).
    /// </summary>
    [Fact]
    public void DecodeOrderCancellationArgs_OutOfRangeNumericRefundMethodValue_ThrowsFormatException()
    {
        var canonical = "1:1" + "2:99" + "8:Customer";

        Assert.Throws<FormatException>(() => ToolArgumentCanonicalizer.DecodeOrderCancellationArgs(canonical));
    }

    /// <summary>
    /// Same underlying gap as
    /// DecodeOrderCancellationArgs_OutOfRangeNumericRefundMethodValue_ThrowsFormatException,
    /// for the OrderCancelReason field -- "99" has no corresponding member
    /// (only the six named members, 0-5, are defined).
    /// </summary>
    [Fact]
    public void DecodeOrderCancellationArgs_OutOfRangeNumericReasonValue_ThrowsFormatException()
    {
        var canonical = "1:1" + "4:null" + "2:99";

        Assert.Throws<FormatException>(() => ToolArgumentCanonicalizer.DecodeOrderCancellationArgs(canonical));
    }

    [Fact]
    public void CanonicalizeCustomerUpdateArgs_OrderOfFieldsChanged_DoesNotAffectOutput()
    {
        // Matches CustomerUpdateActionService's own HashSet<string>.SetEquals
        // intent comparison, which is order-insensitive.
        var canonical1 = ToolArgumentCanonicalizer.CanonicalizeCustomerUpdateArgs(["email", "phone"]);
        var canonical2 = ToolArgumentCanonicalizer.CanonicalizeCustomerUpdateArgs(["phone", "email"]);

        Assert.Equal(canonical1, canonical2);
    }

    [Fact]
    public void CanonicalizeCustomerUpdateArgs_DuplicateFields_CollapseToSameOutputAsDeduplicated()
    {
        var withDuplicate = ToolArgumentCanonicalizer.CanonicalizeCustomerUpdateArgs(["email", "email", "phone"]);
        var deduplicated = ToolArgumentCanonicalizer.CanonicalizeCustomerUpdateArgs(["email", "phone"]);

        Assert.Equal(deduplicated, withDuplicate);
    }

    [Fact]
    public void CanonicalizeCustomerUpdateArgs_DifferentFieldSet_ProducesDifferentOutput()
    {
        var canonical1 = ToolArgumentCanonicalizer.CanonicalizeCustomerUpdateArgs(["email"]);
        var canonical2 = ToolArgumentCanonicalizer.CanonicalizeCustomerUpdateArgs(["phone"]);

        Assert.NotEqual(canonical1, canonical2);
    }

    [Fact]
    public void CanonicalizeCustomerUpdateArgs_IsDeterministic()
    {
        var canonical1 = ToolArgumentCanonicalizer.CanonicalizeCustomerUpdateArgs(["email", "phone", "name"]);
        var canonical2 = ToolArgumentCanonicalizer.CanonicalizeCustomerUpdateArgs(["name", "email", "phone"]);
        var canonical3 = ToolArgumentCanonicalizer.CanonicalizeCustomerUpdateArgs(["name", "email", "phone"]);

        Assert.Equal(canonical1, canonical2);
        Assert.Equal(canonical2, canonical3);
    }

    // ── Refund args (Step refundOrder Phase 2) ──────────────────────────

    private static (string ExternalLineItemId, int Quantity)[] Items(params (string, int)[] items) => items;

    [Fact]
    public void CanonicalizeRefundArgs_IsDeterministic()
    {
        var canonical1 = ToolArgumentCanonicalizer.CanonicalizeRefundArgs(
            RefundMethod.OriginalPaymentMethod, Items(("gid://shopify/LineItem/1", 2)));
        var canonical2 = ToolArgumentCanonicalizer.CanonicalizeRefundArgs(
            RefundMethod.OriginalPaymentMethod, Items(("gid://shopify/LineItem/1", 2)));

        Assert.Equal(canonical1, canonical2);
    }

    [Fact]
    public void CanonicalizeRefundArgs_ItemOrderShuffled_ProducesIdenticalOutput()
    {
        // Design doc §9.1 — "same intent" is the SET of
        // (ExternalLineItemId, Quantity) pairs, not an ordered list.
        var inOrder = ToolArgumentCanonicalizer.CanonicalizeRefundArgs(
            RefundMethod.OriginalPaymentMethod,
            Items(("gid://shopify/LineItem/1", 1), ("gid://shopify/LineItem/2", 2)));
        var shuffled = ToolArgumentCanonicalizer.CanonicalizeRefundArgs(
            RefundMethod.OriginalPaymentMethod,
            Items(("gid://shopify/LineItem/2", 2), ("gid://shopify/LineItem/1", 1)));

        Assert.Equal(inOrder, shuffled);
    }

    [Fact]
    public void CanonicalizeRefundArgs_DifferentMethod_ProducesDifferentOutput()
    {
        var original = ToolArgumentCanonicalizer.CanonicalizeRefundArgs(
            RefundMethod.OriginalPaymentMethod, Items(("gid://shopify/LineItem/1", 1)));
        var storeCredit = ToolArgumentCanonicalizer.CanonicalizeRefundArgs(
            RefundMethod.StoreCredit, Items(("gid://shopify/LineItem/1", 1)));

        Assert.NotEqual(original, storeCredit);
    }

    [Fact]
    public void CanonicalizeRefundArgs_DifferentQuantity_ProducesDifferentOutput()
    {
        var one = ToolArgumentCanonicalizer.CanonicalizeRefundArgs(
            RefundMethod.OriginalPaymentMethod, Items(("gid://shopify/LineItem/1", 1)));
        var two = ToolArgumentCanonicalizer.CanonicalizeRefundArgs(
            RefundMethod.OriginalPaymentMethod, Items(("gid://shopify/LineItem/1", 2)));

        Assert.NotEqual(one, two);
    }

    [Fact]
    public void CanonicalizeRefundArgs_DifferentLineItem_ProducesDifferentOutput()
    {
        var itemA = ToolArgumentCanonicalizer.CanonicalizeRefundArgs(
            RefundMethod.OriginalPaymentMethod, Items(("gid://shopify/LineItem/1", 1)));
        var itemB = ToolArgumentCanonicalizer.CanonicalizeRefundArgs(
            RefundMethod.OriginalPaymentMethod, Items(("gid://shopify/LineItem/DIFFERENT", 1)));

        Assert.NotEqual(itemA, itemB);
    }

    [Fact]
    public void CanonicalizeRefundArgs_DifferentItemCount_ProducesDifferentOutput()
    {
        var oneItem = ToolArgumentCanonicalizer.CanonicalizeRefundArgs(
            RefundMethod.OriginalPaymentMethod, Items(("gid://shopify/LineItem/1", 1)));
        var twoItems = ToolArgumentCanonicalizer.CanonicalizeRefundArgs(
            RefundMethod.OriginalPaymentMethod,
            Items(("gid://shopify/LineItem/1", 1), ("gid://shopify/LineItem/2", 1)));

        Assert.NotEqual(oneItem, twoItems);
    }

    [Fact]
    public void DecodeRefundArgs_RoundTrips_ItemOrderNormalizedToSorted()
    {
        var canonical = ToolArgumentCanonicalizer.CanonicalizeRefundArgs(
            RefundMethod.StoreCredit,
            Items(("gid://shopify/LineItem/2", 2), ("gid://shopify/LineItem/1", 1)));

        var decoded = ToolArgumentCanonicalizer.DecodeRefundArgs(canonical);

        Assert.Equal(RefundMethod.StoreCredit, decoded.Method);
        Assert.Equal(2, decoded.Items.Count);
        // Sorted (ordinal) regardless of the order originally supplied.
        Assert.Equal("gid://shopify/LineItem/1", decoded.Items[0].ExternalLineItemId);
        Assert.Equal(1, decoded.Items[0].Quantity);
        Assert.Equal("gid://shopify/LineItem/2", decoded.Items[1].ExternalLineItemId);
        Assert.Equal(2, decoded.Items[1].Quantity);
    }

    [Fact]
    public void DecodeRefundArgs_SingleItem_RoundTrips()
    {
        var canonical = ToolArgumentCanonicalizer.CanonicalizeRefundArgs(
            RefundMethod.OriginalPaymentMethod, Items(("gid://shopify/LineItem/99999", 7)));

        var decoded = ToolArgumentCanonicalizer.DecodeRefundArgs(canonical);

        Assert.Equal(RefundMethod.OriginalPaymentMethod, decoded.Method);
        var only = Assert.Single(decoded.Items);
        Assert.Equal("gid://shopify/LineItem/99999", only.ExternalLineItemId);
        Assert.Equal(7, only.Quantity);
    }

    [Fact]
    public void DecodeRefundArgs_EmptyString_ThrowsFormatException()
    {
        Assert.Throws<FormatException>(() => ToolArgumentCanonicalizer.DecodeRefundArgs(""));
    }

    [Fact]
    public void DecodeRefundArgs_UnrecognizedRefundMethodValue_ThrowsFormatException()
    {
        // "Bitcoin" is neither a real RefundMethod member name.
        var canonical = "7:Bitcoin" + "1:0";

        Assert.Throws<FormatException>(() => ToolArgumentCanonicalizer.DecodeRefundArgs(canonical));
    }

    [Fact]
    public void DecodeRefundArgs_OutOfRangeNumericRefundMethodValue_ThrowsFormatException()
    {
        // "99" has no corresponding RefundMethod member (only 0 and 1 are defined).
        var canonical = "2:99" + "1:0";

        Assert.Throws<FormatException>(() => ToolArgumentCanonicalizer.DecodeRefundArgs(canonical));
    }

    [Fact]
    public void DecodeRefundArgs_NegativeItemCount_ThrowsFormatException()
    {
        // NumberStyles.None rejects a leading '-' — the encoder never writes
        // one. Method-field length computed, not hardcoded, so this test
        // stays correct if RefundMethod's member names ever change.
        var methodField = $"{nameof(RefundMethod.OriginalPaymentMethod).Length}:{RefundMethod.OriginalPaymentMethod}";
        var canonical = methodField + "2:-1";

        Assert.Throws<FormatException>(() => ToolArgumentCanonicalizer.DecodeRefundArgs(canonical));
    }

    [Fact]
    public void DecodeRefundArgs_TruncatedMidField_ThrowsFormatException()
    {
        var valid = ToolArgumentCanonicalizer.CanonicalizeRefundArgs(
            RefundMethod.StoreCredit, Items(("gid://shopify/LineItem/1", 3)));
        var truncated = valid[..(valid.Length - 3)];

        Assert.Throws<FormatException>(() => ToolArgumentCanonicalizer.DecodeRefundArgs(truncated));
    }

    [Fact]
    public void DecodeRefundArgs_TrailingGarbage_ThrowsFormatException()
    {
        var valid = ToolArgumentCanonicalizer.CanonicalizeRefundArgs(
            RefundMethod.StoreCredit, Items(("gid://shopify/LineItem/1", 3)));
        var corrupted = valid + "extra";

        Assert.Throws<FormatException>(() => ToolArgumentCanonicalizer.DecodeRefundArgs(corrupted));
    }

    [Fact]
    public void DecodeRefundArgs_DeclaredCountExceedsActualItems_ThrowsFormatException()
    {
        // Declares 5 items but the string only contains one item's worth of
        // encoded data after the count field — ReadLengthPrefixedField must
        // fail closed on the resulting overrun, never return a partial/
        // best-guess item list.
        var oneItemCanonical = ToolArgumentCanonicalizer.CanonicalizeRefundArgs(
            RefundMethod.OriginalPaymentMethod, Items(("gid://shopify/LineItem/1", 1)));
        var methodField = $"{nameof(RefundMethod.OriginalPaymentMethod).Length}:{RefundMethod.OriginalPaymentMethod}";
        var itemsPortion = oneItemCanonical[(methodField.Length + "1:1".Length)..];
        var corrupted = methodField + "1:5" + itemsPortion;

        Assert.Throws<FormatException>(() => ToolArgumentCanonicalizer.DecodeRefundArgs(corrupted));
    }
}
