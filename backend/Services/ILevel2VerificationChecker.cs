namespace backend.Services;

/// <summary>
/// Step 21. Read-side Level 2 verification predicate — a reusable primitive
/// for anything that needs to ask "is this conversation currently Level 2
/// verified" (e.g. future write-tool dispatch), kept separate from
/// VerificationCodeService's mutation/issuance responsibilities.
///
/// Deliberately does not authorize order ownership, infer Shopify order
/// ownership, or create/mutate any identity state — see
/// IOrderOwnershipAuthorizationService for that separate, independent
/// security boundary (Step 20), which this predicate is never a substitute
/// for.
/// </summary>
public interface ILevel2VerificationChecker
{
    Task<bool> IsVerifiedAsync(Guid businessId, Guid conversationId, CancellationToken cancellationToken);
}
