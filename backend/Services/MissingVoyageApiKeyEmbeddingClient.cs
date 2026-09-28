using backend.Enums;

namespace backend.Services;

/// <summary>
/// Thrown when embedding is actually requested but Voyage:ApiKey was never
/// configured. Its own type, deliberately never caught anywhere in
/// KnowledgeIndexingService/KnowledgeDocumentsController's error handling —
/// a missing-config error must never be indistinguishable from a genuine
/// Voyage API/network failure (VoyageEmbeddingException, which maps to a
/// 502 response). This one is a programming/deployment error and is meant
/// to be loud — same reasoning as AnthropicNotConfiguredException.
/// </summary>
public class VoyageNotConfiguredException()
    : InvalidOperationException(
        "Voyage:ApiKey is not configured. Set it via `dotnet user-secrets set " +
        "\"Voyage:ApiKey\" \"...\"` (Development) or the hosting platform's " +
        "configuration (production) before knowledge documents can be indexed.");

/// <summary>
/// Registered as IVoyageEmbeddingClient in place of the real Voyage-backed
/// one only when Voyage:ApiKey is absent from configuration — lets the app
/// start regardless, deferring the failure to the exact moment embedding is
/// actually attempted (KnowledgeIndexingService), same lazy-registration
/// pattern as MissingAnthropicApiKeyMessageService. Never a silent
/// fallback: the one member throws immediately.
/// </summary>
public class MissingVoyageApiKeyEmbeddingClient : IVoyageEmbeddingClient
{
    public Task<IReadOnlyList<float[]>> EmbedAsync(
        IReadOnlyList<string> texts, VoyageInputType inputType, CancellationToken cancellationToken = default) =>
        throw new VoyageNotConfiguredException();

    public Task<IReadOnlyList<float[]>> EmbedWithUsageAsync(
        IReadOnlyList<string> texts, VoyageInputType inputType, VoyageUsageAccumulator usage,
        CancellationToken cancellationToken = default) =>
        throw new VoyageNotConfiguredException();
}
