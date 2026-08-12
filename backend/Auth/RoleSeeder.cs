using Microsoft.AspNetCore.Identity;

namespace backend.Auth;

/// <summary>
/// Idempotent startup role seeding. Checks existence before creating each
/// role, so repeated application startups (redeploys, restarts) never
/// attempt a duplicate insert — the DB's own unique index on
/// NormalizedName (default Identity schema) is a second layer of
/// protection against a race between concurrent startups.
/// </summary>
public static class RoleSeeder
{
    public static async Task SeedAsync(IServiceProvider services)
    {
        var roleManager = services.GetRequiredService<RoleManager<IdentityRole<Guid>>>();

        foreach (var roleName in RoleNames.All)
        {
            if (!await roleManager.RoleExistsAsync(roleName))
            {
                var result = await roleManager.CreateAsync(new IdentityRole<Guid>(roleName));
                if (!result.Succeeded)
                {
                    throw new InvalidOperationException(
                        $"Failed to seed role '{roleName}': {string.Join("; ", result.Errors.Select(e => e.Description))}");
                }
            }
        }
    }
}
