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
/// Service-level tests for RefundApprovedActionExecutor — Step refundOrder
/// Phase 3. Mirrors OrderCancelApprovedActionExecutorTests' own conventions
/// (real Postgres via a bare ServiceProvider, MutableTenantAccessor,
/// FakeShopifyHttpMessageHandler per Shopify-facing dependency). Covers
/// Owner Decision 3's Layer 2 kill switch, the Approval state checks,
/// ownership, Level2, the refundable-quantity/amount preflight (design
/// §8.3/§17), the actual refundCreate call, and Approval.RefundId
/// association — see RefundApprovedActionExecutor's own doc comment for the
/// full step-ordering rationale this exercises.
/// </summary>
public class RefundApprovedActionExecutorTests : IAsyncLifetime
{
    private const string AdminApiVersion = "2026-07";
    private const string AccessToken = "test-access-token";
    private const string OrderId = "gid://shopify/Order/12345";
    private const string LineItemId = "gid://shopify/LineItem/1";

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
            .AddScoped<IOrderOwnershipAuthorizationService, OrderOwnershipAuthorizationService>()
            .AddScoped<IRefundService, RefundService>();
        services.AddDataProtection();
        services.AddScoped<IIntegrationTokenProtector, IntegrationTokenProtector>();
        _provider = services.BuildServiceProvider();

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await TestBusinessCleanup.DeleteThenDisposeAsync(_businessIdsToClean, _provider);
    }

    /// <summary>
    /// Seeds one Business/Widget/Customer/Conversation/Approval graph for a
    /// refundOrder Approval requesting `quantity` units of LineItemId.
    /// `refundToolEnabled` defaults true (Owner Decision 3, Layer 2 — the
    /// "everything passes" case every non-kill-switch test relies on;
    /// kill-switch tests override it explicitly). `withIntegration` seeds an
    /// active Shopify Integration unless a test needs to exercise the
    /// no-integration path.
    /// </summary>
    private async Task<(Business Business, Approval Approval, Customer Customer)> SeedApprovalAsync(
        AppDbContext db,
        IIntegrationTokenProtector tokenProtector,
        ApprovalStatus status = ApprovalStatus.Approved,
        DateTimeOffset? expiresAt = null,
        bool customerResolved = true,
        bool level2Verified = true,
        string? customerExternalId = "gid://shopify/Customer/999",
        bool refundToolEnabled = true,
        bool withIntegration = true,
        int quantity = 2,
        RefundMethod method = RefundMethod.OriginalPaymentMethod)
    {
        var now = DateTimeOffset.UtcNow;
        var business = new Business
        {
            Id = Guid.NewGuid(),
            Name = $"RefundApprovedActionExecutor Test {Guid.NewGuid():N}",
            TimeZone = "Europe/Copenhagen",
            RefundToolEnabled = refundToolEnabled,
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

        var items = new[] { (LineItemId, quantity) };
        var canonicalArguments = ToolArgumentCanonicalizer.CanonicalizeRefundArgs(method, items);
        var approval = new Approval
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id,
            ConversationId = conversation.Id,
            CustomerId = customer.Id,
            ToolName = RefundProposalTool.ToolName,
            ResourceId = OrderId,
            CanonicalArguments = canonicalArguments,
            ActionHash = ActionHasher.Derive(
                business.Id, conversation.Id, customer.Id, RefundProposalTool.ToolName, OrderId, canonicalArguments),
            DisplaySummary = $"Refund order {OrderId} ({quantity}x {LineItemId}).",
            Status = status,
            CreatedAt = now,
            ExpiresAt = expiresAt ?? now.AddMinutes(15),
        };

        db.Businesses.Add(business);
        db.Widgets.Add(widget);
        db.Customers.Add(customer);
        db.Conversations.Add(conversation);
        db.Approvals.Add(approval);

        if (withIntegration)
        {
            db.Integrations.Add(new Integration
            {
                Id = Guid.NewGuid(),
                BusinessId = business.Id,
                Platform = ShopifyIntegrationOAuthProvider.PlatformName,
                EncryptedAccessToken = tokenProtector.Protect(ShopifyIntegrationOAuthProvider.PlatformName, AccessToken),
                ExternalAccountIdentifier = $"test-shop-{Guid.NewGuid():N}",
                IsActive = true,
                ConnectedAt = now,
                UpdatedAt = now,
            });
        }

        await db.SaveChangesAsync();
        _businessIdsToClean.Add(business.Id);

        return (business, approval, customer);
    }

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

    private static ShopifyOptions Options_ => new()
    {
        ClientId = "unused", ClientSecret = "unused", RedirectUri = "unused", AdminApiVersion = AdminApiVersion,
    };

    private static ShopifyOrderLookupService NewOrderLookupService(
        IServiceProvider scopedProvider, Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(
            new HttpClient(new FakeShopifyHttpMessageHandler(respond)),
            scopedProvider.GetRequiredService<AppDbContext>(),
            scopedProvider.GetRequiredService<IIntegrationTokenProtector>(),
            Microsoft.Extensions.Options.Options.Create(Options_));

    private static ShopifyOrderLookupService ThrowingOrderLookupService(IServiceProvider scopedProvider) =>
        NewOrderLookupService(scopedProvider, _ => throw new InvalidOperationException(
            "Ownership lookup must not be reached — this test's Approval should refuse at an earlier step."));

    private static ShopifyRefundLookupService NewRefundLookupService(
        IServiceProvider scopedProvider, Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(
            new HttpClient(new FakeShopifyHttpMessageHandler(respond)),
            scopedProvider.GetRequiredService<AppDbContext>(),
            scopedProvider.GetRequiredService<IIntegrationTokenProtector>(),
            Microsoft.Extensions.Options.Options.Create(Options_));

    private static ShopifyRefundLookupService ThrowingRefundLookupService(IServiceProvider scopedProvider) =>
        NewRefundLookupService(scopedProvider, _ => throw new InvalidOperationException(
            "Refund preflight must not be reached — this test's Approval should refuse at an earlier step."));

    private static ShopifyRefundExecutor NewRefundExecutor(
        IServiceProvider scopedProvider, Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(
            new HttpClient(new FakeShopifyHttpMessageHandler(respond)),
            scopedProvider.GetRequiredService<AppDbContext>(),
            scopedProvider.GetRequiredService<IIntegrationTokenProtector>(),
            Microsoft.Extensions.Options.Options.Create(Options_),
            scopedProvider.GetRequiredService<IRefundService>());

    private static ShopifyRefundExecutor ThrowingRefundExecutor(IServiceProvider scopedProvider) =>
        NewRefundExecutor(scopedProvider, _ => throw new InvalidOperationException(
            "refundCreate must not be reached — this test's Approval should refuse before the mutation."));

    private static HttpResponseMessage JsonResponse(string json) =>
        new(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };

    private static string OrderOwnerResponse(string orderId, string? customerId) =>
        customerId is null
            ? $$"""{ "data": { "order": { "id": "{{orderId}}", "customer": null } } }"""
            : $$"""{ "data": { "order": { "id": "{{orderId}}", "customer": { "id": "{{customerId}}" } } } }""";

    private static string PreflightResponse(int refundableQuantity, string amount = "19.99", string currency = "EUR") =>
        $$"""
        { "data": { "order": { "id": "{{OrderId}}", "lineItems": { "edges": [
          { "node": { "id": "{{LineItemId}}", "refundableQuantity": {{refundableQuantity}} } }
        ] }, "suggestedRefund": { "refundLineItems": [
          { "subtotalSet": { "presentmentMoney": { "amount": "{{amount}}", "currencyCode": "{{currency}}" } } }
        ] } } } }
        """;

    private const string RefundCreateSuccessResponse =
        """{ "data": { "refundCreate": { "refund": { "id": "gid://shopify/Refund/1", "totalRefundedSet": { "presentmentMoney": { "amount": "39.98" } } }, "userErrors": [] } } }""";

    private async Task<RefundApprovedActionExecutor> BuildExecutorAsync(
        IServiceProvider scopedProvider,
        Func<HttpRequestMessage, HttpResponseMessage>? ownershipResponder = null,
        Func<HttpRequestMessage, HttpResponseMessage>? preflightResponder = null,
        Func<HttpRequestMessage, HttpResponseMessage>? refundCreateResponder = null)
    {
        var orderLookup = ownershipResponder is null
            ? ThrowingOrderLookupService(scopedProvider)
            : NewOrderLookupService(scopedProvider, ownershipResponder);
        var refundLookup = preflightResponder is null
            ? ThrowingRefundLookupService(scopedProvider)
            : NewRefundLookupService(scopedProvider, preflightResponder);
        var refundExecutor = refundCreateResponder is null
            ? ThrowingRefundExecutor(scopedProvider)
            : NewRefundExecutor(scopedProvider, refundCreateResponder);

        return await Task.FromResult(new RefundApprovedActionExecutor(
            scopedProvider.GetRequiredService<AppDbContext>(),
            scopedProvider.GetRequiredService<IAuditLogService>(),
            scopedProvider.GetRequiredService<ILevel2VerificationChecker>(),
            orderLookup,
            scopedProvider.GetRequiredService<IOrderOwnershipAuthorizationService>(),
            refundLookup,
            scopedProvider.GetRequiredService<IRefundService>(),
            refundExecutor));
    }

    private async Task<List<AuditLog>> ReadAuditLogsAsync(Guid businessId, Guid entityId, string entityType)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.AuditLogs
            .IgnoreQueryFilters()
            .Where(a => a.BusinessId == businessId && a.EntityType == entityType && a.EntityId == entityId.ToString())
            .ToListAsync();
    }

    // ── Owner Decision 3, Layer 2 — kill switch ──────────────────────────

    [Fact]
    public async Task Execute_RefundToolDisabled_Refuses_NoShopifyCall_NoRefundCreated_NoAudit()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tokenProtector = scope.ServiceProvider.GetRequiredService<IIntegrationTokenProtector>();
        var (business, approval, _) = await SeedApprovalAsync(db, tokenProtector, refundToolEnabled: false);
        var staffUserId = await CreateStaffUserAsync(db, business.Id);
        ((MutableTenantAccessor)scope.ServiceProvider.GetRequiredService<ICurrentTenantAccessor>()).CurrentBusinessId = business.Id;

        var executor = await BuildExecutorAsync(scope.ServiceProvider); // every Shopify dependency throws if reached

        var outcome = await executor.ExecuteApprovedActionAsync(approval.Id, staffUserId, CancellationToken.None);

        var refused = Assert.IsType<ExecutionOutcome.Refused>(outcome);
        Assert.Equal(ExecutionRefusalReason.RefundToolDisabled, refused.Reason);
        Assert.Empty(await ReadAuditLogsAsync(business.Id, approval.Id, AuditEntityTypes.Approval));
        Assert.Empty(await db.Refunds.IgnoreQueryFilters().Where(r => r.BusinessId == business.Id).ToListAsync());

        var reloaded = await db.Approvals.IgnoreQueryFilters().AsNoTracking().SingleAsync(a => a.Id == approval.Id);
        Assert.Equal(ApprovalStatus.Approved, reloaded.Status); // untouched — retriable once re-enabled
        Assert.Null(reloaded.RefundId);
    }

    [Fact]
    public async Task Execute_ApprovedWhileEnabled_ThenDisabledBeforeExecution_StillRefuses()
    {
        // Proves human approval does not bypass the execution-time kill
        // switch (design §14.3) — the Approval itself was validly approved;
        // only the Business row's own flag changed afterward.
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tokenProtector = scope.ServiceProvider.GetRequiredService<IIntegrationTokenProtector>();
        var (business, approval, _) = await SeedApprovalAsync(db, tokenProtector, refundToolEnabled: true);
        var staffUserId = await CreateStaffUserAsync(db, business.Id);
        ((MutableTenantAccessor)scope.ServiceProvider.GetRequiredService<ICurrentTenantAccessor>()).CurrentBusinessId = business.Id;

        // Business disables the tool after the Approval was already approved.
        await db.Businesses.Where(b => b.Id == business.Id).ExecuteUpdateAsync(s => s.SetProperty(b => b.RefundToolEnabled, false));

        var executor = await BuildExecutorAsync(scope.ServiceProvider);
        var outcome = await executor.ExecuteApprovedActionAsync(approval.Id, staffUserId, CancellationToken.None);

        var refused = Assert.IsType<ExecutionOutcome.Refused>(outcome);
        Assert.Equal(ExecutionRefusalReason.RefundToolDisabled, refused.Reason);
    }

    // ── Approval state ───────────────────────────────────────────────────

    [Fact]
    public async Task Execute_PendingApproval_Refuses_ApprovalNotApproved_WithAudit()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tokenProtector = scope.ServiceProvider.GetRequiredService<IIntegrationTokenProtector>();
        var (business, approval, _) = await SeedApprovalAsync(db, tokenProtector, status: ApprovalStatus.Pending);
        var staffUserId = await CreateStaffUserAsync(db, business.Id);
        ((MutableTenantAccessor)scope.ServiceProvider.GetRequiredService<ICurrentTenantAccessor>()).CurrentBusinessId = business.Id;

        var executor = await BuildExecutorAsync(scope.ServiceProvider);
        var outcome = await executor.ExecuteApprovedActionAsync(approval.Id, staffUserId, CancellationToken.None);

        var refused = Assert.IsType<ExecutionOutcome.Refused>(outcome);
        Assert.Equal(ExecutionRefusalReason.ApprovalNotApproved, refused.Reason);
        var logs = await ReadAuditLogsAsync(business.Id, approval.Id, AuditEntityTypes.Approval);
        Assert.Single(logs);
        Assert.Equal(AuditEventTypes.UnauthorizedActionBlocked, logs[0].EventType);
    }

    [Fact]
    public async Task Execute_DeniedApproval_Refuses_ApprovalNotApproved()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tokenProtector = scope.ServiceProvider.GetRequiredService<IIntegrationTokenProtector>();
        var (business, approval, _) = await SeedApprovalAsync(db, tokenProtector, status: ApprovalStatus.Denied);
        var staffUserId = await CreateStaffUserAsync(db, business.Id);
        ((MutableTenantAccessor)scope.ServiceProvider.GetRequiredService<ICurrentTenantAccessor>()).CurrentBusinessId = business.Id;

        var executor = await BuildExecutorAsync(scope.ServiceProvider);
        var outcome = await executor.ExecuteApprovedActionAsync(approval.Id, staffUserId, CancellationToken.None);

        var refused = Assert.IsType<ExecutionOutcome.Refused>(outcome);
        Assert.Equal(ExecutionRefusalReason.ApprovalNotApproved, refused.Reason);
    }

    [Fact]
    public async Task Execute_ExpiredApproval_Refuses_ApprovalExpired_WithAudit()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tokenProtector = scope.ServiceProvider.GetRequiredService<IIntegrationTokenProtector>();
        var (business, approval, _) = await SeedApprovalAsync(
            db, tokenProtector, expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));
        var staffUserId = await CreateStaffUserAsync(db, business.Id);
        ((MutableTenantAccessor)scope.ServiceProvider.GetRequiredService<ICurrentTenantAccessor>()).CurrentBusinessId = business.Id;

        var executor = await BuildExecutorAsync(scope.ServiceProvider);
        var outcome = await executor.ExecuteApprovedActionAsync(approval.Id, staffUserId, CancellationToken.None);

        var refused = Assert.IsType<ExecutionOutcome.Refused>(outcome);
        Assert.Equal(ExecutionRefusalReason.ApprovalExpired, refused.Reason);
        var logs = await ReadAuditLogsAsync(business.Id, approval.Id, AuditEntityTypes.Approval);
        Assert.Single(logs);
    }

    [Fact]
    public async Task Execute_UnknownApprovalId_Refuses_ApprovalNotFound()
    {
        await using var scope = _provider.CreateAsyncScope();
        var executor = await BuildExecutorAsync(scope.ServiceProvider);

        var outcome = await executor.ExecuteApprovedActionAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        var refused = Assert.IsType<ExecutionOutcome.Refused>(outcome);
        Assert.Equal(ExecutionRefusalReason.ApprovalNotFound, refused.Reason);
    }

    // ── Tenant isolation ──────────────────────────────────────────────────

    [Fact]
    public async Task Execute_CrossTenantAmbientAccessor_Refuses_ApprovalNotFound_NoShopifyCall()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tokenProtector = scope.ServiceProvider.GetRequiredService<IIntegrationTokenProtector>();
        var (business, approval, _) = await SeedApprovalAsync(db, tokenProtector);
        var staffUserId = await CreateStaffUserAsync(db, business.Id);

        // A DIFFERENT tenant is ambient — the global query filter folds this
        // into ApprovalNotFound, identical to cancelOrder's own guarantee.
        ((MutableTenantAccessor)scope.ServiceProvider.GetRequiredService<ICurrentTenantAccessor>()).CurrentBusinessId = Guid.NewGuid();

        var executor = await BuildExecutorAsync(scope.ServiceProvider);
        var outcome = await executor.ExecuteApprovedActionAsync(approval.Id, staffUserId, CancellationToken.None);

        var refused = Assert.IsType<ExecutionOutcome.Refused>(outcome);
        Assert.Equal(ExecutionRefusalReason.ApprovalNotFound, refused.Reason);
    }

    // ── Ownership ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Execute_OwnershipMismatch_Refuses_WithAudit_NoRefundCreated()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tokenProtector = scope.ServiceProvider.GetRequiredService<IIntegrationTokenProtector>();
        var (business, approval, _) = await SeedApprovalAsync(db, tokenProtector, customerExternalId: "gid://shopify/Customer/999");
        var staffUserId = await CreateStaffUserAsync(db, business.Id);
        ((MutableTenantAccessor)scope.ServiceProvider.GetRequiredService<ICurrentTenantAccessor>()).CurrentBusinessId = business.Id;

        // Shopify reports a DIFFERENT owner than the resolved customer's own ExternalId.
        var executor = await BuildExecutorAsync(
            scope.ServiceProvider,
            ownershipResponder: _ => JsonResponse(OrderOwnerResponse(OrderId, "gid://shopify/Customer/other")));

        var outcome = await executor.ExecuteApprovedActionAsync(approval.Id, staffUserId, CancellationToken.None);

        var refused = Assert.IsType<ExecutionOutcome.Refused>(outcome);
        Assert.Equal(ExecutionRefusalReason.OwnershipVerificationFailed, refused.Reason);
        var logs = await ReadAuditLogsAsync(business.Id, approval.Id, AuditEntityTypes.Approval);
        Assert.Single(logs);
        Assert.Empty(await db.Refunds.IgnoreQueryFilters().Where(r => r.BusinessId == business.Id).ToListAsync());
    }

    // ── Level2 ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Execute_Level2NotVerified_Refuses_WithAudit_NoShopifyCall()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tokenProtector = scope.ServiceProvider.GetRequiredService<IIntegrationTokenProtector>();
        var (business, approval, _) = await SeedApprovalAsync(db, tokenProtector, level2Verified: false);
        var staffUserId = await CreateStaffUserAsync(db, business.Id);
        ((MutableTenantAccessor)scope.ServiceProvider.GetRequiredService<ICurrentTenantAccessor>()).CurrentBusinessId = business.Id;

        var executor = await BuildExecutorAsync(scope.ServiceProvider); // ownership must never be reached

        var outcome = await executor.ExecuteApprovedActionAsync(approval.Id, staffUserId, CancellationToken.None);

        var refused = Assert.IsType<ExecutionOutcome.Refused>(outcome);
        Assert.Equal(ExecutionRefusalReason.Level2VerificationFailed, refused.Reason);
        var logs = await ReadAuditLogsAsync(business.Id, approval.Id, AuditEntityTypes.Approval);
        Assert.Single(logs);
    }

    // ── Active Shopify integration ────────────────────────────────────────

    [Fact]
    public async Task Execute_NoActiveIntegration_Refuses_ShopifyIntegrationUnavailable_NoAudit()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tokenProtector = scope.ServiceProvider.GetRequiredService<IIntegrationTokenProtector>();
        var (business, approval, _) = await SeedApprovalAsync(db, tokenProtector, withIntegration: false);
        var staffUserId = await CreateStaffUserAsync(db, business.Id);
        ((MutableTenantAccessor)scope.ServiceProvider.GetRequiredService<ICurrentTenantAccessor>()).CurrentBusinessId = business.Id;

        var executor = await BuildExecutorAsync(scope.ServiceProvider);
        var outcome = await executor.ExecuteApprovedActionAsync(approval.Id, staffUserId, CancellationToken.None);

        var refused = Assert.IsType<ExecutionOutcome.Refused>(outcome);
        Assert.Equal(ExecutionRefusalReason.ShopifyIntegrationUnavailable, refused.Reason);
        Assert.Empty(await ReadAuditLogsAsync(business.Id, approval.Id, AuditEntityTypes.Approval));
    }

    // ── Refundable-quantity preflight (design §8.3/§17) ──────────────────

    [Fact]
    public async Task Execute_InsufficientRefundableQuantity_Refuses_NoRefundCreated_NoAudit()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tokenProtector = scope.ServiceProvider.GetRequiredService<IIntegrationTokenProtector>();
        var (business, approval, _) = await SeedApprovalAsync(db, tokenProtector, quantity: 5);
        var staffUserId = await CreateStaffUserAsync(db, business.Id);
        ((MutableTenantAccessor)scope.ServiceProvider.GetRequiredService<ICurrentTenantAccessor>()).CurrentBusinessId = business.Id;

        var executor = await BuildExecutorAsync(
            scope.ServiceProvider,
            ownershipResponder: _ => JsonResponse(OrderOwnerResponse(OrderId, "gid://shopify/Customer/999")),
            preflightResponder: _ => JsonResponse(PreflightResponse(refundableQuantity: 2))); // requested 5, only 2 refundable

        var outcome = await executor.ExecuteApprovedActionAsync(approval.Id, staffUserId, CancellationToken.None);

        var refused = Assert.IsType<ExecutionOutcome.Refused>(outcome);
        Assert.Equal(ExecutionRefusalReason.InsufficientRefundableQuantity, refused.Reason);
        Assert.Empty(await ReadAuditLogsAsync(business.Id, approval.Id, AuditEntityTypes.Approval));
        Assert.Empty(await db.Refunds.IgnoreQueryFilters().Where(r => r.BusinessId == business.Id).ToListAsync());
        var reloaded = await db.Approvals.IgnoreQueryFilters().AsNoTracking().SingleAsync(a => a.Id == approval.Id);
        Assert.Equal(ApprovalStatus.Approved, reloaded.Status); // never claimed
    }

    [Fact]
    public async Task Execute_LineItemNotOnOrder_Refuses_RefundLineItemNotOnOrder()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tokenProtector = scope.ServiceProvider.GetRequiredService<IIntegrationTokenProtector>();
        var (business, approval, _) = await SeedApprovalAsync(db, tokenProtector);
        var staffUserId = await CreateStaffUserAsync(db, business.Id);
        ((MutableTenantAccessor)scope.ServiceProvider.GetRequiredService<ICurrentTenantAccessor>()).CurrentBusinessId = business.Id;

        const string differentLineItemResponse =
            """
            { "data": { "order": { "id": "gid://shopify/Order/12345", "lineItems": { "edges": [
              { "node": { "id": "gid://shopify/LineItem/other", "refundableQuantity": 5 } }
            ] }, "suggestedRefund": { "refundLineItems": [
              { "subtotalSet": { "presentmentMoney": { "amount": "19.99", "currencyCode": "EUR" } } }
            ] } } } }
            """;

        var executor = await BuildExecutorAsync(
            scope.ServiceProvider,
            ownershipResponder: _ => JsonResponse(OrderOwnerResponse(OrderId, "gid://shopify/Customer/999")),
            preflightResponder: _ => JsonResponse(differentLineItemResponse));

        var outcome = await executor.ExecuteApprovedActionAsync(approval.Id, staffUserId, CancellationToken.None);

        var refused = Assert.IsType<ExecutionOutcome.Refused>(outcome);
        Assert.Equal(ExecutionRefusalReason.RefundLineItemNotOnOrder, refused.Reason);
    }

    // ── Full success / failure paths — monetary resolution, RefundId, audit ──

    [Fact]
    public async Task Execute_FullSuccess_UsesShopifyResolvedAmount_AssociatesRefundId_AuditsRefundIssued()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tokenProtector = scope.ServiceProvider.GetRequiredService<IIntegrationTokenProtector>();
        // Quantity 2, but the mocked suggestedRefund amount (19.99) is
        // deliberately NOT quantity(2) * any clean per-unit price — proving
        // the executor relays Shopify's own computed subtotal rather than
        // deriving quantity * per-unit price itself.
        var (business, approval, _) = await SeedApprovalAsync(db, tokenProtector, quantity: 2);
        var staffUserId = await CreateStaffUserAsync(db, business.Id);
        ((MutableTenantAccessor)scope.ServiceProvider.GetRequiredService<ICurrentTenantAccessor>()).CurrentBusinessId = business.Id;

        var executor = await BuildExecutorAsync(
            scope.ServiceProvider,
            ownershipResponder: _ => JsonResponse(OrderOwnerResponse(OrderId, "gid://shopify/Customer/999")),
            preflightResponder: _ => JsonResponse(PreflightResponse(refundableQuantity: 5, amount: "19.99", currency: "EUR")),
            refundCreateResponder: _ => JsonResponse(RefundCreateSuccessResponse));

        var outcome = await executor.ExecuteApprovedActionAsync(approval.Id, staffUserId, CancellationToken.None);

        var executed = Assert.IsType<ExecutionOutcome.Executed>(outcome);
        Assert.Equal(ExecutionResultStatus.Confirmed, executed.Status);

        var refund = await db.Refunds.IgnoreQueryFilters().AsNoTracking().SingleAsync(r => r.Id == executed.ExecutionIntentId);
        Assert.Equal(RefundStatus.Confirmed, refund.Status);
        Assert.Equal(39.98m, refund.TotalAmount); // Shopify's own refundCreate response total
        Assert.Equal("EUR", refund.Currency);
        Assert.Equal("gid://shopify/Refund/1", refund.ExternalRefundId);

        var refundItem = await db.RefundItems.IgnoreQueryFilters().AsNoTracking().SingleAsync(ri => ri.RefundId == refund.Id);
        Assert.Equal(19.99m, refundItem.Amount); // Shopify's own suggestedRefund subtotal, not 2 * some unit price
        Assert.Equal(2, refundItem.Quantity);

        var reloadedApproval = await db.Approvals.IgnoreQueryFilters().AsNoTracking().SingleAsync(a => a.Id == approval.Id);
        Assert.Equal(ApprovalStatus.Consumed, reloadedApproval.Status);
        Assert.Equal(refund.Id, reloadedApproval.RefundId);

        var approvalLogs = await ReadAuditLogsAsync(business.Id, approval.Id, AuditEntityTypes.Approval);
        Assert.Single(approvalLogs);
        Assert.Equal(AuditEventTypes.ExecutionClaimed, approvalLogs[0].EventType);

        var refundLogs = await ReadAuditLogsAsync(business.Id, refund.Id, AuditEntityTypes.Refund);
        Assert.Single(refundLogs);
        Assert.Equal(AuditEventTypes.RefundIssued, refundLogs[0].EventType);
    }

    [Fact]
    public async Task Execute_ShopifyRejectsMutation_ResultsInFailed_NotConfirmed_AuditsRefundFailed()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tokenProtector = scope.ServiceProvider.GetRequiredService<IIntegrationTokenProtector>();
        var (business, approval, _) = await SeedApprovalAsync(db, tokenProtector);
        var staffUserId = await CreateStaffUserAsync(db, business.Id);
        ((MutableTenantAccessor)scope.ServiceProvider.GetRequiredService<ICurrentTenantAccessor>()).CurrentBusinessId = business.Id;

        const string rejection =
            """{ "data": { "refundCreate": { "refund": null, "userErrors": [ { "field": ["refundLineItems"], "message": "Cannot refund more than purchased", "code": "OVER_REFUND" } ] } } }""";

        var executor = await BuildExecutorAsync(
            scope.ServiceProvider,
            ownershipResponder: _ => JsonResponse(OrderOwnerResponse(OrderId, "gid://shopify/Customer/999")),
            preflightResponder: _ => JsonResponse(PreflightResponse(refundableQuantity: 5)),
            refundCreateResponder: _ => JsonResponse(rejection));

        var outcome = await executor.ExecuteApprovedActionAsync(approval.Id, staffUserId, CancellationToken.None);

        var executed = Assert.IsType<ExecutionOutcome.Executed>(outcome);
        Assert.Equal(ExecutionResultStatus.Failed, executed.Status);
        Assert.Contains("Cannot refund more than purchased", executed.Detail);

        var refund = await db.Refunds.IgnoreQueryFilters().AsNoTracking().SingleAsync(r => r.Id == executed.ExecutionIntentId);
        Assert.Equal(RefundStatus.Failed, refund.Status);
        Assert.Null(refund.TotalAmount);
        Assert.Null(refund.ExternalRefundId);

        var reloadedApproval = await db.Approvals.IgnoreQueryFilters().AsNoTracking().SingleAsync(a => a.Id == approval.Id);
        Assert.Equal(ApprovalStatus.Consumed, reloadedApproval.Status); // claimed — the Approval itself is used up regardless of outcome
        Assert.Equal(refund.Id, reloadedApproval.RefundId);

        var refundLogs = await ReadAuditLogsAsync(business.Id, refund.Id, AuditEntityTypes.Refund);
        Assert.Single(refundLogs);
        Assert.Equal(AuditEventTypes.RefundFailed, refundLogs[0].EventType);
    }

    // ── Step Shopify-auth-reauth — auth-classified mutation failure ──────

    /// <summary>
    /// Step Shopify-auth-reauth (docs/handoffs/step-shopify-auth-reauth-design.md
    /// §7.4/§10) — an HTTP 401 at the refundCreate mutation boundary must
    /// transition Refund to Failed with an auth-marked FailureReason (never
    /// an uncaught exception/bare 500), set Integration.ReauthorizationRequiredAt,
    /// leave Approval Consumed, and write both the existing RefundFailed
    /// audit event (now carrying FailureReason) and the new
    /// IntegrationReauthorizationRequired event.
    /// </summary>
    [Fact]
    public async Task Execute_ShopifyReturns401_ResultsInFailed_WithAuthReason_SetsReauthorizationRequired_AuditsIntegration()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tokenProtector = scope.ServiceProvider.GetRequiredService<IIntegrationTokenProtector>();
        var (business, approval, _) = await SeedApprovalAsync(db, tokenProtector);
        var staffUserId = await CreateStaffUserAsync(db, business.Id);
        ((MutableTenantAccessor)scope.ServiceProvider.GetRequiredService<ICurrentTenantAccessor>()).CurrentBusinessId = business.Id;

        var executor = await BuildExecutorAsync(
            scope.ServiceProvider,
            ownershipResponder: _ => JsonResponse(OrderOwnerResponse(OrderId, "gid://shopify/Customer/999")),
            preflightResponder: _ => JsonResponse(PreflightResponse(refundableQuantity: 5)),
            refundCreateResponder: _ => new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized));

        var outcome = await executor.ExecuteApprovedActionAsync(approval.Id, staffUserId, CancellationToken.None);

        var executed = Assert.IsType<ExecutionOutcome.Executed>(outcome);
        Assert.Equal(ExecutionResultStatus.Failed, executed.Status);
        Assert.StartsWith(ShopifyFailureReasons.ReauthorizationRequiredPrefix, executed.Detail);
        Assert.Contains("401", executed.Detail);

        var refund = await db.Refunds.IgnoreQueryFilters().AsNoTracking().SingleAsync(r => r.Id == executed.ExecutionIntentId);
        Assert.Equal(RefundStatus.Failed, refund.Status);
        Assert.StartsWith(ShopifyFailureReasons.ReauthorizationRequiredPrefix, refund.FailureReason);
        Assert.Null(refund.TotalAmount);
        Assert.Null(refund.ExternalRefundId);

        var reloadedApproval = await db.Approvals.IgnoreQueryFilters().AsNoTracking().SingleAsync(a => a.Id == approval.Id);
        Assert.Equal(ApprovalStatus.Consumed, reloadedApproval.Status);
        Assert.Equal(refund.Id, reloadedApproval.RefundId);

        var integration = await db.Integrations.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(i => i.BusinessId == business.Id && i.Platform == ShopifyIntegrationOAuthProvider.PlatformName);
        Assert.NotNull(integration.ReauthorizationRequiredAt);
        Assert.True(integration.IsActive); // Decision 1/2 — auth failure never deactivates the integration

        var refundLogs = await ReadAuditLogsAsync(business.Id, refund.Id, AuditEntityTypes.Refund);
        var refundLog = Assert.Single(refundLogs);
        Assert.Equal(AuditEventTypes.RefundFailed, refundLog.EventType);
        Assert.Contains(ShopifyFailureReasons.ReauthorizationRequiredPrefix, refundLog.NewValue);

        var integrationLogs = await ReadAuditLogsAsync(business.Id, integration.Id, AuditEntityTypes.Integration);
        var integrationLog = Assert.Single(integrationLogs);
        Assert.Equal(AuditEventTypes.IntegrationReauthorizationRequired, integrationLog.EventType);
        Assert.Equal(staffUserId, integrationLog.ChangedByUserId);
    }

    /// <summary>Mirrors the 401 test above for HTTP 403 (Decision 7 item 2).</summary>
    [Fact]
    public async Task Execute_ShopifyReturns403_ResultsInFailed_WithAuthReason_SetsReauthorizationRequired()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tokenProtector = scope.ServiceProvider.GetRequiredService<IIntegrationTokenProtector>();
        var (business, approval, _) = await SeedApprovalAsync(db, tokenProtector);
        var staffUserId = await CreateStaffUserAsync(db, business.Id);
        ((MutableTenantAccessor)scope.ServiceProvider.GetRequiredService<ICurrentTenantAccessor>()).CurrentBusinessId = business.Id;

        var executor = await BuildExecutorAsync(
            scope.ServiceProvider,
            ownershipResponder: _ => JsonResponse(OrderOwnerResponse(OrderId, "gid://shopify/Customer/999")),
            preflightResponder: _ => JsonResponse(PreflightResponse(refundableQuantity: 5)),
            refundCreateResponder: _ => new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden));

        var outcome = await executor.ExecuteApprovedActionAsync(approval.Id, staffUserId, CancellationToken.None);

        var executed = Assert.IsType<ExecutionOutcome.Executed>(outcome);
        Assert.Equal(ExecutionResultStatus.Failed, executed.Status);
        Assert.StartsWith(ShopifyFailureReasons.ReauthorizationRequiredPrefix, executed.Detail);
        Assert.Contains("403", executed.Detail);

        var integration = await db.Integrations.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(i => i.BusinessId == business.Id && i.Platform == ShopifyIntegrationOAuthProvider.PlatformName);
        Assert.NotNull(integration.ReauthorizationRequiredAt);
    }

    // Token-decrypt-failure classification (Decision 7 item 3) is not
    // exercised end-to-end at this Approval level: doing so would require
    // corrupting the Integration's EncryptedAccessToken between the
    // ownership-lookup step (step 10) and the refundCreate mutation, but
    // both steps share the same scoped AppDbContext/EF identity map within
    // one ExecuteApprovedActionAsync call — a genuinely corrupted token
    // would already fail the earlier ownership lookup (an uncaught,
    // pre-claim exception, not this design's concern) rather than
    // selectively fail only at the mutation boundary, so there is no
    // realistic single-request scenario this level could construct without
    // an artificial setup that misrepresents production behavior. The
    // classification itself is independently, directly verified at the
    // ShopifyRefundExecutor unit level (ExecuteAsync_TokenDecryptFailure_...),
    // isolated from this confound; the audit/reauthorization-state wiring
    // this Approval level is responsible for is already proven correct by
    // the HTTP 401/403 tests above, which exercise the identical
    // MarkAuthFailureAsync path token-decrypt failure would also reach.

    /// <summary>
    /// Negative regression: an ordinary Shopify userErrors rejection must
    /// never be misclassified as an auth failure — no
    /// ReauthorizationRequiredAt write, no IntegrationReauthorizationRequired
    /// audit event, and the FailureReason must not carry the auth marker.
    /// </summary>
    [Fact]
    public async Task Execute_ShopifyRejectsMutation_DoesNotSetReauthorizationRequired_DoesNotWriteIntegrationAudit()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tokenProtector = scope.ServiceProvider.GetRequiredService<IIntegrationTokenProtector>();
        var (business, approval, _) = await SeedApprovalAsync(db, tokenProtector);
        var staffUserId = await CreateStaffUserAsync(db, business.Id);
        ((MutableTenantAccessor)scope.ServiceProvider.GetRequiredService<ICurrentTenantAccessor>()).CurrentBusinessId = business.Id;

        const string rejection =
            """{ "data": { "refundCreate": { "refund": null, "userErrors": [ { "field": ["refundLineItems"], "message": "Cannot refund more than purchased", "code": "OVER_REFUND" } ] } } }""";

        var executor = await BuildExecutorAsync(
            scope.ServiceProvider,
            ownershipResponder: _ => JsonResponse(OrderOwnerResponse(OrderId, "gid://shopify/Customer/999")),
            preflightResponder: _ => JsonResponse(PreflightResponse(refundableQuantity: 5)),
            refundCreateResponder: _ => JsonResponse(rejection));

        var outcome = await executor.ExecuteApprovedActionAsync(approval.Id, staffUserId, CancellationToken.None);

        var executed = Assert.IsType<ExecutionOutcome.Executed>(outcome);
        Assert.Equal(ExecutionResultStatus.Failed, executed.Status);
        Assert.DoesNotContain(ShopifyFailureReasons.ReauthorizationRequiredPrefix, executed.Detail);

        var integration = await db.Integrations.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(i => i.BusinessId == business.Id && i.Platform == ShopifyIntegrationOAuthProvider.PlatformName);
        Assert.Null(integration.ReauthorizationRequiredAt);

        var integrationLogs = await ReadAuditLogsAsync(business.Id, integration.Id, AuditEntityTypes.Integration);
        Assert.Empty(integrationLogs);
    }

    private class MutableTenantAccessor : ICurrentTenantAccessor
    {
        public Guid? CurrentBusinessId { get; set; }
        public Guid? BusinessId => CurrentBusinessId;
    }
}
