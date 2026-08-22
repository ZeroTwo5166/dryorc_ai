namespace backend.Services;

/// <summary>
/// The HMAC signing key for widget session JWTs. Same secret-handling rule
/// as the DB connection string / Resend API key: never in appsettings.json,
/// set via `dotnet user-secrets set "WidgetToken:SigningKey" "..."`
/// (Development) or the hosting platform's configuration (production).
/// </summary>
public class WidgetTokenOptions
{
    public const string SectionName = "WidgetToken";

    public required string SigningKey { get; set; }
}
