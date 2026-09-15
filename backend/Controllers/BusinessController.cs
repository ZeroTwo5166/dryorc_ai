using System.Security.Claims;
using System.Text.Json;
using backend.Auth;
using backend.Constants;
using backend.Data;
using backend.Enums;
using backend.Interfaces;
using backend.Models;
using backend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace backend.Controllers;

public record SetCancelOrderToolEnabledRequest(bool Enabled);

public record SetCancelOrderToolEnabledResponse(bool CancelOrderToolEnabled);

public record SetAiBusinessAnalystEnabledRequest(bool Enabled);

public record SetAiBusinessAnalystEnabledResponse(bool AiBusinessAnalystEnabled);

public record SetScheduledReportsEnabledRequest(bool Enabled);

public record SetScheduledReportsEnabledResponse(bool ScheduledReportsEnabled);

public record SetConversationAutoCloseEnabledRequest(bool Enabled);

public record SetConversationAutoCloseEnabledResponse(bool ConversationAutoCloseEnabled);

public record SetConversationInactivityCloseDaysRequest(int Days);

public record SetConversationInactivityCloseDaysResponse(int ConversationInactivityCloseDays);

/// <summary>
/// Step 49b — Business-level settings. First real caller of AuditEntityTypes.Business.
/// Reads: AnyStaff (matches MeResponse, which already exposes CancelOrderToolEnabled
/// in full to any authenticated staff member). Writes: OwnerOnly — same tier
/// TagModeMappingsController/IntegrationsController already established for a
/// comparable reason ("governs whether the AI can act autonomously"/"grants access
/// to an external system"), applied here by analogy since CancelOrderToolEnabled is
/// definitionally the same category (whether the AI can autonomously propose a
/// real-world write action at all). See
/// docs/handoffs/step-49b-cancelordertoolenabled-backend-contract-dashboard-toggle-design.md
/// §4 — this is an engineering determination supported by precedent, not a fact the
/// repository states about this specific setting.
///
/// Business resolution is exclusively via ICurrentTenantAccessor.BusinessId — never
/// a client-supplied value. Unlike every other tenant-scoped entity in this
/// codebase, Business is the tenant root itself and has no ITenantOwned global
/// query filter to fall back on, so this is the endpoint's only defense layer
/// (design doc §9) — the same single-layer pattern AuthController.Me() already
/// relies on.
/// </summary>
[ApiController]
[Route("api/business")]
[Authorize(Policy = NivoraPolicies.AnyStaff)]
public class BusinessController(
    AppDbContext db,
    ICurrentTenantAccessor tenantAccessor,
    IAuditLogService auditLogService) : ControllerBase
{
    private Guid CurrentUserId
    {
        get
        {
            var raw = User.FindFirstValue(ClaimTypes.NameIdentifier);
            return Guid.TryParse(raw, out var id)
                ? id
                : throw new InvalidOperationException("Authenticated but no parsable user id claim — unreachable behind [Authorize].");
        }
    }

    /// <summary>
    /// Step 49b — the locked Shopify precondition (owner decision, step-49
    /// addendum): rejecting Enabled = true while the business has no active
    /// Shopify integration is enforced here, at write time, based purely on
    /// Integration.IsActive — never token health, never a live Shopify call.
    /// The precondition check runs even when the stored value is already
    /// true (i.e. before the idempotency short-circuit below) — a literal
    /// reading of "reject any attempt to set ... = true ... unless active,"
    /// not merely "reject any attempt that would change the value." Disabling
    /// is always allowed regardless of Shopify state, and never requires this
    /// check. See design doc §5 for the full reasoning.
    /// </summary>
    [HttpPut("cancel-order-tool")]
    [Authorize(Policy = NivoraPolicies.OwnerOnly)]
    public async Task<IActionResult> SetCancelOrderToolEnabled([FromBody] SetCancelOrderToolEnabledRequest request)
    {
        var businessId = tenantAccessor.BusinessId
            ?? throw new InvalidOperationException("Authenticated but no tenant resolved — unreachable behind [Authorize].");

        var business = await db.Businesses.FirstOrDefaultAsync(b => b.Id == businessId);
        if (business is null)
        {
            return Unauthorized();
        }

        if (request.Enabled)
        {
            var hasActiveShopifyIntegration = await db.Integrations
                .AnyAsync(i => i.Platform == ShopifyIntegrationOAuthProvider.PlatformName && i.IsActive);
            if (!hasActiveShopifyIntegration)
            {
                return Conflict("Cannot enable — no active Shopify integration exists for this business.");
            }
        }

        // Idempotent no-op — matching CustomersController.SetExtendedLogging's
        // own convention of only auditing real transitions. Deliberately after
        // the precondition check above, not instead of it (design doc §5).
        if (business.CancelOrderToolEnabled == request.Enabled)
        {
            return Ok(new SetCancelOrderToolEnabledResponse(business.CancelOrderToolEnabled));
        }

        var oldValue = SerializeAuditValue(business);
        business.CancelOrderToolEnabled = request.Enabled;
        business.UpdatedAt = DateTimeOffset.UtcNow;

        // No AuditLog write on the 409 precondition-rejection path above —
        // matching Step 48's own ExecutionRefusalReason.ShopifyIntegrationUnavailable
        // refusal, which writes no audit entry for the identical reason: this is
        // a business-precondition check that failed, not an unauthorized-action
        // attempt (design doc §10).
        await auditLogService.LogAsync(
            businessId,
            AuditEntityTypes.Business,
            businessId.ToString(),
            request.Enabled ? AuditEventTypes.BusinessCancelOrderToolEnabled : AuditEventTypes.BusinessCancelOrderToolDisabled,
            ActorType.NivoraBusinessStaff,
            changedByUserId: CurrentUserId,
            oldValue: oldValue,
            newValue: SerializeAuditValue(business));

        await db.SaveChangesAsync();

        return Ok(new SetCancelOrderToolEnabledResponse(business.CancelOrderToolEnabled));
    }

    /// <summary>
    /// Step 23 Phase 1 (design §4) — kill switch for the AI Business Analyst
    /// feature. Modeled directly on SetCancelOrderToolEnabled above: OwnerOnly
    /// (stricter than the AnyStaff read path Phase 5's Analyst endpoint will
    /// use), idempotent (no write/audit on a same-value request), audits every
    /// real transition. Unlike SetCancelOrderToolEnabled, there is no
    /// precondition to check here — the Analyst has no external-integration
    /// dependency comparable to Shopify.
    /// </summary>
    [HttpPut("ai-business-analyst")]
    [Authorize(Policy = NivoraPolicies.OwnerOnly)]
    public async Task<IActionResult> SetAiBusinessAnalystEnabled([FromBody] SetAiBusinessAnalystEnabledRequest request)
    {
        var businessId = tenantAccessor.BusinessId
            ?? throw new InvalidOperationException("Authenticated but no tenant resolved — unreachable behind [Authorize].");

        var business = await db.Businesses.FirstOrDefaultAsync(b => b.Id == businessId);
        if (business is null)
        {
            return Unauthorized();
        }

        // Idempotent no-op — matching SetCancelOrderToolEnabled's own
        // convention of only auditing real transitions.
        if (business.AiBusinessAnalystEnabled == request.Enabled)
        {
            return Ok(new SetAiBusinessAnalystEnabledResponse(business.AiBusinessAnalystEnabled));
        }

        var oldValue = SerializeAiBusinessAnalystAuditValue(business);
        business.AiBusinessAnalystEnabled = request.Enabled;
        business.UpdatedAt = DateTimeOffset.UtcNow;

        await auditLogService.LogAsync(
            businessId,
            AuditEntityTypes.Business,
            businessId.ToString(),
            request.Enabled ? AuditEventTypes.BusinessAiBusinessAnalystEnabled : AuditEventTypes.BusinessAiBusinessAnalystDisabled,
            ActorType.NivoraBusinessStaff,
            changedByUserId: CurrentUserId,
            oldValue: oldValue,
            newValue: SerializeAiBusinessAnalystAuditValue(business));

        await db.SaveChangesAsync();

        return Ok(new SetAiBusinessAnalystEnabledResponse(business.AiBusinessAnalystEnabled));
    }

    /// <summary>
    /// Step 24 Phase 1 (design §20; plan §2 Phase 1) — kill switch for the
    /// Scheduled AI Reports feature. Modeled directly on
    /// SetAiBusinessAnalystEnabled above, mechanically: OwnerOnly, idempotent
    /// (no write/audit on a same-value request), audits every real
    /// transition. Like AiBusinessAnalystEnabled and unlike
    /// CancelOrderToolEnabled, there is no precondition to check here — this
    /// flag has no external-integration dependency comparable to Shopify.
    /// This phase creates only the flag and this toggle — no scheduler, no
    /// execution logic, no background-service DI registration exists yet;
    /// per the locked opt-in decision, existing businesses stay disabled
    /// (this endpoint is the only way the flag will ever become true).
    /// </summary>
    [HttpPut("scheduled-reports")]
    [Authorize(Policy = NivoraPolicies.OwnerOnly)]
    public async Task<IActionResult> SetScheduledReportsEnabled([FromBody] SetScheduledReportsEnabledRequest request)
    {
        var businessId = tenantAccessor.BusinessId
            ?? throw new InvalidOperationException("Authenticated but no tenant resolved — unreachable behind [Authorize].");

        var business = await db.Businesses.FirstOrDefaultAsync(b => b.Id == businessId);
        if (business is null)
        {
            return Unauthorized();
        }

        // Idempotent no-op — matching SetAiBusinessAnalystEnabled's own
        // convention of only auditing real transitions.
        if (business.ScheduledReportsEnabled == request.Enabled)
        {
            return Ok(new SetScheduledReportsEnabledResponse(business.ScheduledReportsEnabled));
        }

        var oldValue = SerializeScheduledReportsAuditValue(business);
        business.ScheduledReportsEnabled = request.Enabled;
        business.UpdatedAt = DateTimeOffset.UtcNow;

        await auditLogService.LogAsync(
            businessId,
            AuditEntityTypes.Business,
            businessId.ToString(),
            request.Enabled ? AuditEventTypes.BusinessScheduledReportsEnabled : AuditEventTypes.BusinessScheduledReportsDisabled,
            ActorType.NivoraBusinessStaff,
            changedByUserId: CurrentUserId,
            oldValue: oldValue,
            newValue: SerializeScheduledReportsAuditValue(business));

        await db.SaveChangesAsync();

        return Ok(new SetScheduledReportsEnabledResponse(business.ScheduledReportsEnabled));
    }

    /// <summary>
    /// Step 25 Candidate 2, Phase 1 (implementation plan §1.1/§1.2/§1.5) —
    /// kill switch for the stale/inactive Conversation auto-close feature.
    /// Modeled directly on SetAiBusinessAnalystEnabled/SetScheduledReportsEnabled
    /// above, mechanically: OwnerOnly, idempotent (no write/audit on a
    /// same-value request), audits every real transition. No precondition to
    /// check here — no external-integration dependency comparable to Shopify.
    /// Setting this true does not require ConversationInactivityCloseDays to
    /// be separately validated here — the field is non-nullable and
    /// CHECK-bounded (CK_Business_ConversationInactivityCloseDays_Range), so
    /// it structurally always holds a valid value regardless of this flag
    /// (plan §1.3).
    /// </summary>
    [HttpPut("conversation-auto-close")]
    [Authorize(Policy = NivoraPolicies.OwnerOnly)]
    public async Task<IActionResult> SetConversationAutoCloseEnabled([FromBody] SetConversationAutoCloseEnabledRequest request)
    {
        var businessId = tenantAccessor.BusinessId
            ?? throw new InvalidOperationException("Authenticated but no tenant resolved — unreachable behind [Authorize].");

        var business = await db.Businesses.FirstOrDefaultAsync(b => b.Id == businessId);
        if (business is null)
        {
            return Unauthorized();
        }

        // Idempotent no-op — matching SetScheduledReportsEnabled's own
        // convention of only auditing real transitions.
        if (business.ConversationAutoCloseEnabled == request.Enabled)
        {
            return Ok(new SetConversationAutoCloseEnabledResponse(business.ConversationAutoCloseEnabled));
        }

        var oldValue = SerializeConversationAutoCloseEnabledAuditValue(business);
        business.ConversationAutoCloseEnabled = request.Enabled;
        business.UpdatedAt = DateTimeOffset.UtcNow;

        await auditLogService.LogAsync(
            businessId,
            AuditEntityTypes.Business,
            businessId.ToString(),
            request.Enabled ? AuditEventTypes.BusinessConversationAutoCloseEnabled : AuditEventTypes.BusinessConversationAutoCloseDisabled,
            ActorType.NivoraBusinessStaff,
            changedByUserId: CurrentUserId,
            oldValue: oldValue,
            newValue: SerializeConversationAutoCloseEnabledAuditValue(business));

        await db.SaveChangesAsync();

        return Ok(new SetConversationAutoCloseEnabledResponse(business.ConversationAutoCloseEnabled));
    }

    /// <summary>
    /// Step 25 Candidate 2, Phase 1 (implementation plan §1.3/§1.5) — sets
    /// the inactivity threshold (days). Range-validated against
    /// ConversationAutoCloseLimits.MinDays/MaxDays BEFORE any DB write is
    /// attempted (400, not a DbUpdateException from the CHECK constraint) —
    /// the CHECK constraint (CK_Business_ConversationInactivityCloseDays_Range)
    /// remains a backstop, never the primary rejection mechanism. Idempotent
    /// (no write/audit on a same-value request) and OwnerOnly, matching every
    /// other setter in this file. Changing this value never touches
    /// ConversationAutoCloseEnabled.
    /// </summary>
    [HttpPut("conversation-inactivity-close-days")]
    [Authorize(Policy = NivoraPolicies.OwnerOnly)]
    public async Task<IActionResult> SetConversationInactivityCloseDays([FromBody] SetConversationInactivityCloseDaysRequest request)
    {
        if (request.Days < ConversationAutoCloseLimits.MinDays || request.Days > ConversationAutoCloseLimits.MaxDays)
        {
            return BadRequest($"Days must be between {ConversationAutoCloseLimits.MinDays} and {ConversationAutoCloseLimits.MaxDays}.");
        }

        var businessId = tenantAccessor.BusinessId
            ?? throw new InvalidOperationException("Authenticated but no tenant resolved — unreachable behind [Authorize].");

        var business = await db.Businesses.FirstOrDefaultAsync(b => b.Id == businessId);
        if (business is null)
        {
            return Unauthorized();
        }

        // Idempotent no-op — checked only after range validation above, since
        // an out-of-range request is always rejected regardless of the
        // current stored value (plan §1.5).
        if (business.ConversationInactivityCloseDays == request.Days)
        {
            return Ok(new SetConversationInactivityCloseDaysResponse(business.ConversationInactivityCloseDays));
        }

        var oldValue = SerializeConversationInactivityCloseDaysAuditValue(business);
        business.ConversationInactivityCloseDays = request.Days;
        business.UpdatedAt = DateTimeOffset.UtcNow;

        await auditLogService.LogAsync(
            businessId,
            AuditEntityTypes.Business,
            businessId.ToString(),
            AuditEventTypes.BusinessConversationInactivityCloseDaysChanged,
            ActorType.NivoraBusinessStaff,
            changedByUserId: CurrentUserId,
            oldValue: oldValue,
            newValue: SerializeConversationInactivityCloseDaysAuditValue(business));

        await db.SaveChangesAsync();

        return Ok(new SetConversationInactivityCloseDaysResponse(business.ConversationInactivityCloseDays));
    }

    /// <summary>Deliberately hand-picked, safe fields only — never the full
    /// Business entity, never PII, matching
    /// IntegrationsController.SerializeAuditValue's own convention.</summary>
    private static string SerializeAuditValue(Business business) =>
        JsonSerializer.Serialize(new { business.CancelOrderToolEnabled });

    /// <summary>Same convention as SerializeAuditValue above, scoped to the
    /// AiBusinessAnalystEnabled field.</summary>
    private static string SerializeAiBusinessAnalystAuditValue(Business business) =>
        JsonSerializer.Serialize(new { business.AiBusinessAnalystEnabled });

    /// <summary>Same convention as SerializeAuditValue above, scoped to the
    /// ScheduledReportsEnabled field.</summary>
    private static string SerializeScheduledReportsAuditValue(Business business) =>
        JsonSerializer.Serialize(new { business.ScheduledReportsEnabled });

    /// <summary>Same convention as SerializeAuditValue above, scoped to the
    /// ConversationAutoCloseEnabled field.</summary>
    private static string SerializeConversationAutoCloseEnabledAuditValue(Business business) =>
        JsonSerializer.Serialize(new { business.ConversationAutoCloseEnabled });

    /// <summary>Same convention as SerializeAuditValue above, scoped to the
    /// ConversationInactivityCloseDays field.</summary>
    private static string SerializeConversationInactivityCloseDaysAuditValue(Business business) =>
        JsonSerializer.Serialize(new { business.ConversationInactivityCloseDays });
}
