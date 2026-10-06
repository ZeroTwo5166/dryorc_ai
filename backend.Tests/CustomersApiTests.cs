using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using backend.Auth;
using backend.Constants;
using backend.Controllers;
using backend.Data;
using backend.Enums;
using backend.Models;
using backend.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
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
    private string _adoConnectionString = null!;

    public Task InitializeAsync()
    {
        // Identity-binding/erasure-integrity Phase 4's lock-freshness race
        // test needs a raw, independent connection (not routed through the
        // app's own DI-managed AppDbContext) to act as a deterministic
        // advisory-lock barrier — same technique
        // CustomerDataLifecycleTests.cs/VerificationCodeServiceTests.cs
        // already established for their own advisory-lock races.
        var connectionUri = new ConfigurationBuilder().AddUserSecrets<Program>().Build().GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection not found via user-secrets.");
        _adoConnectionString = PostgresConnectionString.FromUri(connectionUri);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await TestBusinessCleanup.DeleteThenDisposeAsync(_businessIdsToClean, _factory);
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

    // ── Identity-binding/erasure-integrity Phase 4 (Decision 3) ─────────
    //
    // docs/handoffs/identity-binding-erasure-integrity-design.md §4;
    // implementation plan Phase 4. Distinct from
    // LinkIdentity_AnonymizedCustomer_ReturnsConflict_NoRowsWritten above:
    // that test covers the terminal IsAnonymized == true state; these cover
    // the in-flight PendingErasureAt marker Phase 3's CustomerErasureService
    // Phase 0 sets, including the fresh-recheck race against a genuinely
    // concurrent erasure. Erasure itself is never mocked — only
    // ICustomerErasureCheckpoint (an intentional test seam, no-op in
    // production) is substituted, exactly as CustomerDataLifecycleTests.cs's
    // own Phase 3 tests already do.

    private const string PendingErasureConflictMessage =
        "This customer's erasure is currently in progress and cannot be linked to a new identity.";

    [Fact]
    public async Task LinkIdentity_PendingErasureAtSet_RejectsWithoutMutatingAnything()
    {
        var (businessId, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Link Identity Pending Erasure Business");
        using var _ = ownerClient;

        Guid customerId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            customerId = await SeedCustomerAsync(db, businessId);
            await db.Customers.IgnoreQueryFilters()
                .Where(c => c.Id == customerId)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.PendingErasureAt, DateTimeOffset.UtcNow));
        }

        var response = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, $"/api/customers/{customerId}/identities", ownerCookie),
            new LinkExternalIdentityRequest(ConversationChannels.Website, IdentityVerificationMethods.Otp, "should-never-link-pending-erasure", null)));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains(PendingErasureConflictMessage, await response.Content.ReadAsStringAsync());

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();

        var identityCount = await verifyDb.ExternalIdentities.IgnoreQueryFilters().CountAsync(i => i.CustomerId == customerId);
        Assert.Equal(0, identityCount);

        var auditCount = await verifyDb.AuditLogs.IgnoreQueryFilters()
            .CountAsync(a => a.BusinessId == businessId && a.EntityType == AuditEntityTypes.ExternalIdentity);
        Assert.Equal(0, auditCount);

        var customer = await verifyDb.Customers.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.Id == customerId);
        Assert.False(customer.IsAnonymized);
        Assert.NotNull(customer.PendingErasureAt);
    }

    /// <summary>
    /// The post-erasure zero-identity assertion is a regression check only: Phase 1's
    /// unconditional delete-by-CustomerId sweep removes any row inserted at this
    /// checkpoint whether or not Phase 4's fix exists, because the only checkpoint
    /// ('after-phase-0-committed') fires before Phase 1. The escape window after Phase 1's
    /// delete has no checkpoint and is not deterministically testable with the current
    /// seam. The discriminating assertion here is the 409 response; lock and freshness
    /// behavior are proven by the T3 test.
    /// </summary>
    [Fact]
    public async Task LinkIdentity_AfterErasurePhaseZeroCommitted_RejectsWithoutCreatingIdentity()
    {
        var (businessId, ownerId, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Link Identity Erasure Race Business");
        using var _ = ownerClient;

        Guid customerId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            customerId = await SeedCustomerAsync(db, businessId);

            var existingIdentity = new ExternalIdentity
            {
                Id = Guid.NewGuid(),
                BusinessId = businessId,
                CustomerId = customerId,
                Channel = ConversationChannels.Website,
                VerificationMethod = IdentityVerificationMethods.Otp,
                ExternalUserId = "pre-existing-identity",
                LinkedAt = DateTimeOffset.UtcNow,
            };
            db.ExternalIdentities.Add(existingIdentity);
            await db.SaveChangesAsync();
        }

        await using var erasureScope = _factory.Services.CreateAsyncScope();
        var erasureDb = erasureScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var purgeService = erasureScope.ServiceProvider.GetRequiredService<IConversationPurgeService>();
        var auditLogService = erasureScope.ServiceProvider.GetRequiredService<IAuditLogService>();
        var gate = new GatedCustomerErasureCheckpoint("after-phase-0-committed");
        var erasureService = new CustomerErasureService(erasureDb, purgeService, auditLogService, gate);

        var eraseTask = erasureService.EraseCustomerAsync(customerId, ownerId);
        await gate.Reached;

        await using (var duringScope = _factory.Services.CreateAsyncScope())
        {
            var duringDb = duringScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var during = await duringDb.Customers.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.Id == customerId);
            Assert.NotNull(during.PendingErasureAt);
        }

        var response = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, $"/api/customers/{customerId}/identities", ownerCookie),
            new LinkExternalIdentityRequest(ConversationChannels.Email, IdentityVerificationMethods.SignedToken, "should-never-link-race", null)));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains(PendingErasureConflictMessage, await response.Content.ReadAsStringAsync());

        await using (var midScope = _factory.Services.CreateAsyncScope())
        {
            var midDb = midScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var raceIdentityCount = await midDb.ExternalIdentities.IgnoreQueryFilters()
                .CountAsync(i => i.CustomerId == customerId && i.ExternalUserId == "should-never-link-race");
            Assert.Equal(0, raceIdentityCount);
        }

        gate.Release();
        await eraseTask;

        await using var afterScope = _factory.Services.CreateAsyncScope();
        var afterDb = afterScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var after = await afterDb.Customers.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.Id == customerId);
        Assert.True(after.IsAnonymized);
        Assert.Null(after.PendingErasureAt);

        var survivingIdentities = await afterDb.ExternalIdentities.IgnoreQueryFilters().CountAsync(i => i.CustomerId == customerId);
        Assert.Equal(0, survivingIdentities);
    }

    /// <summary>
    /// Proves the recheck is a fresh, AsNoTracking, post-lock query — not a
    /// re-read of the `customer` entity LinkIdentity already loaded (and
    /// still holds tracked) before acquiring the lock. A raw barrier
    /// connection holds ForCustomerErasure open first; LinkIdentity's HTTP
    /// request blocks acquiring the same lock (its own tracked customer
    /// snapshot, from before the block, still shows PendingErasureAt ==
    /// null at that point). Only once exactly one waiter is confirmed
    /// (pg_locks, not a timer) does the barrier set PendingErasureAt and
    /// commit, releasing the lock — LinkIdentity's fresh recheck, issued
    /// strictly after it acquires the now-free lock, must observe the
    /// just-committed value and reject. If the recheck instead read the
    /// stale tracked instance, this would observe PendingErasureAt == null
    /// and incorrectly succeed.
    /// </summary>
    [Fact]
    public async Task LinkIdentity_BlockedOnLock_ObservesPendingErasureCommittedWhileWaiting()
    {
        var (businessId, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Link Identity Lock Freshness Business");
        using var _ = ownerClient;

        Guid customerId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            customerId = await SeedCustomerAsync(db, businessId);
        }

        var lockKey = AdvisoryLockKeyGenerator.ForCustomerErasure(businessId, customerId);
        var (barrierConnection, barrierTransaction) = await AcquireBarrierAsync(lockKey);
        try
        {
            var linkTask = ownerClient.SendAsync(WithBody(
                Authed(HttpMethod.Post, $"/api/customers/{customerId}/identities", ownerCookie),
                new LinkExternalIdentityRequest(ConversationChannels.Website, IdentityVerificationMethods.Otp, "should-block-then-reject", null)));

            await WaitForAdvisoryLockWaitersAsync(barrierConnection, lockKey, expected: 1, raceName: "LinkIdentity vs erasure marker freshness");
            Assert.False(linkTask.IsCompleted);

            await using (var duringCmd = new NpgsqlCommand(
                "UPDATE \"Customers\" SET \"PendingErasureAt\" = now() WHERE \"Id\" = @id", barrierConnection, barrierTransaction))
            {
                duringCmd.Parameters.AddWithValue("id", customerId);
                await duringCmd.ExecuteNonQueryAsync();
            }
            await barrierTransaction.CommitAsync();

            var response = await linkTask;
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Contains(PendingErasureConflictMessage, await response.Content.ReadAsStringAsync());
        }
        finally
        {
            await barrierConnection.DisposeAsync();
        }

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var identityCount = await verifyDb.ExternalIdentities.IgnoreQueryFilters()
            .CountAsync(i => i.CustomerId == customerId && i.ExternalUserId == "should-block-then-reject");
        Assert.Equal(0, identityCount);

        var auditCount = await verifyDb.AuditLogs.IgnoreQueryFilters()
            .CountAsync(a => a.BusinessId == businessId && a.EntityType == AuditEntityTypes.ExternalIdentity);
        Assert.Equal(0, auditCount);
    }

    /// <summary>
    /// Regression guard, not an escape proof: this ordering already worked
    /// correctly before Phase 4 (Phase 1's own unconditional delete-by-
    /// CustomerId sweep, unchanged by this design) — proves the new lock
    /// does not introduce a false rejection or a dropped write when there
    /// is no concurrency at all.
    /// </summary>
    [Fact]
    public async Task LinkIdentity_CompletesBeforeErasureStarts_IdentityIsSweptByPhaseOne()
    {
        var (businessId, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Link Identity Then Erase Business");
        using var _ = ownerClient;

        Guid customerId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            customerId = await SeedCustomerAsync(db, businessId);
        }

        var linkResponse = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, $"/api/customers/{customerId}/identities", ownerCookie),
            new LinkExternalIdentityRequest(ConversationChannels.Website, IdentityVerificationMethods.Otp, "linked-before-erasure", null)));
        Assert.Equal(HttpStatusCode.Created, linkResponse.StatusCode);

        var eraseResponse = await ownerClient.SendAsync(Authed(HttpMethod.Post, $"/api/customers/{customerId}/erase", ownerCookie));
        Assert.Equal(HttpStatusCode.NoContent, eraseResponse.StatusCode);

        await using var afterScope = _factory.Services.CreateAsyncScope();
        var afterDb = afterScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var after = await afterDb.Customers.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.Id == customerId);
        Assert.True(after.IsAnonymized);
        Assert.Null(after.PendingErasureAt);

        var survivingIdentities = await afterDb.ExternalIdentities.IgnoreQueryFilters().CountAsync(i => i.CustomerId == customerId);
        Assert.Equal(0, survivingIdentities);
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

    // ── GDPR erasure endpoint (design §6) ────────────────────────────────

    [Fact]
    public async Task Erase_Unauthenticated_ReturnsUnauthorized()
    {
        using var client = NewClient();
        var response = await client.PostAsync($"/api/customers/{Guid.NewGuid()}/erase", null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Erase_AgentForbidden_CustomerNotAnonymized()
    {
        var (businessId, _, ownerClient, _) = await SetUpBusinessWithOwnerAsync("Erase Agent Forbidden Business");
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

        var response = await agentClient.SendAsync(Authed(HttpMethod.Post, $"/api/customers/{customerId}/erase", agentCookie!));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode); // OwnerOrAdmin, Agent excluded

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var customer = await verifyDb.Customers.IgnoreQueryFilters().SingleAsync(c => c.Id == customerId);
        Assert.False(customer.IsAnonymized);
    }

    /// <summary>
    /// The full HTTP-level proof: Owner/Admin succeeds, the customer and
    /// their conversation are actually erased, and the resulting audit rows
    /// carry the authenticated caller's own id as ChangedByUserId — never
    /// invented or inferred by the service, captured only at the
    /// controller's own CurrentUserId boundary.
    /// </summary>
    [Fact]
    public async Task Erase_OwnerOrAdmin_Succeeds_AnonymizesCustomer_AuditChangedByUserIdCorrect()
    {
        var (businessId, ownerId, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Erase Success Business");
        using var _ = ownerClient;

        Guid customerId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            customerId = await SeedCustomerAsync(db, businessId);
            var widgetId = await SeedWidgetAsync(db, businessId);
            await SeedConversationAsync(db, businessId, widgetId, customerId, DateTimeOffset.UtcNow);
        }

        var response = await ownerClient.SendAsync(Authed(HttpMethod.Post, $"/api/customers/{customerId}/erase", ownerCookie));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();

        var customer = await verifyDb.Customers.IgnoreQueryFilters().SingleAsync(c => c.Id == customerId);
        Assert.True(customer.IsAnonymized);
        Assert.Null(customer.Email);
        Assert.Null(customer.Name);

        // ConversationPurgeService never touches Conversation.CustomerId —
        // it survives purge unchanged, so this still matches this
        // customer's own conversation.
        var conversation = await verifyDb.Conversations.IgnoreQueryFilters().SingleAsync(c => c.CustomerId == customerId);
        Assert.True(conversation.IsAnonymized);
        Assert.Null(conversation.VisitorSessionId);

        var completionAudit = await verifyDb.AuditLogs.IgnoreQueryFilters()
            .SingleAsync(a => a.EntityId == customerId.ToString() && a.EventType == AuditEventTypes.CustomerErased);
        Assert.Equal(ownerId, completionAudit.ChangedByUserId);
        Assert.Equal(ActorType.NivoraBusinessStaff, completionAudit.Actor);

        var conversationAudit = await verifyDb.AuditLogs.IgnoreQueryFilters()
            .Where(a => a.EventType == AuditEventTypes.ConversationErasurePurged && a.BusinessId == businessId)
            .ToListAsync();
        var singleConversationAudit = Assert.Single(conversationAudit);
        Assert.Equal(ownerId, singleConversationAudit.ChangedByUserId);
        Assert.Equal(completionAudit.CorrelationId, singleConversationAudit.CorrelationId);
    }

    [Fact]
    public async Task Erase_CrossTenantCustomer_ReturnsNotFound_NoStateChanged()
    {
        var (businessIdA, _, ownerClientA, _) = await SetUpBusinessWithOwnerAsync("Erase Tenant A");
        using var _a = ownerClientA;
        Guid customerIdA;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            customerIdA = await SeedCustomerAsync(db, businessIdA);
        }

        var (_, _, ownerClientB, ownerCookieB) = await SetUpBusinessWithOwnerAsync("Erase Tenant B");
        using var _b = ownerClientB;

        var response = await ownerClientB.SendAsync(Authed(HttpMethod.Post, $"/api/customers/{customerIdA}/erase", ownerCookieB));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode); // never 403 — cross-tenant existence not leaked

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var customerA = await verifyDb.Customers.IgnoreQueryFilters().SingleAsync(c => c.Id == customerIdA);
        Assert.False(customerA.IsAnonymized);
    }

    [Fact]
    public async Task Erase_UnknownCustomerId_ReturnsNotFound()
    {
        var (_, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Erase Unknown Customer Business");
        using var _ = ownerClient;

        var response = await ownerClient.SendAsync(Authed(HttpMethod.Post, $"/api/customers/{Guid.NewGuid()}/erase", ownerCookie));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// Idempotency through the real HTTP path — no 409, no confirmation
    /// token, exactly matching design §6's own locked idempotency
    /// behavior: a second call on an already-anonymized customer is a
    /// legitimate 204 no-op.
    /// </summary>
    [Fact]
    public async Task Erase_CalledTwice_BothReturnNoContent_NoExtraAuditRows()
    {
        var (businessId, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Erase Idempotent Business");
        using var _ = ownerClient;

        Guid customerId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            customerId = await SeedCustomerAsync(db, businessId);
        }

        var first = await ownerClient.SendAsync(Authed(HttpMethod.Post, $"/api/customers/{customerId}/erase", ownerCookie));
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);

        await using var midScope = _factory.Services.CreateAsyncScope();
        var midDb = midScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var auditCountAfterFirst = await midDb.AuditLogs.IgnoreQueryFilters().CountAsync(a => a.BusinessId == businessId);

        var second = await ownerClient.SendAsync(Authed(HttpMethod.Post, $"/api/customers/{customerId}/erase", ownerCookie));
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode); // legitimate no-op, never 409

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var auditCountAfterSecond = await verifyDb.AuditLogs.IgnoreQueryFilters().CountAsync(a => a.BusinessId == businessId);
        Assert.Equal(auditCountAfterFirst, auditCountAfterSecond);
    }

    // ── Identity-binding/erasure-integrity Phase 8 — OwnerOnly recovery-clear ──
    //
    // docs/handoffs/identity-binding-erasure-integrity-design.md §3.8/§11 (OQ-3);
    // implementation plan Phase 8. Unlike Phase 4/5/6/7, this endpoint has no
    // evidentiary check to race against an in-flight erasure with (§3.8: the
    // codebase has no mechanical way to establish "genuinely abandoned") — the
    // adversarial-concurrency case here is proving the advisory lock itself is
    // real (T7) and that two concurrent Owner clears serialize to exactly one
    // mutation (T8), not proving a rejection.

    private static async Task<DateTimeOffset> SeedPendingErasureAtAsync(AppDbContext db, Guid customerId)
    {
        await db.Customers.IgnoreQueryFilters()
            .Where(c => c.Id == customerId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.PendingErasureAt, DateTimeOffset.UtcNow));

        var persisted = await db.Customers.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.Id == customerId)
            .Select(c => c.PendingErasureAt)
            .SingleAsync();
        return persisted!.Value;
    }

    /// <summary>T1 — real Phase 0 checkpoint (throws, simulating a crash/abandonment
    /// immediately after Phase 0 commits, per design §3.7 point 4's own accepted
    /// scenario), then the actual HTTP recovery-clear endpoint. Same
    /// throws-at-a-named-checkpoint idiom as CustomerDataLifecycleTests.cs's own
    /// ThrowAtCustomerErasureCheckpoint (duplicated here per this project's
    /// established per-file convention for small concurrency-test doubles).</summary>
    [Fact]
    public async Task ClearPendingErasure_AbandonedAfterPhaseZero_ClearsMarkerAndAudits()
    {
        var (businessId, ownerId, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Clear Pending Erasure Abandoned Business");
        using var _ = ownerClient;

        Guid customerId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            customerId = await SeedCustomerAsync(db, businessId);
        }

        await using (var erasureScope = _factory.Services.CreateAsyncScope())
        {
            var erasureDb = erasureScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var purgeService = erasureScope.ServiceProvider.GetRequiredService<IConversationPurgeService>();
            var auditLogService = erasureScope.ServiceProvider.GetRequiredService<IAuditLogService>();
            var failingService = new CustomerErasureService(
                erasureDb, purgeService, auditLogService, new ThrowAtCustomerErasureCheckpoint("after-phase-0-committed"));

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => failingService.EraseCustomerAsync(customerId, ownerId));
        }

        DateTimeOffset expectedOldValue;
        await using (var midScope = _factory.Services.CreateAsyncScope())
        {
            var midDb = midScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var mid = await midDb.Customers.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.Id == customerId);
            Assert.NotNull(mid.PendingErasureAt);
            Assert.False(mid.IsAnonymized);
            expectedOldValue = mid.PendingErasureAt!.Value;
        }

        var response = await ownerClient.SendAsync(
            Authed(HttpMethod.Post, $"/api/customers/{customerId}/pending-erasure/clear", ownerCookie));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await using var afterScope = _factory.Services.CreateAsyncScope();
        var afterDb = afterScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var after = await afterDb.Customers.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.Id == customerId);
        Assert.Null(after.PendingErasureAt);

        var audit = await afterDb.AuditLogs.IgnoreQueryFilters()
            .SingleAsync(a => a.EntityId == customerId.ToString() && a.EventType == AuditEventTypes.CustomerErasureRecoveryCleared);
        Assert.Equal(businessId, audit.BusinessId);
        Assert.Equal(AuditEntityTypes.Customer, audit.EntityType);
        Assert.Equal(ownerId, audit.ChangedByUserId);
        Assert.Equal(ActorType.NivoraBusinessStaff, audit.Actor);
        Assert.Equal(expectedOldValue.ToString("O"), audit.OldValue);
        Assert.Null(audit.NewValue);
    }

    [Fact]
    public async Task ClearPendingErasure_AlreadyNull_NoOp_NoAuditRow()
    {
        var (businessId, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Clear Pending Erasure NoOp Business");
        using var _ = ownerClient;

        Guid customerId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            customerId = await SeedCustomerAsync(db, businessId);
        }

        var response = await ownerClient.SendAsync(
            Authed(HttpMethod.Post, $"/api/customers/{customerId}/pending-erasure/clear", ownerCookie));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var customer = await verifyDb.Customers.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.Id == customerId);
        Assert.Null(customer.PendingErasureAt);

        var auditCount = await verifyDb.AuditLogs.IgnoreQueryFilters()
            .CountAsync(a => a.BusinessId == businessId && a.EventType == AuditEventTypes.CustomerErasureRecoveryCleared);
        Assert.Equal(0, auditCount);
    }

    /// <summary>T3 — a Customer whose erasure already completed (IsAnonymized == true)
    /// already has PendingErasureAt == null per C-3 (Phase 3 clears it on successful
    /// completion) — this collapses into the same no-op path as T2. No special
    /// IsAnonymized handling exists in the endpoint, matching the locked design's
    /// unconditional-clear-under-lock scope (§ Phase 8, "no evidentiary check").</summary>
    [Fact]
    public async Task ClearPendingErasure_CompletedErasure_NoOp_NoAuditRow()
    {
        var (businessId, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Clear Pending Erasure Completed Business");
        using var _ = ownerClient;

        Guid customerId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            customerId = await SeedCustomerAsync(db, businessId, isAnonymized: true);
        }

        var response = await ownerClient.SendAsync(
            Authed(HttpMethod.Post, $"/api/customers/{customerId}/pending-erasure/clear", ownerCookie));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var customer = await verifyDb.Customers.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.Id == customerId);
        Assert.True(customer.IsAnonymized);
        Assert.Null(customer.PendingErasureAt);

        var auditCount = await verifyDb.AuditLogs.IgnoreQueryFilters()
            .CountAsync(a => a.BusinessId == businessId && a.EventType == AuditEventTypes.CustomerErasureRecoveryCleared);
        Assert.Equal(0, auditCount);
    }

    [Fact]
    public async Task ClearPendingErasure_Owner_Succeeds()
    {
        var (businessId, ownerId, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Clear Pending Erasure Owner Business");
        using var _ = ownerClient;

        Guid customerId;
        DateTimeOffset seeded;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            customerId = await SeedCustomerAsync(db, businessId);
            seeded = await SeedPendingErasureAtAsync(db, customerId);
        }

        var response = await ownerClient.SendAsync(
            Authed(HttpMethod.Post, $"/api/customers/{customerId}/pending-erasure/clear", ownerCookie));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var customer = await verifyDb.Customers.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.Id == customerId);
        Assert.Null(customer.PendingErasureAt);

        var audit = await verifyDb.AuditLogs.IgnoreQueryFilters()
            .SingleAsync(a => a.EntityId == customerId.ToString() && a.EventType == AuditEventTypes.CustomerErasureRecoveryCleared);
        Assert.Equal(ownerId, audit.ChangedByUserId);
        Assert.Equal(seeded.ToString("O"), audit.OldValue);
    }

    [Fact]
    public async Task ClearPendingErasure_AdminForbidden_MarkerUnchanged()
    {
        var (businessId, _, ownerClient, _) = await SetUpBusinessWithOwnerAsync("Clear Pending Erasure Admin Forbidden Business");
        using var _ = ownerClient;

        Guid customerId;
        DateTimeOffset seeded;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            customerId = await SeedCustomerAsync(db, businessId);
            seeded = await SeedPendingErasureAtAsync(db, customerId);
        }

        var adminEmail = $"admin-{Guid.NewGuid():N}@example.com";
        await SeedStaffIntoBusinessAsync(businessId, adminEmail, RoleNames.Admin);
        using var adminClient = NewClient();
        var (_, _, adminCookie) = await LoginAsync(adminClient, adminEmail);

        var response = await adminClient.SendAsync(
            Authed(HttpMethod.Post, $"/api/customers/{customerId}/pending-erasure/clear", adminCookie!));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode); // OwnerOnly — Admin excluded, unlike Erase's own OwnerOrAdmin

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var customer = await verifyDb.Customers.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.Id == customerId);
        Assert.Equal(seeded, customer.PendingErasureAt);
    }

    [Fact]
    public async Task ClearPendingErasure_AgentForbidden_MarkerUnchanged()
    {
        var (businessId, _, ownerClient, _) = await SetUpBusinessWithOwnerAsync("Clear Pending Erasure Agent Forbidden Business");
        using var _ = ownerClient;

        Guid customerId;
        DateTimeOffset seeded;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            customerId = await SeedCustomerAsync(db, businessId);
            seeded = await SeedPendingErasureAtAsync(db, customerId);
        }

        var agentEmail = $"agent-{Guid.NewGuid():N}@example.com";
        await SeedStaffIntoBusinessAsync(businessId, agentEmail, RoleNames.Agent);
        using var agentClient = NewClient();
        var (_, _, agentCookie) = await LoginAsync(agentClient, agentEmail);

        var response = await agentClient.SendAsync(
            Authed(HttpMethod.Post, $"/api/customers/{customerId}/pending-erasure/clear", agentCookie!));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var customer = await verifyDb.Customers.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.Id == customerId);
        Assert.Equal(seeded, customer.PendingErasureAt);
    }

    [Fact]
    public async Task ClearPendingErasure_CrossTenantCustomer_ReturnsNotFound_MarkerUnchanged_NoAuditRow()
    {
        var (businessIdA, _, ownerClientA, _) = await SetUpBusinessWithOwnerAsync("Clear Pending Erasure Tenant A");
        using var _a = ownerClientA;
        Guid customerIdA;
        DateTimeOffset seeded;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            customerIdA = await SeedCustomerAsync(db, businessIdA);
            seeded = await SeedPendingErasureAtAsync(db, customerIdA);
        }

        var (_, _, ownerClientB, ownerCookieB) = await SetUpBusinessWithOwnerAsync("Clear Pending Erasure Tenant B");
        using var _b = ownerClientB;

        var response = await ownerClientB.SendAsync(
            Authed(HttpMethod.Post, $"/api/customers/{customerIdA}/pending-erasure/clear", ownerCookieB));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode); // never 403 — cross-tenant existence not leaked

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var customerA = await verifyDb.Customers.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.Id == customerIdA);
        Assert.Equal(seeded, customerA.PendingErasureAt);

        var auditCount = await verifyDb.AuditLogs.IgnoreQueryFilters()
            .CountAsync(a => a.EventType == AuditEventTypes.CustomerErasureRecoveryCleared);
        Assert.Equal(0, auditCount);
    }

    [Fact]
    public async Task ClearPendingErasure_UnknownCustomerId_ReturnsNotFound_NoAuditRow()
    {
        var (businessId, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Clear Pending Erasure Unknown Customer Business");
        using var _ = ownerClient;

        var response = await ownerClient.SendAsync(
            Authed(HttpMethod.Post, $"/api/customers/{Guid.NewGuid()}/pending-erasure/clear", ownerCookie));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var auditCount = await verifyDb.AuditLogs.IgnoreQueryFilters()
            .CountAsync(a => a.BusinessId == businessId && a.EventType == AuditEventTypes.CustomerErasureRecoveryCleared);
        Assert.Equal(0, auditCount);
    }

    /// <summary>T7 — proves the recovery-clear transaction actually waits for
    /// ForCustomerErasure, not merely documents that it should. Same
    /// AcquireBarrierAsync/WaitForAdvisoryLockWaitersAsync idiom as
    /// LinkIdentity_BlockedOnLock_ObservesPendingErasureCommittedWhileWaiting
    /// above (Phase 4's own lock-freshness proof) — deterministic via
    /// pg_locks, never a sleep/timing assumption.</summary>
    [Fact]
    public async Task ClearPendingErasure_BlockedOnLock_CompletesOnlyAfterLockReleased()
    {
        var (businessId, ownerId, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Clear Pending Erasure Lock Business");
        using var _ = ownerClient;

        Guid customerId;
        DateTimeOffset seeded;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            customerId = await SeedCustomerAsync(db, businessId);
            seeded = await SeedPendingErasureAtAsync(db, customerId);
        }

        var lockKey = AdvisoryLockKeyGenerator.ForCustomerErasure(businessId, customerId);
        var (barrierConnection, barrierTransaction) = await AcquireBarrierAsync(lockKey);
        try
        {
            var clearTask = ownerClient.SendAsync(
                Authed(HttpMethod.Post, $"/api/customers/{customerId}/pending-erasure/clear", ownerCookie));

            await WaitForAdvisoryLockWaitersAsync(barrierConnection, lockKey, expected: 1, raceName: "ClearPendingErasure vs simulated in-flight erasure");
            Assert.False(clearTask.IsCompleted);

            await barrierTransaction.CommitAsync();

            var response = await clearTask;
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }
        finally
        {
            await barrierConnection.DisposeAsync();
        }

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var customer = await verifyDb.Customers.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.Id == customerId);
        Assert.Null(customer.PendingErasureAt);

        var audit = await verifyDb.AuditLogs.IgnoreQueryFilters()
            .SingleAsync(a => a.EntityId == customerId.ToString() && a.EventType == AuditEventTypes.CustomerErasureRecoveryCleared);
        Assert.Equal(ownerId, audit.ChangedByUserId);
        Assert.Equal(seeded.ToString("O"), audit.OldValue);
    }

    /// <summary>T8 — two genuine concurrent Owner requests, real HTTP + real DB
    /// concurrency (Task.WhenAll, not sequential awaits). The conditional
    /// ExecuteUpdateAsync's WHERE PendingErasureAt IS NOT NULL, evaluated under
    /// the shared advisory lock, guarantees exactly one of the two transactions
    /// observes a matching row — the other, running strictly after the winner's
    /// commit releases the lock, observes an already-null marker and takes the
    /// no-op path.</summary>
    [Fact]
    public async Task ClearPendingErasure_ConcurrentDoubleClear_ExactlyOneMutationAndAuditRow()
    {
        var (businessId, ownerId, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Clear Pending Erasure Concurrent Business");
        using var _ = ownerClient;

        Guid customerId;
        DateTimeOffset seeded;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            customerId = await SeedCustomerAsync(db, businessId);
            seeded = await SeedPendingErasureAtAsync(db, customerId);
        }

        using var secondClient = NewClient();

        var task1 = ownerClient.SendAsync(Authed(HttpMethod.Post, $"/api/customers/{customerId}/pending-erasure/clear", ownerCookie));
        var task2 = secondClient.SendAsync(Authed(HttpMethod.Post, $"/api/customers/{customerId}/pending-erasure/clear", ownerCookie));
        var responses = await Task.WhenAll(task1, task2);

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.NoContent, r.StatusCode));

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var customer = await verifyDb.Customers.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.Id == customerId);
        Assert.Null(customer.PendingErasureAt);

        var audits = await verifyDb.AuditLogs.IgnoreQueryFilters()
            .Where(a => a.EntityId == customerId.ToString() && a.EventType == AuditEventTypes.CustomerErasureRecoveryCleared)
            .ToListAsync();
        var singleAudit = Assert.Single(audits);
        Assert.Equal(ownerId, singleAudit.ChangedByUserId);
        Assert.Equal(seeded.ToString("O"), singleAudit.OldValue);
    }

    [Fact]
    public async Task ClearPendingErasure_Unauthenticated_ReturnsUnauthorized()
    {
        using var client = NewClient();
        var response = await client.PostAsync($"/api/customers/{Guid.NewGuid()}/pending-erasure/clear", null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>Same throws-at-a-named-checkpoint idiom as
    /// CustomerDataLifecycleTests.cs's own ThrowAtCustomerErasureCheckpoint —
    /// duplicated here per this project's established per-file convention for
    /// small concurrency-test doubles (see the doc comment on
    /// GatedCustomerErasureCheckpoint below for the same note).</summary>
    private class ThrowAtCustomerErasureCheckpoint(string checkpoint) : ICustomerErasureCheckpoint
    {
        public Task ReachedAsync(string checkpointName, CancellationToken cancellationToken)
        {
            if (checkpointName == checkpoint)
            {
                throw new InvalidOperationException($"Injected test failure at '{checkpointName}'.");
            }

            return Task.CompletedTask;
        }
    }

    // ── Identity-binding/erasure-integrity Phase 4 test helpers ──────────

    /// <summary>
    /// Same TaskCompletionSource-gated pause idiom as
    /// CustomerDataLifecycleTests.cs's own GatedCustomerErasureCheckpoint
    /// (private to that file, so duplicated here rather than shared —
    /// matching this project's own established per-file convention for
    /// small concurrency-test doubles, e.g. AcquireBarrierAsync/
    /// WaitForAdvisoryLockWaitersAsync below are independently duplicated
    /// in CustomerDataLifecycleTests.cs and VerificationCodeServiceTests.cs
    /// too). Every checkpoint name other than the configured one passes
    /// through immediately, un-paused.
    /// </summary>
    private class GatedCustomerErasureCheckpoint(string checkpoint) : ICustomerErasureCheckpoint
    {
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes once the configured checkpoint has actually been reached.</summary>
        public Task Reached => _reached.Task;

        public void Release() => _releaseGate.TrySetResult();

        public async Task ReachedAsync(string checkpointName, CancellationToken cancellationToken)
        {
            if (checkpointName != checkpoint)
            {
                return;
            }

            _reached.TrySetResult();
            await _releaseGate.Task;
        }
    }

    /// <summary>Opens a raw barrier connection and pre-acquires the given advisory lock, holding it open until the caller commits/rolls back and disposes the returned transaction — same idiom as CustomerDataLifecycleTests.cs's own AcquireBarrierAsync.</summary>
    private async Task<(NpgsqlConnection Connection, NpgsqlTransaction Transaction)> AcquireBarrierAsync(long lockKey)
    {
        var connection = new NpgsqlConnection(_adoConnectionString);
        await connection.OpenAsync();
        var transaction = await connection.BeginTransactionAsync();
        await using (var lockCmd = new NpgsqlCommand($"SELECT pg_advisory_xact_lock({lockKey})", connection, transaction))
        {
            await lockCmd.ExecuteNonQueryAsync();
        }
        return (connection, transaction);
    }

    /// <summary>Deterministic wait-for-condition, not a fixed sleep — same established convention as CustomerDataLifecycleTests.cs's/VerificationCodeServiceTests.cs's own helper of this name.</summary>
    private async Task WaitForAdvisoryLockWaitersAsync(NpgsqlConnection barrierConnection, long lockKey, int expected, string raceName)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        int count;
        do
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail(
                    $"Expected {expected} concurrent advisory-lock waiters for the {raceName} race within 10s, " +
                    "but they did not appear. If the concurrency protection under test has been removed or is " +
                    "no longer reached, this is the expected failure mode.");
            }

            count = await CountAdvisoryLockWaitersAsync(barrierConnection, lockKey);
            if (count < expected)
            {
                await Task.Delay(25);
            }
        } while (count < expected);
    }

    private async Task<int> CountAdvisoryLockWaitersAsync(NpgsqlConnection barrierConnection, long lockKey)
    {
        var classId = unchecked((uint)(lockKey >> 32));
        var objId = unchecked((uint)lockKey);
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM pg_locks " +
            "WHERE locktype = 'advisory' AND NOT granted AND objsubid = 1 " +
            "AND classid::bigint = @classid AND objid::bigint = @objid",
            barrierConnection);
        cmd.Parameters.AddWithValue("classid", (long)classId);
        cmd.Parameters.AddWithValue("objid", (long)objId);
        return (int)(long)(await cmd.ExecuteScalarAsync())!;
    }
}
