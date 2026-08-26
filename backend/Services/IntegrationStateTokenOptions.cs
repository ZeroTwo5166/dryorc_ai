namespace backend.Services;

/// <summary>
/// The HMAC signing key for integration OAuth state tokens. Same
/// secret-handling rule as WidgetToken:SigningKey / the DB connection
/// string: never in appsettings.json, set via
/// `dotnet user-secrets set "IntegrationStateToken:SigningKey" "..."`
/// (Development) or the hosting platform's configuration (production).
/// </summary>
public class IntegrationStateTokenOptions
{
    public const string SectionName = "IntegrationStateToken";

    public required string SigningKey { get; set; }
}
