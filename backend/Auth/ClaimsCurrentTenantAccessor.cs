using backend.Interfaces;
using Microsoft.AspNetCore.Http;

namespace backend.Auth;

/// <summary>
/// Real <see cref="ICurrentTenantAccessor"/> implementation, live as of Step 4.
/// Resolves BusinessId exclusively from the authenticated request's own
/// <see cref="NivoraClaimTypes.BusinessId"/> claim — set server-side at sign-in
/// by <see cref="NivoraClaimsPrincipalFactory"/>, never from anything the
/// client supplies in the request body, headers, route, or query string.
///
/// Fail-closed on every branch: unauthenticated, missing claim, or a claim
/// value that doesn't parse as a Guid all return null, which the global EF
/// Core tenant query filter already treats as "match zero rows" — never
/// "match everything."
/// </summary>
public class ClaimsCurrentTenantAccessor(IHttpContextAccessor httpContextAccessor) : ICurrentTenantAccessor
{
    public Guid? BusinessId
    {
        get
        {
            var user = httpContextAccessor.HttpContext?.User;
            if (user?.Identity?.IsAuthenticated != true) return null;

            var claim = user.FindFirst(NivoraClaimTypes.BusinessId);
            if (claim is null) return null;

            return Guid.TryParse(claim.Value, out var businessId) ? businessId : null;
        }
    }
}
