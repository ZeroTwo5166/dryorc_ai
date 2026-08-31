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

    /// <summary>
    /// Widget-JWT-only, Step 21 (signed-token half). The Customer.Id
    /// resolved from a successfully-validated signed identity token at
    /// Init — present only when Init actually received and verified one;
    /// absent for an ordinary anonymous widget session. Never present on
    /// the staff cookie principal.
    /// </summary>
    public const string CustomerId = "nivora:customer_id";
}
