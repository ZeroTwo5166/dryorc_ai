namespace backend.Enums;

/// <summary>
/// Exactly three fixed, closed categories — a real enum, not a string
/// constants class, per NIVORA_DEVELOPMENT_PLAN_V2.md Section 11's rule
/// ("real C# enum for small, closed, stable value sets"). Contrast with
/// Notification.Type (NotificationTypes), which is open-ended and grows
/// without a migration.
/// </summary>
public enum NotificationCategory
{
    Team,
    Customer,
    System
}
