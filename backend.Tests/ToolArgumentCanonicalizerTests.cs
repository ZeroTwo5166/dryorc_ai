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
}
