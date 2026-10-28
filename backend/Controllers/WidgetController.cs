using System.Security.Claims;
using System.Text;
using backend.Auth;
using backend.Data;
using backend.Enums;
using backend.Models;
using backend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace backend.Controllers;

/// <summary>SignedIdentityToken (Step 21, signed-token half) is optional
/// and additive — an absent or invalid value never fails Init itself, it
/// only means the resulting session starts without a resolved CustomerId.
/// See ISignedIdentityTokenService.</summary>
public record WidgetInitRequest(string PublicKey, string? VisitorSessionId, string? SignedIdentityToken = null)
{
    // ASP.NET Core logs action arguments at Trace level through the record's
    // ToString, so the visitor session id and the signed identity token print as [redacted].
    protected virtual bool PrintMembers(StringBuilder builder)
    {
        builder.Append("PublicKey = ");
        builder.Append(PublicKey);
        builder.Append(", VisitorSessionId = [redacted]");
        builder.Append(", SignedIdentityToken = [redacted]");
        return true;
    }
}

public record WidgetInitResponse(string VisitorSessionId, string Token);

public record PostWidgetMessageRequest(string Content);

/// <summary>
/// Id/CreatedAt are the customer message just persisted. AiReplyId/
/// AiReplyCreatedAt are the separately-persisted Ai message's own identity
/// (null together whenever AiReply is null) — added for Step 6C so the
/// widget can dedup this same message against a later poll delivery by its
/// real server id, not the customer message's id.
/// </summary>
public record WidgetMessageResponse(Guid Id, DateTimeOffset CreatedAt, string? AiReply, Guid? AiReplyId, DateTimeOffset? AiReplyCreatedAt);

/// <summary>
/// Step 6C customer-side delivery. Role is the raw enum name ("Ai"/"Agent"),
/// same serialization convention ConversationMessageResponse already uses.
/// Never includes AuthorUserId, BusinessId, or ConversationId — see
/// PollMessages' own doc comment for why.
/// </summary>
public record WidgetPolledMessage(Guid Id, string Role, string Content, DateTimeOffset CreatedAt);

/// <summary>
/// Opaque to the client in practice (it should only ever echo back
/// whatever NextCursor it was last given), but transparent in shape —
/// same convention ConversationCursor already established, not an encoded
/// token.
/// </summary>
public record WidgetMessageCursor(DateTimeOffset CreatedAt, Guid Id);

public record WidgetPollResponse(List<WidgetPolledMessage> Messages, WidgetMessageCursor? NextCursor);

/// <summary>
/// Step 21 (OTP half). ConversationId is required and explicit — the widget
/// JWT carries BusinessId/WidgetId/VisitorSessionId only, never
/// ConversationId, so RequestVerificationCode/VerifyVerificationCode must
/// take it from the body and independently cross-check it against the
/// JWT's own claims before doing anything else.
/// </summary>
public record WidgetVerificationRequestRequest(Guid ConversationId, string Email);

/// <summary>
/// Deliberately the SAME shape/status regardless of whether the email
/// matched zero, one, or multiple existing customers — enumeration
/// resistance (design doc Section 6). Sent is always true for any
/// non-throttled request; callers must not infer customer existence from
/// this response.
/// </summary>
public record WidgetVerificationRequestResponse(bool Sent);

/// <summary>Email must be re-submitted alongside Code — see
/// IVerificationCodeService.VerifyCodeAsync's own doc comment for why.</summary>
public record WidgetVerificationVerifyRequest(Guid ConversationId, string Email, string Code)
{
    // ASP.NET Core logs action arguments at Trace level through the record's
    // ToString, so the code prints as [redacted].
    protected virtual bool PrintMembers(StringBuilder builder)
    {
        builder.Append("ConversationId = ");
        builder.Append(ConversationId);
        builder.Append(", Email = ");
        builder.Append(Email);
        builder.Append(", Code = [redacted]");
        return true;
    }
}

/// <summary>Reason is null on success; otherwise the raw
/// VerificationFailureReason name (never the raw code or raw email).</summary>
public record WidgetVerificationVerifyResponse(bool Verified, string? Reason);

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
public class WidgetController(
    AppDbContext db, IWidgetTokenService tokenService, ISignedIdentityTokenService signedIdentityTokenService) : ControllerBase
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

        // Step 21 (signed-token half) — optional and additive. A missing or
        // invalid token never fails Init; it just means customerId stays
        // null and the session starts anonymous, same as today. Nivora's
        // own specific rejection reason is never surfaced to the caller —
        // see ISignedIdentityTokenService's own fail-closed discipline.
        Guid? customerId = null;
        if (!string.IsNullOrWhiteSpace(request.SignedIdentityToken))
        {
            var result = await signedIdentityTokenService.ValidateAndResolveAsync(
                widget.BusinessId, request.SignedIdentityToken, HttpContext.RequestAborted);
            if (result.Outcome == SignedIdentityTokenOutcome.Verified)
            {
                customerId = result.CustomerId;
            }
        }

        var token = tokenService.IssueToken(widget.BusinessId, widget.Id, visitorSessionId, customerId);

        return Ok(new WidgetInitResponse(visitorSessionId, token));
    }

    [HttpPost("messages")]
    [Authorize(Policy = NivoraPolicies.Widget)]
    [EnableRateLimiting("WidgetMessages")]
    public async Task<IActionResult> PostMessage(
        [FromBody] PostWidgetMessageRequest request,
        [FromServices] IWidgetConversationService conversationService,
        [FromServices] IClaudeReplyService replyService)
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
        // Step 21 (signed-token half) — present only if Init already
        // successfully validated a signed identity token for this session.
        Guid? customerId = Guid.TryParse(User.FindFirstValue(NivoraClaimTypes.CustomerId), out var claimCustomerId)
            ? claimCustomerId
            : null;

        var message = await conversationService.PostMessageAsync(businessId, widgetId, visitorSessionId, request.Content, customerId);

        // Deliberately called after the above has already committed — see
        // ClaudeReplyService's own doc comment for why. A null reply here
        // (Claude call failed) is a normal, non-error outcome: the
        // customer's message is already safely persisted regardless.
        var aiReply = await replyService.GenerateReplyAsync(businessId, message.ConversationId);

        return Ok(new WidgetMessageResponse(
            message.Id, message.CreatedAt,
            aiReply?.Content, aiReply?.Id, aiReply?.CreatedAt));
    }

    /// <summary>
    /// Step 6C: customer-side delivery of Agent/Ai messages via minimal
    /// polling — no SignalR/WebSockets, matching the approved design.
    /// Never accepts a client-supplied ConversationId; the caller's
    /// conversation is resolved exactly the way PostMessage above resolves
    /// it (from the widget JWT's own BusinessId/WidgetId/VisitorSessionId
    /// claims), so there is no parameter for a guessed/manipulated
    /// conversation id to even target.
    ///
    /// Cursor semantics: forward keyset on (CreatedAt, Id), same convention
    /// ConversationsController.List already established for its own
    /// (backward) cursor. Supplying neither afterCreatedAt nor afterId
    /// deliberately does NOT return conversation history — it's treated as
    /// "anchor at the server's current time" (cursorCreatedAt defaults to
    /// UtcNow, cursorId to Guid.Empty, the minimum for the tie-break), so a
    /// first-ever poll returns nothing pre-existing and hands back a fresh
    /// NextCursor the client can resume from. This is deliberate: Step 6B's
    /// own decision to defer a general history-fetch endpoint stands —
    /// polling exists to deliver what arrives AFTER a visitor's already-
    /// known point (from their own localStorage transcript, if any), never
    /// to backfill everything they missed. Supplying exactly one of the two
    /// cursor fields is rejected as malformed (400) rather than guessed at.
    ///
    /// Only Ai and Agent messages are returned — never Customer (the widget
    /// already renders its own sent messages optimistically; echoing them
    /// back here would risk duplication for no benefit) and never
    /// AuthorUserId/BusinessId/ConversationId (staff identity and tenant
    /// internals stay server-side, same precedent Step 9's
    /// ConversationMessageResponse already set).
    /// </summary>
    [HttpGet("messages")]
    [Authorize(Policy = NivoraPolicies.Widget)]
    [EnableRateLimiting("WidgetPoll")]
    public async Task<IActionResult> PollMessages(
        [FromQuery] DateTimeOffset? afterCreatedAt = null,
        [FromQuery] Guid? afterId = null)
    {
        if (afterCreatedAt.HasValue != afterId.HasValue)
        {
            return BadRequest();
        }

        var cursorCreatedAt = afterCreatedAt ?? DateTimeOffset.UtcNow;
        var cursorId = afterId ?? Guid.Empty;

        var businessId = Guid.Parse(User.FindFirstValue(NivoraClaimTypes.BusinessId)!);
        var widgetId = Guid.Parse(User.FindFirstValue(NivoraClaimTypes.WidgetId)!);
        var visitorSessionId = User.FindFirstValue(NivoraClaimTypes.VisitorSessionId)!;

        // Same resolution PostMessageAsync uses — IgnoreQueryFilters() +
        // explicit BusinessId comparisons throughout, not relying on the
        // ambient tenant query filter (same defensive posture
        // WidgetConversationService's own doc comment explains).
        var conversation = await db.Conversations
            .IgnoreQueryFilters()
            .Where(c => c.BusinessId == businessId
                && c.WidgetId == widgetId
                && c.VisitorSessionId == visitorSessionId
                && c.Status == ConversationStatus.Open
                && !c.IsAnonymized)
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync();

        // No open conversation yet (visitor has never sent a message, or
        // it's since closed) — a normal state, not an error.
        if (conversation is null)
        {
            return Ok(new WidgetPollResponse([], null));
        }

        const int PageSize = 50;
        var polled = await db.Messages
            .IgnoreQueryFilters()
            .Where(m => m.BusinessId == businessId
                && m.ConversationId == conversation.Id
                && (m.Role == MessageRole.Ai || m.Role == MessageRole.Agent)
                && (m.CreatedAt > cursorCreatedAt || (m.CreatedAt == cursorCreatedAt && m.Id > cursorId)))
            .OrderBy(m => m.CreatedAt)
            .ThenBy(m => m.Id)
            .Take(PageSize)
            .Select(m => new WidgetPolledMessage(m.Id, m.Role.ToString(), m.Content, m.CreatedAt))
            .ToListAsync();

        // Always return a cursor, even for an empty page — either the last
        // message actually returned, or (nothing new) an echo of whatever
        // cursor this request effectively used, so the client always has a
        // stable value for its next poll with no special-cased branch.
        var nextCursor = polled.Count > 0
            ? new WidgetMessageCursor(polled[^1].CreatedAt, polled[^1].Id)
            : new WidgetMessageCursor(cursorCreatedAt, cursorId);

        return Ok(new WidgetPollResponse(polled, nextCursor));
    }

    /// <summary>
    /// Step 21 (OTP half). Uniform response regardless of match count
    /// (enumeration resistance) — Sent/AmbiguousCustomerMatch collapse to
    /// the same 200 body; only the resend cooldown/limit (a normal,
    /// expected throttle, not a customer-existence oracle) surfaces
    /// distinctly, as 429.
    /// </summary>
    [HttpPost("verification/request")]
    [Authorize(Policy = NivoraPolicies.Widget)]
    [EnableRateLimiting("WidgetOtpRequest")]
    public async Task<IActionResult> RequestVerificationCode(
        [FromBody] WidgetVerificationRequestRequest request,
        [FromServices] IVerificationCodeService verificationCodeService)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || !IsPlausibleEmail(request.Email))
        {
            return BadRequest();
        }

        var conversation = await ResolveOwnConversationAsync(request.ConversationId);
        if (conversation is null)
        {
            return NotFound();
        }

        var result = await verificationCodeService.RequestCodeAsync(
            conversation.BusinessId, conversation.Id, conversation.Channel, request.Email,
            HttpContext.RequestAborted);

        return result.Outcome switch
        {
            RequestCodeOutcome.Issued or RequestCodeOutcome.AmbiguousCustomerMatch
                => Ok(new WidgetVerificationRequestResponse(true)),
            RequestCodeOutcome.ResendCooldownActive or RequestCodeOutcome.ResendLimitExceeded
                => StatusCode(StatusCodes.Status429TooManyRequests),
            _ => StatusCode(StatusCodes.Status500InternalServerError),
        };
    }

    [HttpPost("verification/verify")]
    [Authorize(Policy = NivoraPolicies.Widget)]
    [EnableRateLimiting("WidgetOtpVerify")]
    public async Task<IActionResult> VerifyVerificationCode(
        [FromBody] WidgetVerificationVerifyRequest request,
        [FromServices] IVerificationCodeService verificationCodeService)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || !IsPlausibleEmail(request.Email)
            || string.IsNullOrWhiteSpace(request.Code))
        {
            return BadRequest();
        }

        var conversation = await ResolveOwnConversationAsync(request.ConversationId);
        if (conversation is null)
        {
            return NotFound();
        }

        var result = await verificationCodeService.VerifyCodeAsync(
            conversation.BusinessId, conversation.Id, conversation.Channel, request.Email, request.Code,
            HttpContext.RequestAborted);

        return Ok(new WidgetVerificationVerifyResponse(result.Success, result.Success ? null : result.Reason.ToString()));
    }

    /// <summary>
    /// Resolves the caller's own Conversation, cross-checked against the
    /// widget JWT's own (BusinessId, WidgetId, VisitorSessionId) claims —
    /// the same identity-binding requirement PostMessage/PollMessages
    /// enforce for their own implicitly-resolved conversation, applied here
    /// to a client-supplied ConversationId instead (design doc Section 3:
    /// the JWT carries no ConversationId, so a verification request must
    /// supply one explicitly and this must independently confirm it
    /// belongs to the same visitor session).
    /// </summary>
    private async Task<Conversation?> ResolveOwnConversationAsync(Guid conversationId)
    {
        var businessId = Guid.Parse(User.FindFirstValue(NivoraClaimTypes.BusinessId)!);
        var widgetId = Guid.Parse(User.FindFirstValue(NivoraClaimTypes.WidgetId)!);
        var visitorSessionId = User.FindFirstValue(NivoraClaimTypes.VisitorSessionId)!;

        return await db.Conversations
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == conversationId
                && c.BusinessId == businessId
                && c.WidgetId == widgetId
                && c.VisitorSessionId == visitorSessionId
                && !c.IsAnonymized);
    }

    /// <summary>
    /// Minimal format validation only — no existing email-validation
    /// convention exists anywhere else in this codebase to match. Not a
    /// substitute for the normalization VerificationCodeService itself
    /// performs; just rejects obviously-malformed input before it reaches
    /// customer resolution.
    /// </summary>
    private static bool IsPlausibleEmail(string email)
    {
        var at = email.IndexOf('@');
        return at > 0 && at < email.Length - 1 && !email.Contains(' ');
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
