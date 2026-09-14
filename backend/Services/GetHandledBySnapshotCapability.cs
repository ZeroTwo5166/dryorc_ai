using System.Text.Json;
using Anthropic.Models.Messages;

namespace backend.Services;

/// <summary>
/// Step 23 Phase 3 — design §1c, capability 3 of 5. Thin wrapper over
/// IAnalyticsQueryService.GetHandledBySnapshotAsync (extracted from
/// AnalyticsController.cs's original Metric 3 body in Phase 2, itself
/// Step 21 design §9 Decision 1: an all-history current snapshot, no range
/// filter at all).
///
/// This is the one capability whose InputSchema takes no `range` — empty
/// Properties/Required, matching the underlying query's own no-parameter
/// shape — and whose Description must state that limitation prominently
/// (design §1c: "prominent in ... the tool's own Description field
/// content"), not leave it implicit, so Claude's own tool-selection
/// judgment does not attempt to use this as if it were range-scoped or
/// treat it as evidence of a trend (design §1c: a trend question over this
/// specific metric is not a comparison the available data supports, since
/// there is no prior snapshot to compare against — only the current one).
/// </summary>
public class GetHandledBySnapshotCapability(IAnalyticsQueryService analyticsQueryService) : IAnalystCapability
{
    public string Name => "getHandledBySnapshot";

    public string Description =>
        "Returns the current, all-time snapshot of how many conversations are being handled by AI versus " +
        "by human staff. Does not accept a date range and cannot show how this split has changed over " +
        "time — it reflects only right now, across all conversation history, not a trend.";

    public InputSchema InputSchema => new()
    {
        Type = JsonSerializer.SerializeToElement("object"),
        Properties = new Dictionary<string, JsonElement>(),
    };

    public async Task<ToolExecutionResult> ExecuteAsync(
        Guid businessId, IReadOnlyDictionary<string, JsonElement> input, CancellationToken cancellationToken)
    {
        var result = await analyticsQueryService.GetHandledBySnapshotAsync(cancellationToken);
        return ToolExecutionResult.Success(JsonSerializer.Serialize(result));
    }
}
