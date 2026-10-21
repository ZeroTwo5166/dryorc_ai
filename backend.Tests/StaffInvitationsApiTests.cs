using System.Buffers.Text;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace backend.Tests;

/// <summary>
/// Staff invite slice 5b, service level: create, resend and revoke
/// (docs/handoffs/staff-invite-design.md Sections 4-6 and 11; Section T rows
/// T-I2b, T-I8a, T-R1, T-D3a, T-D8, T-D9, T-D10, T-D11, T-D19b, T-D19c).
/// There is no controller yet, so every test calls IStaffInvitationService
/// from a scope of the test host; slice 5c adds the HTTP tests to this class.
/// The tenant comes from a private AsyncLocal accessor: staff calls set it to
/// the caller's business, accept leaves it null (design V8). Tokens are read
/// from the link in the host's FakeEmailSender (D8).
///
/// T-D19b: the seven-case Theory checks StaffInvitationLinkBuilder directly
/// through a private IConfiguration double and a private IHostEnvironment
/// double. The service-level case uses one extra host whose only setting is
/// an empty Frontend:BaseUrl, the one exemption for this file in
/// TestDatabaseUsageTests K7d (erratum 2, E2-1).
/// </summary>
public class StaffInvitationsApiTests : IAsyncLifetime
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

    // T-I2b
    [Fact]
    public async Task Writes_CarryRowBusinessId()
    {
        var (businessId, owner) = await SeedBusinessWithOwnerAsync();
        var email = NewEmail();
        var invitation = await CreateInvitationAsync(owner, email, RoleNames.Agent);
        var token = TokenFromLastEmailTo(email);

        // A second, real business: a write that carried another business's id
        // would still satisfy every foreign key, so only the BusinessId
        // assertions below can catch it (owner ruling, supplementary break
        // T-I2b-S).
        var otherBusinessId = Guid.NewGuid();
        await using (var seedScope = _host.Services.CreateAsyncScope())
        {
            var seedDb = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            seedDb.Businesses.Add(new Business
            {
                Id = otherBusinessId,
                Name = $"T-I2b-S other {otherBusinessId:N}",
                TimeZone = "Europe/Copenhagen",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await seedDb.SaveChangesAsync();
        }

        _businessIdsToClean.Add(otherBusinessId);

        var result = await RunAsync(null, s => s.AcceptAsync(token, Password, CancellationToken.None));
        Assert.Equal(StaffInvitationOutcome.Ok, result.Outcome);

        await using var scope = _host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await db.Users.IgnoreQueryFilters().SingleAsync(u => u.UserName == email);
        Assert.Equal(businessId, user.BusinessId);
        var entityId = invitation.Id.ToString();
        var audit = await db.AuditLogs.IgnoreQueryFilters()
            .SingleAsync(a => a.EntityId == entityId && a.EventType == AuditEventTypes.StaffInvitationAccepted);
        Assert.Equal(businessId, audit.BusinessId);
        var notification = await db.Notifications.IgnoreQueryFilters()
            .SingleAsync(n => n.BusinessId == businessId && n.Type == NotificationTypes.TeamMemberJoined);
        Assert.Equal(businessId, notification.BusinessId);
        Assert.All(
            await db.NotificationRecipients.IgnoreQueryFilters().Where(r => r.NotificationId == notification.Id).ToListAsync(),
            r => Assert.Equal(businessId, r.BusinessId));
    }

    // T-I8a
    [Fact]
    public async Task Resend_OldTokenDead()
    {
        var (_, owner) = await SeedBusinessWithOwnerAsync();
        var email = NewEmail();
        var invitation = await CreateInvitationAsync(owner, email, RoleNames.Agent);
        var oldToken = TokenFromLastEmailTo(email);
        await MoveBackAsync(invitation.Id, TimeSpan.FromSeconds(61));

        var resend = await RunAsync(owner.BusinessId, s => s.ResendAsync(owner, invitation.Id, CancellationToken.None));
        Assert.Equal(StaffInvitationOutcome.Ok, resend.Outcome);
        var newToken = TokenFromLastEmailTo(email);
        Assert.NotEqual(oldToken, newToken);

        Assert.Equal(StaffInvitationOutcome.InvalidLink, (await RunAsync(null, s => s.PreviewAsync(oldToken, CancellationToken.None))).Outcome);
        Assert.Equal(StaffInvitationOutcome.Ok, (await RunAsync(null, s => s.PreviewAsync(newToken, CancellationToken.None))).Outcome);
    }

    // T-R1
    [Fact]
    public async Task Resend_ResponseShowsWrittenValues()
    {
        var (_, owner) = await SeedBusinessWithOwnerAsync();
        var invitation = await CreateInvitationAsync(owner, NewEmail(), RoleNames.Agent);
        await MoveBackAsync(invitation.Id, TimeSpan.FromSeconds(61));
        var before = await LoadInvitationAsync(invitation.Id);

        var resend = await RunAsync(owner.BusinessId, s => s.ResendAsync(owner, invitation.Id, CancellationToken.None));

        Assert.Equal(StaffInvitationOutcome.Ok, resend.Outcome);
        var response = resend.Invitation!;
        var after = await LoadInvitationAsync(invitation.Id);
        Assert.Equal(TruncateToMicroseconds(after.ExpiresAt), TruncateToMicroseconds(response.ExpiresAt));
        Assert.Equal(TruncateToMicroseconds(after.LastSentAt), TruncateToMicroseconds(response.LastSentAt));
        Assert.NotEqual(TruncateToMicroseconds(before.ExpiresAt), TruncateToMicroseconds(response.ExpiresAt));
        Assert.NotEqual(TruncateToMicroseconds(before.LastSentAt), TruncateToMicroseconds(response.LastSentAt));
        Assert.Equal("Pending", response.Status);
    }

    // T-D3a (1 of 4)
    [Fact]
    public async Task Create_SameBusinessMember_409()
    {
        var (businessId, owner) = await SeedBusinessWithOwnerAsync();
        var memberEmail = NewEmail();
        await SeedStaffAsync(businessId, RoleNames.Agent, memberEmail);

        var result = await RunAsync(owner.BusinessId, s => s.CreateAsync(owner, memberEmail, RoleNames.Agent, CancellationToken.None));

        Assert.Equal(StaffInvitationOutcome.Conflict, result.Outcome);
        Assert.Equal(0, await CountInvitationsAsync(businessId, memberEmail));
        Assert.Empty(EmailsTo(memberEmail));
    }

    // T-D3a (2 of 4)
    [Fact]
    public async Task Create_ExistingRow_409()
    {
        var (businessId, owner) = await SeedBusinessWithOwnerAsync();
        var email = NewEmail();
        await CreateInvitationAsync(owner, email, RoleNames.Agent);

        var again = await RunAsync(owner.BusinessId, s => s.CreateAsync(owner, email, RoleNames.Agent, CancellationToken.None));

        Assert.Equal(StaffInvitationOutcome.Conflict, again.Outcome);
        Assert.Equal(1, await CountInvitationsAsync(businessId, email));
        Assert.Single(EmailsTo(email));
    }

    // T-D3a (3 of 4)
    [Fact]
    public async Task CrossBusinessStaff_AlreadyInUseEmail_NoLink()
    {
        var (otherBusinessId, _) = await SeedBusinessWithOwnerAsync();
        var email = NewEmail();
        await SeedStaffAsync(otherBusinessId, RoleNames.Agent, email);
        var (businessId, owner) = await SeedBusinessWithOwnerAsync();

        var result = await RunAsync(owner.BusinessId, s => s.CreateAsync(owner, email, RoleNames.Agent, CancellationToken.None));

        Assert.Equal(StaffInvitationOutcome.Created, result.Outcome);
        Assert.Equal(1, await CountInvitationsAsync(businessId, email));
        var sent = Assert.Single(EmailsTo(email));
        Assert.Equal("Your Nivora invitation", sent.Subject);
        Assert.DoesNotContain("#token=", sent.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("accept-invite", sent.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("href", sent.HtmlBody, StringComparison.Ordinal);
    }

    // T-D3a (4 of 4)
    [Fact]
    public async Task Unknown_JoinLinkEmail()
    {
        var (businessId, owner) = await SeedBusinessWithOwnerAsync();
        var email = NewEmail();

        var result = await RunAsync(owner.BusinessId, s => s.CreateAsync(owner, email, RoleNames.Agent, CancellationToken.None));

        Assert.Equal(StaffInvitationOutcome.Created, result.Outcome);
        Assert.Equal(1, await CountInvitationsAsync(businessId, email));
        var sent = Assert.Single(EmailsTo(email));
        Assert.Equal($"You're invited to join {await BusinessNameAsync(businessId)} on Nivora", sent.Subject);
        Assert.Contains("/accept-invite#token=", sent.HtmlBody, StringComparison.Ordinal);
    }

    // T-D8
    [Fact]
    public async Task Create_EmailFails_RowPersisted_ResendRecovers()
    {
        var sender = new FailOnceEmailSender();
        var host = BuildHost(s => s.AddSingleton<IEmailSender>(sender));
        var (businessId, owner) = await SeedBusinessWithOwnerAsync(host);
        var email = NewEmail();

        var created = await RunAsync(owner.BusinessId, s => s.CreateAsync(owner, email, RoleNames.Agent, CancellationToken.None), host);

        Assert.Equal(StaffInvitationOutcome.Created, created.Outcome);
        Assert.Equal(1, sender.Attempts);
        Assert.Empty(sender.SentEmails);
        Assert.Equal(1, await CountInvitationsAsync(businessId, email));

        await MoveBackAsync(created.Invitation!.Id, TimeSpan.FromSeconds(61));
        var resent = await RunAsync(owner.BusinessId, s => s.ResendAsync(owner, created.Invitation.Id, CancellationToken.None), host);

        Assert.Equal(StaffInvitationOutcome.Ok, resent.Outcome);
        var sent = Assert.Single(sender.SentEmails);
        Assert.Equal(email, sent.To);
        Assert.Contains("/accept-invite#token=", sent.HtmlBody, StringComparison.Ordinal);
    }

    // T-D9
    [Fact]
    public async Task Revoke_DeletesRow_AuditOnly()
    {
        var (_, owner) = await SeedBusinessWithOwnerAsync();
        var email = NewEmail();
        var invitation = await CreateInvitationAsync(owner, email, RoleNames.Agent);
        Assert.Single(EmailsTo(email));

        var result = await RunAsync(owner.BusinessId, s => s.RevokeAsync(owner, invitation.Id, CancellationToken.None));

        Assert.Equal(StaffInvitationOutcome.NoContent, result.Outcome);
        await using var scope = _host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.StaffInvitations.IgnoreQueryFilters().AnyAsync(i => i.Id == invitation.Id));
        var entityId = invitation.Id.ToString();
        var revoked = await db.AuditLogs.IgnoreQueryFilters()
            .SingleAsync(a => a.EntityId == entityId && a.EventType == AuditEventTypes.StaffInvitationRevoked);
        Assert.Equal(StaffInvitationAuditPayload.Build(RoleNames.Agent, "Pending"), revoked.OldValue);
        Assert.Equal(StaffInvitationAuditPayload.Build(RoleNames.Agent, "Revoked"), revoked.NewValue);
        Assert.Equal(owner.UserId, revoked.ChangedByUserId);
        Assert.Single(EmailsTo(email));
    }

    // T-D10
    [Fact]
    public async Task ResendRevoke_MissingOrOtherBusiness_404()
    {
        var (businessA, ownerA) = await SeedBusinessWithOwnerAsync();
        var (businessB, ownerB) = await SeedBusinessWithOwnerAsync();
        var invitation = await CreateInvitationAsync(ownerA, NewEmail(), RoleNames.Agent);
        await MoveBackAsync(invitation.Id, TimeSpan.FromSeconds(61));
        var before = await LoadInvitationAsync(invitation.Id);

        // (a) unknown id.
        var unknown = Guid.NewGuid();
        Assert.Equal(StaffInvitationOutcome.NotFound, (await RunAsync(businessB, s => s.ResendAsync(ownerB, unknown, CancellationToken.None))).Outcome);
        Assert.Equal(StaffInvitationOutcome.NotFound, (await RunAsync(businessB, s => s.RevokeAsync(ownerB, unknown, CancellationToken.None))).Outcome);

        // (b) business A's invitation; caller and tenant both B.
        Assert.Equal(StaffInvitationOutcome.NotFound, (await RunAsync(businessB, s => s.ResendAsync(ownerB, invitation.Id, CancellationToken.None))).Outcome);
        Assert.Equal(StaffInvitationOutcome.NotFound, (await RunAsync(businessB, s => s.RevokeAsync(ownerB, invitation.Id, CancellationToken.None))).Outcome);
        AssertUnchanged(before, await LoadInvitationAsync(invitation.Id));

        // (c) tenant A (the row's business) while the caller is B: only the
        // explicit BusinessId predicates protect the row here.
        Assert.Equal(StaffInvitationOutcome.NotFound, (await RunAsync(businessA, s => s.ResendAsync(ownerB, invitation.Id, CancellationToken.None))).Outcome);
        Assert.Equal(StaffInvitationOutcome.NotFound, (await RunAsync(businessA, s => s.RevokeAsync(ownerB, invitation.Id, CancellationToken.None))).Outcome);
        AssertUnchanged(before, await LoadInvitationAsync(invitation.Id));
    }

    // T-D11
    [Fact]
    public async Task Audit_PayloadAllowlist()
    {
        var (businessId, owner) = await SeedBusinessWithOwnerAsync();
        var agentEmail = NewEmail();
        var agentInvitation = await CreateInvitationAsync(owner, agentEmail, RoleNames.Agent);
        await MoveBackAsync(agentInvitation.Id, TimeSpan.FromSeconds(61));
        Assert.Equal(StaffInvitationOutcome.Ok, (await RunAsync(businessId, s => s.ResendAsync(owner, agentInvitation.Id, CancellationToken.None))).Outcome);
        Assert.Equal(StaffInvitationOutcome.NoContent, (await RunAsync(businessId, s => s.RevokeAsync(owner, agentInvitation.Id, CancellationToken.None))).Outcome);

        var adminEmail = NewEmail();
        var adminInvitation = await CreateInvitationAsync(owner, adminEmail, RoleNames.Admin);
        var adminToken = TokenFromLastEmailTo(adminEmail);
        Assert.Equal(StaffInvitationOutcome.Ok, (await RunAsync(null, s => s.AcceptAsync(adminToken, Password, CancellationToken.None))).Outcome);

        await using var scope = _host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var newUserId = (await db.Users.IgnoreQueryFilters().SingleAsync(u => u.UserName == adminEmail)).Id;
        var ids = new[] { agentInvitation.Id.ToString(), adminInvitation.Id.ToString() };
        var rows = await db.AuditLogs.IgnoreQueryFilters()
            .Where(a => a.EntityType == AuditEntityTypes.StaffInvitation && ids.Contains(a.EntityId))
            .ToListAsync();

        Assert.Equal(5, rows.Count);
        foreach (var row in rows)
        {
            Assert.Equal(businessId, row.BusinessId);
            Assert.Equal(ActorType.NivoraBusinessStaff, row.Actor);
            Assert.Equal(row.EventType == AuditEventTypes.StaffInvitationAccepted ? newUserId : owner.UserId, row.ChangedByUserId);
            foreach (var value in new[] { row.OldValue, row.NewValue })
            {
                if (value is null)
                {
                    continue;
                }

                // Exactly the two allowlisted properties; never the entity, email or token.
                using var json = JsonDocument.Parse(value);
                Assert.Equal(new[] { "role", "status" }, json.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
            }
        }

        AssertPayloads(rows, agentInvitation.Id, AuditEventTypes.StaffInvitationCreated, null, (RoleNames.Agent, "Pending"));
        AssertPayloads(rows, agentInvitation.Id, AuditEventTypes.StaffInvitationResent, (RoleNames.Agent, "Pending"), (RoleNames.Agent, "Pending"));
        AssertPayloads(rows, agentInvitation.Id, AuditEventTypes.StaffInvitationRevoked, (RoleNames.Agent, "Pending"), (RoleNames.Agent, "Revoked"));
        AssertPayloads(rows, adminInvitation.Id, AuditEventTypes.StaffInvitationCreated, null, (RoleNames.Admin, "Pending"));
        AssertPayloads(rows, adminInvitation.Id, AuditEventTypes.StaffInvitationAccepted, (RoleNames.Admin, "Pending"), (RoleNames.Admin, "Accepted"));
    }

    // T-D19b, link-builder level: the seven cases of design Section T.
    [Theory]
    [InlineData(null, "Development")]                                // missing
    [InlineData("/accept", "Development")]                           // relative
    [InlineData("https://example.test/?a=1", "Development")]         // query
    [InlineData("https://example.test/#x", "Development")]           // fragment
    [InlineData("https://user:secret@example.test/", "Development")] // userinfo
    [InlineData("http://example.test/", "Development")]              // http, not local
    [InlineData("http://localhost:3000", "Production")]              // http localhost outside Development
    public void BaseUrl_Invalid_500_NoMutation(string? baseUrl, string environmentName)
    {
        var thrown = Assert.Throws<InvalidOperationException>(() => StaffInvitationLinkBuilder.GetValidatedBaseUri(
            new BaseUrlOnlyConfiguration(baseUrl), new FixedHostEnvironment(environmentName)));

        Assert.Contains("Frontend:BaseUrl", thrown.Message, StringComparison.Ordinal);
        if (baseUrl is not null)
        {
            Assert.DoesNotContain(baseUrl, thrown.Message, StringComparison.Ordinal);
        }
    }

    // T-D19b, service level: an empty Frontend:BaseUrl fails create and
    // resend before any mutation.
    [Fact]
    public async Task BaseUrl_Invalid_500_NoMutation_ServiceLevel()
    {
        var (businessId, owner) = await SeedBusinessWithOwnerAsync();
        var existing = await CreateInvitationAsync(owner, NewEmail(), RoleNames.Agent);
        await MoveBackAsync(existing.Id, TimeSpan.FromSeconds(61));
        var before = await LoadInvitationAsync(existing.Id);

        var emptyBaseUrlHost = _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Frontend:BaseUrl", "");
            b.ConfigureTestServices(s => s.AddSingleton<ICurrentTenantAccessor, AsyncLocalTenantAccessor>());
        });
        var email = NewEmail();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunAsync(businessId, s => s.CreateAsync(owner, email, RoleNames.Agent, CancellationToken.None), emptyBaseUrlHost));
        Assert.Equal(0, await CountInvitationsAsync(businessId, email));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunAsync(businessId, s => s.ResendAsync(owner, existing.Id, CancellationToken.None), emptyBaseUrlHost));
        AssertUnchanged(before, await LoadInvitationAsync(existing.Id));
    }

    // T-D19c
    [Fact]
    public async Task Link_IsFragmentOnly()
    {
        var (_, owner) = await SeedBusinessWithOwnerAsync();
        var email = NewEmail();
        await CreateInvitationAsync(owner, email, RoleNames.Agent);

        var html = Assert.Single(EmailsTo(email)).HtmlBody;
        var href = Regex.Match(html, "href=\"([^\"]*)\"");
        Assert.True(href.Success);
        var link = WebUtility.HtmlDecode(href.Groups[1].Value);
        var baseUrl = _host.Services.GetRequiredService<IConfiguration>()["Frontend:BaseUrl"]!;

        Assert.Matches("^" + Regex.Escape(baseUrl.TrimEnd('/') + "/accept-invite#token=") + "[A-Za-z0-9_-]{43}$", link);
        Assert.DoesNotContain("?", link, StringComparison.Ordinal);
    }

    // ── Harness (private, per-file doubles; CustomersApiTests.cs convention) ──

    /// <summary>Reads the static AsyncLocal, so a test sets the tenant per flow (design V8 "no tenant" = null).</summary>
    private sealed class AsyncLocalTenantAccessor : ICurrentTenantAccessor
    {
        public Guid? BusinessId => Tenant.Value;
    }

    /// <summary>T-D8: the first send throws, later sends are recorded.</summary>
    private sealed class FailOnceEmailSender : IEmailSender
    {
        private int _attempts;

        public int Attempts => Volatile.Read(ref _attempts);

        public List<(string To, string Subject, string HtmlBody)> SentEmails { get; } = [];

        public Task SendEmailAsync(string to, string subject, string htmlBody)
        {
            if (Interlocked.Increment(ref _attempts) == 1)
            {
                throw new InvalidOperationException("Simulated delivery failure for test.");
            }

            SentEmails.Add((to, subject, htmlBody));
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// T-D19b (erratum 2, E2-2): returns the case value for Frontend:BaseUrl
    /// and null for every other key; nothing else is supported.
    /// </summary>
    private sealed class BaseUrlOnlyConfiguration(string? baseUrl) : IConfiguration
    {
        public string? this[string key]
        {
            get => key == "Frontend:BaseUrl" ? baseUrl : null;
            set => throw new NotSupportedException();
        }

        public IConfigurationSection GetSection(string key) => throw new NotSupportedException();

        public IEnumerable<IConfigurationSection> GetChildren() => throw new NotSupportedException();

        public IChangeToken GetReloadToken() => throw new NotSupportedException();
    }

    /// <summary>T-D19b: "outside Development" without a host.</summary>
    private sealed class FixedHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "backend";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
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
    private async Task<StaffCaller> SeedStaffAsync(Guid businessId, string role, string? email = null, WebApplicationFactory<Program>? host = null)
    {
        await using var scope = (host ?? _host).Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        email ??= NewEmail();
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

    private async Task<StaffInvitationResponse> CreateInvitationAsync(StaffCaller caller, string email, string role)
    {
        var result = await RunAsync(caller.BusinessId, s => s.CreateAsync(caller, email, role, CancellationToken.None));
        Assert.Equal(StaffInvitationOutcome.Created, result.Outcome);
        return result.Invitation!;
    }

    private List<(string To, string Subject, string HtmlBody)> EmailsTo(string email) =>
        ((FakeEmailSender)_host.Services.GetRequiredService<IEmailSender>()).SentEmails.Where(e => e.To == email).ToList();

    private string TokenFromLastEmailTo(string email)
    {
        var html = EmailsTo(email).Last().HtmlBody;
        var match = Regex.Match(html, "#token=([A-Za-z0-9_-]{43})\"");
        Assert.True(match.Success, "no invitation link in the email");
        return match.Groups[1].Value;
    }

    /// <summary>
    /// Moves CreatedAt and LastSentAt back together, so the resend cooldown
    /// has passed and CK_StaffInvitations_LastSentAt_Range still holds.
    /// </summary>
    private async Task MoveBackAsync(Guid id, TimeSpan by)
    {
        await using var scope = _host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var updated = await db.StaffInvitations.IgnoreQueryFilters()
            .Where(i => i.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(i => i.CreatedAt, i => i.CreatedAt - by)
                .SetProperty(i => i.LastSentAt, i => i.LastSentAt - by));
        Assert.Equal(1, updated);
    }

    private async Task<StaffInvitation> LoadInvitationAsync(Guid id)
    {
        await using var scope = _host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.StaffInvitations.IgnoreQueryFilters().AsNoTracking().SingleAsync(i => i.Id == id);
    }

    private async Task<int> CountInvitationsAsync(Guid businessId, string email)
    {
        await using var scope = _host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.StaffInvitations.IgnoreQueryFilters().CountAsync(i => i.BusinessId == businessId && i.Email == email);
    }

    private async Task<string> BusinessNameAsync(Guid businessId)
    {
        await using var scope = _host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return (await db.Businesses.SingleAsync(b => b.Id == businessId)).Name;
    }

    private static void AssertUnchanged(StaffInvitation before, StaffInvitation after)
    {
        Assert.Equal(before.TokenHash, after.TokenHash);
        Assert.Equal(before.ExpiresAt, after.ExpiresAt);
        Assert.Equal(before.LastSentAt, after.LastSentAt);
    }

    private static void AssertPayloads(
        List<AuditLog> rows, Guid invitationId, string eventType, (string Role, string Status)? oldValue, (string Role, string Status) newValue)
    {
        var entityId = invitationId.ToString();
        var row = Assert.Single(rows, r => r.EntityId == entityId && r.EventType == eventType);
        Assert.Equal(oldValue is null ? null : StaffInvitationAuditPayload.Build(oldValue.Value.Role, oldValue.Value.Status), row.OldValue);
        Assert.Equal(StaffInvitationAuditPayload.Build(newValue.Role, newValue.Status), row.NewValue);
    }

    /// <summary>
    /// PostgreSQL stores microseconds, .NET ticks are 100 ns: both sides are
    /// truncated before comparing (NotificationApiTests.cs:108-120).
    /// </summary>
    private static DateTimeOffset TruncateToMicroseconds(DateTimeOffset value) =>
        new(value.Ticks - (value.Ticks % 10), value.Offset);
}
