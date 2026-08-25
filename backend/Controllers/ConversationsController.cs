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

/// <summary>
/// Step 15: HandledBy added alongside the existing message list so the
/// staff detail view can show current handler state and gate the "Return
/// to AI" action — previously the messages endpoint returned a bare array
/// with no conversation-level state at all.
/// </summary>
public record ConversationDetailResponse(string HandledBy, List<ConversationMessageResponse> Messages);

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
public class ConversationsController(AppDbContext db, IAuthorizationService authorizationService, ITicketSlaService ticketSlaService) : ControllerBase
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

        return Ok(new ConversationDetailResponse(conversation.HandledBy.ToString(), messages));
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
    /// Step 15 completion: when this write is a genuine Ai -> Human
    /// transition (not a second/third reply while already Human), also
    /// writes a ConversationHandoff row — ActorType.NivoraBusinessStaff,
    /// HandoffReasons.StaffManualTakeover — making the handoff log
    /// symmetric with ReturnToAi's own Human -> Ai reversal row below.
    /// "Genuine transition" is decided from a fresh, lock-protected re-read
    /// of HandledBy (db.Entry(conversation).ReloadAsync(), same pattern
    /// ReturnToAi already established — the value read before the lock was
    /// acquired can be stale relative to a concurrent reversal), not from
    /// the pre-lock value this method loaded conversation with above.
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

        // Step 15: message insert + HandledBy write + notification resolve
        // now share one explicit transaction, guarded by the same
        // conversation-scoped advisory lock ReturnToAi uses — see
        // AdvisoryLockKeyGenerator.ForConversationNotification's own doc
        // comment for why this became necessary once HandledBy became
        // bidirectional. Previously this ran as two separate, unlocked
        // implicit transactions (see the removed comment this replaced);
        // that was accepted as harmless when HandledBy could only ever move
        // one direction, but a concurrent Human→Ai reversal now needs a
        // real serialization point to produce a deterministic result rather
        // than an uncontrolled last-commit-wins outcome.
        var lockKey = AdvisoryLockKeyGenerator.ForConversationNotification(conversation.BusinessId, conversation.Id);
        await using var transaction = await db.Database.BeginTransactionAsync();
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({lockKey})");

            // Fresh read under the lock — same reasoning and same
            // ReloadAsync mechanism ReturnToAi already uses (conversation
            // is already tracked from the load above, so a second
            // db.Conversations query would just hand back that same,
            // possibly-stale tracked instance via EF's identity
            // resolution). Needed here because the write below is no
            // longer a purely idempotent overwrite: whether it represents a
            // genuine Ai -> Human transition — and therefore needs a
            // ConversationHandoff row — depends on the actual current
            // HandledBy value at the moment this lock is held, which a
            // concurrent ReturnToAi reversal may have changed since this
            // method's pre-lock load.
            await db.Entry(conversation).ReloadAsync();
            var previousHandledBy = conversation.HandledBy;

            db.Messages.Add(message);

            conversation.LastMessageAt = now;
            conversation.UpdatedAt = now;
            conversation.HandledBy = HandledBy.Human;

            // Only a genuine Ai -> Human transition is logged — a
            // second/third staff reply while already Human is not a new
            // handoff event. Mirrors ReturnToAi's own
            // "if (conversation.HandledBy == HandledBy.Human)" branch, just
            // for the opposite direction.
            if (previousHandledBy == HandledBy.Ai)
            {
                db.ConversationHandoffs.Add(new ConversationHandoff
                {
                    Id = Guid.NewGuid(),
                    BusinessId = conversation.BusinessId,
                    ConversationId = conversation.Id,
                    FromHandledBy = HandledBy.Ai,
                    ToHandledBy = HandledBy.Human,
                    Reason = HandoffReasons.StaffManualTakeover,
                    ActorType = ActorType.NivoraBusinessStaff,
                    ActorId = CurrentUserId,
                    CreatedAt = now,
                });
            }

            // Step 16: this conversation's active (non-Resolved) ticket, if
            // any, gets its FirstRespondedAt set from this Agent message —
            // never from an AI-generated one (this endpoint only ever
            // creates Role == Agent messages, confirmed above). The DB's
            // IX_Tickets_ActiveTicketPerConversation partial unique index
            // guarantees at most one such ticket exists, so this query is
            // never ambiguous. Query runs inside the lock already held for
            // this conversation, so it's inherently fresh — no ReloadAsync
            // concern applies, unlike `conversation` above, since this is
            // the first read of this Ticket in this request, not a reuse of
            // a pre-lock value. TicketSlaService.ComputeFirstRespondedAt is
            // a pure function: it returns the ticket's own current value
            // unchanged if already set (never overwritten) or null if the
            // message doesn't qualify, so assigning its result back is
            // always safe to call unconditionally. Added to the same change
            // tracker as everything else above — flushed by the single
            // SaveChangesAsync() below, no second one introduced.
            var activeTicket = await db.Tickets
                .FirstOrDefaultAsync(t => t.ConversationId == conversation.Id && t.Status != TicketStatus.Resolved);
            if (activeTicket is not null)
            {
                activeTicket.FirstRespondedAt = ticketSlaService.ComputeFirstRespondedAt(activeTicket, message);
            }

            await db.SaveChangesAsync();

            // A staff reply is the "human attention given" signal — resolve
            // any still-unread CustomerReplied notification(s) for this
            // conversation for every recipient, not just the replying staff
            // member (the underlying "needs a human" condition is
            // conversation-wide, not per-viewer). Same ExecuteUpdateAsync
            // idiom NotificationsController.MarkRead already uses. Shares
            // its "which rows count" definition with
            // WidgetConversationService's dedup check via
            // CustomerRepliedNotificationQueries — see that class's own doc
            // comment for why (audit finding: this predicate had drifted
            // into two independently-written copies).
            await CustomerRepliedNotificationQueries
                .UnreadRecipients(db, conversation.BusinessId, conversation.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.IsRead, true)
                    .SetProperty(r => r.ReadAt, now));

            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }

        return Ok(new ConversationMessageResponse(message.Id, message.Role.ToString(), message.Content, message.CreatedAt));
    }

    /// <summary>
    /// Step 15: Human → Ai reversal — the second half of HandledBy's
    /// lifecycle (Step 9 already implemented Ai → Human on a staff reply,
    /// above). Deliberately does not call IClaudeReplyService itself; the
    /// conversation simply becomes eligible for AI again on whatever
    /// customer message arrives next through the existing widget pipeline
    /// (WidgetController.PostMessage → ClaudeReplyService.GenerateReplyAsync,
    /// both untouched by this endpoint). Any customer messages sent during
    /// the Human-handled window remain ordinary persisted history — the
    /// next AI reply's full-history rebuild (ClaudeReplyService's own
    /// existing, unmodified behavior) naturally includes them as context;
    /// nothing here proactively answers them, and if the customer never
    /// sends another message, nothing else happens.
    ///
    /// AnyStaff, same as CreateMessage above — no per-handler ownership
    /// concept exists anywhere on Conversation (deliberately not
    /// introduced here), so this is symmetric with who can take a
    /// conversation over in the first place.
    ///
    /// Idempotent: calling this on an already-Ai conversation is a no-op —
    /// no ConversationHandoff row, no notification changes — same
    /// "unconditional re-assignment, no branch needed" spirit
    /// CreateMessage's own HandledBy = Human write already established,
    /// except here the branch is required specifically so a duplicate
    /// ConversationHandoff row is never written for a conversation that's
    /// already Ai (see the concurrency reasoning below).
    ///
    /// Concurrency: guarded by the same conversation-scoped advisory lock
    /// as CreateMessage's HandledBy write and
    /// WidgetConversationService.NotifyStaffOfCustomerReplyAsync's
    /// check-then-create — see AdvisoryLockKeyGenerator
    /// .ForConversationNotification's own doc comment for the full
    /// reasoning. Concretely, holding this lock for the read-decide-write
    /// sequence below guarantees: (1) two concurrent reversals can never
    /// both observe Human and both write a ConversationHandoff row — only
    /// whichever acquires the lock first sees Human and transitions; the
    /// second, now finding Ai already, no-ops; (2) a concurrent
    /// CreateMessage (Ai→Human) and this endpoint (Human→Ai) fully
    /// serialize against each other rather than racing — whichever commits
    /// last determines the final HandledBy value, which is a deterministic,
    /// well-defined outcome (not a torn or lost update), and if this
    /// endpoint is the one that read Human before CreateMessage's write
    /// landed, a genuine (if immediately superseded) handoff event is still
    /// correctly recorded — an append-only log recording a real event that
    /// happened, not a live snapshot; (3) a concurrent customer message's
    /// notification-creation check (NotifyStaffOfCustomerReplyAsync, same
    /// lock) can never race this endpoint's notification-resolve step into
    /// an inconsistent order — whichever runs first under the lock is
    /// authoritative for the other, since NotifyStaffOfCustomerReplyAsync
    /// now re-checks HandledBy fresh inside its own lock acquisition (see
    /// its own doc comment) rather than trusting a pre-lock read.
    /// </summary>
    [HttpPost("{id:guid}/return-to-ai")]
    public async Task<IActionResult> ReturnToAi(Guid id)
    {
        var conversation = await db.Conversations.FirstOrDefaultAsync(c => c.Id == id);
        if (conversation is null)
        {
            return NotFound();
        }

        var authResult = await authorizationService.AuthorizeAsync(User, conversation, NivoraPolicies.SameTenant);
        if (!authResult.Succeeded)
        {
            return NotFound();
        }

        var lockKey = AdvisoryLockKeyGenerator.ForConversationNotification(conversation.BusinessId, conversation.Id);
        await using var transaction = await db.Database.BeginTransactionAsync();
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({lockKey})");

            // Fresh read under the lock — conversation was loaded above,
            // before the lock was acquired, and may be stale relative to a
            // concurrent writer that already committed. Deliberately
            // db.Entry(conversation).ReloadAsync(), not another
            // db.Conversations.FirstAsync(...) query: conversation is
            // already tracked by this context from the load above, and EF
            // Core's identity resolution would hand back that same tracked
            // instance (with its now-stale property values) instead of
            // issuing a real query — ReloadAsync is what actually re-reads
            // this row's current values from the database.
            await db.Entry(conversation).ReloadAsync();

            if (conversation.HandledBy == HandledBy.Human)
            {
                var now = DateTimeOffset.UtcNow;
                conversation.HandledBy = HandledBy.Ai;
                conversation.UpdatedAt = now;

                db.ConversationHandoffs.Add(new ConversationHandoff
                {
                    Id = Guid.NewGuid(),
                    BusinessId = conversation.BusinessId,
                    ConversationId = conversation.Id,
                    FromHandledBy = HandledBy.Human,
                    ToHandledBy = HandledBy.Ai,
                    Reason = HandoffReasons.ResolvedReturnedToAi,
                    ActorType = ActorType.NivoraBusinessStaff,
                    ActorId = CurrentUserId,
                    CreatedAt = now,
                });

                await db.SaveChangesAsync();

                // Staff choosing to hand the conversation back to AI is
                // itself a "human attention given" signal for any customer
                // message that was still waiting on a reply — same
                // resolve-for-all-recipients semantics CreateMessage's own
                // staff-reply path already uses, via the same shared query.
                // Deliberately does not create any new notification.
                await CustomerRepliedNotificationQueries
                    .UnreadRecipients(db, conversation.BusinessId, conversation.Id)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(r => r.IsRead, true)
                        .SetProperty(r => r.ReadAt, now));
            }
            // else: already Ai — idempotent no-op. No ConversationHandoff
            // row (no genuine transition occurred), no notification change.

            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }

        return NoContent();
    }
}
