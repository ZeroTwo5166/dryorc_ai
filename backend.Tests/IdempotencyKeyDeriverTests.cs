using backend.Services;
using Xunit;

namespace backend.Tests;

/// <summary>
/// Pure unit tests — no DB, no HTTP. Covers the approved derivation design
/// at docs/handoffs/step-19-idempotency-key-derivation-approved.md: the
/// (assistantMessageId, toolUseId) pair is the retry boundary, every other
/// participating input changes the key, and excluded inputs (timestamps,
/// correlation IDs, raw model prose) simply have no parameter to influence
/// the key through in the first place.
/// </summary>
public class IdempotencyKeyDeriverTests
{
    private static readonly Guid BusinessId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static string Derive(
        Guid? businessId = null, string toolName = "cancelOrder", string resourceId = "ORD-1",
        string canonicalArguments = "args", string assistantMessageId = "msg_1", string toolUseId = "toolu_1") =>
        IdempotencyKeyDeriver.Derive(businessId ?? BusinessId, toolName, resourceId, canonicalArguments, assistantMessageId, toolUseId);

    [Fact]
    public void Derive_SameInputs_ProducesIdenticalKey()
    {
        var key1 = Derive();
        var key2 = Derive();

        Assert.Equal(key1, key2);
    }

    [Fact]
    public void Derive_SameMessageAndToolUseId_WithIdenticalArguments_ProducesIdenticalKey()
    {
        // Scenario 1/2 from the approved design: retrying the same
        // already-received tool_use block must reuse the identical key.
        var key1 = Derive(assistantMessageId: "msg_retry", toolUseId: "toolu_retry");
        var key2 = Derive(assistantMessageId: "msg_retry", toolUseId: "toolu_retry");

        Assert.Equal(key1, key2);
    }

    [Fact]
    public void Derive_ProducesLowercaseHex64CharacterString()
    {
        var key = Derive();

        Assert.Equal(64, key.Length);
        Assert.Matches("^[0-9a-f]{64}$", key);
    }

    [Fact]
    public void Derive_FitsExistingIdempotencyKeyLengthConstraint()
    {
        // OrderCancellationService/CustomerUpdateActionService both reject
        // keys over 256 characters — the derived key must always fit
        // comfortably within that, regardless of how long the canonical
        // arguments string is.
        var key = Derive(canonicalArguments: new string('x', 5000));

        Assert.True(key.Length <= 256);
    }

    [Fact]
    public void Derive_DifferentToolUseId_ProducesDifferentKey()
    {
        // Scenario 3/4: a fresh model-generated tool_use invocation must
        // derive a new key by default.
        var key1 = Derive(toolUseId: "toolu_1");
        var key2 = Derive(toolUseId: "toolu_2");

        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public void Derive_DifferentAssistantMessageId_ProducesDifferentKey()
    {
        var key1 = Derive(assistantMessageId: "msg_1");
        var key2 = Derive(assistantMessageId: "msg_2");

        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public void Derive_DifferentToolName_ProducesDifferentKey()
    {
        // Scenario 5: cancelOrder(123) vs updateCustomer(...) must not collide.
        var key1 = Derive(toolName: "cancelOrder");
        var key2 = Derive(toolName: "updateCustomer");

        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public void Derive_DifferentResourceId_ProducesDifferentKey()
    {
        var key1 = Derive(resourceId: "ORD-1");
        var key2 = Derive(resourceId: "ORD-2");

        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public void Derive_DifferentCanonicalArguments_ProducesDifferentKey()
    {
        // Scenario 6: same tool/resource, materially different arguments
        // (e.g. updateCustomer's target value change surfaced via a
        // different canonical-args string) must not collide.
        var key1 = Derive(canonicalArguments: "email");
        var key2 = Derive(canonicalArguments: "phone");

        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public void Derive_DifferentBusinessId_ProducesDifferentKey()
    {
        var key1 = Derive(businessId: BusinessId);
        var key2 = Derive(businessId: Guid.Parse("22222222-2222-2222-2222-222222222222"));

        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public void Derive_ConcatenationAmbiguity_DoesNotCollideAcrossComponentBoundaries()
    {
        // Proves the length-prefixing actually does its job: naive
        // delimiter-free (or naively delimited) concatenation could make
        // ("ab","c") and ("a","bc") hash identically. Split across
        // resourceId/canonicalArguments specifically (both plain strings
        // with no length prefix of their own in the public API) to prove
        // the internal encoding disambiguates them.
        var key1 = IdempotencyKeyDeriver.Derive(BusinessId, "tool", "ab", "c", "msg", "toolu");
        var key2 = IdempotencyKeyDeriver.Derive(BusinessId, "tool", "a", "bc", "msg", "toolu");

        Assert.NotEqual(key1, key2);
    }
}
