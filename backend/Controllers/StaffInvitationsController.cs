using System.Security.Claims;
using System.Text;
using backend.Auth;
using backend.Constants;
using backend.Interfaces;
using backend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace backend.Controllers;

public record CreateStaffInvitationRequest(string Email, string Role);

public record PreviewStaffInvitationRequest(string Token)
{
    // ASP.NET Core logs action arguments at Trace level through the record's
    // ToString, so the token prints as [redacted] (I3).
    protected virtual bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Token = [redacted]");
        return true;
    }
}

public record AcceptStaffInvitationRequest(string Token, string Password)
{
    // ASP.NET Core logs action arguments at Trace level through the record's
    // ToString, so the token and the password print as [redacted] (I3).
    protected virtual bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Token = [redacted], Password = [redacted]");
        return true;
    }
}

/// <summary>
/// Staff invitations over HTTP (DEC D1, D3, D6, D7, D10, D12, E2; design
/// Section 8). The class attribute is only AnyStaff: an unauthenticated call
/// to a staff action gets 401, and preview and accept are AllowAnonymous.
/// The release switch is the StaffInvitesEnabledFilter (a resource filter,
/// so it answers 404 before model binding), and the D1 role matrix is in
/// the service. No action checks either of them.
/// </summary>
[ApiController]
[Route("api/staff-invitations")]
[Authorize(Policy = NivoraPolicies.AnyStaff)]
[ServiceFilter(typeof(StaffInvitesEnabledFilter))]
public class StaffInvitationsController(
    IStaffInvitationService staffInvitationService,
    ICurrentTenantAccessor tenantAccessor) : ControllerBase
{
    [HttpPost("")]
    [EnableRateLimiting("StaffInviteSend")]
    public async Task<IActionResult> Create([FromBody] CreateStaffInvitationRequest request, CancellationToken cancellationToken)
    {
        var result = await staffInvitationService.CreateAsync(CurrentCaller(), request.Email, request.Role, cancellationToken);
        return result.Outcome switch
        {
            StaffInvitationOutcome.Created => StatusCode(StatusCodes.Status201Created, result.Invitation),
            _ => MapOutcome(result.Outcome, result.Message),
        };
    }

    [HttpGet("")]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var result = await staffInvitationService.ListAsync(CurrentCaller(), cancellationToken);
        return result.Outcome switch
        {
            StaffInvitationOutcome.Ok => Ok(result.Invitations),
            _ => MapOutcome(result.Outcome),
        };
    }

    [HttpPost("{id:guid}/resend")]
    [EnableRateLimiting("StaffInviteSend")]
    public async Task<IActionResult> Resend(Guid id, CancellationToken cancellationToken)
    {
        var result = await staffInvitationService.ResendAsync(CurrentCaller(), id, cancellationToken);
        return result.Outcome switch
        {
            StaffInvitationOutcome.Ok => Ok(result.Invitation),
            _ => MapOutcome(result.Outcome),
        };
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken cancellationToken)
    {
        var result = await staffInvitationService.RevokeAsync(CurrentCaller(), id, cancellationToken);
        return MapOutcome(result.Outcome);
    }

    [HttpPost("preview")]
    [AllowAnonymous]
    [EnableRateLimiting("StaffInviteAnonymous")]
    public async Task<IActionResult> Preview([FromBody] PreviewStaffInvitationRequest request, CancellationToken cancellationToken)
    {
        var result = await staffInvitationService.PreviewAsync(request.Token, cancellationToken);
        return result.Outcome switch
        {
            StaffInvitationOutcome.Ok => Ok(result.Preview),
            _ => MapOutcome(result.Outcome),
        };
    }

    [HttpPost("accept")]
    [AllowAnonymous]
    [EnableRateLimiting("StaffInviteAnonymous")]
    public async Task<IActionResult> Accept([FromBody] AcceptStaffInvitationRequest request, CancellationToken cancellationToken)
    {
        var result = await staffInvitationService.AcceptAsync(request.Token, request.Password, cancellationToken);
        return result.Outcome switch
        {
            StaffInvitationOutcome.Ok => Ok(result.Accepted),
            StaffInvitationOutcome.BadRequest when result.Errors is not null => BadRequest(result.Errors),
            _ => MapOutcome(result.Outcome, result.Message),
        };
    }

    /// <summary>
    /// The caller as the service needs it (design Section 6): user id from
    /// the NameIdentifier claim, business from the tenant accessor, Owner and
    /// Admin from the role claims, and the principal for SameTenant (G-1).
    /// </summary>
    private StaffCaller CurrentCaller()
    {
        var raw = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var userId = Guid.TryParse(raw, out var id)
            ? id
            : throw new InvalidOperationException("Authenticated but no parsable user id claim — unreachable behind [Authorize].");
        var businessId = tenantAccessor.BusinessId
            ?? throw new InvalidOperationException("Authenticated but no tenant resolved — unreachable behind [Authorize].");
        return new StaffCaller(userId, businessId, User.IsInRole(RoleNames.Owner), User.IsInRole(RoleNames.Admin), User);
    }

    /// <summary>
    /// Status codes of design Section 8 (ED-22). The generic invalid link is
    /// an empty 404, the same shape as the switch-off 404 (D6, D7).
    /// </summary>
    private IActionResult MapOutcome(StaffInvitationOutcome outcome, string? message = null) => outcome switch
    {
        StaffInvitationOutcome.NoContent => NoContent(),
        StaffInvitationOutcome.NotFound => NotFound(),
        StaffInvitationOutcome.InvalidLink => NotFound(),
        StaffInvitationOutcome.Forbidden => StatusCode(StatusCodes.Status403Forbidden),
        StaffInvitationOutcome.BadRequest => BadRequest(message),
        StaffInvitationOutcome.Conflict => Conflict(message),
        StaffInvitationOutcome.TooManyRequests => StatusCode(StatusCodes.Status429TooManyRequests),
        _ => throw new InvalidOperationException($"Unexpected staff invitation outcome {outcome}."),
    };
}
