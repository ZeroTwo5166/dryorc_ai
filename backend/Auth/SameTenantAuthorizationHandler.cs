using backend.Interfaces;
using Microsoft.AspNetCore.Authorization;

namespace backend.Auth;

/// <summary>
/// Resource-based authorization requirement: the resource being acted on
/// must belong to the authenticated user's own BusinessId. This is separate
/// from, and deliberately not replaced by, the EF Core global query
/// filter — the filter is read-time defense-in-depth (it structurally
/// prevents a filtered query from ever returning another tenant's row),
/// but it does not protect a resource ID accepted as a route/body
/// parameter from being checked against the wrong field, doesn't cover
/// any query built with IgnoreQueryFilters(), and says nothing about
/// writes. This handler is the explicit, auditable "does this specific
/// already-loaded resource belong to this business" check called out in
/// NIVORA_DEVELOPMENT_PLAN_V2.md Section 3.
/// </summary>
public class SameTenantRequirement : IAuthorizationRequirement;

public class SameTenantAuthorizationHandler(ICurrentTenantAccessor tenantAccessor)
    : AuthorizationHandler<SameTenantRequirement, ITenantOwned>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        SameTenantRequirement requirement,
        ITenantOwned resource)
    {
        var currentBusinessId = tenantAccessor.BusinessId;
        if (currentBusinessId is not null && resource.BusinessId == currentBusinessId)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
