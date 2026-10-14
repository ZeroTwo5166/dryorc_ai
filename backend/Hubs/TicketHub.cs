using backend.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;

namespace backend.Hubs;

/// <summary>
/// Step 13B real-time invalidation signal. Clients treat every event as
/// "go refetch via the existing REST API," never as authoritative state —
/// the payload deliberately carries nothing beyond TicketId/EventType (no
/// Status/Priority/ClaimedByUserId/Summary/CustomerId/ConversationId/etc.).
/// REST remains the sole source of truth; this hub only tells a connected
/// client that something changed and which ticket.
/// </summary>
public record TicketEvent(Guid TicketId, string EventType);

/// <summary>
/// One event name, "TicketEvent", carrying the record above, sent to the
/// server-derived tenant group "business:{businessId}" — never a
/// client-chosen group. Tenant identity is read from Context.User, not
/// ICurrentTenantAccessor: SignalR does not reliably flow
/// IHttpContextAccessor.HttpContext into Hub method invocations the way MVC
/// flows it into controller actions, but Context.User is guaranteed valid
/// for the connection's full lifetime, captured once at the authenticated
/// handshake — the same ClaimsPrincipal ClaimsCurrentTenantAccessor would
/// have read from HttpContext.User for an ordinary request.
/// </summary>
[Authorize(Policy = NivoraPolicies.AnyStaff)]
public class TicketHub(IHubContext<TicketHub> hubContext, ILogger<TicketHub> logger) : Hub
{
    public static string GroupName(Guid businessId) => $"business:{businessId}";

    public override async Task OnConnectedAsync()
    {
        var claim = Context.User?.FindFirst(NivoraClaimTypes.BusinessId);
        if (claim is null || !Guid.TryParse(claim.Value, out var businessId))
        {
            // Same fail-closed posture as ClaimsCurrentTenantAccessor. Not
            // reachable via the real staff sign-in flow (NivoraClaimsPrincipalFactory
            // always sets this claim), but an authenticated-yet-tenant-less
            // connection must never be left sitting in no group rather than
            // rejected outright.
            Context.Abort();
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(businessId));

        // Bounded, in-memory, per-connection expiry — no persistent
        // connection-state table, no periodic re-auth polling. Matched to
        // the cookie's own known absolute expiry (Program.cs:
        // ExpireTimeSpan=8h, SlidingExpiration=false), read the same way
        // AuthController.ExtendSession already reads it
        // (HttpContext.AuthenticateAsync(IdentityConstants.ApplicationScheme)).
        var httpContext = Context.GetHttpContext();
        if (httpContext is not null)
        {
            var authResult = await httpContext.AuthenticateAsync(IdentityConstants.ApplicationScheme);
            LogSessionExpiryAuthOutcome(authResult);
            if (authResult.Properties?.ExpiresUtc is { } expiresUtc)
            {
                _ = MonitorSessionExpiryAsync(expiresUtc, Context.ConnectionId, Context.ConnectionAborted);
            }
        }

        await base.OnConnectedAsync();
    }

    /// <summary>
    /// SignalR's public API has no supported way to force-abort an
    /// arbitrary connection from outside an active Hub method invocation
    /// (Context.Abort() only exists on HubCallerContext, valid only during
    /// a live invocation on that connection) — the documented pattern for
    /// "end this connection from a detached background task" is the same
    /// client-cooperative one already used for logout: tell the client, let
    /// it call connection.stop() on itself. hubContext (an IHubContext,
    /// itself a singleton, not tied to this transient Hub instance's
    /// lifetime) is what makes it safe to reach this connection from a
    /// Task.Delay continuation running long after this method — and this
    /// Hub instance — have returned/been disposed.
    /// </summary>
    private async Task MonitorSessionExpiryAsync(DateTimeOffset expiresUtc, string connectionId, CancellationToken connectionAborted)
    {
        var delay = expiresUtc - DateTimeOffset.UtcNow;
        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;

        try
        {
            await Task.Delay(delay, connectionAborted);
        }
        catch (OperationCanceledException)
        {
            return; // connection already ended naturally before its cookie would have expired
        }

        logger.LogDebug(
            "TicketHub connection {ConnectionId}: sending SessionExpiring, {SecondsRemaining:F1} s remaining.",
            connectionId, (expiresUtc - DateTimeOffset.UtcNow).TotalSeconds);
        try
        {
            await hubContext.Clients.Client(connectionId).SendAsync("SessionExpiring", cancellationToken: connectionAborted);
            logger.LogDebug("TicketHub connection {ConnectionId}: SessionExpiring send returned.", connectionId);
        }
        catch (OperationCanceledException)
        {
            // connection ended in the window between the delay elapsing and this send
        }
    }

    // Diagnostic only: one line per connection. Never the cookie, the
    // claims, the ticket or the raw failure message, only the closed set
    // of categories below.
    private void LogSessionExpiryAuthOutcome(AuthenticateResult authResult)
    {
        if (authResult.Properties?.ExpiresUtc is { } expiresUtc)
        {
            if (authResult.Succeeded)
            {
                logger.LogDebug(
                    "TicketHub connection {ConnectionId}: authenticated {Succeeded}, session-expiry monitor starting, {SecondsRemaining:F1} s remaining.",
                    Context.ConnectionId, authResult.Succeeded, (expiresUtc - DateTimeOffset.UtcNow).TotalSeconds);
                return;
            }

            logger.LogWarning(
                "TicketHub connection {ConnectionId}: authenticated {Succeeded}, session-expiry monitor starting ({FailureCategory}).",
                Context.ConnectionId, authResult.Succeeded, SessionExpiryFailureCategory(authResult));
            return;
        }

        logger.LogWarning(
            "TicketHub connection {ConnectionId}: authenticated {Succeeded}, no session-expiry monitor started ({FailureCategory}).",
            Context.ConnectionId, authResult.Succeeded, SessionExpiryFailureCategory(authResult));
    }

    private static string SessionExpiryFailureCategory(AuthenticateResult authResult) => authResult switch
    {
        { Succeeded: true } => "NoExpiresUtc",
        { None: true } => "NoResult",
        { Failure.Message: "Ticket expired" } => "ExpiredAtAuth",
        _ => "AuthenticationFailed",
    };
}
