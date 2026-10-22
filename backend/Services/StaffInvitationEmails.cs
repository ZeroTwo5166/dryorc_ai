using System.Net;

namespace backend.Services;

/// <summary>One email ready to send: subject (plain text) and HTML body.</summary>
public sealed record StaffInvitationEmail(string Subject, string Html);

/// <summary>
/// The two staff invitation emails (DEC D3, D8; staff-invite design Section 6
/// "Emails", ED-37). The join-link email carries the only copy of the token,
/// inside the link. The already-in-use email, sent when the address already
/// has a Nivora account, carries no link at all. The business name is
/// HTML-encoded because a business owner chooses it.
/// </summary>
public static class StaffInvitationEmails
{
    public static StaffInvitationEmail BuildJoinLink(string businessName, string roleName, string link)
    {
        var business = WebUtility.HtmlEncode(businessName);
        var role = WebUtility.HtmlEncode(roleName);
        var href = WebUtility.HtmlEncode(link);

        var html = $"""
            <p>You have been invited to join {business} on Nivora as {role}.</p>
            <p><a href="{href}">Accept the invitation</a></p>
            <p>This invitation expires in 7 days. If you were not expecting it, you can ignore this email.</p>
            """;

        return new StaffInvitationEmail($"You're invited to join {businessName} on Nivora", html);
    }

    public static StaffInvitationEmail BuildAlreadyInUse(string businessName)
    {
        var business = WebUtility.HtmlEncode(businessName);

        var html = $"""
            <p>{business} tried to invite you to Nivora.</p>
            <p>This email address already has a Nivora account, and one account cannot belong to more than one business.</p>
            <p>Please contact the person who invited you.</p>
            """;

        return new StaffInvitationEmail("Your Nivora invitation", html);
    }
}
