namespace backend.Services;

/// <summary>
/// One chunk retrieved for a given query, with enough context to both build
/// a Claude prompt section and write a MessageKnowledgeSource snapshot row —
/// KnowledgeRetrievalService has no knowledge of either of those; it only
/// hands back what was found.
/// </summary>
public record RetrievedKnowledgeChunk(
    Guid KnowledgeChunkId, Guid KnowledgeDocumentId, string DocumentTitle, string Content);

/// <summary>
/// Finds a business's most relevant, active KnowledgeChunks for a piece of
/// query text (Step 12 — RAG retrieval). Never throws for an expected
/// Voyage-related failure (VoyageEmbeddingException/VoyageNotConfiguredException)
/// — returns an empty list instead, since retrieval is an optional
/// enhancement to a conversation, not a hard dependency the way it is for
/// Step 11's indexing write path.
/// </summary>
public interface IKnowledgeRetrievalService
{
    Task<IReadOnlyList<RetrievedKnowledgeChunk>> RetrieveAsync(
        Guid businessId, string queryText, CancellationToken cancellationToken = default);
}
