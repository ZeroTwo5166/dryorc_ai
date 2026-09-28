namespace backend.Services;

/// <summary>
/// Attribution context every provider-wrapper invoke method requires (A1
/// design docs/handoffs/billing-usage-a1-invocation-capture-design-v11.md
/// §4). Constructed by the caller only after its own tenant-consistency
/// check has passed (v11 §5, rule (b)) — the wrappers and the recorder
/// trust it and never re-check it. Deliberately carries no
/// <c>Conversation</c> entity (v11 §5: "<c>AiCallContext</c> does not gain a
/// <c>Conversation</c> field"). <see cref="Operation"/> is an
/// <see cref="Constants.AiOperations"/> constant. <see cref="CorrelationId"/>
/// and <see cref="Sequence"/> are owned by the service that starts the
/// logical operation (v11 §4, "Ownership of correlation") — the wrappers
/// never generate or increment either.
/// </summary>
public sealed record AiCallContext(
    Guid BusinessId,
    string Operation,
    Guid CorrelationId,
    int Sequence,
    Guid? ConversationId = null);
