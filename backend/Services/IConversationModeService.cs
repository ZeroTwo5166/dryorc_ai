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
}
