using Microsoft.Extensions.Options;

namespace backend.Services;

/// <summary>
/// The staff-invite release switch (DEC D7; design ED-10). Every place that
/// asks "is staff invite on?" goes through this interface: the
/// StaffInvitesEnabledFilter, the two staff-invite rate-limit policies and
/// the Me response. Tests can replace it per host.
/// </summary>
public interface IStaffInviteSwitch
{
    bool IsEnabled { get; }
}

/// <summary>
/// Production switch: reads StaffInviteOptions.Enabled through
/// IOptionsMonitor, so it always sees the current configuration value.
/// </summary>
public class OptionsStaffInviteSwitch(IOptionsMonitor<StaffInviteOptions> options) : IStaffInviteSwitch
{
    public bool IsEnabled => options.CurrentValue.Enabled;
}
