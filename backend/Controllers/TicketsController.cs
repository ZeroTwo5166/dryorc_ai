using System.Security.Claims;
using System.Text.Json;
using backend.Auth;
using backend.Constants;
using backend.Data;
using backend.Enums;
using backend.Hubs;
using backend.Interfaces;
using backend.Models;
using backend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace backend.Controllers;

public record TicketCursor(DateTimeOffset CreatedAt, Guid Id);

public record TicketResponse(
    Guid Id, Guid CustomerId, Guid? ConversationId, string Status, string Priority, string PriorityReason,
    string Channel, Guid? ClaimedByUserId, DateTimeOffset? ClaimedAt, Guid? ResolvedByUserId, DateTimeOffset? ResolvedAt,
    DateTimeOffset? FirstRespondedAt, DateTimeOffset? SlaDeadline, string? Summary,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public record TicketListResponse(List<TicketResponse> Tickets, TicketCursor? NextCursor);

/// <summary>
/// Step 16 explicit-creation-only request. Channel is derived from the
/// Conversation when one is supplied (never trusted from the client in
/// that case); only required from the client for a conversationless
/// ticket, matching Ticket.Channel's own doc comment ("snapshot ... from
/// Conversation.Channel when available, otherwise selected by
/// staff/system for a conversationless ticket").
/// </summary>
public record CreateTicketRequest(Guid CustomerId, Guid? ConversationId, string? Channel, string Priority, string PriorityReason, string? Summary);

public record UpdateTicketPriorityRequest(string Priority);

/// <summary>
/// Step 16. Reads: AnyStaff. All mutations: AnyStaff (Decision 3 —
/// deliberately no claimant-only/Owner-Admin override; every ticket
/// mutation uses the same tier as every read here). Same two-layer
/// tenant pattern as every other controller: the global query filter
/// (Layer 1) plus an explicit SameTenant check on the loaded resource
/// (Layer 2).
///
/// Ticket creation is an explicit staff action only (Decision 1) — no
/// automatic trigger exists anywhere in this controller or elsewhere;
/// RefundLimitReached's automatic path remains Step 20's job.
///
/// Concurrency: Create relies solely on the DB partial unique index
/// (IX_Tickets_ActiveTicketPerConversation) + a caught 23505 → 409,
/// same pattern as CustomersController.LinkIdentity. Claim/Unclaim/
/// Resolve use an atomic ExecuteUpdateAsync conditional update (the
/// WHERE clause is the entire race guard), same idiom
/// NotificationsController.MarkRead already established — because
/// ExecuteUpdateAsync bypasses the change tracker, each of those three
/// wraps its conditional update + audit-log write in one explicit
/// transaction so the two cannot commit independently, mirroring
/// ConversationsController.CreateMessage's own explicit-transaction
/// shape (which also combines an ExecuteUpdateAsync step with a tracked
/// SaveChangesAsync inside one transaction). No advisory lock is used
/// anywhere in this controller.
/// </summary>
[ApiController]
[Route("api/tickets")]
[Authorize(Policy = NivoraPolicies.AnyStaff)]
public class TicketsController(
    AppDbContext db,
    ICurrentTenantAccessor tenantAccessor,
    IAuthorizationService authorizationService,
    IAuditLogService auditLogService,
    ITicketSlaService ticketSlaService,
    IHubContext<TicketHub> hubContext,
    ILogger<TicketsController> logger) : ControllerBase
{
    private const int ListTake = 20;

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
        [FromQuery] string? status = null,
        [FromQuery] int take = ListTake,
        [FromQuery] DateTimeOffset? beforeCreatedAt = null,
        [FromQuery] Guid? beforeId = null)
    {
        take = Math.Clamp(take, 1, 100);

        // Layer 1: the global tenant query filter means this can never
        // return another business's row — no per-row check left to make
        // for a list query, same reasoning as every other List() in this
        // codebase.
        var query = db.Tickets.AsQueryable();

        if (status is not null)
        {
            if (!Enum.TryParse<TicketStatus>(status, out var parsedStatus))
            {
                return BadRequest($"Invalid status '{status}'.");
            }
            query = query.Where(t => t.Status == parsedStatus);
        }

        // Cursor is (CreatedAt, Id), not (ActivityAt, Id) — unlike
        // Conversation.LastMessageAt, Ticket.CreatedAt never mutates after
        // insert, so the "own its instability" naming ConversationCursor
        // uses does not apply here. Same mechanics as
        // ConversationsController.List/NotificationsController.List
        // otherwise (strict less-than boundary, Take(take+1)/hasMore).
        if (beforeCreatedAt is { } cursorCreatedAt && beforeId is { } cursorId)
        {
            query = query.Where(t =>
                t.CreatedAt < cursorCreatedAt ||
                (t.CreatedAt == cursorCreatedAt && t.Id < cursorId));
        }

        var page = await query
            .OrderByDescending(t => t.CreatedAt)
            .ThenByDescending(t => t.Id)
            .Take(take + 1)
            .ToListAsync();

        var hasMore = page.Count > take;
        var pageItems = hasMore ? page.Take(take).ToList() : page;

        var tickets = pageItems.Select(ToResponse).ToList();
        TicketCursor? nextCursor = hasMore
            ? new TicketCursor(pageItems[^1].CreatedAt, pageItems[^1].Id)
            : null;

        return Ok(new TicketListResponse(tickets, nextCursor));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id)
    {
        var ticket = await db.Tickets.FirstOrDefaultAsync(t => t.Id == id);
        if (ticket is null) return NotFound();

        var authResult = await authorizationService.AuthorizeAsync(User, ticket, NivoraPolicies.SameTenant);
        if (!authResult.Succeeded) return NotFound();

        return Ok(ToResponse(ticket));
    }

    /// <summary>
    /// Explicit staff creation only (Decision 1). Channel is derived from
    /// the Conversation when one is supplied — the client's own Channel
    /// value, if any, is ignored in that case, same "never trust the
    /// client for a value that can be derived from an already-authorized
    /// resource" discipline as BusinessId. Duplicate-active-ticket
    /// protection is entirely the DB partial unique index's job — no
    /// pre-check query, no advisory lock (Decision 2).
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateTicketRequest request)
    {
        var businessId = tenantAccessor.BusinessId
            ?? throw new InvalidOperationException("Authenticated but no tenant resolved — unreachable behind [Authorize].");

        // Layer 1 (global filter) + explicit BusinessId re-check: Customer
        // must resolve within this tenant before anything else proceeds.
        var customer = await db.Customers.FirstOrDefaultAsync(c => c.Id == request.CustomerId);
        if (customer is null)
        {
            return BadRequest("Invalid CustomerId.");
        }

        string channel;
        Conversation? conversation = null;
        if (request.ConversationId is { } conversationId)
        {
            conversation = await db.Conversations.FirstOrDefaultAsync(c => c.Id == conversationId);
            if (conversation is null)
            {
                return BadRequest("Invalid ConversationId.");
            }
            if (conversation.CustomerId != request.CustomerId)
            {
                return BadRequest("ConversationId does not belong to the supplied CustomerId.");
            }
            channel = conversation.Channel;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(request.Channel) || !IsValidChannel(request.Channel))
            {
                return BadRequest("A valid Channel is required when ConversationId is not supplied.");
            }
            channel = request.Channel;
        }

        if (!Enum.TryParse<TicketPriority>(request.Priority, out var priority))
        {
            return BadRequest($"Invalid Priority '{request.Priority}'.");
        }

        if (!IsValidPriorityReason(request.PriorityReason))
        {
            return BadRequest($"Invalid PriorityReason '{request.PriorityReason}'.");
        }

        var now = DateTimeOffset.UtcNow;

        // No default invented, no rejection when absent — the caller
        // (TicketSlaService) already returns null, never throws, when
        // neither an exact-channel nor a fallback SlaTarget exists
        // (Decision 5). Unchanged, consumed as-is.
        var slaDeadline = await ticketSlaService.ComputeSlaDeadlineAsync(businessId, priority, channel, now);

        var ticket = new Ticket
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            CustomerId = customer.Id,
            ConversationId = conversation?.Id,
            Status = TicketStatus.Open,
            Priority = priority,
            PriorityReason = request.PriorityReason,
            Channel = channel,
            SlaDeadline = slaDeadline,
            Summary = request.Summary,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Tickets.Add(ticket);

        // Same change tracker as the ticket insert above — both commit
        // together in the one SaveChangesAsync() below. Summary is
        // deliberately excluded from the audit payload (free text, may
        // contain customer PII); Priority/PriorityReason/Status/Channel
        // are safe categorical metadata.
        await auditLogService.LogAsync(
            businessId, AuditEntityTypes.Ticket, ticket.Id.ToString(), AuditEventTypes.TicketCreated,
            ActorType.NivoraBusinessStaff, changedByUserId: CurrentUserId,
            newValue: SerializeAuditValue(ticket));

        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            // IX_Tickets_ActiveTicketPerConversation is the sole authority
            // for "at most one non-Resolved ticket per conversation"
            // (Decision 2) — no pre-check query, translate the DB's own
            // rejection, same pattern CustomersController.LinkIdentity
            // already established.
            return Conflict("An active (non-Resolved) ticket already exists for this conversation.");
        }

        await PublishTicketEventAsync(businessId, ticket.Id, AuditEventTypes.TicketCreated);

        return CreatedAtAction(nameof(Get), new { id = ticket.Id }, ToResponse(ticket));
    }

    /// <summary>
    /// Only Open → InProgress. The WHERE clause (Status == Open AND
    /// ClaimedByUserId IS NULL) is the entire race guard — same
    /// "atomic conditional update, not fetch-then-check-then-save" idiom
    /// NotificationsController.MarkRead already established. Wrapped in an
    /// explicit transaction solely so the ExecuteUpdateAsync (which
    /// bypasses the change tracker) and the audit-log write (which does
    /// not) cannot commit independently.
    /// </summary>
    [HttpPost("{id:guid}/claim")]
    public async Task<IActionResult> Claim(Guid id)
    {
        var ticket = await db.Tickets.FirstOrDefaultAsync(t => t.Id == id);
        if (ticket is null) return NotFound();

        var authResult = await authorizationService.AuthorizeAsync(User, ticket, NivoraPolicies.SameTenant);
        if (!authResult.Succeeded) return NotFound();

        var now = DateTimeOffset.UtcNow;
        await using var transaction = await db.Database.BeginTransactionAsync();
        try
        {
            var affected = await db.Tickets
                .Where(t => t.Id == id && t.Status == TicketStatus.Open && t.ClaimedByUserId == null)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.Status, TicketStatus.InProgress)
                    .SetProperty(t => t.ClaimedByUserId, CurrentUserId)
                    .SetProperty(t => t.ClaimedAt, now)
                    .SetProperty(t => t.UpdatedAt, now));

            if (affected == 0)
            {
                await transaction.RollbackAsync();
                return Conflict("Ticket is not open or has already been claimed.");
            }

            await auditLogService.LogAsync(
                ticket.BusinessId, AuditEntityTypes.Ticket, ticket.Id.ToString(), AuditEventTypes.TicketClaimed,
                ActorType.NivoraBusinessStaff, changedByUserId: CurrentUserId,
                newValue: SerializeAuditValue(TicketStatus.InProgress, ticket.Priority, CurrentUserId, null));

            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }

        await PublishTicketEventAsync(ticket.BusinessId, ticket.Id, AuditEventTypes.TicketClaimed);
        return NoContent();
    }

    /// <summary>Only InProgress → Open. Same atomic-conditional-update + explicit-transaction shape as Claim.</summary>
    [HttpPost("{id:guid}/unclaim")]
    public async Task<IActionResult> Unclaim(Guid id)
    {
        var ticket = await db.Tickets.FirstOrDefaultAsync(t => t.Id == id);
        if (ticket is null) return NotFound();

        var authResult = await authorizationService.AuthorizeAsync(User, ticket, NivoraPolicies.SameTenant);
        if (!authResult.Succeeded) return NotFound();

        var now = DateTimeOffset.UtcNow;
        await using var transaction = await db.Database.BeginTransactionAsync();
        try
        {
            var affected = await db.Tickets
                .Where(t => t.Id == id && t.Status == TicketStatus.InProgress)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.Status, TicketStatus.Open)
                    .SetProperty(t => t.ClaimedByUserId, (Guid?)null)
                    .SetProperty(t => t.ClaimedAt, (DateTimeOffset?)null)
                    .SetProperty(t => t.UpdatedAt, now));

            if (affected == 0)
            {
                await transaction.RollbackAsync();
                return Conflict("Ticket is not currently claimed.");
            }

            await auditLogService.LogAsync(
                ticket.BusinessId, AuditEntityTypes.Ticket, ticket.Id.ToString(), AuditEventTypes.TicketUnclaimed,
                ActorType.NivoraBusinessStaff, changedByUserId: CurrentUserId,
                newValue: SerializeAuditValue(TicketStatus.Open, ticket.Priority, null, null));

            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }

        await PublishTicketEventAsync(ticket.BusinessId, ticket.Id, AuditEventTypes.TicketUnclaimed);
        return NoContent();
    }

    /// <summary>
    /// Only InProgress → Resolved. Idempotent: an already-Resolved ticket
    /// returns 204 with no mutation and no new audit event, never
    /// overwriting the original resolver/timestamp. Correctly
    /// race-safe under two simultaneous resolves — not merely by
    /// coincidence: the ExecuteUpdateAsync's WHERE clause lets Postgres's
    /// own row-level locking decide exactly one winner; the loser's
    /// affected-count is 0, and a fresh re-read (not the stale pre-
    /// transaction snapshot) then correctly distinguishes "someone else
    /// just resolved it a moment ago" (200/204, idempotent) from "this
    /// ticket was never InProgress at all" (409).
    /// </summary>
    [HttpPost("{id:guid}/resolve")]
    public async Task<IActionResult> Resolve(Guid id)
    {
        var ticket = await db.Tickets.FirstOrDefaultAsync(t => t.Id == id);
        if (ticket is null) return NotFound();

        var authResult = await authorizationService.AuthorizeAsync(User, ticket, NivoraPolicies.SameTenant);
        if (!authResult.Succeeded) return NotFound();

        var now = DateTimeOffset.UtcNow;
        await using var transaction = await db.Database.BeginTransactionAsync();
        try
        {
            var affected = await db.Tickets
                .Where(t => t.Id == id && t.Status == TicketStatus.InProgress)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.Status, TicketStatus.Resolved)
                    .SetProperty(t => t.ResolvedByUserId, CurrentUserId)
                    .SetProperty(t => t.ResolvedAt, now)
                    .SetProperty(t => t.UpdatedAt, now));

            if (affected == 0)
            {
                var currentStatus = await db.Tickets.Where(t => t.Id == id).Select(t => t.Status).SingleAsync();
                await transaction.CommitAsync(); // nothing mutated; commits a no-op read
                return currentStatus == TicketStatus.Resolved
                    ? NoContent() // idempotent — already resolved, original resolver/timestamp untouched
                    : Conflict("Ticket must be InProgress to resolve.");
            }

            await auditLogService.LogAsync(
                ticket.BusinessId, AuditEntityTypes.Ticket, ticket.Id.ToString(), AuditEventTypes.TicketResolved,
                ActorType.NivoraBusinessStaff, changedByUserId: CurrentUserId,
                newValue: SerializeAuditValue(TicketStatus.Resolved, ticket.Priority, null, CurrentUserId));

            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }

        // Only reached on the genuine InProgress -> Resolved transition
        // above — the idempotent already-Resolved branch returns early
        // (before this point) and never publishes a second TicketResolved.
        await PublishTicketEventAsync(ticket.BusinessId, ticket.Id, AuditEventTypes.TicketResolved);
        return NoContent();
    }

    /// <summary>
    /// Only Resolved → Reopened. Clearing ClaimedByUserId/ClaimedAt/
    /// ResolvedByUserId/ResolvedAt is deliberate, not an oversight — those
    /// fields represent current state, and a reopened ticket is by
    /// definition no longer resolved or claimed. The historical fact that
    /// a resolution (and this reopening) occurred is preserved in
    /// AuditLog (TicketResolved's row is untouched; this method adds a
    /// TicketReopened row), not in these now-cleared live fields.
    /// </summary>
    [HttpPost("{id:guid}/reopen")]
    public async Task<IActionResult> Reopen(Guid id)
    {
        var ticket = await db.Tickets.FirstOrDefaultAsync(t => t.Id == id);
        if (ticket is null) return NotFound();

        var authResult = await authorizationService.AuthorizeAsync(User, ticket, NivoraPolicies.SameTenant);
        if (!authResult.Succeeded) return NotFound();

        var now = DateTimeOffset.UtcNow;
        await using var transaction = await db.Database.BeginTransactionAsync();
        try
        {
            var affected = await db.Tickets
                .Where(t => t.Id == id && t.Status == TicketStatus.Resolved)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.Status, TicketStatus.Reopened)
                    .SetProperty(t => t.ClaimedByUserId, (Guid?)null)
                    .SetProperty(t => t.ClaimedAt, (DateTimeOffset?)null)
                    .SetProperty(t => t.ResolvedByUserId, (Guid?)null)
                    .SetProperty(t => t.ResolvedAt, (DateTimeOffset?)null)
                    .SetProperty(t => t.UpdatedAt, now));

            if (affected == 0)
            {
                await transaction.RollbackAsync();
                return Conflict("Ticket must be Resolved to reopen.");
            }

            await auditLogService.LogAsync(
                ticket.BusinessId, AuditEntityTypes.Ticket, ticket.Id.ToString(), AuditEventTypes.TicketReopened,
                ActorType.NivoraBusinessStaff, changedByUserId: CurrentUserId,
                newValue: SerializeAuditValue(TicketStatus.Reopened, ticket.Priority, null, null));

            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }

        await PublishTicketEventAsync(ticket.BusinessId, ticket.Id, AuditEventTypes.TicketReopened);
        return NoContent();
    }

    /// <summary>
    /// Permitted regardless of lifecycle state (Decision 4) — no
    /// conditional WHERE needed, so a plain tracked-entity update is
    /// sufficient; the ticket mutation and the audit-log write already
    /// share one change tracker and one SaveChangesAsync(), same as
    /// CustomersController.LinkIdentity — no explicit transaction wrapper
    /// needed here, unlike Claim/Unclaim/Resolve/Reopen, since there is no
    /// ExecuteUpdateAsync step to keep atomic with the audit write.
    /// </summary>
    [HttpPut("{id:guid}/priority")]
    public async Task<IActionResult> UpdatePriority(Guid id, [FromBody] UpdateTicketPriorityRequest request)
    {
        var ticket = await db.Tickets.FirstOrDefaultAsync(t => t.Id == id);
        if (ticket is null) return NotFound();

        var authResult = await authorizationService.AuthorizeAsync(User, ticket, NivoraPolicies.SameTenant);
        if (!authResult.Succeeded) return NotFound();

        if (!Enum.TryParse<TicketPriority>(request.Priority, out var priority))
        {
            return BadRequest($"Invalid Priority '{request.Priority}'.");
        }

        var oldValue = SerializeAuditValue(ticket);
        ticket.Priority = priority;
        ticket.UpdatedAt = DateTimeOffset.UtcNow;

        await auditLogService.LogAsync(
            ticket.BusinessId, AuditEntityTypes.Ticket, ticket.Id.ToString(), AuditEventTypes.TicketPriorityChanged,
            ActorType.NivoraBusinessStaff, changedByUserId: CurrentUserId,
            oldValue: oldValue, newValue: SerializeAuditValue(ticket));

        await db.SaveChangesAsync();

        await PublishTicketEventAsync(ticket.BusinessId, ticket.Id, AuditEventTypes.TicketPriorityChanged);
        return NoContent();
    }

    /// <summary>
    /// Step 13B. Always called strictly after a successful commit — never
    /// before, never on a losing/no-op branch. Broadcast failures are
    /// caught and logged here, never allowed to turn an already-successful
    /// mutation into a 500 or trigger a second database attempt; SignalR is
    /// an invalidation signal only, never load-bearing for the HTTP
    /// response already determined above this call.
    /// </summary>
    private async Task PublishTicketEventAsync(Guid businessId, Guid ticketId, string eventType)
    {
        try
        {
            await hubContext.Clients.Group(TicketHub.GroupName(businessId))
                .SendAsync("TicketEvent", new TicketEvent(ticketId, eventType));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to publish {EventType} SignalR event for ticket {TicketId}.", eventType, ticketId);
        }
    }

    private static TicketResponse ToResponse(Ticket t) => new(
        t.Id, t.CustomerId, t.ConversationId, t.Status.ToString(), t.Priority.ToString(), t.PriorityReason,
        t.Channel, t.ClaimedByUserId, t.ClaimedAt, t.ResolvedByUserId, t.ResolvedAt,
        t.FirstRespondedAt, t.SlaDeadline, t.Summary, t.CreatedAt, t.UpdatedAt);

    private static string SerializeAuditValue(Ticket t) =>
        SerializeAuditValue(t.Status, t.Priority, t.ClaimedByUserId, t.ResolvedByUserId);

    private static string SerializeAuditValue(TicketStatus status, TicketPriority priority, Guid? claimedByUserId, Guid? resolvedByUserId) =>
        JsonSerializer.Serialize(new
        {
            Status = status.ToString(),
            Priority = priority.ToString(),
            ClaimedByUserId = claimedByUserId,
            ResolvedByUserId = resolvedByUserId,
        });

    private static bool IsValidChannel(string channel) =>
        channel is ConversationChannels.Website or ConversationChannels.Email;

    private static bool IsValidPriorityReason(string reason) =>
        reason is TicketPriorityReasons.RefundDispute or TicketPriorityReasons.ManualEscalation or TicketPriorityReasons.RefundLimitReached;
}
