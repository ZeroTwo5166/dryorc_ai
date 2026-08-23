namespace backend.Enums
{
    /// <summary>
    /// Voyage's input_type distinction — Document for indexing-side content
    /// (Step 11's only use), Query for retrieval-side lookups (Step 12, not
    /// implemented yet). Kept on the interface now specifically so Step 12
    /// doesn't need a breaking change to IVoyageEmbeddingClient later.
    /// </summary>
    public enum VoyageInputType
    {
        Document,
        Query
    }
}
