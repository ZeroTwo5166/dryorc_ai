using Microsoft.Extensions.Configuration;
using Xunit;

namespace backend.Tests;

/// <summary>
/// Gate for real, billed Voyage API calls in tests. Both conditions
/// must hold — the API key alone is never sufficient:
///   Voyage:EnableLiveIntegrationTests == true (exact, not loose-truthy —
///     ConfigurationBinder.GetValue&lt;bool&gt; only accepts "true"/"false"
///     case-insensitive; anything else, including absent, is false)
///   AND
///   Voyage:ApiKey is present
///
/// Computed once per test process (config doesn't change mid-run). Same
/// shape as LiveClaudeTestGate, with its own flag, so enabling live Claude
/// tests never also enables live Voyage calls (or the reverse).
/// </summary>
public static class LiveVoyageTestGate
{
    public const string SkipReason =
        "Live Voyage integration test skipped — requires both Voyage:EnableLiveIntegrationTests=true " +
        "AND a valid Voyage:ApiKey configured via dotnet user-secrets. Neither condition is met by default.";

    public static readonly bool IsEnabled = ComputeIsEnabled();

    private static bool ComputeIsEnabled()
    {
        var configuration = new ConfigurationBuilder()
            .AddUserSecrets<Program>()
            .Build();

        var flagEnabled = configuration.GetValue<bool>("Voyage:EnableLiveIntegrationTests");
        var hasKey = !string.IsNullOrWhiteSpace(configuration["Voyage:ApiKey"]);

        return flagEnabled && hasKey;
    }
}

/// <summary>
/// A [Fact] that shows as genuinely Skipped (not silently passed, and not
/// attempted-then-failed) whenever LiveVoyageTestGate.IsEnabled is false.
/// Same runtime-conditional skip mechanism as LiveClaudeFactAttribute — see
/// that type's doc comment for why Skip is set in the constructor.
/// </summary>
public sealed class LiveVoyageFactAttribute : FactAttribute
{
    public LiveVoyageFactAttribute()
    {
        if (!LiveVoyageTestGate.IsEnabled)
        {
            Skip = LiveVoyageTestGate.SkipReason;
        }
    }
}
