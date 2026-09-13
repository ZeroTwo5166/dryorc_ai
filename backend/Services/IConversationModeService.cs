using backend.Enums;
using backend.Models;

namespace backend.Services;

/// <summary>
/// Step 13A — computes a conversation's effective ConversationMode. Steps 1
/// (base mode)/3 (minimum floor)/4 (Level 2 verification) of the documented
/// 4-step order are implemented; step 2 (refund-limit evaluation) is
/// deliberately deferred — see ConversationModeService's own doc comment.
///
/// No production caller exists yet, deliberately — standalone, tested
/// domain logic ahead of its eventual integration (matching KnowledgeChunker's
/// precedent from Step 11). Not wired into ClaudeReplyService, AI reply
/// generation, or human handoff in 13A.
/// </summary>
public interface IConversationModeService
{
    Task<ConversationMode> ComputeEffectiveModeAsync(Conversation conversation, CancellationToken cancellationToken = default);

    /// <summary>
    /// Step 22 — calls the unchanged ComputeEffectiveModeAsync, then applies
    /// Stricter(conversation.EffectiveMode, computedMode) and sets the
    /// result on conversation.EffectiveMode before returning it. Mutates the
    /// tracked entity only — does not call SaveChangesAsync; the caller's
    /// own transaction commits it, matching this codebase's established
    /// pattern (e.g. WidgetConversationService's LastMessageAt write). The
    /// persisted field only ever moves upward: a lower computedMode (e.g.
    /// after a tag is removed) leaves the existing, stricter value
    /// unchanged — see step-22 design doc §C.
    /// </summary>
    Task<ConversationMode> ComputeAndRatchetEffectiveModeAsync(Conversation conversation, CancellationToken cancellationToken = default);
}
