using backend.Interfaces;

namespace backend.Models;

/// <summary>
/// One record per provider invocation attempt that reaches the provider
/// boundary — Anthropic or Voyage. Immutable after insert; only the future
/// retention job may delete expired rows. See A1 design
/// (docs/handoffs/billing-usage-a1-invocation-capture-design-v11.md §3) for
/// the authoritative field-by-field contract this class implements.
/// </summary>
public class AiProviderInvocation : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid BusinessId { get; set; }

    /// <summary>See <see cref="Constants.AiProviders"/> for valid values.</summary>
    public required string Provider { get; set; }

    /// <summary>
    /// Model identifier sent — <see cref="Constants.AnthropicModels.Default"/>
    /// for Anthropic, or the existing <see cref="Constants.VoyageModels.Voyage3"/>
    /// for Voyage. See the implementation report for a naming discrepancy in
    /// A1 §3/§6, which refer to this constant as "VoyageModels.Embedding" —
    /// a name that does not exist in source; VoyageModels itself is
    /// untouched by this slice (out of scope).
    /// </summary>
    public required string Model { get; set; }

    /// <summary>
    /// Business purpose only, never a class, endpoint, or method name. See
    /// <see cref="Constants.AiOperations"/> for valid values.
    /// </summary>
    public required string Operation { get; set; }

    /// <summary>One logical application operation, however many provider invocations it causes.</summary>
    public Guid CorrelationId { get; set; }

    /// <summary>
    /// Starts at zero and increases monotonically within a CorrelationId.
    /// Means ordering only; phase identity belongs in Operation. This
    /// monotonicity invariant spans multiple rows sharing one CorrelationId
    /// and cannot be expressed as a single-row CHECK constraint — it is an
    /// application-level invariant only.
    /// </summary>
    public int Sequence { get; set; }

    /// <summary>
    /// FK SetNull describes database behavior only if the referenced
    /// Conversation row is ever deleted — the shipped lifecycle anonymizes
    /// Conversation rows in place instead, so this never actually fires
    /// today. Not explicitly nulled at customer erasure (A1 §7, Decision 3).
    /// </summary>
    public Guid? ConversationId { get; set; }

    /// <summary>
    /// Provider-reported input usage. Null means unknown (no usage
    /// obtained), never a proxy for a reported zero.
    /// </summary>
    public long? InputTokens { get; set; }

    /// <summary>Provider-reported output usage. Always null for Voyage rows (embeddings have no output-token concept).</summary>
    public long? OutputTokens { get; set; }

    /// <summary>Anthropic only. Always null for Voyage rows.</summary>
    public long? CacheCreationInputTokens { get; set; }

    /// <summary>Anthropic only. Always null for Voyage rows.</summary>
    public long? CacheReadInputTokens { get; set; }

    /// <summary>Number of texts in an embedding request.</summary>
    public int? InputItemCount { get; set; }

    /// <summary>See <see cref="Constants.AiOutcomes"/> for valid values.</summary>
    public required string Outcome { get; set; }

    /// <summary>
    /// True only on a Voyage Failed row carrying the non-null sum of
    /// batches that completed before a later batch failed (A1 §2, §3).
    /// Never true for any other row shape.
    /// </summary>
    public bool IsUsagePartial { get; set; }

    /// <summary>
    /// Failure cause only, never raw provider error text, never whether
    /// usage is known. See <see cref="Constants.AiErrorCategories"/> for
    /// valid values. Null if and only if Outcome is Succeeded.
    /// </summary>
    public string? ErrorCategory { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
