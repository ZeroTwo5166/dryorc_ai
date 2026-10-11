using backend.Constants;
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
///
/// The HNSW index is shared by every business, and under an HNSW plan
/// Postgres applies the BusinessId predicate as a filter on what the index
/// scan returns — by default at most hnsw.ef_search (40) candidates, of
/// any business. A business whose own chunks are not among those 40 would
/// get fewer than TopK of them. So each retrieval turns on pgvector's
/// iterative index scan (hnsw.iterative_scan = relaxed_order, pgvector
/// 0.8.0 and later; Program.cs refuses to start below that), which keeps
/// scanning until TopK rows pass the filter or a scan limit
/// (hnsw.max_scan_tuples, or work_mem × hnsw.scan_mem_multiplier) ends it.
/// Past those limits the same shortfall can return: this is not
/// tenant-isolated recall at any index size.
/// </summary>
public class KnowledgeRetrievalService(AppDbContext db, IVoyageEmbeddingInvoker embeddingInvoker) : IKnowledgeRetrievalService
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
        Guid businessId, Guid conversationId, string queryText, CancellationToken cancellationToken = default)
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
            embeddings = await embeddingInvoker.EmbedAsync(new AiCallContext(businessId, AiOperations.KnowledgeRetrievalQuery, Guid.NewGuid(), 0, conversationId), [queryText], VoyageInputType.Query, cancellationToken);
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
        //
        // SET LOCAL only has an effect inside a transaction block, and it
        // must be the query's own transaction. Join the caller's if one is
        // already open on this AppDbContext (a second BeginTransactionAsync
        // would throw); otherwise own one for the two statements. Nothing
        // is caught: if the SET LOCAL fails, the exception leaves this
        // method, and a caller that wrapped this call in its own
        // transaction finds that transaction aborted. In a joined
        // transaction the setting stays in effect until the caller's
        // transaction ends; it only affects HNSW index scans, and this is
        // the only vector-distance query in the application.
        var ownedTransaction = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(cancellationToken)
            : null;
        await using (ownedTransaction)
        {
            await db.Database.ExecuteSqlRawAsync("SET LOCAL hnsw.iterative_scan = relaxed_order", cancellationToken);

            // No distance predicate here, deliberately. With it in the
            // WHERE, a query with fewer than TopK chunks under the
            // threshold could never fill its LIMIT, and the iterative scan
            // would run to its limit on every such message. The threshold
            // is applied below, to the at most TopK rows that come back.
            var nearest = await db.KnowledgeChunks
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
                .OrderBy(x => x.Distance)
                .Take(TopK)
                .Select(x => new
                {
                    ChunkId = x.Chunk.Id,
                    DocumentId = x.Document.Id,
                    x.Document.Title,
                    x.Chunk.Content,
                    x.Distance,
                })
                .ToListAsync(cancellationToken);

            if (ownedTransaction is not null)
            {
                await ownedTransaction.CommitAsync(cancellationToken);
            }

            // relaxed_order may hand rows back slightly out of distance
            // order, so the order is restored here. That corrects order
            // only: which rows came back is approximate under an HNSW plan.
            return nearest
                .Where(x => x.Distance < RelevanceThreshold)
                .OrderBy(x => x.Distance)
                .Select(x => new RetrievedKnowledgeChunk(x.ChunkId, x.DocumentId, x.Title, x.Content))
                .ToList();
        }
    }
}
