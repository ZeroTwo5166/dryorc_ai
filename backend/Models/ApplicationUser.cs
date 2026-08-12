using backend.Interfaces;
using Microsoft.AspNetCore.Identity;

namespace backend.Models;

/// <summary>
/// Staff/dashboard user (Step 4). Role assignment (Owner/Admin/Agent) goes
/// through ASP.NET Identity's own Role tables, not a column here.
///
/// Never hard-delete this entity — deactivate via IsActive instead. This
/// is required, not just conventional: Message.AuthorUserId's
/// CK_Messages_AuthorUserId_RolePairing constraint depends on Agent-role
/// messages always retaining a non-null AuthorUserId, which only holds if
/// the referenced ApplicationUser row is never removed.
/// </summary>
public class ApplicationUser : IdentityUser<Guid>, ITenantOwned
{
    public Guid BusinessId { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
}