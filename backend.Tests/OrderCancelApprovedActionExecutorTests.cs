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
/// OrderCancelProposalToolTests. Covers steps 3-12 of the execution ordering
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
/// this slice), plus one boundary test documenting that everything past
/// step 12 still throws NotImplementedException.
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
    private const string Shop = "test-shop";
    private const string AccessToken = "test-access-token";

    private ServiceProvider _provider = null!;
    private readonly List<Guid> _businessIdsToClean = [];

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
            .AddScoped<ILevel2VerificationChecker, Level2VerificationChecker>();
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
            // Cascade, so any Approval row is removed automatically when
            // its Conversation is. Integrations/Customers/Widgets before
            // Businesses.
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"AuditLogs\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Conversations\" WHERE \"BusinessId\" = ANY({ids})");
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
            ExternalAccountIdentifier = Shop,
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

    private static HttpResponseMessage JsonResponse(string json) =>
        new(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };

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
        var executor = new OrderCancelApprovedActionExecutor(
            readDb, auditLogService, level2Checker, orderLookupService, ownershipService);

        var outcome = await executor.ExecuteApprovedActionAsync(approval.Id, CancellationToken.None);

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
        var executor = new OrderCancelApprovedActionExecutor(
            readDb, auditLogService, level2Checker, orderLookupService, ownershipService);

        var outcome = await executor.ExecuteApprovedActionAsync(Guid.NewGuid(), CancellationToken.None);

        var refused = Assert.IsType<ExecutionOutcome.Refused>(outcome);
        Assert.Equal(ExecutionRefusalReason.ApprovalNotFound, refused.Reason);
    }

    /// <summary>
    /// Step 6 — every non-Approved status must refuse with
    /// ApprovalNotApproved and write exactly one durable AuditLog row
    /// (AuditEventTypes.UnauthorizedActionBlocked, ActorType.NivoraSystem,
    /// ChangedByUserId null, NewValue == the actual status) before
    /// returning. Covers all three non-Approved ApprovalStatus members —
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

        tenant.CurrentBusinessId = business.Id;
        await using var readScope = _provider.CreateAsyncScope();
        var readDb = readScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var auditLogService = readScope.ServiceProvider.GetRequiredService<IAuditLogService>();
        var level2Checker = readScope.ServiceProvider.GetRequiredService<ILevel2VerificationChecker>();
        var orderLookupService = ThrowingOrderLookupService(readScope.ServiceProvider);
        var ownershipService = new OrderOwnershipAuthorizationService(readDb);
        var executor = new OrderCancelApprovedActionExecutor(
            readDb, auditLogService, level2Checker, orderLookupService, ownershipService);

        var outcome = await executor.ExecuteApprovedActionAsync(approval.Id, CancellationToken.None);

        var refused = Assert.IsType<ExecutionOutcome.Refused>(outcome);
        Assert.Equal(ExecutionRefusalReason.ApprovalNotApproved, refused.Reason);

        var auditLogs = await ReadAuditLogsAsync(business.Id, approval.Id);
        var entry = Assert.Single(auditLogs);
        Assert.Equal(AuditEventTypes.UnauthorizedActionBlocked, entry.EventType);
        Assert.Equal(ActorType.NivoraSystem, entry.Actor);
        Assert.Null(entry.ChangedByUserId);
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

        tenant.CurrentBusinessId = business.Id;
        await using var readScope = _provider.CreateAsyncScope();
        var readDb = readScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var auditLogService = readScope.ServiceProvider.GetRequiredService<IAuditLogService>();
        var level2Checker = readScope.ServiceProvider.GetRequiredService<ILevel2VerificationChecker>();
        var orderLookupService = ThrowingOrderLookupService(readScope.ServiceProvider);
        var ownershipService = new OrderOwnershipAuthorizationService(readDb);
        var executor = new OrderCancelApprovedActionExecutor(
            readDb, auditLogService, level2Checker, orderLookupService, ownershipService);

        var outcome = await executor.ExecuteApprovedActionAsync(approval.Id, CancellationToken.None);

        var refused = Assert.IsType<ExecutionOutcome.Refused>(outcome);
        Assert.Equal(ExecutionRefusalReason.ApprovalExpired, refused.Reason);

        var auditLogs = await ReadAuditLogsAsync(business.Id, approval.Id);
        var entry = Assert.Single(auditLogs);
        Assert.Equal(AuditEventTypes.UnauthorizedActionBlocked, entry.EventType);
        Assert.Equal(ActorType.NivoraSystem, entry.Actor);
        Assert.Null(entry.ChangedByUserId);
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
        var executor = new OrderCancelApprovedActionExecutor(
            readDb, auditLogService, level2Checker, orderLookupService, ownershipService);

        var outcome = await executor.ExecuteApprovedActionAsync(approval.Id, CancellationToken.None);

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

        tenant.CurrentBusinessId = business.Id;
        await using var readScope = _provider.CreateAsyncScope();
        var readDb = readScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var auditLogService = readScope.ServiceProvider.GetRequiredService<IAuditLogService>();
        var level2Checker = readScope.ServiceProvider.GetRequiredService<ILevel2VerificationChecker>();
        var orderLookupService = ThrowingOrderLookupService(readScope.ServiceProvider);
        var ownershipService = new OrderOwnershipAuthorizationService(readDb);
        var executor = new OrderCancelApprovedActionExecutor(
            readDb, auditLogService, level2Checker, orderLookupService, ownershipService);

        var outcome = await executor.ExecuteApprovedActionAsync(approval.Id, CancellationToken.None);

        var refused = Assert.IsType<ExecutionOutcome.Refused>(outcome);
        Assert.Equal(ExecutionRefusalReason.Level2VerificationFailed, refused.Reason);

        var auditLogs = await ReadAuditLogsAsync(business.Id, approval.Id);
        var entry = Assert.Single(auditLogs);
        Assert.Equal(AuditEventTypes.UnauthorizedActionBlocked, entry.EventType);
        Assert.Equal(ActorType.NivoraSystem, entry.Actor);
        Assert.Null(entry.ChangedByUserId);
        Assert.Equal("Level2VerificationFailed", entry.NewValue);
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
        var executor = new OrderCancelApprovedActionExecutor(
            readDb, auditLogService, level2Checker, orderLookupService, ownershipService);

        var outcome = await executor.ExecuteApprovedActionAsync(approval.Id, CancellationToken.None);

        var refused = Assert.IsType<ExecutionOutcome.Refused>(outcome);
        Assert.Equal(ExecutionRefusalReason.OwnershipVerificationFailed, refused.Reason);

        var auditLogs = await ReadAuditLogsAsync(business.Id, approval.Id);
        var entry = Assert.Single(auditLogs);
        Assert.Equal(AuditEventTypes.UnauthorizedActionBlocked, entry.EventType);
        Assert.Equal(ActorType.NivoraSystem, entry.Actor);
        Assert.Null(entry.ChangedByUserId);
        Assert.Equal("OwnershipVerificationFailed:OwnerMismatch", entry.NewValue);

        // No mutation, no consumption: the Approval is untouched by a step
        // 10 refusal, unlike step 13's (not yet implemented) atomic claim.
        Assert.Equal(ApprovalStatus.Approved, await ReadApprovalStatusAsync(approval.Id));
    }

    /// <summary>
    /// Step 10 — Shopify confirms the SAME owning customer as the
    /// Approval's resolved Customer -> ownership verification succeeds,
    /// writes NO AuditLog row, and execution falls through toward step 11,
    /// which is not yet implemented -> NotImplementedException. Proves the
    /// negative case explicitly, distinct from the general "everything
    /// passes" boundary test below (which this test overlaps with by
    /// design, for the same reason step-38/step-39 each added their own
    /// dedicated success-path assertion alongside the general boundary
    /// test rather than relying on the boundary test alone).
    /// </summary>
    [Fact]
    public async Task ExecuteApprovedActionAsync_OwnershipVerificationSucceeds_FallsThroughToNotImplementedAndWritesNoAuditLog()
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
            _ => JsonResponse(OrderOwnerResponse(approval.ResourceId, customerExternalId)));
        var ownershipService = new OrderOwnershipAuthorizationService(readDb);
        var executor = new OrderCancelApprovedActionExecutor(
            readDb, auditLogService, level2Checker, orderLookupService, ownershipService);

        await Assert.ThrowsAsync<NotImplementedException>(
            () => executor.ExecuteApprovedActionAsync(approval.Id, CancellationToken.None));

        var auditLogs = await ReadAuditLogsAsync(business.Id, approval.Id);
        Assert.Empty(auditLogs);
        Assert.Equal(ApprovalStatus.Approved, await ReadApprovalStatusAsync(approval.Id));
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
        var executor = new OrderCancelApprovedActionExecutor(
            readDb, auditLogService, level2Checker, orderLookupService, ownershipService);

        var outcome = await executor.ExecuteApprovedActionAsync(approval.Id, CancellationToken.None);

        var refused = Assert.IsType<ExecutionOutcome.Refused>(outcome);
        Assert.Equal(ExecutionRefusalReason.ActionHashMismatch, refused.Reason);

        var auditLogs = await ReadAuditLogsAsync(business.Id, approval.Id);
        var entry = Assert.Single(auditLogs);
        Assert.Equal(AuditEventTypes.UnauthorizedActionBlocked, entry.EventType);
        Assert.Equal(ActorType.NivoraSystem, entry.Actor);
        Assert.Null(entry.ChangedByUserId);
        Assert.Equal("ActionHashMismatch", entry.NewValue);

        Assert.Equal(ApprovalStatus.Approved, await ReadApprovalStatusAsync(approval.Id));
    }

    /// <summary>
    /// Steps 11/12 — the stored ActionHash matches what Execute recomputes
    /// (the ordinary case: no customer-identity drift since proposal time)
    /// -> step 11 does not refuse, step 12's execution-leg key derivation
    /// runs (a pure, always-succeeding computation with no refusal path
    /// and no AuditLog write of its own, per step-42 §2), and execution
    /// falls through to the NotImplementedException boundary. Writes NO
    /// AuditLog row. Proves the negative case explicitly, distinct from
    /// the general boundary test below, same convention as every earlier
    /// step's dedicated success-path assertion.
    /// </summary>
    [Fact]
    public async Task ExecuteApprovedActionAsync_ActionHashMatches_FallsThroughToNotImplementedAndWritesNoAuditLog()
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
            _ => JsonResponse(OrderOwnerResponse(approval.ResourceId, customerExternalId)));
        var ownershipService = new OrderOwnershipAuthorizationService(readDb);
        var executor = new OrderCancelApprovedActionExecutor(
            readDb, auditLogService, level2Checker, orderLookupService, ownershipService);

        await Assert.ThrowsAsync<NotImplementedException>(
            () => executor.ExecuteApprovedActionAsync(approval.Id, CancellationToken.None));

        var auditLogs = await ReadAuditLogsAsync(business.Id, approval.Id);
        Assert.Empty(auditLogs);
        Assert.Equal(ApprovalStatus.Approved, await ReadApprovalStatusAsync(approval.Id));
    }

    /// <summary>
    /// Deliberate scope-boundary test, same convention as
    /// OrderCancelProposalToolTests' own explicit-refusal tests: documents,
    /// rather than merely leaves implicit, exactly where this session's
    /// implementation stops. Must be updated (not deleted) by whichever
    /// future session extends ExecuteApprovedActionAsync past step 12 — a
    /// passing NotImplementedException here after that point would mean the
    /// new step's logic was never reached. Extended by this pass (was
    /// previously the steps-3-10 boundary) — no new seeding is required,
    /// since steps 11/12 introduce no new external dependency and the
    /// default seed's ActionHash already matches what Execute recomputes.
    /// Also proves the adjacent-case negative: a valid Approved+unexpired+
    /// resolved-customer+Level2-verified+ownership-matching+hash-matching
    /// Approval does NOT trigger any of steps 6/7/8/9/10/11's refusals, and
    /// writes NO AuditLog row — none of their writes are unconditional on
    /// every ExecuteApprovedActionAsync call.
    /// </summary>
    [Fact]
    public async Task ExecuteApprovedActionAsync_ApprovalExistsForCurrentTenant_ThrowsNotImplementedAndWritesNoAuditLog()
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
            _ => JsonResponse(OrderOwnerResponse(approval.ResourceId, customerExternalId)));
        var ownershipService = new OrderOwnershipAuthorizationService(readDb);
        var executor = new OrderCancelApprovedActionExecutor(
            readDb, auditLogService, level2Checker, orderLookupService, ownershipService);

        await Assert.ThrowsAsync<NotImplementedException>(
            () => executor.ExecuteApprovedActionAsync(approval.Id, CancellationToken.None));

        var auditLogs = await ReadAuditLogsAsync(business.Id, approval.Id);
        Assert.Empty(auditLogs);
    }

    private class MutableTenantAccessor : ICurrentTenantAccessor
    {
        public Guid? CurrentBusinessId { get; set; }
        public Guid? BusinessId => CurrentBusinessId;
    }
}
