using backend.Data;
using backend.Enums;
using backend.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace backend.Services;

/// <summary>
/// See IOrderCancellationService for the full contract and idempotency
/// mechanism handoff for the design rationale. IgnoreQueryFilters() +
/// explicit BusinessId, same non-HTTP-service defensive posture as
/// OrderOwnershipAuthorizationService/VerificationCodeService — this
/// service has no HTTP-request-scoped caller guaranteeing the right tenant
/// context.
///
/// Insert-first, no pre-check query — IX_OrderCancellations_BusinessId_IdempotencyKey
/// is the sole race arbiter, same convention as CustomersController.LinkIdentity/
/// TicketsController.Create. The failed insert's tracked entity is explicitly
/// detached on collision so this scoped DbContext stays safe for later use
/// by the same request/scope (a lingering Added entity would otherwise be
/// resubmitted by any later SaveChangesAsync on this context).
/// </summary>
public class OrderCancellationService(AppDbContext db) : IOrderCancellationService
{
    private const string IdempotencyIndexName = "IX_OrderCancellations_BusinessId_IdempotencyKey";
    private const string PendingGuardIndexName = "IX_OrderCancellations_BusinessId_ExternalOrderId_Pending";

    public async Task<OrderCancellationResult> RequestCancellationAsync(
        Guid businessId, Guid customerId, Guid? conversationId,
        string externalOrderId, string? customerReason,
        string idempotencyKey, bool restock, RefundMethod? refundMethod, OrderCancelReason reason,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 256)
        {
            throw new ArgumentException(
                "Idempotency key must be non-empty and at most 256 characters.", nameof(idempotencyKey));
        }

        var now = DateTimeOffset.UtcNow;
        var cancellation = new OrderCancellation
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            CustomerId = customerId,
            ConversationId = conversationId,
            ExternalOrderId = externalOrderId,
            CustomerReason = customerReason,
            IdempotencyKey = idempotencyKey,
            Status = OrderCancellationStatus.Pending,
            Restock = restock,
            Method = refundMethod,
            Reason = reason,
            RequestedAt = now,
            UpdatedAt = now,
        };
        db.OrderCancellations.Add(cancellation);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return new OrderCancellationResult(cancellation.Id, IdempotentOperationOutcome.InProgress, null);
        }
        catch (DbUpdateException ex) when (
            ex.InnerException is PostgresException { SqlState: "23505", ConstraintName: IdempotencyIndexName })
        {
            db.Entry(cancellation).State = EntityState.Detached;

            // AsNoTracking is required, not optional: MarkConfirmedAsync/
            // MarkFailedAsync use ExecuteUpdateAsync, which bypasses the
            // change tracker entirely (same idiom as
            // NotificationsController.MarkRead/VerificationCodeService). A
            // tracking query here could return a stale cached instance from
            // an earlier read on this same scoped DbContext instead of the
            // row's current DB state.
            var existing = await db.OrderCancellations
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

            var sameIntent = existing.ExternalOrderId == externalOrderId
                && existing.Restock == restock
                && existing.Method == refundMethod
                && existing.Reason == reason;

            if (!sameIntent)
            {
                return new OrderCancellationResult(
                    existing.Id, IdempotentOperationOutcome.KeyReusedWithDifferentIntent, null);
            }

            return existing.Status switch
            {
                OrderCancellationStatus.Confirmed =>
                    new OrderCancellationResult(existing.Id, IdempotentOperationOutcome.Confirmed, null),
                OrderCancellationStatus.Failed =>
                    new OrderCancellationResult(existing.Id, IdempotentOperationOutcome.Failed, existing.FailureReason),
                _ => new OrderCancellationResult(existing.Id, IdempotentOperationOutcome.InProgress, null),
            };
        }
        // Step 23S — Owner Decision (23Q §3, Option B): the new
        // (BusinessId, ExternalOrderId) WHERE Status='Pending' guard is
        // signaled via a distinct thrown exception, not a new
        // IdempotentOperationOutcome value. Same insert-and-catch idiom as
        // the existing clause above, distinguished only by ConstraintName —
        // the two conflicts are structurally different (a different
        // idempotency key colliding against a row from an entirely separate
        // caller/Approval, not "the same key reused with different intent"),
        // so this is not a variant of KeyReusedWithDifferentIntent.
        catch (DbUpdateException ex) when (
            ex.InnerException is PostgresException { SqlState: "23505", ConstraintName: PendingGuardIndexName })
        {
            db.Entry(cancellation).State = EntityState.Detached;
            throw new DuplicatePendingOrderCancellationException(businessId, externalOrderId);
        }
    }

    public Task MarkConfirmedAsync(Guid orderCancellationId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        return db.OrderCancellations
            .IgnoreQueryFilters()
            .Where(x => x.Id == orderCancellationId && x.Status == OrderCancellationStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, OrderCancellationStatus.Confirmed)
                .SetProperty(x => x.CompletedAt, now)
                .SetProperty(x => x.UpdatedAt, now), cancellationToken);
    }

    public Task MarkFailedAsync(Guid orderCancellationId, string failureReason, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        return db.OrderCancellations
            .IgnoreQueryFilters()
            .Where(x => x.Id == orderCancellationId && x.Status == OrderCancellationStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, OrderCancellationStatus.Failed)
                .SetProperty(x => x.FailureReason, failureReason)
                .SetProperty(x => x.UpdatedAt, now), cancellationToken);
    }
}
