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

