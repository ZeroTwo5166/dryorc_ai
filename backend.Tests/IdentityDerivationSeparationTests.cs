using backend.Services;
using Xunit;

namespace backend.Tests;

/// <summary>
/// Defense-in-depth cross-check between ActionHasher (step 11's action
/// identity) and ApprovalExecutionKeyDeriver (step 12's execution-leg
/// identity) — identified during design-doc verification
/// (docs/handoffs/step-42-cancelorder-execute-steps-11-12-classification-and-identity-collapse-analysis.md
/// §5's re-derivation) as feasible now, even though full integration
/// coverage of the actual misuse scenario (a call site inside Execute
/// silently reusing one deriver's output as the other's input) cannot be
/// observed until step 13 exists and makes the execution-leg key's value
/// reach something persisted. This test does not close that integration-
/// level risk — see step-42 §7b for why no test achievable before step 13
/// can. It only proves the two functions are not interchangeable for the
/// four inputs they share (BusinessId, ToolName, ResourceId,
/// CanonicalArguments), so a future refactor cannot silently make them
/// collide or converge without this test failing first.
///
/// Pure unit test — no DB, no HTTP, mirrors ActionHasherTests'/
/// ApprovalExecutionKeyDeriverTests' own existing rigor for the pair
/// considered together rather than individually.
/// </summary>
public class IdentityDerivationSeparationTests
{
    [Fact]
    public void ActionHasherAndApprovalExecutionKeyDeriver_SameSharedInputs_ProduceDifferentOutputs()
    {
        var businessId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        const string toolName = "cancelOrder";
        const string resourceId = "gid://shopify/Order/12345";
        const string canonicalArguments = "args";

        // ActionHasher's two exclusive inputs (ConversationId, CustomerId)
        // are arbitrary here — the point being tested is that even with
        // every SHARED input held identical, the two functions' outputs
        // never collide, regardless of what their exclusive inputs are.
        var actionHash = ActionHasher.Derive(
            businessId,
            conversationId: Guid.Parse("22222222-2222-2222-2222-222222222222"),
            customerId: Guid.Parse("33333333-3333-3333-3333-333333333333"),
            toolName, resourceId, canonicalArguments);

        var approvalExecutionKey = ApprovalExecutionKeyDeriver.Derive(
            businessId, toolName, resourceId, canonicalArguments,
            approvalId: Guid.Parse("44444444-4444-4444-4444-444444444444"));

        Assert.NotEqual(actionHash, approvalExecutionKey);
    }
}
