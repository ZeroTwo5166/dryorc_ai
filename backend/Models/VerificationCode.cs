using backend.Interfaces;

namespace backend.Models;

/// <summary>
/// OTP codes for Level 2 verification (Section 4 fallback path, when a
/// signed-token handoff isn't available). Conversation-scoped by design —
/// a code issued for one conversation cannot verify a different one, even
/// for the same customer.
///
/// A code is valid only when:
///   Used == false AND AttemptCount < MaxAttempts
///   AND SupersededAt == null AND ExpiresAt > now
///
/// Requesting a new code must supersede any existing active code for the
/// same (BusinessId, CustomerId, ConversationId, Channel) scope —
/// invalidation + new-code creation must happen atomically (advisory lock
/// keyed on that same scope), so two concurrent resend requests within
/// one conversation can never leave two simultaneously active codes. A
/// resend in one conversation must never affect an active code in a
/// different conversation for the same customer.
///
/// MaxAttempts is stored per-row (not a global constant) so a policy
/// change (e.g. 5 → 10 attempts) doesn't retroactively alter the limit
/// already-issued codes were created under.
/// </summary>
public class VerificationCode : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid BusinessId { get; set; }
    public Guid CustomerId { get; set; }
    public Guid ConversationId { get; set; }

    /// <summary>See <see cref="ConversationChannels"/> for valid values.</summary>
    public required string Channel { get; set; }

    /// <summary>Hash only — the raw code is never stored.</summary>
    public required string CodeHash { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public bool Used { get; set; } = false;
    public int AttemptCount { get; set; } = 0;
    public int MaxAttempts { get; set; } = 5;

    public DateTimeOffset? SupersededAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
