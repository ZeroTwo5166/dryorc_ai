using backend.Data;
using backend.Enums;
using backend.Interfaces;
using backend.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace backend.Tests;

/// <summary>
/// Step refundOrder Phase 1 — confirms the new Approval.RefundId
/// relationship (docs/handoffs/step-refundorder-design.md §12.3) persists
/// correctly and that the pre-existing Approval.OrderCancellationId
/// relationship remains valid and unaffected. Persistence-only — no
/// RefundApprovedActionExecutor exists yet (a later phase), so this does
/// not exercise any execution/approval-state-machine behavior, only the EF
/// mapping itself.
/// </summary>
public class ApprovalRefundRelationshipTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;
    private readonly List<Guid> _businessIdsToClean = [];

    public Task InitializeAsync()
    {
        var configuration = new ConfigurationBuilder()
            .AddUserSecrets<Program>()
            .Build();

        var connectionUri = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection not found via user-secrets.");

        _provider = new ServiceCollection()
            .AddSingleton<ICurrentTenantAccessor>(new FixedTenantAccessor(null))
            .AddDbContext<AppDbContext>(o => o.UseNpgsql(
                PostgresConnectionString.FromUri(connectionUri), x => x.UseVector()))
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

            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Approvals\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Refunds\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"OrderCancellations\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Conversations\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Widgets\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Customers\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Businesses\" WHERE \"Id\" = ANY({ids})");
        }

        await _provider.DisposeAsync();
    }

    /// <summary>Minimal Widget/Customer/Conversation graph an Approval's
    /// required, non-nullable FKs demand — same minimal shape as
    /// ApprovalsApiTests.SeedApprovalAsync.</summary>
    private async Task<(Guid BusinessId, Guid CustomerId, Guid ConversationId)> SeedGraphAsync(AppDbContext db)
    {
        var now = DateTimeOffset.UtcNow;
        var business = new Business
        {
            Id = Guid.NewGuid(), Name = $"Approval-Refund Test {Guid.NewGuid():N}",
            TimeZone = "Europe/Copenhagen", CreatedAt = now, UpdatedAt = now,
        };
        var widget = new Widget
        {
            Id = Guid.NewGuid(), BusinessId = business.Id, Name = "Test Widget",
            PublicKey = Guid.NewGuid().ToString("N"), CreatedAt = now, UpdatedAt = now,
        };
        var customer = new Customer
        {
            Id = Guid.NewGuid(), BusinessId = business.Id, Name = "Test Customer",
            Email = "test.customer@marker-test.invalid", CreatedAt = now, UpdatedAt = now,
        };
        var conversation = new Conversation
        {
            Id = Guid.NewGuid(), BusinessId = business.Id, WidgetId = widget.Id, CustomerId = customer.Id,
            VisitorSessionId = Guid.NewGuid().ToString("N"), Channel = "Website", Status = ConversationStatus.Open,
            CreatedAt = now, UpdatedAt = now,
        };

        db.Businesses.Add(business);
        db.Widgets.Add(widget);
        db.Customers.Add(customer);
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync();
        _businessIdsToClean.Add(business.Id);

        return (business.Id, customer.Id, conversation.Id);
    }

    private static Approval NewApproval(Guid businessId, Guid conversationId, Guid customerId, string toolName, string resourceId)
    {
        var now = DateTimeOffset.UtcNow;
        return new Approval
        {
            Id = Guid.NewGuid(), BusinessId = businessId, ConversationId = conversationId, CustomerId = customerId,
            ToolName = toolName, ResourceId = resourceId, CanonicalArguments = "placeholder",
            ActionHash = new string('a', 64), DisplaySummary = $"Test approval for {toolName}.",
            Status = ApprovalStatus.Pending, CreatedAt = now, ExpiresAt = now.AddMinutes(15),
        };
    }

    [Fact]
    public async Task Approval_RefundId_PersistsAndRoundTrips()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var (businessId, customerId, conversationId) = await SeedGraphAsync(db);

        var refund = new Refund
        {
            Id = Guid.NewGuid(), BusinessId = businessId, CustomerId = customerId,
            ExternalOrderId = "gid://shopify/Order/1", Method = RefundMethod.OriginalPaymentMethod,
            Currency = "EUR", IdempotencyKey = Guid.NewGuid().ToString("N"),
            Status = RefundStatus.Pending, RequestedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Refunds.Add(refund);

        var approval = NewApproval(businessId, conversationId, customerId, "refundOrder", "gid://shopify/Order/1");
        approval.RefundId = refund.Id;
        db.Approvals.Add(approval);

        await db.SaveChangesAsync();

        var reloaded = await db.Approvals.IgnoreQueryFilters().AsNoTracking().SingleAsync(a => a.Id == approval.Id);
        Assert.Equal(refund.Id, reloaded.RefundId);
        Assert.Null(reloaded.OrderCancellationId);
    }

    [Fact]
    public async Task Approval_OrderCancellationId_RemainsValid_Unaffected()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var (businessId, customerId, conversationId) = await SeedGraphAsync(db);

        var cancellation = new OrderCancellation
        {
            Id = Guid.NewGuid(), BusinessId = businessId, CustomerId = customerId,
            ExternalOrderId = "gid://shopify/Order/2", IdempotencyKey = Guid.NewGuid().ToString("N"),
            Status = OrderCancellationStatus.Pending, Reason = OrderCancelReason.Customer,
            RequestedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.OrderCancellations.Add(cancellation);

        var approval = NewApproval(businessId, conversationId, customerId, "cancelOrder", "gid://shopify/Order/2");
        approval.OrderCancellationId = cancellation.Id;
        db.Approvals.Add(approval);

        await db.SaveChangesAsync();

        // Existing cancelOrder relationship must remain exactly as it was —
        // unmodified, unweakened by the sibling RefundId column's addition.
        var reloaded = await db.Approvals.IgnoreQueryFilters().AsNoTracking().SingleAsync(a => a.Id == approval.Id);
        Assert.Equal(cancellation.Id, reloaded.OrderCancellationId);
        Assert.Null(reloaded.RefundId);
    }

    [Fact]
    public async Task Approval_BothIntentColumns_CanBothBeNull_ForAPendingUnclaimedApproval()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var (businessId, customerId, conversationId) = await SeedGraphAsync(db);

        var approval = NewApproval(businessId, conversationId, customerId, "refundOrder", "gid://shopify/Order/3");
        db.Approvals.Add(approval);
        await db.SaveChangesAsync();

        var reloaded = await db.Approvals.IgnoreQueryFilters().AsNoTracking().SingleAsync(a => a.Id == approval.Id);
        Assert.Null(reloaded.RefundId);
        Assert.Null(reloaded.OrderCancellationId);
    }

    private class FixedTenantAccessor(Guid? businessId) : ICurrentTenantAccessor
    {
        public Guid? BusinessId => businessId;
    }
}
