using System.Net;
using System.Net.Http.Json;
using backend.Auth;
using backend.Constants;
using backend.Controllers;
using backend.Data;
using backend.Enums;
using backend.Interfaces;
using backend.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace backend.Tests;

/// <summary>
/// GDPR Retention/Anonymization Lifecycle (docs/handoffs/gdpr-lifecycle-design.md)
/// — §9 final end-to-end verification. Not a new implementation phase:
/// Phases 1-4 (committed as c0cbc18, 8703c59, e35ed77, fe5124a) are each
/// already exhaustively proven at the service level
/// (CustomerDataLifecycleTests.cs) and, for the erasure endpoint's
/// authorization/tenant-isolation/idempotency contract, at the HTTP level
/// (CustomersApiTests.cs). This file proves the two real, non-faked
/// execution paths those existing suites were never built to exercise:
///
///   HTTP → CustomersController.Erase → CustomerErasureService, against a
///   realistic multi-entity fixture (every identity/security/logging
///   deletion-set row, Approvals spanning every status, multiple
///   conversations each with Messages) — CustomersApiTests.cs's own Erase
///   tests use a deliberately minimal fixture, since they exist to prove
///   the authorization/tenant-isolation contract, not the full deletion
///   set (already proven, but only at the service level, by
///   CustomerDataLifecycleTests.cs).
///
///   DataLifecycleBackgroundService → RetentionEnforcementService →
///   ConversationPurgeService, through the real Program.cs-registered host
///   (NivoraApiFactory) — covering the eligible/exempt/allowed/untouched
///   matrix Decision 3 requires, plus per-business RetentionDays tenant
///   isolation, mirroring DataLifecycleBackgroundServiceTests.cs's own
///   "RealProgramCsHost_..." test (which exists for
///   ConversationInactivityClose but has no retention-specific
///   counterpart — retention's own atomic-claim race coverage is,
///   likewise, currently proven only against a bare ServiceProvider, never
///   through the real hosted service).
///
/// Plus one interaction test proving Decision 3's active-Ticket retention
/// exemption does not transfer to GDPR erasure (design §0, Decision 3:
/// "Does not transfer"), and that the two mechanisms record distinct audit
/// event types for what is, underneath, the same ConversationPurgeService
/// call.
///
/// No production code is changed by this file. Every value asserted here
/// is produced exclusively by the real, already-shipped pipeline — nothing
/// in this file hand-constructs a Customer/Conversation.IsAnonymized
/// transition or an AuditLog row to look like one.
///
/// [Collection("ConversationAutoClose")]: this file seeds real,
/// deliberately-backdated Conversation rows and, in several tests, boots
/// the real NivoraApiFactory host — starting the real
/// DataLifecycleBackgroundService, whose RetentionEnforcementService job is
/// unconditionally live (no enabled flag, unlike auto-close) and whose
/// discovery query is deliberately cross-tenant (IgnoreQueryFilters(), no
/// BusinessId scoping). This is the identical class of shared-dev-DB race
/// ConversationAutoCloseTestCollection.cs already documents and fixes for
/// ConversationInactivityCloseService — retention shares the very same
/// tick and the very same risk, so this file joins that existing
/// collection rather than introducing a new one.
/// </summary>
[Collection("ConversationAutoClose")]
public class GdprLifecycleFinalE2ETests : IAsyncLifetime
{
    private readonly List<Guid> _businessIdsToClean = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_businessIdsToClean.Count == 0)
        {
            return;
        }

        await using var db = DbFor();
        var ids = _businessIdsToClean.ToArray();

        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"AuditLogs\" WHERE \"BusinessId\" = ANY({ids})");
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Tickets\" WHERE \"BusinessId\" = ANY({ids})");
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"MessageKnowledgeSources\" WHERE \"BusinessId\" = ANY({ids})");
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Messages\" WHERE \"BusinessId\" = ANY({ids})");
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"VerificationCodes\" WHERE \"BusinessId\" = ANY({ids})");
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"CustomerExtendedLoggingHistories\" WHERE \"BusinessId\" = ANY({ids})");
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"FlaggedCustomerActivityLogs\" WHERE \"BusinessId\" = ANY({ids})");
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"ExternalIdentities\" WHERE \"BusinessId\" = ANY({ids})");
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Conversations\" WHERE \"BusinessId\" = ANY({ids})");
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Customers\" WHERE \"BusinessId\" = ANY({ids})");
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Widgets\" WHERE \"BusinessId\" = ANY({ids})");
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"AspNetUserRoles\" WHERE \"UserId\" IN (SELECT \"Id\" FROM \"AspNetUsers\" WHERE \"BusinessId\" = ANY({ids}))");
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"AspNetUsers\" WHERE \"BusinessId\" = ANY({ids})");
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Businesses\" WHERE \"Id\" = ANY({ids})");
    }

    // ── Infra helpers (mirrors Step25Candidate2FinalE2ETests.cs / CustomersApiTests.cs) ──

    private static AppDbContext DbFor() =>
        new ServiceCollection()
            .AddSingleton<ICurrentTenantAccessor>(new FixedTenantAccessor(null))
            .AddDbContext<AppDbContext>(o => o.UseNpgsql(
                PostgresConnectionString.FromUri(new ConfigurationBuilder()
                    .AddUserSecrets<Program>().Build()
                    .GetConnectionString("DefaultConnection")!), x => x.UseVector()))
            .BuildServiceProvider()
            .GetRequiredService<AppDbContext>();

    private sealed class FixedTenantAccessor(Guid? businessId) : ICurrentTenantAccessor
    {
        public Guid? BusinessId => businessId;
    }

    private const string BusinessTimeZone = "Europe/Copenhagen";
    private const string Password = "Passw0rd!!";

    private static HttpClient NewClient(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });

    private static async Task<(HttpResponseMessage Response, RegisterResponse? Body)> RegisterAsync(
        HttpClient client, string businessName, string email)
    {
        var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(businessName, BusinessTimeZone, email, Password));
        var body = response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<RegisterResponse>() : null;
        return (response, body);
    }

    private static async Task ConfirmEmailAsync(WebApplicationFactory<Program> factory, HttpClient client, Guid userId)
    {
        string token;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await userManager.FindByIdAsync(userId.ToString());
            Assert.NotNull(user);
            token = await userManager.GenerateEmailConfirmationTokenAsync(user!);
        }

        var response = await client.PostAsJsonAsync("/api/auth/confirm-email", new ConfirmEmailRequest(userId, token));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static async Task<string> LoginAsync(WebApplicationFactory<Program> factory, string email)
    {
        var client = NewClient(factory);
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.First().Split(';')[0]
            : throw new InvalidOperationException("No Set-Cookie header on a successful login response.");
    }

    /// <summary>The real production path for creating a Business + its sole
    /// Owner: POST /api/auth/register, confirmed, logged in. Never a raw
    /// SQL/EF insert of either row. Returns the Owner's own email (not just
    /// a cookie) so a caller can re-authenticate against a DIFFERENT,
    /// later-created NivoraApiFactory instance — a cookie minted by one
    /// WebApplicationFactory's own Data Protection key ring is not assumed
    /// portable to another.</summary>
    private async Task<(Guid BusinessId, Guid OwnerId, string Email, string Cookie)> RegisterBusinessWithOwnerAsync(
        WebApplicationFactory<Program> factory, string businessName)
    {
        var email = $"gdpr-e2e-{Guid.NewGuid():N}@example.com";
        using var anonClient = NewClient(factory);
        var (registerResponse, registerBody) = await RegisterAsync(anonClient, businessName, email);
        Assert.Equal(HttpStatusCode.OK, registerResponse.StatusCode);
        Assert.NotNull(registerBody);
        _businessIdsToClean.Add(registerBody!.BusinessId);

        await ConfirmEmailAsync(factory, anonClient, registerBody.UserId);
        var cookie = await LoginAsync(factory, email);

        return (registerBody.BusinessId, registerBody.UserId, email, cookie);
    }

    private static async Task<ApplicationUser> SeedStaffAsync(WebApplicationFactory<Program> factory, Guid businessId, string role)
    {
        var email = $"gdpr-e2e-staff-{Guid.NewGuid():N}@example.com";
        await using var scope = factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(), BusinessId = businessId, UserName = email, Email = email,
            EmailConfirmed = true, CreatedAt = DateTimeOffset.UtcNow,
        };
        var createResult = await userManager.CreateAsync(user, Password);
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

    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition, int timeoutSeconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(200);
        }

        return false;
    }

    // ── Fixture seeding (raw db, mirrors CustomerDataLifecycleTests.cs) ────

    private static async Task<Guid> SeedWidgetAsync(Guid businessId)
    {
        await using var db = DbFor();
        var widget = new Widget
        {
            Id = Guid.NewGuid(), BusinessId = businessId, Name = "GDPR E2E Widget",
            PublicKey = $"gdpr-e2e-{Guid.NewGuid():N}", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Widgets.Add(widget);
        await db.SaveChangesAsync();
        return widget.Id;
    }

    private static async Task<Guid> SeedCustomerAsync(Guid businessId)
    {
        await using var db = DbFor();
        var customer = new Customer
        {
            Id = Guid.NewGuid(), BusinessId = businessId,
            Email = $"customer-{Guid.NewGuid():N}@example.com", Name = "Jane Doe",
            ExternalId = $"shopify-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        return customer.Id;
    }

    private static async Task<Guid> SeedConversationAsync(
        Guid businessId, Guid widgetId, Guid? customerId, DateTimeOffset createdAt, DateTimeOffset? lastMessageAt,
        bool isAnonymized = false)
    {
        await using var db = DbFor();
        var conversation = new Conversation
        {
            Id = Guid.NewGuid(), BusinessId = businessId, WidgetId = widgetId, CustomerId = customerId,
            VisitorSessionId = isAnonymized ? null : $"session-{Guid.NewGuid():N}", Channel = ConversationChannels.Website,
            Status = ConversationStatus.Open, HandledBy = HandledBy.Ai,
            CreatedAt = createdAt, UpdatedAt = createdAt, LastMessageAt = lastMessageAt,
            IsAnonymized = isAnonymized, AnonymizedAt = isAnonymized ? createdAt : null,
        };
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync();
        return conversation.Id;
    }

    private static async Task SeedMessageAsync(Guid businessId, Guid conversationId, DateTimeOffset createdAt)
    {
        await using var db = DbFor();
        db.Messages.Add(new Message
        {
            Id = Guid.NewGuid(), BusinessId = businessId, ConversationId = conversationId,
            Role = MessageRole.Customer, Content = "Where is my order?", CreatedAt = createdAt,
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedTicketAsync(Guid businessId, Guid customerId, Guid conversationId, TicketStatus status)
    {
        await using var db = DbFor();
        db.Tickets.Add(new Ticket
        {
            Id = Guid.NewGuid(), BusinessId = businessId, CustomerId = customerId, ConversationId = conversationId,
            Status = status, Priority = TicketPriority.Medium, PriorityReason = TicketPriorityReasons.ManualEscalation,
            Channel = ConversationChannels.Website, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedApprovalAsync(Guid businessId, Guid conversationId, Guid customerId, ApprovalStatus status)
    {
        await using var db = DbFor();
        db.Approvals.Add(new Approval
        {
            Id = Guid.NewGuid(), BusinessId = businessId, ConversationId = conversationId, CustomerId = customerId,
            ToolName = "cancelOrder", ResourceId = $"order-{Guid.NewGuid():N}", CanonicalArguments = "{}",
            ActionHash = Guid.NewGuid().ToString("N"), DisplaySummary = "GDPR E2E test approval",
            Status = status, CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Seeds one of each explicit-deletion-set row (Phase 1's four
    /// ExecuteDeleteAsync targets) for one customer.</summary>
    private static async Task SeedIdentityCleanupRowsAsync(Guid businessId, Guid customerId, Guid conversationId, Guid enabledByUserId)
    {
        await using var db = DbFor();
        db.ExternalIdentities.Add(new ExternalIdentity
        {
            Id = Guid.NewGuid(), BusinessId = businessId, CustomerId = customerId,
            Channel = ConversationChannels.Website, VerificationMethod = IdentityVerificationMethods.SignedToken,
            ExternalUserId = $"ext-{Guid.NewGuid():N}", LinkedAt = DateTimeOffset.UtcNow,
        });
        db.FlaggedCustomerActivityLogs.Add(new FlaggedCustomerActivityLog
        {
            Id = Guid.NewGuid(), BusinessId = businessId, CustomerId = customerId, ConversationId = conversationId,
            ToolName = "getOrder", Actor = ActorType.Ai, CreatedAt = DateTimeOffset.UtcNow,
        });
        db.CustomerExtendedLoggingHistories.Add(new CustomerExtendedLoggingHistory
        {
            Id = Guid.NewGuid(), BusinessId = businessId, CustomerId = customerId,
            EnabledByUserId = enabledByUserId, EnabledAt = DateTimeOffset.UtcNow,
        });
        db.VerificationCodes.Add(new VerificationCode
        {
            Id = Guid.NewGuid(), BusinessId = businessId, CustomerId = customerId, ConversationId = conversationId,
            Channel = ConversationChannels.Website, CodeHash = "gdpr-e2e-hash", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
        });
        await db.SaveChangesAsync();
    }

    private static async Task<bool> IsPurgedAsync(Guid conversationId)
    {
        await using var db = DbFor();
        return await db.Conversations.IgnoreQueryFilters()
            .Where(c => c.Id == conversationId).Select(c => c.IsAnonymized).SingleAsync();
    }

    private static async Task<int> RetentionAuditCountAsync(Guid conversationId)
    {
        await using var db = DbFor();
        return await db.AuditLogs.IgnoreQueryFilters()
            .CountAsync(a => a.EntityId == conversationId.ToString() && a.EventType == AuditEventTypes.ConversationRetentionPurged);
    }

    // ── 1. Erasure — real HTTP endpoint, full realistic fixture ───────────

    /// <summary>
    /// The full HTTP-level proof §9 requires: every deletion-set entity
    /// (ExternalIdentity, FlaggedCustomerActivityLog,
    /// CustomerExtendedLoggingHistory, VerificationCode), Approvals
    /// spanning all four statuses across two conversations, both
    /// conversations' own Messages — all produced by one real
    /// POST /api/customers/{id}/erase call, verified from a separate
    /// DbContext. CustomersApiTests.cs's own
    /// Erase_OwnerOrAdmin_Succeeds_AnonymizesCustomer_AuditChangedByUserIdCorrect
    /// already proves the authorization/audit-correlation contract with a
    /// minimal fixture; this test proves the SAME real endpoint reaches the
    /// full deletion set, not just Customer + one Conversation.
    /// </summary>
    [Fact]
    public async Task Erasure_RealHttpEndpoint_FullRealisticFixture_EntireDeletionSetAndApprovalStatusSplitCorrect()
    {
        await using var factory = new NivoraApiFactory();
        var (businessId, ownerId, _, ownerCookie) = await RegisterBusinessWithOwnerAsync(factory, "GDPR E2E Erasure Business");

        var customerId = await SeedCustomerAsync(businessId);
        var widgetId = await SeedWidgetAsync(businessId);
        var now = DateTimeOffset.UtcNow;

        var conversation1Id = await SeedConversationAsync(businessId, widgetId, customerId, now, now);
        await SeedMessageAsync(businessId, conversation1Id, now);
        var conversation2Id = await SeedConversationAsync(businessId, widgetId, customerId, now, now);
        await SeedMessageAsync(businessId, conversation2Id, now);

        await SeedIdentityCleanupRowsAsync(businessId, customerId, conversation1Id, ownerId);

        // Decision 4 — Approvals spanning every status, spread across both
        // conversations, so both the "owns" purge branch's Approval cleanup
        // and Phase 1's customer-scoped belt-and-braces delete are exercised.
        await SeedApprovalAsync(businessId, conversation1Id, customerId, ApprovalStatus.Pending);
        await SeedApprovalAsync(businessId, conversation1Id, customerId, ApprovalStatus.Denied);
        await SeedApprovalAsync(businessId, conversation2Id, customerId, ApprovalStatus.Approved);
        await SeedApprovalAsync(businessId, conversation2Id, customerId, ApprovalStatus.Consumed);

        using var ownerClient = NewClient(factory);
        var response = await ownerClient.SendAsync(Authed(HttpMethod.Post, $"/api/customers/{customerId}/erase", ownerCookie));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // Verify from a genuinely separate DbContext/scope — never the
        // tracked entities any prior step touched.
        await using var verifyDb = DbFor();

        var customer = await verifyDb.Customers.IgnoreQueryFilters().SingleAsync(c => c.Id == customerId);
        Assert.True(customer.IsAnonymized);
        Assert.NotNull(customer.AnonymizedAt);
        Assert.Null(customer.Email);
        Assert.Null(customer.Name);
        Assert.Null(customer.ExternalId);

        Assert.False(await verifyDb.ExternalIdentities.IgnoreQueryFilters().AnyAsync(e => e.CustomerId == customerId));
        Assert.False(await verifyDb.FlaggedCustomerActivityLogs.IgnoreQueryFilters().AnyAsync(f => f.CustomerId == customerId));
        Assert.False(await verifyDb.CustomerExtendedLoggingHistories.IgnoreQueryFilters().AnyAsync(h => h.CustomerId == customerId));
        Assert.False(await verifyDb.VerificationCodes.IgnoreQueryFilters().AnyAsync(v => v.CustomerId == customerId));

        var remainingApprovals = await verifyDb.Approvals.IgnoreQueryFilters()
            .Where(a => a.CustomerId == customerId).ToListAsync();
        var survivingApproval = Assert.Single(remainingApprovals);
        Assert.Equal(ApprovalStatus.Consumed, survivingApproval.Status);

        foreach (var conversationId in new[] { conversation1Id, conversation2Id })
        {
            var conversation = await verifyDb.Conversations.IgnoreQueryFilters().SingleAsync(c => c.Id == conversationId);
            Assert.True(conversation.IsAnonymized);
            Assert.Null(conversation.VisitorSessionId);
            Assert.False(await verifyDb.Messages.IgnoreQueryFilters().AnyAsync(m => m.ConversationId == conversationId));
        }

        var completionAudit = await verifyDb.AuditLogs.IgnoreQueryFilters()
            .SingleAsync(a => a.EntityId == customerId.ToString() && a.EventType == AuditEventTypes.CustomerErased);
        Assert.Equal(AuditEntityTypes.Customer, completionAudit.EntityType);
        Assert.Equal(ActorType.NivoraBusinessStaff, completionAudit.Actor);
        Assert.Equal(ownerId, completionAudit.ChangedByUserId);
        Assert.NotNull(completionAudit.CorrelationId);

        var conversationAudits = await verifyDb.AuditLogs.IgnoreQueryFilters()
            .Where(a => a.EventType == AuditEventTypes.ConversationErasurePurged && a.BusinessId == businessId)
            .ToListAsync();
        Assert.Equal(2, conversationAudits.Count);
        Assert.All(conversationAudits, a =>
        {
            Assert.Equal(AuditEntityTypes.Conversation, a.EntityType);
            Assert.Equal(ActorType.NivoraBusinessStaff, a.Actor);
            Assert.Equal(ownerId, a.ChangedByUserId);
            Assert.Equal(completionAudit.CorrelationId, a.CorrelationId);
        });
        Assert.Equal(
            new[] { conversation1Id.ToString(), conversation2Id.ToString() }.OrderBy(x => x),
            conversationAudits.Select(a => a.EntityId).OrderBy(x => x));
    }

    // ── 2. Erasure — Admin role (Owner already proven by CustomersApiTests) ─

    [Fact]
    public async Task Erasure_AdminRole_RealHttpEndpoint_Succeeds_AnonymizesCustomer_AuditReflectsAdmin()
    {
        await using var factory = new NivoraApiFactory();
        var (businessId, _, _, _) = await RegisterBusinessWithOwnerAsync(factory, "GDPR E2E Admin Business");
        var admin = await SeedStaffAsync(factory, businessId, RoleNames.Admin);

        var customerId = await SeedCustomerAsync(businessId);

        using var adminClient = NewClient(factory);
        var adminCookie = await LoginAsync(factory, admin.Email!);

        var response = await adminClient.SendAsync(Authed(HttpMethod.Post, $"/api/customers/{customerId}/erase", adminCookie));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await using var verifyDb = DbFor();
        var customer = await verifyDb.Customers.IgnoreQueryFilters().SingleAsync(c => c.Id == customerId);
        Assert.True(customer.IsAnonymized);

        var completionAudit = await verifyDb.AuditLogs.IgnoreQueryFilters()
            .SingleAsync(a => a.EntityId == customerId.ToString() && a.EventType == AuditEventTypes.CustomerErased);
        Assert.Equal(admin.Id, completionAudit.ChangedByUserId);
    }

    // ── 3. Retention — real DataLifecycleBackgroundService, real Program.cs
    //    DI host. Mirrors DataLifecycleBackgroundServiceTests.cs's own
    //    "RealProgramCsHost_..." test, which covers ConversationInactivityClose
    //    but has no retention-specific counterpart. ─────────────────────────

    [Fact]
    public async Task Retention_RealBackgroundService_EligiblePurged_ActiveTicketExempt_ResolvedTicketPurged_NotOldUntouched_ZeroMessageFallback_AlreadyAnonymizedUntouched_PerBusinessRetentionDaysIsolated()
    {
        const int retentionDaysA = 30;
        var now = DateTimeOffset.UtcNow;
        var pastCutoffA = now.AddDays(-(retentionDaysA + 10)); // 40 days
        var withinCutoffA = now.AddDays(-5);

        Guid businessAId;
        await using (var seedDb = DbFor())
        {
            var businessA = new Business
            {
                Id = Guid.NewGuid(), Name = $"GDPR E2E Retention A {Guid.NewGuid():N}", TimeZone = BusinessTimeZone,
                RetentionDays = retentionDaysA, CreatedAt = now, UpdatedAt = now,
            };
            seedDb.Businesses.Add(businessA);
            await seedDb.SaveChangesAsync();
            businessAId = businessA.Id;
        }
        _businessIdsToClean.Add(businessAId);

        var widgetAId = await SeedWidgetAsync(businessAId);
        var customerAId = await SeedCustomerAsync(businessAId);

        var eligibleId = await SeedConversationAsync(businessAId, widgetAId, customerAId, pastCutoffA, pastCutoffA);

        var activeTicketConvId = await SeedConversationAsync(businessAId, widgetAId, customerAId, pastCutoffA, pastCutoffA);
        await SeedTicketAsync(businessAId, customerAId, activeTicketConvId, TicketStatus.Open);

        var resolvedTicketConvId = await SeedConversationAsync(businessAId, widgetAId, customerAId, pastCutoffA, pastCutoffA);
        await SeedTicketAsync(businessAId, customerAId, resolvedTicketConvId, TicketStatus.Resolved);

        var notOldConvId = await SeedConversationAsync(businessAId, widgetAId, customerAId, withinCutoffA, withinCutoffA);

        var zeroMessageConvId = await SeedConversationAsync(businessAId, widgetAId, customerAId, pastCutoffA, null);

        var alreadyAnonymizedConvId = await SeedConversationAsync(
            businessAId, widgetAId, customerAId, pastCutoffA, pastCutoffA, isAnonymized: true);

        // Business B — default (180-day) RetentionDays, same age as A's
        // eligible target — proves retention respects each business's own
        // RetentionDays independently, not a shared/global cutoff.
        Guid businessBId;
        await using (var seedDb = DbFor())
        {
            var businessB = new Business
            {
                Id = Guid.NewGuid(), Name = $"GDPR E2E Retention B {Guid.NewGuid():N}", TimeZone = BusinessTimeZone,
                CreatedAt = now, UpdatedAt = now, // RetentionDays defaults to RetentionPolicyLimits.DefaultDays (180)
            };
            seedDb.Businesses.Add(businessB);
            await seedDb.SaveChangesAsync();
            businessBId = businessB.Id;
        }
        _businessIdsToClean.Add(businessBId);
        var widgetBId = await SeedWidgetAsync(businessBId);
        var customerBId = await SeedCustomerAsync(businessBId);
        var untouchedOtherBusinessConvId = await SeedConversationAsync(businessBId, widgetBId, customerBId, pastCutoffA, pastCutoffA);

        // NOW start the real verification host, for the first time, only
        // once every business and conversation already exists — real
        // Program.cs DI registration, real DataLifecycleBackgroundService,
        // whose first tick runs immediately (before any 24h wait).
        await using var factory = new NivoraApiFactory { HostedServices = TestHostedServices.DataLifecycle };
        _ = factory.Services;

        var settled = await WaitUntilAsync(async () =>
            await IsPurgedAsync(eligibleId) && await IsPurgedAsync(resolvedTicketConvId) && await IsPurgedAsync(zeroMessageConvId));
        Assert.True(settled, "Retention did not settle within the timeout through the real host.");

        Assert.True(await IsPurgedAsync(eligibleId));
        Assert.Equal(1, await RetentionAuditCountAsync(eligibleId));

        Assert.False(await IsPurgedAsync(activeTicketConvId)); // Decision 3 — active Ticket exemption
        Assert.Equal(0, await RetentionAuditCountAsync(activeTicketConvId));

        Assert.True(await IsPurgedAsync(resolvedTicketConvId)); // Resolved Ticket does not block purge
        Assert.Equal(1, await RetentionAuditCountAsync(resolvedTicketConvId));

        Assert.False(await IsPurgedAsync(notOldConvId));
        Assert.Equal(0, await RetentionAuditCountAsync(notOldConvId));

        Assert.True(await IsPurgedAsync(zeroMessageConvId)); // CreatedAt fallback (no messages, LastMessageAt null)
        Assert.Equal(1, await RetentionAuditCountAsync(zeroMessageConvId));

        Assert.Equal(0, await RetentionAuditCountAsync(alreadyAnonymizedConvId)); // never reprocessed, no duplicate audit

        Assert.False(await IsPurgedAsync(untouchedOtherBusinessConvId)); // Business B's own 180-day window protects it
        Assert.Equal(0, await RetentionAuditCountAsync(untouchedOtherBusinessConvId));

        // Audit content correctness.
        await using var verifyDb = DbFor();
        var entry = await verifyDb.AuditLogs.IgnoreQueryFilters()
            .SingleAsync(a => a.EntityId == eligibleId.ToString() && a.EventType == AuditEventTypes.ConversationRetentionPurged);
        Assert.Equal(AuditEntityTypes.Conversation, entry.EntityType);
        Assert.Equal(ActorType.NivoraSystem, entry.Actor);
        Assert.Null(entry.ChangedByUserId);
        Assert.Null(entry.CorrelationId); // retention is not correlated across rows the way erasure is
    }

    // ── 4. Interaction — Decision 3's active-Ticket exemption does not
    //    transfer to GDPR erasure; the two mechanisms record distinct audit
    //    event types for the same underlying ConversationPurgeService call. ─

    [Fact]
    public async Task Interaction_ActiveTicketRetentionExemptionDoesNotBlockGdprErasure_DistinctAuditEventTypes()
    {
        const int retentionDays = 30;
        var now = DateTimeOffset.UtcNow;
        var pastCutoff = now.AddDays(-(retentionDays + 10));

        Guid businessId, ownerId, customerId, conversationId;
        string email;

        await using (var setupFactory = new NivoraApiFactory())
        {
            (businessId, ownerId, email, _) = await RegisterBusinessWithOwnerAsync(setupFactory, "GDPR E2E Interaction Business");
        }

        // Lower RetentionDays after registration so the conversation below
        // is genuinely retention-eligible by age — no HTTP endpoint in this
        // design changes RetentionDays; raw db, same as
        // CustomerDataLifecycleTests.cs's own CreateBusinessAsync(retentionDays:).
        await using (var db = DbFor())
        {
            var business = await db.Businesses.IgnoreQueryFilters().SingleAsync(b => b.Id == businessId);
            business.RetentionDays = retentionDays;
            await db.SaveChangesAsync();
        }

        var widgetId = await SeedWidgetAsync(businessId);
        customerId = await SeedCustomerAsync(businessId);
        conversationId = await SeedConversationAsync(businessId, widgetId, customerId, pastCutoff, pastCutoff);
        await SeedMessageAsync(businessId, conversationId, pastCutoff);
        await SeedTicketAsync(businessId, customerId, conversationId, TicketStatus.Open);

        // Start the real host — real DataLifecycleBackgroundService, first
        // tick runs immediately, RetentionEnforcementService is
        // unconditionally live (unlike auto-close, no enabled flag).
        await using var factory = new NivoraApiFactory { HostedServices = TestHostedServices.DataLifecycle };
        _ = factory.Services;

        // Bounded grace window (Step25's own "prove absence" convention) —
        // retention's atomic claim must find the active Ticket and exempt
        // this conversation, exactly as Decision 3 requires.
        await Task.Delay(TimeSpan.FromSeconds(5));
        Assert.False(await IsPurgedAsync(conversationId));
        Assert.Equal(0, await RetentionAuditCountAsync(conversationId));

        // Now erase the customer via the real HTTP endpoint — Decision 3's
        // active-Ticket exemption is retention-only and "does not transfer"
        // to GDPR erasure (design §0, Decision 3).
        using var ownerClient = NewClient(factory);
        var ownerCookie = await LoginAsync(factory, email);
        var response = await ownerClient.SendAsync(Authed(HttpMethod.Post, $"/api/customers/{customerId}/erase", ownerCookie));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await using var verifyDb = DbFor();
        var conversation = await verifyDb.Conversations.IgnoreQueryFilters().SingleAsync(c => c.Id == conversationId);
        Assert.True(conversation.IsAnonymized); // erasure purges it despite the still-active Ticket
        Assert.False(await verifyDb.Messages.IgnoreQueryFilters().AnyAsync(m => m.ConversationId == conversationId));

        // Distinct audit event types — never ConversationRetentionPurged for
        // this conversation, exactly one ConversationErasurePurged.
        Assert.Equal(0, await RetentionAuditCountAsync(conversationId));
        var erasureAudit = await verifyDb.AuditLogs.IgnoreQueryFilters()
            .SingleAsync(a => a.EntityId == conversationId.ToString() && a.EventType == AuditEventTypes.ConversationErasurePurged);
        Assert.Equal(ActorType.NivoraBusinessStaff, erasureAudit.Actor);
        Assert.Equal(ownerId, erasureAudit.ChangedByUserId);
    }
}
