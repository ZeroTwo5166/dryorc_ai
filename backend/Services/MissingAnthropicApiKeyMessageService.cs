using Anthropic;
using Anthropic.Core;
using Anthropic.Models.Messages;
using Anthropic.Services;
using Anthropic.Services.Messages;

namespace backend.Services;

/// <summary>
/// Thrown when Claude is actually invoked but Anthropic:ApiKey was never
/// configured. Deliberately its own type, not a bare InvalidOperationException
/// caught anywhere in ClaudeReplyService's catch chain — a missing-config
/// error must never be indistinguishable from a genuine Anthropic API/
/// network failure (which degrade to a null reply). This one is a
/// programming/deployment error and is meant to be loud.
/// </summary>
public class AnthropicNotConfiguredException()
    : InvalidOperationException(
        "Anthropic:ApiKey is not configured. Set it via `dotnet user-secrets set " +
        "\"Anthropic:ApiKey\" \"sk-ant-...\"` (Development) or the hosting platform's " +
        "configuration (production) before Claude can be used.");

/// <summary>
/// Registered as IMessageService in place of the real Anthropic-backed one
/// only when Anthropic:ApiKey is absent from configuration — lets the app
/// start and IClaudeReplyService remain resolvable via DI either way,
/// deferring the failure to the exact moment Claude is actually invoked
/// (ClaudeReplyService.GenerateReplyAsync) rather than at startup. Never a
/// silent fallback: every member throws immediately.
/// </summary>
public class MissingAnthropicApiKeyMessageService : IMessageService
{
    // Only Create is ever called by ClaudeReplyService — every member below
    // throws the same way, purely to satisfy the interface.
    public IMessageServiceWithRawResponse WithRawResponse => throw new AnthropicNotConfiguredException();

    public IMessageService WithOptions(Func<ClientOptions, ClientOptions> modifier) =>
        throw new AnthropicNotConfiguredException();

    public IBatchService Batches => throw new AnthropicNotConfiguredException();

    public Task<Message> Create(MessageCreateParams parameters, CancellationToken cancellationToken = default) =>
        throw new AnthropicNotConfiguredException();

    public IAsyncEnumerable<RawMessageStreamEvent> CreateStreaming(
        MessageCreateParams parameters, CancellationToken cancellationToken = default) =>
        throw new AnthropicNotConfiguredException();

    public Task<MessageTokensCount> CountTokens(
        MessageCountTokensParams parameters, CancellationToken cancellationToken = default) =>
        throw new AnthropicNotConfiguredException();
}
