using backend.Services;
using Xunit;

namespace backend.Tests;

/// <summary>
/// Pure unit tests — no DB, no HTTP. Covers the two invariants Phase 2 of
/// docs/handoffs/identity-binding-erasure-integrity-implementation-plan.md
/// explicitly requires for the new ForCustomerErasure key: determinism, and
/// distinctness from every other existing AdvisoryLockKeyGenerator key for
/// the same (businessId, id) pair. This is the file's first test of this
/// shape — no prior AdvisoryLockKeyGeneratorTests.cs existed for any of the
/// other five keys (confirmed by inspection before writing this file), so
/// this establishes precedent rather than following one.
/// </summary>
public class AdvisoryLockKeyGeneratorTests
{
    private static readonly Guid BusinessId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Id = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void ForCustomerErasure_SameInputs_ProducesIdenticalKey()
    {
        var key1 = AdvisoryLockKeyGenerator.ForCustomerErasure(BusinessId, Id);
        var key2 = AdvisoryLockKeyGenerator.ForCustomerErasure(BusinessId, Id);

        Assert.Equal(key1, key2);
    }

    /// <summary>
    /// Distinctness from every other existing key for the same
    /// (businessId, id) pair — the plan's own illustrative comparison
    /// (ForCustomerErasure(b, c) != ForVerificationCode(b, c, c, "x")),
    /// extended to every other key whose own signature is meaningfully
    /// comparable against the same two-Guid pair. ForCustomerEmailResolution
    /// is deliberately excluded: its second parameter is a string
    /// (normalizedEmail), not a Guid id, so there is no same-shaped
    /// (businessId, id) pair to compare it against without inventing a
    /// comparison the plan never specified.
    /// </summary>
    [Fact]
    public void ForCustomerErasure_DistinctFromEveryOtherKey_ForSameBusinessAndIdPair()
    {
        var customerErasureKey = AdvisoryLockKeyGenerator.ForCustomerErasure(BusinessId, Id);

        Assert.NotEqual(customerErasureKey, AdvisoryLockKeyGenerator.ForVisitorSession(BusinessId, Id, "session"));
        Assert.NotEqual(customerErasureKey, AdvisoryLockKeyGenerator.ForKnowledgeDocument(BusinessId, Id));
        Assert.NotEqual(customerErasureKey, AdvisoryLockKeyGenerator.ForConversationNotification(BusinessId, Id));
        Assert.NotEqual(customerErasureKey, AdvisoryLockKeyGenerator.ForVerificationCode(BusinessId, Id, Id, "x"));
        Assert.NotEqual(customerErasureKey, AdvisoryLockKeyGenerator.ForConversationTagState(BusinessId, Id));
    }

    /// <summary>
    /// Staff invite slice 5a (S5a-4): the per-business invitation cap key is
    /// deterministic, differs between businesses, and differs from
    /// ForCustomerErasure for the same business.
    /// </summary>
    [Fact]
    public void ForStaffInvitationCap_IsDeterministic_AndScopedPerBusiness()
    {
        var otherBusinessId = Guid.Parse("33333333-3333-3333-3333-333333333333");

        var key1 = AdvisoryLockKeyGenerator.ForStaffInvitationCap(BusinessId);
        var key2 = AdvisoryLockKeyGenerator.ForStaffInvitationCap(BusinessId);

        Assert.Equal(key1, key2);
        Assert.NotEqual(key1, AdvisoryLockKeyGenerator.ForStaffInvitationCap(otherBusinessId));
        Assert.NotEqual(key1, AdvisoryLockKeyGenerator.ForCustomerErasure(BusinessId, BusinessId));
    }
}
