using System.Text.Json;
using backend.Auth;

namespace backend.Services;

/// <summary>
/// The only builder of staff invitation audit OldValue/NewValue (DEC D11;
/// staff-invite design Section 11, ED-27). Its only inputs are two
/// allowlisted strings, so the entity, the email and the token can never
/// reach an audit payload.
/// </summary>
public static class StaffInvitationAuditPayload
{
    public const string Pending = "Pending";
    public const string Expired = "Expired";
    public const string Accepted = "Accepted";
    public const string Revoked = "Revoked";

    private static readonly string[] Roles = [RoleNames.Admin, RoleNames.Agent];
    private static readonly string[] Statuses = [Pending, Expired, Accepted, Revoked];

    /// <summary>Returns <c>{"role":"...","status":"..."}</c>; throws for any other role or status.</summary>
    public static string Build(string role, string status)
    {
        if (!Roles.Contains(role, StringComparer.Ordinal))
        {
            throw new ArgumentException("Not an allowed staff invitation role.", nameof(role));
        }

        if (!Statuses.Contains(status, StringComparer.Ordinal))
        {
            throw new ArgumentException("Not an allowed staff invitation status.", nameof(status));
        }

        return JsonSerializer.Serialize(new { role, status });
    }
}
