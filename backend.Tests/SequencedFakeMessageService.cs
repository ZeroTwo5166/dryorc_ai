using Anthropic;
using Anthropic.Core;
using Anthropic.Models.Messages;
using Anthropic.Services;
using Anthropic.Services.Messages;

namespace backend.Tests;

/// <summary>
/// A second IMessageService test double, separate from FakeMessageService,
/// used only by ToolDispatchServiceTests. FakeMessageService always returns
/// the same single reply and is depended on by ClaudeReplyServiceTests —
/// this type exists instead of extending FakeMessageService with
/// multi-response/tool-use behavior so that file, and the tests that
/// already rely on its current single-reply shape, are never touched.
///
/// Returns real Anthropic.Models.Messages.Message objects (built with real
/// ContentBlock/ToolUseBlock/TextBlock types, same convention as
/// FakeMessageService) from a caller-supplied queue, one per Create call —
/// so a test can script an exact multi-turn tool-use conversation.
/// </summary>
public class SequencedFakeMessageService : IMessageService
{
    private readonly Queue<Message> _responses = new();

    public Exception? ExceptionToThrow { get; set; }
    public int CallCount { get; private set; }
    public List<MessageCreateParams> Requests { get; } = [];

    public void Enqueue(Message message) => _responses.Enqueue(message);

    public Task<Message> Create(MessageCreateParams parameters, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        CallCount++;
        Requests.Add(parameters);

        if (ExceptionToThrow is not null)
        {
            throw ExceptionToThrow;
        }

        if (_responses.Count == 0)
        {
            throw new InvalidOperationException(
                $"SequencedFakeMessageService: Create was called a {CallCount}th time with no queued response left.");
        }

        return Task.FromResult(_responses.Dequeue());
    }

    public IMessageServiceWithRawResponse WithRawResponse =>
        throw new NotSupportedException("SequencedFakeMessageService: not used by ToolDispatchService.");

    public IMessageService WithOptions(Func<ClientOptions, ClientOptions> modifier) =>
        throw new NotSupportedException("SequencedFakeMessageService: not used by ToolDispatchService.");

    public IBatchService Batches =>
        throw new NotSupportedException("SequencedFakeMessageService: not used by ToolDispatchService.");

    public IAsyncEnumerable<RawMessageStreamEvent> CreateStreaming(MessageCreateParams parameters, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("SequencedFakeMessageService: ToolDispatchService is non-streaming only.");

    public Task<MessageTokensCount> CountTokens(MessageCountTokensParams parameters, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("SequencedFakeMessageService: not used by ToolDispatchService.");
}
