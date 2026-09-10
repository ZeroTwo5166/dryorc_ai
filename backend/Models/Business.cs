using backend.Constants;
using backend.Enums;

namespace backend.Models;



public class Business
{
    public Guid Id { get; set; }
    public required string Name { get; set; }

    public int RetentionDays { get; set; } = RetentionPolicyLimits.DefaultDays;

    public BusinessStatus Status { get; set; } = BusinessStatus.Active;

    /// <summary>
    /// Free-text for now; Step 26 (billing) defines the real tier catalog.
    /// Nullable/string on purpose — avoids a migration every time tiers change
    /// before pricing is locked.
    /// </summary>
    public string? PlanTier { get; set; } = "trial";

    /// <summary>
    /// IANA timezone id (e.g. "Europe/Copenhagen", "Europe/Warsaw"). Drives
    /// day-boundary logic for scheduled reports (Step 24) and SLA reporting.
    /// Required, no default — Nivora targets EU/Danish businesses broadly,
    /// not just Denmark, so silently defaulting to one region's timezone
    /// risks wrong day-boundaries for every other business. Must be set
    /// explicitly during onboarding.
    /// </summary>
    public required string TimeZone { get; set; }

    public string? ContactEmail { get; set; }
    public string? ContactPhone { get; set; }

    /// <summary>
    /// EU VAT/registration number — CVR for Danish businesses, national VAT
    /// number equivalent for other EU businesses. One field covers both.
    /// </summary>
    public string? VatNumber { get; set; }


    public bool ExtendedAuditLoggingEnabled { get; set; } = false;
    public bool CancelOrderRestocksInventory { get; set; } = false;

    /// <summary>
    /// Step 48 — a narrow, single-tool kill switch: whether the AI is
    /// offered the cancelOrder proposal tool at all for this business.
    /// Default false — opt-in, not opt-out. This is explicitly NOT Decision
    /// 8's deferred per-write-tool automation-tier system (auto-approve
    /// under a limit, tiered policy) — it has no relationship to the
    /// human-approval pipeline itself; a business with this true still
    /// requires the full propose→approve/deny→execute flow, unchanged. See
    /// docs/handoffs/step-48-cancelorder-tool-registration-production-safety-scoping.md.
    /// </summary>
    public bool CancelOrderToolEnabled { get; set; } = false;

    /// <summary>
    /// Step refundOrder Phase 3 — Owner Decision 3 (docs/handoffs/step-refundorder-design.md
    /// §14.3): mandatory dual-layer kill switch for refundOrder, deliberately
    /// stricter than CancelOrderToolEnabled's single-layer precedent above.
    /// Layer 1 (exposure): gates whether the AI is offered the refundOrder
    /// proposal tool at all (ClaudeReplyService's disabledToolNames
    /// construction). Layer 2 (execution): RefundApprovedActionExecutor
    /// independently re-checks this same field at execution time, so an
    /// Approval proposed and even staff-approved while this was true is still
    /// refused if the business subsequently set it to false before execution
    /// — human approval does not bypass this check. Default false — opt-in,
    /// not opt-out, matching CancelOrderToolEnabled's own default.
    /// </summary>
    public bool RefundToolEnabled { get; set; } = false;

    /// <summary>
    /// Step getOrder (design §0/Decision 1) — one shared, opt-in kill switch
    /// gating exposure of all four read-only Shopify tools (getOrder,
    /// getProduct, searchProducts, getInventory) together, not four separate
    /// flags. Exposure/control-plane only, mirroring CancelOrderToolEnabled's
    /// own precedent — never a substitute for getOrder's own Level 2/
    /// ownership checks, which remain mandatory inside GetOrderTool.ExecuteAsync
    /// regardless of this flag's value. Default false — opt-in, not opt-out.
    /// Only getOrder is implemented as of this column's introduction; the
    /// other three tools this flag is scoped for remain unbuilt (separate,
    /// later passes per the read-only-tools design's own §16 phasing).
    /// </summary>
    public bool ShopifyReadToolsEnabled { get; set; } = false;


    /// <summary>
    /// Maximum refund amount Nivora may execute autonomously (Mode = Auto)
    /// without human approval, for tags mapped to Auto. Null means no limit
    /// configured — defaults to requiring human approval regardless of tag
    /// mode (safe/conservative default, not "unlimited"). Amount above this
    /// limit triggers Ticket creation with PriorityReason = RefundLimitReached.
    /// </summary>
    public decimal? AutoRefundLimit { get; set; }

    /// <summary>ISO 4217 currency code, e.g. "EUR". Required if
    /// AutoRefundLimit is set.</summary>
    public string? AutoRefundLimitCurrency { get; set; }


    public bool StoreCreditRefundsEnabled { get; set; } = false;


    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

