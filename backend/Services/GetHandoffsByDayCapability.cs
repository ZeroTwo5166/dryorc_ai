using System.Text.Json;
using Anthropic.Models.Messages;

namespace backend.Services;

/// <summary>
/// Step 23 Phase 3 — design §1c, capability 4 of 5. Thin wrapper over
/// IAnalyticsQueryService.GetHandoffsByDayAsync (extracted from
/// AnalyticsController.cs's original Metric 4 body in Phase 2).
/// </summary>
public class GetHandoffsByDayCapability(IAnalyticsQueryService analyticsQueryService) : IAnalystCapability
{
    public string Name => "getHandoffsByDay";

    public string Description =>
        "Returns the number of AI-to-human handoffs per day over the given range, one entry per calendar " +
        "day including days with zero handoffs.";

    public InputSchema InputSchema => new()
    {
        Type = JsonSerializer.SerializeToElement("object"),
        Properties = new Dictionary<string, JsonElement>
        {
            ["range"] = JsonSerializer.SerializeToElement(new
            {
                type = "string",
                @enum = analyticsQueryService.ValidRangeKeys.ToArray(),
                description = "The time window to aggregate over.",
            }),
        },
        Required = ["range"],
    };

    public async Task<ToolExecutionResult> ExecuteAsync(
        Guid businessId, IReadOnlyDictionary<string, JsonElement> input, CancellationToken cancellationToken)
    {
        var rangeValue = input.TryGetValue("range", out var rangeElement) && rangeElement.ValueKind == JsonValueKind.String
            ? rangeElement.GetString()
            : null;

        if (!analyticsQueryService.TryParseRange(rangeValue, out var window))
        {
            return ToolExecutionResult.Error($"range must be one of: {string.Join(", ", analyticsQueryService.ValidRangeKeys)}");
        }

        var result = await analyticsQueryService.GetHandoffsByDayAsync(window, cancellationToken);
        return ToolExecutionResult.Success(JsonSerializer.Serialize(result));
    }
}
