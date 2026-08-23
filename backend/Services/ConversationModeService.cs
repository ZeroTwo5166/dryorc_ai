using backend.Data;
using backend.Enums;
using backend.Models;
using Microsoft.EntityFrameworkCore;

namespace backend.Services;

/// <summary>
/// Implements the documented 4-step effective-mode computation (dev plan v2
/// Section 6 / schema reference Section 3.1) — each step may only make the
/// result stricter than the previous step, never more permissive.
/// Auto &lt; Assist &lt; Manual, using the enum's own declaration order.
///
/// IgnoreQueryFilters() + explicit BusinessId throughout, deliberately not
/// relying on the ambient tenant query filter — same defensive posture
/// WidgetConversationService/KnowledgeRetrievalService already use, since
/// this service (like those) has no HTTP-request-scoped caller guaranteeing
/// the right tenant context.
/// </summary>
public class ConversationModeService(AppDbContext db) : IConversationModeService
{
    public async Task<ConversationMode> ComputeEffectiveModeAsync(
        Conversation conversation, CancellationToken cancellationToken = default)
    {
        // Step 1: base mode from non-floor TagModeMapping matches — the
        // strictest of all matches, or Auto if the conversation has no tags
        // or none match any mapping. Case-insensitive tag matching — no
        // enforced normalization convention exists anywhere in this
        // codebase yet (nothing currently writes Conversation.Tags in
        // production); this establishes case-insensitive comparison as the
        // simplest safe rule for 13A, not a pre-existing convention.
        var mode = await ComputeBaseModeAsync(conversation.BusinessId, conversation.Tags, cancellationToken);

        // Step 2: refund-limit evaluation.
        // TODO(Step 20): implement refund-request amount evaluation once
        // the refund/tool-dispatch input exists. No legitimate source of
        // "is this a refund request" or "what amount" exists anywhere in
        // this codebase today — Refund/RefundItem are post-decision
        // execution records (written after a refund is already being
        // carried out), not a pre-decision request signal, and no
        // tool-calling/dispatch layer exists yet. Deliberately a no-op:
        // this step intentionally does not run.

        // Step 3: minimum-floor override — only ever pushes stricter.
        mode = await ApplyMinimumFloorAsync(conversation.BusinessId, conversation.Tags, mode, cancellationToken);

        // Step 4: Level 2 verification/security override.
        mode = await ApplyLevel2VerificationAsync(conversation, mode, cancellationToken);

        return mode;
    }

    private async Task<ConversationMode> ComputeBaseModeAsync(
        Guid businessId, List<string> tags, CancellationToken cancellationToken)
    {
        if (tags.Count == 0)
        {
            return ConversationMode.Auto;
        }

        var normalizedTags = tags.Select(t => t.ToLowerInvariant()).ToList();
        var matchingModes = await db.TagModeMappings
            .IgnoreQueryFilters()
            .Where(tm => tm.BusinessId == businessId && !tm.IsMinimumFloor && normalizedTags.Contains(tm.Tag.ToLower()))
            .Select(tm => tm.Mode)
            .ToListAsync(cancellationToken);

        // Strictest of all non-floor matches, established explicitly as
        // 13A's multi-tag resolution rule (not first-match/last-match/
        // database-order) — Aggregate with Auto as the seed also correctly
        // handles the zero-match case without a separate branch, since Auto
        // is the identity element for "stricter of."
        return matchingModes.Aggregate(ConversationMode.Auto, Stricter);
    }

    private async Task<ConversationMode> ApplyMinimumFloorAsync(
        Guid businessId, List<string> tags, ConversationMode currentMode, CancellationToken cancellationToken)
    {
        if (tags.Count == 0)
        {
            return currentMode;
        }

        var normalizedTags = tags.Select(t => t.ToLowerInvariant()).ToList();
        var floorModes = await db.TagModeMappings
            .IgnoreQueryFilters()
            .Where(tm => tm.BusinessId == businessId && tm.IsMinimumFloor && normalizedTags.Contains(tm.Tag.ToLower()))
            .Select(tm => tm.Mode)
            .ToListAsync(cancellationToken);

        return floorModes.Aggregate(currentMode, Stricter);
    }

    private async Task<ConversationMode> ApplyLevel2VerificationAsync(
        Conversation conversation, ConversationMode currentMode, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        if (conversation.Level2VerifiedUntil is { } until && until > now)
        {
            return currentMode;
        }

        if (conversation.CustomerId is { } customerId)
        {
            // Inlined per ExternalIdentityExtensions.IsCurrentlyValid's own
            // doc comment: that plain C# method doesn't translate to SQL
            // inside a Where()/AnyAsync() clause, and explicitly names this
            // exact raw condition as the query-site equivalent to keep in
            // sync with it — not a new verification abstraction, the same
            // documented rule, inlined where it must be to run as one
            // efficient query instead of loading rows into memory to
            // re-check client-side.
            var hasValidIdentity = await db.ExternalIdentities
                .IgnoreQueryFilters()
                .AnyAsync(ei => ei.BusinessId == conversation.BusinessId && ei.CustomerId == customerId
                    && ei.RevokedAt == null && (ei.ExpiresAt == null || ei.ExpiresAt > now), cancellationToken);

            if (hasValidIdentity)
            {
                return currentMode;
            }
        }

        return Stricter(currentMode, ConversationMode.Manual);
    }

    private static ConversationMode Stricter(ConversationMode a, ConversationMode b) =>
        (ConversationMode)Math.Max((int)a, (int)b);
}
