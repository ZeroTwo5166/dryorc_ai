namespace backend.Constants;

/// <summary>
/// String constants for Conversation.Channel. Use these instead of raw
/// string literals so typos are caught at compile time — critical here
/// since Channel participates in SlaTargets(BusinessId, Priority, Channel)
/// matching, and a mismatched/misspelled value silently breaks SLA lookup
/// with no error. Add new values here as new channels come online (v2
/// Instagram, v3/v4 WhatsApp) — no migration required, since the
/// underlying column is just varchar.
/// </summary>
public static class ConversationChannels
{
    public const string Website = "Website";
    public const string Email = "Email";
    // Instagram, WhatsApp added here when those integrations ship
}