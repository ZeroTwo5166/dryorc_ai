using backend.Auth;
using backend.Data;
using backend.Interfaces;
using backend.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace backend.Controllers;

public record CreateCustomerRequest(string? Email, string? Name);

public record CustomerResponse(Guid Id, Guid BusinessId, string? Email, string? Name);

/// <summary>
/// Minimal protected, tenant-scoped resource — exists to give Step 4's
/// authorization layer something real to enforce against (and the
/// cross-tenant sanity test something real to hit), not a real feature.
/// Demonstrates all 4 checklist steps from NIVORA_DEVELOPMENT_PLAN_V2.md
/// Section 3 explicitly, rather than relying on any one of them alone.
/// </summary>
[ApiController]
[Route("api/customers")]
[Authorize(Policy = NivoraPolicies.AnyStaff)] // step 1: who is the authenticated user
public class CustomersController(
    AppDbContext db,
    ICurrentTenantAccessor tenantAccessor,
    IAuthorizationService authorizationService) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List()
    {
        // Step 2 (which business) comes from ICurrentTenantAccessor via the
        // cookie's BusinessId claim. Step 3 (resource ownership) is
        // structural here: AppDbContext's global tenant query filter means
        // this query can never return a row belonging to another business
        // — there is no per-row check left to make for a list query.
        var customers = await db.Customers
            .OrderBy(c => c.CreatedAt)
            .Select(c => new CustomerResponse(c.Id, c.BusinessId, c.Email, c.Name))
            .ToListAsync();

        return Ok(customers);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id)
    {
        // Layer 1, defense-in-depth: the global tenant filter means this
        // can structurally never load another business's row.
        var customer = await db.Customers.FirstOrDefaultAsync(c => c.Id == id);
        if (customer is null) return NotFound();

        // Layer 2, explicit — step 3 of the checklist, not assumed just
        // because layer 1 already worked. NIVORA_DEVELOPMENT_PLAN_V2.md
        // Section 3 is explicit that query filtering alone must not be
        // treated as the complete authorization model.
        var authResult = await authorizationService.AuthorizeAsync(User, customer, NivoraPolicies.SameTenant);
        if (!authResult.Succeeded) return NotFound();

        return Ok(new CustomerResponse(customer.Id, customer.BusinessId, customer.Email, customer.Name));
    }

    [HttpPost]
    [Authorize(Policy = NivoraPolicies.OwnerOrAdmin)] // step 4: does the role permit this operation
    public async Task<IActionResult> Create([FromBody] CreateCustomerRequest request)
    {
        var businessId = tenantAccessor.BusinessId
            ?? throw new InvalidOperationException("Authenticated but no tenant resolved — unreachable behind [Authorize].");

        // BusinessId always comes from the authenticated principal, never
        // from the request — CreateCustomerRequest has no BusinessId field
        // at all, so there is nothing for a client-supplied value to override.
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            Email = request.Email,
            Name = request.Name,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();

        return CreatedAtAction(
            nameof(Get),
            new { id = customer.Id },
            new CustomerResponse(customer.Id, customer.BusinessId, customer.Email, customer.Name));
    }
}
