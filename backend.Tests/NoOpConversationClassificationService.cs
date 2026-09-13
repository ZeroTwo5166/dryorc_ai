using Anthropic.Models.Messages;
using backend.Services;

namespace backend.Tests;

/// <summary>
/// Test-only no-op double for tests that construct ClaudeReplyService
/// directly (not through DI) to exercise reply/RAG/tool-dispatch behavior
/// pre-dating Step 22 — those tests don't care about classification, so
/// this does nothing rather than making a real/fake Claude call. Distinct
/// from ConversationClassificationServiceTests, which test the real
/// ConversationClassificationService directly.
/// </summary>
public class NoOpConversationClassificationService : IConversationClassificationService
{
    public int CallCount { get; private set; }

    public Task ClassifyAsync(
        Guid businessId, Guid conversationId, IReadOnlyList<MessageParam> messages,
        CancellationToken cancellationToken = default)
    {
        CallCount++;
        return Task.CompletedTask;
    }
}
