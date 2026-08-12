using backend.Interfaces;

namespace backend.Models;

/// <summary>
/// Amount is what was REQUESTED for this line item — not necessarily what
/// Shopify actually confirmed refunded (see Refund.TotalAmount for that).
/// No per-line-item ActualAmount in v1; add one later if per-item
/// reconciliation becomes necessary.
/// </summary>
public class RefundItem : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid BusinessId { get; set; }
    public Guid RefundId { get; set; }

    public required string ExternalLineItemId { get; set; }
    public int Quantity { get; set; }
    public decimal Amount { get; set; }
}
