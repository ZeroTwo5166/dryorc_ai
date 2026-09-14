using System.Text.Json;
using Anthropic.Models.Messages;

namespace backend.Services;

/// <summary>
/// Step 23 Phase 3 — design §1b/§1c. A read-only, no-customer capability for
/// the AI Business Analyst. Deliberately not IDispatchTool/
/// IReadOnlyDispatchTool: ToolDispatchContext.CustomerId is non-nullable,
/// and an Analyst request has no customer at all (design §1b) — a new,
/// narrower interface family is required instead. Name/Description/
/// InputSchema mirror IDispatchTool's shape for consistency; ToolExecutionResult
/// (IDispatchTool.cs) is reused as-is for the same IsError/Content contract
/// the existing dispatch loop's tool_result mapping already proves.
///
/// businessId is passed explicitly by the caller — the eventual Phase 5
/// endpoint, from its own ICurrentTenantAccessor-resolved tenant, never from
/// the client request body or from Claude's own tool-call input (design §6)
/// — for the same trusted-context discipline every IDispatchTool already
/// follows. The 5 concrete capabilities in this phase do not re-apply it as
/// an explicit filter themselves: each delegates entirely to
/// IAnalyticsQueryService, whose own AppDbContext already carries the
/// ambient global tenant query filter for this same request — the identical
/// trust model AnalyticsController itself already uses (Phase 2), not the
/// IgnoreQueryFilters()-plus-explicit-BusinessId pattern GetCustomerTool and
/// the other IDispatchTool implementations use. That pattern exists
/// specifically because ITS caller, ToolDispatchService, serves
/// unauthenticated customer-widget conversations with no reliable ambient
/// tenant context (the same reasoning ConversationModeService's own doc
/// comment gives). The Analyst's eventual caller is instead a staff-
/// authenticated HTTP request — structurally identical to
/// AnalyticsController's own already-verified context — so relying on the
/// same ambient filter here is consistent, not a gap. businessId still
/// appears on every capability's signature, both for interface-shape
/// uniformity across Phase 4's generic dispatch and as the trusted value a
/// future non-AnalyticsQueryService-backed capability would need to filter
/// with directly.
/// </summary>
public interface IAnalystCapability
{
    string Name { get; }
    string Description { get; }
    InputSchema InputSchema { get; }

    Task<ToolExecutionResult> ExecuteAsync(
        Guid businessId, IReadOnlyDictionary<string, JsonElement> input, CancellationToken cancellationToken);
}
