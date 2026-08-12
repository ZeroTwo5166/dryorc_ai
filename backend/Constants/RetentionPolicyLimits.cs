namespace backend.Constants;

/// <summary>
/// Bounds enforced on Business.RetentionDays. See NIVORA_DEVELOPMENT_PLAN.md
/// Section 10.1 — businesses configure retention within limits Nivora enforces,
/// it must never be left unbounded.
/// </summary>
public static class RetentionPolicyLimits
{
    public const int MinDays = 30;
    public const int MaxDays = 730;
    public const int DefaultDays = 180;
}