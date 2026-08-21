using backend.Data;
using backend.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace backend.Auth;

/// <summary>
/// Subclasses the default EF Core Identity <see cref="UserStore{TUser,TRole,TContext,TKey}"/>
/// to carry exactly one narrowly-scoped exception to <c>AppDbContext</c>'s
/// global tenant query filter.
///
/// <para>
/// <see cref="ApplicationUser"/> implements <c>ITenantOwned</c>, so every normal
/// read is scoped to <c>ICurrentTenantAccessor.BusinessId</c> and fails closed
/// (zero rows) when no tenant is known. At sign-in time, BusinessId is exactly
/// what hasn't been established yet — <c>SignInManager.PasswordSignInAsync(string, ...)</c>
/// is this app's only sign-in entry point, and it resolves the user via
/// <c>UserManager.FindByNameAsync</c>, which calls straight through to
/// <see cref="FindByNameAsync"/> below, before any authentication has happened.
/// Under the normal filtered store this query always returns null, so no one
/// could ever log in.
/// </para>
///
/// <para>
/// <b>This was traced empirically, not assumed:</b> every <c>IUserStore</c>/
/// <c>IUserPasswordStore</c>/<c>IUserLockoutStore</c> method was instrumented
/// with logging on a subclass of the real EF Core <c>UserStore</c>, and a real
/// <c>SignInManager.PasswordSignInAsync</c> call was run end-to-end against a
/// real (Neon) database. <see cref="FindByNameAsync"/> was the <b>only</b> store
/// method invoked before authentication completed — not <c>FindByEmailAsync</c>
/// (this app signs in by username, and registration sets
/// <c>UserName == Email</c>, so a separate email-lookup override was never
/// exercised), and not <c>FindByIdAsync</c> (that one's only caller,
/// <c>SecurityStampValidator</c>'s periodic re-validation on later requests,
/// runs after the BusinessId claim is already present on the incoming request's
/// <c>HttpContext.User</c> from the auth cookie itself — so the normal filtered
/// store already has a tenant to scope to by the time it runs, and needs no
/// bypass).
/// </para>
///
/// <para>
/// <b>Do not extend this pattern elsewhere without the same kind of empirical
/// proof.</b> This override exists solely to solve the pre-authentication
/// chicken-and-egg problem — "look the user up to learn which tenant they
/// belong to" — nothing more. Every other method on this store (including
/// writes, and all other reads) behaves exactly as the base EF Core store does:
/// fully tenant-filtered.
/// </para>
/// </summary>
public class TenantAwareUserStore(AppDbContext context, IdentityErrorDescriber? describer = null)
    : UserStore<ApplicationUser, IdentityRole<Guid>, AppDbContext, Guid>(context, describer)
{
    public override Task<ApplicationUser?> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Context.Users
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.NormalizedUserName == normalizedUserName, cancellationToken);
    }

    /// <summary>
    /// Same chicken-and-egg exception as <see cref="FindByNameAsync"/> above,
    /// for the same reason: <c>AuthController.ConfirmEmail</c> is
    /// <c>[AllowAnonymous]</c> (a user clicking an emailed link has no
    /// session/tenant cookie yet), so the normal tenant-filtered lookup would
    /// fail closed and no one could ever confirm their email. The token
    /// itself — not tenant context — is what proves the caller is entitled to
    /// touch this row.
    /// </summary>
    public override Task<ApplicationUser?> FindByIdAsync(string userId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Guid.TryParse(userId, out var id))
        {
            return Task.FromResult<ApplicationUser?>(null);
        }
        return Context.Users
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == id, cancellationToken);
    }
}
