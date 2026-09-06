using backend.Enums;
using backend.Interfaces;

namespace backend.Models;

/// <summary>
/// Step 22 AI Action Safety Model — the durable human-approval record for an
/// AI-proposed write action. See docs/handoffs/step-22-ai-action-safety-model-concrete-contract-design-v2.md
/// §5 for the full field-by-field contract, docs/handoffs/step-22t-approval-customerid-nullability.md
/// for why CustomerId is required non-null (a proposal cannot exist for a
/// conversation with no resolved customer), and
/// docs/handoffs/step-22w-approval-migration-design-independent-verification.md
/// §7 for why ConversationId's delete behavior is Cascade rather than
/// Restrict.
///
/// BusinessId, ConversationId, CustomerId, ToolName, ResourceId,
/// CanonicalArguments, and ActionHash are immutable once created — no
/// application code may issue a second UPDATE against any of these columns
/// after INSERT. The human-approval gate's entire value depends on "what a
/// staff member saw and approved is exactly what executes"; mutating any of
/// these after creation would defeat that without bypassing any
/// authorization check.
///
/// This entity is persistence only, per Step 22X's explicitly narrow scope
/// — no proposal, approval, denial, execution, action-hash computation,
/// Level 2, ownership, or audit logic exists anywhere yet. Nothing
/// currently constructs, reads, or writes this table.
/// </summary>
public class Approval : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid BusinessId { get; set; }
    public Guid ConversationId { get; set; }
    public Guid CustomerId { get; set; }

    public required string ToolName { get; set; }
    public required string ResourceId { get; set; }
    public required string CanonicalArguments { get; set; }
    public required string ActionHash { get; set; }
    public required string DisplaySummary { get; set; }

    public ApprovalStatus Status { get; set; } = ApprovalStatus.Pending;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? ApprovedAt { get; set; }
    public Guid? ApprovedBy { get; set; }

    public DateTimeOffset? DeniedAt { get; set; }
    public Guid? DeniedBy { get; set; }

    /// <summary>
    /// Optional, staff-supplied free text explaining a denial — Step 47
    /// addendum decision. No max length, matching Ticket.Summary's identical
    /// optional-free-text convention. Never populated for any other status
    /// transition.
    /// </summary>
    public string? DeniedReason { get; set; }

    public DateTimeOffset? ConsumedAt { get; set; }

    public Guid? OrderCancellationId { get; set; }
}
