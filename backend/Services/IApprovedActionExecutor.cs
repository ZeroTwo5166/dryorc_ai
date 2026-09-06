namespace backend.Services;

/// <summary>
/// Reasons ExecuteApprovedActionAsync refuses to proceed before any
/// execution-intent row (e.g. OrderCancellation) is created or referenced.
/// Every value corresponds to one of the pure-read steps (1-12) of the
/// execution ordering in docs/handoffs/step-22z-approval-proposal-approval-lifecycle-design.md
/// §10, all of which strictly precede the atomic claim (step 13) — none of
/// these can ever coexist with a populated ExecutionIntentId. Locked shape,
/// unchanged since docs/handoffs/step-23u-execution-outcome-design.md §8, as
/// corrected through docs/handoffs/step-23w-execution-outcome-correction-narrow-verification.md
/// and re-confirmed unchanged through docs/handoffs/step-24-cancelorder-executor-design.md
/// and docs/handoffs/step-28-decode-gap-design-note-independent-verification.md.
/// Step 29 implements only ApprovalNotFound's own trigger path (Approval
/// row load) — every other value below remains design-only until a later
/// implementation session adds its own trigger.
/// </summary>
public enum ExecutionRefusalReason
{
    /// <summary>No Approval row exists for the supplied ApprovalId — or one
    /// exists but belongs to a different tenant than the ambient
    /// ICurrentTenantAccessor currently resolves, which the global EF Core
    /// tenant query filter folds into this same, structurally
    /// indistinguishable case (docs/handoffs/step-24-cancelorder-executor-design.md
    /// §3, independently confirmed docs/handoffs/step-25/26). The only
    /// value with a concrete implementation as of Step 29.</summary>
    ApprovalNotFound,

    /// <summary>Approval.ExpiresAt is not after now at the time of the
    /// check (step 7). Not implemented as of Step 29.</summary>
    ApprovalExpired,

    /// <summary>Approval.Status is not Approved — covers Pending (never
    /// approved), Denied, and Consumed (already executed). Does not cover
    /// the case where a concurrent Execute call wins the Approved-to-
    /// Consumed race first — that is the separate ApprovalClaimLost value
    /// (step-43 addendum owner decision), not folded in here. Not
    /// implemented as of Step 29.</summary>
    ApprovalNotApproved,

    /// <summary>Conversation.CustomerId is null when re-derived fresh at
    /// execution time (step 8). Not implemented as of Step 29.</summary>
    CustomerUnresolved,

    /// <summary>ILevel2VerificationChecker.IsVerifiedAsync returned false
    /// for the freshly re-derived CustomerId (step 9). Not implemented as
    /// of Step 29.</summary>
    Level2VerificationFailed,

    /// <summary>IOrderOwnershipAuthorizationService.AuthorizeAsync refused
    /// (step 10). Not implemented as of Step 29.</summary>
    OwnershipVerificationFailed,

    /// <summary>The recomputed action hash does not match the stored
    /// Approval.ActionHash byte-for-byte (step 11). Not implemented as of
    /// Step 29.</summary>
    ActionHashMismatch,

    /// <summary>The tool-specific idempotent-insert call threw
    /// DuplicatePendingOrderCancellationException — a Pending execution-
    /// intent row already exists for this order under a different Approval.
    /// Not implemented as of Step 29.</summary>
    DuplicatePendingCancellation,

    /// <summary>The guarded Approved→Consumed claim (step 13) affected zero
    /// rows — either a concurrent Execute call won the race first, or
    /// Approval.ExpiresAt elapsed between step 7's earlier check and the
    /// claim itself. The two causes are structurally indistinguishable from
    /// the affected-row count alone, so this single value covers both
    /// (step-43 addendum owner decision) — not a cause a client can act on
    /// any differently depending on which actually occurred.</summary>
    ApprovalClaimLost,
}

/// <summary>
/// Status of an execution attempt that DID create/reference a durable
/// execution-intent row. Deliberately 3-valued, not 4 — a plain,
/// Nivora-caused Confirmed is not reachable as a synchronous return value
/// from any code path that exists today (step-23u §1), so it is not a
/// member of this enum. Not yet reachable from any implemented code path as
/// of Step 29 — no execution attempt reaches the atomic claim yet.
/// </summary>
public enum ExecutionResultStatus
{
    /// <summary>The order was found already cancelled — either by the
    /// pre-execution ground-truth check or by mutation-time classification.
    /// Resolves the row's Status to Confirmed with causation NOT attributed
    /// to Nivora.</summary>
    ExternallySatisfied,

    /// <summary>A definite, Shopify-reported rejection. The tool-specific
    /// MarkFailedAsync-equivalent was called.</summary>
    Failed,

    /// <summary>The mutation was accepted (or its result is unknown) and
    /// the row remains Pending for reconciliation to resolve later. The
    /// default outcome of any attempt that is neither a definite rejection
    /// nor a pre-confirmed already-cancelled state.</summary>
    UnknownNeedsReconciliation,
}

/// <summary>
/// Return type of IApprovedActionExecutor.ExecuteApprovedActionAsync.
/// Deliberately not one flat record — a refusal that occurs before any
/// execution-intent row exists (Refused) and a result that references one
/// (Executed) are genuinely different shapes, not the same shape with an
/// optional field. The private constructor closes the hierarchy to exactly
/// these two cases; consumers pattern-match on the concrete record type.
///
/// Represents only outcomes the execution-ordering design treats as
/// legitimate business outcomes of the ordered sequence.
/// Infrastructure-level failures (missing/inactive Shopify integration,
/// token decrypt failure, non-2xx, malformed JSON, top-level GraphQL
/// errors, and a failed pre-execution ground-truth lookup) are NOT
/// represented here — they propagate as exceptions, mirroring
/// IOrderCancellationExecutor.ExecuteAsync's and
/// IShopifyOrderLookupService.GetCancellationStateAsync's own existing,
/// already-established convention that infrastructure failures are not
/// legitimate outcomes and must not be silently recorded as one.
/// </summary>
public abstract record ExecutionOutcome
{
    private ExecutionOutcome() { }

    /// <summary>No execution-intent row was created or referenced —
    /// refused during the pure-read/pre-claim portion of the ordered
    /// sequence, or by the atomic claim's own guard.</summary>
    public sealed record Refused(ExecutionRefusalReason Reason, string Detail) : ExecutionOutcome;

    /// <summary>An execution-intent row was created (the atomic claim
    /// succeeded) and the attempt reached at least the pre-execution
    /// ground-truth check. ExecutionIntentId is deliberately named
    /// tool-agnostically, not OrderCancellationId — a future
    /// refundOrder/updateCustomer executor populates this from its own
    /// execution-intent entity's Id, not OrderCancellation's.</summary>
    public sealed record Executed(Guid ExecutionIntentId, ExecutionResultStatus Status, string? Detail) : ExecutionOutcome;
}

/// <summary>
/// Staff-facing execution capability — see docs/handoffs/step-22-ai-action-safety-model-concrete-contract-design.md
/// §3 (the original, locked source of this signature — independently
/// re-verified against that exact line by docs/handoffs/step-28-decode-gap-design-note-independent-verification.md
/// §1, not merely a downstream citation) for the execution-ordering
/// contract this interface's implementer must satisfy. A completely
/// separate interface hierarchy from IDispatchTool/IActionProposalTool,
/// resolved only by the staff-facing execution endpoint's own DI scope,
/// never reachable from ToolDispatchService's AI-facing tool collection.
///
/// ExecuteApprovedActionAsync(Guid approvalId, ...) deliberately accepts no
/// other business-data parameter — BusinessId/CustomerId/ToolName/ResourceId/
/// CanonicalArguments/ConversationId/ActionHash must all be loaded from the
/// Approval row keyed by approvalId, never accepted as caller-supplied
/// values: accepting any of these from the client would create two
/// disagreeing sources of truth and defeat the hash-comparison mechanism
/// for whichever field was trusted from the caller.
///
/// executingStaffUserId (added per docs/handoffs/step-45-staff-actor-identity-threading-design.md,
/// owner decision confirmed in that document's addendum) is categorically
/// different from the fields above: it is never compared against anything
/// stored on Approval (there is no ExecutedBy column, and none is added by
/// that design), it is not one of ActionHasher.Derive's six hash inputs, and
/// its only use is as an opaque value recorded on the AuditLog rows steps
/// 6-13 write (Actor=NivoraBusinessStaff, ChangedByUserId=executingStaffUserId).
/// Non-nullable: every current and anticipated caller is an authenticated
/// staff HTTP request behind NivoraPolicies.OwnerOrAdmin + SameTenant
/// (verified upstream of this interface, never inside it — step-45 §4/§7);
/// the caller resolves it the same way every existing controller resolves
/// CurrentUserId (User.FindFirstValue(ClaimTypes.NameIdentifier)), not via
/// an injected accessor — step-45 §3 for why this deliberately does not
/// mirror ICurrentTenantAccessor's shape.
/// </summary>
public interface IApprovedActionExecutor
{
    Task<ExecutionOutcome> ExecuteApprovedActionAsync(
        Guid approvalId, Guid executingStaffUserId, CancellationToken cancellationToken);
}
