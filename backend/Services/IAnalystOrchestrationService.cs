namespace backend.Services;

/// <summary>
/// Step 23 Phase 4 — design §2/§3. The 4 outcomes the locked design itself
/// defines are Answered/Refused/CapabilityFailed/Disabled (design §3c,
/// §5) — but Disabled is the kill-switch gate, checked by the eventual
/// Phase 5 endpoint entirely *before* this service is ever invoked (design
/// §4: "before any Claude call, before any tool execution"), so this
/// service itself never produces it. ClaudeCallFailed is a 5th, this-
/// service-internal outcome the locked design does not name at all — found
/// during Phase 4 discovery (neither the design doc, the implementation
/// plan, nor the owner-decisions doc address a genuine Anthropic API
/// failure — rate limit/5xx/network/timeout — during either Claude call)
/// and added by owner decision during this phase, matching the exact
/// precedent every other Claude-calling service in this codebase already
/// follows (`ToolDispatchOutcome.ClaudeCallFailed`,
/// `ClassificationOutcome.ClaudeCallFailed`): an infrastructure failure is
/// a distinct case from a business-logic non-answer, never collapsed into
/// Refused. How ClaudeCallFailed maps onto the eventual HTTP response shape
/// is deliberately left to Phase 5 — not decided here.
/// </summary>
public enum AnalystOutcomeType
{
    Answered,
    Refused,
    CapabilityFailed,
    ClaudeCallFailed,
}

/// <summary>
/// ResponseText is the only content ever shown to the staff member.
/// Answered's ResponseText is Claude's own Phase-2 synthesis text,
/// always backed by real capability data (design §3a). The other three
/// types are always a fixed, backend-authored string — never Claude's own
/// text, per design §3a/§3b/§3c's core anti-fabrication guarantee.
/// </summary>
public record AnalystOutcome(AnalystOutcomeType Type, string ResponseText);

/// <summary>
/// Step 23 Phase 4 — the two-phase Claude dispatch engine (design §2b/§2c):
/// exactly one tool-selection call, then — only if that call selected 1-3
/// capabilities and every one of them executed successfully — exactly one
/// synthesis call with Tools withheld. Never more than 2 real Anthropic API
/// calls in any path (design §2b's "Total raw Anthropic API calls" table).
/// Deliberately Claude-free to test: built and adversarially verified
/// entirely against fake IMessageService/IAnalystCapability implementations
/// (mirroring ToolDispatchServiceTests' own pattern) — proving orchestration
/// correctness independently of Phase 3's real capabilities or Phase 5's
/// endpoint/kill-switch/HTTP shape.
/// </summary>
public interface IAnalystOrchestrationService
{
    Task<AnalystOutcome> AnswerAsync(Guid businessId, string question, CancellationToken cancellationToken = default);
}
