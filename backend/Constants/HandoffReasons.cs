namespace backend.Constants;

public static class HandoffReasons
{
    public const string AiUncertain = "AiUncertain";
    public const string TagModeManualFloor = "TagModeManualFloor";
    public const string RefundLimitReached = "RefundLimitReached";
    public const string CustomerRequestedHuman = "CustomerRequestedHuman";
    public const string StaffManualTakeover = "StaffManualTakeover";
    public const string ResolvedReturnedToAi = "ResolvedReturnedToAi";
    // extend as more handoff triggers are identified
}