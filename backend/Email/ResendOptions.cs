namespace backend.Email;

/// <summary>
/// Resend's shared, unverified-domain sender (onboarding@resend.dev) is the
/// default FromEmail — works with no setup, but is rate-limited and can only
/// be trusted for real deliverability once a real domain is verified in
/// Resend and this is pointed at it. ApiKey never lives here — it's read
/// directly from configuration (dotnet user-secrets locally) in Program.cs,
/// the same "never commit a secret" rule as ConnectionStrings:DefaultConnection.
/// </summary>
public class ResendOptions
{
    public const string SectionName = "Resend";

    public string FromEmail { get; set; } = "onboarding@resend.dev";
}
