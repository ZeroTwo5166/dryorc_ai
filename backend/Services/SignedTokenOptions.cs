namespace backend.Services;

/// <summary>
/// Step 21 (signed-token half). Config-driven parameters for
/// ISignedIdentityTokenService — same IOptions&lt;T&gt; pattern as
/// VerificationOptions/ShopifyOptions/WidgetTokenOptions, but unlike those
/// classes, nothing here is a secret: Nivora never holds a business's
/// private signing key, only per-business public keys (BusinessSigningKey),
/// so there is no SigningKey/ClientSecret-equivalent value requiring
/// dotnet user-secrets.
///
/// GrantDurationHours' default (4) is the implementation-ready
/// specification's own recommendation, not an owner-approved product
/// decision — see docs/handoffs/step-21-signed-token-implementation-ready.md
/// §6/§16. Do not describe this default as a locked decision in any future
/// report.
/// </summary>
public class SignedTokenOptions
{
    public const string SectionName = "SignedToken";

    /// <summary>Maximum accepted token validity window (exp - iat), enforced
    /// in addition to standard exp/clock-skew validation — see
    /// SignedIdentityTokenService's own doc comment for why this must be
    /// checked explicitly (TokenValidationParameters.ValidateLifetime does
    /// not itself cap the window length, only checks exp against now).</summary>
    public int MaxTokenLifetimeMinutes { get; set; } = 5;

    /// <summary>Sliding ExternalIdentity.ExpiresAt duration, extended on
    /// every successful fresh signed-token validation.</summary>
    public int GrantDurationHours { get; set; } = 4;

    public int ClockSkewMinutes { get; set; } = 1;
}
