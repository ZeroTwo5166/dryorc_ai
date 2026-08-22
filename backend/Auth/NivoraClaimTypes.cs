namespace backend.Auth;

public static class NivoraClaimTypes
{
    /// <summary>
    /// The authenticated principal's Business.Id, as a string Guid. Shared
    /// between the staff cookie principal (set by
    /// NivoraClaimsPrincipalFactory) and the widget JWT principal (set by
    /// WidgetTokenService) — this is exactly what lets
    /// ClaimsCurrentTenantAccessor resolve the tenant for both without any
    /// scheme-specific branching.
    /// </summary>
    public const string BusinessId = "nivora:business_id";

    /// <summary>Widget-JWT-only: the Widget.Id this session was issued for.</summary>
    public const string WidgetId = "nivora:widget_id";

    /// <summary>Widget-JWT-only: the visitor session this token is bound to.</summary>
    public const string VisitorSessionId = "nivora:visitor_session_id";
}
