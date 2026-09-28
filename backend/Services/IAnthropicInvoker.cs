using Anthropic.Models.Messages;

namespace backend.Services;

/// <summary>
/// The single Anthropic invoker (A1 design
/// docs/handoffs/billing-usage-a1-invocation-capture-design-v11.md §4): calls
/// the SDK's <c>IMessageService.Create</c> and records exactly one
/// <see cref="Models.AiProviderInvocation"/> per call that reaches it — after
/// the call returns or throws, then returns the same response or rethrows the
/// same exception, with caller behavior otherwise unchanged (v11 §4:153). No
/// call site uses this yet — migrating them is Slice 4 (errata 2 §1).
/// </summary>
public interface IAnthropicInvoker
{
    Task<Message> CreateMessageAsync(
        AiCallContext context, MessageCreateParams parameters, CancellationToken cancellationToken = default);
}
