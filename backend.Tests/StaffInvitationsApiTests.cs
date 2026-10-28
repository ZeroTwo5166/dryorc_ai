using System.Buffers.Text;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using backend.Auth;
using backend.Constants;
using backend.Controllers;
using backend.Data;
using backend.Email;
using backend.Enums;
using backend.Interfaces;
using backend.Models;
using backend.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace backend.Tests;

/// <summary>
/// Staff invite slices 5b and 5c: create, resend and revoke
/// (docs/handoffs/staff-invite-design.md Sections 4-6, 8, 9 and 11; Section T
/// rows T-I2b, T-I8a, T-R1, T-D3a, T-D8, T-D9, T-D10, T-D11, T-D19b, T-D19c
/// at service level, and T-I3, T-I4, T-I4b, T-I6, T-D12 over HTTP).
/// The service-level tests call IStaffInvitationService from a scope of
/// _host, where the tenant comes from a private AsyncLocal accessor: staff
/// calls set it to the caller's business, accept leaves it null (design V8).
/// The HTTP tests use the root factory (or a host derived from it without
/// that accessor), so the tenant comes from the signed-in cookie as in
/// production. Tokens are read from the link in the host's FakeEmailSender
/// (D8).
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

    // T-I3 (DES :619; implementation design G-4): every token the invitee
    // was emailed, by the create and by the resend, is absent from the
    // database, the audit and notification rows, every response body and
    // the captured log output.
    [Fact]
    public async Task Token_NeverPersistedLoggedAuditedOrReturned()
    {
        var logs = new CapturingLoggerProvider();
        var host = CapturingLoggerProvider.Attach(_factory, logs);
        var (businessId, owner) = await SeedBusinessWithLoggedInOwnerAsync(host);
        var email = NewEmail();
        var bodies = new List<string>();
        var rowSnapshots = new List<string>();

        var created = await SendAsync(host, HttpMethod.Post, "/api/staff-invitations", owner.Cookie,
            new CreateStaffInvitationRequest(email, RoleNames.Agent));
        Assert.Equal(HttpStatusCode.Created, created.Status);
        bodies.Add(created.Body);
        var invitationId = ReadInvitation(created.Body).Id;
        rowSnapshots.Add(await InvitationColumnsAsync(invitationId, host));

        await MoveBackAsync(invitationId, TimeSpan.FromSeconds(61), host);
        var resent = await SendAsync(host, HttpMethod.Post, $"/api/staff-invitations/{invitationId}/resend", owner.Cookie);
        Assert.Equal(HttpStatusCode.OK, resent.Status);
        bodies.Add(resent.Body);
        rowSnapshots.Add(await InvitationColumnsAsync(invitationId, host));

        // Every token in every email to the invitee: the create's and the resend's.
        var tokens = EmailsTo(email, host)
            .SelectMany(e => Regex.Matches(e.HtmlBody, "#token=([A-Za-z0-9_-]{43})").Select(m => m.Groups[1].Value))
            .Distinct()
            .ToList();
        Assert.Equal(2, tokens.Count);
        var resendToken = tokens[1];

        var preview = await SendAsync(host, HttpMethod.Post, "/api/staff-invitations/preview", null,
            new PreviewStaffInvitationRequest(resendToken));
        Assert.Equal(HttpStatusCode.OK, preview.Status);
        bodies.Add(preview.Body);
        var accept = await SendAsync(host, HttpMethod.Post, "/api/staff-invitations/accept", null,
            new AcceptStaffInvitationRequest(resendToken, Password));
        Assert.Equal(HttpStatusCode.OK, accept.Status);
        bodies.Add(accept.Body);

        string auditValues;
        string notifications;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var entityId = invitationId.ToString();
            var audits = await db.AuditLogs.IgnoreQueryFilters().AsNoTracking()
                .Where(a => a.EntityType == AuditEntityTypes.StaffInvitation && a.EntityId == entityId)
                .Select(a => new { a.OldValue, a.NewValue })
                .ToListAsync();
            Assert.Equal(3, audits.Count);
            auditValues = JsonSerializer.Serialize(audits);
            notifications = JsonSerializer.Serialize(await db.Notifications.IgnoreQueryFilters().AsNoTracking()
                .Where(n => n.BusinessId == businessId)
                .ToListAsync());
        }

        // Positive control: the provider sees an Information line of the
        // service's own category, so a missed capture cannot pass as "no leak".
        var marker = Guid.NewGuid().ToString("N");
        host.Services.GetRequiredService<ILogger<StaffInvitationService>>().LogInformation("T-I3 positive control {Marker}", marker);
        var captured = logs.Entries;
        Assert.Single(captured, e =>
            e.Category == typeof(StaffInvitationService).FullName
            && e.Level == LogLevel.Information
            && e.Message == $"T-I3 positive control {marker}");

        foreach (var token in tokens)
        {
            Assert.All(rowSnapshots, row => Assert.DoesNotContain(token, row, StringComparison.Ordinal));
            Assert.DoesNotContain(token, auditValues, StringComparison.Ordinal);
            Assert.DoesNotContain(token, notifications, StringComparison.Ordinal);
            Assert.All(bodies, body => Assert.DoesNotContain(token, body, StringComparison.Ordinal));
        }

        var invokerTraceForPreview = captured.Any(e =>
            e.Category.Contains("ControllerActionInvoker", StringComparison.Ordinal)
            && e.Level == LogLevel.Trace
            && e.Message.Contains("PreviewStaffInvitationRequest", StringComparison.Ordinal));
        Assert.True(invokerTraceForPreview,
            "positive control: no ControllerActionInvoker Trace entry holds PreviewStaffInvitationRequest");

        LogCaptureAssert.NoneContain(
            captured,
            [.. tokens.Select((value, n) => new DeniedValue($"T-I3 token {n}", value)),
             new DeniedValue("T-I3 Password constant", Password)]);
    }

    // T-I4: every 403 cell of design Section 9; nothing changes.
    [Theory]
    [InlineData(RoleNames.Owner, "create", RoleNames.Owner)]
    [InlineData(RoleNames.Admin, "create", RoleNames.Admin)]
    [InlineData(RoleNames.Admin, "create", RoleNames.Owner)]
    [InlineData(RoleNames.Admin, "resend", RoleNames.Admin)]
    [InlineData(RoleNames.Admin, "revoke", RoleNames.Admin)]
    [InlineData(RoleNames.Agent, "create", RoleNames.Admin)]
    [InlineData(RoleNames.Agent, "create", RoleNames.Agent)]
    [InlineData(RoleNames.Agent, "create", RoleNames.Owner)]
    [InlineData(RoleNames.Agent, "resend", RoleNames.Admin)]
    [InlineData(RoleNames.Agent, "resend", RoleNames.Agent)]
    [InlineData(RoleNames.Agent, "revoke", RoleNames.Admin)]
    [InlineData(RoleNames.Agent, "revoke", RoleNames.Agent)]
    [InlineData(RoleNames.Agent, "list", null)]
    public async Task RoleMatrix_ForbiddenCombination(string callerRole, string operation, string? targetRole)
    {
        var cell = await RunMatrixCellAsync(callerRole, operation, targetRole);

        Assert.Equal(HttpStatusCode.Forbidden, cell.Status);
        Assert.Equal(cell.InvitationsBefore, await CountBusinessInvitationsAsync(cell.BusinessId));
        Assert.Equal(cell.AuditsBefore, await CountBusinessAuditsAsync(cell.BusinessId));
        if (cell.Target is not null)
        {
            AssertUnchanged(cell.Target, await LoadInvitationAsync(cell.Target.Id, _factory));
        }
    }

    // T-I4b: the allowed cells of design Section 9.
    [Theory]
    [InlineData(RoleNames.Owner, "create", RoleNames.Admin)]
    [InlineData(RoleNames.Owner, "create", RoleNames.Agent)]
    [InlineData(RoleNames.Owner, "resend", RoleNames.Admin)]
    [InlineData(RoleNames.Owner, "resend", RoleNames.Agent)]
    [InlineData(RoleNames.Owner, "revoke", RoleNames.Admin)]
    [InlineData(RoleNames.Owner, "revoke", RoleNames.Agent)]
    [InlineData(RoleNames.Owner, "list", null)]
    [InlineData(RoleNames.Admin, "create", RoleNames.Agent)]
    [InlineData(RoleNames.Admin, "resend", RoleNames.Agent)]
    [InlineData(RoleNames.Admin, "revoke", RoleNames.Agent)]
    [InlineData(RoleNames.Admin, "list", null)]
    public async Task RoleMatrix_AllowedCombination(string callerRole, string operation, string? targetRole)
    {
        var cell = await RunMatrixCellAsync(callerRole, operation, targetRole);

        var expected = operation switch
        {
            "create" => HttpStatusCode.Created,
            "revoke" => HttpStatusCode.NoContent,
            _ => HttpStatusCode.OK,
        };
        Assert.Equal(expected, cell.Status);
    }

    // T-I6: an email that is staff of another business and an unknown email
    // look the same to the inviter; only the email bodies differ.
    [Fact]
    public async Task CrossBusinessAndUnknownEmail_IndistinguishableToInviter()
    {
        var (otherBusinessId, _) = await SeedBusinessWithOwnerAsync(_factory);
        var staffEmail = NewEmail();
        await SeedStaffAsync(otherBusinessId, RoleNames.Agent, staffEmail, _factory);
        var unknownEmail = NewEmail();
        var (_, owner) = await SeedBusinessWithLoggedInOwnerAsync(_factory);

        var staffCreate = await SendAsync(_factory, HttpMethod.Post, "/api/staff-invitations", owner.Cookie,
            new CreateStaffInvitationRequest(staffEmail, RoleNames.Agent));
        var unknownCreate = await SendAsync(_factory, HttpMethod.Post, "/api/staff-invitations", owner.Cookie,
            new CreateStaffInvitationRequest(unknownEmail, RoleNames.Agent));
        Assert.Equal(HttpStatusCode.Created, staffCreate.Status);
        Assert.Equal(HttpStatusCode.Created, unknownCreate.Status);

        var list = await SendAsync(_factory, HttpMethod.Get, "/api/staff-invitations", owner.Cookie);
        Assert.Equal(HttpStatusCode.OK, list.Status);

        using var staffCreateJson = JsonDocument.Parse(staffCreate.Body);
        using var unknownCreateJson = JsonDocument.Parse(unknownCreate.Body);
        using var listJson = JsonDocument.Parse(list.Body);
        var entries = listJson.RootElement.EnumerateArray().ToList();
        var staffEntry = Assert.Single(entries, e => e.GetProperty("email").GetString() == staffEmail);
        var unknownEntry = Assert.Single(entries, e => e.GetProperty("email").GetString() == unknownEmail);

        // The shape check: the same property names everywhere.
        var shapes = new[] { staffCreateJson.RootElement, unknownCreateJson.RootElement, staffEntry, unknownEntry }
            .Select(e => e.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray())
            .ToList();
        Assert.All(shapes, shape => Assert.Equal(shapes[0], shape));

        // The value check: every property except the per-invitation ones is
        // equal, so any added property is compared as well.
        string[] perInvitation = ["id", "email", "createdAt", "expiresAt", "lastSentAt"];
        var compared = shapes[0].Except(perInvitation).ToList();
        Assert.Contains("role", compared);
        Assert.Contains("status", compared);
        foreach (var name in compared)
        {
            Assert.Equal(staffCreateJson.RootElement.GetProperty(name).GetRawText(), unknownCreateJson.RootElement.GetProperty(name).GetRawText());
            Assert.Equal(staffEntry.GetProperty(name).GetRawText(), unknownEntry.GetProperty(name).GetRawText());
        }

        var staffMail = Assert.Single(EmailsTo(staffEmail, _factory));
        Assert.Equal("Your Nivora invitation", staffMail.Subject);
        Assert.DoesNotContain("#token=", staffMail.HtmlBody, StringComparison.Ordinal);
        var unknownMail = Assert.Single(EmailsTo(unknownEmail, _factory));
        Assert.Contains("/accept-invite#token=", unknownMail.HtmlBody, StringComparison.Ordinal);
    }

    // T-D12: the 21st create by one user within a minute is refused.
    [Fact]
    public async Task SendLimit_21stCreateIn1Min_429()
    {
        var (_, owner) = await SeedBusinessWithLoggedInOwnerAsync(_factory);

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 21; i++)
        {
            var response = await SendAsync(_factory, HttpMethod.Post, "/api/staff-invitations", owner.Cookie,
                new CreateStaffInvitationRequest(NewEmail(), RoleNames.Agent));
            statuses.Add(response.Status);
        }

        Assert.All(statuses.Take(20), s => Assert.Equal(HttpStatusCode.Created, s));
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[20]);
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
        var businessId = await SeedBusinessAsync(host);
        return (businessId, await SeedStaffAsync(businessId, RoleNames.Owner, host: host));
    }

    private async Task<Guid> SeedBusinessAsync(WebApplicationFactory<Program>? host = null)
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
        return businessId;
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

    private List<(string To, string Subject, string HtmlBody)> EmailsTo(string email, WebApplicationFactory<Program>? host = null) =>
        ((FakeEmailSender)(host ?? _host).Services.GetRequiredService<IEmailSender>()).SentEmails.Where(e => e.To == email).ToList();

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
    private async Task MoveBackAsync(Guid id, TimeSpan by, WebApplicationFactory<Program>? host = null)
    {
        await using var scope = (host ?? _host).Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var updated = await db.StaffInvitations.IgnoreQueryFilters()
            .Where(i => i.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(i => i.CreatedAt, i => i.CreatedAt - by)
                .SetProperty(i => i.LastSentAt, i => i.LastSentAt - by));
        Assert.Equal(1, updated);
    }

    private async Task<StaffInvitation> LoadInvitationAsync(Guid id, WebApplicationFactory<Program>? host = null)
    {
        await using var scope = (host ?? _host).Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.StaffInvitations.IgnoreQueryFilters().AsNoTracking().SingleAsync(i => i.Id == id);
    }

    private async Task<int> CountInvitationsAsync(Guid businessId, string email, WebApplicationFactory<Program>? host = null)
    {
        await using var scope = (host ?? _host).Services.CreateAsyncScope();
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

    // ── HTTP harness (slice 5c; BusinessApiTests.cs:41-125 shapes) ──

    private sealed record StaffLogin(StaffCaller Caller, string Email, string Cookie);

    private sealed record HttpResult(HttpStatusCode Status, string Body);

    private sealed record MatrixCell(HttpStatusCode Status, Guid BusinessId, int InvitationsBefore, int AuditsBefore, StaffInvitation? Target);

    private static HttpClient NewClient(WebApplicationFactory<Program> host) =>
        host.CreateClient(new() { BaseAddress = new Uri("https://localhost") });

    private static async Task<HttpResult> SendAsync(
        WebApplicationFactory<Program> host, HttpMethod method, string url, string? cookie, object? body = null)
    {
        using var client = NewClient(host);
        using var request = new HttpRequestMessage(method, url);
        if (cookie is not null)
        {
            request.Headers.Add("Cookie", cookie);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType());
        }

        using var response = await client.SendAsync(request);
        return new HttpResult(response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task<string> LoginCookieAsync(WebApplicationFactory<Program> host, string email)
    {
        using var client = NewClient(host);
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return response.Headers.GetValues("Set-Cookie").First().Split(';')[0];
    }

    private async Task<StaffLogin> SeedLoggedInStaffAsync(WebApplicationFactory<Program> host, Guid businessId, string role)
    {
        var email = NewEmail();
        var caller = await SeedStaffAsync(businessId, role, email, host);
        return new StaffLogin(caller, email, await LoginCookieAsync(host, email));
    }

    private async Task<(Guid BusinessId, StaffLogin Owner)> SeedBusinessWithLoggedInOwnerAsync(WebApplicationFactory<Program> host)
    {
        var businessId = await SeedBusinessAsync(host);
        return (businessId, await SeedLoggedInStaffAsync(host, businessId, RoleNames.Owner));
    }

    private static StaffInvitationResponse ReadInvitation(string body) =>
        JsonSerializer.Deserialize<StaffInvitationResponse>(body, JsonSerializerOptions.Web)!;

    /// <summary>T-I3: every column of the row as text, the hash in three encodings.</summary>
    private static async Task<string> InvitationColumnsAsync(Guid id, WebApplicationFactory<Program> host)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.StaffInvitations.IgnoreQueryFilters().AsNoTracking().SingleAsync(i => i.Id == id);
        return string.Join("|",
            row.Id, row.BusinessId, row.Email, row.NormalizedKey, row.Role,
            Convert.ToBase64String(row.TokenHash), Base64Url.EncodeToString(row.TokenHash), Convert.ToHexString(row.TokenHash),
            row.InvitedByUserId, row.CreatedAt.ToString("O"), row.ExpiresAt.ToString("O"), row.LastSentAt.ToString("O"));
    }

    /// <summary>
    /// One cell of the design Section 9 matrix over HTTP: a business with a
    /// signed-in Owner, the caller in the given role, and for resend and
    /// revoke an invitation of the target role whose cooldown has passed, so
    /// the role rule is the only possible refusal.
    /// </summary>
    private async Task<MatrixCell> RunMatrixCellAsync(string callerRole, string operation, string? targetRole)
    {
        var (businessId, owner) = await SeedBusinessWithLoggedInOwnerAsync(_factory);
        var caller = callerRole == RoleNames.Owner ? owner : await SeedLoggedInStaffAsync(_factory, businessId, callerRole);
        StaffInvitation? target = null;
        if (operation is "resend" or "revoke")
        {
            target = await LoadInvitationAsync(await SeedInvitationRowAsync(businessId, owner.Caller.UserId, targetRole!), _factory);
        }

        var invitationsBefore = await CountBusinessInvitationsAsync(businessId);
        var auditsBefore = await CountBusinessAuditsAsync(businessId);
        var response = operation switch
        {
            "create" => await SendAsync(_factory, HttpMethod.Post, "/api/staff-invitations", caller.Cookie,
                new CreateStaffInvitationRequest(NewEmail(), targetRole!)),
            "list" => await SendAsync(_factory, HttpMethod.Get, "/api/staff-invitations", caller.Cookie),
            "resend" => await SendAsync(_factory, HttpMethod.Post, $"/api/staff-invitations/{target!.Id}/resend", caller.Cookie),
            "revoke" => await SendAsync(_factory, HttpMethod.Delete, $"/api/staff-invitations/{target!.Id}", caller.Cookie),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null),
        };

        return new MatrixCell(response.Status, businessId, invitationsBefore, auditsBefore, target);
    }

    /// <summary>A pending row sent two minutes ago; every slice 2 CHECK holds.</summary>
    private async Task<Guid> SeedInvitationRowAsync(Guid businessId, Guid invitedBy, string role)
    {
        var now = DateTimeOffset.UtcNow;
        var email = NewEmail();
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var row = new StaffInvitation
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            Email = email,
            NormalizedKey = userManager.NormalizeName(email),
            Role = role,
            TokenHash = SHA256.HashData(RandomNumberGenerator.GetBytes(32)),
            InvitedByUserId = invitedBy,
            CreatedAt = now.AddMinutes(-2),
            LastSentAt = now.AddMinutes(-2),
            ExpiresAt = now.AddDays(7),
        };
        db.StaffInvitations.Add(row);
        await db.SaveChangesAsync();
        return row.Id;
    }

    private async Task<int> CountBusinessInvitationsAsync(Guid businessId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.StaffInvitations.IgnoreQueryFilters().CountAsync(i => i.BusinessId == businessId);
    }

    private async Task<int> CountBusinessAuditsAsync(Guid businessId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.AuditLogs.IgnoreQueryFilters().CountAsync(a => a.BusinessId == businessId);
    }
}
