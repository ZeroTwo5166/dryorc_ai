using System.Buffers.Text;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Security.Cryptography;
using backend.Auth;
using backend.Constants;
using backend.Data;
using backend.Email;
using backend.Enums;
using backend.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace backend.Services;

/// <summary>
/// The signed-in staff caller of an invitation operation, built by the
/// controller from the cookie principal (staff-invite design Section 6).
/// Principal is the controller's User; the service needs it for the
/// SameTenant check (implementation design G-1).
/// </summary>
public sealed record StaffCaller(Guid UserId, Guid BusinessId, bool IsOwner, bool IsAdmin, ClaimsPrincipal Principal);

/// <summary>One invitation as staff see it. No field reveals whether the email has an account (I6) or the token (I3).</summary>
public sealed record StaffInvitationResponse(
    Guid Id,
    string Email,
    string Role,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset LastSentAt);

public sealed record PreviewStaffInvitationResponse(string BusinessName, string Email, string Role);

public sealed record AcceptStaffInvitationResponse(string Email);

/// <summary>What an operation ended with; the controller maps it to a status code (design Section 8).</summary>
public enum StaffInvitationOutcome
{
    Ok,
    Created,
    NoContent,
    NotFound,
    InvalidLink,
    Forbidden,
    BadRequest,
    Conflict,
    TooManyRequests,
}

/// <summary>Created carries Invitation; BadRequest and Conflict carry Message.</summary>
public sealed record CreateResult(StaffInvitationOutcome Outcome, StaffInvitationResponse? Invitation = null, string? Message = null);

/// <summary>Ok carries Invitations.</summary>
public sealed record ListResult(StaffInvitationOutcome Outcome, IReadOnlyList<StaffInvitationResponse>? Invitations = null);

/// <summary>Ok carries Invitation.</summary>
public sealed record ResendResult(StaffInvitationOutcome Outcome, StaffInvitationResponse? Invitation = null);

public sealed record RevokeResult(StaffInvitationOutcome Outcome);

/// <summary>Ok carries Preview.</summary>
public sealed record PreviewResult(StaffInvitationOutcome Outcome, PreviewStaffInvitationResponse? Preview = null);

/// <summary>Ok carries Accepted; BadRequest carries Errors (Identity descriptions); Conflict carries Message.</summary>
public sealed record AcceptResult(
    StaffInvitationOutcome Outcome,
    AcceptStaffInvitationResponse? Accepted = null,
    IReadOnlyList<string>? Errors = null,
    string? Message = null);

/// <summary>
/// Staff invitations: create, list, resend, revoke (signed-in staff) and
/// preview, accept (anonymous token holder). Expected outcomes are returned
/// as results, never thrown. Thrown: an invalid Frontend:BaseUrl (D19, before
/// any mutation) and the "500" rows of the accept failure table.
/// </summary>
public interface IStaffInvitationService
{
    Task<CreateResult> CreateAsync(StaffCaller caller, string email, string role, CancellationToken ct);

    Task<ListResult> ListAsync(StaffCaller caller, CancellationToken ct);

    Task<ResendResult> ResendAsync(StaffCaller caller, Guid id, CancellationToken ct);

    Task<RevokeResult> RevokeAsync(StaffCaller caller, Guid id, CancellationToken ct);

    Task<PreviewResult> PreviewAsync(string token, CancellationToken ct);

    Task<AcceptResult> AcceptAsync(string token, string password, CancellationToken ct);
}

/// <summary>
/// Staff invitation flows (DEC D1, D3-D6, D8-D12, D15, D18, D19;
/// docs/handoffs/staff-invite-design.md Sections 5-7 and 11).
///
/// Tenant rules (D15): staff paths stay tenant-filtered and also name
/// caller.BusinessId explicitly. The anonymous paths (preview, accept) have
/// no tenant; they find the row through LoadByTokenHashAsync, the only
/// IgnoreQueryFilters site in this file, and every write takes BusinessId
/// from that row.
///
/// The raw token (I3) is a local of CreateAsync and ResendAsync, handed only
/// to the email builder; it is never stored, logged, audited or returned.
/// Emails go out after commit and a send failure never fails the request
/// (D8).
/// </summary>
public class StaffInvitationService(
    AppDbContext db,
    UserManager<ApplicationUser> userManager,
    IAuditLogService auditLogService,
    INotificationService notificationService,
    IEmailSender emailSender,
    IConfiguration configuration,
    IHostEnvironment environment,
    IOptions<StaffInviteOptions> options,
    IStaffInvitationAcceptFailurePoint failurePoint,
    IStaffInvitationLockCheckpoint lockCheckpoint,
    IAuthorizationService authorizationService,
    ILogger<StaffInvitationService> logger) : IStaffInvitationService
{
    /// <summary>D4: an invitation is valid for 7 days from create or the last resend. Fixed, not an option.</summary>
    private static readonly TimeSpan Expiry = TimeSpan.FromDays(7);

    private const int TokenBytes = 32;

    // Base64url without padding of 32 bytes.
    private const int TokenLength = 43;

    private const int MaxEmailLength = 256;

    public async Task<CreateResult> CreateAsync(StaffCaller caller, string email, string role, CancellationToken ct)
    {
        // Step 1: role matrix (D1).
        if (!caller.IsOwner && !caller.IsAdmin)
        {
            return new CreateResult(StaffInvitationOutcome.Forbidden);
        }

        if (role != RoleNames.Admin && role != RoleNames.Agent && role != RoleNames.Owner)
        {
            return new CreateResult(StaffInvitationOutcome.BadRequest, Message: "Role must be Admin or Agent.");
        }

        if (role == RoleNames.Owner || (role == RoleNames.Admin && !caller.IsOwner))
        {
            return new CreateResult(StaffInvitationOutcome.Forbidden);
        }

        // Step 2: email validation (ED-17).
        var trimmedEmail = (email ?? "").Trim();
        if (!IsAcceptableEmail(trimmedEmail))
        {
            return new CreateResult(StaffInvitationOutcome.BadRequest, Message: "Enter a valid email address.");
        }

        // Step 3: base URL before any mutation (D19); throws when invalid.
        var baseUri = StaffInvitationLinkBuilder.GetValidatedBaseUri(configuration, environment);

        // Step 4: the key Identity will use for NormalizedUserName (D5).
        var key = userManager.NormalizeName(trimmedEmail);

        // Step 5: account check (D3, ED-16).
        var existing = await userManager.FindByNameAsync(trimmedEmail);
        if (existing is not null && existing.BusinessId == caller.BusinessId)
        {
            return new CreateResult(StaffInvitationOutcome.Conflict, Message: "This email already belongs to a member of your business.");
        }

        var now = DateTimeOffset.UtcNow;
        StaffInvitation invitation;
        string token;

        // Step 6: cap check and insert serialized per business (D12).
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock({AdvisoryLockKeyGenerator.ForStaffInvitationCap(caller.BusinessId)})", ct);

            // Step 7: one row per business and key (D3).
            if (await db.StaffInvitations.AnyAsync(i => i.BusinessId == caller.BusinessId && i.NormalizedKey == key, ct))
            {
                await transaction.RollbackAsync(ct);
                return ExistingInvitationConflict();
            }

            // Step 8: cap over every retained row, pending and expired (D12).
            if (await db.StaffInvitations.CountAsync(i => i.BusinessId == caller.BusinessId, ct) >= options.Value.MaxRetainedPerBusiness)
            {
                await transaction.RollbackAsync(ct);
                return new CreateResult(StaffInvitationOutcome.Conflict, Message: "Your business has reached its invitation limit.");
            }

            // Step 9: token and row.
            byte[] tokenHash;
            (token, tokenHash) = NewToken();
            invitation = new StaffInvitation
            {
                Id = Guid.NewGuid(),
                BusinessId = caller.BusinessId,
                Email = trimmedEmail,
                NormalizedKey = key,
                Role = role,
                TokenHash = tokenHash,
                InvitedByUserId = caller.UserId,
                CreatedAt = now,
                LastSentAt = now,
                ExpiresAt = now + Expiry,
            };
            db.StaffInvitations.Add(invitation);

            // Step 10: audit, save, commit.
            await auditLogService.LogAsync(
                caller.BusinessId,
                AuditEntityTypes.StaffInvitation,
                invitation.Id.ToString(),
                AuditEventTypes.StaffInvitationCreated,
                ActorType.NivoraBusinessStaff,
                changedByUserId: caller.UserId,
                oldValue: null,
                newValue: StaffInvitationAuditPayload.Build(invitation.Role, StaffInvitationAuditPayload.Pending),
                cancellationToken: ct);

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException
            {
                SqlState: "23505",
                ConstraintName: "IX_StaffInvitations_BusinessId_NormalizedKey",
            })
            {
                // The advisory lock already prevents this race; the unique
                // index is the backstop.
                await transaction.RollbackAsync(ct);
                return ExistingInvitationConflict();
            }

            await transaction.CommitAsync(ct);
        }

        // Step 11: send after commit (D8). The account check decides the
        // template (D3); the response is the same either way (I6).
        var businessName = await GetBusinessNameAsync(caller.BusinessId, ct);
        var message = existing is not null
            ? StaffInvitationEmails.BuildAlreadyInUse(businessName)
            : StaffInvitationEmails.BuildJoinLink(businessName, invitation.Role, StaffInvitationLinkBuilder.BuildLink(baseUri, token));
        await SendInvitationEmailAsync(invitation.Id, invitation.Email, message.Subject, message.Html);

        // Step 12.
        return new CreateResult(StaffInvitationOutcome.Created, ToResponse(invitation, now));
    }

    public async Task<ListResult> ListAsync(StaffCaller caller, CancellationToken ct)
    {
        if (!caller.IsOwner && !caller.IsAdmin)
        {
            return new ListResult(StaffInvitationOutcome.Forbidden);
        }

        var now = DateTimeOffset.UtcNow;
        var rows = await db.StaffInvitations
            .Where(i => i.BusinessId == caller.BusinessId)
            .OrderByDescending(i => i.CreatedAt)
            .ToListAsync(ct);

        return new ListResult(StaffInvitationOutcome.Ok, rows.Select(r => ToResponse(r, now)).ToList());
    }

    public async Task<ResendResult> ResendAsync(StaffCaller caller, Guid id, CancellationToken ct)
    {
        // Step 1.
        if (!caller.IsOwner && !caller.IsAdmin)
        {
            return new ResendResult(StaffInvitationOutcome.Forbidden);
        }

        // Step 2: base URL before any mutation (D19); throws when invalid.
        var baseUri = StaffInvitationLinkBuilder.GetValidatedBaseUri(configuration, environment);

        var now = DateTimeOffset.UtcNow;
        var cooldown = TimeSpan.FromSeconds(options.Value.ResendCooldownSeconds);
        var newExpiresAt = now + Expiry;
        StaffInvitation row;
        string token;

        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            // Step 3: row lock (D10).
            var locked = await LockStaffRowAsync(caller, id, ct);
            await lockCheckpoint.AfterRowLockAsync(StaffInvitationLockOperation.Resend, ct);
            if (locked is null)
            {
                await transaction.RollbackAsync(ct);
                return new ResendResult(StaffInvitationOutcome.NotFound);
            }

            row = locked;

            // Step 4: SameTenant (G-1), then the D1 target rule.
            if (!(await authorizationService.AuthorizeAsync(caller.Principal, row, NivoraPolicies.SameTenant)).Succeeded)
            {
                await transaction.RollbackAsync(ct);
                return new ResendResult(StaffInvitationOutcome.NotFound);
            }

            if (!caller.IsOwner && row.Role != RoleNames.Agent)
            {
                await transaction.RollbackAsync(ct);
                return new ResendResult(StaffInvitationOutcome.Forbidden);
            }

            var statusBefore = StatusAt(row, now);

            // Step 5: new token and expiry in one UPDATE, refused inside the
            // cooldown (D4, D12).
            byte[] tokenHash;
            (token, tokenHash) = NewToken();
            var updated = await db.StaffInvitations
                .Where(i => i.Id == id && i.BusinessId == caller.BusinessId && i.LastSentAt <= now - cooldown)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(i => i.TokenHash, tokenHash)
                    .SetProperty(i => i.ExpiresAt, newExpiresAt)
                    .SetProperty(i => i.LastSentAt, now), ct);
            if (updated == 0)
            {
                await transaction.RollbackAsync(ct);
                return new ResendResult(StaffInvitationOutcome.TooManyRequests);
            }

            // Step 6.
            await auditLogService.LogAsync(
                row.BusinessId,
                AuditEntityTypes.StaffInvitation,
                row.Id.ToString(),
                AuditEventTypes.StaffInvitationResent,
                ActorType.NivoraBusinessStaff,
                changedByUserId: caller.UserId,
                oldValue: StaffInvitationAuditPayload.Build(row.Role, statusBefore),
                newValue: StaffInvitationAuditPayload.Build(row.Role, StaffInvitationAuditPayload.Pending),
                cancellationToken: ct);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }

        // Step 7: the account check is repeated at every send (D3).
        var existing = await userManager.FindByNameAsync(row.Email);
        var businessName = await GetBusinessNameAsync(row.BusinessId, ct);
        var message = existing is not null
            ? StaffInvitationEmails.BuildAlreadyInUse(businessName)
            : StaffInvitationEmails.BuildJoinLink(businessName, row.Role, StaffInvitationLinkBuilder.BuildLink(baseUri, token));
        await SendInvitationEmailAsync(row.Id, row.Email, message.Subject, message.Html);

        // Step 8: built from the values the UPDATE wrote, never from the
        // tracked row, which still holds the old ExpiresAt and LastSentAt
        // (ED-40).
        return new ResendResult(
            StaffInvitationOutcome.Ok,
            new StaffInvitationResponse(row.Id, row.Email, row.Role, StaffInvitationAuditPayload.Pending, row.CreatedAt, newExpiresAt, now));
    }

    public async Task<RevokeResult> RevokeAsync(StaffCaller caller, Guid id, CancellationToken ct)
    {
        if (!caller.IsOwner && !caller.IsAdmin)
        {
            return new RevokeResult(StaffInvitationOutcome.Forbidden);
        }

        var now = DateTimeOffset.UtcNow;

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var row = await LockStaffRowAsync(caller, id, ct);
        await lockCheckpoint.AfterRowLockAsync(StaffInvitationLockOperation.Revoke, ct);
        if (row is null)
        {
            await transaction.RollbackAsync(ct);
            return new RevokeResult(StaffInvitationOutcome.NotFound);
        }

        if (!(await authorizationService.AuthorizeAsync(caller.Principal, row, NivoraPolicies.SameTenant)).Succeeded)
        {
            await transaction.RollbackAsync(ct);
            return new RevokeResult(StaffInvitationOutcome.NotFound);
        }

        if (!caller.IsOwner && row.Role != RoleNames.Agent)
        {
            await transaction.RollbackAsync(ct);
            return new RevokeResult(StaffInvitationOutcome.Forbidden);
        }

        // D9: revoke deletes the row at once.
        db.StaffInvitations.Remove(row);
        await auditLogService.LogAsync(
            row.BusinessId,
            AuditEntityTypes.StaffInvitation,
            row.Id.ToString(),
            AuditEventTypes.StaffInvitationRevoked,
            ActorType.NivoraBusinessStaff,
            changedByUserId: caller.UserId,
            oldValue: StaffInvitationAuditPayload.Build(row.Role, StatusAt(row, now)),
            newValue: StaffInvitationAuditPayload.Build(row.Role, StaffInvitationAuditPayload.Revoked),
            cancellationToken: ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return new RevokeResult(StaffInvitationOutcome.NoContent);
    }

    public async Task<PreviewResult> PreviewAsync(string token, CancellationToken ct)
    {
        var tokenHash = TryHashToken(token);
        if (tokenHash is null)
        {
            return new PreviewResult(StaffInvitationOutcome.InvalidLink);
        }

        var row = await LoadByTokenHashAsync(tokenHash, forUpdate: false, ct);
        if (row is null || row.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            return new PreviewResult(StaffInvitationOutcome.InvalidLink);
        }

        var businessName = await GetBusinessNameAsync(row.BusinessId, ct);
        return new PreviewResult(StaffInvitationOutcome.Ok, new PreviewStaffInvitationResponse(businessName, row.Email, row.Role));
    }

    /// <summary>
    /// The accept transaction, design Section 7 steps 1-17: one transaction
    /// holds the row lock, the TeamMemberJoined row, the user, its role, the
    /// row delete and the audit row, so it ends with all of them or with
    /// nothing changed (I1).
    /// </summary>
    public async Task<AcceptResult> AcceptAsync(string token, string password, CancellationToken ct)
    {
        // Step 1: no database access for a malformed token.
        var tokenHash = TryHashToken(token);
        if (tokenHash is null)
        {
            return new AcceptResult(StaffInvitationOutcome.InvalidLink);
        }

        var now = DateTimeOffset.UtcNow;

        // Step 2.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            // Step 3: row lock (D10, D15 site 1).
            var row = await LoadByTokenHashAsync(tokenHash, forUpdate: true, ct);
            await lockCheckpoint.AfterRowLockAsync(StaffInvitationLockOperation.Accept, ct);

            // Step 4: validity re-check under the lock.
            if (row is null || row.ExpiresAt <= now)
            {
                await transaction.RollbackAsync(ct);
                return new AcceptResult(StaffInvitationOutcome.InvalidLink);
            }

            // Step 5: no Business.Status rule exists (D21) and the inviter is
            // not re-checked (D22).

            // Step 6: Identity's password rules (D6).
            var candidate = new ApplicationUser { BusinessId = row.BusinessId, UserName = row.Email, Email = row.Email };
            var passwordErrors = new List<string>();
            foreach (var validator in userManager.PasswordValidators)
            {
                var result = await validator.ValidateAsync(userManager, candidate, password);
                if (!result.Succeeded)
                {
                    passwordErrors.AddRange(result.Errors.Select(e => e.Description));
                }
            }

            if (passwordErrors.Count > 0)
            {
                await transaction.RollbackAsync(ct);
                return new AcceptResult(StaffInvitationOutcome.BadRequest, Errors: passwordErrors);
            }

            // Step 7: first acceptance wins (D3, ED-20).
            if (await userManager.FindByNameAsync(row.Email) is not null)
            {
                await transaction.RollbackAsync(ct);
                return AccountExistsConflict();
            }

            // Step 8: TeamMemberJoined, written in this transaction and
            // before the new user exists, so the new member is not a
            // recipient (D18, ED-18).
            await notificationService.NotifyAsync(
                row.BusinessId,
                NotificationTypes.TeamMemberJoined,
                new TeamMemberJoinedArgs(),
                ActorType.NivoraSystem,
                cancellationToken: ct);

            // Step 9: the user, BusinessId from the row (D6, D15).
            var user = new ApplicationUser
            {
                Id = Guid.NewGuid(),
                BusinessId = row.BusinessId,
                UserName = row.Email,
                Email = row.Email,
                EmailConfirmed = true,
                IsActive = true,
                CreatedAt = now,
            };

            IdentityResult createResult;
            try
            {
                createResult = await userManager.CreateAsync(user, password);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
            {
                // UserNameIndex: another acceptance or a Register won (D3, D6).
                await transaction.RollbackAsync(ct);
                return AccountExistsConflict();
            }

            if (!createResult.Succeeded)
            {
                await transaction.RollbackAsync(ct);
                if (createResult.Errors.Any(e => e.Code == "DuplicateUserName"))
                {
                    // Identity's own duplicate check saw the account first (ED-19).
                    return AccountExistsConflict();
                }

                return new AcceptResult(StaffInvitationOutcome.BadRequest, Errors: createResult.Errors.Select(e => e.Description).ToList());
            }

            // Step 10.
            var roleResult = await userManager.AddToRoleAsync(user, row.Role);
            if (!roleResult.Succeeded)
            {
                throw new InvalidOperationException("Failed to assign the invited role to the new user.");
            }

            // Step 11.
            await failurePoint.AfterUserCreatedAsync(ct);

            // Step 12: the invitation is used up (D6, D9).
            db.StaffInvitations.Remove(row);

            // Step 13: the user row exists already, so the audit FK holds (V3).
            await auditLogService.LogAsync(
                row.BusinessId,
                AuditEntityTypes.StaffInvitation,
                row.Id.ToString(),
                AuditEventTypes.StaffInvitationAccepted,
                ActorType.NivoraBusinessStaff,
                changedByUserId: user.Id,
                oldValue: StaffInvitationAuditPayload.Build(row.Role, StaffInvitationAuditPayload.Pending),
                newValue: StaffInvitationAuditPayload.Build(row.Role, StaffInvitationAuditPayload.Accepted),
                cancellationToken: ct);

            // Steps 14 and 15.
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            // Steps 16 and 17: nothing to push, no email, no sign-in.
            return new AcceptResult(StaffInvitationOutcome.Ok, new AcceptStaffInvitationResponse(row.Email));
        }
        catch
        {
            // Step 15: any failure leaves nothing behind (I1).
            if (db.Database.CurrentTransaction is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }

            throw;
        }
    }

    /// <summary>
    /// The one request-path IgnoreQueryFilters site for invitations (DEC D15
    /// site 1, I2). Preview and accept are anonymous and have no tenant, so
    /// the tenant filter would match nothing; the hash of a valid token, not
    /// a tenant, is what proves the caller may read this row, and the row's
    /// own BusinessId is used for every write that follows. Never call this
    /// from a staff path. forUpdate selects the locking variant used by
    /// accept (D10); preview reads without a lock. ToListAsync, not
    /// FirstOrDefaultAsync, so EF adds no LIMIT around the raw SQL; TokenHash
    /// is unique, so at most one row comes back.
    /// </summary>
    private async Task<StaffInvitation?> LoadByTokenHashAsync(byte[] tokenHash, bool forUpdate, CancellationToken ct)
    {
        var query = forUpdate
            ? db.StaffInvitations.FromSql($"SELECT * FROM \"StaffInvitations\" WHERE \"TokenHash\" = {tokenHash} FOR UPDATE")
            : db.StaffInvitations.FromSql($"SELECT * FROM \"StaffInvitations\" WHERE \"TokenHash\" = {tokenHash}");
        var rows = await query.IgnoreQueryFilters().AsTracking().ToListAsync(ct);
        return rows.SingleOrDefault();
    }

    /// <summary>
    /// Staff row lock for resend and revoke (D10). The tenant filter stays on
    /// and BusinessId is also named explicitly (D15).
    /// </summary>
    private async Task<StaffInvitation?> LockStaffRowAsync(StaffCaller caller, Guid id, CancellationToken ct)
    {
        var rows = await db.StaffInvitations
            .FromSql($"SELECT * FROM \"StaffInvitations\" WHERE \"Id\" = {id} AND \"BusinessId\" = {caller.BusinessId} FOR UPDATE")
            .AsTracking()
            .ToListAsync(ct);
        return rows.SingleOrDefault();
    }

    /// <summary>
    /// Mirrors AuthController's send helpers: a failed send is logged with
    /// the invitation id only (never the address or the link) and never
    /// fails the request; resend recovers it (D8).
    /// </summary>
    private async Task SendInvitationEmailAsync(Guid invitationId, string to, string subject, string html)
    {
        try
        {
            await emailSender.SendEmailAsync(to, subject, html);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send staff invitation email for invitation {InvitationId}.", invitationId);
        }
    }

    private async Task<string> GetBusinessNameAsync(Guid businessId, CancellationToken ct)
    {
        var business = await db.Businesses.FindAsync([businessId], ct)
            ?? throw new InvalidOperationException("The invitation's business does not exist.");
        return business.Name;
    }

    private bool IsAcceptableEmail(string email)
    {
        if (email.Length == 0 || email.Length > MaxEmailLength || !new EmailAddressAttribute().IsValid(email))
        {
            return false;
        }

        // An address Identity would refuse as a UserName could never be accepted.
        var allowed = userManager.Options.User.AllowedUserNameCharacters;
        return string.IsNullOrEmpty(allowed) || email.All(allowed.Contains);
    }

    /// <summary>D4: 32 random bytes, base64url without padding; only the SHA-256 of the raw bytes is kept.</summary>
    private static (string Token, byte[] TokenHash) NewToken()
    {
        var raw = RandomNumberGenerator.GetBytes(TokenBytes);
        try
        {
            return (Base64Url.EncodeToString(raw), SHA256.HashData(raw));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(raw);
        }
    }

    /// <summary>The hash of a well-formed token, or null (ED-12): exactly 43 characters decoding to exactly 32 bytes.</summary>
    private static byte[]? TryHashToken(string? token)
    {
        if (token is null || token.Length != TokenLength)
        {
            return null;
        }

        var raw = new byte[TokenBytes];
        try
        {
            if (!Base64Url.TryDecodeFromChars(token, raw, out var written) || written != TokenBytes)
            {
                return null;
            }

            return SHA256.HashData(raw);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(raw);
        }
    }

    private static string StatusAt(StaffInvitation row, DateTimeOffset now) =>
        row.ExpiresAt > now ? StaffInvitationAuditPayload.Pending : StaffInvitationAuditPayload.Expired;

    private static StaffInvitationResponse ToResponse(StaffInvitation row, DateTimeOffset now) =>
        new(row.Id, row.Email, row.Role, StatusAt(row, now), row.CreatedAt, row.ExpiresAt, row.LastSentAt);

    private static CreateResult ExistingInvitationConflict() =>
        new(StaffInvitationOutcome.Conflict, Message: "An invitation for this email already exists. Resend it instead.");

    private static AcceptResult AccountExistsConflict() =>
        new(StaffInvitationOutcome.Conflict, Message: "An account with this email already exists.");
}
