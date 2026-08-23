using backend.Auth;
using backend.Data;
using backend.Enums;
using backend.Interfaces;
using backend.Models;
using backend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace backend.Controllers;

public record KnowledgeDocumentResponse(
    Guid Id, string Type, string Title, string Content, bool IsActive, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public record CreateKnowledgeDocumentRequest(string Type, string Title, string Content);

public record UpdateKnowledgeDocumentRequest(string Type, string Title, string Content, bool IsActive);

/// <summary>
/// Step 10 — Knowledge Base CRUD; Step 11 — chunking/embedding wired into
/// Create/content-changing Update via IKnowledgeIndexingService. Nothing
/// here performs RAG retrieval (Step 12). Same AnyStaff + SameTenant +
/// OwnerOrAdmin-for-writes pattern CustomersController already established.
/// </summary>
[ApiController]
[Route("api/knowledge")]
[Authorize(Policy = NivoraPolicies.AnyStaff)]
public class KnowledgeDocumentsController(
    AppDbContext db,
    ICurrentTenantAccessor tenantAccessor,
    IAuthorizationService authorizationService,
    IKnowledgeIndexingService indexingService) : ControllerBase
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

        KnowledgeDocument document;
        try
        {
            document = await indexingService.CreateAsync(
                businessId, type, request.Title, request.Content, HttpContext.RequestAborted);
        }
        catch (VoyageEmbeddingException)
        {
            return StatusCode(StatusCodes.Status502BadGateway,
                "Failed to generate embeddings for this document. Nothing was saved — try again.");
        }

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
        // separate route. IKnowledgeIndexingService decides whether Content
        // actually changed and only re-chunks/re-embeds when it did.
        try
        {
            document = await indexingService.UpdateAsync(
                document, type, request.Title, request.Content, request.IsActive, HttpContext.RequestAborted);
        }
        catch (VoyageEmbeddingException)
        {
            return StatusCode(StatusCodes.Status502BadGateway,
                "Failed to generate embeddings for this update. The document was left unchanged — try again.");
        }

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
