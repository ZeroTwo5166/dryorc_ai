using backend.Constants;
using backend.Data;
using backend.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace backend.Services;

public class AiInvocationRecorder(
    IServiceScopeFactory scopeFactory,
    ILogger<AiInvocationRecorder> logger) : IAiInvocationRecorder
{
    public async Task RecordAsync(AiInvocationRecordRequest request)
    {
        try
        {
            // Fresh DI scope, never the caller's — this AppDbContext has its
            // own Npgsql connection, so it is not part of the caller's
            // pending changes or transaction (v11 §5, "Isolation").
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // Own short internal timeout, never the caller's token (v11 §5,
            // "Cancellation") — deliberately not a parameter on this method.
            // Value and exact coverage: see IsolatedWriteLimits.WriteTimeout.
            using var timeoutCts = new CancellationTokenSource(IsolatedWriteLimits.WriteTimeout);

            db.AiProviderInvocations.Add(new AiProviderInvocation
            {
                Id = Guid.NewGuid(),
                BusinessId = request.BusinessId,
                Provider = request.Provider,
                Model = request.Model,
                Operation = request.Operation,
                CorrelationId = request.CorrelationId,
                Sequence = request.Sequence,
                ConversationId = request.ConversationId,
                InputTokens = request.InputTokens,
                OutputTokens = request.OutputTokens,
                CacheCreationInputTokens = request.CacheCreationInputTokens,
                CacheReadInputTokens = request.CacheReadInputTokens,
                InputItemCount = request.InputItemCount,
                Outcome = request.Outcome,
                IsUsagePartial = request.IsUsagePartial,
                ErrorCategory = request.ErrorCategory,
                CreatedAt = DateTimeOffset.UtcNow,
            });

            await db.SaveChangesAsync(timeoutCts.Token);
        }
        catch (Exception ex)
        {
            // v11 §5, Decision 1: fail open. Persistence failure — DB error,
            // timeout, or the (BusinessId, CorrelationId, Sequence) unique
            // index — must never fail an otherwise-successful AI provider
            // operation. Structured, non-content diagnostics only; no
            // prompt/response content or PII.
            logger.LogError(ex,
                "AiProviderInvocation write failed. BusinessId={BusinessId} Operation={Operation} CorrelationId={CorrelationId} Sequence={Sequence} ExceptionType={ExceptionType}",
                request.BusinessId, request.Operation, request.CorrelationId, request.Sequence, ex.GetType().Name);
        }
    }
}
