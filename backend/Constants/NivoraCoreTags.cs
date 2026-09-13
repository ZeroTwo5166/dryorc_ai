namespace backend.Constants;

/// <summary>
/// Step 22 — the Nivora-wide core classification vocabulary. Illustrative
/// list adopted verbatim from the owner-decision doc (not a final taxonomy
/// decision by this pass). Plain string + static class constants, matching
/// NIVORA_SCHEMA_AND_LOGIC_REFERENCE.md §11's own convention for
/// open-ended, growing value sets — same shape as ConversationChannels/
/// AuditEntityTypes/AuditEventTypes. No DB-level enum/CHECK constraint;
/// Conversation.Tags/TagModeMapping.Tag stay plain strings. "Core" vs.
/// "custom" is a derived property at read time (is this string present in
/// All?), not a stored fact — see step-22 design doc §B.
/// </summary>
public static class NivoraCoreTags
{
    public const string Order = "order";
    public const string Shipping = "shipping";
    public const string Refund = "refund";
    public const string Payment = "payment";
    public const string Technical = "technical";
    public const string Complaint = "complaint";
    public const string General = "general";

    public static readonly IReadOnlyList<string> All =
        [Order, Shipping, Refund, Payment, Technical, Complaint, General];
}
