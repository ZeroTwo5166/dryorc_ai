namespace backend.Enums;

/// <summary>
/// Step 22 AI Action Safety Model — Approval lifecycle. Expired is
/// deliberately not a member: it is a read-time predicate (ExpiresAt <=
/// now), never an active write, mirroring VerificationCode's identical
/// existing pattern (no Status column at all, expiry represented purely via
/// ExpiresAt). See docs/handoffs/step-22-ai-action-safety-model-concrete-contract-design-v2.md
/// §7 for the full state machine.
/// </summary>
public enum ApprovalStatus
{
    Pending,
    Approved,
    Denied,
    Consumed
}
