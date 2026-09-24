namespace backend.Constants;

/// <summary>
/// The locked Anthropic model for AI provider invocations (A1 design
/// docs/handoffs/billing-usage-a1-invocation-capture-design-v11.md §6,
/// Decision 5 — locked: compile-time constants). A separate class/file from
/// VoyageModels, not a merged constants file, per the same Decision 5.
/// Configuration (a runtime-settable value) was deliberately rejected: a
/// config-driven model value would allow a silent runtime model swap —
/// changing cost, latency, and tool-calling behavior — without a code
/// review or test run to catch a regression.
/// </summary>
public static class AnthropicModels
{
    public const string Default = "claude-sonnet-5";
}
