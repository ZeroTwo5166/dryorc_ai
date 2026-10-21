using backend.Auth;
using backend.Constants;
using backend.Data;
using backend.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace backend.Controllers;

public record TeamMemberResponse(Guid UserId, string Email, string Role, DateTimeOffset JoinedAt);

/// <summary>
/// The member list of the caller's business (DEC D14; design Section 8,
/// ED-26). Any staff role may read it, and it stays available while the
/// staff-invite switch is off (D7), so this controller has no switch filter.
/// </summary>
[ApiController]
[Route("api/team")]
[Authorize(Policy = NivoraPolicies.AnyStaff)]
public class TeamController(AppDbContext db, ICurrentTenantAccessor tenantAccessor) : ControllerBase
{
    /// <summary>
    /// Every user of the business, including inactive ones (no production
    /// path deactivates staff). The tenant filter scopes the query and the
    /// BusinessId predicate names the business explicitly as well (D14).
    /// A user with several roles is shown with the highest one.
    /// </summary>
    [HttpGet("members")]
    public async Task<ActionResult<List<TeamMemberResponse>>> Members(CancellationToken cancellationToken)
    {
        var businessId = tenantAccessor.BusinessId
            ?? throw new InvalidOperationException("Authenticated but no tenant resolved — unreachable behind [Authorize].");

        var rows = await (
            from u in db.Users.Where(u => u.BusinessId == businessId)
            join ur in db.UserRoles on u.Id equals ur.UserId
            join r in db.Roles on ur.RoleId equals r.Id
            select new { u.Id, u.Email, u.CreatedAt, RoleName = r.Name })
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(row => row.Id)
            .Select(g =>
            {
                var top = g.OrderByDescending(row => RoleRank(row.RoleName)).First();
                return new TeamMemberResponse(top.Id, top.Email ?? string.Empty, top.RoleName ?? string.Empty, top.CreatedAt);
            })
            .OrderBy(member => member.JoinedAt)
            .ToList();
    }

    private static int RoleRank(string? role) => role switch
    {
        RoleNames.Owner => 3,
        RoleNames.Admin => 2,
        RoleNames.Agent => 1,
        _ => 0,
    };
}
