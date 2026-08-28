namespace backend.Services;

/// <summary>
/// Step 21 (OTP half). Config-driven parameters for VerificationCodeService,
/// same pattern as ShopifyOptions/WidgetTokenOptions/IntegrationStateTokenOptions
/// — a small options class, HashingPepper set via `dotnet user-secrets set
/// "Verification:HashingPepper" "..."` (Development) or the hosting
/// platform's configuration (production), never in appsettings.json.
///
/// The numeric defaults below are the explicitly approved Step 21 values
/// (design doc Section 11's recommendations, confirmed/refined directly by
/// the user during implementation) — not placeholders pending a future
/// decision, unlike the widget rate-limit numbers in Program.cs.
/// </summary>
public class VerificationOptions
{
    public const string SectionName = "Verification";

    /// <summary>Digits in a generated OTP code. Always numeric.</summary>
    public int CodeLength { get; set; } = 6;

    /// <summary>
    /// How long an issued VerificationCode remains submittable
    /// (VerificationCode.ExpiresAt), independent of Level2VerifiedDurationMinutes.
    /// </summary>
    public int CodeExpiryMinutes { get; set; } = 5;

    /// <summary>Conversation.Level2VerifiedUntil duration granted on successful verification.</summary>
    public int Level2VerifiedDurationMinutes { get; set; } = 30;

    /// <summary>
    /// Minimum seconds between resend requests for the same
    /// (BusinessId, CustomerId, ConversationId, Channel) verification scope
    /// — enforced in VerificationCodeService, not the VisitorSessionId-
    /// partitioned rate limiter (which remains a complementary, bypassable-
    /// via-fresh-session volumetric control, not this security boundary).
    /// </summary>
    public int ResendCooldownSeconds { get; set; } = 60;

    /// <summary>Maximum resends allowed per verification scope per rolling hour.</summary>
    public int MaxResendsPerHour { get; set; } = 5;

    /// <summary>
    /// Server-side secret keying the OTP hash (HMAC-SHA256), never a bare
    /// unsalted hash — a 6-digit code space is trivially brute-forced
    /// offline against a leaked CodeHash column without this key.
    /// </summary>
    public required string HashingPepper { get; set; }
}
