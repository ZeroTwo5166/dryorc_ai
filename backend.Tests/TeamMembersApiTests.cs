using System.Net;
using System.Net.Http.Json;
using backend.Auth;
using backend.Constants;
using backend.Controllers;
using backend.Data;
using backend.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace backend.Tests;

/// <summary>
/// Staff invite slice 5c: GET /api/team/members (DEC D14;
/// docs/handoffs/staff-invite-design.md Section 8 TeamController, ED-26;
/// Section T row T-D14). Any staff role reads the member list of its own
/// business and never sees another business's users.
/// </summary>
public class TeamMembersApiTests : IAsyncLifetime
{
    private const string Password = "Passw0rd!!";

    private readonly NivoraApiFactory _factory = new();
    private readonly List<Guid> _businessIdsToClean = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await TestBusinessCleanup.DeleteThenDisposeAsync(_businessIdsToClean, _factory);
    }

    // T-D14
    [Fact]
    public async Task AnyStaff_SeesOwnBusinessMembersOnly()
    {
        var businessA = await SeedBusinessAsync();
        var ownerA = await SeedStaffAsync(businessA, RoleNames.Owner);
        var adminA = await SeedStaffAsync(businessA, RoleNames.Admin);
        var agentA = await SeedStaffAsync(businessA, RoleNames.Agent);
        var businessB = await SeedBusinessAsync();
        var ownerB = await SeedStaffAsync(businessB, RoleNames.Owner);
        var agentB = await SeedStaffAsync(businessB, RoleNames.Agent);

        using var client = _factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(agentA.Email!, Password));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var cookie = login.Headers.GetValues("Set-Cookie").First().Split(';')[0];

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/team/members");
        request.Headers.Add("Cookie", cookie);
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var members = await response.Content.ReadFromJsonAsync<List<TeamMemberResponse>>();
        Assert.NotNull(members);

        var expected = new[] { ownerA, adminA, agentA }
            .Select(u => (u.Id, u.Email!))
            .OrderBy(m => m.Id)
            .ToArray();
        Assert.Equal(expected, members!.Select(m => (m.UserId, m.Email)).OrderBy(m => m.UserId).ToArray());
        Assert.Equal(RoleNames.Owner, members.Single(m => m.UserId == ownerA.Id).Role);
        Assert.Equal(RoleNames.Admin, members.Single(m => m.UserId == adminA.Id).Role);
        Assert.Equal(RoleNames.Agent, members.Single(m => m.UserId == agentA.Id).Role);
        Assert.DoesNotContain(members, m => m.UserId == ownerB.Id || m.UserId == agentB.Id);
        Assert.Equal(members.OrderBy(m => m.JoinedAt).Select(m => m.UserId), members.Select(m => m.UserId));
    }

    private async Task<Guid> SeedBusinessAsync()
    {
        var businessId = Guid.NewGuid();
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Businesses.Add(new Business
            {
                Id = businessId,
                Name = $"Team Co {businessId:N}",
                TimeZone = "Europe/Copenhagen",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        _businessIdsToClean.Add(businessId);
        return businessId;
    }

    /// <summary>BusinessApiTests.SeedStaffIntoBusinessAsync shape.</summary>
    private async Task<ApplicationUser> SeedStaffAsync(Guid businessId, string role)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var email = $"staff-invite-{Guid.NewGuid():N}@example.test";
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var createResult = await userManager.CreateAsync(user, Password);
        Assert.True(createResult.Succeeded, string.Join("; ", createResult.Errors.Select(e => e.Description)));
        var roleResult = await userManager.AddToRoleAsync(user, role);
        Assert.True(roleResult.Succeeded, string.Join("; ", roleResult.Errors.Select(e => e.Description)));
        return user;
    }
}
