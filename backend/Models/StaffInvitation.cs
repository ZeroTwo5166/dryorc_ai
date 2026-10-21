using backend.Interfaces;

namespace backend.Models;

/// <summary>
/// A pending staff invitation into one business (staff invite, see
/// docs/handoffs/staff-invite-design.md Section 2). A row exists only while
/// the invitation is pending or expired: accepting or revoking it deletes
/// the row, so there are no accepted/revoked columns. The raw invitation
/// token is never stored, only its SHA-256 hash in <see cref="TokenHash"/>.
/// </summary>
public class StaffInvitation : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid BusinessId { get; set; }

    /// <summary>Invitee email as entered, trimmed; becomes the user's UserName and Email at acceptance.</summary>
    public required string Email { get; set; }

    /// <summary><c>userManager.NormalizeName(Email)</c>; unique per business.</summary>
    public required string NormalizedKey { get; set; }

    /// <summary><see cref="backend.Auth.RoleNames.Admin"/> or <see cref="backend.Auth.RoleNames.Agent"/>, never Owner.</summary>
    public required string Role { get; set; }

    /// <summary>SHA-256 over the 32 raw token bytes; always exactly 32 bytes.</summary>
    public required byte[] TokenHash { get; set; }

    /// <summary>The staff user who created the invitation.</summary>
    public Guid InvitedByUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>CreatedAt or the last resend, plus 7 days.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Last create/resend send time; the resend cooldown clock.</summary>
    public DateTimeOffset LastSentAt { get; set; }
}
