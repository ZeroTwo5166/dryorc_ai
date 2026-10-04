using backend.Services;

namespace backend.Tests;

/// <summary>
/// Test double for IKnowledgeRetrievalService, used by ClaudeReplyServiceTests
/// to control exactly what "was retrieved" without needing real Postgres
/// vector data — retrieval's own correctness (ordering, threshold, tenant
/// isolation, etc.) is KnowledgeRetrievalServiceTests' job, not this one's.
/// Defaults to returning nothing, matching every pre-Step-12 test's
/// expectation (no KnowledgeChunks seeded, so real retrieval would find
/// nothing anyway).
/// </summary>
public class FakeKnowledgeRetrievalService : IKnowledgeRetrievalService
{
    public List<RetrievedKnowledgeChunk> ChunksToReturn { get; set; } = [];
    public Exception? ExceptionToThrow { get; set; }
    public int CallCount { get; private set; }
    public List<(Guid BusinessId, Guid ConversationId, string QueryText)> Requests { get; } = [];

    public Task<IReadOnlyList<RetrievedKnowledgeChunk>> RetrieveAsync(
        Guid businessId, Guid conversationId, string queryText, CancellationToken cancellationToken = default)
    {
        CallCount++;
        Requests.Add((businessId, conversationId, queryText));

        if (ExceptionToThrow is not null)
        {
            throw ExceptionToThrow;
        }

        IReadOnlyList<RetrievedKnowledgeChunk> result = ChunksToReturn;
        return Task.FromResult(result);
    }
}
