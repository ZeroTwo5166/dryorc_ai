using backend.Data;
using backend.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace backend.Services;

/// <summary>
/// See IOrderCancellationReconciliationService for the contract. Each
/// Pending row is reconciled independently — one row's failure (a
/// transient Shopify error, a missing Integration) is logged and does not
/// stop the sweep from processing the rest.
/// </summary>
public class OrderCancellationReconciliationService(
    AppDbContext db,
    IShopifyOrderLookupService shopifyOrderLookupService,
    IOrderCancellationService orderCancellationService,
    ILogger<OrderCancellationReconciliationService> logger) : IOrderCancellationReconciliationService
{
    /// <summary>
    /// Proposed default, not derived from any Shopify-documented job
    /// duration (none exists — see the design handoff). A row still
    /// Pending with Order.cancelledAt still null past this age is flagged,
    /// never auto-retried (an in-flight job vs. a genuinely lost request
    /// cannot be distinguished without a "call attempted" marker this pass
    /// does not add).
    /// </summary>
    private static readonly TimeSpan StuckThreshold = TimeSpan.FromMinutes(30);

    public async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        var pendingRows = await db.OrderCancellations
            .IgnoreQueryFilters()
            .Where(x => x.Status == OrderCancellationStatus.Pending)
            .Select(x => new { x.Id, x.BusinessId, x.ExternalOrderId, x.RequestedAt })
            .ToListAsync(cancellationToken);

        foreach (var row in pendingRows)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var state = await shopifyOrderLookupService.GetCancellationStateAsync(
                    row.BusinessId, row.ExternalOrderId, cancellationToken);

                if (state.CancelledAt is not null)
                {
                    await orderCancellationService.MarkConfirmedAsync(row.Id, cancellationToken);
                    continue;
                }

                if (DateTimeOffset.UtcNow - row.RequestedAt > StuckThreshold)
                {
                    logger.LogWarning(
                        "OrderCancellation {OrderCancellationId} for business {BusinessId} has been Pending " +
                        "for over {ThresholdMinutes} minutes with Shopify still reporting the order uncancelled. " +
                        "Flagging for manual review — no automated retry, no automated escalation.",
                        row.Id, row.BusinessId, StuckThreshold.TotalMinutes);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex, "Reconciliation failed for OrderCancellation {OrderCancellationId} (business {BusinessId}).",
                    row.Id, row.BusinessId);
            }
        }
    }
}
