using System.Net;
using backend.Constants;
using backend.Data;
using backend.Enums;
using backend.Interfaces;
using backend.Models;
using backend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace backend.Tests;

/// <summary>
/// Service-level tests for OrderCancelApprovedActionExecutor, run against
/// the real Neon dev DB via a bare ServiceProvider, same pattern as
/// OrderCancelProposalToolTests. Covers steps 3-13 of the execution ordering
/// (docs/handoffs/step-22z-approval-proposal-approval-lifecycle-design.md
/// §10): the Approval load, ApprovalNotFound refusal (including the
/// tenant-fold-in property independently confirmed by
/// docs/handoffs/step-24/25/26), the Status/ExpiresAt/CustomerId checks
/// added by docs/handoffs/step-38-cancelorder-execute-next-slice-design.md
/// §6 (steps 6/7/8, with an AuditLog write required for steps 6/7 but
/// deliberately not for step 8), the Level 2 verification check added by
/// docs/handoffs/step-39-cancelorder-execute-steps-9-16-next-slice-design.md
/// §7 (step 9, with an AuditLog write per the explicit owner decision
/// recorded in that document's §5a), the ownership verification check added
/// by docs/handoffs/step-41-cancelorder-execute-steps-10-16-classification-and-atomic-claim-design.md
/// §6 (step 10, with an AuditLog write, endorsing
/// docs/handoffs/step-40-cancelorder-execute-step-10-ownership-design.md
/// §4/§5), the action-hash recompute and execution-leg key derivation added
/// by docs/handoffs/step-42-cancelorder-execute-steps-11-12-classification-and-identity-collapse-analysis.md
/// §8 (steps 11/12, step 11 with an AuditLog write, step 12 with none —
/// it is a pure, always-succeeding derivation with no observable output in
/// this slice), and the atomic claim + OrderCancellation row creation added
/// by docs/handoffs/step-43-cancelorder-execute-step-13-atomic-claim-design.md
/// (step 13, with a required AuditLog write on success, none on either of
/// its own two refusal paths), plus one boundary test documenting that
/// everything past step 13 still throws NotImplementedException.
///
/// Uses a MutableTenantAccessor (same shape as AppDbContextTenantFilterTests'
/// own private nested class — not shared, per this codebase's established
/// per-test-file convention), not a FixedTenantAccessor, because these
/// tests specifically need to move the ambient tenant between a real,
/// non-null value and a *different* real, non-null value within the same
/// test — the exact scenario the executor's cross-tenant guarantee is
/// about.
///
/// Step 10 requires IShopifyOrderLookupService, which needs
/// IIntegrationTokenProtector (real data-protection stack, same convention
/// as ShopifyOrderLookupServiceTests) and an HttpClient — a
/// FakeShopifyHttpMessageHandler (same class ShopifyOrderCancellationExecutorTests/
/// ShopifyOrderLookupServiceTests already use) is wired in per test via
/// NewOrderLookupService, never a real network call. Tests that must never
/// reach step 10 (every step 6-9 refusal, plus ApprovalNotFound/cross-tenant)
/// are given a lookup service whose handler throws if invoked at all
/// (ThrowingOrderLookupService) — this both satisfies the constructor's now-
/// mandatory parameter and affirmatively proves no Shopify call happens on
/// those paths, since a real invocation would fail the test.
/// </summary>
public class OrderCancelApprovedActionExecutorTests : IAsyncLifetime
{
    private const string AdminApiVersion = "2026-07";
    private const string AccessToken = "test-access-token";

    private ServiceProvider _provider = null!;
    private readonly List<Guid> _businessIdsToClean = [];
    private readonly List<Guid> _userIdsToClean = [];

    public Task InitializeAsync()
    {
        var configuration = new ConfigurationBuilder().AddUserSecrets<Program>().Build();
        var connectionUri = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection not found via user-secrets.");

        var services = new ServiceCollection()
            .AddSingleton<ICurrentTenantAccessor>(new MutableTenantAccessor())
            .AddDbContext<AppDbContext>(o => o.UseNpgsql(
                PostgresConnectionString.FromUri(connectionUri), x => x.UseVector()))
            .AddScoped<IAuditLogService, AuditLogService>()
            .AddScoped<ILevel2VerificationChecker, Level2VerificationChecker>()
            .AddScoped<IOrderCancellationService, OrderCancellationService>();
        services.AddDataProtection();
        services.AddScoped<IIntegrationTokenProtector, IntegrationTokenProtector>();
        _provider = services.BuildServiceProvider();

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_businessIdsToClean.Count > 0)
        {
            await using var scope = _provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var ids = _businessIdsToClean.ToArray();

            // AuditLogs first — AuditLog.BusinessId's FK is Restrict, not
            // Cascade/SetNull, so any row this test wrote would otherwise
            // block the Businesses delete below with a FK violation.
            // Conversations next — Approval's own ConversationId FK is
            // Cascade, so any Approval row (including its own
            // OrderCancellationId reference) is removed automatically when
            // its Conversation is. OrderCancellations next, only now safe
            // to delete: Approval.OrderCancellationId's FK is Restrict, so
            // it must be gone (via the Conversation cascade above) before
            // any OrderCancellation row it pointed to can be removed; both
            // OrderCancellation.CustomerId and .BusinessId are themselves
            // Restrict, so OrderCancellations must in turn be deleted
            // before Customers/Businesses below (added by step-43's
            // implementation pass — no test before it ever created an
            // OrderCancellation row, so this ordering requirement was
            // never previously exercised). Integrations/Customers/Widgets
            // before Businesses.
            // AspNetUsers next — AuditLog.ChangedByUserId now has a real FK
            // to AspNetUsers (FK_AuditLogs_AspNetUsers_ChangedByUserId),
            // discovered by this session's identity-threading fix (previously
            // never exercised, since ChangedByUserId was always null before
            // step-45). Must run after the AuditLogs delete above and before
            // Businesses below, same ordering convention as
            // NotificationDataTests.cs's own DisposeAsync.
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"AuditLogs\" WHERE \"BusinessId\" = ANY({ids})");
            if (_userIdsToClean.Count > 0)
            {
                var userIds = _userIdsToClean.ToArray();
                await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"AspNetUsers\" WHERE \"Id\" = ANY({userIds})");
            }
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Conversations\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"OrderCancellations\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Integrations\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Customers\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Widgets\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Businesses\" WHERE \"Id\" = ANY({ids})");
        }

        await _provider.DisposeAsync();
    }

    /// <summary>
    /// Seeds one Business/Widget/Customer/Conversation/Approval graph.
    /// `status`/`expiresAt`/`customerResolved`/`level2Verified` let each
    /// test target exactly one of steps 6/7/8/9's own branches without
    /// touching the others — defaults (Approved, 15 minutes out, resolved
    /// customer, Level 2 verified) are the "everything passes" case the
    /// existing NotImplementedException test relies on. `level2Verified`
    /// sets `Conversation.Level2VerifiedUntil` to one hour in the future
    /// when true (matching `Level2VerificationChecker.IsVerifiedAsync`'s
    /// own `until > now` check), or leaves it null when false — no
    /// `ExternalIdentity` row is seeded either way, so a false value always
    /// fails verification via both of that checker's own paths.
    /// `customerExternalId`, added for step 10, sets `Customer.ExternalId`
    /// (null by default — CustomerNotLinkedToExternalIdentity would fire if
    /// a step-10 test reached AuthorizeAsync without setting this).
    /// `actionHashCustomerIdOverride`, added for step 11, lets a test store
    /// an `Approval.ActionHash` computed against a *different* CustomerId
    /// than the one actually linked to the Conversation — simulating the
    /// one genuine drift step 11's recompute can detect (per step-24 §2a:
    /// with `Execute`'s locked, `approvalId`-only signature, `CustomerId`
    /// is the only one of the six hash inputs ever re-derived from a live
    /// source, so a mismatch can only ever mean customer-identity drift
    /// since proposal time). `null` (default) means the hash is computed
    /// against the real `customer.Id`, matching what `Execute` will
    /// recompute — the "everything passes" case every other test relies on.
    /// </summary>
    private async Task<(Business Business, Approval Approval, Customer Customer)> SeedApprovalAsync(
        AppDbContext db,
        ApprovalStatus status = ApprovalStatus.Approved,
        DateTimeOffset? expiresAt = null,
        bool customerResolved = true,
        bool level2Verified = true,
        string? customerExternalId = null,
        Guid? actionHashCustomerIdOverride = null)
    {
        var now = DateTimeOffset.UtcNow;
        var business = new Business
        {
            Id = Guid.NewGuid(),
            Name = $"OrderCancelApprovedActionExecutor Test {Guid.NewGuid():N}",
            TimeZone = "Europe/Copenhagen",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var widget = new Widget
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id,
            Name = "Test Widget",
            PublicKey = Guid.NewGuid().ToString("N"),
            CreatedAt = now,
            UpdatedAt = now,
        };
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id,
            Name = "Test Customer",
            Email = "test.customer@marker-test.invalid",
            ExternalId = customerExternalId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id,
            WidgetId = widget.Id,
            CustomerId = customerResolved ? customer.Id : null,
            VisitorSessionId = Guid.NewGuid().ToString("N"),
            Channel = "Website",
            Status = ConversationStatus.Open,
            Level2VerifiedUntil = level2Verified ? now.AddHours(1) : null,
            CreatedAt = now,
            UpdatedAt = now,
        };

        var canonicalArguments = ToolArgumentCanonicalizer.CanonicalizeOrderCancellationArgs(
            restock: true, method: RefundMethod.OriginalPaymentMethod, reason: OrderCancelReason.Customer);
        var approval = new Approval
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id,
            ConversationId = conversation.Id,
            CustomerId = customer.Id,
            ToolName = "cancelOrder",
            ResourceId = "gid://shopify/Order/12345",
            CanonicalArguments = canonicalArguments,
            ActionHash = ActionHasher.Derive(
                business.Id, conversation.Id, actionHashCustomerIdOverride ?? customer.Id,
                "cancelOrder", "gid://shopify/Order/12345", canonicalArguments),
            DisplaySummary = "Cancel order gid://shopify/Order/12345 (Customer, restocking inventory, refund to original payment method).",
            Status = status,
            CreatedAt = now,
            ExpiresAt = expiresAt ?? now.AddMinutes(15),
        };

        db.Businesses.Add(business);
        db.Widgets.Add(widget);
        db.Customers.Add(customer);
        db.Conversations.Add(conversation);
        db.Approvals.Add(approval);
        await db.SaveChangesAsync();
        _businessIdsToClean.Add(business.Id);

        return (business, approval, customer);
    }

    /// <summary>
    /// Seeds a real ApplicationUser row to stand in for "the executing
    /// staff member" — required because AuditLog.ChangedByUserId carries a
    /// real FK to AspNetUsers (FK_AuditLogs_AspNetUsers_ChangedByUserId);
    /// an arbitrary Guid.NewGuid() with no backing row fails that
    /// constraint the moment a write actually reaches AuditLogs. Same
    /// minimal shape as NotificationDataTests.CreateStaffUserAsync — a
    /// direct db.Users.Add, not the full UserManager.CreateAsync path,
    /// since no password/role/email-confirmation behavior is under test
    /// here, only the FK's existence.
    /// </summary>
    private async Task<Guid> CreateStaffUserAsync(AppDbContext db, Guid businessId)
    {
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            UserName = $"staff-{Guid.NewGuid():N}@marker-test.invalid",
            Email = $"staff-{Guid.NewGuid():N}@marker-test.invalid",
            EmailConfirmed = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        _userIdsToClean.Add(user.Id);
        return user.Id;
    }

    /// <summary>
    /// Seeds an active Shopify Integration for the business — required
    /// before IShopifyOrderLookupService.LookupOrderOwnerAsync can succeed
    /// (it throws InvalidOperationException otherwise). Same shape as
    /// ShopifyOrderLookupServiceTests.CreateBusinessWithIntegrationAsync.
    /// </summary>
    private static async Task AddIntegrationAsync(AppDbContext db, IIntegrationTokenProtector tokenProtector, Guid businessId)
    {
        db.Integrations.Add(new Integration
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            Platform = ShopifyIntegrationOAuthProvider.PlatformName,
            EncryptedAccessToken = tokenProtector.Protect(ShopifyIntegrationOAuthProvider.PlatformName, AccessToken),
            ExternalAccountIdentifier = $"test-shop-{Guid.NewGuid():N}",
            IsActive = true,
            ConnectedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Constructs a real ShopifyOrderLookupService wired to
    /// FakeShopifyHttpMessageHandler — never a real network call. Same
    /// pattern as ShopifyOrderLookupServiceTests.NewService.
    /// </summary>
    private static ShopifyOrderLookupService NewOrderLookupService(
        IServiceProvider scopedProvider, Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(
            new HttpClient(new FakeShopifyHttpMessageHandler(respond)),
            scopedProvider.GetRequiredService<AppDbContext>(),
            scopedProvider.GetRequiredService<IIntegrationTokenProtector>(),
            Options.Create(new ShopifyOptions
            {
                ClientId = "unused", ClientSecret = "unused", RedirectUri = "unused", AdminApiVersion = AdminApiVersion,
            }));

    /// <summary>
    /// For every test whose refusal must fire strictly before step 10 (or
    /// that never reaches step 10 at all) — a lookup service whose handler
    /// throws if the executor ever actually sends an HTTP request through
    /// it, proving step 10's Shopify call is genuinely never made on these
    /// paths, not merely untested.
    /// </summary>
    private static ShopifyOrderLookupService ThrowingOrderLookupService(IServiceProvider scopedProvider) =>
        NewOrderLookupService(scopedProvider, _ => throw new InvalidOperationException(
            "Step 10 must not be reached — this test's Approval should refuse at an earlier step."));

    private static string OrderOwnerResponse(string orderId, string? customerId) =>
        customerId is null
            ? $$"""{ "data": { "order": { "id": "{{orderId}}", "customer": null } } }"""
            : $$"""{ "data": { "order": { "id": "{{orderId}}", "customer": { "id": "{{customerId}}" } } } }""";

    /// <summary>
    /// Step 14's own ground-truth query response shape — same shape
    /// OrderCancellationReconciliationServiceTests.CancellationStateResponse
    /// already uses, replicated here rather than shared, matching this
    /// codebase's existing convention of not factoring test-fixture JSON
    /// builders across unrelated test classes.
    /// </summary>
    private static string CancellationStateResponse(string orderId, DateTimeOffset? cancelledAt) =>
        cancelledAt is null
            ? $$"""{ "data": { "order": { "id": "{{orderId}}", "cancelledAt": null } } }"""
            : $$"""{ "data": { "order": { "id": "{{orderId}}", "cancelledAt": "{{cancelledAt.Value:O}}" } } }""";

    /// <summary>
    /// Step 10 (LookupOrderOwnerAsync) and step 14 (GetCancellationStateAsync)
    /// share one IShopifyOrderLookupService instance and therefore one fake
    /// handler in these tests — a test that must reach both needs its
    /// responses served in the exact order the executor calls them, not by
    /// request-content inspection (the fake handler is call-order-based
    /// throughout this file, matching NewOrderLookupService's own existing
    /// per-test callback convention).
    /// </summary>
    private static Func<HttpRequestMessage, HttpResponseMessage> SequencedResponses(params string[] jsonResponses)
    {
        var index = 0;
        return _ => JsonResponse(jsonResponses[index++]);
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };

    /// <summary>Step 15's own mutation-accepted response shape — same shape
    /// ShopifyOrderCancellationExecutorTests.AcceptedJobResponse already
    /// uses.</summary>
    private const string AcceptedJobResponse =
        """{ "data": { "orderCancel": { "job": { "id": "gid://shopify/Job/1", "done": false }, "orderCancelUserErrors": [] } } }""";

    /// <summary>Step 15's own mutation-rejected response shape — same shape
    /// ShopifyOrderCancellationExecutorTests uses for a genuine Shopify
    /// rejection (orderCancelUserErrors non-empty).</summary>
    private const string RejectedMutationResponse =
        """{ "data": { "orderCancel": { "job": null, "orderCancelUserErrors": [ { "field": ["orderId"], "message": "Order already cancelled", "code": "ALREADY_CANCELLED" } ] } } }""";

    /// <summary>
    /// Constructs a real ShopifyOrderCancellationExecutor (step 15's own
    /// mutation caller) wired to FakeShopifyHttpMessageHandler — same
    /// pattern as ShopifyOrderCancellationExecutorTests.NewExecutor. A
    /// separate HttpClient/handler from NewOrderLookupService's — step 10/14
    /// and step 15 are genuinely different Shopify services in production,
    /// unified here only by both being fakes over the same Neon dev DB.
    /// </summary>
    private static ShopifyOrderCancellationExecutor NewOrderCancellationExecutor(
        IServiceProvider scopedProvider, Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(
            new HttpClient(new FakeShopifyHttpMessageHandler(respond)),
            scopedProvider.GetRequiredService<AppDbContext>(),
            scopedProvider.GetRequiredService<IIntegrationTokenProtector>(),
            Options.Create(new ShopifyOptions
            {
                ClientId = "unused", ClientSecret = "unused", RedirectUri = "unused", AdminApiVersion = AdminApiVersion,
            }),
            scopedProvider.GetRequiredService<IOrderCancellationService>());

    /// <summary>
    /// For every test whose outcome is decided at or before step 13 (i.e.
    /// step 14/15 must never run) — a fake IOrderCancellationExecutor that
    /// throws if ExecuteAsync is ever called at all, affirmatively proving
    /// step 15 is genuinely never reached on these paths, matching
    /// ThrowingOrderLookupService's identical role for step 10.
    /// </summary>
    private sealed class ThrowingOrderCancellationExecutor : IOrderCancellationExecutor
    {
        public Task ExecuteAsync(Guid orderCancellationId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "Step 15 must not be reached — this test's Approval should refuse, or resolve at step 14, before it.");
    }

    private async Task<List<AuditLog>> ReadAuditLogsAsync(Guid businessId, Guid entityId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.AuditLogs
            .IgnoreQueryFilters()
            .Where(a => a.BusinessId == businessId
                && a.EntityType == AuditEntityTypes.Approval
                && a.EntityId == entityId.ToString())
            .ToListAsync();
    }

    private async Task<ApprovalStatus> ReadApprovalStatusAsync(Guid approvalId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var approval = await db.Approvals.IgnoreQueryFilters().AsNoTracking()
            .FirstAsync(a => a.Id == approvalId);
        return approval.Status;
    }

    private async Task<OrderCancellationStatus> ReadOrderCancellationStatusAsync(Guid? orderCancellationId)
    {
        Assert.NotNull(orderCancellationId);
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.OrderCancellations.IgnoreQueryFilters().AsNoTracking()
            .FirstAsync(o => o.Id == orderCancellationId!.Value);
        return row.Status;
    }

    [Fact]
    public async Task ExecuteApprovedActionAsync_ApprovalBelongsToDifferentTenant_ReturnsApprovalNotFound()
    {
        var tenant = (MutableTenantAccessor)_provider.GetRequiredService<ICurrentTenantAccessor>();

        await using var seedScope = _provider.CreateAsyncScope();
        var seedDb = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (owningBusiness, approval, _) = await SeedApprovalAsync(seedDb);

        // A second, real, independently-seeded tenant -- not the approval's
        // own BusinessId -- proving this is a genuine cross-tenant read,
        // not merely "no tenant resolved."
        var otherBusiness = new Business
        {
            Id = Guid.NewGuid(),
            Name = $"Other Tenant {Guid.NewGuid():N}",
            TimeZone = "Europe/Copenhagen",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        seedDb.Businesses.Add(otherBusiness);
        await seedDb.SaveChangesAsync();
        _businessIdsToClean.Add(otherBusiness.Id);

        tenant.CurrentBusinessId = otherBusiness.Id;
        await using var readScope = _provider.CreateAsyncScope();
        var readDb = readScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var auditLogService = readScope.ServiceProvider.GetRequiredService<IAuditLogService>();
        var level2Checker = readScope.ServiceProvider.GetRequiredService<ILevel2VerificationChecker>();
        var orderLookupService = ThrowingOrderLookupService(readScope.ServiceProvider);
        var ownershipService = new OrderOwnershipAuthorizationService(readDb);
        var orderCancellationService = readScope.ServiceProvider.GetRequiredService<IOrderCancellationService>();
        var executor = new OrderCancelApprovedActionExecutor(
            readDb, auditLogService, level2Checker, orderLookupService, ownershipService, orderCancellationService,
            new ThrowingOrderCancellationExecutor());

        var outcome = await executor.ExecuteApprovedActionAsync(approval.Id, Guid.NewGuid(), CancellationToken.None);

        var refused = Assert.IsType<ExecutionOutcome.Refused>(outcome);
        Assert.Equal(ExecutionRefusalReason.ApprovalNotFound, refused.Reason);
        Assert.NotEqual(owningBusiness.Id, otherBusiness.Id); // sanity: genuinely two different tenants
    }

    [Fact]
    public async Task ExecuteApprovedActionAsync_NoSuchApprovalId_ReturnsApprovalNotFound()
    {
        var tenant = (MutableTenantAccessor)_provider.GetRequiredService<ICurrentTenantAccessor>();

        await using var seedScope = _provider.CreateAsyncScope();
        var seedDb = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (business, _, _) = await SeedApprovalAsync(seedDb);

        tenant.CurrentBusinessId = business.Id;
        await using var readScope = _provider.CreateAsyncScope();
        var readDb = readScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var auditLogService = readScope.ServiceProvider.GetRequiredService<IAuditLogService>();
        var level2Checker = readScope.ServiceProvider.GetRequiredService<ILevel2VerificationChecker>();
        var orderLookupService = ThrowingOrderLookupService(readScope.ServiceProvider);
        var ownershipService = new OrderOwnershipAuthorizationService(readDb);
        var orderCancellationService = readScope.ServiceProvider.GetRequiredService<IOrderCancellationService>();
        var executor = new OrderCancelApprovedActionExecutor(
            readDb, auditLogService, level2Checker, orderLookupService, ownershipService, orderCancellationService,
            new ThrowingOrderCancellationExecutor());

        var outcome = await executor.ExecuteApprovedActionAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        var refused = Assert.IsType<ExecutionOutcome.Refused>(outcome);
        Assert.Equal(ExecutionRefusalReason.ApprovalNotFound, refused.Reason);
    }

    /// <summary>
    /// Step 6 — every non-Approved status must refuse with
    /// ApprovalNotApproved and write exactly one durable AuditLog row
    /// (AuditEventTypes.UnauthorizedActionBlocked, ActorType.NivoraBusinessStaff,
    /// ChangedByUserId == the executing staff member, NewValue == the actual
    /// status) before returning. Covers all three non-Approved ApprovalStatus members —
    /// not just one — since step-38 §6's write applies uniformly to "Status
    /// != Approved", not to one specific wrong status.
    /// </summary>
    [Theory]
    [InlineData(ApprovalStatus.Pending)]
    [InlineData(ApprovalStatus.Denied)]
    [InlineData(ApprovalStatus.Consumed)]
    public async Task ExecuteApprovedActionAsync_StatusNotApproved_ReturnsApprovalNotApprovedAndWritesAuditLog(
        ApprovalStatus status)
    {
        var tenant = (MutableTenantAccessor)_provider.GetRequiredService<ICurrentTenantAccessor>();

        await using var seedScope = _provider.CreateAsyncScope();
        var seedDb = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (business, approval, _) = await SeedApprovalAsync(seedDb, status: status);
        var executingStaffUserId = await CreateStaffUserAsync(seedDb, business.Id);

        tenant.CurrentBusinessId = business.Id;
        await using var readScope = _provider.CreateAsyncScope();
        var readDb = readScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var auditLogService = readScope.ServiceProvider.GetRequiredService<IAuditLogService>();
        var level2Checker = readScope.ServiceProvider.GetRequiredService<ILevel2VerificationChecker>();
        var orderLookupService = ThrowingOrderLookupService(readScope.ServiceProvider);
        var ownershipService = new OrderOwnershipAuthorizationService(readDb);
        var orderCancellationService = readScope.ServiceProvider.GetRequiredService<IOrderCancellationService>();
        var executor = new OrderCancelApprovedActionExecutor(
            readDb, auditLogService, level2Checker, orderLookupService, ownershipService, orderCancellationService,
            new ThrowingOrderCancellationExecutor());
        var outcome = await executor.ExecuteApprovedActionAsync(approval.Id, executingStaffUserId, CancellationToken.None);

        var refused = Assert.IsType<ExecutionOutcome.Refused>(outcome);
        Assert.Equal(ExecutionRefusalReason.ApprovalNotApproved, refused.Reason);

        var auditLogs = await ReadAuditLogsAsync(business.Id, approval.Id);
        var entry = Assert.Single(auditLogs);
        Assert.Equal(AuditEventTypes.UnauthorizedActionBlocked, entry.EventType);
        Assert.Equal(ActorType.NivoraBusinessStaff, entry.Actor);
        Assert.Equal(executingStaffUserId, entry.ChangedByUserId);
        Assert.Equal(status.ToString(), entry.NewValue);
    }

    /// <summary>
    /// Step 7 — an expired (but Approved) approval must refuse with
    /// ApprovalExpired and write exactly one durable AuditLog row, distinct
    /// from step 6's write (different NewValue, same event type/actor
    /// shape). Also proves ordering: this Approval's own Status is
    /// Approved, so if step 6 incorrectly fired first this test would fail
    /// on the wrong ExecutionRefusalReason.
    /// </summary>
    [Fact]
    public async Task ExecuteApprovedActionAsync_ApprovalExpired_ReturnsApprovalExpiredAndWritesAuditLog()
    {
        var tenant = (MutableTenantAccessor)_provider.GetRequiredService<ICurrentTenantAccessor>();

        await using var seedScope = _provider.CreateAsyncScope();
        var seedDb = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (business, approval, _) = await SeedApprovalAsync(
            seedDb, expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));
        var executingStaffUserId = await CreateStaffUserAsync(seedDb, business.Id);

        tenant.CurrentBusinessId = business.Id;
        await using var readScope = _provider.CreateAsyncScope();
        var readDb = readScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var auditLogService = readScope.ServiceProvider.GetRequiredService<IAuditLogService>();
        var level2Checker = readScope.ServiceProvider.GetRequiredService<ILevel2VerificationChecker>();
        var orderLookupService = ThrowingOrderLookupService(readScope.ServiceProvider);
        var ownershipService = new OrderOwnershipAuthorizationService(readDb);
        var orderCancellationService = readScope.ServiceProvider.GetRequiredService<IOrderCancellationService>();
        var executor = new OrderCancelApprovedActionExecutor(
            readDb, auditLogService, level2Checker, orderLookupService, ownershipService, orderCancellationService,
            new ThrowingOrderCancellationExecutor());
        var outcome = await executor.ExecuteApprovedActionAsync(approval.Id, executingStaffUserId, CancellationToken.None);

        var refused = Assert.IsType<ExecutionOutcome.Refused>(outcome);
        Assert.Equal(ExecutionRefusalReason.ApprovalExpired, refused.Reason);

        var auditLogs = await ReadAuditLogsAsync(business.Id, approval.Id);
        var entry = Assert.Single(auditLogs);
        Assert.Equal(AuditEventTypes.UnauthorizedActionBlocked, entry.EventType);
        Assert.Equal(ActorType.NivoraBusinessStaff, entry.Actor);
        Assert.Equal(executingStaffUserId, entry.ChangedByUserId);
        Assert.Equal("Expired", entry.NewValue);
    }

    /// <summary>
    /// Step 8 — an Approved, unexpired approval whose Conversation no
    /// longer has a resolved CustomerId must refuse with CustomerUnresolved
    /// and write NO AuditLog row at all — step-38 §2a/§7's deliberately
    /// undecided classification for this specific refusal, not silently
    /// resolved by this implementation either.
    /// </summary>
    [Fact]
    public async Task ExecuteApprovedActionAsync_CustomerUnresolved_ReturnsCustomerUnresolvedAndWritesNoAuditLog()
    {
        var tenant = (MutableTenantAccessor)_provider.GetRequiredService<ICurrentTenantAccessor>();

        await using var seedScope = _provider.CreateAsyncScope();
        var seedDb = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (business, approval, _) = await SeedApprovalAsync(seedDb, customerResolved: false);

        tenant.CurrentBusinessId = business.Id;
        await using var readScope = _provider.CreateAsyncScope();
        var readDb = readScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var auditLogService = readScope.ServiceProvider.GetRequiredService<IAuditLogService>();
        var level2Checker = readScope.ServiceProvider.GetRequiredService<ILevel2VerificationChecker>();
        var orderLookupService = ThrowingOrderLookupService(readScope.ServiceProvider);
        var ownershipService = new OrderOwnershipAuthorizationService(readDb);
        var orderCancellationService = readScope.ServiceProvider.GetRequiredService<IOrderCancellationService>();
        var executor = new OrderCancelApprovedActionExecutor(
            readDb, auditLogService, level2Checker, orderLookupService, ownershipService, orderCancellationService,
            new ThrowingOrderCancellationExecutor());

        var outcome = await executor.ExecuteApprovedActionAsync(approval.Id, Guid.NewGuid(), CancellationToken.None);

        var refused = Assert.IsType<ExecutionOutcome.Refused>(outcome);
        Assert.Equal(ExecutionRefusalReason.CustomerUnresolved, refused.Reason);

        var auditLogs = await ReadAuditLogsAsync(business.Id, approval.Id);
        Assert.Empty(auditLogs);
    }

    /// <summary>
    /// Step 9 — a currently-unverified Conversation (no
    /// `Level2VerifiedUntil` in the future, no `ExternalIdentity` row) must
    /// refuse with `Level2VerificationFailed` and write exactly one durable
    /// `AuditLog` row, matching steps 6/7's exact shape — per the explicit
    /// owner decision recorded in
    /// docs/handoffs/step-39-cancelorder-execute-steps-9-16-next-slice-design.md
    /// §5a (source itself did not settle this; the owner decided parity
    /// with steps 6/7 anyway). Also proves ordering: this Approval is
    /// Approved, unexpired, and has a resolved customer, so if steps
    /// 6/7/8 incorrectly fired first this test would fail on the wrong
    /// `ExecutionRefusalReason`.
    /// </summary>
    [Fact]
    public async Task ExecuteApprovedActionAsync_Level2VerificationFailed_ReturnsLevel2VerificationFailedAndWritesAuditLog()
    {
        var tenant = (MutableTenantAccessor)_provider.GetRequiredService<ICurrentTenantAccessor>();

        await using var seedScope = _provider.CreateAsyncScope();
        var seedDb = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (business, approval, _) = await SeedApprovalAsync(seedDb, level2Verified: false);
        var executingStaffUserId = await CreateStaffUserAsync(seedDb, business.Id);

        tenant.CurrentBusinessId = business.Id;
        await using var readScope = _provider.CreateAsyncScope();
        var readDb = readScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var auditLogService = readScope.ServiceProvider.GetRequiredService<IAuditLogService>();
        var level2Checker = readScope.ServiceProvider.GetRequiredService<ILevel2VerificationChecker>();
        var orderLookupService = ThrowingOrderLookupService(readScope.ServiceProvider);
        var ownershipService = new OrderOwnershipAuthorizationService(readDb);
        var orderCancellationService = readScope.ServiceProvider.GetRequiredService<IOrderCancellationService>();
        var executor = new OrderCancelApprovedActionExecutor(
            readDb, auditLogService, level2Checker, orderLookupService, ownershipService, orderCancellationService,
            new ThrowingOrderCancellationExecutor());
        var outcome = await executor.ExecuteApprovedActionAsync(approval.Id, executingStaffUserId, CancellationToken.None);

        var refused = Assert.IsType<ExecutionOutcome.Refused>(outcome);
        Assert.Equal(ExecutionRefusalReason.Level2VerificationFailed, refused.Reason);

        var auditLogs = await ReadAuditLogsAsync(business.Id, approval.Id);
        var entry = Assert.Single(auditLogs);
        Assert.Equal(AuditEventTypes.UnauthorizedActionBlocked, entry.EventType);
        Assert.Equal(ActorType.NivoraBusinessStaff, entry.Actor);
        Assert.Equal(executingStaffUserId, entry.ChangedByUserId);
        Assert.Equal("Level2VerificationFailed", entry.NewValue);
    }

    /// <summary>
    /// Step 48 — an Approved, unexpired, resolved-customer, Level-2-verified
    /// approval whose business has NO Integration row at all (active or
    /// otherwise) must refuse with ShopifyIntegrationUnavailable, cleanly,
    /// rather than let IShopifyOrderLookupService.LookupOrderOwnerAsync
    /// throw an uncaught InvalidOperationException at step 10. Both
    /// ThrowingOrderLookupService and ThrowingOrderCancellationExecutor are
    /// used here — proving steps 10 through 15 are never reached at all,
    /// not merely that step 10's own throw is caught somewhere downstream.
    /// Business.CancelOrderToolEnabled is set true purely for scenario
    /// fidelity to the user-described case ("a business that has the tool
    /// enabled... but has no connected/live Shopify integration") — Execute
    /// itself never reads that flag; the kill switch lives entirely at the
    /// tool-exposure layer (ClaudeReplyServiceTests covers that).
    /// </summary>
    [Fact]
    public async Task ExecuteApprovedActionAsync_NoActiveShopifyIntegration_ReturnsShopifyIntegrationUnavailable_NeverThrows()
    {
        var tenant = (MutableTenantAccessor)_provider.GetRequiredService<ICurrentTenantAccessor>();

        await using var seedScope = _provider.CreateAsyncScope();
        var seedDb = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (business, approval, _) = await SeedApprovalAsync(seedDb);
        business.CancelOrderToolEnabled = true;
        await seedDb.SaveChangesAsync();
        var executingStaffUserId = await CreateStaffUserAsync(seedDb, business.Id);
        // Deliberately no AddIntegrationAsync call — this business has no
        // Integration row at all.

        tenant.CurrentBusinessId = business.Id;
        await using var readScope = _provider.CreateAsyncScope();
        var readDb = readScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var auditLogService = readScope.ServiceProvider.GetRequiredService<IAuditLogService>();
        var level2Checker = readScope.ServiceProvider.GetRequiredService<ILevel2VerificationChecker>();
        var orderLookupService = ThrowingOrderLookupService(readScope.ServiceProvider);
        var ownershipService = new OrderOwnershipAuthorizationService(readDb);
        var orderCancellationService = readScope.ServiceProvider.GetRequiredService<IOrderCancellationService>();
        var executor = new OrderCancelApprovedActionExecutor(
            readDb, auditLogService, level2Checker, orderLookupService, ownershipService, orderCancellationService,
            new ThrowingOrderCancellationExecutor());

        var outcome = await executor.ExecuteApprovedActionAsync(approval.Id, executingStaffUserId, CancellationToken.None);

        var refused = Assert.IsType<ExecutionOutcome.Refused>(outcome);
        Assert.Equal(ExecutionRefusalReason.ShopifyIntegrationUnavailable, refused.Reason);

        // No mutation, no consumption: this refusal happens strictly before
        // step 13's atomic claim, same as every other pre-claim refusal.
        Assert.Equal(ApprovalStatus.Approved, await ReadApprovalStatusAsync(approval.Id));
    }

    /// <summary>
    /// Step 10 — Shopify reports a different order owner than the
    /// Approval's resolved Customer (OwnerMismatch, one of
    /// OwnershipCheckFailureReason's eight members) must refuse with
    /// OwnershipVerificationFailed and write exactly one durable AuditLog
    /// row whose NewValue distinctly names both the step and the specific
    /// sub-reason ("OwnershipVerificationFailed:OwnerMismatch") — not
    /// copy-pasted wording from any prior step's NewValue. Also proves the
    /// Approval itself is left completely untouched: no mutation occurs and
    /// no Approval is ever consumed on a step 10 refusal, since Status is
    /// re-read fresh from the DB afterward and asserted unchanged.
    /// </summary>
    [Fact]
    public async Task ExecuteApprovedActionAsync_OwnershipVerificationFails_ReturnsOwnershipVerificationFailedAndWritesAuditLog()
    {
        var tenant = (MutableTenantAccessor)_provider.GetRequiredService<ICurrentTenantAccessor>();

        await using var seedScope = _provider.CreateAsyncScope();
        var seedDb = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tokenProtector = seedScope.ServiceProvider.GetRequiredService<IIntegrationTokenProtector>();
        var (business, approval, _) = await SeedApprovalAsync(
            seedDb, customerExternalId: "gid://shopify/Customer/AAA");
        await AddIntegrationAsync(seedDb, tokenProtector, business.Id);

        tenant.CurrentBusinessId = business.Id;
        await using var readScope = _provider.CreateAsyncScope();
        var readDb = readScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var auditLogService = readScope.ServiceProvider.GetRequiredService<IAuditLogService>();
        var level2Checker = readScope.ServiceProvider.GetRequiredService<ILevel2VerificationChecker>();
        // Shopify reports a DIFFERENT owning customer ("BBB") than the one
        // linked to this Approval's Customer ("AAA") -> OwnerMismatch.
        var orderLookupService = NewOrderLookupService(
            readScope.ServiceProvider,
            _ => JsonResponse(OrderOwnerResponse(approval.ResourceId, "gid://shopify/Customer/BBB")));
        var ownershipService = new OrderOwnershipAuthorizationService(readDb);
        var orderCancellationService = readScope.ServiceProvider.GetRequiredService<IOrderCancellationService>();
        var executor = new OrderCancelApprovedActionExecutor(
            readDb, auditLogService, level2Checker, orderLookupService, ownershipService, orderCancellationService,
            new ThrowingOrderCancellationExecutor());

        var executingStaffUserId = await CreateStaffUserAsync(seedDb, business.Id);
        var outcome = await executor.ExecuteApprovedActionAsync(approval.Id, executingStaffUserId, CancellationToken.None);

        var refused = Assert.IsType<ExecutionOutcome.Refused>(outcome);
        Assert.Equal(ExecutionRefusalReason.OwnershipVerificationFailed, refused.Reason);

        var auditLogs = await ReadAuditLogsAsync(business.Id, approval.Id);
        var entry = Assert.Single(auditLogs);
        Assert.Equal(AuditEventTypes.UnauthorizedActionBlocked, entry.EventType);
        Assert.Equal(ActorType.NivoraBusinessStaff, entry.Actor);
        Assert.Equal(executingStaffUserId, entry.ChangedByUserId);
        Assert.Equal("OwnershipVerificationFailed:OwnerMismatch", entry.NewValue);

        // No mutation, no consumption: the Approval is untouched by a step
        // 10 refusal, unlike step 13's (not yet implemented) atomic claim.
        Assert.Equal(ApprovalStatus.Approved, await ReadApprovalStatusAsync(approval.Id));
    }

    /// <summary>
    /// Step 10 — Shopify confirms the SAME owning customer as the
    /// Approval's resolved Customer -> ownership verification succeeds, and
    /// execution proceeds through step 13's atomic claim and (step-47) all
    /// the way through steps 14-16: the ground-truth check (step 14) finds
    /// the order not yet cancelled, so the mutation is sent (step 15) and
    /// accepted (job pending) -> Executed(UnknownNeedsReconciliation).
    /// Proves the negative case explicitly, distinct from the general
    /// "everything passes" boundary test below (which this test overlaps
    /// with by design, for the same reason step-38/step-39 each added their
    /// own dedicated success-path assertion alongside the general boundary
    /// test rather than relying on the boundary test alone).
    /// </summary>
    [Fact]
    public async Task ExecuteApprovedActionAsync_OwnershipVerificationSucceeds_ClaimsAndWritesExecutionClaimedAuditLog()
    {
        var tenant = (MutableTenantAccessor)_provider.GetRequiredService<ICurrentTenantAccessor>();
        const string customerExternalId = "gid://shopify/Customer/AAA";

        await using var seedScope = _provider.CreateAsyncScope();
        var seedDb = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tokenProtector = seedScope.ServiceProvider.GetRequiredService<IIntegrationTokenProtector>();
        var (business, approval, _) = await SeedApprovalAsync(seedDb, customerExternalId: customerExternalId);
        await AddIntegrationAsync(seedDb, tokenProtector, business.Id);
        var executingStaffUserId = await CreateStaffUserAsync(seedDb, business.Id);

        tenant.CurrentBusinessId = business.Id;
        await using var readScope = _provider.CreateAsyncScope();
        var readDb = readScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var auditLogService = readScope.ServiceProvider.GetRequiredService<IAuditLogService>();
        var level2Checker = readScope.ServiceProvider.GetRequiredService<ILevel2VerificationChecker>();
        var orderLookupService = NewOrderLookupService(
            readScope.ServiceProvider,
            SequencedResponses(
                OrderOwnerResponse(approval.ResourceId, customerExternalId),
                CancellationStateResponse(approval.ResourceId, null)));
        var ownershipService = new OrderOwnershipAuthorizationService(readDb);
        var orderCancellationService = readScope.ServiceProvider.GetRequiredService<IOrderCancellationService>();
        var orderCancellationExecutor = NewOrderCancellationExecutor(
            readScope.ServiceProvider, _ => JsonResponse(AcceptedJobResponse));
        var executor = new OrderCancelApprovedActionExecutor(
            readDb, auditLogService, level2Checker, orderLookupService, ownershipService, orderCancellationService,
            orderCancellationExecutor);

        var outcome = await executor.ExecuteApprovedActionAsync(approval.Id, executingStaffUserId, CancellationToken.None);

        var executed = Assert.IsType<ExecutionOutcome.Executed>(outcome);
        Assert.Equal(ExecutionResultStatus.UnknownNeedsReconciliation, executed.Status);
        Assert.Null(executed.Detail);

        var auditLogs = await ReadAuditLogsAsync(business.Id, approval.Id);
        var entry = Assert.Single(auditLogs);
        Assert.Equal(AuditEventTypes.ExecutionClaimed, entry.EventType);
        Assert.Equal(ActorType.NivoraBusinessStaff, entry.Actor);
        Assert.Equal(executingStaffUserId, entry.ChangedByUserId);
        Assert.Equal(ApprovalStatus.Consumed, await ReadApprovalStatusAsync(approval.Id));
    }

    /// <summary>
    /// Step 11 — the stored Approval.ActionHash was computed against a
    /// customer identity that no longer matches the Conversation's current
    /// CustomerId (the one genuine drift step 11's recompute can detect,
    /// per step-24 §2a — see SeedApprovalAsync's own doc comment). Must
    /// refuse with ActionHashMismatch and write exactly one durable
    /// AuditLog row, handled identically to a genuine tampering attempt
    /// (step-22z §13: "a legitimate state-drift denial and a genuine
    /// tampering attempt are handled identically") — no special-casing by
    /// cause. Also proves no mutation, no consumption: the Approval is
    /// left untouched by a step 11 refusal, same as every earlier
    /// pre-claim refusal.
    /// </summary>
    [Fact]
    public async Task ExecuteApprovedActionAsync_ActionHashMismatch_ReturnsActionHashMismatchAndWritesAuditLog()
    {
        var tenant = (MutableTenantAccessor)_provider.GetRequiredService<ICurrentTenantAccessor>();
        const string customerExternalId = "gid://shopify/Customer/AAA";

        await using var seedScope = _provider.CreateAsyncScope();
        var seedDb = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tokenProtector = seedScope.ServiceProvider.GetRequiredService<IIntegrationTokenProtector>();
        var (business, approval, _) = await SeedApprovalAsync(
            seedDb, customerExternalId: customerExternalId, actionHashCustomerIdOverride: Guid.NewGuid());
        await AddIntegrationAsync(seedDb, tokenProtector, business.Id);

        tenant.CurrentBusinessId = business.Id;
        await using var readScope = _provider.CreateAsyncScope();
        var readDb = readScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var auditLogService = readScope.ServiceProvider.GetRequiredService<IAuditLogService>();
        var level2Checker = readScope.ServiceProvider.GetRequiredService<ILevel2VerificationChecker>();
        // Ownership (step 10) must still pass so execution actually reaches
        // step 11 — the real customer's ExternalId matches Shopify's
        // reported owner; only the stored ActionHash's own CustomerId
        // input was overridden to something else at seed time.
        var orderLookupService = NewOrderLookupService(
            readScope.ServiceProvider,
            _ => JsonResponse(OrderOwnerResponse(approval.ResourceId, customerExternalId)));
        var ownershipService = new OrderOwnershipAuthorizationService(readDb);
        var orderCancellationService = readScope.ServiceProvider.GetRequiredService<IOrderCancellationService>();
        var executor = new OrderCancelApprovedActionExecutor(
            readDb, auditLogService, level2Checker, orderLookupService, ownershipService, orderCancellationService,
            new ThrowingOrderCancellationExecutor());

        var executingStaffUserId = await CreateStaffUserAsync(seedDb, business.Id);
        var outcome = await executor.ExecuteApprovedActionAsync(approval.Id, executingStaffUserId, CancellationToken.None);

        var refused = Assert.IsType<ExecutionOutcome.Refused>(outcome);
        Assert.Equal(ExecutionRefusalReason.ActionHashMismatch, refused.Reason);

        var auditLogs = await ReadAuditLogsAsync(business.Id, approval.Id);
        var entry = Assert.Single(auditLogs);
        Assert.Equal(AuditEventTypes.UnauthorizedActionBlocked, entry.EventType);
        Assert.Equal(ActorType.NivoraBusinessStaff, entry.Actor);
        Assert.Equal(executingStaffUserId, entry.ChangedByUserId);
        Assert.Equal("ActionHashMismatch", entry.NewValue);

        Assert.Equal(ApprovalStatus.Approved, await ReadApprovalStatusAsync(approval.Id));
    }

    /// <summary>
    /// Steps 11/12 — the stored ActionHash matches what Execute recomputes
    /// (the ordinary case: no customer-identity drift since proposal time)
    /// -> step 11 does not refuse, step 12's execution-leg key derivation
    /// runs (a pure, always-succeeding computation with no refusal path
    /// and no AuditLog write of its own, per step-42 §2), and execution
    /// proceeds through step 13's atomic claim, then (step-47) step 14's own
    /// ground-truth check finds the order ALREADY cancelled externally ->
    /// Executed(ExternallySatisfied), never reaching step 15's mutation call
    /// at all (ThrowingOrderCancellationExecutor affirmatively proves this).
    /// Proves the negative case explicitly, distinct from the general
    /// boundary test below, same convention as every earlier step's
    /// dedicated success-path assertion.
    /// </summary>
    [Fact]
    public async Task ExecuteApprovedActionAsync_ActionHashMatches_ClaimsAndWritesExecutionClaimedAuditLog()
    {
        var tenant = (MutableTenantAccessor)_provider.GetRequiredService<ICurrentTenantAccessor>();
        const string customerExternalId = "gid://shopify/Customer/AAA";

        await using var seedScope = _provider.CreateAsyncScope();
        var seedDb = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tokenProtector = seedScope.ServiceProvider.GetRequiredService<IIntegrationTokenProtector>();
        var (business, approval, _) = await SeedApprovalAsync(seedDb, customerExternalId: customerExternalId);
        await AddIntegrationAsync(seedDb, tokenProtector, business.Id);
        var executingStaffUserId = await CreateStaffUserAsync(seedDb, business.Id);

        tenant.CurrentBusinessId = business.Id;
        await using var readScope = _provider.CreateAsyncScope();
        var readDb = readScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var auditLogService = readScope.ServiceProvider.GetRequiredService<IAuditLogService>();
        var level2Checker = readScope.ServiceProvider.GetRequiredService<ILevel2VerificationChecker>();
        var now = DateTimeOffset.UtcNow;
        var orderLookupService = NewOrderLookupService(
            readScope.ServiceProvider,
            SequencedResponses(
                OrderOwnerResponse(approval.ResourceId, customerExternalId),
                CancellationStateResponse(approval.ResourceId, now)));
        var ownershipService = new OrderOwnershipAuthorizationService(readDb);
        var orderCancellationService = readScope.ServiceProvider.GetRequiredService<IOrderCancellationService>();
        var executor = new OrderCancelApprovedActionExecutor(
            readDb, auditLogService, level2Checker, orderLookupService, ownershipService, orderCancellationService,
            new ThrowingOrderCancellationExecutor());

        var outcome = await executor.ExecuteApprovedActionAsync(approval.Id, executingStaffUserId, CancellationToken.None);

        var executed = Assert.IsType<ExecutionOutcome.Executed>(outcome);
        Assert.Equal(ExecutionResultStatus.ExternallySatisfied, executed.Status);
        Assert.Equal("Order was already cancelled externally.", executed.Detail);
        Assert.Equal(OrderCancellationStatus.Confirmed, await ReadOrderCancellationStatusAsync(executed.ExecutionIntentId));

        var auditLogs = await ReadAuditLogsAsync(business.Id, approval.Id);
        var entry = Assert.Single(auditLogs);
        Assert.Equal(AuditEventTypes.ExecutionClaimed, entry.EventType);
        Assert.Equal(ActorType.NivoraBusinessStaff, entry.Actor);
        Assert.Equal(executingStaffUserId, entry.ChangedByUserId);
        Assert.Equal(ApprovalStatus.Consumed, await ReadApprovalStatusAsync(approval.Id));
    }

    /// <summary>
    /// Deliberate scope-boundary test, same convention as
    /// OrderCancelProposalToolTests' own explicit-refusal tests: documents,
    /// rather than merely leaves implicit, exactly where this session's
    /// implementation stops for steps that DO refuse — a passing outcome
    /// here after a future step is added without updating this test would
    /// mean the new step's logic was never reached. Extended through step-47
    /// (was previously the steps-3-12 boundary, per
    /// docs/handoffs/step-43-cancelorder-execute-step-13-atomic-claim-design.md,
    /// then the steps-3-13 boundary) — this is now the full "everything
    /// passes, end to end" test: asserts the Approval is Consumed with
    /// OrderCancellationId set, exactly one ExecutionClaimed AuditLog row
    /// exists, the created OrderCancellation row itself has the expected
    /// BusinessId/CustomerId/ConversationId/ExternalOrderId/Restock/Method/
    /// Reason/IdempotencyKey-length, and the final ExecutionOutcome/final
    /// OrderCancellation.Status both reflect a mutation genuinely sent and
    /// accepted (step 15). Confirms exactly one call to each of the two
    /// separate Shopify surfaces this flow now touches — the ownership+
    /// ground-truth lookup service (step 10 + step 14, two calls, same fake
    /// handler) and the mutation executor (step 15, one call, a separate
    /// fake handler) — step 13 itself still performs zero network/Shopify
    /// I/O of its own (step-43 §3, unchanged). Also proves the
    /// adjacent-case negative: a valid Approved+unexpired+resolved-customer+
    /// Level2-verified+ownership-matching+hash-matching+not-yet-cancelled
    /// Approval does NOT trigger any of steps 6/7/8/9/10/11's refusals nor
    /// step 14's ExternallySatisfied shortcut.
    /// </summary>
    [Fact]
    public async Task ExecuteApprovedActionAsync_ApprovalExistsForCurrentTenant_ClaimsCreatesOrderCancellationAndThrowsNotImplemented()
    {
        var tenant = (MutableTenantAccessor)_provider.GetRequiredService<ICurrentTenantAccessor>();
        const string customerExternalId = "gid://shopify/Customer/AAA";

        await using var seedScope = _provider.CreateAsyncScope();
        var seedDb = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tokenProtector = seedScope.ServiceProvider.GetRequiredService<IIntegrationTokenProtector>();
        var (business, approval, _) = await SeedApprovalAsync(seedDb, customerExternalId: customerExternalId);
        await AddIntegrationAsync(seedDb, tokenProtector, business.Id);

        tenant.CurrentBusinessId = business.Id;
        await using var readScope = _provider.CreateAsyncScope();
        var readDb = readScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var auditLogService = readScope.ServiceProvider.GetRequiredService<IAuditLogService>();
        var level2Checker = readScope.ServiceProvider.GetRequiredService<ILevel2VerificationChecker>();
        var lookupCallCount = 0;
        var orderLookupService = NewOrderLookupService(
            readScope.ServiceProvider,
            request =>
            {
                lookupCallCount++;
                return JsonResponse(lookupCallCount == 1
                    ? OrderOwnerResponse(approval.ResourceId, customerExternalId)
                    : CancellationStateResponse(approval.ResourceId, null));
            });
        var ownershipService = new OrderOwnershipAuthorizationService(readDb);
        var orderCancellationService = readScope.ServiceProvider.GetRequiredService<IOrderCancellationService>();
        var mutationCallCount = 0;
        var orderCancellationExecutor = NewOrderCancellationExecutor(
            readScope.ServiceProvider,
            _ =>
            {
                mutationCallCount++;
                return JsonResponse(AcceptedJobResponse);
            });
        var executor = new OrderCancelApprovedActionExecutor(
            readDb, auditLogService, level2Checker, orderLookupService, ownershipService, orderCancellationService,
            orderCancellationExecutor);

        var executingStaffUserId = await CreateStaffUserAsync(seedDb, business.Id);
        var outcome = await executor.ExecuteApprovedActionAsync(approval.Id, executingStaffUserId, CancellationToken.None);

        var executed = Assert.IsType<ExecutionOutcome.Executed>(outcome);
        Assert.Equal(ExecutionResultStatus.UnknownNeedsReconciliation, executed.Status);
        Assert.Null(executed.Detail);
        Assert.Equal(2, lookupCallCount);
        Assert.Equal(1, mutationCallCount);

        var auditLogs = await ReadAuditLogsAsync(business.Id, approval.Id);
        var entry = Assert.Single(auditLogs);
        Assert.Equal(AuditEventTypes.ExecutionClaimed, entry.EventType);
        Assert.Equal(ActorType.NivoraBusinessStaff, entry.Actor);
        Assert.Equal(executingStaffUserId, entry.ChangedByUserId);

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var finalApproval = await verifyDb.Approvals.IgnoreQueryFilters().AsNoTracking()
            .FirstAsync(a => a.Id == approval.Id);
        Assert.Equal(ApprovalStatus.Consumed, finalApproval.Status);
        Assert.NotNull(finalApproval.ConsumedAt);
        Assert.NotNull(finalApproval.OrderCancellationId);

        var orderCancellation = await verifyDb.OrderCancellations.IgnoreQueryFilters().AsNoTracking()
            .FirstAsync(o => o.Id == finalApproval.OrderCancellationId!.Value);
        Assert.Equal(business.Id, orderCancellation.BusinessId);
        Assert.Equal(approval.CustomerId, orderCancellation.CustomerId);
        Assert.Equal(approval.ConversationId, orderCancellation.ConversationId);
        Assert.Equal(approval.ResourceId, orderCancellation.ExternalOrderId);
        Assert.Equal(OrderCancellationStatus.Pending, orderCancellation.Status);
        Assert.True(orderCancellation.Restock);
        Assert.Equal(RefundMethod.OriginalPaymentMethod, orderCancellation.Method);
        Assert.Equal(OrderCancelReason.Customer, orderCancellation.Reason);
        Assert.Equal(64, orderCancellation.IdempotencyKey.Length);
    }

    /// <summary>
    /// Step 15 — the ground-truth check (step 14) finds the order not yet
    /// cancelled, so the mutation is sent, but Shopify itself rejects it
    /// (orderCancelUserErrors non-empty) -> Executed(Failed, reason), and
    /// the underlying OrderCancellation row is left Failed with
    /// FailureReason set (IOrderCancellationExecutor.ExecuteAsync's own,
    /// unmodified behavior — already independently verified by
    /// ShopifyOrderCancellationExecutorTests; this test only confirms
    /// Execute correctly re-reads and reports that outcome, not that the
    /// mutation executor itself behaves correctly).
    /// </summary>
    [Fact]
    public async Task ExecuteApprovedActionAsync_MutationRejectedByShopify_ReturnsFailedAndWritesExecutionClaimedAuditLog()
    {
        var tenant = (MutableTenantAccessor)_provider.GetRequiredService<ICurrentTenantAccessor>();
        const string customerExternalId = "gid://shopify/Customer/AAA";

        await using var seedScope = _provider.CreateAsyncScope();
        var seedDb = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tokenProtector = seedScope.ServiceProvider.GetRequiredService<IIntegrationTokenProtector>();
        var (business, approval, _) = await SeedApprovalAsync(seedDb, customerExternalId: customerExternalId);
        await AddIntegrationAsync(seedDb, tokenProtector, business.Id);
        var executingStaffUserId = await CreateStaffUserAsync(seedDb, business.Id);

        tenant.CurrentBusinessId = business.Id;
        await using var readScope = _provider.CreateAsyncScope();
        var readDb = readScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var auditLogService = readScope.ServiceProvider.GetRequiredService<IAuditLogService>();
        var level2Checker = readScope.ServiceProvider.GetRequiredService<ILevel2VerificationChecker>();
        var orderLookupService = NewOrderLookupService(
            readScope.ServiceProvider,
            SequencedResponses(
                OrderOwnerResponse(approval.ResourceId, customerExternalId),
                CancellationStateResponse(approval.ResourceId, null)));
        var ownershipService = new OrderOwnershipAuthorizationService(readDb);
        var orderCancellationService = readScope.ServiceProvider.GetRequiredService<IOrderCancellationService>();
        var orderCancellationExecutor = NewOrderCancellationExecutor(
            readScope.ServiceProvider, _ => JsonResponse(RejectedMutationResponse));
        var executor = new OrderCancelApprovedActionExecutor(
            readDb, auditLogService, level2Checker, orderLookupService, ownershipService, orderCancellationService,
            orderCancellationExecutor);

        var outcome = await executor.ExecuteApprovedActionAsync(approval.Id, executingStaffUserId, CancellationToken.None);

        var executed = Assert.IsType<ExecutionOutcome.Executed>(outcome);
        Assert.Equal(ExecutionResultStatus.Failed, executed.Status);
        Assert.Equal("Order already cancelled", executed.Detail);

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var orderCancellation = await verifyDb.OrderCancellations.IgnoreQueryFilters().AsNoTracking()
            .FirstAsync(o => o.Id == executed.ExecutionIntentId);
        Assert.Equal(OrderCancellationStatus.Failed, orderCancellation.Status);
        Assert.Equal("Order already cancelled", orderCancellation.FailureReason);
    }

    // ── Step Shopify-auth-reauth — auth-classified mutation failure (F1) ──

    /// <summary>
    /// Step Shopify-auth-reauth (docs/handoffs/step-shopify-auth-reauth-design.md
    /// §8.3/§10, F1) — the mandatory, independently-verified fix: an
    /// HTTP 401 at the step 15 mutation boundary must transition
    /// OrderCancellation to Failed with an auth-marked FailureReason, set
    /// Integration.ReauthorizationRequiredAt, and — the specific thing two
    /// independent design-verification passes existed to catch — actually
    /// PERSIST both new audit-log rows (OrderCancellationFailed against the
    /// OrderCancellation entity, IntegrationReauthorizationRequired against
    /// the Integration entity). Reading them back from the database via a
    /// fresh scope, rather than trusting an in-memory tracked entity, is
    /// exactly the assertion that would fail if the mandatory new
    /// db.SaveChangesAsync(cancellationToken) call (design §8.3) were ever
    /// accidentally removed.
    /// </summary>
    [Fact]
    public async Task ExecuteApprovedActionAsync_MutationHttp401_MarksFailedWithAuthReason_SetsReauthorizationRequired_PersistsBothNewAuditRows()
    {
        var tenant = (MutableTenantAccessor)_provider.GetRequiredService<ICurrentTenantAccessor>();
        const string customerExternalId = "gid://shopify/Customer/AAA";

        await using var seedScope = _provider.CreateAsyncScope();
        var seedDb = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tokenProtector = seedScope.ServiceProvider.GetRequiredService<IIntegrationTokenProtector>();
        var (business, approval, _) = await SeedApprovalAsync(seedDb, customerExternalId: customerExternalId);
        await AddIntegrationAsync(seedDb, tokenProtector, business.Id);
        var executingStaffUserId = await CreateStaffUserAsync(seedDb, business.Id);

        tenant.CurrentBusinessId = business.Id;
        await using var readScope = _provider.CreateAsyncScope();
        var readDb = readScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var auditLogService = readScope.ServiceProvider.GetRequiredService<IAuditLogService>();
        var level2Checker = readScope.ServiceProvider.GetRequiredService<ILevel2VerificationChecker>();
        var orderLookupService = NewOrderLookupService(
            readScope.ServiceProvider,
            SequencedResponses(
                OrderOwnerResponse(approval.ResourceId, customerExternalId),
                CancellationStateResponse(approval.ResourceId, null)));
        var ownershipService = new OrderOwnershipAuthorizationService(readDb);
        var orderCancellationService = readScope.ServiceProvider.GetRequiredService<IOrderCancellationService>();
        var orderCancellationExecutor = NewOrderCancellationExecutor(
            readScope.ServiceProvider, _ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var executor = new OrderCancelApprovedActionExecutor(
            readDb, auditLogService, level2Checker, orderLookupService, ownershipService, orderCancellationService,
            orderCancellationExecutor);

        var outcome = await executor.ExecuteApprovedActionAsync(approval.Id, executingStaffUserId, CancellationToken.None);

        var executed = Assert.IsType<ExecutionOutcome.Executed>(outcome);
        Assert.Equal(ExecutionResultStatus.Failed, executed.Status);
        Assert.StartsWith(ShopifyFailureReasons.ReauthorizationRequiredPrefix, executed.Detail);

        // Re-read everything from a fresh scope/connection — never trust the
        // in-memory `readDb` tracked state for the persistence assertions
        // below, since that would not distinguish "actually flushed to
        // Postgres" from "merely added to this process's change tracker."
        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();

        var orderCancellation = await verifyDb.OrderCancellations.IgnoreQueryFilters().AsNoTracking()
            .FirstAsync(o => o.Id == executed.ExecutionIntentId);
        Assert.Equal(OrderCancellationStatus.Failed, orderCancellation.Status);
        Assert.NotNull(orderCancellation.FailureReason);
        Assert.StartsWith(ShopifyFailureReasons.ReauthorizationRequiredPrefix, orderCancellation.FailureReason);
        Assert.Contains("401", orderCancellation.FailureReason);

        var integration = await verifyDb.Integrations.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(i => i.BusinessId == business.Id && i.Platform == ShopifyIntegrationOAuthProvider.PlatformName);
        Assert.NotNull(integration.ReauthorizationRequiredAt);
        Assert.True(integration.IsActive); // Decision 1/2 — an auth failure never deactivates the integration

        var cancellationAuditLogs = await verifyDb.AuditLogs.IgnoreQueryFilters()
            .Where(a => a.BusinessId == business.Id
                && a.EntityType == AuditEntityTypes.OrderCancellation
                && a.EntityId == orderCancellation.Id.ToString())
            .ToListAsync();
        var cancellationLog = Assert.Single(cancellationAuditLogs);
        Assert.Equal(AuditEventTypes.OrderCancellationFailed, cancellationLog.EventType);
        Assert.Equal(ActorType.NivoraBusinessStaff, cancellationLog.Actor);
        Assert.Equal(executingStaffUserId, cancellationLog.ChangedByUserId);
        Assert.Contains(ShopifyFailureReasons.ReauthorizationRequiredPrefix, cancellationLog.NewValue);

        var integrationAuditLogs = await verifyDb.AuditLogs.IgnoreQueryFilters()
            .Where(a => a.BusinessId == business.Id
                && a.EntityType == AuditEntityTypes.Integration
                && a.EntityId == integration.Id.ToString())
            .ToListAsync();
        var integrationLog = Assert.Single(integrationAuditLogs);
        Assert.Equal(AuditEventTypes.IntegrationReauthorizationRequired, integrationLog.EventType);
        Assert.Equal(ActorType.NivoraBusinessStaff, integrationLog.Actor);
        Assert.Equal(executingStaffUserId, integrationLog.ChangedByUserId);
    }

    /// <summary>Mirrors the 401 test above for HTTP 403 (Decision 7 item 2)
    /// — lighter assertions, since the shared MarkAuthFailureAsync/audit
    /// mechanism is already fully exercised above.</summary>
    [Fact]
    public async Task ExecuteApprovedActionAsync_MutationHttp403_MarksFailedWithAuthReason_SetsReauthorizationRequired()
    {
        var tenant = (MutableTenantAccessor)_provider.GetRequiredService<ICurrentTenantAccessor>();
        const string customerExternalId = "gid://shopify/Customer/AAA";

        await using var seedScope = _provider.CreateAsyncScope();
        var seedDb = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tokenProtector = seedScope.ServiceProvider.GetRequiredService<IIntegrationTokenProtector>();
        var (business, approval, _) = await SeedApprovalAsync(seedDb, customerExternalId: customerExternalId);
        await AddIntegrationAsync(seedDb, tokenProtector, business.Id);
        var executingStaffUserId = await CreateStaffUserAsync(seedDb, business.Id);

        tenant.CurrentBusinessId = business.Id;
        await using var readScope = _provider.CreateAsyncScope();
        var readDb = readScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var auditLogService = readScope.ServiceProvider.GetRequiredService<IAuditLogService>();
        var level2Checker = readScope.ServiceProvider.GetRequiredService<ILevel2VerificationChecker>();
        var orderLookupService = NewOrderLookupService(
            readScope.ServiceProvider,
            SequencedResponses(
                OrderOwnerResponse(approval.ResourceId, customerExternalId),
                CancellationStateResponse(approval.ResourceId, null)));
        var ownershipService = new OrderOwnershipAuthorizationService(readDb);
        var orderCancellationService = readScope.ServiceProvider.GetRequiredService<IOrderCancellationService>();
        var orderCancellationExecutor = NewOrderCancellationExecutor(
            readScope.ServiceProvider, _ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        var executor = new OrderCancelApprovedActionExecutor(
            readDb, auditLogService, level2Checker, orderLookupService, ownershipService, orderCancellationService,
            orderCancellationExecutor);

        var outcome = await executor.ExecuteApprovedActionAsync(approval.Id, executingStaffUserId, CancellationToken.None);

        var executed = Assert.IsType<ExecutionOutcome.Executed>(outcome);
        Assert.Equal(ExecutionResultStatus.Failed, executed.Status);
        Assert.StartsWith(ShopifyFailureReasons.ReauthorizationRequiredPrefix, executed.Detail);
        Assert.Contains("403", executed.Detail);

        var integration = await readDb.Integrations.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(i => i.BusinessId == business.Id && i.Platform == ShopifyIntegrationOAuthProvider.PlatformName);
        Assert.NotNull(integration.ReauthorizationRequiredAt);
    }

    /// <summary>
    /// Negative regression: an ordinary Shopify userErrors rejection (the
    /// pre-existing, unmodified path) must NEVER be misclassified as an
    /// auth failure — no ReauthorizationRequiredAt write, no
    /// OrderCancellationFailed/IntegrationReauthorizationRequired audit
    /// events, matching design §8.3's explicit narrow-scoping requirement.
    /// </summary>
    [Fact]
    public async Task ExecuteApprovedActionAsync_MutationRejectedByShopify_DoesNotSetReauthorizationRequired_DoesNotWriteNewAuditEvents()
    {
        var tenant = (MutableTenantAccessor)_provider.GetRequiredService<ICurrentTenantAccessor>();
        const string customerExternalId = "gid://shopify/Customer/AAA";

        await using var seedScope = _provider.CreateAsyncScope();
        var seedDb = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tokenProtector = seedScope.ServiceProvider.GetRequiredService<IIntegrationTokenProtector>();
        var (business, approval, _) = await SeedApprovalAsync(seedDb, customerExternalId: customerExternalId);
        await AddIntegrationAsync(seedDb, tokenProtector, business.Id);
        var executingStaffUserId = await CreateStaffUserAsync(seedDb, business.Id);

        tenant.CurrentBusinessId = business.Id;
        await using var readScope = _provider.CreateAsyncScope();
        var readDb = readScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var auditLogService = readScope.ServiceProvider.GetRequiredService<IAuditLogService>();
        var level2Checker = readScope.ServiceProvider.GetRequiredService<ILevel2VerificationChecker>();
        var orderLookupService = NewOrderLookupService(
            readScope.ServiceProvider,
            SequencedResponses(
                OrderOwnerResponse(approval.ResourceId, customerExternalId),
                CancellationStateResponse(approval.ResourceId, null)));
        var ownershipService = new OrderOwnershipAuthorizationService(readDb);
        var orderCancellationService = readScope.ServiceProvider.GetRequiredService<IOrderCancellationService>();
        var orderCancellationExecutor = NewOrderCancellationExecutor(
            readScope.ServiceProvider, _ => JsonResponse(RejectedMutationResponse));
        var executor = new OrderCancelApprovedActionExecutor(
            readDb, auditLogService, level2Checker, orderLookupService, ownershipService, orderCancellationService,
            orderCancellationExecutor);

        var outcome = await executor.ExecuteApprovedActionAsync(approval.Id, executingStaffUserId, CancellationToken.None);

        var executed = Assert.IsType<ExecutionOutcome.Executed>(outcome);
        Assert.Equal(ExecutionResultStatus.Failed, executed.Status);
        Assert.DoesNotContain(ShopifyFailureReasons.ReauthorizationRequiredPrefix, executed.Detail);

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();

        var integration = await verifyDb.Integrations.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(i => i.BusinessId == business.Id && i.Platform == ShopifyIntegrationOAuthProvider.PlatformName);
        Assert.Null(integration.ReauthorizationRequiredAt);

        var cancellationAuditLogs = await verifyDb.AuditLogs.IgnoreQueryFilters()
            .Where(a => a.BusinessId == business.Id && a.EntityType == AuditEntityTypes.OrderCancellation)
            .ToListAsync();
        Assert.Empty(cancellationAuditLogs);

        var integrationAuditLogs = await verifyDb.AuditLogs.IgnoreQueryFilters()
            .Where(a => a.BusinessId == business.Id && a.EntityType == AuditEntityTypes.Integration)
            .ToListAsync();
        Assert.Empty(integrationAuditLogs);
    }

    /// <summary>
    /// Step 13 — TOCTOU claim-race-loss: another, independent write flips
    /// Approval.Status away from Approved between step 7's earlier check
    /// and step 13's own claim, simulating a concurrent Execute call
    /// winning the Approved->Consumed race first
    /// (docs/handoffs/step-43-cancelorder-execute-step-13-atomic-claim-design.md
    /// §4/§10/§12). The write is injected via the fake Shopify response
    /// callback — the one point between step 7 and the claim where this
    /// test's own code runs synchronously inside Execute's call stack — and
    /// uses a raw SQL UPDATE against a completely independent DbContext
    /// scope/connection, not the tracked `approval`/`readDb` instances, so
    /// it genuinely represents an independent writer rather than a mutation
    /// of this test's own in-memory state. Must refuse with the new,
    /// distinct ApprovalClaimLost value (step-43 addendum owner decision) —
    /// never ApprovalNotApproved — and must write NO AuditLog row (step-43
    /// §15b, the same exclusion already established for step 6's own
    /// status check, restated for this new value).
    /// </summary>
    [Fact]
    public async Task ExecuteApprovedActionAsync_ConcurrentExecuteWinsClaimRace_ReturnsApprovalClaimLostAndWritesNoAuditLog()
    {
        var tenant = (MutableTenantAccessor)_provider.GetRequiredService<ICurrentTenantAccessor>();
        const string customerExternalId = "gid://shopify/Customer/AAA";

        await using var seedScope = _provider.CreateAsyncScope();
        var seedDb = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tokenProtector = seedScope.ServiceProvider.GetRequiredService<IIntegrationTokenProtector>();
        var (business, approval, _) = await SeedApprovalAsync(seedDb, customerExternalId: customerExternalId);
        await AddIntegrationAsync(seedDb, tokenProtector, business.Id);

        tenant.CurrentBusinessId = business.Id;
        await using var readScope = _provider.CreateAsyncScope();
        var readDb = readScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var auditLogService = readScope.ServiceProvider.GetRequiredService<IAuditLogService>();
        var level2Checker = readScope.ServiceProvider.GetRequiredService<ILevel2VerificationChecker>();
        var orderLookupService = NewOrderLookupService(
            readScope.ServiceProvider,
            _ =>
            {
                using var raceScope = _provider.CreateScope();
                var raceDb = raceScope.ServiceProvider.GetRequiredService<AppDbContext>();
                raceDb.Database.ExecuteSqlInterpolated(
                    $"UPDATE \"Approvals\" SET \"Status\" = 'Consumed', \"ConsumedAt\" = now() WHERE \"Id\" = {approval.Id}");
                return JsonResponse(OrderOwnerResponse(approval.ResourceId, customerExternalId));
            });
        var ownershipService = new OrderOwnershipAuthorizationService(readDb);
        var orderCancellationService = readScope.ServiceProvider.GetRequiredService<IOrderCancellationService>();
        var executor = new OrderCancelApprovedActionExecutor(
            readDb, auditLogService, level2Checker, orderLookupService, ownershipService, orderCancellationService,
            new ThrowingOrderCancellationExecutor());

        var outcome = await executor.ExecuteApprovedActionAsync(approval.Id, Guid.NewGuid(), CancellationToken.None);

        var refused = Assert.IsType<ExecutionOutcome.Refused>(outcome);
        Assert.Equal(ExecutionRefusalReason.ApprovalClaimLost, refused.Reason);
        Assert.Equal("ApprovalClaimLost", refused.Detail);

        var auditLogs = await ReadAuditLogsAsync(business.Id, approval.Id);
        Assert.Empty(auditLogs);

        // The "concurrent winner"'s own write is what the Approval reflects
        // now — this losing attempt's own transaction never committed.
        Assert.Equal(ApprovalStatus.Consumed, await ReadApprovalStatusAsync(approval.Id));
    }

    /// <summary>
    /// Step 13 — the claim's own WHERE clause re-checks ExpiresAt > now a
    /// second time, independently of step 7's earlier (already-passed)
    /// check
    /// (docs/handoffs/step-43-cancelorder-execute-step-13-atomic-claim-design.md
    /// §5). Since Execute reuses the same `now` captured at step 7 for
    /// both checks (§5's own confirmed-safe engineering choice), the ONLY
    /// way to make the claim's own ExpiresAt predicate fail while step 7's
    /// identical predicate already passed is for the row's ExpiresAt
    /// column itself to change in the database between the two checks —
    /// this test injects exactly that, via a raw SQL UPDATE from a
    /// completely independent DbContext scope, executed inside the fake
    /// Shopify response callback (the one point between step 7 and the
    /// claim where this test's own code runs synchronously). Deliberately
    /// distinct from ExecuteApprovedActionAsync_ApprovalExpired_... (step
    /// 7's own, non-atomic, pre-existing check) — that test's Approval is
    /// already expired before Execute is ever called; this one's is not.
    /// Must refuse with ApprovalClaimLost (the same value a lost race
    /// produces — the two causes are indistinguishable from the
    /// affected-row count alone, per step-43 §4) and write no AuditLog row.
    /// </summary>
    [Fact]
    public async Task ExecuteApprovedActionAsync_ExpiresAtElapsesBetweenStep7AndClaim_ReturnsApprovalClaimLostAndWritesNoAuditLog()
    {
        var tenant = (MutableTenantAccessor)_provider.GetRequiredService<ICurrentTenantAccessor>();
        const string customerExternalId = "gid://shopify/Customer/AAA";

        await using var seedScope = _provider.CreateAsyncScope();
        var seedDb = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tokenProtector = seedScope.ServiceProvider.GetRequiredService<IIntegrationTokenProtector>();
        // Expires 15 minutes out (the default) — comfortably valid at step
        // 7's check; the callback below is what actually expires it before
        // the claim runs.
        var (business, approval, _) = await SeedApprovalAsync(seedDb, customerExternalId: customerExternalId);
        await AddIntegrationAsync(seedDb, tokenProtector, business.Id);

        tenant.CurrentBusinessId = business.Id;
        await using var readScope = _provider.CreateAsyncScope();
        var readDb = readScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var auditLogService = readScope.ServiceProvider.GetRequiredService<IAuditLogService>();
        var level2Checker = readScope.ServiceProvider.GetRequiredService<ILevel2VerificationChecker>();
        var orderLookupService = NewOrderLookupService(
            readScope.ServiceProvider,
            _ =>
            {
                using var raceScope = _provider.CreateScope();
                var raceDb = raceScope.ServiceProvider.GetRequiredService<AppDbContext>();
                raceDb.Database.ExecuteSqlInterpolated(
                    $"UPDATE \"Approvals\" SET \"ExpiresAt\" = now() - interval '1 minute' WHERE \"Id\" = {approval.Id}");
                return JsonResponse(OrderOwnerResponse(approval.ResourceId, customerExternalId));
            });
        var ownershipService = new OrderOwnershipAuthorizationService(readDb);
        var orderCancellationService = readScope.ServiceProvider.GetRequiredService<IOrderCancellationService>();
        var executor = new OrderCancelApprovedActionExecutor(
            readDb, auditLogService, level2Checker, orderLookupService, ownershipService, orderCancellationService,
            new ThrowingOrderCancellationExecutor());

        var outcome = await executor.ExecuteApprovedActionAsync(approval.Id, Guid.NewGuid(), CancellationToken.None);

        var refused = Assert.IsType<ExecutionOutcome.Refused>(outcome);
        Assert.Equal(ExecutionRefusalReason.ApprovalClaimLost, refused.Reason);
        Assert.Equal("ApprovalClaimLost", refused.Detail);

        var auditLogs = await ReadAuditLogsAsync(business.Id, approval.Id);
        Assert.Empty(auditLogs);

        // Only ExpiresAt was forced into the past — Status is untouched
        // (still Approved), and this losing attempt's own transaction
        // never committed, so no OrderCancellationId was ever set either.
        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var finalApproval = await verifyDb.Approvals.IgnoreQueryFilters().AsNoTracking()
            .FirstAsync(a => a.Id == approval.Id);
        Assert.Equal(ApprovalStatus.Approved, finalApproval.Status);
        Assert.Null(finalApproval.OrderCancellationId);
    }

    /// <summary>
    /// Step 13 — a second, independently-proposed Approval for the SAME
    /// order (same BusinessId + ExternalOrderId) as an already-created,
    /// still-Pending OrderCancellation row must refuse with
    /// DuplicatePendingCancellation, per the pre-existing
    /// IX_OrderCancellations_BusinessId_ExternalOrderId_Pending guard
    /// (Step 23Q->23T, unmodified and unaffected by step 13 — see
    /// docs/handoffs/step-43-cancelorder-execute-step-13-atomic-claim-design.md
    /// §10/§11). Must write NO AuditLog row for this specific refusal
    /// (step-43 addendum owner decision, §15b) — asserted negatively here,
    /// not merely left unchecked: the first Approval's own successful claim
    /// DOES write one ExecutionClaimed row (for its own Id), so this test
    /// explicitly checks the SECOND Approval's own AuditLog rows are empty,
    /// not the total count across both.
    /// </summary>
    [Fact]
    public async Task ExecuteApprovedActionAsync_SecondApprovalSameOrderWhilePending_ReturnsDuplicatePendingCancellationAndWritesNoAuditLog()
    {
        var tenant = (MutableTenantAccessor)_provider.GetRequiredService<ICurrentTenantAccessor>();
        const string customerExternalId = "gid://shopify/Customer/AAA";

        await using var seedScope = _provider.CreateAsyncScope();
        var seedDb = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tokenProtector = seedScope.ServiceProvider.GetRequiredService<IIntegrationTokenProtector>();
        var (business, firstApproval, _) = await SeedApprovalAsync(seedDb, customerExternalId: customerExternalId);
        await AddIntegrationAsync(seedDb, tokenProtector, business.Id);

        // A second, independent Approval for the SAME business and the
        // SAME order (SeedApprovalAsync's own hardcoded ResourceId) — its
        // own Widget/Customer/Conversation, a different ApprovalId, but the
        // exact same (BusinessId, ExternalOrderId) pair the Pending-row
        // guard is scoped to.
        var now = DateTimeOffset.UtcNow;
        var secondWidget = new Widget
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id,
            Name = "Second Test Widget",
            PublicKey = Guid.NewGuid().ToString("N"),
            CreatedAt = now,
            UpdatedAt = now,
        };
        // A distinct ExternalId — Customer has its own
        // (BusinessId, ExternalId) unique index, so the second customer
        // cannot share the first customer's "AAA" value under the same
        // Business.
        const string secondCustomerExternalId = "gid://shopify/Customer/AAA2";
        var secondCustomer = new Customer
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id,
            Name = "Second Test Customer",
            Email = "second.customer@marker-test.invalid",
            ExternalId = secondCustomerExternalId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var secondConversation = new Conversation
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id,
            WidgetId = secondWidget.Id,
            CustomerId = secondCustomer.Id,
            VisitorSessionId = Guid.NewGuid().ToString("N"),
            Channel = "Website",
            Status = ConversationStatus.Open,
            Level2VerifiedUntil = now.AddHours(1),
            CreatedAt = now,
            UpdatedAt = now,
        };
        var secondCanonicalArguments = ToolArgumentCanonicalizer.CanonicalizeOrderCancellationArgs(
            restock: true, method: RefundMethod.OriginalPaymentMethod, reason: OrderCancelReason.Customer);
        var secondApproval = new Approval
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id,
            ConversationId = secondConversation.Id,
            CustomerId = secondCustomer.Id,
            ToolName = "cancelOrder",
            ResourceId = firstApproval.ResourceId,
            CanonicalArguments = secondCanonicalArguments,
            ActionHash = ActionHasher.Derive(
                business.Id, secondConversation.Id, secondCustomer.Id,
                "cancelOrder", firstApproval.ResourceId, secondCanonicalArguments),
            DisplaySummary = "Cancel order (second, independent proposal for the same order).",
            Status = ApprovalStatus.Approved,
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(15),
        };
        seedDb.Widgets.Add(secondWidget);
        seedDb.Customers.Add(secondCustomer);
        seedDb.Conversations.Add(secondConversation);
        seedDb.Approvals.Add(secondApproval);
        await seedDb.SaveChangesAsync();
        var firstExecutingStaffUserId = await CreateStaffUserAsync(seedDb, business.Id);

        tenant.CurrentBusinessId = business.Id;

        // First Execute call — succeeds, creating the Pending
        // OrderCancellation row the second call will collide against.
        await using (var firstScope = _provider.CreateAsyncScope())
        {
            var firstDb = firstScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var firstAuditLogService = firstScope.ServiceProvider.GetRequiredService<IAuditLogService>();
            var firstLevel2Checker = firstScope.ServiceProvider.GetRequiredService<ILevel2VerificationChecker>();
            var firstOrderLookupService = NewOrderLookupService(
                firstScope.ServiceProvider,
                SequencedResponses(
                    OrderOwnerResponse(firstApproval.ResourceId, customerExternalId),
                    CancellationStateResponse(firstApproval.ResourceId, null)));
            var firstOwnershipService = new OrderOwnershipAuthorizationService(firstDb);
            var firstOrderCancellationService = firstScope.ServiceProvider.GetRequiredService<IOrderCancellationService>();
            var firstOrderCancellationExecutor = NewOrderCancellationExecutor(
                firstScope.ServiceProvider, _ => JsonResponse(AcceptedJobResponse));
            var firstExecutor = new OrderCancelApprovedActionExecutor(
                firstDb, firstAuditLogService, firstLevel2Checker, firstOrderLookupService,
                firstOwnershipService, firstOrderCancellationService, firstOrderCancellationExecutor);

            var firstOutcome = await firstExecutor.ExecuteApprovedActionAsync(
                firstApproval.Id, firstExecutingStaffUserId, CancellationToken.None);
            var firstExecuted = Assert.IsType<ExecutionOutcome.Executed>(firstOutcome);
            Assert.Equal(ExecutionResultStatus.UnknownNeedsReconciliation, firstExecuted.Status);
        }

        // Second Execute call, same order, first attempt still Pending —
        // must be blocked.
        await using var readScope = _provider.CreateAsyncScope();
        var readDb = readScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var auditLogService = readScope.ServiceProvider.GetRequiredService<IAuditLogService>();
        var level2Checker = readScope.ServiceProvider.GetRequiredService<ILevel2VerificationChecker>();
        var orderLookupService = NewOrderLookupService(
            readScope.ServiceProvider,
            _ => JsonResponse(OrderOwnerResponse(secondApproval.ResourceId, secondCustomerExternalId)));
        var ownershipService = new OrderOwnershipAuthorizationService(readDb);
        var orderCancellationService = readScope.ServiceProvider.GetRequiredService<IOrderCancellationService>();
        var executor = new OrderCancelApprovedActionExecutor(
            readDb, auditLogService, level2Checker, orderLookupService, ownershipService, orderCancellationService,
            new ThrowingOrderCancellationExecutor());

        // Even though this attempt is ultimately refused and rolled back
        // (DuplicatePendingCancellation), it still reaches step 13's atomic
        // claim and adds an ExecutionClaimed AuditLog row to the same
        // tracked-context batch RequestCancellationAsync flushes — that row
        // needs a real ChangedByUserId to satisfy
        // FK_AuditLogs_AspNetUsers_ChangedByUserId, or the batch fails on
        // the FK before ever reaching the duplicate-key check this test
        // means to exercise.
        var secondExecutingStaffUserId = await CreateStaffUserAsync(seedDb, business.Id);
        var outcome = await executor.ExecuteApprovedActionAsync(secondApproval.Id, secondExecutingStaffUserId, CancellationToken.None);

        var refused = Assert.IsType<ExecutionOutcome.Refused>(outcome);
        Assert.Equal(ExecutionRefusalReason.DuplicatePendingCancellation, refused.Reason);
        Assert.Equal($"DuplicatePendingCancellation:{secondApproval.ResourceId}", refused.Detail);

        // Negative assertion, not merely omitted: the SECOND Approval's own
        // AuditLog rows must be empty — the first Approval's own successful
        // claim wrote its own ExecutionClaimed row under a different
        // EntityId.
        var secondApprovalAuditLogs = await ReadAuditLogsAsync(business.Id, secondApproval.Id);
        Assert.Empty(secondApprovalAuditLogs);

        // The second attempt's own claim is rolled back — Approval reverts
        // to Approved, not left Consumed.
        Assert.Equal(ApprovalStatus.Approved, await ReadApprovalStatusAsync(secondApproval.Id));
    }

    private class MutableTenantAccessor : ICurrentTenantAccessor
    {
        public Guid? CurrentBusinessId { get; set; }
        public Guid? BusinessId => CurrentBusinessId;
    }
}
