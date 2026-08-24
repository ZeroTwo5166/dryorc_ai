using System.Security.Claims;
using backend.Auth;
using backend.Constants;
using backend.Data;
using backend.Enums;
using backend.Models;
using backend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace backend.Controllers;

/// <summary>
/// ActivityAt is (Conversation.LastMessageAt ?? Conversation.CreatedAt) —
/// the exact value List() sorts and filters by — never Conversation.CreatedAt
/// itself. Deliberately not named/shaped like NotificationCursor: unlike
/// NotificationRecipient.CreatedAt (set once, immutable for the row's
/// lifetime), LastMessageAt is mutable and only ever moves forward as new
/// messages arrive. See the mutable-activity note on List() below for the
/// one consequence that has for an in-progress pagination walk.
/// </summary>
public record ConversationCursor(DateTimeOffset ActivityAt, Guid Id);

public record ConversationSummaryResponse(
    Guid Id,
    string Channel,
    string Status,
    string HandledBy,
    string? LastMessagePreview,
    DateTimeOffset? LastMessageAt,
    DateTimeOffset CreatedAt);

public record ConversationListResponse(List<ConversationSummaryResponse> Conversations, ConversationCursor? NextCursor);

public record ConversationMessageResponse(Guid Id, string Role, string Content, DateTimeOffset CreatedAt);

public record CreateConversationMessageRequest(string Content);

/// <summary>
/// Backend verification/read surface for Steps 6-7, now also the Step 9
/// Business Inbox read+reply API. Same AnyStaff + SameTenant two-layer
/// pattern CustomersController already established — nothing new invented
/// here.
/// </summary>
[ApiController]
[Route("api/conversations")]
[Authorize(Policy = NivoraPolicies.AnyStaff)]
public class ConversationsController(AppDbContext db, IAuthorizationService authorizationService) : ControllerBase
{
    private const int PreviewMaxLength = 120;

    private Guid CurrentUserId
    {
        get
        {
            var raw = User.FindFirstValue(ClaimTypes.NameIdentifier);
            return Guid.TryParse(raw, out var id)
                ? id
                : throw new InvalidOperationException("Authenticated but no parsable user id claim — unreachable behind [Authorize].");
        }
    }

    /// <summary>
    /// Business Inbox, most-recently-active-first: ordered and paginated by
    /// (LastMessageAt ?? CreatedAt) DESC, Id DESC — never Conversation.CreatedAt
    /// alone. See ConversationCursor's doc comment for why the cursor field
    /// is named ActivityAt rather than CreatedAt.
    ///
    /// Known v1 tradeoff, accepted deliberately: LastMessageAt is mutable
    /// and only ever moves forward (a new message always sets it to a
    /// timestamp >= its previous value). A conversation that receives a new
    /// message between two paginated requests can cross from "older than
    /// the current cursor" to "newer than it," which — because the
    /// continuation filter is a strict less-than boundary — omits it from
    /// the REST of that particular in-progress pagination walk (it is never
    /// duplicated; already-returned rows can only move further toward
    /// "newer," never back below a cursor they already passed). A fresh,
    /// cursor-less request immediately shows it again, now near the top.
    /// This is intentionally not compensated for with polling, SignalR,
    /// WebSockets, or any other live-update mechanism — out of scope for
    /// Step 9.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int take = 20,
        [FromQuery] DateTimeOffset? beforeActivityAt = null,
        [FromQuery] Guid? beforeId = null)
    {
        take = Math.Clamp(take, 1, 100);

        // Layer 1: the global tenant query filter means this can never
        // return another business's row — no per-row check left to make
        // for a list query, same reasoning as CustomersController.List.
        var query = db.Conversations.AsQueryable();

        if (beforeActivityAt is { } cursorActivityAt && beforeId is { } cursorId)
        {
            query = query.Where(c =>
                (c.LastMessageAt ?? c.CreatedAt) < cursorActivityAt ||
                ((c.LastMessageAt ?? c.CreatedAt) == cursorActivityAt && c.Id < cursorId));
        }

        var page = await query
            .OrderByDescending(c => c.LastMessageAt ?? c.CreatedAt)
            .ThenByDescending(c => c.Id)
            .Take(take + 1)
            .ToListAsync();

        var hasMore = page.Count > take;
        var pageItems = hasMore ? page.Take(take).ToList() : page;

        // Correlated per-conversation scalar subquery (translates to a
        // LATERAL join), backed by the existing (BusinessId, ConversationId,
        // CreatedAt) index on Messages — no full message history loaded, no
        // schema change.
        var conversationIds = pageItems.Select(c => c.Id).ToList();
        var previews = await db.Conversations
            .Where(c => conversationIds.Contains(c.Id))
            .Select(c => new
            {
                c.Id,
                LastMessageContent = db.Messages
                    .Where(m => m.ConversationId == c.Id)
                    .OrderByDescending(m => m.CreatedAt)
                    .ThenByDescending(m => m.Id)
                    .Select(m => m.Content)
                    .FirstOrDefault(),
            })
            .ToDictionaryAsync(x => x.Id, x => x.LastMessageContent);

        var conversations = pageItems
            .Select(c => new ConversationSummaryResponse(
                c.Id,
                c.Channel,
                c.Status.ToString(),
                c.HandledBy.ToString(),
                TruncatePreview(previews.GetValueOrDefault(c.Id)),
                c.LastMessageAt,
                c.CreatedAt))
            .ToList();

        ConversationCursor? nextCursor = hasMore
            ? new ConversationCursor(pageItems[^1].LastMessageAt ?? pageItems[^1].CreatedAt, pageItems[^1].Id)
            : null;

        return Ok(new ConversationListResponse(conversations, nextCursor));
    }

    private static string? TruncatePreview(string? content) =>
        content is null || content.Length <= PreviewMaxLength
            ? content
            : content[..(PreviewMaxLength - 3)] + "...";

    [HttpGet("{id:guid}/messages")]
    public async Task<IActionResult> Messages(Guid id)
    {
        var conversation = await db.Conversations.FirstOrDefaultAsync(c => c.Id == id);
        if (conversation is null)
        {
            return NotFound();
        }

        // Layer 2, explicit — same SameTenant check CustomersController.Get
        // uses, not assumed redundant just because layer 1 already worked.
        var authResult = await authorizationService.AuthorizeAsync(User, conversation, NivoraPolicies.SameTenant);
        if (!authResult.Succeeded)
        {
            return NotFound();
        }

        var messages = await db.Messages
            .Where(m => m.ConversationId == id)
            .OrderBy(m => m.CreatedAt)
            .Select(m => new ConversationMessageResponse(m.Id, m.Role.ToString(), m.Content, m.CreatedAt))
            .ToListAsync();

        return Ok(messages);
    }

    /// <summary>
    /// Step 9 staff-reply composer. Persists a Message{Role=Agent,
    /// AuthorUserId=CurrentUserId} and, on the conversation's first Agent
    /// reply, flips HandledBy from Ai to Human in the same SaveChangesAsync
    /// — see Conversation.HandledBy's own doc comment for what that flip
    /// means downstream (ClaudeReplyService stops auto-replying while
    /// Human). Setting HandledBy = Human unconditionally here is
    /// deliberately idempotent: a second/third staff reply just re-assigns
    /// the same value, no branch needed to detect "is this the first one."
    ///
    /// No customer-delivery mechanism exists yet (no SignalR/polling/widget
    /// changes) — this message is persisted and dashboard-visible only, an
    /// explicitly accepted Step 9 MVP limitation, not an oversight.
    ///
    /// Also resolves (marks read) any still-unread CustomerReplied
    /// notification(s) for this conversation, for every recipient — see
    /// WidgetConversationService's own doc comment for where those get
    /// created. Concurrency: this mark-read and a near-simultaneous new
    /// customer message's notification-check run in separate transactions
    /// with no shared lock. Worst case if a fresh CustomerReplied
    /// notification is created in the narrow window between this method's
    /// SaveChangesAsync and its mark-read query below, that brand-new
    /// notification also gets swept up and marked read here — a benign,
    /// self-correcting staleness (the badge briefly under-counts; the
    /// message itself is unaffected and still fully visible in the
    /// conversation), same class of narrow timing window this codebase
    /// already accepts for the HandledBy Ai/Human race rather than adding
    /// locking for. The reverse — an old, genuinely-unaddressed notification
    /// incorrectly staying unread — cannot happen: this update is
    /// unconditional over every currently-unread CustomerReplied
    /// notification for this conversation at the moment it runs.
    /// </summary>
    [HttpPost("{id:guid}/messages")]
    public async Task<IActionResult> CreateMessage(Guid id, [FromBody] CreateConversationMessageRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Content))
        {
            return BadRequest();
        }

        var conversation = await db.Conversations.FirstOrDefaultAsync(c => c.Id == id);
        if (conversation is null)
        {
            return NotFound();
        }

        // Layer 2, explicit — same SameTenant check Messages() above uses,
        // not assumed redundant just because layer 1 already worked.
        var authResult = await authorizationService.AuthorizeAsync(User, conversation, NivoraPolicies.SameTenant);
        if (!authResult.Succeeded)
        {
            return NotFound();
        }

        // Defensive only — nothing in this codebase ever sets a conversation
        // to Closed yet (closing/reopening is unbuilt), so this branch is
        // currently unreachable in practice. Kept so the endpoint is already
        // correct once a future step introduces closing, rather than
        // silently allowing replies to a closed conversation.
        if (conversation.Status == ConversationStatus.Closed)
        {
            return Conflict();
        }

        var now = DateTimeOffset.UtcNow;
        var message = new Message
        {
            Id = Guid.NewGuid(),
            BusinessId = conversation.BusinessId,
            ConversationId = conversation.Id,
            Role = MessageRole.Agent,
            Content = request.Content,
            AuthorUserId = CurrentUserId,
            CreatedAt = now,
        };
        db.Messages.Add(message);

        conversation.LastMessageAt = now;
        conversation.UpdatedAt = now;
        conversation.HandledBy = HandledBy.Human;

        await db.SaveChangesAsync();

        // A staff reply is the "human attention given" signal — resolve any
        // still-unread CustomerReplied notification(s) for this conversation
        // for every recipient, not just the replying staff member (the
        // underlying "needs a human" condition is conversation-wide, not
        // per-viewer). Not required to share a transaction with the message
        // insert above — a notification staying visibly unread for a moment
        // after the reply already landed is cosmetic, not a correctness
        // issue (see this method's own doc comment for the fuller
        // concurrency reasoning against a concurrent new customer message).
        // Same ExecuteUpdateAsync idiom NotificationsController.MarkRead
        // already uses. Shares its "which rows count" definition with
        // WidgetConversationService's dedup check via
        // CustomerRepliedNotificationQueries — see that class's own doc
        // comment for why (audit finding: this predicate had drifted into
        // two independently-written copies).
        await CustomerRepliedNotificationQueries
            .UnreadRecipients(db, conversation.BusinessId, conversation.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.IsRead, true)
                .SetProperty(r => r.ReadAt, now));

        return Ok(new ConversationMessageResponse(message.Id, message.Role.ToString(), message.Content, message.CreatedAt));
    }
}
