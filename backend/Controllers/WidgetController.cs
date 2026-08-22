using System.Security.Claims;
using backend.Auth;
using backend.Data;
using backend.Enums;
using backend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace backend.Controllers;

public record WidgetInitRequest(string PublicKey, string? VisitorSessionId);

public record WidgetInitResponse(string VisitorSessionId, string Token);

public record PostWidgetMessageRequest(string Content);

public record WidgetMessageResponse(Guid Id, DateTimeOffset CreatedAt);

/// <summary>
/// Public, widget-facing endpoints — no staff cookie involved anywhere
/// here. init is fully anonymous; messages requires a widget JWT
/// (NivoraPolicies.Widget, bound to NivoraAuthSchemes.WidgetBearer).
///
/// [EnableCors("Widget")] at the class level, separate from the dashboard's
/// own CORS policy (Program.cs) — a widget can be embedded on any
/// third-party storefront, unknown ahead of time, so this policy allows
/// any origin. Real per-business domain restriction is the
/// Widget.AllowedDomains check inside Init below, not CORS.
/// </summary>
[ApiController]
[Route("api/widget")]
[EnableCors("Widget")]
public class WidgetController(AppDbContext db, IWidgetTokenService tokenService) : ControllerBase
{
    [HttpPost("init")]
    [EnableRateLimiting("WidgetInit")]
    public async Task<IActionResult> Init([FromBody] WidgetInitRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.PublicKey))
        {
            return BadRequest();
        }

        var widget = await db.Widgets
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(w => w.PublicKey == request.PublicKey && w.IsActive);

        // Collapses "no such widget" and "widget deactivated" into the same
        // response, deliberately — same not-found idiom used elsewhere in
        // this codebase (e.g. CustomersController.Get's SameTenant check)
        // rather than distinguishing them for an anonymous caller.
        if (widget is null)
        {
            return NotFound();
        }

        // Application-level soft check, per the schema doc's own framing —
        // NOT the real security boundary (Origin can be omitted or forged
        // by a non-browser client; server-side token validation on every
        // subsequent request is what actually authorizes anything). Only
        // enforced when the business has actually configured a domain
        // allowlist and the request carries an Origin header at all.
        var origin = Request.Headers.Origin.ToString();
        if (widget.AllowedDomains.Count > 0 && !string.IsNullOrEmpty(origin) && !OriginIsAllowed(origin, widget.AllowedDomains))
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        string? visitorSessionId = null;

        if (!string.IsNullOrWhiteSpace(request.VisitorSessionId))
        {
            var hasOpenConversation = await db.Conversations
                .IgnoreQueryFilters()
                .AnyAsync(c => c.BusinessId == widget.BusinessId
                    && c.WidgetId == widget.Id
                    && c.VisitorSessionId == request.VisitorSessionId
                    && c.Status == ConversationStatus.Open
                    && !c.IsAnonymized);

            if (hasOpenConversation)
            {
                visitorSessionId = request.VisitorSessionId;
            }
        }

        // No matching open conversation (or none supplied at all) — start a
        // fresh visitor session rather than trusting an unverified
        // client-supplied id.
        visitorSessionId ??= Guid.NewGuid().ToString("N");

        var token = tokenService.IssueToken(widget.BusinessId, widget.Id, visitorSessionId);

        return Ok(new WidgetInitResponse(visitorSessionId, token));
    }

    [HttpPost("messages")]
    [Authorize(Policy = NivoraPolicies.Widget)]
    [EnableRateLimiting("WidgetMessages")]
    public async Task<IActionResult> PostMessage(
        [FromBody] PostWidgetMessageRequest request, [FromServices] IWidgetConversationService conversationService)
    {
        if (string.IsNullOrWhiteSpace(request.Content))
        {
            return BadRequest();
        }

        // Identity comes only from the authenticated widget JWT's claims —
        // never from the request body. There is no ConversationId,
        // WidgetId, or BusinessId field on PostWidgetMessageRequest at all,
        // so there is nothing for a client-supplied value to override.
        var businessId = Guid.Parse(User.FindFirstValue(NivoraClaimTypes.BusinessId)!);
        var widgetId = Guid.Parse(User.FindFirstValue(NivoraClaimTypes.WidgetId)!);
        var visitorSessionId = User.FindFirstValue(NivoraClaimTypes.VisitorSessionId)!;

        var message = await conversationService.PostMessageAsync(businessId, widgetId, visitorSessionId, request.Content);

        return Ok(new WidgetMessageResponse(message.Id, message.CreatedAt));
    }

    private static bool OriginIsAllowed(string origin, List<string> allowedDomains)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri))
        {
            return false;
        }

        var host = originUri.Host;
        return allowedDomains.Any(domain =>
            string.Equals(host, domain, StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase));
    }
}
