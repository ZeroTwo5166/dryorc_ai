namespace backend.Auth;

/// <summary>The three staff roles per NIVORA_DEVELOPMENT_PLAN_V2.md's Step 4 scope.</summary>
public static class RoleNames
{
    public const string Owner = "Owner";
    public const string Admin = "Admin";
    public const string Agent = "Agent";

    public static readonly string[] All = [Owner, Admin, Agent];
}
