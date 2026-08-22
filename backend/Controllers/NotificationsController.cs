using System.Security.Claims;
using backend.Auth;
using backend.Data;
using backend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace backend.Controllers;

public record NotificationCursor(DateTimeOffset CreatedAt, Guid Id);

/// <summary>
/// Id here is NotificationRecipient.Id, not Notification.Id — from the
/// client's perspective this row IS "my notification"; the underlying
/// shared Notification row is an implementation detail never exposed
/// directly. Mark-as-read/read-all both target rows by this same Id.
/// </summary>
public record NotificationResponse(
    Guid Id,
    string Category,
    string Type,
    string Title,
    string? Link,
    bool IsRead,
    DateTimeOffset CreatedAt);

public record NotificationListResponse(
    List<NotificationResponse> Notifications,
    NotificationCursor? NextCursor,
    NotificationCursor? ReadAllUpTo);

public record UnreadCountResponse(int Count);

/// <summary>
/// Staff-facing read/mark-read API over Notification/NotificationRecipient.
/// No endpoint here creates a notification — that's NotificationService's
/// job, with no live caller yet.
/// </summary>
[ApiController]
[Route("api/notifications")]
[Authorize(Policy = NivoraPolicies.AnyStaff)]
public class NotificationsController(AppDbContext db) : ControllerBase
{
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

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int take = 20,
        [FromQuery] DateTimeOffset? beforeCreatedAt = null,
        [FromQuery] Guid? beforeId = null)
    {
        take = Math.Clamp(take, 1, 100);
        var userId = CurrentUserId;

        // Tenant filter (global query filter, via ITenantOwned) already
        // scopes this to the caller's own business — the explicit UserId
        // filter below is the second, ownership-level check: the tenant
        // filter alone only proves "same business," not "this recipient
        // row belongs to the calling staff member."
        var query = db.NotificationRecipients
            .Where(r => r.UserId == userId);

        if (beforeCreatedAt is { } cursorCreatedAt && beforeId is { } cursorId)
        {
            query = query.Where(r =>
                r.CreatedAt < cursorCreatedAt ||
                (r.CreatedAt == cursorCreatedAt && r.Id < cursorId));
        }

        var page = await query
            .OrderByDescending(r => r.CreatedAt)
            .ThenByDescending(r => r.Id)
            .Take(take + 1)
            .Join(db.Notifications, r => r.NotificationId, n => n.Id, (r, n) => new { Recipient = r, Notification = n })
            .ToListAsync();

        var hasMore = page.Count > take;
        var pageItems = hasMore ? page.Take(take).ToList() : page;

        var notifications = pageItems
            .Select(x => new NotificationResponse(
                x.Recipient.Id,
                x.Notification.Category.ToString(),
                x.Notification.Type,
                x.Notification.Title,
                NotificationLinkResolver.Resolve(x.Notification),
                x.Recipient.IsRead,
                x.Recipient.CreatedAt))
            .ToList();

        NotificationCursor? nextCursor = hasMore
            ? new NotificationCursor(pageItems[^1].Recipient.CreatedAt, pageItems[^1].Recipient.Id)
            : null;

        // Computed across ALL unread rows for this user, not just the
        // current page — a "mark all read" click must be able to clear
        // unread notifications the user hasn't scrolled to yet.
        var readAllUpTo = await db.NotificationRecipients
            .Where(r => r.UserId == userId && !r.IsRead)
            .OrderByDescending(r => r.CreatedAt)
            .ThenByDescending(r => r.Id)
            .Select(r => new NotificationCursor(r.CreatedAt, r.Id))
            .FirstOrDefaultAsync();

        return Ok(new NotificationListResponse(notifications, nextCursor, readAllUpTo));
    }

    [HttpGet("unread-count")]
    public async Task<IActionResult> UnreadCount()
    {
        var userId = CurrentUserId;
        var count = await db.NotificationRecipients.CountAsync(r => r.UserId == userId && !r.IsRead);
        return Ok(new UnreadCountResponse(count));
    }

    [HttpPost("{id:guid}/read")]
    public async Task<IActionResult> MarkRead(Guid id)
    {
        var userId = CurrentUserId;
        var now = DateTimeOffset.UtcNow;

        // Atomic conditional update, not fetch-then-check-then-save — the
        // WHERE clause is the entire authorization check. A client-supplied
        // UserId is never trusted; CurrentUserId comes only from the
        // authenticated principal.
        var affected = await db.NotificationRecipients
            .Where(r => r.Id == id && r.UserId == userId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.IsRead, true)
                .SetProperty(r => r.ReadAt, now));

        // Zero rows affected collapses "doesn't exist" and "belongs to
        // someone else" into the same response, same idiom
        // CustomersController.Get uses for SameTenant — never a
        // distinguishable Forbidden that would let a caller probe for
        // other users' notification ids.
        return affected == 0 ? NotFound() : NoContent();
    }

    [HttpPost("read-all")]
    public async Task<IActionResult> ReadAll([FromBody] NotificationCursor cursor)
    {
        var userId = CurrentUserId;
        var now = DateTimeOffset.UtcNow;

        // Protects notifications that arrived after the cursor was
        // captured (including same-CreatedAt ties) from being silently
        // marked read — only rows at or before the cursor are touched.
        await db.NotificationRecipients
            .Where(r => r.UserId == userId && !r.IsRead)
            .Where(r =>
                r.CreatedAt < cursor.CreatedAt ||
                (r.CreatedAt == cursor.CreatedAt && r.Id <= cursor.Id))
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.IsRead, true)
                .SetProperty(r => r.ReadAt, now));

        return NoContent();
    }
}
