using System.Security.Cryptography;
using System.Text;

namespace backend.Services;

/// <summary>
/// Deterministic execution-leg identity derivation for the Step 22 AI Action
/// Safety Model — see docs/handoffs/step-22-ai-action-safety-model-concrete-contract-design-v2.md
/// §12 and docs/handoffs/step-22z-approval-proposal-approval-lifecycle-design.md
/// §14 for the execution-identity contract this implements, independently
/// and identically stated in both documents. Not yet called from anywhere —
/// no `Execute`/`IApprovedActionExecutor` implementation exists yet (Step
/// 23F implements this function in isolation, per that pass's own
/// explicitly narrow authorization).
///
/// This is the fourth of four structurally distinct identity concepts this
/// design requires never collapse into one another (22J §5's "kept
/// distinct" subsection, re-confirmed 22M/22N, restated unweakened in 22Z
/// §14): approval identity (`Approval.Id`), action identity
/// (`ActionHasher`, spanning an `Approval`'s entire lifetime), AI-turn
/// idempotency (`IdempotencyKeyDeriver`, bounded to one
/// `(assistantMessageId, toolUseId)` dispatch-loop retry), and this one —
/// execution-leg identity, identifying one execution *attempt* against an
/// already-`Approved` `Approval`. Deliberately a separate type from both
/// `ActionHasher` and `IdempotencyKeyDeriver`, not a third method bolted
/// onto either — the identical reasoning `ActionHasher`'s own doc comment
/// already states for why it is not a second method on
/// `IdempotencyKeyDeriver`: colocating would misname part of the class's
/// own contents and risk exactly the conflation this design has repeatedly
/// warned against.
///
/// Named `ApprovalExecutionKeyDeriver`, matching the design's own
/// illustrative name (22Z §14: "e.g. conceptually
/// `ApprovalExecutionKeyDeriver`") rather than inventing a different one.
/// The design's phrasing ("e.g. conceptually") does not make this name a
/// hard requirement, only a strong, doc-stated precedent — this is a
/// judgment call, flagged in the Step 23F handoff, resolved in favor of the
/// design's own suggested name over any other candidate.
///
/// Inputs: `(BusinessId, ToolName, ResourceId, CanonicalArguments,
/// ApprovalId)` — exactly the five fields both design documents specify,
/// independently and identically. Deliberately excludes `ConversationId`
/// and `CustomerId` — those are what `ActionHash` already binds; this
/// function's only job is to make one `Approval`'s own single legitimate
/// execution attempt uniquely, deterministically identifiable, not to
/// re-bind customer/conversation identity a second time.
///
/// No attempt-number component: both design documents reason through this
/// explicitly and identically — the settled 1:1 relationship (one
/// `Approval` maps to exactly one legitimate execution attempt; a retry
/// after failure always requires a new `Approval`, never a second attempt
/// against the same one) means an attempt-number would always be `1` in
/// every legitimate case, so `ApprovalId` alone is sufficient and no such
/// field is added.
///
/// Hash algorithm: SHA-256, lowercase hex, length-prefixed concatenation —
/// identical construction to `IdempotencyKeyDeriver.Derive` and
/// `ActionHasher.Derive`, explicitly required by both design documents
/// ("structurally similar in construction to `IdempotencyKeyDeriver.Derive`
/// (length-prefixed concatenation, SHA-256)").
///
/// This value becomes the tool-specific execution-intent row's own
/// `IdempotencyKey` column (e.g. `OrderCancellation.IdempotencyKey`,
/// reusing the existing column, not adding a new one) — per 22Z §14,
/// unchanged.
///
/// What this provides: exactly one execution-intent row can ever be created
/// per `Approval`, enforced redundantly at two independent layers — the
/// `Approved→Consumed` guard itself, and the tool-specific idempotency
/// table's own existing unique index (e.g.
/// `IX_OrderCancellations_BusinessId_IdempotencyKey`) — a purely internal,
/// database-level dedup guarantee.
///
/// What this does NOT provide, stated without hedging per the design's own
/// explicit instruction (22-concrete-contract-design-v2.md §12): a local
/// execution identifier does not create Shopify-side idempotency. Nothing
/// about deriving this key, however carefully, causes Shopify to
/// deduplicate a mutation sent twice.
/// </summary>
public static class ApprovalExecutionKeyDeriver
{
    /// <param name="businessId">Server-derived tenant id — never model-supplied.</param>
    /// <param name="toolName">The dispatched tool's own Name — generic, not
    /// hardcoded to `orderCancel`, so this function is directly reusable by
    /// a future `refundOrder`/`updateCustomer` tool without modification.</param>
    /// <param name="resourceId">The tool's own resource identifier (e.g.
    /// `ExternalOrderId` for `orderCancel`) — passed through exactly as
    /// given, no normalization invented here (the `ExternalOrderId`
    /// normalization open questions, 22H §10/§19, remain unresolved and are
    /// inherited by any eventual real caller, not this function).</param>
    /// <param name="canonicalArguments">The tool's own canonicalized
    /// argument representation — the same value used for the `Approval`'s
    /// stored/recomputed `ActionHash`, not re-derived or reimplemented
    /// here.</param>
    /// <param name="approvalId">The `Approval.Id` this execution attempt is
    /// claiming — the sole per-attempt-distinguishing input; see this
    /// class's own doc comment for why no attempt-number component is
    /// added.</param>
    /// <returns>A lowercase hex-encoded SHA-256 digest (64 characters) —
    /// directly assignable to `OrderCancellation.IdempotencyKey`
    /// (`required string`).</returns>
    public static string Derive(
        Guid businessId, string toolName, string resourceId, string canonicalArguments,
        Guid approvalId)
    {
        // Length-prefixed concatenation, not a delimiter-joined string —
        // identical discipline to IdempotencyKeyDeriver.Derive and
        // ActionHasher.Derive, for the same reason: this is what makes the
        // encoding unambiguous regardless of what characters any individual
        // component contains, e.g. distinguishing (toolName: "AB",
        // resourceId: "C") from (toolName: "A", resourceId: "BC") is
        // impossible with plain concatenation but always unambiguous here.
        var canonicalInput = string.Concat(
            LengthPrefixed(businessId.ToString("D")),
            LengthPrefixed(toolName),
            LengthPrefixed(resourceId),
            LengthPrefixed(canonicalArguments),
            LengthPrefixed(approvalId.ToString("D")));

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalInput));
        return Convert.ToHexStringLower(hash);
    }

    private static string LengthPrefixed(string value) => $"{value.Length}:{value}";
}
