using backend.Constants;
using backend.Enums;
using Microsoft.Extensions.Logging;

namespace backend.Services;

/// <summary>
/// See <see cref="IVoyageEmbeddingInvoker"/>. Row semantics, per A1 design v11
/// §2-§3 and the Slice 3 decisions addendum
/// (docs/handoffs/billing-usage-a1-slice3-decisions-addendum.md):
/// <list type="bullet">
/// <item>One row per call that reaches the client (OD-6). No row for empty
/// input (never dispatched, v11 §2:70) or <see cref="VoyageNotConfiguredException"/>
/// (the missing-key stand-in never dispatches).</item>
/// <item><c>InputTokens</c> = the accumulator's total: the sum across
/// completed batches, or null when none completed or any completed batch's
/// usage was missing/unusable (OD-2, OD-4).</item>
/// <item><c>InputItemCount</c> = every requested text, whatever the outcome
/// (OD-5).</item>
/// <item><c>IsUsagePartial</c> = Failed, a non-null total, and at least one
/// requested batch that didn't contribute usage (OD-9).</item>
/// </list>
/// </summary>
public class VoyageEmbeddingInvoker(
    IVoyageEmbeddingClient client,
    IAiInvocationRecorder recorder,
    ILogger<VoyageEmbeddingInvoker> logger) : IVoyageEmbeddingInvoker
{
    public async Task<IReadOnlyList<float[]>> EmbedAsync(
        AiCallContext context, IReadOnlyList<string> texts, VoyageInputType inputType,
        CancellationToken cancellationToken = default)
    {
        // Fresh per call, never a field — a reused accumulator would sum
        // across invocations.
        var usage = new VoyageUsageAccumulator();

        if (texts.Count == 0)
        {
            return await client.EmbedWithUsageAsync(texts, inputType, usage, cancellationToken);
        }

        IReadOnlyList<float[]> result;
        try
        {
            result = await client.EmbedWithUsageAsync(texts, inputType, usage, cancellationToken);
        }
        catch (VoyageNotConfiguredException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var totalTokens = usage.TotalTokens;
            await RecordAsync(context, texts.Count, totalTokens, AiOutcomes.Failed,
                isUsagePartial: totalTokens is not null && usage.CompletedBatches < usage.RequestedBatches,
                errorCategory: AiErrorCategorizer.ForVoyage(ex, cancellationToken));
            throw;
        }

        await RecordAsync(context, texts.Count, usage.TotalTokens, AiOutcomes.Succeeded,
            isUsagePartial: false, errorCategory: null);
        return result;
    }

    private async Task RecordAsync(
        AiCallContext context, int inputItemCount, long? inputTokens, string outcome,
        bool isUsagePartial, string? errorCategory)
    {
        var request = new AiInvocationRecordRequest
        {
            BusinessId = context.BusinessId,
            Provider = AiProviders.Voyage,
            Model = VoyageModels.Voyage3,
            Operation = context.Operation,
            CorrelationId = context.CorrelationId,
            Sequence = context.Sequence,
            ConversationId = context.ConversationId,
            InputTokens = inputTokens,
            InputItemCount = inputItemCount,
            Outcome = outcome,
            IsUsagePartial = isUsagePartial,
            ErrorCategory = errorCategory,
        };

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
                context.BusinessId, context.Operation, context.CorrelationId, context.Sequence, ex.GetType().Name);
        }
    }
}
