namespace backend.Constants;

/// <summary>
/// Bounds enforced on Business.ConversationInactivityCloseDays. See
/// docs/handoffs/step-25-candidate2-stale-conversation-autoclose-design.md
/// §5 and the implementation plan's §1.3/§1.5 — mirrors
/// RetentionPolicyLimits.cs's exact shape.
/// </summary>
public static class ConversationAutoCloseLimits
{
    public const int MinDays = 1;
    public const int MaxDays = 365;
    public const int DefaultDays = 14;
}
