namespace backend.Auth;

/// <summary>
/// Named authentication scheme identifiers. The staff cookie scheme is
/// registered by AddIdentity/ConfigureApplicationCookie as
/// IdentityConstants.ApplicationScheme and remains the default — nothing
/// here changes that. WidgetBearer is a second, explicitly-named,
/// non-default scheme; NivoraPolicies.Widget binds to it explicitly so no
/// endpoint can accidentally end up authenticated by the wrong scheme.
/// </summary>
public static class NivoraAuthSchemes
{
    public const string WidgetBearer = "WidgetBearer";
}
