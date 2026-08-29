namespace backend.Services;

/// <summary>
/// Shared outcome shape for OrderCancellationService/CustomerUpdateActionService's
/// idempotent entry points. See the idempotency-mechanism handoff for the full
/// design rationale — InProgress/KeyReusedWithDifferentIntent must never be
/// conflated with Confirmed/Failed by any caller.
/// </summary>
public enum IdempotentOperationOutcome
{
    /// <summary>Freshly created or previously cached success.</summary>
    Confirmed,

    /// <summary>Freshly recorded or previously cached failure. A genuine
    /// retry requires a new idempotency key.</summary>
    Failed,

    /// <summary>The operation exists (fresh or from a prior request) and
    /// has not yet reached a final external outcome. Never resubmitted.
    /// Never reported as success or failure.</summary>
    InProgress,

    /// <summary>The supplied idempotency key already exists for this
    /// Business, but the incoming request's operation-defining fields
    /// don't match the originally stored intent. No cached result is
    /// returned; nothing is executed.</summary>
    KeyReusedWithDifferentIntent,
}
