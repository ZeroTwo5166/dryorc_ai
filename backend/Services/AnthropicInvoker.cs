using Anthropic.Models.Messages;
using Anthropic.Services;
using backend.Constants;
using Microsoft.Extensions.Logging;

namespace backend.Services;

/// <summary>
/// See <see cref="IAnthropicInvoker"/>. Row semantics, per A1 design v11
/// §2-§3 and the Slice 3 decisions addendum
/// (docs/handoffs/billing-usage-a1-slice3-decisions-addendum.md):
/// <list type="bullet">
/// <item>No wrapper-level cancellation pre-check: every call that reaches
/// <c>IMessageService.Create</c> gets a row, including one whose token was
/// already cancelled (OD-6). The only no-row case is
/// <see cref="AnthropicNotConfiguredException"/> from the missing-key
/// stand-in, which never reaches the SDK.</item>
/// <item>Success: <c>Usage</c> maps field-for-field (v11 §3). Any 2xx is
/// <c>Succeeded</c>, whatever its stop reason (design pass §5, derived).</item>
/// <item>Failure: <c>Failed</c>, all tokens null, a category from
/// <see cref="AiErrorCategorizer"/>, never raw provider text (v11 §2:85).</item>
/// <item><c>Model</c> is the literal the caller sent in
/// <see cref="MessageCreateParams.Model"/>, never the response's model
/// (OD-7 option (i), per the Slice 3 implementation prompt).</item>
/// </list>
/// </summary>
public class AnthropicInvoker(
    IMessageService messageService,
    IAiInvocationRecorder recorder,
    ILogger<AnthropicInvoker> logger) : IAnthropicInvoker
{
    public async Task<Message> CreateMessageAsync(
        AiCallContext context, MessageCreateParams parameters, CancellationToken cancellationToken = default)
    {
        Message response;
        try
        {
            response = await messageService.Create(parameters, cancellationToken);
        }
        catch (AnthropicNotConfiguredException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Building the row must never replace the provider's exception:
            // params built via MessageCreateParams.FromRawUnchecked with a
            // null or absent model make ReadModelSent throw. Then no row is
            // written (no placeholder model, OD-7) and the original is
            // rethrown. Non-content fields only.
            AiInvocationRecordRequest? failedRow = null;
            try
            {
                failedRow = Build(context, parameters, AiOutcomes.Failed) with
                {
                    ErrorCategory = AiErrorCategorizer.ForAnthropic(ex, cancellationToken),
                };
            }
            catch (Exception buildEx)
            {
                logger.LogError(
                    "AiProviderInvocation row could not be built after a failed Anthropic call; no row written. BusinessId={BusinessId} Operation={Operation} CorrelationId={CorrelationId} Sequence={Sequence} ExceptionType={ExceptionType}",
                    context.BusinessId, context.Operation, context.CorrelationId, context.Sequence, buildEx.GetType().Name);
            }

            if (failedRow is not null)
            {
                await RecordAsync(failedRow);
            }
            throw;
        }

        var (inputTokens, outputTokens, cacheCreationInputTokens, cacheReadInputTokens) = ReadUsage(response);
        // Same rule as the failure path: building the row (ReadModelSent on
        // an unreadable model) must never cost the caller the response. No
        // row, no placeholder model (OD-7); the same instance is returned.
        AiInvocationRecordRequest? succeededRow = null;
        try
        {
            succeededRow = Build(context, parameters, AiOutcomes.Succeeded) with
            {
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
                CacheCreationInputTokens = cacheCreationInputTokens,
                CacheReadInputTokens = cacheReadInputTokens,
            };
        }
        catch (Exception buildEx)
        {
            logger.LogError(
                "AiProviderInvocation row could not be built after a succeeded Anthropic call; no row written. BusinessId={BusinessId} Operation={Operation} CorrelationId={CorrelationId} Sequence={Sequence} ExceptionType={ExceptionType}",
                context.BusinessId, context.Operation, context.CorrelationId, context.Sequence, buildEx.GetType().Name);
        }

        if (succeededRow is not null)
        {
            await RecordAsync(succeededRow);
        }
        return response;
    }

    private static AiInvocationRecordRequest Build(AiCallContext context, MessageCreateParams parameters, string outcome) => new()
    {
        BusinessId = context.BusinessId,
        Provider = AiProviders.Anthropic,
        Model = ReadModelSent(parameters),
        Operation = context.Operation,
        CorrelationId = context.CorrelationId,
        Sequence = context.Sequence,
        ConversationId = context.ConversationId,
        Outcome = outcome,
        IsUsagePartial = false,
    };

    // The SDK's JSON-backed getters throw on a missing or mistyped field;
    // reading usage for telemetry must never fail the caller's call. Every
    // usage field is read inside this guard, and if any one can't be read
    // the row's usage is null as a whole (mirrors OD-2). A field that is
    // legitimately null in a readable usage object stays null on its own.
    private static (long? InputTokens, long? OutputTokens, long? CacheCreationInputTokens, long? CacheReadInputTokens) ReadUsage(Message response)
    {
        try
        {
            var usage = response.Usage;
            return (usage.InputTokens, usage.OutputTokens, usage.CacheCreationInputTokens, usage.CacheReadInputTokens);
        }
        catch (Exception)
        {
            return (null, null, null, null);
        }
    }

    private static string ReadModelSent(MessageCreateParams parameters)
    {
        try
        {
            return parameters.Model.Raw();
        }
        catch (Exception)
        {
            return parameters.Model.Json.ToString();
        }
    }

    private async Task RecordAsync(AiInvocationRecordRequest request)
    {
        try
        {
            // Awaited, never fire-and-forget (v11 §5, "Latency"); no caller
            // token — the recorder has its own timeout (errata 2 D1).
            await recorder.RecordAsync(request);
        }
        catch (Exception ex)
        {
            // IAiInvocationRecorder never throws by contract; this guard only
            // stops a future recorder regression from replacing the
            // provider's own result or exception. Non-content fields only.
            logger.LogError(
                "AiProviderInvocation recorder threw despite its fail-open contract. BusinessId={BusinessId} Operation={Operation} CorrelationId={CorrelationId} Sequence={Sequence} ExceptionType={ExceptionType}",
                request.BusinessId, request.Operation, request.CorrelationId, request.Sequence, ex.GetType().Name);
        }
    }
}
