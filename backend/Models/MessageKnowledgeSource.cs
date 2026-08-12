using backend.Interfaces;

namespace backend.Models;

/// <summary>
/// Records which knowledge chunks were used to generate a given AI
/// Message, with a content snapshot taken at generation time — so a
/// disputed AI answer can always be traced back to exactly what the
/// knowledge base said at that moment, even if the source document has
/// since been edited or deactivated. One Message can have multiple rows
/// (multiple chunks cited in one answer).
/// </summary>
public class MessageKnowledgeSource : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid BusinessId { get; set; }
    public Guid MessageId { get; set; }

    /// <summary>
    /// Nullable — the source document/chunk may since be deleted, but the
    /// snapshot fields below preserve what was actually used regardless.
    /// </summary>
    public Guid? KnowledgeDocumentId { get; set; }
    public Guid? KnowledgeChunkId { get; set; }

    /// <summary>Document title at the moment this answer was generated.</summary>
    public required string DocumentTitleSnapshot { get; set; }

    /// <summary>Chunk content at the moment this answer was generated.</summary>
    public required string ChunkContentSnapshot { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}