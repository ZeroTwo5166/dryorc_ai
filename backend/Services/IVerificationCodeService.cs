namespace backend.Services;

public enum RequestCodeOutcome
{
    /// <summary>A new code was generated and sent (covers both the
    /// zero-match auto-create path and the single-existing-match path —
    /// deliberately not distinguished in this outcome, since the caller
    /// must return the same response for both to preserve enumeration
    /// resistance; see WidgetController's own usage).</summary>
    Issued,

    /// <summary>
    /// More than one Customer matched the normalized email within this
    /// Business. Fails safely: no VerificationCode was created, no code was
    /// generated or sent. Per the design doc's enumeration-resistance
    /// requirement (Section 6), the caller must still present this
    /// identically to Issued in its HTTP response.
    /// </summary>
    AmbiguousCustomerMatch,

    /// <summary>The scope-keyed resend cooldown has not yet elapsed.</summary>
    ResendCooldownActive,

    /// <summary>The scope-keyed 5-per-hour resend limit has been reached.</summary>
    ResendLimitExceeded,
}

public record RequestCodeResult(RequestCodeOutcome Outcome);

public enum VerificationFailureReason
{
    None,
    NotFound,
    Expired,
    AlreadyUsed,
    Superseded,
    AttemptsExhausted,
    CodeMismatch,

    /// <summary>
    /// Identity-binding/erasure-integrity design, Decision 1 (implementation
    /// plan Phase 1). The target Conversation's CustomerId is already
    /// non-null and bound to a different customer than the one that just
    /// verified — the conditional UPDATE in VerifyCodeAsync rejects the
    /// rebind rather than silently overwriting it. Re-verification by the
    /// already-bound customer is unaffected by this reason.
    /// </summary>
    AlreadyBoundToDifferentCustomer,
}

public record VerificationResult(bool Success, VerificationFailureReason Reason);

/// <summary>
/// Step 21 (OTP half). Issues and verifies Level 2 verification OTP codes.
/// See VerificationCode's own doc comment for the underlying schema
/// invariants this service must uphold (atomic supersede, atomic consume,
/// per-row MaxAttempts).
///
/// Customer resolution/auto-creation happens inside RequestCodeAsync,
/// before a VerificationCode row is ever created — a data-resolution step,
/// not proof of identity. Conversation.CustomerId is attached only inside
/// VerifyCodeAsync, and only on a fully successful, atomically-consumed
/// code check. These are two distinct events; no code path merges them.
/// </summary>
public interface IVerificationCodeService
{
    /// <summary>
    /// channel is the conversation's own Channel (see ConversationChannels)
    /// — the verification scope key, not a delivery-method selector; OTP
    /// delivery in this pass is always email (Decision 23: SMS deferred,
    /// no ISmsSender exists). email is normalized (trim+lowercase)
    /// internally before lookup/creation.
    /// </summary>
    Task<RequestCodeResult> RequestCodeAsync(
        Guid businessId, Guid conversationId, string channel, string email,
        CancellationToken cancellationToken);

    /// <summary>
    /// email must be re-submitted here (not just the code) — the widget JWT
    /// carries no CustomerId, and Conversation.CustomerId is not yet set at
    /// this point, so there is no other way to derive the CustomerId half
    /// of the (BusinessId, CustomerId, ConversationId, Channel) scope key
    /// the active VerificationCode is looked up by. Re-resolves the
    /// Customer by the same normalized-email rule RequestCodeAsync uses
    /// (0/1/many-match), but never auto-creates on zero matches.
    /// </summary>
    Task<VerificationResult> VerifyCodeAsync(
        Guid businessId, Guid conversationId, string channel, string email, string submittedCode,
        CancellationToken cancellationToken);
}
