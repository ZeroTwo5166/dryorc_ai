using System.Net;
using System.Security.Claims;
using backend.Auth;
using backend.Data;
using backend.Email;
using backend.Hubs;
using backend.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace backend.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController(
    AppDbContext db,
    UserManager<ApplicationUser> userManager,
    SignInManager<ApplicationUser> signInManager,
    IEmailSender emailSender,
    IConfiguration configuration,
    IHubContext<TicketHub> ticketHubContext,
    ILogger<AuthController> logger) : ControllerBase
{
    /// <summary>
    /// Generates a fresh email-confirmation token and emails the verification
    /// link. Called right after registration and from /resend-verification —
    /// deliberately never allowed to fail the caller's request: an account
    /// that exists but whose confirmation email bounced is recoverable via
    /// resend, but a registration that rolled back because Resend had a
    /// transient outage would not be.
    /// </summary>
    private async Task SendVerificationEmailAsync(ApplicationUser user)
    {
        try
        {
            var token = await userManager.GenerateEmailConfirmationTokenAsync(user);
            var frontendBaseUrl = configuration["Frontend:BaseUrl"] ?? "http://localhost:3000";
            var confirmUrl =
                $"{frontendBaseUrl}/verify-email?userId={user.Id}" +
                $"&token={WebUtility.UrlEncode(token)}" +
                $"&email={WebUtility.UrlEncode(user.Email)}";

            var html = $"""
                <p>Welcome to Nivora — confirm your email to activate your account.</p>
                <p><a href="{confirmUrl}">Verify your email</a></p>
                <p>If you didn't request this, you can ignore this email.</p>
                """;

            await emailSender.SendEmailAsync(user.Email!, "Verify your Nivora account", html);
        }
        catch (Exception ex)
        {
            // Never let an email-provider/token failure surface as an error
            // from a caller (Register/ResendVerification) whose own DB work
            // already succeeded — the account is real either way; a failed
            // send is recoverable via /resend-verification, a failed
            // registration would not be.
            logger.LogError(ex, "Failed to send verification email to user {UserId}.", user.Id);
        }
    }

    /// <summary>
    /// Generates a fresh password-reset token and emails the reset link.
    /// Same never-fail-the-caller rule as <see cref="SendVerificationEmailAsync"/>:
    /// ForgotPassword always responds 204 regardless of what happens here, so a
    /// transient Resend outage must not surface as an error — it's silently
    /// unrecoverable for that one request either way, and logging is all a
    /// caller-visible failure here could add.
    /// </summary>
    private async Task SendPasswordResetEmailAsync(ApplicationUser user)
    {
        try
        {
            var token = await userManager.GeneratePasswordResetTokenAsync(user);
            var frontendBaseUrl = configuration["Frontend:BaseUrl"] ?? "http://localhost:3000";
            var resetUrl =
                $"{frontendBaseUrl}/reset-password?userId={user.Id}" +
                $"&token={WebUtility.UrlEncode(token)}";

            var html = $"""
                <p>We received a request to reset the password for your Nivora account.</p>
                <p><a href="{resetUrl}">Reset your password</a></p>
                <p>This link will expire shortly. If you didn't request this, you can safely ignore this email — your password won't be changed.</p>
                """;

            await emailSender.SendEmailAsync(user.Email!, "Reset your Nivora password", html);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send password reset email to user {UserId}.", user.Id);
        }
    }

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
            // Stays false until the /confirm-email link is clicked —
            // RequireConfirmedEmail (Program.cs) blocks PasswordSignInAsync
            // for this user until then.
            EmailConfirmed = false,
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

        // Outside the transaction and after commit, deliberately — never hold
        // a DB transaction open across an external HTTP call.
        await SendVerificationEmailAsync(user);

        return Ok(new RegisterResponse(user.Id, business.Id, user.Email));
    }

    /// <summary>
    /// Confirms a staff account's email from the link sent by
    /// SendVerificationEmailAsync. Anonymous — the token itself (Identity's
    /// signed, time-limited data-protection token) is the credential here,
    /// not a signed-in session.
    /// </summary>
    [HttpPost("confirm-email")]
    [AllowAnonymous]
    public async Task<IActionResult> ConfirmEmail([FromBody] ConfirmEmailRequest request)
    {
        var user = await userManager.FindByIdAsync(request.UserId.ToString());
        if (user is null)
        {
            return BadRequest("This verification link is invalid.");
        }

        if (user.EmailConfirmed)
        {
            return NoContent();
        }

        var result = await userManager.ConfirmEmailAsync(user, request.Token);
        if (!result.Succeeded)
        {
            return BadRequest("This verification link is invalid or has expired.");
        }

        return NoContent();
    }

    /// <summary>
    /// Re-sends the verification email — the recovery path for a lost/expired
    /// link or an email that never arrived. Always responds 200 regardless of
    /// whether the address is registered or already verified, so this can't
    /// be used to enumerate staff accounts.
    /// </summary>
    [HttpPost("resend-verification")]
    [AllowAnonymous]
    public async Task<IActionResult> ResendVerification([FromBody] ResendVerificationRequest request)
    {
        // FindByNameAsync, not FindByEmailAsync — this app signs in by
        // username with UserName == Email (see TenantAwareUserStore), and
        // only FindByNameAsync has the anonymous tenant-filter bypass this
        // unauthenticated endpoint needs.
        var user = await userManager.FindByNameAsync(request.Email);
        if (user is not null && !user.EmailConfirmed)
        {
            await SendVerificationEmailAsync(user);
        }

        return NoContent();
    }

    /// <summary>
    /// Starts the forgot-password flow. Always responds 204 regardless of
    /// whether the address is registered — same anti-enumeration rule as
    /// /resend-verification — so this can never be used to confirm whether a
    /// given email has a Nivora account.
    /// </summary>
    [HttpPost("forgot-password")]
    [AllowAnonymous]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequest request)
    {
        // FindByNameAsync, not FindByEmailAsync — same reasoning as
        // ResendVerification: UserName == Email, and only FindByNameAsync has
        // the anonymous tenant-filter bypass this unauthenticated endpoint needs.
        var user = await userManager.FindByNameAsync(request.Email);
        if (user is not null)
        {
            await SendPasswordResetEmailAsync(user);
        }

        return NoContent();
    }

    /// <summary>
    /// Completes the forgot-password flow from the link sent by
    /// SendPasswordResetEmailAsync. Anonymous — the token itself is the
    /// credential here, not a signed-in session. Resetting the password
    /// rotates the user's security stamp (Identity's own behavior), which
    /// invalidates any sessions signed in before the reset.
    /// </summary>
    [HttpPost("reset-password")]
    [AllowAnonymous]
    public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequest request)
    {
        // FindByIdAsync — same chicken-and-egg tenant-filter bypass as
        // ConfirmEmail: the caller has no session/tenant cookie yet.
        var user = await userManager.FindByIdAsync(request.UserId.ToString());
        if (user is null)
        {
            return BadRequest("This password reset link is invalid or has expired.");
        }

        var result = await userManager.ResetPasswordAsync(user, request.Token, request.NewPassword);
        if (!result.Succeeded)
        {
            if (result.Errors.Any(e => e.Code == "InvalidToken"))
            {
                return BadRequest("This password reset link is invalid or has expired.");
            }

            return BadRequest(result.Errors.Select(e => e.Description));
        }

        return NoContent();
    }

    /// <summary>
    /// Cookie sign-in. Goes through SignInManager.PasswordSignInAsync, which
    /// resolves the user via UserManager.FindByNameAsync — routed to
    /// TenantAwareUserStore.FindByNameAsync, one of this codebase's
    /// deliberate, documented IgnoreQueryFilters() sites (see that class for
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

        if (result.IsNotAllowed)
        {
            // RequireConfirmedEmail (Program.cs) — the credentials may well be
            // correct, but PreSignInCheck blocks sign-in until EmailConfirmed
            // is true. 403, not 401: this is "who you are is known and your
            // account exists," not "invalid credentials."
            return StatusCode(StatusCodes.Status403Forbidden, "Please verify your email before signing in.");
        }

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
        // Captured before SignOutAsync() — same ClaimTypes.NameIdentifier
        // convention every controller already uses for the acting user, and
        // the value SignalR's own default IUserIdProvider groups
        // connections by, so Clients.User(...) below reaches this staff
        // member's already-open TicketHub connection(s) with no custom
        // IUserIdProvider needed.
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        await signInManager.SignOutAsync();

        if (userId is not null)
        {
            try
            {
                await ticketHubContext.Clients.User(userId).SendAsync("ForceDisconnect");
            }
            catch (Exception ex)
            {
                // Best-effort, same as TicketsController's own broadcast
                // failures — a SignalR delivery problem must never turn an
                // otherwise-successful logout into an error response.
                logger.LogError(ex, "Failed to send ForceDisconnect to user {UserId} on logout.", userId);
            }
        }

        return NoContent();
    }

    /// <summary>
    /// Returns the currently signed-in staff user, for the dashboard to
    /// confirm session state on load (e.g. the post-login home page) without
    /// guessing from cookie presence alone. Tenant-scoped like everything
    /// else: Business has no query filter; the lookup is keyed by the
    /// signed-in user's own BusinessId.
    /// </summary>
    [HttpGet("me")]
    [Authorize(Policy = NivoraPolicies.AnyStaff)]
    public async Task<IActionResult> Me()
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
        {
            return Unauthorized();
        }

        var business = await db.Businesses.FindAsync(user.BusinessId);
        if (business is null)
        {
            return Unauthorized();
        }

        var roles = await userManager.GetRolesAsync(user);

        return Ok(new MeResponse(
            user.Id, user.BusinessId, user.Email!, business.Name, business.PlanTier, roles.ToList(),
            await GetSessionExpiresAtUtcAsync(), business.CancelOrderToolEnabled, business.AiBusinessAnalystEnabled,
            business.ScheduledReportsEnabled));
    }

    /// <summary>
    /// Extends an already-authenticated session by exactly 2 hours from now
    /// — absolute, not sliding, same posture as the initial 8-hour login
    /// cookie (SlidingExpiration = false in Program.cs applies to every
    /// ticket this handler issues, this one included). Can only ever extend
    /// a session that already passed cookie auth — [Authorize] rejects an
    /// unauthenticated caller with 401 before this method body ever runs, so
    /// there's no path for a client to create or extend a session it didn't
    /// already hold.
    /// </summary>
    [HttpPost("extend-session")]
    [Authorize(Policy = NivoraPolicies.AnyStaff)]
    public async Task<IActionResult> ExtendSession()
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
        {
            return Unauthorized();
        }

        // Preserve the current cookie's IsPersistent (the "remember me" the
        // user chose at login) rather than silently changing it here — this
        // endpoint's job is only to move the expiry, nothing else about the
        // session's shape.
        var currentTicket = await HttpContext.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        var isPersistent = currentTicket.Properties?.IsPersistent ?? true;

        var expiresAtUtc = DateTimeOffset.UtcNow.AddHours(2);
        await HttpContext.SignInAsync(
            IdentityConstants.ApplicationScheme,
            User,
            new AuthenticationProperties
            {
                IsPersistent = isPersistent,
                ExpiresUtc = expiresAtUtc,
            });

        return Ok(new ExtendSessionResponse(expiresAtUtc));
    }

    /// <summary>
    /// Reads back the ExpiresUtc actually committed to the current request's
    /// authentication ticket — whether that ticket came from the original
    /// login (auto-computed by the cookie handler as IssuedUtc + the 8h
    /// ExpireTimeSpan, since Login never sets ExpiresUtc explicitly) or from
    /// ExtendSession above (explicitly set to +2h). Reading it back here
    /// rather than recomputing it is what makes the server the single source
    /// of truth the frontend's expiry warning is built on.
    /// </summary>
    private async Task<DateTimeOffset> GetSessionExpiresAtUtcAsync()
    {
        var result = await HttpContext.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        return result.Properties?.ExpiresUtc ?? DateTimeOffset.UtcNow.AddHours(8);
    }
}
