using backend.Enums;
using backend.Interfaces;

namespace backend.Models;

/// <summary>
/// Business-configured policy: what conversation mode a given tag implies.
/// This is routing policy only — it does NOT encode monetary thresholds.
/// Refund amount-based auto-approval is a separate mechanism
/// (Business.AutoRefundLimit); the two combine at evaluation time in
/// application logic, not in this table's shape.
///
/// IsMinimumFloor means: no matter what other logic computes for this
/// conversation (including the refund auto-limit calculation), the
/// effective mode can never go below what this mapping specifies. Not
/// constrained to Mode = Manual specifically — kept general so it can
/// override any computed mode, though Manual is the primary real use case
/// today (e.g. RefundDispute).
///
/// Changes to this table (create/update) must write an AuditLog entry
/// (EventType = TagModeMappingChanged, Actor = NivoraBusinessStaff) in
/// the same transaction — this table governs whether the AI can act
/// autonomously on tagged conversations, so changes deserve traceability.
/// </summary>
public class TagModeMapping : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid BusinessId { get; set; }

    /// <summary>
    /// Must be normalized (lowercase/kebab-case) — Conversation.Tags and
    /// this field must use the same canonical representation; do not rely
    /// on case-insensitive database comparison.
    /// </summary>
    public required string Tag { get; set; }

    public ConversationMode Mode { get; set; }
    public bool IsMinimumFloor { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
