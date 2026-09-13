using System.Text.Json;
using backend.Constants;
using backend.Enums;

namespace backend.Services;

/// <summary>
/// Step 22 Phase 3 — the staff tag-correction write path. Deliberately
/// minimal: one operation (replace Conversation.Tags), no separate
/// add/remove endpoints, no history — see the step-22 design doc's owner
/// decision 5 guardrail ("must not grow into a general tagging-management
/// system"). Normalizes the same way classification does
/// (.Trim().ToLowerInvariant()), for the same reason
/// (ConversationClassificationService.cs's own Normalize) — Conversation.Tags
/// stays consistently normalized regardless of which writer touched it.
///
/// Never writes Conversation.EffectiveMode directly — every Tags mutation
/// goes through IConversationModeService.ApplyTagsAndRatchetAsync, the one
/// method allowed to advance that field, and only upward. This is what
/// makes the no-downgrade guarantee hold for staff-initiated tag removal
/// the same way it already holds for AI-initiated reclassification: both
/// writers funnel through the identical lock-guarded ratchet, not two
/// independently-written code paths that happen to look similar.
///
/// Every successful correction writes one AuditLog row
/// (ConversationTagsCorrected), added to the same DbContext change tracker
/// inside the ApplyTagsAndRatchetAsync callback — so it commits atomically
/// with the Tags mutation and the ratchet, never as a separate write. Same
/// category as TagModeMappingChanged/ApprovalApproved/ApprovalDenied: a
/// write path that directly feeds AI-autonomy mode computation.
/// </summary>
public interface IConversationTagCorrectionService
{
    Task CorrectTagsAsync(
        Guid businessId, Guid conversationId, IReadOnlyList<string> tags, Guid changedByUserId,
        CancellationToken cancellationToken = default);
}

public class ConversationTagCorrectionService(
    IConversationModeService modeService, IAuditLogService auditLogService) : IConversationTagCorrectionService
{
    public async Task CorrectTagsAsync(
        Guid businessId, Guid conversationId, IReadOnlyList<string> tags, Guid changedByUserId,
        CancellationToken cancellationToken = default)
    {
        var normalizedTags = tags
            .Select(t => t.Trim().ToLowerInvariant())
            .Where(t => t.Length > 0)
            .Distinct()
            .ToList();

        await modeService.ApplyTagsAndRatchetAsync(businessId, conversationId, async (conversation, ct) =>
        {
            // Tag names only, matching CustomerUpdateAction.FieldsChanged's
            // field-names-only precedent — but here the names ARE the whole
            // content (categorical labels like "refund"/"shipping", never
            // customer PII, same content TagModeMapping.Tag already logs
            // directly), so the full added/removed name lists are logged,
            // not just a changed/unchanged flag.
            var removedTags = conversation.Tags.Except(normalizedTags).ToList();
            var addedTags = normalizedTags.Except(conversation.Tags).ToList();

            conversation.Tags = normalizedTags;

            // A PrimaryTag no longer present among the corrected Tags is
            // orphaned — cleared for consistency, not because staff asked
            // for PrimaryTag specifically (this endpoint only accepts
            // Tags; PrimaryTag is display-only per step-22 design doc §D
            // and has zero effect on mode computation either way).
            if (conversation.PrimaryTag is not null && !normalizedTags.Contains(conversation.PrimaryTag))
            {
                conversation.PrimaryTag = null;
            }

            await auditLogService.LogAsync(
                businessId, AuditEntityTypes.Conversation, conversationId.ToString(),
                AuditEventTypes.ConversationTagsCorrected, ActorType.NivoraBusinessStaff,
                changedByUserId: changedByUserId,
                oldValue: JsonSerializer.Serialize(new { removedTags }),
                newValue: JsonSerializer.Serialize(new { addedTags }),
                conversationId: conversationId,
                cancellationToken: ct);
        }, cancellationToken);
    }
}
