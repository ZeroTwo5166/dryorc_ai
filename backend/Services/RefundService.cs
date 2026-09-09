using backend.Data;
using backend.Enums;
using backend.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace backend.Services;

/// <summary>
/// See IRefundService for the full contract. IgnoreQueryFilters() +
/// explicit BusinessId, same non-HTTP-service defensive posture as
/// OrderCancellationService/OrderOwnershipAuthorizationService/
/// VerificationCodeService — this service has no HTTP-request-scoped
/// caller guaranteeing the right tenant context.
///
/// Insert-first, no pre-check query — IX_Refunds_BusinessId_IdempotencyKey
/// is the sole race arbiter, same convention as OrderCancellationService/
/// CustomersController.LinkIdentity/TicketsController.Create. The Refund
/// row and its RefundItem rows are added to the same change tracker and
/// flushed in one SaveChangesAsync call — on a 23505 collision, every
/// tracked entity from that failed attempt (the Refund and each
/// RefundItem) is explicitly detached so this scoped DbContext stays safe
/// for later use by the same request/scope.
///
/// Deliberately does NOT resolve Shopify pricing, does NOT call Shopify,
/// and does NOT compute RefundItem.Amount — see IRefundService's own doc
/// comment and docs/handoffs/step-refundorder-design.md §8.3/§10. Every
/// monetary value this service persists is accepted as an already-resolved
/// input, never derived here.
/// </summary>
public class RefundService(AppDbContext db) : IRefundService
{
    private const string IdempotencyIndexName = "IX_Refunds_BusinessId_IdempotencyKey";

    public async Task<RefundResult> RequestRefundAsync(
        Guid businessId, Guid customerId, Guid? conversationId,
        string externalOrderId, string? customerReason,
        string idempotencyKey, RefundMethod refundMethod, string currency,
        IReadOnlyList<RefundItemLine> items,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 256)
        {
            throw new ArgumentException(
                "Idempotency key must be non-empty and at most 256 characters.", nameof(idempotencyKey));
        }

        if (string.IsNullOrWhiteSpace(externalOrderId))
        {
            throw new ArgumentException("External order id must be non-empty.", nameof(externalOrderId));
        }

        if (string.IsNullOrWhiteSpace(currency))
        {
            throw new ArgumentException("Currency must be non-empty.", nameof(currency));
        }

        if (items.Count == 0)
        {
            throw new ArgumentException(
                "A refund request must identify at least one line item — there is no flat, order-level " +
                "refund amount in this contract (design doc §10, Owner Decision 1).", nameof(items));
        }

        var seenLineItems = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.ExternalLineItemId))
            {
                throw new ArgumentException("Each refund item must have a non-empty ExternalLineItemId.", nameof(items));
            }

            if (item.Quantity <= 0)
            {
                throw new ArgumentException(
                    $"Refund item '{item.ExternalLineItemId}' has a non-positive quantity ({item.Quantity}).", nameof(items));
            }

            if (item.ResolvedAmount <= 0)
            {
                throw new ArgumentException(
                    $"Refund item '{item.ExternalLineItemId}' has a non-positive resolved amount ({item.ResolvedAmount}).",
                    nameof(items));
            }

            if (!seenLineItems.Add(item.ExternalLineItemId))
            {
                throw new ArgumentException(
                    $"Refund item '{item.ExternalLineItemId}' is listed more than once in the same request.", nameof(items));
            }
        }

        var now = DateTimeOffset.UtcNow;
        var refund = new Refund
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            CustomerId = customerId,
            ConversationId = conversationId,
            ExternalOrderId = externalOrderId,
            Method = refundMethod,
            Currency = currency,
            IdempotencyKey = idempotencyKey,
            Status = RefundStatus.Pending,
            RequestedAt = now,
            UpdatedAt = now,
        };
        var refundItems = items
            .Select(item => new RefundItem
            {
                Id = Guid.NewGuid(),
                BusinessId = businessId,
                RefundId = refund.Id,
                ExternalLineItemId = item.ExternalLineItemId,
                Quantity = item.Quantity,
                Amount = item.ResolvedAmount,
            })
            .ToList();

        db.Refunds.Add(refund);
        db.RefundItems.AddRange(refundItems);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return new RefundResult(refund.Id, IdempotentOperationOutcome.InProgress, null);
        }
        catch (DbUpdateException ex) when (
            ex.InnerException is PostgresException { SqlState: "23505", ConstraintName: IdempotencyIndexName })
        {
            db.Entry(refund).State = EntityState.Detached;
            foreach (var refundItem in refundItems)
            {
                db.Entry(refundItem).State = EntityState.Detached;
            }

            // AsNoTracking is required, not optional: MarkConfirmedAsync/
            // MarkFailedAsync use ExecuteUpdateAsync, which bypasses the
            // change tracker entirely (same idiom as
            // OrderCancellationService/NotificationsController.MarkRead/
            // VerificationCodeService). A tracking query here could return a
            // stale cached instance from an earlier read on this same
            // scoped DbContext instead of the row's current DB state.
            var existing = await db.Refunds
                .IgnoreQueryFilters()
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    x => x.BusinessId == businessId && x.IdempotencyKey == idempotencyKey, cancellationToken);

            if (existing is null)
            {
                // The row that caused the 23505 is gone by the time we
                // re-read — an ambiguous state this service must never
                // silently paper over.
                throw;
            }

            var existingItems = await db.RefundItems
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(x => x.RefundId == existing.Id)
                .Select(x => new { x.ExternalLineItemId, x.Quantity })
                .ToListAsync(cancellationToken);

            var sameIntent = existing.ExternalOrderId == externalOrderId
                && existing.Method == refundMethod
                && SameItemSet(existingItems.Select(x => (x.ExternalLineItemId, x.Quantity)), items);

            if (!sameIntent)
            {
                return new RefundResult(existing.Id, IdempotentOperationOutcome.KeyReusedWithDifferentIntent, null);
            }

            return existing.Status switch
            {
                RefundStatus.Confirmed =>
                    new RefundResult(existing.Id, IdempotentOperationOutcome.Confirmed, null),
                RefundStatus.Failed =>
                    new RefundResult(existing.Id, IdempotentOperationOutcome.Failed, existing.FailureReason),
                _ => new RefundResult(existing.Id, IdempotentOperationOutcome.InProgress, null),
            };
        }
    }

    /// <summary>
    /// Order-independent set equality over (ExternalLineItemId, Quantity)
    /// pairs — design doc §9.1: "the set of (ExternalLineItemId, Quantity)
    /// pairs... [t]wo requests naming the same order/method but a
    /// different set of items or different quantities for the same item
    /// are different intents." ResolvedAmount is deliberately excluded from
    /// this comparison — it is a derived fact about the intent, not part of
    /// the caller's own intent, exactly as CustomerReason is excluded from
    /// OrderCancellationService's own sameIntent comparison.
    /// </summary>
    private static bool SameItemSet(
        IEnumerable<(string ExternalLineItemId, int Quantity)> existingItems, IReadOnlyList<RefundItemLine> requestedItems)
    {
        var existingSet = existingItems.ToHashSet();
        var requestedSet = requestedItems
            .Select(x => (x.ExternalLineItemId, x.Quantity))
            .ToHashSet();

        return existingSet.SetEquals(requestedSet);
    }

    public Task MarkConfirmedAsync(
        Guid refundId, string externalRefundId, decimal totalAmount, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        return db.Refunds
            .IgnoreQueryFilters()
            .Where(x => x.Id == refundId && x.Status == RefundStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, RefundStatus.Confirmed)
                .SetProperty(x => x.ExternalRefundId, externalRefundId)
                .SetProperty(x => x.TotalAmount, totalAmount)
                .SetProperty(x => x.CompletedAt, now)
                .SetProperty(x => x.UpdatedAt, now), cancellationToken);
    }

    public Task MarkFailedAsync(Guid refundId, string failureReason, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        return db.Refunds
            .IgnoreQueryFilters()
            .Where(x => x.Id == refundId && x.Status == RefundStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, RefundStatus.Failed)
                .SetProperty(x => x.FailureReason, failureReason)
                .SetProperty(x => x.UpdatedAt, now), cancellationToken);
    }
}
