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

    /// <summary>Deliberately hand-picked, safe fields only — never the full
    /// Business entity, never PII, matching
    /// IntegrationsController.SerializeAuditValue's own convention.</summary>
    private static string SerializeAuditValue(Business business) =>
        JsonSerializer.Serialize(new { business.CancelOrderToolEnabled });
}
