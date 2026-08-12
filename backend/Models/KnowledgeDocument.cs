using backend.Enums;
using backend.Interfaces;

namespace backend.Models;

public class KnowledgeDocument : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid BusinessId { get; set; }

    public KnowledgeDocumentType Type { get; set; }
    public required string Title { get; set; }
    public required string Content { get; set; }

    /// <summary>
    /// Soft-delete/deactivation, consistent with the rest of the schema.
    /// A deactivated document should be excluded from RAG retrieval and
    /// re-chunking, but not hard-deleted — MessageKnowledgeSource rows may
    /// still reference it via KnowledgeDocumentId for historical AI answers.
    /// </summary>
    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}