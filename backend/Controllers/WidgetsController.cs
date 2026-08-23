using System.Security.Cryptography;
using backend.Auth;
using backend.Data;
using backend.Interfaces;
using backend.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace backend.Controllers;

public record CreateWidgetRequest(string Name, List<string>? AllowedDomains);

public record WidgetResponse(
    Guid Id,
    string Name,
    string PublicKey,
    List<string> AllowedDomains,
    bool IsActive,
    DateTimeOffset CreatedAt);

/// <summary>
/// Step 6: the staff-facing widget-provisioning surface — the production
/// path that creates a Widget row, previously missing entirely (only test
/// code could construct one). Deliberately a separate controller from
/// WidgetController (api/widget, singular): that one is the public,
/// anonymous/widget-JWT-facing surface with a permissive any-origin CORS
/// policy; this one is a normal staff-cookie-authenticated dashboard
/// resource and relies on the default DashboardDev CORS policy like every
/// other staff controller (no [EnableCors] override).
///
/// Reads: AnyStaff. Writes: OwnerOnly — deliberately not the OwnerOrAdmin
/// default most other write-gated controllers use. A Widget's PublicKey is
/// a live production credential (anyone holding it can open widget
/// sessions for this business); same "materially higher-stakes than a
/// normal write" category TagModeMappingsController's own OwnerOnly
/// decision already established for this codebase.
/// </summary>
[ApiController]
[Route("api/widgets")]
[Authorize(Policy = NivoraPolicies.AnyStaff)]
public class WidgetsController(
    AppDbContext db,
    ICurrentTenantAccessor tenantAccessor,
    IAuthorizationService authorizationService) : ControllerBase
{
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id)
    {
        // Layer 1: the global tenant query filter means this can never load
        // another business's row.
        var widget = await db.Widgets.FirstOrDefaultAsync(w => w.Id == id);
        if (widget is null) return NotFound();

        // Layer 2, explicit — same SameTenant check every other controller
        // in this codebase uses, not assumed redundant just because layer 1
        // already worked.
        var authResult = await authorizationService.AuthorizeAsync(User, widget, NivoraPolicies.SameTenant);
        if (!authResult.Succeeded) return NotFound();

        return Ok(ToResponse(widget));
    }

    [HttpPost]
    [Authorize(Policy = NivoraPolicies.OwnerOnly)]
    public async Task<IActionResult> Create([FromBody] CreateWidgetRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest("Name is required.");
        }

        var businessId = tenantAccessor.BusinessId
            ?? throw new InvalidOperationException("Authenticated but no tenant resolved — unreachable behind [Authorize].");

        var allowedDomains = (request.AllowedDomains ?? [])
            .Select(d => d.Trim())
            .Where(d => d.Length > 0)
            .ToList();

        var now = DateTimeOffset.UtcNow;
        var widget = new Widget
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            Name = request.Name.Trim(),
            // 256 bits of CSPRNG entropy — RandomNumberGenerator, deliberately
            // not Guid.NewGuid(), which carries no cryptographic-unpredictability
            // guarantee. Collision against the DB's unique index on PublicKey
            // is not a realistic scenario at this entropy, so no retry-on-
            // conflict loop is added here — the same proportionality this
            // codebase already applies to Guid.NewGuid()-based uniqueness
            // needs elsewhere (e.g. WidgetTokenService's JWT Jti).
            PublicKey = RandomNumberGenerator.GetHexString(64, lowercase: true),
            AllowedDomains = allowedDomains,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Widgets.Add(widget);
        await db.SaveChangesAsync();

        return CreatedAtAction(nameof(Get), new { id = widget.Id }, ToResponse(widget));
    }

    private static WidgetResponse ToResponse(Widget w) =>
        new(w.Id, w.Name, w.PublicKey, w.AllowedDomains, w.IsActive, w.CreatedAt);
}
