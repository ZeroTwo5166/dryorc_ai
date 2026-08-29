namespace backend.Services;

public record CustomerUpdateActionResult(
    Guid CustomerUpdateActionId,
    IdempotentOperationOutcome Outcome,
    string? FailureReason);

/// <summary>
/// Idempotency mechanism half of the CustomerUpdateAction workflow — same
/// shape and same out-of-scope boundary as IOrderCancellationService. Does
/// not call Shopify itself.
/// </summary>
public interface ICustomerUpdateActionService
{
    /// <summary>
    /// Idempotent entry point. fieldsChanged is the field-NAME list only,
    /// never values (matching the model's own documented PII discipline).
    /// idempotencyKey is caller-supplied, same contract as
    /// IOrderCancellationService.RequestCancellationAsync.
    /// </summary>
    Task<CustomerUpdateActionResult> RequestUpdateAsync(
        Guid businessId, Guid customerId, Guid? conversationId,
        List<string> fieldsChanged, string idempotencyKey,
        CancellationToken cancellationToken);

    /// <summary>Transitions Pending → Confirmed and sets CompletedAt. Same
    /// atomic no-op-if-not-Pending guard as
    /// IOrderCancellationService.MarkConfirmedAsync.</summary>
    Task MarkConfirmedAsync(Guid customerUpdateActionId, CancellationToken cancellationToken);

    /// <summary>Transitions Pending → Failed. Same guard as
    /// MarkConfirmedAsync.</summary>
    Task MarkFailedAsync(Guid customerUpdateActionId, string failureReason, CancellationToken cancellationToken);
}
