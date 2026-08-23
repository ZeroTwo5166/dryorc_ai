using backend.Enums;

namespace backend.Services;

/// <summary>
/// Thin wrapper over Voyage AI's embeddings API
/// (https://api.voyageai.com/v1/embeddings). Deliberately not locked to
/// document embeddings only — inputType lets a future Step 12 retrieval path
/// reuse this same client for query embeddings without a breaking interface
/// change, even though Step 11 only ever passes VoyageInputType.Document.
/// </summary>
public interface IVoyageEmbeddingClient
{
    /// <summary>
    /// Embeds every text, batching internally per Voyage's documented
    /// max-texts-per-request limit (VoyageModels.MaxTextsPerRequest). The
    /// returned list's Nth element is always the embedding for texts[N] —
    /// callers never need to reason about Voyage's own response
    /// ordering/index semantics: the implementation re-maps by Voyage's
    /// returned index and throws VoyageEmbeddingException rather than risk
    /// silently pairing an embedding with the wrong text.
    /// </summary>
    Task<IReadOnlyList<float[]>> EmbedAsync(
        IReadOnlyList<string> texts, VoyageInputType inputType, CancellationToken cancellationToken = default);
}

/// <summary>
/// A genuine external-dependency failure — non-success HTTP status,
/// network/timeout failure, or a malformed/incomplete Voyage response (wrong
/// embedding count, missing/duplicate/out-of-range index). Distinct from
/// VoyageNotConfiguredException (a programming/deployment error, never
/// caught in the indexing path) — this is the type
/// KnowledgeDocumentsController catches to return 502.
/// </summary>
public class VoyageEmbeddingException(string message, Exception? innerException = null)
    : Exception(message, innerException);
