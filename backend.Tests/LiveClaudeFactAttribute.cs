using Microsoft.Extensions.Configuration;
using Xunit;

namespace backend.Tests;

/// <summary>
/// Gate for real, billed Anthropic API calls in tests. Both conditions
/// must hold — the API key alone is never sufficient:
///   Anthropic:EnableLiveIntegrationTests == true (exact, not loose-truthy —
///     ConfigurationBinder.GetValue&lt;bool&gt; only accepts "true"/"false"
///     case-insensitive; anything else, including absent, is false)
///   AND
///   Anthropic:ApiKey is present
///
/// Computed once per test process (config doesn't change mid-run).
/// </summary>
public static class LiveClaudeTestGate
{
    public const string SkipReason =
        "Live Claude integration test skipped — requires both Anthropic:EnableLiveIntegrationTests=true " +
        "AND a valid Anthropic:ApiKey configured via dotnet user-secrets. Neither condition is met by default.";

    public static readonly bool IsEnabled = ComputeIsEnabled();

    private static bool ComputeIsEnabled()
    {
        var configuration = new ConfigurationBuilder()
            .AddUserSecrets<Program>()
            .Build();

        var flagEnabled = configuration.GetValue<bool>("Anthropic:EnableLiveIntegrationTests");
        var hasKey = !string.IsNullOrWhiteSpace(configuration["Anthropic:ApiKey"]);

        return flagEnabled && hasKey;
    }
}

/// <summary>
/// A [Fact] that shows as genuinely Skipped (not silently passed, and not
/// attempted-then-failed) whenever LiveClaudeTestGate.IsEnabled is false.
/// FactAttribute.Skip is a real, settable, virtual property in xunit 2.9.3
/// (confirmed against the installed assembly, not assumed) — setting it in
/// this subtype's constructor, which runs at test-discovery time, is the
/// standard xunit v2 mechanism for a runtime-conditional skip. xunit v2 has
/// no Assert.Skip (that's a v3-only API) and Skip can't be a runtime
/// expression on a plain [Fact(Skip = ...)] usage, since attribute
/// arguments must be compile-time constants — a custom attribute
/// constructor is the correct escape hatch.
/// </summary>
public sealed class LiveClaudeFactAttribute : FactAttribute
{
    public LiveClaudeFactAttribute()
    {
        if (!LiveClaudeTestGate.IsEnabled)
        {
            Skip = LiveClaudeTestGate.SkipReason;
        }
    }
}
