using backend.Data;
using backend.Enums;
using backend.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace backend.Services;

/// <summary>
/// See ICustomerUpdateActionService for the full contract and the
/// idempotency-mechanism handoff for the design rationale. Structurally
/// identical to OrderCancellationService — see that class's own doc comment
/// for the shared insert-first/detach-on-collision rationale.
/// </summary>
public class CustomerUpdateActionService(AppDbContext db) : ICustomerUpdateActionService
{
    private const string IdempotencyIndexName = "IX_CustomerUpdateActions_BusinessId_IdempotencyKey";

    public async Task<CustomerUpdateActionResult> RequestUpdateAsync(
        Guid businessId, Guid customerId, Guid? conversationId,
        List<string> fieldsChanged, string idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 256)
        {
            throw new ArgumentException(
                "Idempotency key must be non-empty and at most 256 characters.", nameof(idempotencyKey));
        }

        var now = DateTimeOffset.UtcNow;
        var action = new CustomerUpdateAction
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            CustomerId = customerId,
            ConversationId = conversationId,
            FieldsChanged = fieldsChanged,
            IdempotencyKey = idempotencyKey,
            Status = CustomerUpdateActionStatus.Pending,
            RequestedAt = now,
            UpdatedAt = now,
        };
        db.CustomerUpdateActions.Add(action);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return new CustomerUpdateActionResult(action.Id, IdempotentOperationOutcome.InProgress, null);
        }
        catch (DbUpdateException ex) when (
            ex.InnerException is PostgresException { SqlState: "23505", ConstraintName: IdempotencyIndexName })
        {
            db.Entry(action).State = EntityState.Detached;

            // AsNoTracking is required, not optional — see
            // OrderCancellationService.RequestCancellationAsync's own
            // comment on this same pattern.
            var existing = await db.CustomerUpdateActions
                .IgnoreQueryFilters()
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    x => x.BusinessId == businessId && x.IdempotencyKey == idempotencyKey, cancellationToken);

            if (existing is null)
            {
                throw;
            }

            var sameIntent = existing.CustomerId == customerId
                && new HashSet<string>(existing.FieldsChanged).SetEquals(fieldsChanged);

            if (!sameIntent)
            {
                return new CustomerUpdateActionResult(
                    existing.Id, IdempotentOperationOutcome.KeyReusedWithDifferentIntent, null);
            }

            return existing.Status switch
            {
                CustomerUpdateActionStatus.Confirmed =>
                    new CustomerUpdateActionResult(existing.Id, IdempotentOperationOutcome.Confirmed, null),
                CustomerUpdateActionStatus.Failed =>
                    new CustomerUpdateActionResult(existing.Id, IdempotentOperationOutcome.Failed, existing.FailureReason),
                _ => new CustomerUpdateActionResult(existing.Id, IdempotentOperationOutcome.InProgress, null),
            };
        }
    }

    public Task MarkConfirmedAsync(Guid customerUpdateActionId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        return db.CustomerUpdateActions
            .IgnoreQueryFilters()
            .Where(x => x.Id == customerUpdateActionId && x.Status == CustomerUpdateActionStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, CustomerUpdateActionStatus.Confirmed)
                .SetProperty(x => x.CompletedAt, now)
                .SetProperty(x => x.UpdatedAt, now), cancellationToken);
    }

    public Task MarkFailedAsync(Guid customerUpdateActionId, string failureReason, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        return db.CustomerUpdateActions
            .IgnoreQueryFilters()
            .Where(x => x.Id == customerUpdateActionId && x.Status == CustomerUpdateActionStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, CustomerUpdateActionStatus.Failed)
                .SetProperty(x => x.FailureReason, failureReason)
                .SetProperty(x => x.UpdatedAt, now), cancellationToken);
    }
}
