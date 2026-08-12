using backend.Data;
using backend.Interfaces;
using backend.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace backend.Tests;

/// <summary>
/// Deterministic, mechanism-level regression test for the bug documented in
/// AppDbContext.cs and project_nivora_ef_gotchas.md #5.
///
/// Context on why this test exists in this shape: a real, captured stack
/// trace (during the original Step 4 debugging session) proved that
/// Identity's SecurityStampValidator resolves a scoped AppDbContext
/// *during* UseAuthentication() — before HttpContext.User is populated for
/// that request — via SecurityStampValidator&lt;TUser&gt;'s constructor
/// requiring SignInManager&lt;TUser&gt; (confirmed by reflecting on the
/// installed Microsoft.AspNetCore.Identity.dll: that constructor is not
/// conditional). But a later attempt to build an end-to-end HTTP test that
/// naturally *forces* that interleaving on every run was NOT reliably
/// reproducible in a fast, tight request loop across repeated attempts —
/// SecurityStampValidator's own internal validation appears to be gated by
/// something interval/timing-related that a sub-second test loop doesn't
/// reliably cross, even though the exact same code path fired originally
/// (under much slower, logging-laden conditions). Chasing that timing
/// dependency further would only produce a flaky test.
///
/// So instead of depending on organic SecurityStampValidator timing, this
/// test manually reproduces the exact interleaving pattern the bug depends
/// on — an AppDbContext instance constructed from a DI scope while
/// ICurrentTenantAccessor.BusinessId currently returns null (simulating
/// "constructed before this request's auth was resolved"), with the
/// accessor's value THEN changing to the real tenant later in that same
/// scope's lifetime (simulating "auth completes later in the request") —
/// and asserts a query run on that *same* AppDbContext instance afterward
/// is correctly tenant-scoped. This is deterministic on every run, and
/// directly targets the mechanism the fix relies on (reading
/// ICurrentTenantAccessor.BusinessId live inside the filter lambda, not
/// snapshotting it once in the constructor) rather than depending on
/// whether Identity happens to trigger it naturally on any given request.
/// </summary>
public class AppDbContextTenantFilterTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;
    private Guid _businessId;
    private Guid _customerId;

    public async Task InitializeAsync()
    {
        var configuration = new ConfigurationBuilder()
            .AddUserSecrets<Program>()
            .Build();
        var connectionUri = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection not found via user-secrets.");

        _provider = new ServiceCollection()
            .AddSingleton<ICurrentTenantAccessor>(new MutableTenantAccessor())
            .AddDbContext<AppDbContext>(o => o.UseNpgsql(
                PostgresConnectionString.FromUri(connectionUri), x => x.UseVector()))
            .BuildServiceProvider();

        _businessId = Guid.NewGuid();
        _customerId = Guid.NewGuid();

        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Businesses.Add(new Business
        {
            Id = _businessId,
            Name = "Filter Interleaving Test",
            TimeZone = "Europe/Copenhagen",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.Customers.Add(new Customer
        {
            Id = _customerId,
            BusinessId = _businessId,
            Name = "Filter Interleaving Customer",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await using (var scope = _provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Customers\" WHERE \"BusinessId\" = {_businessId}");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Businesses\" WHERE \"Id\" = {_businessId}");
        }
        await _provider.DisposeAsync();
    }

    [Fact]
    public async Task QueryFilter_ReflectsAccessorValueAtQueryTime_NotAtDbContextConstructionTime()
    {
        var tenant = (MutableTenantAccessor)_provider.GetRequiredService<ICurrentTenantAccessor>();

        // 1. Simulate "constructed before this request's auth was resolved":
        //    tenant is null at the moment AppDbContext is built — exactly
        //    SecurityStampValidator's captured interleaving.
        tenant.CurrentBusinessId = null;

        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>(); // constructed NOW, tenant == null

        // A query at this point must see zero rows — fail-closed, matching
        // "no tenant resolved yet" (this is also what caused the original
        // bug's symptom when the OLD code froze this state permanently).
        var beforeAuth = await db.Customers.ToListAsync();
        Assert.Empty(beforeAuth);

        // 2. Simulate "auth completes later in the same request" — the
        //    accessor's value changes, but the SAME AppDbContext instance
        //    from step 1 is reused (exactly what happens in the real app:
        //    AppDbContext is Scoped, so the request's controller gets back
        //    the same, already-constructed instance).
        tenant.CurrentBusinessId = _businessId;

        // 3. The fix under test: this query, on the SAME instance, must now
        //    reflect the NEW value — proving the filter reads
        //    ICurrentTenantAccessor.BusinessId live at query-execution time,
        //    not a value captured once when AppDbContext was constructed.
        var afterAuth = await db.Customers.ToListAsync();
        Assert.Single(afterAuth);
        Assert.Equal(_customerId, afterAuth[0].Id);
    }

    private class MutableTenantAccessor : ICurrentTenantAccessor
    {
        public Guid? CurrentBusinessId { get; set; }
        public Guid? BusinessId => CurrentBusinessId;
    }
}
