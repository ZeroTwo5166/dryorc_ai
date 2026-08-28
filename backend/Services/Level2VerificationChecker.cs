using backend.Data;
using Microsoft.EntityFrameworkCore;

namespace backend.Services;

/// <summary>
/// Mirrors ConversationModeService.ApplyLevel2VerificationAsync's own
/// OR-condition exactly — a currently-valid ExternalIdentity for the
/// linked customer OR Conversation.Level2VerifiedUntil > now (schema
/// reference doc Section 3.2; design doc Section 8.1, item 7) — kept here
/// as its own reusable read-side primitive rather than duplicated inline
/// wherever else Level 2 status needs checking, without changing
/// ConversationModeService's own existing behavior.
///
/// IgnoreQueryFilters() + explicit BusinessId throughout, matching
/// ConversationModeService/WidgetConversationService's defensive posture —
/// this service is plausibly callable from a non-HTTP context later (tool
/// dispatch), so it must not rely on the ambient tenant query filter.
/// </summary>
public class Level2VerificationChecker(AppDbContext db) : ILevel2VerificationChecker
{
    public async Task<bool> IsVerifiedAsync(Guid businessId, Guid conversationId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        var conversation = await db.Conversations
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == conversationId && c.BusinessId == businessId, cancellationToken);

        if (conversation is null)
        {
            return false;
        }

        if (conversation.Level2VerifiedUntil is { } until && until > now)
        {
            return true;
        }

        if (conversation.CustomerId is not { } customerId)
        {
            return false;
        }

        // Inlined per ExternalIdentityExtensions.IsCurrentlyValid's own doc
        // comment — the query-site equivalent of that plain C# method, kept
        // in sync with it, same as ConversationModeService's own inlining.
        return await db.ExternalIdentities
            .IgnoreQueryFilters()
            .AnyAsync(ei => ei.BusinessId == businessId && ei.CustomerId == customerId
                && ei.RevokedAt == null && (ei.ExpiresAt == null || ei.ExpiresAt > now), cancellationToken);
    }
}
