using Anthropic;
using Anthropic.Core;
using Anthropic.Models.Messages;
using Anthropic.Services;
using Anthropic.Services.Messages;

namespace backend.Tests;

/// <summary>
/// Registered in place of the real Anthropic-backed IMessageService for
/// normal (non-live) test execution — see NivoraApiFactory. Deliberately
/// returns a response built from the SDK's own real Message/ContentBlock/
/// TextBlock/Usage types, not a pre-extracted string — ClaudeReplyService's
/// actual content-block parsing/extraction logic runs against this exactly
/// as it would against a real API response, only the network call itself
/// is faked.
///
/// Only Create is ever called by ClaudeReplyService; the rest of
/// IMessageService is implemented just to satisfy the interface.
/// </summary>
public class FakeMessageService : IMessageService
{
    public string ReplyText { get; set; } = "This is a fake AI reply used for testing.";
    public Exception? ExceptionToThrow { get; set; }
    public int CallCount { get; private set; }
    public List<MessageCreateParams> Requests { get; } = [];

    public Task<Message> Create(MessageCreateParams parameters, CancellationToken cancellationToken = default)
    {
        CallCount++;
        Requests.Add(parameters);

        if (ExceptionToThrow is not null)
        {
            throw ExceptionToThrow;
        }

        var message = new Message
        {
            ID = $"msg_fake_{Guid.NewGuid():N}",
            Container = null,
            Content = new List<ContentBlock> { new TextBlock { Text = ReplyText, Citations = null } },
            Model = "claude-sonnet-5",
            StopDetails = null,
            StopReason = "end_turn",
            StopSequence = null,
            Usage = new Usage
            {
                CacheCreation = null,
                CacheCreationInputTokens = null,
                CacheReadInputTokens = null,
                InferenceGeo = null,
                InputTokens = 10,
                OutputTokens = 10,
                OutputTokensDetails = null,
                ServerToolUse = null,
                ServiceTier = null,
            },
        };

        return Task.FromResult(message);
    }

    public IMessageServiceWithRawResponse WithRawResponse => throw new NotSupportedException("FakeMessageService: not used by ClaudeReplyService.");

    public IMessageService WithOptions(Func<ClientOptions, ClientOptions> modifier) =>
        throw new NotSupportedException("FakeMessageService: not used by ClaudeReplyService.");

    public IBatchService Batches => throw new NotSupportedException("FakeMessageService: not used by ClaudeReplyService.");

    public IAsyncEnumerable<RawMessageStreamEvent> CreateStreaming(MessageCreateParams parameters, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("FakeMessageService: Step 8 is non-streaming only.");

    public Task<MessageTokensCount> CountTokens(MessageCountTokensParams parameters, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("FakeMessageService: not used by ClaudeReplyService.");
}
