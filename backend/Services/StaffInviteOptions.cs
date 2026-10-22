namespace backend.Services;

/// <summary>
/// Staff invitations (DEC D7, D12; staff-invite design Section 4). Bound from
/// the "StaffInvites" configuration section and validated at start-up: every
/// number must be greater than zero.
///
/// Enabled is the release switch. Its code default is false and is never
/// changed in code (D7); it is turned on per environment through
/// configuration (appsettings.Development.json, the test host).
///
/// The invitation expiry (7 days) and the retention after expiry (30 days)
/// are fixed by the design and are deliberately not options.
/// </summary>
public class StaffInviteOptions
{
    public const string SectionName = "StaffInvites";

    /// <summary>Release switch (D7). Code default false.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Minimum seconds between two sends of the same invitation (D12).
    /// Placeholder, not a tuned value.
    /// </summary>
    public int ResendCooldownSeconds { get; set; } = 60;

    /// <summary>
    /// Maximum invitation rows one business may hold at once (D12).
    /// Placeholder, not a tuned value.
    /// </summary>
    public int MaxRetainedPerBusiness { get; set; } = 50;

    /// <summary>
    /// Permits per minute per client IP for the anonymous preview and accept
    /// endpoints (D12). Placeholder, not a tuned value.
    /// </summary>
    public int AnonymousPermitLimitPerMinute { get; set; } = 10;

    /// <summary>
    /// Permits per minute per signed-in user for create and resend (D12).
    /// Placeholder, not a tuned value.
    /// </summary>
    public int SendPermitLimitPerMinute { get; set; } = 20;
}
