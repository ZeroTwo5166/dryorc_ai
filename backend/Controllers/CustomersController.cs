using System.Security.Claims;
using System.Text.Json;
using backend.Auth;
using backend.Constants;
using backend.Data;
using backend.Enums;
using backend.Helpers;
using backend.Interfaces;
using backend.Models;
using backend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace backend.Controllers;

public record CreateCustomerRequest(string? Email, string? Name);

public record CustomerResponse(Guid Id, Guid BusinessId, string? Email, string? Name);

/// <summary>
/// Step 14 profile-page summary, not the full paginated browse view
/// ConversationsController.List already provides elsewhere — bounded,
/// no cursor.
/// </summary>
public record CustomerConversationSummaryResponse(
    Guid Id, string Channel, string Status, string HandledBy, DateTimeOffset? LastMessageAt, DateTimeOffset CreatedAt);

/// <summary>
/// Step 14: the customer profile detail shape — everything List()'s bare
/// CustomerResponse doesn't need (IsAnonymized, ExtendedLoggingEnabled, a
/// recent-conversation summary). Kept as its own type rather than widening
/// CustomerResponse, since List() has no reason to pay for a per-row
/// conversation query.
/// </summary>
public record CustomerDetailResponse(
    Guid Id, Guid BusinessId, string? Email, string? Name, bool IsAnonymized, bool ExtendedLoggingEnabled,
    DateTimeOffset CreatedAt, List<CustomerConversationSummaryResponse> RecentConversations);

/// <summary>
/// IsCurrentlyValid is computed server-side via
/// ExternalIdentityExtensions.IsCurrentlyValid — never re-derived from
/// RevokedAt/ExpiresAt independently in a controller or the frontend, per
/// that extension's own doc comment.
/// </summary>
public record ExternalIdentityResponse(
    Guid Id, string Channel, string VerificationMethod, string ExternalUserId,
    DateTimeOffset LinkedAt, DateTimeOffset? ExpiresAt, DateTimeOffset? RevokedAt, Guid? RevokedByUserId,
    bool IsCurrentlyValid);

public record LinkExternalIdentityRequest(string Channel, string VerificationMethod, string ExternalUserId, DateTimeOffset? ExpiresAt);

public record SetExtendedLoggingRequest(bool Enabled);

/// <summary>
/// Step 4's original minimal auth-scaffolding resource, now also the
/// Step 14 customer profile / identity-linking surface. Reads: AnyStaff.
/// Writes (Create, identity link/revoke, extended-logging toggle):
/// OwnerOrAdmin — same two-tier split KnowledgeDocumentsController already
/// established. Deliberately not OwnerOnly: unlike
/// TagModeMappingsController's routing-policy control, identity linking is
/// a routine support-operations task, not a materially higher-stakes
/// safety-model lever.
/// </summary>
[ApiController]
[Route("api/customers")]
[Authorize(Policy = NivoraPolicies.AnyStaff)] // step 1: who is the authenticated user
public class CustomersController(
    AppDbContext db,
    ICurrentTenantAccessor tenantAccessor,
    IAuthorizationService authorizationService,
    IAuditLogService auditLogService) : ControllerBase
{
    private const int RecentConversationsTake = 20;

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
    public async Task<IActionResult> List()
    {
        // Step 2 (which business) comes from ICurrentTenantAccessor via the
        // cookie's BusinessId claim. Step 3 (resource ownership) is
        // structural here: AppDbContext's global tenant query filter means
        // this query can never return a row belonging to another business
        // — there is no per-row check left to make for a list query.
        var customers = await db.Customers
            .OrderBy(c => c.CreatedAt)
            .Select(c => new CustomerResponse(c.Id, c.BusinessId, c.Email, c.Name))
            .ToListAsync();

        return Ok(customers);
    }

    /// <summary>
    /// Step 14: the customer profile detail view. Was a bare CustomerResponse
    /// under Step 4's original scaffolding — widened here rather than
    /// replaced, since List() still uses the narrower shape.
    /// </summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id)
    {
        // Layer 1, defense-in-depth: the global tenant filter means this
        // can structurally never load another business's row.
        var customer = await db.Customers.FirstOrDefaultAsync(c => c.Id == id);
        if (customer is null) return NotFound();

        // Layer 2, explicit — step 3 of the checklist, not assumed just
        // because layer 1 already worked. NIVORA_DEVELOPMENT_PLAN_V2.md
        // Section 3 is explicit that query filtering alone must not be
        // treated as the complete authorization model.
        var authResult = await authorizationService.AuthorizeAsync(User, customer, NivoraPolicies.SameTenant);
        if (!authResult.Succeeded) return NotFound();

        var recentConversations = await db.Conversations
            .Where(c => c.CustomerId == id)
            .OrderByDescending(c => c.LastMessageAt ?? c.CreatedAt)
            .ThenByDescending(c => c.Id)
            .Take(RecentConversationsTake)
            .Select(c => new CustomerConversationSummaryResponse(
                c.Id, c.Channel, c.Status.ToString(), c.HandledBy.ToString(), c.LastMessageAt, c.CreatedAt))
            .ToListAsync();

        return Ok(new CustomerDetailResponse(
            customer.Id, customer.BusinessId, customer.Email, customer.Name,
            customer.IsAnonymized, customer.ExtendedLoggingEnabled, customer.CreatedAt, recentConversations));
    }

    [HttpPost]
    [Authorize(Policy = NivoraPolicies.OwnerOrAdmin)] // step 4: does the role permit this operation
    public async Task<IActionResult> Create([FromBody] CreateCustomerRequest request)
    {
        var businessId = tenantAccessor.BusinessId
            ?? throw new InvalidOperationException("Authenticated but no tenant resolved — unreachable behind [Authorize].");

        // BusinessId always comes from the authenticated principal, never
        // from the request — CreateCustomerRequest has no BusinessId field
        // at all, so there is nothing for a client-supplied value to override.
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            Email = request.Email,
            Name = request.Name,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();

        return CreatedAtAction(
            nameof(Get),
            new { id = customer.Id },
            new CustomerResponse(customer.Id, customer.BusinessId, customer.Email, customer.Name));
    }

    // ── Step 14: identity linking ───────────────────────────────────────

    [HttpGet("{customerId:guid}/identities")]
    public async Task<IActionResult> ListIdentities(Guid customerId)
    {
        var customer = await db.Customers.FirstOrDefaultAsync(c => c.Id == customerId);
        if (customer is null) return NotFound();

        var authResult = await authorizationService.AuthorizeAsync(User, customer, NivoraPolicies.SameTenant);
        if (!authResult.Succeeded) return NotFound();

        // IsCurrentlyValid(now) is plain C#, not SQL-translatable (see its
        // own doc comment) — materialize first, then map.
        var now = DateTimeOffset.UtcNow;
        var identities = await db.ExternalIdentities
            .Where(i => i.CustomerId == customerId)
            .OrderByDescending(i => i.LinkedAt)
            .ToListAsync();

        return Ok(identities.Select(i => ToIdentityResponse(i, now)).ToList());
    }

    /// <summary>
    /// Channel/VerificationMethod are validated here against the same
    /// constants classes Conversation.Channel and
    /// CK_ExternalIdentities_VerificationMethod already use —
    /// VerificationMethod is additionally backed by that DB CHECK
    /// constraint (defense in depth); Channel has no DB-level constraint,
    /// so this application-level check is its only enforcement.
    ///
    /// GDPR: rejects with 409 if the customer has already been anonymized
    /// — CustomerErasureService has permanently deleted this customer's
    /// prior identity rows by that point, and nothing about that decision
    /// should be undoable by linking a fresh one.
    ///
    /// Concurrency: the partial unique index
    /// (BusinessId, Channel, ExternalUserId) WHERE RevokedAt IS NULL is the
    /// sole concurrency authority for duplicate-link races — no advisory
    /// lock, no check-then-insert workaround. A violation is caught and
    /// translated to 409, same pattern AuthController's duplicate-email
    /// registration path already established.
    /// </summary>
    [HttpPost("{customerId:guid}/identities")]
    [Authorize(Policy = NivoraPolicies.OwnerOrAdmin)]
    public async Task<IActionResult> LinkIdentity(Guid customerId, [FromBody] LinkExternalIdentityRequest request)
    {
        var customer = await db.Customers.FirstOrDefaultAsync(c => c.Id == customerId);
        if (customer is null) return NotFound();

        var authResult = await authorizationService.AuthorizeAsync(User, customer, NivoraPolicies.SameTenant);
        if (!authResult.Succeeded) return NotFound();

        if (customer.IsAnonymized)
        {
            return Conflict("This customer has been anonymized and can no longer be linked to an identity.");
        }

        if (!IsValidChannel(request.Channel))
        {
            return BadRequest($"Invalid Channel '{request.Channel}'.");
        }

        if (!IsValidVerificationMethod(request.VerificationMethod))
        {
            return BadRequest($"Invalid VerificationMethod '{request.VerificationMethod}'.");
        }

        if (string.IsNullOrWhiteSpace(request.ExternalUserId))
        {
            return BadRequest("ExternalUserId is required.");
        }

        var now = DateTimeOffset.UtcNow;
        var identity = new ExternalIdentity
        {
            Id = Guid.NewGuid(),
            BusinessId = customer.BusinessId,
            CustomerId = customer.Id,
            Channel = request.Channel,
            VerificationMethod = request.VerificationMethod,
            ExternalUserId = request.ExternalUserId,
            LinkedAt = now,
            ExpiresAt = request.ExpiresAt,
        };
        db.ExternalIdentities.Add(identity);

        // Added to the same change tracker as the identity insert above —
        // both commit together in the one SaveChangesAsync() below, same
        // convention TagModeMappingsController.Create already established
        // (IAuditLogService never calls SaveChangesAsync() itself, by
        // design, for exactly this reason). ExternalUserId is deliberately
        // excluded from the audit payload — it can carry PII (e.g. an
        // email) depending on Channel, and AuditLog.OldValue/NewValue are
        // PII-forbidden by design; Channel/VerificationMethod/timestamps
        // are safe categorical metadata.
        await auditLogService.LogAsync(
            customer.BusinessId,
            AuditEntityTypes.ExternalIdentity,
            identity.Id.ToString(),
            AuditEventTypes.IdentityLinked,
            ActorType.NivoraBusinessStaff,
            changedByUserId: CurrentUserId,
            newValue: SerializeAuditValue(identity));

        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            return Conflict("An active identity already exists for this Channel/ExternalUserId combination.");
        }

        return CreatedAtAction(nameof(ListIdentities), new { customerId = customer.Id }, ToIdentityResponse(identity, now));
    }

    /// <summary>
    /// Idempotent — revoking an already-revoked identity is a no-op (no
    /// second audit event, no overwriting the original RevokedAt/
    /// RevokedByUserId), same "unconditional re-assignment collapses to a
    /// no-op" spirit ReturnToAi already established for its own already-Ai
    /// case. Never hard-deletes — RevokedAt/RevokedByUserId preserve the
    /// historical record; hard deletion remains GDPR erasure's job alone
    /// (CustomerErasureService).
    /// </summary>
    [HttpPost("{customerId:guid}/identities/{identityId:guid}/revoke")]
    [Authorize(Policy = NivoraPolicies.OwnerOrAdmin)]
    public async Task<IActionResult> RevokeIdentity(Guid customerId, Guid identityId)
    {
        var customer = await db.Customers.FirstOrDefaultAsync(c => c.Id == customerId);
        if (customer is null) return NotFound();

        var authResult = await authorizationService.AuthorizeAsync(User, customer, NivoraPolicies.SameTenant);
        if (!authResult.Succeeded) return NotFound();

        // Scoped to this customerId too, not just Id — collapses "doesn't
        // exist," "belongs to a different customer," and (already excluded
        // above) "belongs to a different tenant" into the same NotFound,
        // same idiom NotificationsController.MarkRead already established.
        var identity = await db.ExternalIdentities
            .FirstOrDefaultAsync(i => i.Id == identityId && i.CustomerId == customerId);
        if (identity is null) return NotFound();

        if (identity.RevokedAt is not null)
        {
            return NoContent();
        }

        var now = DateTimeOffset.UtcNow;
        identity.RevokedAt = now;
        identity.RevokedByUserId = CurrentUserId;

        await auditLogService.LogAsync(
            customer.BusinessId,
            AuditEntityTypes.ExternalIdentity,
            identity.Id.ToString(),
            AuditEventTypes.IdentityRevoked,
            ActorType.NivoraBusinessStaff,
            changedByUserId: CurrentUserId,
            newValue: SerializeAuditValue(identity));

        await db.SaveChangesAsync();

        return NoContent();
    }

    private static ExternalIdentityResponse ToIdentityResponse(ExternalIdentity identity, DateTimeOffset now) =>
        new(identity.Id, identity.Channel, identity.VerificationMethod, identity.ExternalUserId,
            identity.LinkedAt, identity.ExpiresAt, identity.RevokedAt, identity.RevokedByUserId,
            identity.IsCurrentlyValid(now));

    private static string SerializeAuditValue(ExternalIdentity identity) =>
        JsonSerializer.Serialize(new { identity.Channel, identity.VerificationMethod, identity.LinkedAt, identity.RevokedAt });

    private static bool IsValidChannel(string channel) =>
        channel is ConversationChannels.Website or ConversationChannels.Email;

    private static bool IsValidVerificationMethod(string method) =>
        method is IdentityVerificationMethods.SignedToken or IdentityVerificationMethods.Otp;

    // ── Step 14: extended-logging toggle ────────────────────────────────

    /// <summary>
    /// A separate code path from identity linking above — a fraud-
    /// monitoring dial, not identity proof, even though it lives on the
    /// same Customer table and ships under the same roadmap step.
    /// Idempotent: setting Enabled to its current value is a no-op (no new
    /// CustomerExtendedLoggingHistory row, no audit entry), same spirit as
    /// RevokeIdentity/ReturnToAi above.
    ///
    /// Deliberately does not require Business.ExtendedAuditLoggingEnabled
    /// to already be true. That flag only gates whether
    /// FlaggedCustomerActivityLog rows actually get written elsewhere (see
    /// that model's own doc comment: "two-layer-gated") — it is not a
    /// precondition for setting this per-customer opt-in, so an Owner/Admin
    /// can pre-configure customers before ever flipping the business-wide
    /// switch. The response's ExtendedLoggingEnabled value combined with a
    /// client-side read of the business record (already available via
    /// existing endpoints) is enough for the UI to explain when the toggle
    /// currently has no live effect, without this endpoint blocking the
    /// write itself.
    /// </summary>
    [HttpPut("{customerId:guid}/extended-logging")]
    [Authorize(Policy = NivoraPolicies.OwnerOrAdmin)]
    public async Task<IActionResult> SetExtendedLogging(Guid customerId, [FromBody] SetExtendedLoggingRequest request)
    {
        var customer = await db.Customers.FirstOrDefaultAsync(c => c.Id == customerId);
        if (customer is null) return NotFound();

        var authResult = await authorizationService.AuthorizeAsync(User, customer, NivoraPolicies.SameTenant);
        if (!authResult.Succeeded) return NotFound();

        if (customer.ExtendedLoggingEnabled == request.Enabled)
        {
            return NoContent();
        }

        var now = DateTimeOffset.UtcNow;
        customer.ExtendedLoggingEnabled = request.Enabled;
        customer.UpdatedAt = now;

        if (request.Enabled)
        {
            db.CustomerExtendedLoggingHistories.Add(new CustomerExtendedLoggingHistory
            {
                Id = Guid.NewGuid(),
                BusinessId = customer.BusinessId,
                CustomerId = customer.Id,
                EnabledByUserId = CurrentUserId,
                EnabledAt = now,
            });

            await auditLogService.LogAsync(
                customer.BusinessId,
                AuditEntityTypes.Customer,
                customer.Id.ToString(),
                AuditEventTypes.CustomerExtendedLoggingEnabled,
                ActorType.NivoraBusinessStaff,
                changedByUserId: CurrentUserId);
        }
        else
        {
            // At most one active (DisabledAt IS NULL) row can exist per
            // customer, per the partial unique index — that row is the one
            // being closed out here.
            var activeHistory = await db.CustomerExtendedLoggingHistories
                .SingleOrDefaultAsync(h => h.CustomerId == customer.Id && h.DisabledAt == null);
            if (activeHistory is not null)
            {
                activeHistory.DisabledByUserId = CurrentUserId;
                activeHistory.DisabledAt = now;
            }

            await auditLogService.LogAsync(
                customer.BusinessId,
                AuditEntityTypes.Customer,
                customer.Id.ToString(),
                AuditEventTypes.CustomerExtendedLoggingDisabled,
                ActorType.NivoraBusinessStaff,
                changedByUserId: CurrentUserId);
        }

        await db.SaveChangesAsync();

        return NoContent();
    }
}
