using backend.Data;
using Microsoft.EntityFrameworkCore;

namespace backend.Services;

public interface IRetentionEnforcementService
{
    /// <summary>Purges every conversation, across every business, past its retention cutoff. Returns how many were purged.</summary>
    Task<int> EnforceRetentionAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The scheduled counterpart to CustomerErasureService — same underlying
/// ConversationPurgeService, different trigger: age-based and per-business
/// rather than on-demand and per-customer (see ConversationPurgeService's
/// own doc comment for why both share one operation instead of two
/// divergent deletion implementations).
///
/// Cutoff: Conversation.LastMessageAt &lt; now - Business.RetentionDays,
/// falling back to Conversation.CreatedAt for conversations with no
/// messages at all. Already-anonymized conversations are excluded up
/// front — PurgeConversationAsync would no-op on them anyway, but skipping
/// them here avoids the round-trip.
///
/// Explicitly cross-tenant by nature (iterates every business in one
/// pass), so — like ConversationPurgeService/CustomerErasureService —
/// every query here uses IgnoreQueryFilters() with an explicit BusinessId
/// filter rather than the ambient ICurrentTenantAccessor, which has
/// nothing to resolve in this no-HttpContext, scheduled-job context.
/// </summary>
public class RetentionEnforcementService(AppDbContext db, IConversationPurgeService purgeService)
    : IRetentionEnforcementService
{
    public async Task<int> EnforceRetentionAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;

        var businesses = await db.Businesses
            .IgnoreQueryFilters()
            .Select(b => new { b.Id, b.RetentionDays })
            .ToListAsync(cancellationToken);

        var purgedCount = 0;

        foreach (var business in businesses)
        {
            var cutoff = now.AddDays(-business.RetentionDays);

            var expiredConversationIds = await db.Conversations
                .IgnoreQueryFilters()
                .Where(c => c.BusinessId == business.Id && !c.IsAnonymized)
                .Where(c =>
                    (c.LastMessageAt != null && c.LastMessageAt < cutoff) ||
                    (c.LastMessageAt == null && c.CreatedAt < cutoff))
                .Select(c => c.Id)
                .ToListAsync(cancellationToken);

            foreach (var conversationId in expiredConversationIds)
            {
                await purgeService.PurgeConversationAsync(conversationId, cancellationToken);
                purgedCount++;
            }
        }

        return purgedCount;
    }
}
