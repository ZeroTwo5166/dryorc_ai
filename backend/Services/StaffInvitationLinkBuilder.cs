namespace backend.Services;

/// <summary>
/// The invitation link (DEC D19; staff-invite design Sections 4 and 5).
/// Create and resend validate Frontend:BaseUrl before any database work, and
/// the link carries the token only in the URL fragment, so it never reaches
/// a server request line or a Referer header.
/// </summary>
public static class StaffInvitationLinkBuilder
{
    private const string BaseUrlKey = "Frontend:BaseUrl";

    /// <summary>
    /// Returns Frontend:BaseUrl as an absolute URI, or throws
    /// InvalidOperationException (a 500, before any mutation) when it is
    /// missing, not absolute, carries a query, fragment or user info, or uses
    /// a scheme other than https. Plain http is allowed only in Development
    /// and only for localhost or 127.0.0.1. The exception message names the
    /// key, never the configured value.
    /// </summary>
    public static Uri GetValidatedBaseUri(IConfiguration configuration, IHostEnvironment env)
    {
        // Step 1: present.
        var raw = configuration[BaseUrlKey];
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw Invalid();
        }

        // Step 2: absolute.
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            throw Invalid();
        }

        // Step 3: no query and no fragment, also not an empty one.
        if (uri.Query.Length != 0 || uri.Fragment.Length != 0 || raw.Contains('?') || raw.Contains('#'))
        {
            throw Invalid();
        }

        // Step 4: no credentials in a link base.
        if (uri.UserInfo.Length != 0)
        {
            throw Invalid();
        }

        // Step 5: https, or http on localhost in Development only.
        if (uri.Scheme == Uri.UriSchemeHttps)
        {
            return uri;
        }

        if (uri.Scheme == Uri.UriSchemeHttp && env.IsDevelopment() && (uri.Host == "localhost" || uri.Host == "127.0.0.1"))
        {
            return uri;
        }

        // Step 6.
        throw Invalid();
    }

    /// <summary>{base}/accept-invite#token={token}, built with the URI APIs.</summary>
    public static string BuildLink(Uri baseUri, string token) =>
        new UriBuilder(baseUri)
        {
            Path = baseUri.AbsolutePath.TrimEnd('/') + "/accept-invite",
            Fragment = "token=" + token,
        }.Uri.AbsoluteUri;

    private static InvalidOperationException Invalid() =>
        new($"{BaseUrlKey} is missing or is not a valid link base for staff invitations.");
}
