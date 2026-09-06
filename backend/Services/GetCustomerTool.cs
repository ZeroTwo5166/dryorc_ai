using System.Text.Json;
using Anthropic.Models.Messages;
using backend.Data;
using Microsoft.EntityFrameworkCore;

namespace backend.Services;

/// <summary>
/// Step 23K — the first concrete IDispatchTool implementation registered in
/// this codebase, built to prove the ClaudeReplyService → ToolDispatchService
/// → IDispatchTool wiring works end to end (Step 23I/23J found this path
/// entirely unreachable; nothing before this pass exercised it).
///
/// Deliberately scoped to a local Nivora Customer-record lookup, NOT a live
/// Shopify customer query — the checklist's original "getCustomer" candidate
/// workstream almost certainly meant the latter, but that requires a new
/// Shopify GraphQL query and credential resolution this pass has no reason
/// to build; this tool exists to prove dispatch plumbing, not to be the
/// final production "getCustomer." Flagged explicitly, not silently
/// mislabeled as the complete feature.
///
/// Takes no AI-supplied arguments at all — CustomerId comes entirely from
/// the server-derived ToolDispatchContext (never from the model's input),
/// same trust boundary every existing dispatch tool already operates under.
/// </summary>
public class GetCustomerTool(AppDbContext db) : IReadOnlyDispatchTool
{
    public string Name => "getCustomer";

    public string Description =>
        "Look up the current customer's name and email address on file, if any.";

    public InputSchema InputSchema => new()
    {
        Type = JsonSerializer.SerializeToElement("object"),
        Properties = new Dictionary<string, JsonElement>(),
    };

    public async Task<ToolExecutionResult> ExecuteAsync(
        ToolDispatchContext context, IReadOnlyDictionary<string, JsonElement> input, CancellationToken cancellationToken)
    {
        var customer = await db.Customers
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == context.CustomerId && c.BusinessId == context.BusinessId, cancellationToken);

        if (customer is null || customer.IsAnonymized)
        {
            return ToolExecutionResult.Success("No profile information is on file for this customer.");
        }

        var name = string.IsNullOrWhiteSpace(customer.Name) ? "(not on file)" : customer.Name;
        var email = string.IsNullOrWhiteSpace(customer.Email) ? "(not on file)" : customer.Email;
        return ToolExecutionResult.Success($"Name: {name}. Email: {email}.");
    }
}
