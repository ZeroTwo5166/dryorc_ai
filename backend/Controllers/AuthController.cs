using backend.Auth;
using backend.Data;
using backend.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace backend.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController(
    AppDbContext db,
    UserManager<ApplicationUser> userManager,
    SignInManager<ApplicationUser> signInManager) : ControllerBase
{
    /// <summary>
    /// Creates a new Business and its first staff user (role: Owner) in one
    /// atomic transaction. Anonymous — this is how a business first joins
    /// Nivora. Does not sign the caller in; call /login afterward.
    /// </summary>
    [HttpPost("register")]
    [AllowAnonymous]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.BusinessName) ||
            string.IsNullOrWhiteSpace(request.TimeZone) ||
            string.IsNullOrWhiteSpace(request.Email) ||
            string.IsNullOrWhiteSpace(request.Password))
        {
            return BadRequest("BusinessName, TimeZone, Email, and Password are all required.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync();

        var business = new Business
        {
            Id = Guid.NewGuid(),
            Name = request.BusinessName,
            TimeZone = request.TimeZone,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Businesses.Add(business);
        await db.SaveChangesAsync();

        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id,
            UserName = request.Email,
            Email = request.Email,
            // No email-confirmation flow exists in Step 4's scope — treat
            // every registered account as immediately usable.
            EmailConfirmed = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        IdentityResult createResult;
        try
        {
            createResult = await userManager.CreateAsync(user, request.Password);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            // The tenant filter blinds UserManager's own built-in duplicate-
            // email pre-check while anonymous (no BusinessId known yet), so
            // a genuine collision only surfaces here, at the database's own
            // unique index — not a bug, the DB constraint is the real
            // backstop, this is just translating it to a clean response.
            await transaction.RollbackAsync();
            return Conflict("A staff account with this email already exists.");
        }

        if (!createResult.Succeeded)
        {
            await transaction.RollbackAsync();
            return BadRequest(createResult.Errors.Select(e => e.Description));
        }

        var roleResult = await userManager.AddToRoleAsync(user, RoleNames.Owner);
        if (!roleResult.Succeeded)
        {
            await transaction.RollbackAsync();
            return StatusCode(500, "Failed to assign the Owner role to the new user.");
        }

        await transaction.CommitAsync();

        return Ok(new RegisterResponse(user.Id, business.Id, user.Email));
    }

    /// <summary>
    /// Cookie sign-in. Goes through SignInManager.PasswordSignInAsync, which
    /// resolves the user via UserManager.FindByNameAsync — routed to
    /// TenantAwareUserStore.FindByNameAsync, the one deliberate
    /// IgnoreQueryFilters() exception in this codebase (see that class for
    /// why). On success, NivoraClaimsPrincipalFactory embeds BusinessId into
    /// the auth cookie so ICurrentTenantAccessor can resolve the tenant on
    /// every later request without a DB hit.
    /// </summary>
    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        // Exactly one store lookup, by design: FindByNameAsync (routed to
        // TenantAwareUserStore's override) is called here, once, explicitly
        // — then the TUser-object overload of PasswordSignInAsync is used
        // below so Identity never needs a second, redundant lookup.
        var user = await userManager.FindByNameAsync(request.Email);
        if (user is null)
        {
            return Unauthorized();
        }

        var result = await signInManager.PasswordSignInAsync(
            user, request.Password, request.RememberMe, lockoutOnFailure: true);

        if (!result.Succeeded)
        {
            return Unauthorized();
        }

        var roles = await userManager.GetRolesAsync(user);

        return Ok(new LoginResponse(user.Id, user.BusinessId, user.Email!, roles.ToList()));
    }

    [HttpPost("logout")]
    [Authorize(Policy = NivoraPolicies.AnyStaff)]
    public async Task<IActionResult> Logout()
    {
        await signInManager.SignOutAsync();
        return NoContent();
    }
}
