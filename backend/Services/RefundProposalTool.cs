using System.Text.Json;
using Anthropic.Models.Messages;
using backend.Data;
using backend.Enums;
using backend.Models;
using Microsoft.EntityFrameworkCore;

namespace backend.Services;

/// <summary>
/// Step refundOrder Phase 2 — the second concrete IActionProposalTool,
/// mirroring OrderCancelProposalTool's shape and every one of its
/// established conventions. See docs/handoffs/step-refundorder-design.md
/// §5 for the full domain-contract derivation this implements, and §21 for
/// this pass's own scope boundary.
///
/// Scoped narrowly to proposal creation only, identical in spirit to
/// OrderCancelProposalTool's own Step 23N scope: does NOT implement
/// IApprovedActionExecutor, Level 2 verification, ownership verification,
/// the refundable-quantity check (design §8.3, still unbuilt), or the
/// actual Shopify refundCreate mutation — all of those belong to a future
/// RefundApprovedActionExecutor (Phase 3), strictly after this Approval is
/// Approved. Proposal creation happens entirely before any of that, and
/// never mutates anything external.
///
/// Owner Decision 1 (item/quantity-only input, no arbitrary caller-supplied
/// monetary amount) is enforced structurally, not just by convention: this
/// class has no field, parameter, or code path anywhere that reads or
/// stores a caller-supplied amount/currency value. Owner Decision 2
/// (mandatory proposal -> approval -> execution) is enforced the same way
/// IActionProposalTool enforces it for cancelOrder — this class exposes no
/// ExecuteAsync-shaped member, cannot invoke IRefundService.MarkConfirmedAsync/
/// MarkFailedAsync (it takes no IRefundService dependency at all), and never
/// reads Business.AutoRefundLimit (this class takes no Business/AppDbContext
/// query against that field anywhere).
///
/// Deliberately does NOT create a Refund row or populate Approval.RefundId —
/// mirroring OrderCancelProposalTool's own identical omission of
/// OrderCancellationId at propose time (design §11.2: the execution-intent
/// row is created inside the future executor's own atomic-claim
/// transaction, step 13-equivalent, not here). Creating a Refund row at
/// propose time would risk orphaned Pending rows for every proposal that is
/// later denied or left to expire, and would contradict IActionProposalTool's
/// own "exclusively a durable-record operation" / never-touches-the-
/// execution-intent-table contract that OrderCancelProposalTool already
/// establishes.
/// </summary>
public class RefundProposalTool(AppDbContext db) : IActionProposalTool
{
    private static readonly TimeSpan ApprovalTtl = TimeSpan.FromMinutes(15); // mirrors OrderCancelProposalTool's own Decision 1 precedent

    /// <summary>
    /// A stable, non-stringly-typed reference to this tool's own name — same
    /// role as OrderCancelProposalTool.ToolName (Step 48's own per-business
    /// gate convention), even though no equivalent kill switch exists for
    /// refundOrder yet (deliberately deferred to a later step, mirroring
    /// cancelOrder's own history: Step 23N shipped the proposal tool before
    /// Step 48 added its kill switch).
    /// </summary>
    public const string ToolName = "refundOrder";

    public string Name => ToolName;

    public string Description =>
        "Propose refunding one or more line items from a customer's Shopify order, by quantity. This does not issue " +
        "the refund immediately — it creates a request that requires Owner/Admin staff approval before anything " +
        "happens. This tool never accepts a flat monetary amount — only specific line items and the quantity of each " +
        "to refund.";

    public InputSchema InputSchema => new()
    {
        Type = JsonSerializer.SerializeToElement("object"),
        Properties = new Dictionary<string, JsonElement>
        {
            ["orderId"] = JsonSerializer.SerializeToElement(new
            {
                type = "string",
                description = "The Shopify order identifier the refund applies to, exactly as known from the conversation.",
            }),
            ["items"] = JsonSerializer.SerializeToElement(new
            {
                type = "array",
                description =
                    "The line items and quantities to refund. At least one item is required. There is no flat, " +
                    "order-level refund amount — every refund must be expressed as specific line items and quantities.",
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        externalLineItemId = new { type = "string", description = "The Shopify line item identifier." },
                        quantity = new { type = "integer", description = "How many units of this line item to refund." },
                    },
                    required = new[] { "externalLineItemId", "quantity" },
                },
            }),
            ["refundMethod"] = JsonSerializer.SerializeToElement(new
            {
                type = "string",
                description = "How to refund the customer: OriginalPaymentMethod or StoreCredit.",
            }),
            ["customerReason"] = JsonSerializer.SerializeToElement(new
            {
                type = "string",
                description = "Optional free-text context from the customer about why they want a refund.",
            }),
        },
        Required = ["orderId", "items", "refundMethod"],
    };

    public IdempotencyToolInputs DescribeProposalInputs(IReadOnlyDictionary<string, JsonElement> input)
    {
        var parsed = ParseInput(input);
        var canonicalArguments = ToolArgumentCanonicalizer.CanonicalizeRefundArgs(parsed.Method, parsed.Items);
        return new IdempotencyToolInputs(parsed.ResourceId, canonicalArguments);
    }

    public async Task<ActionProposalResult> ProposeAsync(
        ToolDispatchContext context, IReadOnlyDictionary<string, JsonElement> input, CancellationToken cancellationToken)
    {
        // Identical re-derivation discipline to OrderCancelProposalTool's own
        // ProposeAsync — never trust ToolDispatchContext.CustomerId directly,
        // always re-read the current Conversation row.
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

        (string ResourceId, RefundMethod Method, IReadOnlyList<(string ExternalLineItemId, int Quantity)> Items, string? CustomerReason) parsed;
        try
        {
            parsed = ParseInput(input);
        }
        catch (FormatException)
        {
            // Mirrors OrderCancelProposalTool's own Step 33 discipline:
            // Nivora's backend, not the AI, must independently reject
            // invalid input before anything is canonicalized, hashed, or
            // persisted.
            return new ActionProposalResult(
                ToolExecutionResult.Error("This action's refund method or item list was not recognized or was invalid."),
                null);
        }

        var canonicalArguments = ToolArgumentCanonicalizer.CanonicalizeRefundArgs(parsed.Method, parsed.Items);
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
            DisplaySummary = BuildDisplaySummary(parsed.ResourceId, parsed.Method, parsed.Items),
            Status = ApprovalStatus.Pending,
            CreatedAt = now,
            ExpiresAt = now.Add(ApprovalTtl),
            // RefundId deliberately left null — see class doc comment.
        };

        db.Approvals.Add(approval);
        await db.SaveChangesAsync(cancellationToken);

        return new ActionProposalResult(
            ToolExecutionResult.Success(
                $"Refund request submitted for staff approval (reference {approval.Id}). " +
                "This will not take effect until an Owner or Admin approves it."),
            approval.Id);
    }

    /// <summary>
    /// Proposal-time validation — distinct in kind from Phase 1's
    /// RefundService.RequestRefundAsync (which validates again,
    /// independently, at persistence time — this method's checks are not a
    /// substitute for that one) and from a future Phase 3 executor's
    /// execution-time refundable-quantity check (design §8.3, not possible
    /// here — no Shopify call is made at proposal time). Rejects: a missing/
    /// empty item array (Owner Decision 1 — there is no flat-amount
    /// fallback), a zero/negative or non-integer quantity, a duplicate
    /// ExternalLineItemId within one request, and an unrecognized
    /// refundMethod value — all via the same Enum.TryParse-plus-
    /// Enum.IsDefined / throw-FormatException discipline
    /// OrderCancelProposalTool.ParseInput already establishes for the
    /// identical reason (Step 33).
    /// </summary>
    private static (string ResourceId, RefundMethod Method, IReadOnlyList<(string ExternalLineItemId, int Quantity)> Items, string? CustomerReason) ParseInput(
        IReadOnlyDictionary<string, JsonElement> input)
    {
        var resourceId = input["orderId"].GetString()!;
        var method = ParseRequiredEnum<RefundMethod>(input["refundMethod"].GetString()!, "refundMethod");

        var itemsElement = input["items"];
        if (itemsElement.ValueKind != JsonValueKind.Array || itemsElement.GetArrayLength() == 0)
        {
            throw new FormatException(
                "Refund items must be a non-empty array — there is no flat, order-level refund amount.");
        }

        var items = new List<(string ExternalLineItemId, int Quantity)>();
        var seenLineItems = new HashSet<string>(StringComparer.Ordinal);
        foreach (var itemElement in itemsElement.EnumerateArray())
        {
            if (itemElement.ValueKind != JsonValueKind.Object
                || !itemElement.TryGetProperty("externalLineItemId", out var lineItemIdElement)
                || lineItemIdElement.ValueKind != JsonValueKind.String)
            {
                throw new FormatException("Each refund item must have a string externalLineItemId.");
            }

            var externalLineItemId = lineItemIdElement.GetString();
            if (string.IsNullOrWhiteSpace(externalLineItemId))
            {
                throw new FormatException("Each refund item must have a non-empty externalLineItemId.");
            }

            if (!itemElement.TryGetProperty("quantity", out var quantityElement)
                || quantityElement.ValueKind != JsonValueKind.Number
                || !quantityElement.TryGetInt32(out var quantity)
                || quantity <= 0)
            {
                throw new FormatException($"Refund item '{externalLineItemId}' has a missing or non-positive quantity.");
            }

            if (!seenLineItems.Add(externalLineItemId))
            {
                throw new FormatException($"Refund item '{externalLineItemId}' is listed more than once in the same request.");
            }

            items.Add((externalLineItemId, quantity));
        }

        string? customerReason = input.TryGetValue("customerReason", out var reasonElement)
            && reasonElement.ValueKind != JsonValueKind.Null
            ? reasonElement.GetString()
            : null;

        return (resourceId, method, items, customerReason);
    }

    private static TEnum ParseRequiredEnum<TEnum>(string value, string fieldName) where TEnum : struct, Enum
    {
        if (!Enum.TryParse<TEnum>(value, out var parsed) || !Enum.IsDefined(parsed))
        {
            throw new FormatException($"Unrecognized {fieldName} value '{value}'.");
        }

        return parsed;
    }

    /// <summary>
    /// Deliberately minimal, mirroring OrderCancelProposalTool.BuildDisplaySummary's
    /// own explicit "a display representation is required, but its exact
    /// wording was never specified and is not treated as authoritative/
    /// parseable by anything" convention. Built entirely from server-derived/
    /// canonical fields, never AI-authored prose.
    /// </summary>
    private static string BuildDisplaySummary(
        string resourceId, RefundMethod method, IReadOnlyList<(string ExternalLineItemId, int Quantity)> items)
    {
        var methodText = method switch
        {
            RefundMethod.OriginalPaymentMethod => "to original payment method",
            RefundMethod.StoreCredit => "as store credit",
            _ => "via an unrecognized method",
        };
        var itemsText = string.Join(", ", items.Select(i => $"{i.Quantity}x {i.ExternalLineItemId}"));
        return $"Refund order {resourceId} ({itemsText}), {methodText}.";
    }
}
