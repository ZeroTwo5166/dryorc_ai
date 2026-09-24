namespace backend.Constants;

/// <summary>
/// String constants for AiProviderInvocation.ErrorCategory — the failure's
/// cause only, never whether usage is known (that distinction is
/// IsUsagePartial's job) and never raw provider error text (A1 design
/// docs/handoffs/billing-usage-a1-invocation-capture-design-v11.md §3).
/// </summary>
public static class AiErrorCategories
{
    public const string RateLimited = "RateLimited";
    public const string ServerError = "ServerError";
    public const string Timeout = "Timeout";
    public const string Cancelled = "Cancelled";
    public const string InvalidRequest = "InvalidRequest";
    public const string Unknown = "Unknown";
}
