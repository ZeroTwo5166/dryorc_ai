using System.Security.Claims;
using System.Text.Json;
using backend.Auth;
using backend.Constants;
using backend.Data;
using backend.Enums;
using backend.Interfaces;
using backend.Models;
using backend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace backend.Controllers;

public record TagModeMappingResponse(
    Guid Id, string Tag, string Mode, bool IsMinimumFloor, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public record CreateTagModeMappingRequest(string Tag, string Mode, bool IsMinimumFloor);

public record UpdateTagModeMappingRequest(string Tag, string Mode, bool IsMinimumFloor);

/// <summary>
/// Step 13A. Reads: AnyStaff. Writes: OwnerOnly — deliberately not
/// OwnerOrAdmin (every other write-gated controller in this codebase uses
/// OwnerOrAdmin; this table governs whether the AI can act autonomously on
/// tagged conversations, a materially higher-stakes category, per an
/// explicit decision). Same SameTenant two-layer pattern
/// KnowledgeDocumentsController already established. No Delete endpoint —
/// the documented AuditLog requirement only covers create/update, and this
/// table has no soft-delete field; hard-delete semantics were never
/// specified, so none are implemented.
/// </summary>
[ApiController]
[Route("api/tag-mode-mappings")]
[Authorize(Policy = NivoraPolicies.AnyStaff)]
public class TagModeMappingsController(
    AppDbContext db,
    ICurrentTenantAccessor tenantAccessor,
    IAuthorizationService authorizationService,
    IAuditLogService auditLogService) : ControllerBase
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
    public async Task<IActionResult> List()
    {
        // Layer 1: the global tenant query filter means this can never
        // return another business's row — no per-row check left to make
        // for a list query, same reasoning as CustomersController.List.
        var mappings = await db.TagModeMappings
            .OrderBy(m => m.Tag)
            .Select(m => new TagModeMappingResponse(m.Id, m.Tag, m.Mode.ToString(), m.IsMinimumFloor, m.CreatedAt, m.UpdatedAt))
            .ToListAsync();

        return Ok(mappings);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id)
    {
        var mapping = await db.TagModeMappings.FirstOrDefaultAsync(m => m.Id == id);
        if (mapping is null) return NotFound();

        // Layer 2, explicit — same SameTenant check CustomersController.Get/
        // KnowledgeDocumentsController.Get use, not assumed redundant just
        // because layer 1 already worked.
        var authResult = await authorizationService.AuthorizeAsync(User, mapping, NivoraPolicies.SameTenant);
        if (!authResult.Succeeded) return NotFound();

        return Ok(ToResponse(mapping));
    }

    [HttpPost]
    [Authorize(Policy = NivoraPolicies.OwnerOnly)]
    public async Task<IActionResult> Create([FromBody] CreateTagModeMappingRequest request)
    {
        if (!Enum.TryParse<ConversationMode>(request.Mode, out var mode) || !Enum.IsDefined(mode))
        {
            return BadRequest($"Invalid Mode '{request.Mode}'.");
        }

        var businessId = tenantAccessor.BusinessId
            ?? throw new InvalidOperationException("Authenticated but no tenant resolved — unreachable behind [Authorize].");

        var now = DateTimeOffset.UtcNow;
        var mapping = new TagModeMapping
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            Tag = request.Tag,
            Mode = mode,
            IsMinimumFloor = request.IsMinimumFloor,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.TagModeMappings.Add(mapping);

        // Audit entry added to the same change tracker as the mapping
        // insert above — both commit together in the one SaveChangesAsync()
        // below, never as two separate writes. No pre-existing production
        // AuditLog write exists anywhere to follow as precedent for the
        // OldValue/NewValue shape — this establishes the convention (a
        // small JSON object of only the fields that matter for this table,
        // never the full entity, never PII) rather than following one.
        await auditLogService.LogAsync(
            businessId,
            AuditEntityTypes.TagModeMapping,
            mapping.Id.ToString(),
            AuditEventTypes.TagModeMappingChanged,
            ActorType.NivoraBusinessStaff,
            changedByUserId: CurrentUserId,
            newValue: SerializeAuditValue(mapping));

        await db.SaveChangesAsync();

        return CreatedAtAction(nameof(Get), new { id = mapping.Id }, ToResponse(mapping));
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = NivoraPolicies.OwnerOnly)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateTagModeMappingRequest request)
    {
        var mapping = await db.TagModeMappings.FirstOrDefaultAsync(m => m.Id == id);
        if (mapping is null) return NotFound();

        var authResult = await authorizationService.AuthorizeAsync(User, mapping, NivoraPolicies.SameTenant);
        if (!authResult.Succeeded) return NotFound();

        if (!Enum.TryParse<ConversationMode>(request.Mode, out var mode) || !Enum.IsDefined(mode))
        {
            return BadRequest($"Invalid Mode '{request.Mode}'.");
        }

        var oldValue = SerializeAuditValue(mapping);

        mapping.Tag = request.Tag;
        mapping.Mode = mode;
        mapping.IsMinimumFloor = request.IsMinimumFloor;
        mapping.UpdatedAt = DateTimeOffset.UtcNow;

        await auditLogService.LogAsync(
            mapping.BusinessId,
            AuditEntityTypes.TagModeMapping,
            mapping.Id.ToString(),
            AuditEventTypes.TagModeMappingChanged,
            ActorType.NivoraBusinessStaff,
            changedByUserId: CurrentUserId,
            oldValue: oldValue,
            newValue: SerializeAuditValue(mapping));

        await db.SaveChangesAsync();

        return Ok(ToResponse(mapping));
    }

    private static string SerializeAuditValue(TagModeMapping mapping) =>
        JsonSerializer.Serialize(new { mapping.Tag, Mode = mapping.Mode.ToString(), mapping.IsMinimumFloor });

    private static TagModeMappingResponse ToResponse(TagModeMapping m) =>
        new(m.Id, m.Tag, m.Mode.ToString(), m.IsMinimumFloor, m.CreatedAt, m.UpdatedAt);
}
