using System.Text.Json;
using Anthropic.Models.Messages;

namespace backend.Services;

/// <summary>
/// Step 23 Phase 3 — design §1c, capability 1 of 5. Thin wrapper over
/// IAnalyticsQueryService.GetConversationsByDayAsync (extracted from
/// AnalyticsController.cs's original Metric 1 body in Phase 2). Rejects an
/// invalid/missing `range` the same way AnalyticsController's own
/// InvalidRange_ReturnsBadRequest test proves for the HTTP path, by reusing
/// IAnalyticsQueryService.TryParseRange — not a second, drifting validation
/// implementation.
/// </summary>
public class GetConversationsByDayCapability(IAnalyticsQueryService analyticsQueryService) : IAnalystCapability
{
    public string Name => "getConversationsByDay";

    public string Description =>
        "Returns the number of new conversations started per day over the given range, one entry per " +
        "calendar day including days with zero conversations.";

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

        var result = await analyticsQueryService.GetConversationsByDayAsync(window, cancellationToken);
        return ToolExecutionResult.Success(JsonSerializer.Serialize(result));
    }
}
