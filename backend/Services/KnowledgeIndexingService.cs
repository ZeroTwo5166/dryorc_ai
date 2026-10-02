using backend.Constants;
using backend.Data;
using backend.Enums;
using backend.Models;
using Microsoft.EntityFrameworkCore;
using Pgvector;

namespace backend.Services;

public interface IKnowledgeIndexingService
{
    /// <summary>
    /// Chunks and embeds content, then persists a new KnowledgeDocument
    /// together with its complete chunk set in one atomic write. Embedding
    /// happens entirely before any database write begins — a Voyage failure
    /// (VoyageEmbeddingException) leaves nothing persisted at all.
    /// </summary>
    Task<KnowledgeDocument> CreateAsync(
        Guid businessId, KnowledgeDocumentType type, string title, string content,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an existing, already-loaded (and already tenant/authorization
    /// checked by the caller) KnowledgeDocument. If content is unchanged from
    /// its current persisted value, only Type/Title/IsActive are updated and
    /// no chunk/Voyage work happens at all — covers ordinary metadata edits,
    /// deactivation, and reactivation, all without touching chunks. If
    /// content changed, the document update and a full chunk replacement
    /// (delete existing, insert freshly-embedded new ones) commit together in
    /// one transaction, guarded by an advisory lock keyed on the document —
    /// never as two separate commits.
    /// </summary>
    Task<KnowledgeDocument> UpdateAsync(
        KnowledgeDocument document, KnowledgeDocumentType type, string title, string content, bool isActive,
        CancellationToken cancellationToken = default);
}

public class KnowledgeIndexingService(AppDbContext db, IVoyageEmbeddingInvoker embeddingInvoker) : IKnowledgeIndexingService
{
    public async Task<KnowledgeDocument> CreateAsync(
        Guid businessId, KnowledgeDocumentType type, string title, string content,
        CancellationToken cancellationToken = default)
    {
        var chunkTexts = KnowledgeChunker.Chunk(content);

        // Voyage call happens before any database write — never hold a
        // transaction open across an external HTTP call, same rule
        // ClaudeReplyService/WidgetConversationService already follow. If
        // this throws, nothing below has run yet: no document, no chunks.
        // A1 Slice 4b design §2.1/§3: this method starts its own logical
        // operation with at most one provider call, so it owns a fresh
        // CorrelationId and Sequence 0 (v11 §4:154).
        var embeddings = chunkTexts.Count > 0
            ? await embeddingInvoker.EmbedAsync(new AiCallContext(businessId, AiOperations.KnowledgeIndexing, Guid.NewGuid(), 0), chunkTexts, VoyageInputType.Document, cancellationToken)
            : [];

        var now = DateTimeOffset.UtcNow;
        var document = new KnowledgeDocument
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            Type = type,
            Title = title,
            Content = content,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.KnowledgeDocuments.Add(document);
        db.KnowledgeChunks.AddRange(BuildChunkRows(document, chunkTexts, embeddings, now));

        // A brand-new document has no prior chunks, and no concurrent writer
        // can target its (not-yet-existing) Id — no advisory lock needed
        // here, only UpdateAsync's re-index path needs one. One
        // SaveChangesAsync already wraps the document insert and every chunk
        // insert in a single implicit transaction/commit — the document is
        // never persisted with a partial chunk set.
        await db.SaveChangesAsync(cancellationToken);

        return document;
    }

    public async Task<KnowledgeDocument> UpdateAsync(
        KnowledgeDocument document, KnowledgeDocumentType type, string title, string content, bool isActive,
        CancellationToken cancellationToken = default)
    {
        var contentChanged = document.Content != content;

        if (!contentChanged)
        {
            // Covers plain metadata edits, deactivation, and reactivation —
            // all with content unchanged: no chunk touch, no Voyage call.
            document.Type = type;
            document.Title = title;
            document.IsActive = isActive;
            document.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return document;
        }

        var chunkTexts = KnowledgeChunker.Chunk(content);

        // Same rule as CreateAsync: Voyage call fully completes before any
        // database write. If this throws, the transaction below never
        // opens — document.Content/Type/Title/IsActive are never mutated,
        // so the document and its existing chunks are left exactly as they
        // were. Its own logical operation, separate from CreateAsync's: a
        // fresh CorrelationId and Sequence 0 (A1 Slice 4b design §2.1/§3).
        var embeddings = chunkTexts.Count > 0
            ? await embeddingInvoker.EmbedAsync(new AiCallContext(document.BusinessId, AiOperations.KnowledgeIndexing, Guid.NewGuid(), 0), chunkTexts, VoyageInputType.Document, cancellationToken)
            : [];

        var lockKey = AdvisoryLockKeyGenerator.ForKnowledgeDocument(document.BusinessId, document.Id);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            // Serializes against another concurrent content-changing update
            // to this same document — held only across this fast, local DB
            // write; the slow Voyage call above already completed before
            // this transaction even began.
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock({lockKey})", cancellationToken);

            // IgnoreQueryFilters + explicit BusinessId, deliberately not
            // relying on the ambient tenant query filter — same defensive
            // posture WidgetConversationService already uses: this service's
            // correctness (never leaving a stale chunk behind a fresh
            // insert) must not depend on whatever ICurrentTenantAccessor
            // happens to resolve to for the calling request.
            var existingChunks = await db.KnowledgeChunks
                .IgnoreQueryFilters()
                .Where(c => c.BusinessId == document.BusinessId && c.KnowledgeDocumentId == document.Id)
                .ToListAsync(cancellationToken);
            db.KnowledgeChunks.RemoveRange(existingChunks);

            var now = DateTimeOffset.UtcNow;
            document.Type = type;
            document.Title = title;
            document.Content = content;
            document.IsActive = isActive;
            document.UpdatedAt = now;

            db.KnowledgeChunks.AddRange(BuildChunkRows(document, chunkTexts, embeddings, now));

            // Document update, chunk deletes, and chunk inserts all commit
            // together here — the required atomicity guarantee:
            // KnowledgeDocument.Content and its KnowledgeChunk rows can never
            // be observed to represent two different versions.
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }

        return document;
    }

    private static IEnumerable<KnowledgeChunk> BuildChunkRows(
        KnowledgeDocument document, IReadOnlyList<string> chunkTexts, IReadOnlyList<float[]> embeddings, DateTimeOffset now)
    {
        for (var i = 0; i < chunkTexts.Count; i++)
        {
            yield return new KnowledgeChunk
            {
                Id = Guid.NewGuid(),
                BusinessId = document.BusinessId, // explicit, never derived from ambient tenant state
                KnowledgeDocumentId = document.Id,
                ChunkIndex = i,
                Content = chunkTexts[i],
                Embedding = new Vector(embeddings[i]),
                EmbeddingModel = VoyageModels.Voyage3,
                CreatedAt = now,
                UpdatedAt = now,
            };
        }
    }
}
