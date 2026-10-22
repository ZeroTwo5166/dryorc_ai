using System.Security.Cryptography;
using backend.Auth;
using backend.Data;
using backend.Interfaces;
using backend.Models;
using backend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace backend.Tests;

/// <summary>
/// Staff invite slice 6, T-I8b: the retention job deletes StaffInvitation
/// rows whose ExpiresAt is at least 30 days past and keeps the rest, in every
/// business (DEC D9, I8; staff-invite design Section 12). Real Postgres, same
/// harness as AiProviderInvocationRetentionServiceTests.
///
/// The tenant accessor resolves no business: the fail-closed tenant filter
/// then matches no row, so this test only passes if the service bypasses it
/// (the global sweep, not one tenant's).
///
/// The service reads the wall clock, so the boundary row (ExpiresAt =
/// now - 30 days at seeding) is already past the cutoff when the delete
/// runs. <c>&lt;=</c> versus <c>&lt;</c> cannot be told apart this way; the source scan
/// T-I2 pins the predicate text.
///
/// In the ConversationAutoClose collection because every test host that runs
/// the real DataLifecycleBackgroundService is in it, and that host's first
/// tick runs this same global delete; see that collection's doc comment.
/// </summary>
[Collection("ConversationAutoClose")]
public class StaffInvitationCleanupServiceTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;
    private readonly List<Guid> _businessIdsToClean = [];

    public Task InitializeAsync()
    {
        _provider = new ServiceCollection()
            .AddSingleton<ICurrentTenantAccessor>(new FixedTenantAccessor(null))
            .AddDbContext<AppDbContext>(o => o.UseNpgsql(
                TestDatabase.ConnectionString, x => x.UseVector()))
            .AddScoped<IStaffInvitationCleanupService, StaffInvitationCleanupService>()
            .BuildServiceProvider();

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await TestBusinessCleanup.DeleteThenDisposeAsync(_businessIdsToClean, _provider);
    }

    // T-I8b
    [Fact]
    public async Task DeletesOnlyRowsExpiredAtLeast30Days_AcrossBusinesses()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IStaffInvitationCleanupService>();

        var (businessA, userA) = await SeedBusinessWithUserAsync(db, "Staff Invite Cleanup A");
        var (businessB, userB) = await SeedBusinessWithUserAsync(db, "Staff Invite Cleanup B");

        var now = DateTimeOffset.UtcNow;
        var expired31A = BuildRow(businessA, userA, now.AddDays(-31));
        var expired30A = BuildRow(businessA, userA, now.AddDays(-30));
        var expired29A = BuildRow(businessA, userA, now.AddDays(-29));
        var pendingA = BuildRow(businessA, userA, now.AddDays(7));
        var expired31B = BuildRow(businessB, userB, now.AddDays(-31));
        var expired30B = BuildRow(businessB, userB, now.AddDays(-30));
        var expired29B = BuildRow(businessB, userB, now.AddDays(-29));
        var pendingB = BuildRow(businessB, userB, now.AddDays(7));
        db.StaffInvitations.AddRange(expired31A, expired30A, expired29A, pendingA, expired31B, expired30B, expired29B, pendingB);
        await db.SaveChangesAsync();

        Guid[] seededIds =
        [
            expired31A.Id, expired30A.Id, expired29A.Id, pendingA.Id,
            expired31B.Id, expired30B.Id, expired29B.Id, pendingB.Id,
        ];

        // Setup guard: a failure here is a seeding failure, not a retention one.
        Assert.Equal(seededIds.Order(), await PresentSeededIdsAsync(seededIds));

        var deleted = await service.DeleteExpiredAsync();

        Assert.Equal(
            new[] { expired29A.Id, pendingA.Id, expired29B.Id, pendingB.Id }.Order(),
            await PresentSeededIdsAsync(seededIds));
        Assert.True(deleted >= 4, $"DeleteExpiredAsync returned {deleted}, expected at least the 4 seeded expired rows");
    }

    /// <summary>One business and one staff user in it (the inviter); returns (BusinessId, UserId).</summary>
    private async Task<(Guid BusinessId, Guid UserId)> SeedBusinessWithUserAsync(AppDbContext db, string name)
    {
        var now = DateTimeOffset.UtcNow;
        var businessId = Guid.NewGuid();
        db.Businesses.Add(new Business
        {
            Id = businessId,
            Name = $"{name} {Guid.NewGuid():N}",
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

    /// <summary>A row with the given ExpiresAt; CreatedAt = LastSentAt = ExpiresAt - 7 days, so every slice 2 CHECK holds.</summary>
    private static StaffInvitation BuildRow(Guid businessId, Guid invitedByUserId, DateTimeOffset expiresAt)
    {
        var email = $"staff-invite-{Guid.NewGuid():N}@example.test";
        var sentAt = expiresAt.AddDays(-7);
        return new StaffInvitation
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            Email = email,
            NormalizedKey = email.ToUpperInvariant(),
            Role = RoleNames.Agent,
            TokenHash = SHA256.HashData(RandomNumberGenerator.GetBytes(32)),
            InvitedByUserId = invitedByUserId,
            CreatedAt = sentAt,
            LastSentAt = sentAt,
            ExpiresAt = expiresAt,
        };
    }

    /// <summary>Which of the given seeded IDs still exist, sorted; read
    /// through a fresh scope, never the seeding context's change tracker.</summary>
    private async Task<List<Guid>> PresentSeededIdsAsync(Guid[] seededIds)
    {
        await using var readScope = _provider.CreateAsyncScope();
        var readDb = readScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var present = await readDb.StaffInvitations
            .IgnoreQueryFilters()
            .Where(i => seededIds.Contains(i.Id))
            .Select(i => i.Id)
            .ToListAsync();
        return present.Order().ToList();
    }

    private sealed class FixedTenantAccessor(Guid? businessId) : ICurrentTenantAccessor
    {
        public Guid? BusinessId => businessId;
    }
}
