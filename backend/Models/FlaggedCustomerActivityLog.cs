using backend.Enums;
using backend.Interfaces;

namespace backend.Models;

/// <summary>
/// Records read-level tool activity (getOrder, getInventory, etc.) for a
/// specific customer who has been flagged for closer review — separate
/// from AuditLog, which only records write/authorization-relevant events
/// for all customers by default.
///
/// Rows here are only ever written when BOTH:
///   - Business.ExtendedAuditLoggingEnabled == true (feature gate), AND
///   - Customer.ExtendedLoggingEnabled == true (this specific customer
///     was flagged, e.g. suspected fraud)
///
/// Unlike AuditLog, this table's purpose inherently requires linking
/// activity to a specific customer, so it is NOT subject to the "no PII"
/// rule the same way — Parameters may reference customer-specific data
/// (e.g. which order was queried). This is why it's opt-in per customer
/// rather than always-on: the privacy cost is real and must be a
/// deliberate choice, not a default.
/// </summary>
public class FlaggedCustomerActivityLog : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid BusinessId { get; set; }
    public Guid CustomerId { get; set; }

    /// <summary>Nullable — some activity may not be conversation-bound.</summary>
    public Guid? ConversationId { get; set; }

    /// <summary>e.g. "getOrder", "getInventory", "searchProducts".</summary>
    public required string ToolName { get; set; }

    /// <summary>
    /// What was queried/passed — e.g. {"orderId": "4471"}. Unlike
    /// AuditLog.OldValue/NewValue, this may reference customer-specific
    /// data by design, since that's the entire point of this table.
    /// </summary>
    public string? Parameters { get; set; }

    public ActorType Actor { get; set; }

    /// <summary>Populated only when Actor == NivoraBusinessStaff.</summary>
    public Guid? PerformedByUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}


