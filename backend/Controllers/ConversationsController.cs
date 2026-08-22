using backend.Auth;
using backend.Data;
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

/// <summary>
/// Backend verification/read surface for Steps 6-7, now also the Step 9
/// Business Inbox read API. Same AnyStaff + SameTenant two-layer pattern
/// CustomersController already established — nothing new invented here.
/// </summary>
[ApiController]
[Route("api/conversations")]
[Authorize(Policy = NivoraPolicies.AnyStaff)]
public class ConversationsController(AppDbContext db, IAuthorizationService authorizationService) : ControllerBase
{
    private const int PreviewMaxLength = 120;

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
}
