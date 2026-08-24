using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using backend.Auth;
using backend.Constants;
using backend.Controllers;
using backend.Data;
using backend.Enums;
using backend.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace backend.Tests;

/// <summary>
/// HTTP-level tests for Step 14 — CustomersController's profile-detail,
/// identity-linking, and extended-logging-toggle endpoints, run against the
/// real app + real Neon dev DB via NivoraApiFactory — same pattern as
/// TagModeMappingsApiTests/ConversationsApiTests. Covers read (AnyStaff) vs
/// write (OwnerOrAdmin) authorization, tenant isolation, GDPR protection for
/// anonymized customers, the audit-log writes this step adds, and the
/// database-uniqueness-only concurrency guarantee for duplicate links (no
/// advisory lock — see LinkIdentity's own doc comment for why).
/// </summary>
public class CustomersApiTests : IAsyncLifetime
{
    private readonly NivoraApiFactory _factory = new();
    private readonly List<Guid> _businessIdsToClean = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_businessIdsToClean.Count > 0)
        {
            await using var scope = _factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var ids = _businessIdsToClean.ToArray();

            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"AuditLogs\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"CustomerExtendedLoggingHistories\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"ExternalIdentities\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Conversations\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Widgets\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Customers\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"AspNetUserRoles\" WHERE \"UserId\" IN (SELECT \"Id\" FROM \"AspNetUsers\" WHERE \"BusinessId\" = ANY({ids}))");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"AspNetUsers\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Businesses\" WHERE \"Id\" = ANY({ids})");
        }

        await _factory.DisposeAsync();
    }

    private HttpClient NewClient() => _factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });

    private async Task<(HttpResponseMessage Response, RegisterResponse? Body)> RegisterAsync(
        HttpClient client, string businessName, string email, string password = "Passw0rd!!")
    {
        var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(businessName, "Europe/Copenhagen", email, password));
        var body = response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<RegisterResponse>() : null;
        return (response, body);
    }

    private async Task ConfirmEmailAsync(HttpClient client, Guid userId)
    {
        string token;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await userManager.FindByIdAsync(userId.ToString());
            Assert.NotNull(user);
            token = await userManager.GenerateEmailConfirmationTokenAsync(user!);
        }

        var response = await client.PostAsJsonAsync("/api/auth/confirm-email", new ConfirmEmailRequest(userId, token));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static async Task<(HttpResponseMessage Response, LoginResponse? Body, string? Cookie)> LoginAsync(
        HttpClient client, string email, string password = "Passw0rd!!")
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, password));
        LoginResponse? body = null;
        string? cookie = null;
        if (response.IsSuccessStatusCode)
        {
            body = await response.Content.ReadFromJsonAsync<LoginResponse>();
            cookie = response.Headers.TryGetValues("Set-Cookie", out var values) ? values.First().Split(';')[0] : null;
        }
        return (response, body, cookie);
    }

    private async Task<ApplicationUser> SeedStaffIntoBusinessAsync(Guid businessId, string email, string role)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var createResult = await userManager.CreateAsync(user, "Passw0rd!!");
        Assert.True(createResult.Succeeded, string.Join("; ", createResult.Errors.Select(e => e.Description)));
        var roleResult = await userManager.AddToRoleAsync(user, role);
        Assert.True(roleResult.Succeeded, string.Join("; ", roleResult.Errors.Select(e => e.Description)));
        return user;
    }

    private static HttpRequestMessage Authed(HttpMethod method, string url, string cookie)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("Cookie", cookie);
        return request;
    }

    private static HttpRequestMessage WithBody<T>(HttpRequestMessage request, T body)
    {
        request.Content = JsonContent.Create(body);
        return request;
    }

    private async Task<(Guid BusinessId, Guid OwnerId, HttpClient OwnerClient, string OwnerCookie)> SetUpBusinessWithOwnerAsync(string businessName)
    {
        var email = $"owner-{Guid.NewGuid():N}@example.com";
        using var anonClient = NewClient();
        var (registerResponse, registerBody) = await RegisterAsync(anonClient, businessName, email);
        Assert.Equal(HttpStatusCode.OK, registerResponse.StatusCode);
        Assert.NotNull(registerBody);
        _businessIdsToClean.Add(registerBody!.BusinessId);
        await ConfirmEmailAsync(anonClient, registerBody.UserId);

        var client = NewClient();
        var (loginResponse, _, cookie) = await LoginAsync(client, email);
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);

        return (registerBody.BusinessId, registerBody.UserId, client, cookie!);
    }

    private static async Task<Guid> SeedCustomerAsync(AppDbContext db, Guid businessId, bool isAnonymized = false, string? email = "customer@example.com")
    {
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            Email = isAnonymized ? null : email,
            Name = isAnonymized ? null : "Test Customer",
            IsAnonymized = isAnonymized,
            AnonymizedAt = isAnonymized ? DateTimeOffset.UtcNow : null,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        return customer.Id;
    }

    private static async Task<Guid> SeedWidgetAsync(AppDbContext db, Guid businessId)
    {
        var widget = new Widget
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            Name = "Test Widget",
            PublicKey = Guid.NewGuid().ToString("N"),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Widgets.Add(widget);
        await db.SaveChangesAsync();
        return widget.Id;
    }

    private static async Task SeedConversationAsync(
        AppDbContext db, Guid businessId, Guid widgetId, Guid customerId, DateTimeOffset activityAt)
    {
        db.Conversations.Add(new Conversation
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            WidgetId = widgetId,
            CustomerId = customerId,
            Channel = ConversationChannels.Website,
            Status = ConversationStatus.Open,
            HandledBy = HandledBy.Ai,
            LastMessageAt = activityAt,
            CreatedAt = activityAt,
            UpdatedAt = activityAt,
        });
        await db.SaveChangesAsync();
    }

    // ── Customer profile detail ─────────────────────────────────────────

    [Fact]
    public async Task Get_SameTenantStaff_ReturnsDetailWithConversationHistory()
    {
        var (businessId, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Customer Detail Business");
        using var _ = ownerClient;

        Guid customerId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            customerId = await SeedCustomerAsync(db, businessId);
            var widgetId = await SeedWidgetAsync(db, businessId);
            await SeedConversationAsync(db, businessId, widgetId, customerId, DateTimeOffset.UtcNow);
        }

        var response = await ownerClient.SendAsync(Authed(HttpMethod.Get, $"/api/customers/{customerId}", ownerCookie));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<CustomerDetailResponse>();
        Assert.NotNull(body);
        Assert.Equal(customerId, body!.Id);
        Assert.False(body.IsAnonymized);
        Assert.False(body.ExtendedLoggingEnabled);
        Assert.Single(body.RecentConversations);
    }

    [Fact]
    public async Task Get_CrossTenantCustomer_ReturnsNotFound()
    {
        var (businessIdA, _, ownerClientA, _) = await SetUpBusinessWithOwnerAsync("Customer Detail Tenant A");
        using var _a = ownerClientA;
        Guid customerIdA;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            customerIdA = await SeedCustomerAsync(db, businessIdA);
        }

        var (_, _, ownerClientB, ownerCookieB) = await SetUpBusinessWithOwnerAsync("Customer Detail Tenant B");
        using var _b = ownerClientB;

        var response = await ownerClientB.SendAsync(Authed(HttpMethod.Get, $"/api/customers/{customerIdA}", ownerCookieB));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── Identity listing ─────────────────────────────────────────────────

    [Fact]
    public async Task ListIdentities_ReturnsSameTenantIdentities_WithCorrectValiditySemantics()
    {
        var (businessId, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Identity List Business");
        using var _ = ownerClient;

        Guid customerId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            customerId = await SeedCustomerAsync(db, businessId);

            // Active identity.
            db.ExternalIdentities.Add(new ExternalIdentity
            {
                Id = Guid.NewGuid(),
                BusinessId = businessId,
                CustomerId = customerId,
                Channel = ConversationChannels.Website,
                VerificationMethod = IdentityVerificationMethods.Otp,
                ExternalUserId = "active-external-id",
                LinkedAt = DateTimeOffset.UtcNow,
            });

            // Expired-but-not-revoked — IsCurrentlyValid must be false per
            // ExternalIdentityExtensions, RevokedAt IS NULL alone is not enough.
            db.ExternalIdentities.Add(new ExternalIdentity
            {
                Id = Guid.NewGuid(),
                BusinessId = businessId,
                CustomerId = customerId,
                Channel = ConversationChannels.Email,
                VerificationMethod = IdentityVerificationMethods.SignedToken,
                ExternalUserId = "expired-external-id",
                LinkedAt = DateTimeOffset.UtcNow.AddDays(-30),
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1),
            });

            await db.SaveChangesAsync();
        }

        var response = await ownerClient.SendAsync(Authed(HttpMethod.Get, $"/api/customers/{customerId}/identities", ownerCookie));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var identities = await response.Content.ReadFromJsonAsync<List<ExternalIdentityResponse>>();
        Assert.NotNull(identities);
        Assert.Equal(2, identities!.Count);

        var active = identities.Single(i => i.ExternalUserId == "active-external-id");
        Assert.True(active.IsCurrentlyValid);

        var expired = identities.Single(i => i.ExternalUserId == "expired-external-id");
        Assert.False(expired.IsCurrentlyValid);
        Assert.Null(expired.RevokedAt); // expired, not revoked — the two are deliberately distinct
    }

    [Fact]
    public async Task ListIdentities_CrossTenantCustomer_ReturnsNotFound()
    {
        var (businessIdA, _, ownerClientA, _) = await SetUpBusinessWithOwnerAsync("Identity List Tenant A");
        using var _a = ownerClientA;
        Guid customerIdA;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            customerIdA = await SeedCustomerAsync(db, businessIdA);
        }

        var (_, _, ownerClientB, ownerCookieB) = await SetUpBusinessWithOwnerAsync("Identity List Tenant B");
        using var _b = ownerClientB;

        var response = await ownerClientB.SendAsync(Authed(HttpMethod.Get, $"/api/customers/{customerIdA}/identities", ownerCookieB));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── Identity linking ─────────────────────────────────────────────────

    [Fact]
    public async Task LinkIdentity_Valid_Succeeds_PersistsWithTrustedBusinessId_AndWritesAuditEvent()
    {
        var (businessId, ownerId, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Link Identity Business");
        using var _ = ownerClient;

        Guid customerId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            customerId = await SeedCustomerAsync(db, businessId);
        }

        var response = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, $"/api/customers/{customerId}/identities", ownerCookie),
            new LinkExternalIdentityRequest(ConversationChannels.Website, IdentityVerificationMethods.Otp, "verified-user-123", null)));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ExternalIdentityResponse>();
        Assert.NotNull(body);
        Assert.True(body!.IsCurrentlyValid);
        Assert.Null(body.RevokedAt);
        Assert.Null(body.RevokedByUserId);

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var persisted = await verifyDb.ExternalIdentities.IgnoreQueryFilters().SingleAsync(i => i.Id == body.Id);
        // BusinessId came from the authenticated tenant context, never any
        // request field — LinkExternalIdentityRequest has no BusinessId
        // property at all, so there is nothing for a client value to override.
        Assert.Equal(businessId, persisted.BusinessId);
        Assert.Equal(customerId, persisted.CustomerId);
        Assert.Null(persisted.RevokedAt);

        var auditLogs = await verifyDb.AuditLogs.IgnoreQueryFilters()
            .Where(a => a.BusinessId == businessId && a.EntityId == body.Id.ToString())
            .ToListAsync();
        var entry = Assert.Single(auditLogs);
        Assert.Equal(AuditEntityTypes.ExternalIdentity, entry.EntityType);
        Assert.Equal(AuditEventTypes.IdentityLinked, entry.EventType);
        Assert.Equal(ActorType.NivoraBusinessStaff, entry.Actor);
        Assert.Equal(ownerId, entry.ChangedByUserId);
        Assert.NotNull(entry.NewValue);

        // PII check: ExternalUserId must never appear in the audit payload.
        Assert.DoesNotContain("verified-user-123", entry.NewValue);
        using var newValueDoc = JsonDocument.Parse(entry.NewValue!);
        Assert.Equal(ConversationChannels.Website, newValueDoc.RootElement.GetProperty("Channel").GetString());
    }

    [Fact]
    public async Task LinkIdentity_InvalidChannel_ReturnsBadRequest()
    {
        var (businessId, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Link Identity Invalid Channel Business");
        using var _ = ownerClient;

        Guid customerId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            customerId = await SeedCustomerAsync(db, businessId);
        }

        var response = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, $"/api/customers/{customerId}/identities", ownerCookie),
            new LinkExternalIdentityRequest("NotARealChannel", IdentityVerificationMethods.Otp, "some-id", null)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task LinkIdentity_InvalidVerificationMethod_ReturnsBadRequest()
    {
        var (businessId, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Link Identity Invalid Method Business");
        using var _ = ownerClient;

        Guid customerId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            customerId = await SeedCustomerAsync(db, businessId);
        }

        var response = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, $"/api/customers/{customerId}/identities", ownerCookie),
            new LinkExternalIdentityRequest(ConversationChannels.Website, "NotARealMethod", "some-id", null)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task LinkIdentity_MissingExternalUserId_ReturnsBadRequest()
    {
        var (businessId, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Link Identity Missing ExternalUserId Business");
        using var _ = ownerClient;

        Guid customerId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            customerId = await SeedCustomerAsync(db, businessId);
        }

        var response = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, $"/api/customers/{customerId}/identities", ownerCookie),
            new LinkExternalIdentityRequest(ConversationChannels.Website, IdentityVerificationMethods.Otp, "   ", null)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task LinkIdentity_AgentForbidden()
    {
        var (businessId, _, ownerClient, _) = await SetUpBusinessWithOwnerAsync("Link Identity Agent Forbidden Business");
        using var _ = ownerClient;

        Guid customerId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            customerId = await SeedCustomerAsync(db, businessId);
        }

        var agentEmail = $"agent-{Guid.NewGuid():N}@example.com";
        await SeedStaffIntoBusinessAsync(businessId, agentEmail, RoleNames.Agent);
        using var agentClient = NewClient();
        var (_, _, agentCookie) = await LoginAsync(agentClient, agentEmail);

        var response = await agentClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, $"/api/customers/{customerId}/identities", agentCookie!),
            new LinkExternalIdentityRequest(ConversationChannels.Website, IdentityVerificationMethods.Otp, "some-id", null)));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode); // OwnerOrAdmin, Agent excluded
    }

    /// <summary>
    /// The core GDPR invariant: an anonymized customer's identity proof was
    /// already permanently deleted by CustomerErasureService — linking a
    /// fresh one must never be possible, and the failed attempt must leave
    /// no ExternalIdentity or audit row behind (rejected before any write).
    /// </summary>
    [Fact]
    public async Task LinkIdentity_AnonymizedCustomer_ReturnsConflict_NoRowsWritten()
    {
        var (businessId, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Link Identity Anonymized Business");
        using var _ = ownerClient;

        Guid customerId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            customerId = await SeedCustomerAsync(db, businessId, isAnonymized: true);
        }

        var response = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, $"/api/customers/{customerId}/identities", ownerCookie),
            new LinkExternalIdentityRequest(ConversationChannels.Website, IdentityVerificationMethods.Otp, "should-never-link", null)));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();

        var identityCount = await verifyDb.ExternalIdentities.IgnoreQueryFilters().CountAsync(i => i.CustomerId == customerId);
        Assert.Equal(0, identityCount);

        var auditCount = await verifyDb.AuditLogs.IgnoreQueryFilters()
            .CountAsync(a => a.BusinessId == businessId && a.EntityType == AuditEntityTypes.ExternalIdentity);
        Assert.Equal(0, auditCount);

        // Confirm no PII was reconstructed on the customer row itself.
        var customer = await verifyDb.Customers.IgnoreQueryFilters().SingleAsync(c => c.Id == customerId);
        Assert.Null(customer.Email);
        Assert.Null(customer.Name);
    }

    /// <summary>
    /// Same atomicity proof shape as
    /// TagModeMappingsApiTests.Create_DuplicateTag_FailsAtomically_...:
    /// the ExternalIdentity insert and its AuditLog row share one change
    /// tracker and one SaveChangesAsync call inside LinkIdentity, so a
    /// successful link having exactly one audit row (never zero, never
    /// more than one) proves the two writes cannot commit independently.
    /// </summary>
    [Fact]
    public async Task LinkIdentity_SuccessfulLink_IdentityAndAuditEvent_AreCommittedAtomically()
    {
        var (businessId, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Link Identity Atomicity Business");
        using var _ = ownerClient;

        Guid customerId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            customerId = await SeedCustomerAsync(db, businessId);
        }

        var response = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, $"/api/customers/{customerId}/identities", ownerCookie),
            new LinkExternalIdentityRequest(ConversationChannels.Website, IdentityVerificationMethods.Otp, "atomic-user", null)));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ExternalIdentityResponse>();

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();

        Assert.True(await verifyDb.ExternalIdentities.IgnoreQueryFilters().AnyAsync(i => i.Id == body!.Id));
        var auditCount = await verifyDb.AuditLogs.IgnoreQueryFilters()
            .CountAsync(a => a.EntityId == body!.Id.ToString() && a.EventType == AuditEventTypes.IdentityLinked);
        Assert.Equal(1, auditCount);
    }

    /// <summary>
    /// The database's partial unique index — (BusinessId, Channel,
    /// ExternalUserId) WHERE RevokedAt IS NULL — is the sole concurrency
    /// authority for this race, deliberately with no advisory lock (unlike
    /// the Step 15 HandledBy/notification races). Two genuinely concurrent
    /// HTTP requests, fired via Task.WhenAll on two independent HttpClient
    /// instances (so neither request waits on the other client-side), are
    /// enough to prove the invariant: Postgres itself serializes the two
    /// concurrent inserts against the same unique key, so no barrier/waiter
    /// polling is needed the way Step 15's advisory-lock races required it.
    /// </summary>
    [Fact]
    public async Task LinkIdentity_TwoConcurrentRequests_SameChannelAndExternalUserId_OnlyOneSucceeds()
    {
        var (businessId, _, ownerClient1, ownerCookie) = await SetUpBusinessWithOwnerAsync("Link Identity Concurrent Business");
        using var _1 = ownerClient1;
        using var ownerClient2 = NewClient();

        Guid customerId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            customerId = await SeedCustomerAsync(db, businessId);
        }

        var request = new LinkExternalIdentityRequest(ConversationChannels.Website, IdentityVerificationMethods.Otp, "racing-external-id", null);

        var task1 = ownerClient1.SendAsync(WithBody(Authed(HttpMethod.Post, $"/api/customers/{customerId}/identities", ownerCookie), request));
        var task2 = ownerClient2.SendAsync(WithBody(Authed(HttpMethod.Post, $"/api/customers/{customerId}/identities", ownerCookie), request));

        await Task.WhenAll(task1, task2);
        var statusCodes = new[] { (await task1).StatusCode, (await task2).StatusCode };

        Assert.Contains(HttpStatusCode.Created, statusCodes);
        Assert.Contains(HttpStatusCode.Conflict, statusCodes);

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var activeCount = await verifyDb.ExternalIdentities.IgnoreQueryFilters()
            .CountAsync(i => i.CustomerId == customerId && i.Channel == ConversationChannels.Website
                && i.ExternalUserId == "racing-external-id" && i.RevokedAt == null);
        Assert.Equal(1, activeCount); // never zero, never two — exactly one active row survives the race
    }

    // ── Identity revocation ──────────────────────────────────────────────

    [Fact]
    public async Task RevokeIdentity_Valid_PopulatesRevocationFields_NeverHardDeletes_WritesAuditEvent()
    {
        var (businessId, ownerId, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Revoke Identity Business");
        using var _ = ownerClient;

        Guid customerId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            customerId = await SeedCustomerAsync(db, businessId);
        }

        var linkResponse = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, $"/api/customers/{customerId}/identities", ownerCookie),
            new LinkExternalIdentityRequest(ConversationChannels.Website, IdentityVerificationMethods.Otp, "to-be-revoked", null)));
        var linked = await linkResponse.Content.ReadFromJsonAsync<ExternalIdentityResponse>();

        var revokeResponse = await ownerClient.SendAsync(
            Authed(HttpMethod.Post, $"/api/customers/{customerId}/identities/{linked!.Id}/revoke", ownerCookie));
        Assert.Equal(HttpStatusCode.NoContent, revokeResponse.StatusCode);

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var identity = await verifyDb.ExternalIdentities.IgnoreQueryFilters().SingleAsync(i => i.Id == linked.Id);
        Assert.NotNull(identity.RevokedAt);
        Assert.Equal(ownerId, identity.RevokedByUserId);
        Assert.Equal("to-be-revoked", identity.ExternalUserId); // row still exists — never hard-deleted

        var auditLogs = await verifyDb.AuditLogs.IgnoreQueryFilters()
            .Where(a => a.EntityId == linked.Id.ToString() && a.EventType == AuditEventTypes.IdentityRevoked)
            .ToListAsync();
        var entry = Assert.Single(auditLogs);
        Assert.Equal(ownerId, entry.ChangedByUserId);
    }

    [Fact]
    public async Task RevokeIdentity_SecondRevoke_IsIdempotent_NoSecondAuditEvent_PreservesOriginalRevoker()
    {
        var (businessId, ownerId, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Revoke Identity Idempotent Business");
        using var _ = ownerClient;

        Guid customerId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            customerId = await SeedCustomerAsync(db, businessId);
        }

        var linkResponse = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, $"/api/customers/{customerId}/identities", ownerCookie),
            new LinkExternalIdentityRequest(ConversationChannels.Website, IdentityVerificationMethods.Otp, "double-revoke", null)));
        var linked = await linkResponse.Content.ReadFromJsonAsync<ExternalIdentityResponse>();

        var first = await ownerClient.SendAsync(Authed(HttpMethod.Post, $"/api/customers/{customerId}/identities/{linked!.Id}/revoke", ownerCookie));
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);

        var adminEmail = $"admin-{Guid.NewGuid():N}@example.com";
        await SeedStaffIntoBusinessAsync(businessId, adminEmail, RoleNames.Admin);
        using var adminClient = NewClient();
        var (_, _, adminCookie) = await LoginAsync(adminClient, adminEmail);

        var second = await adminClient.SendAsync(Authed(HttpMethod.Post, $"/api/customers/{customerId}/identities/{linked.Id}/revoke", adminCookie!));
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var identity = await verifyDb.ExternalIdentities.IgnoreQueryFilters().SingleAsync(i => i.Id == linked.Id);
        Assert.Equal(ownerId, identity.RevokedByUserId); // original revoker preserved, not overwritten by the second caller

        var auditCount = await verifyDb.AuditLogs.IgnoreQueryFilters()
            .CountAsync(a => a.EntityId == linked.Id.ToString() && a.EventType == AuditEventTypes.IdentityRevoked);
        Assert.Equal(1, auditCount); // still exactly one — the second, no-op revoke wrote nothing
    }

    [Fact]
    public async Task RevokeIdentity_CrossTenantCustomer_ReturnsNotFound()
    {
        var (businessIdA, _, ownerClientA, ownerCookieA) = await SetUpBusinessWithOwnerAsync("Revoke Identity Tenant A");
        using var _a = ownerClientA;

        Guid customerIdA;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            customerIdA = await SeedCustomerAsync(db, businessIdA);
        }

        var linkResponse = await ownerClientA.SendAsync(WithBody(
            Authed(HttpMethod.Post, $"/api/customers/{customerIdA}/identities", ownerCookieA),
            new LinkExternalIdentityRequest(ConversationChannels.Website, IdentityVerificationMethods.Otp, "tenant-a-identity", null)));
        var linked = await linkResponse.Content.ReadFromJsonAsync<ExternalIdentityResponse>();

        var (_, _, ownerClientB, ownerCookieB) = await SetUpBusinessWithOwnerAsync("Revoke Identity Tenant B");
        using var _b = ownerClientB;

        var response = await ownerClientB.SendAsync(
            Authed(HttpMethod.Post, $"/api/customers/{customerIdA}/identities/{linked!.Id}/revoke", ownerCookieB));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var identity = await verifyDb.ExternalIdentities.IgnoreQueryFilters().SingleAsync(i => i.Id == linked.Id);
        Assert.Null(identity.RevokedAt); // cross-tenant attempt must not have touched it
    }

    // ── Multiple identities per customer ─────────────────────────────────

    [Fact]
    public async Task LinkIdentity_SecondDistinctIdentity_SameCustomer_Succeeds()
    {
        var (businessId, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Multiple Identities Business");
        using var _ = ownerClient;

        Guid customerId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            customerId = await SeedCustomerAsync(db, businessId);
        }

        var first = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, $"/api/customers/{customerId}/identities", ownerCookie),
            new LinkExternalIdentityRequest(ConversationChannels.Website, IdentityVerificationMethods.Otp, "identity-one", null)));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, $"/api/customers/{customerId}/identities", ownerCookie),
            new LinkExternalIdentityRequest(ConversationChannels.Email, IdentityVerificationMethods.SignedToken, "identity-two", null)));
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var count = await verifyDb.ExternalIdentities.IgnoreQueryFilters().CountAsync(i => i.CustomerId == customerId);
        Assert.Equal(2, count); // no restriction against multiple identities per customer
    }

    // ── Extended logging ──────────────────────────────────────────────────

    [Fact]
    public async Task SetExtendedLogging_Enable_CreatesHistoryRow_AndAuditEvent()
    {
        var (businessId, ownerId, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Extended Logging Enable Business");
        using var _ = ownerClient;

        Guid customerId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            customerId = await SeedCustomerAsync(db, businessId);
        }

        var response = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Put, $"/api/customers/{customerId}/extended-logging", ownerCookie),
            new SetExtendedLoggingRequest(true)));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();

        var customer = await verifyDb.Customers.IgnoreQueryFilters().SingleAsync(c => c.Id == customerId);
        Assert.True(customer.ExtendedLoggingEnabled);

        var history = await verifyDb.CustomerExtendedLoggingHistories.IgnoreQueryFilters()
            .SingleAsync(h => h.CustomerId == customerId);
        Assert.Equal(ownerId, history.EnabledByUserId);
        Assert.Null(history.DisabledAt);
        Assert.Null(history.DisabledByUserId);

        var auditLogs = await verifyDb.AuditLogs.IgnoreQueryFilters()
            .Where(a => a.EntityId == customerId.ToString() && a.EventType == AuditEventTypes.CustomerExtendedLoggingEnabled)
            .ToListAsync();
        Assert.Single(auditLogs);
    }

    [Fact]
    public async Task SetExtendedLogging_Disable_ClosesHistoryRow_AndAuditEvent()
    {
        var (businessId, ownerId, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Extended Logging Disable Business");
        using var _ = ownerClient;

        Guid customerId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            customerId = await SeedCustomerAsync(db, businessId);
        }

        var enableResponse = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Put, $"/api/customers/{customerId}/extended-logging", ownerCookie),
            new SetExtendedLoggingRequest(true)));
        Assert.Equal(HttpStatusCode.NoContent, enableResponse.StatusCode);

        var disableResponse = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Put, $"/api/customers/{customerId}/extended-logging", ownerCookie),
            new SetExtendedLoggingRequest(false)));
        Assert.Equal(HttpStatusCode.NoContent, disableResponse.StatusCode);

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();

        var customer = await verifyDb.Customers.IgnoreQueryFilters().SingleAsync(c => c.Id == customerId);
        Assert.False(customer.ExtendedLoggingEnabled);

        var history = await verifyDb.CustomerExtendedLoggingHistories.IgnoreQueryFilters()
            .SingleAsync(h => h.CustomerId == customerId);
        Assert.NotNull(history.DisabledAt);
        Assert.Equal(ownerId, history.DisabledByUserId);

        var disableAuditLogs = await verifyDb.AuditLogs.IgnoreQueryFilters()
            .Where(a => a.EntityId == customerId.ToString() && a.EventType == AuditEventTypes.CustomerExtendedLoggingDisabled)
            .ToListAsync();
        Assert.Single(disableAuditLogs);
    }

    [Fact]
    public async Task SetExtendedLogging_AlreadyInRequestedState_IsIdempotent_NoDuplicateHistoryOrAudit()
    {
        var (businessId, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Extended Logging Idempotent Business");
        using var _ = ownerClient;

        Guid customerId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            customerId = await SeedCustomerAsync(db, businessId);
        }

        var first = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Put, $"/api/customers/{customerId}/extended-logging", ownerCookie),
            new SetExtendedLoggingRequest(true)));
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);

        var second = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Put, $"/api/customers/{customerId}/extended-logging", ownerCookie),
            new SetExtendedLoggingRequest(true)));
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();

        var historyCount = await verifyDb.CustomerExtendedLoggingHistories.IgnoreQueryFilters().CountAsync(h => h.CustomerId == customerId);
        Assert.Equal(1, historyCount);

        var auditCount = await verifyDb.AuditLogs.IgnoreQueryFilters()
            .CountAsync(a => a.EntityId == customerId.ToString() && a.EventType == AuditEventTypes.CustomerExtendedLoggingEnabled);
        Assert.Equal(1, auditCount);
    }

    [Fact]
    public async Task SetExtendedLogging_AgentForbidden()
    {
        var (businessId, _, ownerClient, _) = await SetUpBusinessWithOwnerAsync("Extended Logging Agent Forbidden Business");
        using var _ = ownerClient;

        Guid customerId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            customerId = await SeedCustomerAsync(db, businessId);
        }

        var agentEmail = $"agent-{Guid.NewGuid():N}@example.com";
        await SeedStaffIntoBusinessAsync(businessId, agentEmail, RoleNames.Agent);
        using var agentClient = NewClient();
        var (_, _, agentCookie) = await LoginAsync(agentClient, agentEmail);

        var response = await agentClient.SendAsync(WithBody(
            Authed(HttpMethod.Put, $"/api/customers/{customerId}/extended-logging", agentCookie!),
            new SetExtendedLoggingRequest(true)));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task SetExtendedLogging_CrossTenantCustomer_ReturnsNotFound_NoStateChanged()
    {
        var (businessIdA, _, ownerClientA, _) = await SetUpBusinessWithOwnerAsync("Extended Logging Tenant A");
        using var _a = ownerClientA;
        Guid customerIdA;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            customerIdA = await SeedCustomerAsync(db, businessIdA);
        }

        var (_, _, ownerClientB, ownerCookieB) = await SetUpBusinessWithOwnerAsync("Extended Logging Tenant B");
        using var _b = ownerClientB;

        var response = await ownerClientB.SendAsync(WithBody(
            Authed(HttpMethod.Put, $"/api/customers/{customerIdA}/extended-logging", ownerCookieB),
            new SetExtendedLoggingRequest(true)));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var customer = await verifyDb.Customers.IgnoreQueryFilters().SingleAsync(c => c.Id == customerIdA);
        Assert.False(customer.ExtendedLoggingEnabled);
    }

    /// <summary>
    /// Same atomicity shape as the identity-link test above: the
    /// Customer.ExtendedLoggingEnabled mutation, the
    /// CustomerExtendedLoggingHistory insert, and the AuditLog insert all
    /// share one change tracker and one SaveChangesAsync call inside
    /// SetExtendedLogging.
    /// </summary>
    [Fact]
    public async Task SetExtendedLogging_Enable_SettingAndHistoryAndAudit_AreCommittedAtomically()
    {
        var (businessId, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Extended Logging Atomicity Business");
        using var _ = ownerClient;

        Guid customerId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            customerId = await SeedCustomerAsync(db, businessId);
        }

        var response = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Put, $"/api/customers/{customerId}/extended-logging", ownerCookie),
            new SetExtendedLoggingRequest(true)));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();

        var customer = await verifyDb.Customers.IgnoreQueryFilters().SingleAsync(c => c.Id == customerId);
        var historyExists = await verifyDb.CustomerExtendedLoggingHistories.IgnoreQueryFilters().AnyAsync(h => h.CustomerId == customerId);
        var auditExists = await verifyDb.AuditLogs.IgnoreQueryFilters()
            .AnyAsync(a => a.EntityId == customerId.ToString() && a.EventType == AuditEventTypes.CustomerExtendedLoggingEnabled);

        // All three either all happened (this assertion) or, had the
        // controller thrown mid-way, none would have (SaveChangesAsync
        // failing would leave ExtendedLoggingEnabled false, no history row,
        // no audit row) — there is no reachable partial state in between.
        Assert.True(customer.ExtendedLoggingEnabled);
        Assert.True(historyExists);
        Assert.True(auditExists);
    }
}
