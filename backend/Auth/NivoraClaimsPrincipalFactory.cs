using System.Security.Claims;
using backend.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace backend.Auth;

/// <summary>
/// Embeds the signed-in user's BusinessId as a claim in the cookie-backed
/// principal, so <see cref="ClaimsCurrentTenantAccessor"/> can resolve the
/// tenant from <c>HttpContext.User</c> on every subsequent request without a
/// database hit and without ever trusting anything client-supplied. Identity
/// calls this factory again during <c>SecurityStampValidator</c>'s periodic
/// re-validation of an existing cookie, so the claim stays correct for the
/// lifetime of the session on the same schedule as the rest of the principal.
/// </summary>
public class NivoraClaimsPrincipalFactory(
    UserManager<ApplicationUser> userManager,
    RoleManager<IdentityRole<Guid>> roleManager,
    IOptions<IdentityOptions> optionsAccessor)
    : UserClaimsPrincipalFactory<ApplicationUser, IdentityRole<Guid>>(userManager, roleManager, optionsAccessor)
{
    public override async Task<ClaimsPrincipal> CreateAsync(ApplicationUser user)
    {
        var principal = await base.CreateAsync(user);
        ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim(NivoraClaimTypes.BusinessId, user.BusinessId.ToString()));
        return principal;
    }
}
