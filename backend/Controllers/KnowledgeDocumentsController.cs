using backend.Auth;
using backend.Data;
using backend.Enums;
using backend.Interfaces;
using backend.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace backend.Controllers;

public record KnowledgeDocumentResponse(
    Guid Id, string Type, string Title, string Content, bool IsActive, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public record CreateKnowledgeDocumentRequest(string Type, string Title, string Content);

public record UpdateKnowledgeDocumentRequest(string Type, string Title, string Content, bool IsActive);

/// <summary>
/// Step 10 — Knowledge Base CRUD only. Creating/updating a document never
/// touches KnowledgeChunk (chunking/embeddings are Step 11) and nothing
/// here performs RAG retrieval (Step 12). Same AnyStaff + SameTenant +
/// OwnerOrAdmin-for-writes pattern CustomersController already established.
/// </summary>
[ApiController]
[Route("api/knowledge")]
[Authorize(Policy = NivoraPolicies.AnyStaff)]
public class KnowledgeDocumentsController(
    AppDbContext db,
    ICurrentTenantAccessor tenantAccessor,
    IAuthorizationService authorizationService) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List()
    {
        // Layer 1: the global tenant query filter means this can never
        // return another business's row — no per-row check left to make
        // for a list query, same reasoning as CustomersController.List.
        // Deliberately includes inactive documents: staff manage the whole
        // knowledge base here, including reactivating a deactivated one —
        // filtering IsActive out is retrieval's job (Step 12), not this list.
        var documents = await db.KnowledgeDocuments
            .OrderByDescending(d => d.UpdatedAt)
            .Select(d => new KnowledgeDocumentResponse(
                d.Id, d.Type.ToString(), d.Title, d.Content, d.IsActive, d.CreatedAt, d.UpdatedAt))
            .ToListAsync();

        return Ok(documents);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id)
    {
        var document = await db.KnowledgeDocuments.FirstOrDefaultAsync(d => d.Id == id);
        if (document is null) return NotFound();

        // Layer 2, explicit — same SameTenant check CustomersController.Get
        // uses, not assumed redundant just because layer 1 already worked.
        var authResult = await authorizationService.AuthorizeAsync(User, document, NivoraPolicies.SameTenant);
        if (!authResult.Succeeded) return NotFound();

        return Ok(ToResponse(document));
    }

    [HttpPost]
    [Authorize(Policy = NivoraPolicies.OwnerOrAdmin)]
    public async Task<IActionResult> Create([FromBody] CreateKnowledgeDocumentRequest request)
    {
        if (!Enum.TryParse<KnowledgeDocumentType>(request.Type, out var type))
        {
            return BadRequest($"Invalid Type '{request.Type}'.");
        }

        var businessId = tenantAccessor.BusinessId
            ?? throw new InvalidOperationException("Authenticated but no tenant resolved — unreachable behind [Authorize].");

        var now = DateTimeOffset.UtcNow;
        var document = new KnowledgeDocument
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            Type = type,
            Title = request.Title,
            Content = request.Content,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.KnowledgeDocuments.Add(document);
        await db.SaveChangesAsync();

        return CreatedAtAction(nameof(Get), new { id = document.Id }, ToResponse(document));
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = NivoraPolicies.OwnerOrAdmin)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateKnowledgeDocumentRequest request)
    {
        var document = await db.KnowledgeDocuments.FirstOrDefaultAsync(d => d.Id == id);
        if (document is null) return NotFound();

        var authResult = await authorizationService.AuthorizeAsync(User, document, NivoraPolicies.SameTenant);
        if (!authResult.Succeeded) return NotFound();

        if (!Enum.TryParse<KnowledgeDocumentType>(request.Type, out var type))
        {
            return BadRequest($"Invalid Type '{request.Type}'.");
        }

        // IsActive is settable here too — reactivating a deactivated document
        // is just another field update through this same endpoint, no
        // separate route.
        document.Type = type;
        document.Title = request.Title;
        document.Content = request.Content;
        document.IsActive = request.IsActive;
        document.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        return Ok(ToResponse(document));
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = NivoraPolicies.OwnerOrAdmin)]
    public async Task<IActionResult> Deactivate(Guid id)
    {
        var document = await db.KnowledgeDocuments.FirstOrDefaultAsync(d => d.Id == id);
        if (document is null) return NotFound();

        var authResult = await authorizationService.AuthorizeAsync(User, document, NivoraPolicies.SameTenant);
        if (!authResult.Succeeded) return NotFound();

        // Soft-delete only, per KnowledgeDocument.IsActive's own doc comment:
        // MessageKnowledgeSource rows may still reference this document
        // historically, so the row itself is never physically removed.
        document.IsActive = false;
        document.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        return NoContent();
    }

    private static KnowledgeDocumentResponse ToResponse(KnowledgeDocument d) =>
        new(d.Id, d.Type.ToString(), d.Title, d.Content, d.IsActive, d.CreatedAt, d.UpdatedAt);
}
