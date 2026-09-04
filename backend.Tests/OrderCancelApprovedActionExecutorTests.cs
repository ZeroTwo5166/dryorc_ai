using backend.Constants;
using backend.Data;
using backend.Enums;
using backend.Interfaces;
using backend.Models;
using backend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace backend.Tests;

/// <summary>
/// Service-level tests for OrderCancelApprovedActionExecutor, run against
/// the real Neon dev DB via a bare ServiceProvider, same pattern as
/// OrderCancelProposalToolTests. Covers steps 3-9 of the execution ordering
/// (docs/handoffs/step-22z-approval-proposal-approval-lifecycle-design.md
/// §10): the Approval load, ApprovalNotFound refusal (including the
/// tenant-fold-in property independently confirmed by
/// docs/handoffs/step-24/25/26), the Status/ExpiresAt/CustomerId checks
/// added by docs/handoffs/step-38-cancelorder-execute-next-slice-design.md
/// §6 (steps 6/7/8, with an AuditLog write required for steps 6/7 but
/// deliberately not for step 8), the Level 2 verification check added by
/// docs/handoffs/step-39-cancelorder-execute-steps-9-16-next-slice-design.md
/// §7 (step 9, with an AuditLog write per the explicit owner decision
/// recorded in that document's §5a), plus one boundary test documenting
/// that everything past step 9 still throws NotImplementedException.
///
/// Uses a MutableTenantAccessor (same shape as AppDbContextTenantFilterTests'
/// own private nested class — not shared, per this codebase's established
/// per-test-file convention), not a FixedTenantAccessor, because these
/// tests specifically need to move the ambient tenant between a real,
/// non-null value and a *different* real, non-null value within the same
/// test — the exact scenario the executor's cross-tenant guarantee is
/// about.
/// </summary>
public class OrderCancelApprovedActionExecutorTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;
    private readonly List<Guid> _businessIdsToClean = [];

    public Task InitializeAsync()
    {
        var configuration = new ConfigurationBuilder().AddUserSecrets<Program>().Build();
        var connectionUri = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection not found via user-secrets.");

        _provider = new ServiceCollection()
            .AddSingleton<ICurrentTenantAccessor>(new MutableTenantAccessor())
            .AddDbContext<AppDbContext>(o => o.UseNpgsql(
                PostgresConnectionString.FromUri(connectionUri), x => x.UseVector()))
            .AddScoped<IAuditLogService, AuditLogService>()
            .AddScoped<ILevel2VerificationChecker, Level2VerificationChecker>()
            .BuildServiceProvider();

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
            // its Conversation is. Customers/Widgets before Businesses.
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"AuditLogs\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Conversations\" WHERE \"BusinessId\" = ANY({ids})");
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
    /// </summary>
    private async Task<(Business Business, Approval Approval)> SeedApprovalAsync(
        AppDbContext db,
        ApprovalStatus status = ApprovalStatus.Approved,
        DateTimeOffset? expiresAt = null,
        bool customerResolved = true,
        bool level2Verified = true)
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
                business.Id, conversation.Id, customer.Id, "cancelOrder", "gid://shopify/Order/12345", canonicalArguments),
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

        return (business, approval);
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

    [Fact]
    public async Task ExecuteApprovedActionAsync_ApprovalBelongsToDifferentTenant_ReturnsApprovalNotFound()
    {
        var tenant = (MutableTenantAccessor)_provider.GetRequiredService<ICurrentTenantAccessor>();

        await using var seedScope = _provider.CreateAsyncScope();
        var seedDb = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (owningBusiness, approval) = await SeedApprovalAsync(seedDb);

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
        var executor = new OrderCancelApprovedActionExecutor(readDb, auditLogService, level2Checker);

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
        var (business, _) = await SeedApprovalAsync(seedDb);

        tenant.CurrentBusinessId = business.Id;
        await using var readScope = _provider.CreateAsyncScope();
        var readDb = readScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var auditLogService = readScope.ServiceProvider.GetRequiredService<IAuditLogService>();
        var level2Checker = readScope.ServiceProvider.GetRequiredService<ILevel2VerificationChecker>();
        var executor = new OrderCancelApprovedActionExecutor(readDb, auditLogService, level2Checker);

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
        var (business, approval) = await SeedApprovalAsync(seedDb, status: status);

        tenant.CurrentBusinessId = business.Id;
        await using var readScope = _provider.CreateAsyncScope();
        var readDb = readScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var auditLogService = readScope.ServiceProvider.GetRequiredService<IAuditLogService>();
        var level2Checker = readScope.ServiceProvider.GetRequiredService<ILevel2VerificationChecker>();
        var executor = new OrderCancelApprovedActionExecutor(readDb, auditLogService, level2Checker);

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
        var (business, approval) = await SeedApprovalAsync(
            seedDb, expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));

        tenant.CurrentBusinessId = business.Id;
        await using var readScope = _provider.CreateAsyncScope();
        var readDb = readScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var auditLogService = readScope.ServiceProvider.GetRequiredService<IAuditLogService>();
        var level2Checker = readScope.ServiceProvider.GetRequiredService<ILevel2VerificationChecker>();
        var executor = new OrderCancelApprovedActionExecutor(readDb, auditLogService, level2Checker);

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
        var (business, approval) = await SeedApprovalAsync(seedDb, customerResolved: false);

        tenant.CurrentBusinessId = business.Id;
        await using var readScope = _provider.CreateAsyncScope();
        var readDb = readScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var auditLogService = readScope.ServiceProvider.GetRequiredService<IAuditLogService>();
        var level2Checker = readScope.ServiceProvider.GetRequiredService<ILevel2VerificationChecker>();
        var executor = new OrderCancelApprovedActionExecutor(readDb, auditLogService, level2Checker);

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
        var (business, approval) = await SeedApprovalAsync(seedDb, level2Verified: false);

        tenant.CurrentBusinessId = business.Id;
        await using var readScope = _provider.CreateAsyncScope();
        var readDb = readScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var auditLogService = readScope.ServiceProvider.GetRequiredService<IAuditLogService>();
        var level2Checker = readScope.ServiceProvider.GetRequiredService<ILevel2VerificationChecker>();
        var executor = new OrderCancelApprovedActionExecutor(readDb, auditLogService, level2Checker);

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
    /// Deliberate scope-boundary test, same convention as
    /// OrderCancelProposalToolTests' own explicit-refusal tests: documents,
    /// rather than merely leaves implicit, exactly where this session's
    /// implementation stops. Must be updated (not deleted) by whichever
    /// future session extends ExecuteApprovedActionAsync past step 9 — a
    /// passing NotImplementedException here after that point would mean the
    /// new step's logic was never reached. Also proves the adjacent-case
    /// negative: a valid Approved+unexpired+resolved-customer+Level2-verified
    /// Approval does NOT trigger any of steps 6/7/8/9's refusals, and writes
    /// NO AuditLog row — steps 6/7/9's writes are conditional on their own
    /// refusal firing, not unconditional on every ExecuteApprovedActionAsync
    /// call.
    /// </summary>
    [Fact]
    public async Task ExecuteApprovedActionAsync_ApprovalExistsForCurrentTenant_ThrowsNotImplementedAndWritesNoAuditLog()
    {
        var tenant = (MutableTenantAccessor)_provider.GetRequiredService<ICurrentTenantAccessor>();

        await using var seedScope = _provider.CreateAsyncScope();
        var seedDb = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (business, approval) = await SeedApprovalAsync(seedDb);

        tenant.CurrentBusinessId = business.Id;
        await using var readScope = _provider.CreateAsyncScope();
        var readDb = readScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var auditLogService = readScope.ServiceProvider.GetRequiredService<IAuditLogService>();
        var level2Checker = readScope.ServiceProvider.GetRequiredService<ILevel2VerificationChecker>();
        var executor = new OrderCancelApprovedActionExecutor(readDb, auditLogService, level2Checker);

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
