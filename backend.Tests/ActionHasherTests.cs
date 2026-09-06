using backend.Services;
using Xunit;

namespace backend.Tests;

/// <summary>
/// Pure unit tests — no DB, no HTTP. Covers the Step 22 action-identity hash
/// (docs/handoffs/step-22-ai-action-safety-model-concrete-contract-design-v2.md
/// §13): determinism, independent collision resistance across all six
/// action-identity components, and a boundary-ambiguity regression test
/// proving the length-prefixing scheme actually does its job.
///
/// The boundary-ambiguity test would fail against a naive, unprefixed
/// concatenation implementation: for the two input pairs used below,
/// concatenating `toolName` directly adjacent to `resourceId` with no
/// length prefix produces the byte-identical string `"ABC"` in both cases
/// (`"AB"+"C"` and `"A"+"BC"`), which a naive implementation would hash
/// identically — a real, exploitable collision between two different
/// logical actions. `ActionHasher.Derive`'s actual length-prefixed encoding
/// produces `"2:AB1:C"` vs. `"1:A2:BC"` for the same two inputs — distinct
/// strings — so the test below only passes because the real implementation
/// avoids exactly that defect, not because the inputs happen to differ in
/// some other way.
/// </summary>
public class ActionHasherTests
{
    private static readonly Guid BusinessId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ConversationId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid CustomerId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static string Derive(
        Guid? businessId = null, Guid? conversationId = null, Guid? customerId = null,
        string toolName = "cancelOrder", string resourceId = "ORD-1", string canonicalArguments = "args") =>
        ActionHasher.Derive(
            businessId ?? BusinessId, conversationId ?? ConversationId, customerId ?? CustomerId,
            toolName, resourceId, canonicalArguments);

    [Fact]
    public void Derive_SameInputs_ProducesIdenticalHash()
    {
        var hash1 = Derive();
        var hash2 = Derive();

        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void Derive_ProducesLowercaseHex64CharacterString()
    {
        var hash = Derive();

        Assert.Equal(64, hash.Length);
        Assert.Matches("^[0-9a-f]{64}$", hash);
    }

    [Fact]
    public void Derive_DifferentBusinessId_ProducesDifferentHash()
    {
        var hash1 = Derive(businessId: BusinessId);
        var hash2 = Derive(businessId: Guid.Parse("44444444-4444-4444-4444-444444444444"));

        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void Derive_DifferentConversationId_ProducesDifferentHash()
    {
        var hash1 = Derive(conversationId: ConversationId);
        var hash2 = Derive(conversationId: Guid.Parse("55555555-5555-5555-5555-555555555555"));

        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void Derive_DifferentCustomerId_ProducesDifferentHash()
    {
        var hash1 = Derive(customerId: CustomerId);
        var hash2 = Derive(customerId: Guid.Parse("66666666-6666-6666-6666-666666666666"));

        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void Derive_DifferentToolName_ProducesDifferentHash()
    {
        // Mirrors IdempotencyKeyDeriverTests' equivalent case: cancelOrder(123)
        // vs updateCustomer(...) must not collide.
        var hash1 = Derive(toolName: "cancelOrder");
        var hash2 = Derive(toolName: "updateCustomer");

        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void Derive_DifferentResourceId_ProducesDifferentHash()
    {
        var hash1 = Derive(resourceId: "ORD-1");
        var hash2 = Derive(resourceId: "ORD-2");

        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void Derive_DifferentCanonicalArguments_ProducesDifferentHash()
    {
        // Same tool/resource, materially different canonical arguments
        // (e.g. a different Method/Reason combination for orderCancel,
        // represented here by two different pre-canonicalized strings —
        // ToolArgumentCanonicalizer's own correctness is tested separately)
        // must not collide.
        var hash1 = Derive(canonicalArguments: "args-a");
        var hash2 = Derive(canonicalArguments: "args-b");

        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void Derive_ConcatenationAmbiguity_DoesNotCollideAcrossComponentBoundaries()
    {
        // Proves the length-prefixing actually does its job: a naive,
        // unprefixed concatenation of ToolName immediately followed by
        // ResourceId would produce the identical string "ABC" for both
        // ("AB","C") and ("A","BC") — see this file's class doc comment for
        // the exact reasoning. Every other component is held fixed so this
        // test isolates exactly the ToolName/ResourceId boundary.
        var hash1 = Derive(toolName: "AB", resourceId: "C");
        var hash2 = Derive(toolName: "A", resourceId: "BC");

        Assert.NotEqual(hash1, hash2);
    }
}
