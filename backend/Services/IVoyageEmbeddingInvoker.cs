using backend.Enums;

namespace backend.Services;

/// <summary>
/// The single Voyage client wrapper (A1 design
/// docs/handoffs/billing-usage-a1-invocation-capture-design-v11.md §4): embeds
/// through <see cref="IVoyageEmbeddingClient"/> and records exactly one
/// <see cref="Models.AiProviderInvocation"/> per call that reaches the client
/// — after the call returns or throws, then returns or rethrows, with caller
/// behavior otherwise unchanged (v11 §4:153). Usage is summed across the
/// client's internal batches via a per-call <see cref="VoyageUsageAccumulator"/>
/// (Slice 3 decisions addendum Q1). Production caller so far:
/// KnowledgeIndexingService (Slice 4b). Customer-turn retrieval
/// (KnowledgeRetrievalService) still calls IVoyageEmbeddingClient directly
/// until it migrates in Slice 4a (errata 2 §1).
/// </summary>
public interface IVoyageEmbeddingInvoker
{
    Task<IReadOnlyList<float[]>> EmbedAsync(
        AiCallContext context, IReadOnlyList<string> texts, VoyageInputType inputType,
        CancellationToken cancellationToken = default);
}
