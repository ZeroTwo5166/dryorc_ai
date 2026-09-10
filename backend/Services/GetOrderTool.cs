using System.Text;
using System.Text.Json;
using Anthropic.Models.Messages;
using backend.Constants;
using backend.Data;
using backend.Enums;
using Microsoft.EntityFrameworkCore;

namespace backend.Services;

/// <summary>
/// Step getOrder — Phase A of the read-only Shopify tool surface. See
/// docs/handoffs/step-getorder-implementation-design.md for the full design
/// (as amended by its independent-review corrections and the post-research
/// addendum) and docs/handoffs/step-getorder-shopify-research.md for the
/// Shopify GraphQL field/argument research this implementation is built on.
///
/// Direct IReadOnlyDispatchTool (design §0/Decision 2) — no Approval row, no
/// human approve/deny step. Every check design §0's locked sequence requires
/// (Level 2 verification, active-integration, identifier resolution to a
/// canonical GID, ownership authorization) is enforced inline and
/// synchronously inside ExecuteAsync, unconditionally, regardless of
/// Business.ShopifyReadToolsEnabled — that flag is an exposure/control-plane
/// gate only (checked one layer up, in ClaudeReplyService, before this class
/// is ever reached), never a substitute for these checks.
///
/// Does NOT call IShopifyOrderLookupService.LookupOrderOwnerAsync — that
/// method belongs to OrderCancelApprovedActionExecutor and is deliberately
/// left untouched (design §1.4/§2.1a). Ownership here goes through the
/// separate LookupOrderOwnerForReadToolAsync method instead, which can
/// distinguish a 401/403 Shopify response and raise ShopifyAuthFailureException
/// for it.
/// </summary>
public class GetOrderTool(
    AppDbContext db,
    ILevel2VerificationChecker level2Checker,
    IShopifyOrderLookupService orderLookupService,
    IOrderOwnershipAuthorizationService ownershipService,
    IAuditLogService auditLogService) : IReadOnlyDispatchTool
{
    /// <summary>
    /// Step getOrder — a stable, non-stringly-typed reference to this tool's
    /// own name, mirroring OrderCancelProposalTool.ToolName's own precedent,
    /// for ClaudeReplyService's Business.ShopifyReadToolsEnabled gate to
    /// name without duplicating the literal "getOrder" in a second file.
    /// </summary>
    public const string ToolName = "getOrder";

    private const string GidOrderPrefix = "gid://shopify/Order/";

    public string Name => ToolName;

    public string Description =>
        "Look up the current customer's own Shopify order — status, line items, and tracking. " +
        "Accepts either the order name/number as the customer knows it (e.g. \"#1001\") or a Shopify order GID.";

    public InputSchema InputSchema => new()
    {
        Type = JsonSerializer.SerializeToElement("object"),
        Properties = new Dictionary<string, JsonElement>
        {
            ["orderIdentifier"] = JsonSerializer.SerializeToElement(new
            {
                type = "string",
                description = "The order identifier, exactly as known from the conversation (e.g. \"#1001\").",
            }),
        },
        Required = ["orderIdentifier"],
    };

    public async Task<ToolExecutionResult> ExecuteAsync(
        ToolDispatchContext context, IReadOnlyDictionary<string, JsonElement> input, CancellationToken cancellationToken)
    {
        // 1. Parse input — required string, raw AI-supplied text, no
        // client-side normalization (design §1.10/§2.1's own convention).
        if (!input.TryGetValue("orderIdentifier", out var identifierElement)
            || identifierElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(identifierElement.GetString()))
        {
            return ToolExecutionResult.Error("An order identifier is required to look up an order.");
        }

        var orderIdentifier = identifierElement.GetString()!;

        // 2. Level 2 verification (design §3 step 2). ConversationId is
        // Guid? — a null conversation cannot be Level 2 verified (no OTP
        // flow has a conversation to bind to); treat null identically to
        // "not verified," never call IsVerifiedAsync with a synthesized
        // value.
        if (context.ConversationId is not { } conversationId
            || !await level2Checker.IsVerifiedAsync(context.BusinessId, conversationId, cancellationToken))
        {
            return RefuseNotVerified();
        }

        // 3. Active-integration pre-check (mirrors OrderCancelApprovedActionExecutor's
        // step-48 precedent) — a clean refusal, not an uncaught exception,
        // when no active Shopify integration exists.
        var hasActiveIntegration = await db.Integrations.IgnoreQueryFilters()
            .AnyAsync(i => i.BusinessId == context.BusinessId
                && i.Platform == ShopifyIntegrationOAuthProvider.PlatformName && i.IsActive, cancellationToken);
        if (!hasActiveIntegration)
        {
            return RefuseInfraUnavailable();
        }

        try
        {
            // 4. Identifier resolution (design §2.1/§2.3). A GID-shaped
            // input skips name resolution entirely — no Shopify call needed
            // to make that determination, it's a pure string check.
            string orderGid;
            if (orderIdentifier.StartsWith(GidOrderPrefix, StringComparison.Ordinal))
            {
                orderGid = orderIdentifier;
            }
            else
            {
                var resolution = await orderLookupService.ResolveOrderNameAsync(context.BusinessId, orderIdentifier, cancellationToken);
                switch (resolution.Outcome)
                {
                    case OrderNameResolutionOutcome.NotFound:
                        return RefuseNotFound();
                    case OrderNameResolutionOutcome.Ambiguous:
                        return RefuseAmbiguous();
                }

                orderGid = resolution.ResolvedOrderGid!;
            }

            // 5. Ownership (mirrors executor step 10) — LookupOrderOwnerForReadToolAsync,
            // NOT LookupOrderOwnerAsync (design §1.4/§2.1a).
            var ownership = await orderLookupService.LookupOrderOwnerForReadToolAsync(context.BusinessId, orderGid, cancellationToken);
            var ownershipResult = await ownershipService.AuthorizeAsync(context.BusinessId, context.CustomerId, ownership, cancellationToken);
            if (!ownershipResult.IsAuthorized)
            {
                await AuditAsync(context, orderGid, $"OwnershipVerificationFailed:{ownershipResult.Reason}", cancellationToken);
                return RefuseNotFound(); // Group B — same text as NotFound, deliberately (design §4).
            }

            // 6. Order-detail read (design §2.2) — "not found" here is a
            // genuine anomaly (the order existed moments ago at step 5), so
            // this call is allowed to throw uncaught like every other
            // infra-failure path, caught by ToolDispatchService's generic
            // backstop.
            var detail = await orderLookupService.GetOrderDetailAsync(context.BusinessId, orderGid, cancellationToken);

            // 7. Audit success, then format the result.
            await AuditAsync(context, orderGid, "Success", cancellationToken);
            return ToolExecutionResult.Success(FormatOrderDetail(detail));
        }
        catch (ShopifyAuthFailureException)
        {
            // Integration.ReauthorizationRequiredAt is already written by
            // whichever of the three IShopifyOrderLookupService methods
            // raised this — see design §1.7/§3's classification-placement
            // note. Routed here to Group D, never ToolDispatchService's
            // fully generic catch, which has no reauthorization-writing
            // hook and would otherwise return the generic
            // "internal error occurred" text instead of this
            // reauthorization-safe one.
            return RefuseInfraUnavailable();
        }
    }

    // ── Refusal messaging (design §4) — four deliberate groups ──

    /// <summary>Group A — identity not yet established. DESIGN RECOMMENDATION
    /// text (design §4) — no existing verification-flow copy was found
    /// anywhere in this codebase to reuse (a negative finding, not a citation
    /// this pass failed to locate).</summary>
    private static ToolExecutionResult RefuseNotVerified() => ToolExecutionResult.Error(
        "Before I can share order details, I'll need to verify it's you — could you confirm the email or " +
        "phone number on the order so I can send a verification code?");

    /// <summary>Group B — "no such order for you," deliberately identical text
    /// for both order-not-found and ownership-authorization-failed, to
    /// prevent order-number enumeration (design §4).</summary>
    private static ToolExecutionResult RefuseNotFound() => ToolExecutionResult.Error(
        "I can't find an order matching that for your account.");

    /// <summary>Group C — ambiguous, kept separate from Group B: a pure
    /// Shopify-side "your search matched more than one order" outcome, not a
    /// tenant/ownership boundary (design §4).</summary>
    private static ToolExecutionResult RefuseAmbiguous() => ToolExecutionResult.Error(
        "I found more than one order matching that — could you share the exact order number, like #1001?");

    /// <summary>Group D — Shopify unreachable / auth failure (no active
    /// integration, 401/403, token-decrypt failure, non-2xx, malformed JSON,
    /// GraphQL errors, timeout). Never mentions "401," "403," "token," or
    /// "reconnect" — matches the established reauthorization-copy discipline
    /// elsewhere in this codebase (design §4).</summary>
    private static ToolExecutionResult RefuseInfraUnavailable() => ToolExecutionResult.Error(
        "I'm having trouble reaching our order system right now — please try again in a moment.");

    /// <summary>
    /// Design §5 — logged on exactly two paths (success, ownership-refusal),
    /// never on the ordinary unverified/not-found/ambiguous refusals. A
    /// resolved orderGid always exists by the time this is called on either
    /// path (design §3's sequence: resolution failures return before this
    /// point). newValue naming matches
    /// OrderCancelApprovedActionExecutor.WriteUnauthorizedExecutionBlockedAsync's
    /// own ownership-failure marker exactly, for consistency between the two
    /// sibling audit trails.
    /// </summary>
    private async Task AuditAsync(ToolDispatchContext context, string orderGid, string outcomeMarker, CancellationToken cancellationToken)
    {
        await auditLogService.LogAsync(
            context.BusinessId, AuditEntityTypes.Order, orderGid,
            AuditEventTypes.OrderLookupPerformed, ActorType.Ai,
            conversationId: context.ConversationId, newValue: outcomeMarker,
            cancellationToken: cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static string FormatOrderDetail(OrderDetailResult detail)
    {
        var sb = new StringBuilder();
        sb.Append("Order ").Append(detail.Name).Append(':');
        sb.Append(" Financial status: ").Append(detail.FinancialStatus ?? "(unknown)").Append('.');
        sb.Append(" Fulfillment status: ").Append(detail.FulfillmentStatus ?? "(unknown)").Append('.');
        sb.Append(detail.CancelledAt is { } cancelledAt
            ? $" Cancelled at {cancelledAt:u}."
            : " Not cancelled.");

        sb.Append(" Line items: ");
        sb.Append(detail.LineItems.Count == 0
            ? "none."
            : string.Join(", ", detail.LineItems.Select(li =>
                $"{li.Title} x{li.Quantity}{(li.Price is null ? "" : $" (${li.Price} each)")}")) + '.');

        sb.Append(" Tracking: ");
        sb.Append(detail.Tracking.Count == 0
            ? "no tracking information available."
            : string.Join(", ", detail.Tracking.Select(t =>
                t.Number is null && t.Url is null
                    ? "(no details)"
                    : $"{t.Number ?? "(no number)"}{(t.Url is null ? "" : $" — {t.Url}")}")) + '.');

        return sb.ToString();
    }
}
