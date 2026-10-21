using backend.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace backend.Auth;

/// <summary>
/// Staff-invite release switch at the HTTP edge (DEC D7; design ED-25).
/// While the switch is off, every staff-invite endpoint answers 404 and the
/// action never runs.
///
/// Why a resource filter: resource filters run before model binding, so
/// they also run before UnsupportedContentTypeFilter (order -3000) and
/// ModelStateInvalidFilter (order -2000). A request with a bad body or
/// content type therefore gets 404 while the switch is off, never 400 or
/// 415, and the endpoints reveal nothing.
///
/// Authorization filters run before resource filters. The controllers keep
/// their AnyStaff class attribute, so an unauthenticated caller of a
/// staff-only endpoint still gets 401, not 404 (erratum E2).
/// </summary>
public class StaffInvitesEnabledFilter(IStaffInviteSwitch staffInviteSwitch) : IAsyncResourceFilter
{
    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        if (!staffInviteSwitch.IsEnabled)
        {
            context.Result = new NotFoundResult();
            return;
        }

        await next();
    }
}
