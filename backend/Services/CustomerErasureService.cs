using backend.Data;
using Microsoft.EntityFrameworkCore;

namespace backend.Services;

public interface ICustomerErasureService
{
    Task EraseCustomerAsync(Guid customerId, CancellationToken cancellationToken = default);
}

/// <summary>
/// GDPR data-subject erasure request handling. Three ordered phases, each
/// independently safe to retry:
///
/// 1. Delete the pure identity/security/logging rows that have no reason
///    to survive erasure (ExternalIdentity, FlaggedCustomerActivityLog —
///    both previously documented but never actually implemented in code;
///    CustomerExtendedLoggingHistory, VerificationCode — previously
///    missing from the documented erasure steps entirely). All four are
///    plain DELETE-WHERE operations, naturally idempotent (deleting zero
///    matching rows on a retry is harmless), so no completion tracking is
///    needed for this phase.
/// 2. Purge every Conversation this customer had, via the same
///    ConversationPurgeService the retention job uses — each call is
///    independently atomic and idempotent.
/// 3. Anonymize the Customer row's own identity fields (Email/Name/
///    ExternalId → null, IsAnonymized/AnonymizedAt set) — deliberately
///    LAST, so IsAnonymized == true reliably means "this customer's
///    erasure fully completed," not just "we started."
///
/// Like ConversationPurgeService, BusinessId is resolved explicitly from
/// the loaded Customer row (not the ambient ICurrentTenantAccessor) so
/// this works correctly whether it's triggered by an authenticated request
/// or a system process with no HttpContext at all.
/// </summary>
public class CustomerErasureService(AppDbContext db, IConversationPurgeService purgeService) : ICustomerErasureService
{
    public async Task EraseCustomerAsync(Guid customerId, CancellationToken cancellationToken = default)
    {
        var customer = await db.Customers
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == customerId, cancellationToken);

        if (customer is null)
        {
            return;
        }

        var businessId = customer.BusinessId;

        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            await db.ExternalIdentities
                .IgnoreQueryFilters()
                .Where(e => e.BusinessId == businessId && e.CustomerId == customerId)
                .ExecuteDeleteAsync(cancellationToken);

            await db.FlaggedCustomerActivityLogs
                .IgnoreQueryFilters()
                .Where(f => f.BusinessId == businessId && f.CustomerId == customerId)
                .ExecuteDeleteAsync(cancellationToken);

            await db.CustomerExtendedLoggingHistories
                .IgnoreQueryFilters()
                .Where(h => h.BusinessId == businessId && h.CustomerId == customerId)
                .ExecuteDeleteAsync(cancellationToken);

            await db.VerificationCodes
                .IgnoreQueryFilters()
                .Where(v => v.BusinessId == businessId && v.CustomerId == customerId)
                .ExecuteDeleteAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }

        var conversationIds = await db.Conversations
            .IgnoreQueryFilters()
            .Where(c => c.BusinessId == businessId && c.CustomerId == customerId)
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);

        foreach (var conversationId in conversationIds)
        {
            await purgeService.PurgeConversationAsync(conversationId, cancellationToken);
        }

        if (!customer.IsAnonymized)
        {
            customer.Email = null;
            customer.Name = null;
            customer.ExternalId = null;
            customer.IsAnonymized = true;
            customer.AnonymizedAt = DateTimeOffset.UtcNow;
            customer.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
