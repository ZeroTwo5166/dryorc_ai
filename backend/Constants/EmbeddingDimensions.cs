namespace backend.Constants;

/// <summary>
/// Vector dimension pgvector's HNSW index on KnowledgeChunk.Embedding is
/// built with. pgvector requires a fixed dimension to build an HNSW index —
/// unlike most column-shape decisions, this one can't be deferred to a
/// migration-free varchar-style constant; changing it requires a real
/// migration (new column + reindex + re-embedding all existing chunks).
/// 1024 matches Voyage AI's "voyage-3" model, the example already used in
/// KnowledgeChunk.EmbeddingModel's doc comment.
/// </summary>
public static class EmbeddingDimensions
{
    public const int VoyageThree = 1024;
}
