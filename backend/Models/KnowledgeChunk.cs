using backend.Interfaces;
using Pgvector;

namespace backend.Models;

public class KnowledgeChunk : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid BusinessId { get; set; }
    public Guid KnowledgeDocumentId { get; set; }

    /// <summary>Order of this chunk within its parent document.</summary>
    public int ChunkIndex { get; set; }

    public required string Content { get; set; }

    public Vector Embedding { get; set; } = null!;

    /// <summary>
    /// Which embedding model produced this vector (e.g. "voyage-3"). Never
    /// compare embeddings from different models in the same similarity
    /// search — their vector spaces aren't compatible. Lets a future
    /// model change re-embed cleanly and filter old vs. new during rollout.
    /// </summary>
    public required string EmbeddingModel { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}