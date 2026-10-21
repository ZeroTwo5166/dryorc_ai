using System.Buffers.Text;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using backend.Auth;
using backend.Constants;
using backend.Data;
using backend.Email;
using backend.Enums;
using backend.Interfaces;
using backend.Models;
using backend.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace backend.Tests;

/// <summary>
/// Staff invite slice 5b, service level: preview and the accept transaction
/// (docs/handoffs/staff-invite-design.md Section 7; Section T rows T-I1a,
/// T-I1b, T-I1c, T-D3b, T-D5 (also covers T-NE-F11), T-D6, T-D18). There is no
/// controller yet, so every test calls IStaffInvitationService from a scope
/// of the test host. The tenant comes from a private AsyncLocal accessor:
/// staff calls set it to the caller's business, preview and accept leave it
/// null, which is the "no tenant" state of an anonymous request (design V8).
/// Tokens are read from the link in the host's FakeEmailSender (D8).
/// </summary>
public class StaffInvitationAcceptTests : IAsyncLifetime
{
    private const string Password = "Passw0rd!!";

    private static readonly AsyncLocal<Guid?> Tenant = new();

    private readonly NivoraApiFactory _factory = new();
    private readonly List<Guid> _businessIdsToClean = [];
    private WebApplicationFactory<Program> _host = null!;

    public Task InitializeAsync()
    {
        _host = BuildHost();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await TestBusinessCleanup.DeleteThenDisposeAsync(_businessIdsToClean, _factory);
    }

    // T-I1a
    [Fact]
    public async Task Accept_Success_CreatesUserRoleAuditNotification_DeletesRow()
    {
        var (businessId, owner) = await SeedBusinessWithOwnerAsync();
        var email = NewEmail();
        var invitation = await CreateInvitationAsync(owner, email, RoleNames.Agent);
        var token = TokenFromLastEmailTo(email);

        var result = await RunAsync(null, s => s.AcceptAsync(token, Password, CancellationToken.None));

        Assert.Equal(StaffInvitationOutcome.Ok, result.Outcome);
        Assert.Equal(email, result.Accepted!.Email);

        await using var scope = _host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await FindUserAsync(db, email);
        Assert.NotNull(user);
        Assert.True(user!.EmailConfirmed);
        Assert.True(user.IsActive);
        Assert.Equal(businessId, user.BusinessId);
        Assert.Equal(new[] { RoleNames.Agent }, await RoleNamesOfAsync(db, user.Id));

        var accepted = Assert.Single(await AuditRowsAsync(db, invitation.Id), a => a.EventType == AuditEventTypes.StaffInvitationAccepted);
        Assert.Equal(ActorType.NivoraBusinessStaff, accepted.Actor);
        Assert.Equal(user.Id, accepted.ChangedByUserId);
        Assert.Single(await TeamMemberJoinedAsync(db, businessId));
        Assert.False(await db.StaffInvitations.IgnoreQueryFilters().AnyAsync(i => i.Id == invitation.Id));
    }

    // T-I1b (design V2: proves that rolling back undoes UserManager's own writes)
    [Fact]
    public async Task Accept_FailureAfterUserCreateAndRole_RollsBackEverything()
    {
        var capture = new AcceptFailureCapture();
        var host = BuildHost(s => s.AddScoped<IStaffInvitationAcceptFailurePoint>(
            sp => new ThrowingAcceptFailurePoint(sp.GetRequiredService<AppDbContext>(), capture)));
        var (businessId, owner) = await SeedBusinessWithOwnerAsync(host);
        var email = NewEmail();
        var invitation = await CreateInvitationAsync(owner, email, RoleNames.Agent, host);
        var token = TokenFromLastEmailTo(email, host);
        var before = await LoadInvitationAsync(invitation.Id, host);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunAsync(null, s => s.AcceptAsync(token, Password, CancellationToken.None), host));

        // A fresh scope and context: nothing tracked from the failed attempt
        // (CustomerDataLifecycleTests.cs PurgeConversation_JoinsAmbientTransaction idiom).
        await using var verifyScope = host.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        // No user, so no role row either (AspNetUserRoles references AspNetUsers).
        Assert.Null(await FindUserAsync(verifyDb, email));
        // And directly: the role row the failure point saw inside the
        // transaction is gone for that exact user id.
        Assert.NotNull(capture.UserId);
        Assert.Equal(1, capture.RoleRowsInsideTransaction);
        Assert.Equal(0, await verifyDb.UserRoles.CountAsync(ur => ur.UserId == capture.UserId));
        Assert.DoesNotContain(await AuditRowsAsync(verifyDb, invitation.Id), a => a.EventType == AuditEventTypes.StaffInvitationAccepted);
        Assert.Empty(await TeamMemberJoinedAsync(verifyDb, businessId));
        var after = await verifyDb.StaffInvitations.IgnoreQueryFilters().AsNoTracking().SingleAsync(i => i.Id == invitation.Id);
        AssertUnchanged(before, after);
    }

    // T-I1c
    [Fact]
    public async Task Accept_WeakPassword_400_NothingChanged()
    {
        var (businessId, owner) = await SeedBusinessWithOwnerAsync();
        var email = NewEmail();
        var invitation = await CreateInvitationAsync(owner, email, RoleNames.Agent);
        var token = TokenFromLastEmailTo(email);
        var before = await LoadInvitationAsync(invitation.Id);

        var result = await RunAsync(null, s => s.AcceptAsync(token, "weak", CancellationToken.None));

        Assert.Equal(StaffInvitationOutcome.BadRequest, result.Outcome);
        Assert.NotEmpty(result.Errors!);

        await using var scope = _host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        AssertUnchanged(before, await db.StaffInvitations.IgnoreQueryFilters().AsNoTracking().SingleAsync(i => i.Id == invitation.Id));
        Assert.Null(await FindUserAsync(db, email));
        Assert.Empty(await TeamMemberJoinedAsync(db, businessId));
        Assert.DoesNotContain(await AuditRowsAsync(db, invitation.Id), a => a.EventType == AuditEventTypes.StaffInvitationAccepted);
    }

    // T-D3b
    [Fact]
    public async Task Accept_EmailRegisteredMeanwhile_409_NothingCreated()
    {
        var (businessId, owner) = await SeedBusinessWithOwnerAsync();
        var email = NewEmail();
        var invitation = await CreateInvitationAsync(owner, email, RoleNames.Agent);
        var token = TokenFromLastEmailTo(email);
        var before = await LoadInvitationAsync(invitation.Id);

        // Register first, over HTTP (BusinessApiTests.RegisterAsync shape).
        using var client = _host.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        var register = await client.PostAsJsonAsync(
            "/api/auth/register", new RegisterRequest("Registered meanwhile", "Europe/Copenhagen", email, Password));
        Assert.Equal(HttpStatusCode.OK, register.StatusCode);
        var registered = await register.Content.ReadFromJsonAsync<RegisterResponse>();
        _businessIdsToClean.Add(registered!.BusinessId);

        var result = await RunAsync(null, s => s.AcceptAsync(token, Password, CancellationToken.None));

        Assert.Equal(StaffInvitationOutcome.Conflict, result.Outcome);
        await using var scope = _host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        AssertUnchanged(before, await db.StaffInvitations.IgnoreQueryFilters().AsNoTracking().SingleAsync(i => i.Id == invitation.Id));
        var user = await FindUserAsync(db, email);
        Assert.Equal(registered.BusinessId, user!.BusinessId);
        Assert.Empty(await TeamMemberJoinedAsync(db, businessId));
        Assert.DoesNotContain(await AuditRowsAsync(db, invitation.Id), a => a.EventType == AuditEventTypes.StaffInvitationAccepted);
    }

    // T-D5 (T-NE-F11 is covered by this test)
    [Fact]
    public async Task Accept_NormalizedUserNameEqualsNormalizedKey()
    {
        var (_, owner) = await SeedBusinessWithOwnerAsync();
        var email = $"Staff-Invite-{Guid.NewGuid():N}@Example.Test";
        var invitation = await CreateInvitationAsync(owner, email, RoleNames.Agent);
        var token = TokenFromLastEmailTo(email);

        string normalizedKey;
        await using (var scope = _host.Services.CreateAsyncScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            normalizedKey = (await db.StaffInvitations.IgnoreQueryFilters().SingleAsync(i => i.Id == invitation.Id)).NormalizedKey;
            Assert.Equal(userManager.NormalizeName(email), normalizedKey);
        }

        var result = await RunAsync(null, s => s.AcceptAsync(token, Password, CancellationToken.None));
        Assert.Equal(StaffInvitationOutcome.Ok, result.Outcome);

        await using (var scope = _host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var user = await FindUserAsync(db, email);
            Assert.Equal(normalizedKey, user!.NormalizedUserName);
        }
    }

    // T-D6
    [Fact]
    public async Task Preview_ValidExpiredUnknownUsed()
    {
        var (businessId, owner) = await SeedBusinessWithOwnerAsync();
        var email = NewEmail();
        await CreateInvitationAsync(owner, email, RoleNames.Admin);
        var token = TokenFromLastEmailTo(email);

        var valid = await RunAsync(null, s => s.PreviewAsync(token, CancellationToken.None));
        Assert.Equal(StaffInvitationOutcome.Ok, valid.Outcome);
        Assert.Equal(new PreviewStaffInvitationResponse(await BusinessNameAsync(businessId), email, RoleNames.Admin), valid.Preview);

        var now = DateTimeOffset.UtcNow;
        var expiredToken = await SeedInvitationAsync(businessId, owner.UserId, NewEmail(), RoleNames.Agent, now.AddDays(-8), now.AddDays(-1));
        Assert.Equal(StaffInvitationOutcome.InvalidLink, (await RunAsync(null, s => s.PreviewAsync(expiredToken, CancellationToken.None))).Outcome);

        var (unknownToken, _) = NewTokenAndHash();
        Assert.Equal(StaffInvitationOutcome.InvalidLink, (await RunAsync(null, s => s.PreviewAsync(unknownToken, CancellationToken.None))).Outcome);

        Assert.Equal(StaffInvitationOutcome.Ok, (await RunAsync(null, s => s.AcceptAsync(token, Password, CancellationToken.None))).Outcome);
        Assert.Equal(StaffInvitationOutcome.InvalidLink, (await RunAsync(null, s => s.PreviewAsync(token, CancellationToken.None))).Outcome);
    }

    // T-D18 (design V8: anonymous, no tenant)
    [Fact]
    public async Task Accept_NoTenant_TeamMemberJoinedReachesOthersNotNewMember()
    {
        var (businessId, owner) = await SeedBusinessWithOwnerAsync();
        var activeAgent = await SeedStaffAsync(businessId, RoleNames.Agent);
        var inactiveAgent = await SeedStaffAsync(businessId, RoleNames.Agent, isActive: false);
        var (_, otherBusinessOwner) = await SeedBusinessWithOwnerAsync();
        var email = NewEmail();
        await CreateInvitationAsync(owner, email, RoleNames.Agent);
        var token = TokenFromLastEmailTo(email);

        var result = await RunAsync(null, s => s.AcceptAsync(token, Password, CancellationToken.None));
        Assert.Equal(StaffInvitationOutcome.Ok, result.Outcome);

        await using var scope = _host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var newMember = await FindUserAsync(db, email);
        var notification = Assert.Single(await TeamMemberJoinedAsync(db, businessId));
        var recipients = await db.NotificationRecipients.IgnoreQueryFilters()
            .Where(r => r.NotificationId == notification.Id)
            .Select(r => r.UserId)
            .ToListAsync();

        Assert.Equal(new[] { owner.UserId, activeAgent.UserId }.OrderBy(id => id), recipients.OrderBy(id => id));
        Assert.DoesNotContain(newMember!.Id, recipients);
        Assert.DoesNotContain(inactiveAgent.UserId, recipients);
        Assert.DoesNotContain(otherBusinessOwner.UserId, recipients);
    }

    // ── Harness (private, per-file doubles; CustomersApiTests.cs convention) ──

    /// <summary>Reads the static AsyncLocal, so a test sets the tenant per flow (design V8 "no tenant" = null).</summary>
    private sealed class AsyncLocalTenantAccessor : ICurrentTenantAccessor
    {
        public Guid? BusinessId => Tenant.Value;
    }

    /// <summary>What ThrowingAcceptFailurePoint saw inside the accept transaction, just before it threw.</summary>
    private sealed class AcceptFailureCapture
    {
        public Guid? UserId { get; set; }

        public int RoleRowsInsideTransaction { get; set; }
    }

    /// <summary>
    /// T-I1b: throws after the user and its role exist. Scoped, so it shares
    /// the accept operation's AppDbContext and open transaction: it records
    /// the new user's id from the change tracker and counts that user's
    /// AspNetUserRoles rows as the transaction sees them, so the test can
    /// check that exact id after the rollback.
    /// </summary>
    private sealed class ThrowingAcceptFailurePoint(AppDbContext db, AcceptFailureCapture capture) : IStaffInvitationAcceptFailurePoint
    {
        public async Task AfterUserCreatedAsync(CancellationToken ct)
        {
            var userId = db.ChangeTracker.Entries<ApplicationUser>().Single().Entity.Id;
            capture.UserId = userId;
            capture.RoleRowsInsideTransaction = await db.UserRoles.CountAsync(ur => ur.UserId == userId, ct);
            throw new InvalidOperationException("Simulated failure after the accepted user and role were created.");
        }
    }

    private WebApplicationFactory<Program> BuildHost(Action<IServiceCollection>? overrides = null) =>
        _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.AddSingleton<ICurrentTenantAccessor, AsyncLocalTenantAccessor>();
            overrides?.Invoke(s);
        }));

    private async Task<T> RunAsync<T>(Guid? tenant, Func<IStaffInvitationService, Task<T>> operation, WebApplicationFactory<Program>? host = null)
    {
        Tenant.Value = tenant;
        await using var scope = (host ?? _host).Services.CreateAsyncScope();
        return await operation(scope.ServiceProvider.GetRequiredService<IStaffInvitationService>());
    }

    private static string NewEmail() => $"staff-invite-{Guid.NewGuid():N}@example.test";

    private async Task<(Guid BusinessId, StaffCaller Owner)> SeedBusinessWithOwnerAsync(WebApplicationFactory<Program>? host = null)
    {
        var businessId = Guid.NewGuid();
        await using (var scope = (host ?? _host).Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Businesses.Add(new Business
            {
                Id = businessId,
                Name = $"Invite Co {businessId:N}",
                TimeZone = "Europe/Copenhagen",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        _businessIdsToClean.Add(businessId);
        return (businessId, await SeedStaffAsync(businessId, RoleNames.Owner, host: host));
    }

    /// <summary>BusinessApiTests.SeedStaffIntoBusinessAsync shape; returns the caller with its principal (G-1).</summary>
    private async Task<StaffCaller> SeedStaffAsync(Guid businessId, string role, bool isActive = true, WebApplicationFactory<Program>? host = null)
    {
        await using var scope = (host ?? _host).Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var email = NewEmail();
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            IsActive = isActive,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var createResult = await userManager.CreateAsync(user, Password);
        Assert.True(createResult.Succeeded, string.Join("; ", createResult.Errors.Select(e => e.Description)));
        var roleResult = await userManager.AddToRoleAsync(user, role);
        Assert.True(roleResult.Succeeded, string.Join("; ", roleResult.Errors.Select(e => e.Description)));
        return Caller(user.Id, businessId, role);
    }

    private static StaffCaller Caller(Guid userId, Guid businessId, string role)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(NivoraClaimTypes.BusinessId, businessId.ToString()),
            new Claim(ClaimTypes.Role, role),
        ], "staff-invite-test"));
        return new StaffCaller(userId, businessId, role == RoleNames.Owner, role == RoleNames.Admin, principal);
    }

    private async Task<StaffInvitationResponse> CreateInvitationAsync(StaffCaller caller, string email, string role, WebApplicationFactory<Program>? host = null)
    {
        var result = await RunAsync(caller.BusinessId, s => s.CreateAsync(caller, email, role, CancellationToken.None), host);
        Assert.Equal(StaffInvitationOutcome.Created, result.Outcome);
        return result.Invitation!;
    }

    private string TokenFromLastEmailTo(string email, WebApplicationFactory<Program>? host = null)
    {
        var sender = (FakeEmailSender)(host ?? _host).Services.GetRequiredService<IEmailSender>();
        var html = sender.SentEmails.Last(e => e.To == email).HtmlBody;
        var match = Regex.Match(html, "#token=([A-Za-z0-9_-]{43})\"");
        Assert.True(match.Success, "no invitation link in the email");
        return match.Groups[1].Value;
    }

    private static (string Token, byte[] Hash) NewTokenAndHash()
    {
        var raw = RandomNumberGenerator.GetBytes(32);
        return (Base64Url.EncodeToString(raw), SHA256.HashData(raw));
    }

    /// <summary>Seeds a row directly with a known token; every slice 2 CHECK holds (ExpiresAt > CreatedAt, LastSentAt = CreatedAt).</summary>
    private async Task<string> SeedInvitationAsync(Guid businessId, Guid invitedBy, string email, string role, DateTimeOffset createdAt, DateTimeOffset expiresAt)
    {
        var (token, hash) = NewTokenAndHash();
        await using var scope = _host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        db.StaffInvitations.Add(new StaffInvitation
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            Email = email,
            NormalizedKey = userManager.NormalizeName(email),
            Role = role,
            TokenHash = hash,
            InvitedByUserId = invitedBy,
            CreatedAt = createdAt,
            LastSentAt = createdAt,
            ExpiresAt = expiresAt,
        });
        await db.SaveChangesAsync();
        return token;
    }

    private async Task<StaffInvitation> LoadInvitationAsync(Guid id, WebApplicationFactory<Program>? host = null)
    {
        await using var scope = (host ?? _host).Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.StaffInvitations.IgnoreQueryFilters().AsNoTracking().SingleAsync(i => i.Id == id);
    }

    private static void AssertUnchanged(StaffInvitation before, StaffInvitation after)
    {
        Assert.Equal(before.TokenHash, after.TokenHash);
        Assert.Equal(before.ExpiresAt, after.ExpiresAt);
        Assert.Equal(before.LastSentAt, after.LastSentAt);
        Assert.Equal(before.Email, after.Email);
        Assert.Equal(before.Role, after.Role);
    }

    private async Task<string> BusinessNameAsync(Guid businessId)
    {
        await using var scope = _host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return (await db.Businesses.SingleAsync(b => b.Id == businessId)).Name;
    }

    /// <summary>Accept and Register both store the email as entered as UserName.</summary>
    private static async Task<ApplicationUser?> FindUserAsync(AppDbContext db, string email) =>
        await db.Users.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(u => u.UserName == email);

    private static async Task<string[]> RoleNamesOfAsync(AppDbContext db, Guid userId) =>
        await (from ur in db.UserRoles
               join r in db.Roles on ur.RoleId equals r.Id
               where ur.UserId == userId
               select r.Name!).ToArrayAsync();

    private static async Task<List<AuditLog>> AuditRowsAsync(AppDbContext db, Guid invitationId)
    {
        var entityId = invitationId.ToString();
        return await db.AuditLogs.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.EntityType == AuditEntityTypes.StaffInvitation && a.EntityId == entityId)
            .ToListAsync();
    }

    private static async Task<List<Notification>> TeamMemberJoinedAsync(AppDbContext db, Guid businessId) =>
        await db.Notifications.IgnoreQueryFilters().AsNoTracking()
            .Where(n => n.BusinessId == businessId && n.Type == NotificationTypes.TeamMemberJoined)
            .ToListAsync();
}
