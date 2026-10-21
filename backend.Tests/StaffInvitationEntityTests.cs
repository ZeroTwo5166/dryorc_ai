using System.Security.Cryptography;
using backend.Auth;
using backend.Data;
using backend.Interfaces;
using backend.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace backend.Tests;

/// <summary>
/// Staff invite slice 2: schema tests for StaffInvitation against nivora_test
/// through a bare ServiceProvider (same shape as NotificationDataTests and
/// AiProviderInvocationPersistenceTests). T-K4: the tenant query filter.
/// T-DB: every CHECK and unique index rejects a bad row by name
/// (docs/handoffs/staff-invite-design.md Section 2 and Section T).
/// </summary>
public class StaffInvitationEntityTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;
    private readonly List<Guid> _businessIdsToClean = [];

    public Task InitializeAsync()
    {
        _provider = new ServiceCollection()
            .AddSingleton<ICurrentTenantAccessor>(new MutableTenantAccessor())
            .AddDbContext<AppDbContext>(o => o.UseNpgsql(
                TestDatabase.ConnectionString, x => x.UseVector()))
            .BuildServiceProvider();

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await TestBusinessCleanup.DeleteThenDisposeAsync(_businessIdsToClean, _provider);
    }

    // ── Fixture helpers ─────────────────────────────────────────────

    /// <summary>One business and one staff user in it; returns (BusinessId, UserId).</summary>
    private async Task<(Guid BusinessId, Guid UserId)> SeedBusinessWithUserAsync(AppDbContext db, string name)
    {
        var now = DateTimeOffset.UtcNow;
        var businessId = Guid.NewGuid();
        db.Businesses.Add(new Business
        {
            Id = businessId,
            Name = name,
            TimeZone = "Europe/Copenhagen",
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        _businessIdsToClean.Add(businessId);

        var userName = $"staff-invite-{Guid.NewGuid():N}@example.test";
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            UserName = userName,
            NormalizedUserName = userName.ToUpperInvariant(),
            Email = userName,
            NormalizedEmail = userName.ToUpperInvariant(),
            SecurityStamp = Guid.NewGuid().ToString(),
            ConcurrencyStamp = Guid.NewGuid().ToString(),
            CreatedAt = now,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        return (businessId, user.Id);
    }

    /// <summary>A row that satisfies every slice 2 CHECK; tests change one field to break one.</summary>
    private static StaffInvitation BuildValidRow(Guid businessId, Guid invitedByUserId)
    {
        var now = DateTimeOffset.UtcNow;
        var email = $"staff-invite-{Guid.NewGuid():N}@example.test";
        return new StaffInvitation
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            Email = email,
            NormalizedKey = email.ToUpperInvariant(),
            Role = RoleNames.Agent,
            TokenHash = RandomNumberGenerator.GetBytes(32),
            InvitedByUserId = invitedByUserId,
            CreatedAt = now,
            ExpiresAt = now.AddDays(7),
            LastSentAt = now,
        };
    }

    /// <summary>Saves the row in a fresh scope and returns the PostgresException it is rejected with.</summary>
    private async Task<PostgresException> SaveExpectingPostgresErrorAsync(StaffInvitation row)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.StaffInvitations.Add(row);
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        return Assert.IsType<PostgresException>(ex.InnerException);
    }

    private void SetTenant(Guid? businessId) =>
        ((MutableTenantAccessor)_provider.GetRequiredService<ICurrentTenantAccessor>()).CurrentBusinessId = businessId;

    // ── T-K4: tenant query filter ───────────────────────────────────

    [Fact]
    public async Task TenantFilter_BusinessASeesOnlyItsOwn()
    {
        SetTenant(null);
        await using var seedScope = _provider.CreateAsyncScope();
        var seedDb = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (businessIdA, userIdA) = await SeedBusinessWithUserAsync(seedDb, "Staff Invite Tenant Filter A");
        var (businessIdB, userIdB) = await SeedBusinessWithUserAsync(seedDb, "Staff Invite Tenant Filter B");

        var rowA = BuildValidRow(businessIdA, userIdA);
        var rowB = BuildValidRow(businessIdB, userIdB);
        seedDb.StaffInvitations.AddRange(rowA, rowB);
        await seedDb.SaveChangesAsync();

        await using var scopeA = _provider.CreateAsyncScope();
        var dbA = scopeA.ServiceProvider.GetRequiredService<AppDbContext>();
        SetTenant(businessIdA);
        var seenByA = await dbA.StaffInvitations.ToListAsync();
        Assert.Single(seenByA);
        Assert.Equal(rowA.Id, seenByA[0].Id);

        var bothIds = new[] { rowA.Id, rowB.Id };
        var unfiltered = await dbA.StaffInvitations.IgnoreQueryFilters()
            .Where(s => bothIds.Contains(s.Id))
            .Select(s => s.Id)
            .ToListAsync();
        Assert.Equal(bothIds.Order(), unfiltered.Order());
    }

    [Fact]
    public async Task NoAmbientTenant_SeesZeroRows()
    {
        SetTenant(null);
        await using var seedScope = _provider.CreateAsyncScope();
        var seedDb = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (businessIdA, userIdA) = await SeedBusinessWithUserAsync(seedDb, "Staff Invite No Tenant A");
        var (businessIdB, userIdB) = await SeedBusinessWithUserAsync(seedDb, "Staff Invite No Tenant B");

        var rowA = BuildValidRow(businessIdA, userIdA);
        var rowB = BuildValidRow(businessIdB, userIdB);
        seedDb.StaffInvitations.AddRange(rowA, rowB);
        await seedDb.SaveChangesAsync();

        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Empty(await db.StaffInvitations.ToListAsync());

        var bothIds = new[] { rowA.Id, rowB.Id };
        var unfiltered = await db.StaffInvitations.IgnoreQueryFilters()
            .Where(s => bothIds.Contains(s.Id))
            .Select(s => s.Id)
            .ToListAsync();
        Assert.Equal(bothIds.Order(), unfiltered.Order());
    }

    // ── T-DB: constraints ───────────────────────────────────────────

    [Fact]
    public async Task Constraints_RoleOwner_RejectedByRoleCheck()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (businessId, userId) = await SeedBusinessWithUserAsync(db, "Staff Invite Role Check");

        var row = BuildValidRow(businessId, userId);
        row.Role = RoleNames.Owner;

        var pgEx = await SaveExpectingPostgresErrorAsync(row);
        Assert.Equal("23514", pgEx.SqlState); // check_violation
        Assert.Equal("CK_StaffInvitations_Role", pgEx.ConstraintName);
    }

    [Fact]
    public async Task Constraints_TokenHash31Bytes_RejectedByLengthCheck()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (businessId, userId) = await SeedBusinessWithUserAsync(db, "Staff Invite TokenHash Length");

        var row = BuildValidRow(businessId, userId);
        row.TokenHash = RandomNumberGenerator.GetBytes(31);

        var pgEx = await SaveExpectingPostgresErrorAsync(row);
        Assert.Equal("23514", pgEx.SqlState); // check_violation
        Assert.Equal("CK_StaffInvitations_TokenHash_Length", pgEx.ConstraintName);
    }

    [Fact]
    public async Task Constraints_ExpiresAtEqualsCreatedAt_RejectedByExpiresAfterCreatedCheck()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (businessId, userId) = await SeedBusinessWithUserAsync(db, "Staff Invite Expiry Check");

        var row = BuildValidRow(businessId, userId);
        row.ExpiresAt = row.CreatedAt;

        var pgEx = await SaveExpectingPostgresErrorAsync(row);
        Assert.Equal("23514", pgEx.SqlState); // check_violation
        Assert.Equal("CK_StaffInvitations_ExpiresAfterCreated", pgEx.ConstraintName);
    }

    [Fact]
    public async Task Constraints_DuplicateTokenHash_RejectedByUniqueIndex()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (businessId, userId) = await SeedBusinessWithUserAsync(db, "Staff Invite Duplicate TokenHash");

        var first = BuildValidRow(businessId, userId);
        db.StaffInvitations.Add(first);
        await db.SaveChangesAsync();

        var second = BuildValidRow(businessId, userId); // different email, same hash
        second.TokenHash = (byte[])first.TokenHash.Clone();

        var pgEx = await SaveExpectingPostgresErrorAsync(second);
        Assert.Equal("23505", pgEx.SqlState); // unique_violation
        Assert.Equal("IX_StaffInvitations_TokenHash", pgEx.ConstraintName);
    }

    [Fact]
    public async Task Constraints_DuplicateBusinessAndNormalizedKey_RejectedByUniqueIndex()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (businessId, userId) = await SeedBusinessWithUserAsync(db, "Staff Invite Duplicate Key");

        var first = BuildValidRow(businessId, userId);
        db.StaffInvitations.Add(first);
        await db.SaveChangesAsync();

        var second = BuildValidRow(businessId, userId); // fresh token hash, same business and key
        second.Email = first.Email;
        second.NormalizedKey = first.NormalizedKey;

        var pgEx = await SaveExpectingPostgresErrorAsync(second);
        Assert.Equal("23505", pgEx.SqlState); // unique_violation
        Assert.Equal("IX_StaffInvitations_BusinessId_NormalizedKey", pgEx.ConstraintName);
    }

    private class MutableTenantAccessor : ICurrentTenantAccessor
    {
        public Guid? CurrentBusinessId { get; set; }
        public Guid? BusinessId => CurrentBusinessId;
    }
}
