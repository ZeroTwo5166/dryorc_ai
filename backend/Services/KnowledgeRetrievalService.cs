using backend.Data;
using backend.Enums;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace backend.Services;

/// <summary>
/// pgvector cosine-distance retrieval over KnowledgeChunk, scoped to one
/// business and its active documents. Reuses the existing HNSW
/// (vector_cosine_ops) index — no new index/migration. TopK and
/// RelevanceThreshold are code constants for MVP, deliberately not
/// configuration (see the class doc comments on each).
/// </summary>
public class KnowledgeRetrievalService(AppDbContext db, IVoyageEmbeddingClient embeddingClient) : IKnowledgeRetrievalService
{
    /// <summary>Max chunks returned per query. Tune here — not scattered through the query.</summary>
    public const int TopK = 5;

    /// <summary>
    /// pgvector CosineDistance: 0 = identical, larger = less similar (this is
    /// distance, not similarity — do not confuse the two). A chunk is only
    /// considered relevant if its distance to the query is strictly less
    /// than this value. 0.5 is an initial MVP/tunable default, not a
    /// scientifically validated cutoff — revisit once real Voyage embeddings
    /// (not the test suite's synthetic fake vectors) are available to
    /// calibrate against.
    /// </summary>
    public const float RelevanceThreshold = 0.5f;

    public async Task<IReadOnlyList<RetrievedKnowledgeChunk>> RetrieveAsync(
        Guid businessId, string queryText, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<float[]> embeddings;
        try
        {
            // VoyageInputType.Query, not .Document — this is the
            // retrieval-side embedding Step 11 deliberately kept the
            // interface shape ready for. Never throws out of this method:
            // an expected Voyage failure here means "no retrieval this
            // turn," not "break the conversation" — the opposite of Step
            // 11's write-path philosophy, where the same failure types are
            // fatal to the request. See this interface's own doc comment.
            embeddings = await embeddingClient.EmbedAsync([queryText], VoyageInputType.Query, cancellationToken);
        }
        catch (VoyageEmbeddingException)
        {
            return [];
        }
        catch (VoyageNotConfiguredException)
        {
            return [];
        }

        if (embeddings.Count == 0)
        {
            return [];
        }

        var queryVector = new Vector(embeddings[0]);

        // IgnoreQueryFilters() + explicit BusinessId on BOTH sides of the
        // join, deliberately not relying on the ambient tenant query filter
        // — same defensive posture WidgetConversationService/
        // KnowledgeIndexingService already use. BusinessId comes only from
        // the caller's own explicit parameter, never derived from the
        // retrieved rows or ambient context.
        var results = await db.KnowledgeChunks
            .IgnoreQueryFilters()
            .Where(c => c.BusinessId == businessId)
            .Join(
                db.KnowledgeDocuments.IgnoreQueryFilters().Where(d => d.BusinessId == businessId && d.IsActive),
                chunk => chunk.KnowledgeDocumentId,
                document => document.Id,
                (chunk, document) => new
                {
                    Chunk = chunk,
                    Document = document,
                    Distance = chunk.Embedding.CosineDistance(queryVector),
                })
            .Where(x => x.Distance < RelevanceThreshold)
            .OrderBy(x => x.Distance)
            .Take(TopK)
            .Select(x => new RetrievedKnowledgeChunk(x.Chunk.Id, x.Document.Id, x.Document.Title, x.Chunk.Content))
            .ToListAsync(cancellationToken);

        return results;
    }
}
