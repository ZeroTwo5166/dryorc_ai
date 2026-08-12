namespace backend.Auth;

/// <summary>
/// Named authorization policies, registered in Program.cs via
/// AddAuthorizationBuilder(). Role-tier policies cover "does this user's
/// role permit the operation" (step 4 of the auth checklist below).
/// SameTenant covers step 3 ("does the requested resource belong to
/// this user's business") — see SameTenantRequirement/Handler.
///
/// The 4-step checklist every protected operation must satisfy
/// (authentication answers only #1 — the rest is authorization,
/// deliberately kept separate):
///   1. Who is the authenticated user?            -> [Authorize] / cookie auth
///   2. Which BusinessId do they belong to?        -> ICurrentTenantAccessor
///   3. Does the resource belong to that Business?  -> SameTenant policy
///   4. Does their role permit this operation?      -> the role-tier policies below
/// </summary>
public static class NivoraPolicies
{
    /// <summary>Any authenticated staff member, any role.</summary>
    public const string AnyStaff = "AnyStaff";

    /// <summary>Owner or Admin only — Agent excluded.</summary>
    public const string OwnerOrAdmin = "OwnerOrAdmin";

    /// <summary>Owner only.</summary>
    public const string OwnerOnly = "OwnerOnly";

    /// <summary>
    /// Resource-based policy (see SameTenantRequirement/Handler): the loaded
    /// resource's BusinessId must match the authenticated user's
    /// BusinessId. Not declarative — must be invoked explicitly via
    /// IAuthorizationService.AuthorizeAsync(User, resource, SameTenant)
    /// after the resource has been loaded, since ASP.NET Core's
    /// declarative [Authorize] attribute has no way to know about a
    /// specific entity instance ahead of the action running.
    /// </summary>
    public const string SameTenant = "SameTenant";
}
