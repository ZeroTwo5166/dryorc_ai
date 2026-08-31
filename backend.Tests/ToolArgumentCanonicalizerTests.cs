using backend.Services;
using Xunit;

namespace backend.Tests;

/// <summary>
/// Pure unit tests — no DB, no HTTP. Covers only the two currently known
/// argument shapes (OrderCancellation, CustomerUpdateAction) per
/// docs/handoffs/step-19-idempotency-key-derivation-approved.md §5 — no
/// generic canonicalizer exists to test.
/// </summary>
public class ToolArgumentCanonicalizerTests
{
    [Fact]
    public void CanonicalizeOrderCancellationArgs_IsDeterministic()
    {
        var canonical1 = ToolArgumentCanonicalizer.CanonicalizeOrderCancellationArgs("ORD-1", restock: true, refund: false);
        var canonical2 = ToolArgumentCanonicalizer.CanonicalizeOrderCancellationArgs("ORD-1", restock: true, refund: false);

        Assert.Equal(canonical1, canonical2);
    }

    [Theory]
    [InlineData("ORD-2", true, false)]
    [InlineData("ORD-1", false, false)]
    [InlineData("ORD-1", true, true)]
    public void CanonicalizeOrderCancellationArgs_DifferingField_ProducesDifferentOutput(string externalOrderId, bool restock, bool refund)
    {
        var baseline = ToolArgumentCanonicalizer.CanonicalizeOrderCancellationArgs("ORD-1", restock: true, refund: false);
        var varied = ToolArgumentCanonicalizer.CanonicalizeOrderCancellationArgs(externalOrderId, restock, refund);

        Assert.NotEqual(baseline, varied);
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
