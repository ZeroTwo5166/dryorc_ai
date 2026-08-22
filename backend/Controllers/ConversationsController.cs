using backend.Auth;
using backend.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace backend.Controllers;

public record ConversationSummaryResponse(
    Guid Id, string Channel, string Status, string HandledBy, DateTimeOffset? LastMessageAt, DateTimeOffset CreatedAt);

public record ConversationMessageResponse(Guid Id, string Role, string Content, DateTimeOffset CreatedAt);

/// <summary>
/// Backend verification/read surface for Steps 6-7 — NOT the dashboard
/// inbox UI (Step 9, a separate, later pass). Exists so a posted widget
/// message can actually be confirmed to have landed correctly. Same
/// AnyStaff + SameTenant two-layer pattern CustomersController already
/// established — nothing new invented here.
/// </summary>
[ApiController]
[Route("api/conversations")]
[Authorize(Policy = NivoraPolicies.AnyStaff)]
public class ConversationsController(AppDbContext db, IAuthorizationService authorizationService) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List()
    {
        // Layer 1: the global tenant query filter means this can never
        // return another business's row — no per-row check left to make
        // for a list query, same reasoning as CustomersController.List.
        var conversations = await db.Conversations
            .OrderByDescending(c => c.LastMessageAt ?? c.CreatedAt)
            .Select(c => new ConversationSummaryResponse(
                c.Id, c.Channel, c.Status.ToString(), c.HandledBy.ToString(), c.LastMessageAt, c.CreatedAt))
            .ToListAsync();

        return Ok(conversations);
    }

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
