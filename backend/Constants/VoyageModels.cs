namespace backend.Constants;

/// <summary>
/// The locked embedding model for Step 11 — voyage-3, matching
/// EmbeddingDimensions.VoyageThree (1024) and the fixed vector(1024) column
/// on KnowledgeChunk.Embedding. Confirmed against Voyage's current API docs:
/// voyage-3 does NOT support the output_dimension request parameter — only
/// voyage-3-large/voyage-3.5/voyage-4-family models do — but voyage-3's
/// native/default output is already exactly 1024-dim, so there is nothing to
/// request anyway. VoyageEmbeddingClient never sends output_dimension.
/// </summary>
public static class VoyageModels
{
    public const string Voyage3 = "voyage-3";

    /// <summary>
    /// Voyage's documented maximum number of input texts in a single
    /// embeddings request (confirmed current, uniform across models — not
    /// tiered by model). Voyage's total-tokens-per-request limit is not
    /// consistently documented for voyage-3 specifically (their docs group
    /// "voyage-4, voyage-3.5, voyage-2" at 320K tokens but omit voyage-3 from
    /// that table) — deliberately not enforced client-side rather than
    /// inventing an unverified number; realistic hand-authored
    /// KnowledgeDocument content makes this a non-issue in practice (a
    /// document would need on the order of hundreds of chunks before this
    /// text-count limit alone would even trigger a second batch).
    /// </summary>
    public const int MaxTextsPerRequest = 1000;
}
