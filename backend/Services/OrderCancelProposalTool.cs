using System.Text.Json;
using Anthropic.Models.Messages;
using backend.Data;
using backend.Enums;
using backend.Models;
using Microsoft.EntityFrameworkCore;

namespace backend.Services;

/// <summary>
/// Step 23N — the first concrete IActionProposalTool: proposal creation for
/// cancelOrder. See docs/handoffs/step-22z-approval-proposal-approval-lifecycle-design.md
/// §7 for the contract this implements, and
/// docs/handoffs/step-23n-ordercancel-proposal-tool-implementation.md for the
/// full derivation of every field below, including which choices are
/// design-locked vs. this pass's own engineering decisions.
///
/// Scoped narrowly to proposal creation only, per this pass's explicit
/// authorization: does NOT implement IApprovedActionExecutor, Level 2
/// verification, ownership verification, the ground-truth already-cancelled
/// check, or the actual Shopify orderCancel mutation — all of those belong
/// to Execute, strictly after this Approval is Approved (22Z-B's established
/// ordering: customer re-derivation → Level 2 → ownership → hash recompute →
/// atomic claim → ground-truth check → mutation). Proposal creation happens
/// entirely before any of that, and never mutates anything external.
///
/// Proposal-time deduplication against existing active proposals (22Z §11)
/// is NOT implemented here — this pass's own task list omits it, and this
/// pass does not invent it. Flagged as a deliberate, narrow omission, not an
/// oversight — a future pass may add it without changing this tool's own
/// contract.
/// </summary>
public class OrderCancelProposalTool(AppDbContext db) : IActionProposalTool
{
    private static readonly TimeSpan ApprovalTtl = TimeSpan.FromMinutes(15); // Decision 1, ADOPTED

    /// <summary>
    /// Step 48 — a stable, non-stringly-typed reference to this tool's own
    /// name, for ClaudeReplyService's per-business gate (Business.CancelOrderToolEnabled)
    /// to name without duplicating the literal "cancelOrder" in a second file.
    /// </summary>
    public const string ToolName = "cancelOrder";

    public string Name => ToolName;

    public string Description =>
        "Propose cancelling a customer's Shopify order. This does not cancel the order immediately — " +
        "it creates a request that requires Owner/Admin staff approval before anything happens.";

    public InputSchema InputSchema => new()
    {
        Type = JsonSerializer.SerializeToElement("object"),
        Properties = new Dictionary<string, JsonElement>
        {
            ["orderId"] = JsonSerializer.SerializeToElement(new
            {
                type = "string",
                description = "The Shopify order identifier to cancel, exactly as known from the conversation.",
            }),
            ["restock"] = JsonSerializer.SerializeToElement(new
            {
                type = "boolean",
                description = "Whether to restock the cancelled items' inventory.",
            }),
            ["reason"] = JsonSerializer.SerializeToElement(new
            {
                type = "string",
                description = "The cancellation reason: Customer, Payment, Fraud, Inventory, StaffError, or Other.",
            }),
            ["refundMethod"] = JsonSerializer.SerializeToElement(new
            {
                type = "string",
                description = "How to refund the customer: OriginalPaymentMethod or StoreCredit. Omit entirely for no refund.",
            }),
            ["customerReason"] = JsonSerializer.SerializeToElement(new
            {
                type = "string",
                description = "Optional free-text context from the customer about why they want to cancel.",
            }),
        },
        Required = ["orderId", "reason"],
    };

    public IdempotencyToolInputs DescribeProposalInputs(IReadOnlyDictionary<string, JsonElement> input)
    {
        var parsed = ParseInput(input);
        var canonicalArguments = ToolArgumentCanonicalizer.CanonicalizeOrderCancellationArgs(parsed.Restock, parsed.Method, parsed.Reason);
        return new IdempotencyToolInputs(parsed.ResourceId, canonicalArguments);
    }

    public async Task<ActionProposalResult> ProposeAsync(
        ToolDispatchContext context, IReadOnlyDictionary<string, JsonElement> input, CancellationToken cancellationToken)
    {
        // 22A §13 / 22Z §8: a write proposal requires a real ConversationId
        // and a resolved, non-null CustomerId — checked fresh against the
        // current Conversation row, not merely trusted from
        // ToolDispatchContext's own (non-nullable-typed, but not
        // independently re-verified here) CustomerId field. See the Step
        // 23N handoff for why this pass re-derives rather than trusts the
        // context value directly.
        if (context.ConversationId is not Guid conversationId)
        {
            return new ActionProposalResult(
                ToolExecutionResult.Error("This action requires an active conversation."), null);
        }

        var conversation = await db.Conversations
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == conversationId && c.BusinessId == context.BusinessId, cancellationToken);

        if (conversation?.CustomerId is not Guid customerId)
        {
            return new ActionProposalResult(
                ToolExecutionResult.Error(
                    "This action requires a verified customer identity, which is not yet available on this conversation."),
                null);
        }

        (string ResourceId, bool Restock, RefundMethod? Method, OrderCancelReason Reason, string? CustomerReason) parsed;
        try
        {
            parsed = ParseInput(input);
        }
        catch (FormatException)
        {
            // Step 33 — Principle 1: Nivora's backend, not the AI, must
            // independently reject invalid input before anything is
            // canonicalized, hashed, or persisted. ParseInput throws
            // FormatException for an unrecognized reason/refundMethod value
            // (including an out-of-range numeric string an Enum.TryParse
            // alone would otherwise silently accept — see
            // docs/handoffs/step-33-propose-side-enum-isdefined-fix.md).
            return new ActionProposalResult(
                ToolExecutionResult.Error("This action's cancellation reason or refund method was not recognized."),
                null);
        }

        var canonicalArguments = ToolArgumentCanonicalizer.CanonicalizeOrderCancellationArgs(parsed.Restock, parsed.Method, parsed.Reason);
        var actionHash = ActionHasher.Derive(
            context.BusinessId, conversationId, customerId, Name, parsed.ResourceId, canonicalArguments);

        var now = DateTimeOffset.UtcNow;
        var approval = new Approval
        {
            Id = Guid.NewGuid(),
            BusinessId = context.BusinessId,
            ConversationId = conversationId,
            CustomerId = customerId,
            ToolName = Name,
            ResourceId = parsed.ResourceId,
            CanonicalArguments = canonicalArguments,
            ActionHash = actionHash,
            DisplaySummary = BuildDisplaySummary(parsed.ResourceId, parsed.Restock, parsed.Method, parsed.Reason),
            Status = ApprovalStatus.Pending,
            CreatedAt = now,
            ExpiresAt = now.Add(ApprovalTtl),
        };

        db.Approvals.Add(approval);
        await db.SaveChangesAsync(cancellationToken);

        return new ActionProposalResult(
            ToolExecutionResult.Success(
                $"Cancellation request submitted for staff approval (reference {approval.Id}). " +
                "This will not take effect until an Owner or Admin approves it."),
            approval.Id);
    }

    /// <summary>
    /// GAP/engineering decision, not design-locked — see the Step 23N
    /// handoff §2. ResourceId is the AI-supplied orderId string, passed
    /// through exactly as given: no lookup, no normalization. This directly
    /// inherits the already-open ExternalOrderId normalization question
    /// (22H §10/§19, restated 22Z §9), which this pass does not resolve —
    /// consistent with ActionHasher's own established convention of never
    /// inventing a normalization rule, and required by this pass's explicit
    /// prohibition on live Shopify calls (no order-lookup data source exists
    /// locally to validate or resolve orderId against — confirmed no Order
    /// model exists anywhere in this repository).
    /// </summary>
    private static (string ResourceId, bool Restock, RefundMethod? Method, OrderCancelReason Reason, string? CustomerReason) ParseInput(
        IReadOnlyDictionary<string, JsonElement> input)
    {
        var resourceId = input["orderId"].GetString()!;
        var restock = input.TryGetValue("restock", out var restockElement) && restockElement.GetBoolean();
        var reason = ParseRequiredEnum<OrderCancelReason>(input["reason"].GetString()!, "reason");

        RefundMethod? method = input.TryGetValue("refundMethod", out var methodElement)
            && methodElement.ValueKind != JsonValueKind.Null
            ? ParseRequiredEnum<RefundMethod>(methodElement.GetString()!, "refundMethod")
            : null;

        string? customerReason = input.TryGetValue("customerReason", out var reasonElement)
            && reasonElement.ValueKind != JsonValueKind.Null
            ? reasonElement.GetString()
            : null;

        return (resourceId, restock, method, reason, customerReason);
    }

    /// <summary>
    /// Step 33 — the same Enum.TryParse-plus-Enum.IsDefined discipline
    /// Step 31/32 already established for the decode side
    /// (ToolArgumentCanonicalizer.DecodeOrderCancellationArgs), applied
    /// here to the AI's own raw tool-call input. Enum.TryParse alone (and
    /// Enum.Parse, which this replaces) silently accepts any numeric
    /// string, including one with no corresponding member at all (e.g.
    /// "99"), converting it directly to an undefined enum value rather than
    /// failing — the exact gap docs/handoffs/step-31-decode-enum-isdefined-fix.md
    /// and docs/handoffs/step-32-decode-enum-isdefined-fix-independent-verification.md
    /// independently confirmed is more directly reachable here than on the
    /// decode side, since this parses the AI's own untrusted input
    /// directly, not an already-canonicalized, already-hashed internal
    /// string. Throws FormatException, caught by ProposeAsync's own
    /// try/catch and converted into its established ActionProposalResult
    /// refusal shape — never silently canonicalized, hashed, or persisted.
    /// </summary>
    private static TEnum ParseRequiredEnum<TEnum>(string value, string fieldName) where TEnum : struct, Enum
    {
        if (!Enum.TryParse<TEnum>(value, out var parsed) || !Enum.IsDefined(parsed))
        {
            throw new FormatException($"Unrecognized {fieldName} value '{value}'.");
        }

        return parsed;
    }

    /// <summary>
    /// Deliberately minimal, per this pass's explicit instruction not to
    /// over-engineer the format (22B §2's own table: a display representation
    /// is required, but its exact wording was never specified and is not
    /// treated as authoritative/parseable by anything). Built entirely from
    /// server-derived/canonical fields, never AI-authored prose (22B §2).
    /// </summary>
    private static string BuildDisplaySummary(string resourceId, bool restock, RefundMethod? method, OrderCancelReason reason)
    {
        var refundText = method switch
        {
            null => "no refund",
            RefundMethod.OriginalPaymentMethod => "refund to original payment method",
            RefundMethod.StoreCredit => "refund as store credit",
            _ => "unknown refund method",
        };
        var restockText = restock ? "restocking inventory" : "not restocking inventory";
        return $"Cancel order {resourceId} ({reason}, {restockText}, {refundText}).";
    }
}
