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
using Npgsql;

namespace backend.Controllers;

public record ConnectIntegrationRequest(string ExternalAccountIdentifier);

public record ConnectIntegrationResponse(string AuthorizationUrl);

/// <summary>
/// Never includes EncryptedAccessToken/EncryptedRefreshToken — token
/// material must never leave the backend in any API response.
/// </summary>
public record IntegrationResponse(
    Guid Id, string Platform, string? ExternalAccountIdentifier, List<string> Scopes,
    DateTimeOffset? ScopesUpdatedAt, DateTimeOffset ConnectedAt, DateTimeOffset? DisconnectedAt, bool IsActive);

/// <summary>
/// Step 18: the generic OAuth integration framework — connect/callback/
/// disconnect + list, for whatever IIntegrationOAuthProvider implementations
/// are registered in DI. Step 18 ships NO concrete provider in production
/// (see IIntegrationOAuthProvider's own doc comment) — IEnumerable
/// injection correctly resolves to an empty sequence when nothing is
/// registered, so every {platform} route below hits the explicit
/// "unregistered platform → 404" branch in production today, by design,
/// until Step 19 registers the first real provider (Shopify).
///
/// Reads (List): AnyStaff. Every mutation (Connect/Callback/Disconnect):
/// OwnerOnly — connecting/disconnecting an external integration
/// grants/revokes real access to an external system on the business's
/// behalf, a materially higher-stakes category than ordinary staff writes,
/// the same tier TagModeMappingsController already established for a
/// comparable reason (AI autonomy), chosen deliberately over the more
/// common OwnerOrAdmin tier for this controller.
///
/// Callback is reachable only via the OAuth provider's own redirect — the
/// staff's browser makes a top-level GET navigation back to this backend,
/// which still carries the staff cookie (SameSite=Lax is exactly what
/// preserves that on a cross-site top-level navigation), so Callback
/// authenticates through the ordinary staff-cookie pipeline like every other
/// OwnerOnly action here. The OAuth "state" token is never a substitute for
/// that authentication — it is an additional CSRF/tenant-binding check
/// layered on top of it. See IntegrationStateTokenService's doc comment for
/// the full four-part security breakdown (signed state / live cookie /
/// provider code / residual risk) — never collapse that into a generic
/// "OAuth is CSRF protected" statement anywhere this controller is discussed.
/// </summary>
[ApiController]
[Route("api/integrations")]
[Authorize(Policy = NivoraPolicies.AnyStaff)]
public class IntegrationsController(
    AppDbContext db,
    ICurrentTenantAccessor tenantAccessor,
    IAuthorizationService authorizationService,
    IAuditLogService auditLogService,
    IIntegrationTokenProtector tokenProtector,
    IIntegrationStateTokenService stateTokenService,
    IEnumerable<IIntegrationOAuthProvider> providers,
    IConfiguration configuration,
    ILogger<IntegrationsController> logger) : ControllerBase
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

    [HttpGet]
    public async Task<IActionResult> List()
    {
        // Layer 1: the global tenant query filter means this can never
        // return another business's row — no per-row check left to make for
        // a list query, same reasoning as every other List() in this
        // codebase.
        var integrations = await db.Integrations
            .OrderBy(i => i.Platform)
            .ToListAsync();

        return Ok(integrations.Select(ToResponse).ToList());
    }

    /// <summary>
    /// Unregistered-platform behavior is an explicit Step 18 contract, not
    /// an incidental gap: Step 18 ships zero concrete providers, so this
    /// branch is the one every production call currently takes. Returns a
    /// clean 404 — never an unhandled exception, never a 500, never a
    /// silent no-op — and creates no Integration row.
    /// </summary>
    [HttpPost("{platform}/connect")]
    [Authorize(Policy = NivoraPolicies.OwnerOnly)]
    public IActionResult Connect(string platform, [FromBody] ConnectIntegrationRequest request)
    {
        var provider = providers.FirstOrDefault(p => p.Platform == platform);
        if (provider is null) return NotFound();

        var businessId = tenantAccessor.BusinessId
            ?? throw new InvalidOperationException("Authenticated but no tenant resolved — unreachable behind [Authorize].");

        var state = stateTokenService.Issue(businessId, CurrentUserId, platform);

        string authorizationUrl;
        try
        {
            authorizationUrl = provider.BuildAuthorizationUrl(state, request.ExternalAccountIdentifier);
        }
        catch (ArgumentException ex)
        {
            // Providers with their own syntax rules for ExternalAccountIdentifier
            // (e.g. Shopify's bare-shop-name rule) throw ArgumentException on an
            // invalid value — see IIntegrationOAuthProvider.BuildAuthorizationUrl's
            // doc comment. A 400 here, not a 500: this is caller input, not a
            // server fault.
            return BadRequest(ex.Message);
        }

        return Ok(new ConnectIntegrationResponse(authorizationUrl));
    }

    /// <summary>
    /// Reachable only via the OAuth provider's own redirect — see this
    /// controller's class-level doc comment for why the staff cookie is
    /// still present on this request. Every failure branch redirects back to
    /// the dashboard's integrations settings page with an error indicator
    /// rather than returning a raw API error, since the caller here is a
    /// browser mid-navigation, not fetch(). Unregistered-platform check
    /// happens first, before any state/token work, same contract as Connect.
    ///
    /// Step 19 LOCKED ordering (do not reorder): provider resolution →
    /// provider-specific callback STRUCTURAL validation (against the raw
    /// query collection, not bound parameters — see
    /// IIntegrationOAuthProvider.ValidateCallbackStructure's doc comment for
    /// why this must run before state validation) → Nivora state-token
    /// validation → live-cookie↔state BusinessId/UserId check →
    /// provider-specific callback AUTHENTICITY validation (e.g. Shopify
    /// HMAC, then post-HMAC shop-syntax revalidation — the SSRF boundary) →
    /// token exchange → existing upsert/audit/save/redirect. Shopify HMAC
    /// and the Nivora state token are independent mechanisms — neither
    /// substitutes for the other; both must pass. `code`/`state` are read
    /// from the raw query, not [FromQuery]-bound parameters, since binding
    /// alone can silently hide duplicate/unknown parameters that structural
    /// validation must catch.
    /// </summary>
    [HttpGet("{platform}/callback")]
    [Authorize(Policy = NivoraPolicies.OwnerOnly)]
    public async Task<IActionResult> Callback(string platform)
    {
        var provider = providers.FirstOrDefault(p => p.Platform == platform);
        if (provider is null) return NotFound();

        var settingsUrl = BuildSettingsRedirectUrl();
        var query = HttpContext.Request.Query;

        if (!provider.ValidateCallbackStructure(query))
        {
            return Redirect($"{settingsUrl}?integrationError=hmac_invalid");
        }

        var code = query["code"].ToString();
        var state = query["state"].ToString();

        var claims = stateTokenService.Validate(state, platform);
        if (claims is null)
        {
            return Redirect($"{settingsUrl}?integrationError=invalid_state");
        }

        var businessId = tenantAccessor.BusinessId
            ?? throw new InvalidOperationException("Authenticated but no tenant resolved — unreachable behind [Authorize].");

        // The live authenticated cookie's BusinessId/UserId must match the
        // state token's embedded values — the state token alone is never
        // trusted for tenant identity. See IntegrationStateTokenService's
        // doc comment, point 2.
        if (claims.BusinessId != businessId || claims.UserId != CurrentUserId)
        {
            return Redirect($"{settingsUrl}?integrationError=state_mismatch");
        }

        // Provider-specific authenticity (e.g. Shopify HMAC) + post-HMAC
        // shop-syntax revalidation — the untrusted callback `shop`/external
        // identifier only becomes authoritative here, never earlier. Reuses
        // integrationError=hmac_invalid: both a structural failure above and
        // an authenticity failure here mean the same thing to the caller —
        // "this callback did not pass Shopify's own contract" — Shopify's
        // current documentation defines no separate error taxonomy between
        // them.
        var externalAccountIdentifier = provider.ValidateCallbackAuthenticity(query);
        if (externalAccountIdentifier is null)
        {
            return Redirect($"{settingsUrl}?integrationError=hmac_invalid");
        }

        OAuthTokenResult tokenResult;
        try
        {
            tokenResult = await provider.ExchangeCodeAsync(code, externalAccountIdentifier, HttpContext.RequestAborted);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Integration code exchange failed for platform {Platform}.", platform);
            return Redirect($"{settingsUrl}?integrationError=exchange_failed");
        }

        var now = DateTimeOffset.UtcNow;
        var encryptedAccessToken = tokenProtector.Protect(platform, tokenResult.AccessToken);
        var encryptedRefreshToken = tokenResult.RefreshToken is { } refreshToken
            ? tokenProtector.Protect(platform, refreshToken)
            : null;

        var integration = await db.Integrations
            .FirstOrDefaultAsync(i => i.BusinessId == businessId && i.Platform == platform);

        if (integration is null)
        {
            integration = new Integration
            {
                Id = Guid.NewGuid(),
                BusinessId = businessId,
                Platform = platform,
                EncryptedAccessToken = encryptedAccessToken,
                ConnectedAt = now,
                UpdatedAt = now,
            };
            db.Integrations.Add(integration);
        }
        else
        {
            integration.EncryptedAccessToken = encryptedAccessToken;
            integration.ConnectedAt = now;
            integration.DisconnectedAt = null;
            integration.UpdatedAt = now;
        }

        // Full replace, never merge — Scopes is a cache of the last
        // confirmed OAuth grant, per Integration.Scopes's own doc comment.
        integration.EncryptedRefreshToken = encryptedRefreshToken;
        integration.ExternalAccountIdentifier = tokenResult.ExternalAccountIdentifier;
        integration.Scopes = tokenResult.Scopes;
        integration.ScopesUpdatedAt = now;
        integration.TokenExpiresAt = tokenResult.ExpiresAt;
        integration.IsActive = true;

        // Same change tracker as the Integration write above — both commit
        // together in the one SaveChangesAsync() below. Token material is
        // deliberately excluded from the audit payload.
        await auditLogService.LogAsync(
            businessId,
            AuditEntityTypes.Integration,
            integration.Id.ToString(),
            AuditEventTypes.IntegrationConnected,
            ActorType.NivoraBusinessStaff,
            changedByUserId: CurrentUserId,
            newValue: SerializeAuditValue(integration));

        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            return Redirect($"{settingsUrl}?integrationError=conflict");
        }

        return Redirect($"{settingsUrl}?connected={platform}");
    }

    /// <summary>
    /// Idempotent — disconnecting an already-disconnected integration is a
    /// no-op (no second audit event), same spirit as
    /// CustomersController.RevokeIdentity. Never deletes the row or its
    /// encrypted token columns — IsActive/DisconnectedAt make it inert;
    /// hard deletion is not this endpoint's job. Gated on the Integration
    /// row's own existence, not on provider registration — an integration
    /// connected while its provider was registered must remain
    /// disconnectable even if that provider is later removed from DI.
    /// </summary>
    [HttpPost("{platform}/disconnect")]
    [Authorize(Policy = NivoraPolicies.OwnerOnly)]
    public async Task<IActionResult> Disconnect(string platform)
    {
        var integration = await db.Integrations.FirstOrDefaultAsync(i => i.Platform == platform);
        if (integration is null) return NotFound();

        var authResult = await authorizationService.AuthorizeAsync(User, integration, NivoraPolicies.SameTenant);
        if (!authResult.Succeeded) return NotFound();

        if (!integration.IsActive)
        {
            return NoContent();
        }

        var now = DateTimeOffset.UtcNow;
        integration.IsActive = false;
        integration.DisconnectedAt = now;
        integration.UpdatedAt = now;

        await auditLogService.LogAsync(
            integration.BusinessId,
            AuditEntityTypes.Integration,
            integration.Id.ToString(),
            AuditEventTypes.IntegrationDisconnected,
            ActorType.NivoraBusinessStaff,
            changedByUserId: CurrentUserId);

        await db.SaveChangesAsync();

        return NoContent();
    }

    private string BuildSettingsRedirectUrl()
    {
        var baseUrl = configuration["Frontend:BaseUrl"]
            ?? throw new InvalidOperationException("Missing Frontend:BaseUrl configuration.");
        return $"{baseUrl.TrimEnd('/')}/settings/integrations";
    }

    private static IntegrationResponse ToResponse(Integration integration) => new(
        integration.Id, integration.Platform, integration.ExternalAccountIdentifier, integration.Scopes,
        integration.ScopesUpdatedAt, integration.ConnectedAt, integration.DisconnectedAt, integration.IsActive);

    /// <summary>Deliberately hand-picked, safe fields only — never the
    /// encrypted token columns, per AuditLog.OldValue/NewValue's PII/secret
    /// exclusion rule.</summary>
    private static string SerializeAuditValue(Integration integration) =>
        JsonSerializer.Serialize(new
        {
            integration.Platform,
            integration.ExternalAccountIdentifier,
            integration.Scopes,
            integration.ConnectedAt,
        });
}
