using System.Text.Json;
using backend.Data;
using backend.Enums;
using backend.Interfaces;
using backend.Models;
using backend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace backend.Tests;

/// <summary>
/// Service-level tests for OrderCancelProposalTool, run against the real
/// Neon dev DB via a bare ServiceProvider — same pattern as
/// ClaudeReplyServiceTests. Covers proposal creation only (Step 23N's own
/// scope) — no Level 2, no ownership, no Shopify call, no Execute.
/// </summary>
public class OrderCancelProposalToolTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;
    private readonly List<Guid> _businessIdsToClean = [];

    public Task InitializeAsync()
    {
        _provider = new ServiceCollection()
            .AddSingleton<ICurrentTenantAccessor>(new FixedTenantAccessor(null))
            .AddDbContext<AppDbContext>(o => o.UseNpgsql(
                TestDatabase.ConnectionString, x => x.UseVector()))
            .BuildServiceProvider();

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await TestBusinessCleanup.DeleteThenDisposeAsync(_businessIdsToClean, _provider);
    }

    private async Task<(Business Business, Customer? Customer, Conversation Conversation)> SeedAsync(AppDbContext db, bool resolveCustomer)
    {
        var business = new Business
        {
            Id = Guid.NewGuid(),
            Name = $"OrderCancelProposalTool Test {Guid.NewGuid():N}",
            TimeZone = "Europe/Copenhagen",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        var widget = new Widget
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id,
            Name = "Test Widget",
            PublicKey = Guid.NewGuid().ToString("N"),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        Customer? customer = null;
        if (resolveCustomer)
        {
            customer = new Customer
            {
                Id = Guid.NewGuid(),
                BusinessId = business.Id,
                Name = "Test Customer",
                Email = "test.customer@marker-test.invalid",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
        }
        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id,
            WidgetId = widget.Id,
            CustomerId = customer?.Id,
            VisitorSessionId = Guid.NewGuid().ToString("N"),
            Channel = "Website",
            Status = ConversationStatus.Open,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        db.Businesses.Add(business);
        db.Widgets.Add(widget);
        if (customer is not null)
        {
            db.Customers.Add(customer);
        }
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync();
        _businessIdsToClean.Add(business.Id);

        return (business, customer, conversation);
    }

    private static Dictionary<string, JsonElement> Args(object anonymous) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(anonymous))!;

    [Fact]
    public async Task ProposeAsync_ResolvedCustomer_CreatesPendingApproval_ReturnsApprovalId()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (business, customer, conversation) = await SeedAsync(db, resolveCustomer: true);

        var tool = new OrderCancelProposalTool(db);
        var context = new ToolDispatchContext(business.Id, customer!.Id, conversation.Id, new CustomerReplyCorrelation());
        var input = Args(new { orderId = "gid://shopify/Order/12345", restock = true, reason = "Customer", refundMethod = "OriginalPaymentMethod" });

        var result = await tool.ProposeAsync(context, input, CancellationToken.None);

        Assert.False(result.DispatchResult.IsError);
        Assert.NotNull(result.ApprovalId);

        var approval = await db.Approvals.IgnoreQueryFilters().SingleAsync(a => a.Id == result.ApprovalId);
        Assert.Equal(ApprovalStatus.Pending, approval.Status);
        Assert.Equal(business.Id, approval.BusinessId);
        Assert.Equal(conversation.Id, approval.ConversationId);
        Assert.Equal(customer.Id, approval.CustomerId);
        Assert.Equal("cancelOrder", approval.ToolName);
        Assert.Equal("gid://shopify/Order/12345", approval.ResourceId);
        Assert.False(string.IsNullOrWhiteSpace(approval.DisplaySummary));
        Assert.Null(approval.OrderCancellationId); // execution-time only, never populated by proposal
    }

    [Fact]
    public async Task ProposeAsync_NoResolvedCustomer_RefusesAndPersistsNoApproval()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (business, _, conversation) = await SeedAsync(db, resolveCustomer: false);

        var tool = new OrderCancelProposalTool(db);
        // Deliberately supplies a non-null ToolDispatchContext.CustomerId
        // (the type requires one) that does NOT match Conversation.CustomerId
        // in the DB (which is null) — proves ProposeAsync re-derives the
        // customer fresh from the Conversation row rather than trusting
        // whatever the context happens to carry.
        var context = new ToolDispatchContext(business.Id, Guid.NewGuid(), conversation.Id, new CustomerReplyCorrelation());
        var input = Args(new { orderId = "gid://shopify/Order/12345", restock = true, reason = "Customer" });

        var result = await tool.ProposeAsync(context, input, CancellationToken.None);

        Assert.True(result.DispatchResult.IsError);
        Assert.Null(result.ApprovalId);

        var approvalCount = await db.Approvals.IgnoreQueryFilters().CountAsync(a => a.ConversationId == conversation.Id);
        Assert.Equal(0, approvalCount);
    }

    /// <summary>
    /// Step 33 — regression test for the gap step-31/32 flagged as the
    /// higher-priority sibling of the already-fixed decode-side
    /// Enum.TryParse-without-IsDefined gap: ParseInput used Enum.Parse
    /// directly on the AI's own raw, untrusted "reason" string. A numeric
    /// string with no corresponding OrderCancelReason member (e.g. "99")
    /// parses "successfully" via .NET's own numeric-string-acceptance
    /// behavior for Enum.Parse/TryParse, producing an undefined
    /// OrderCancelReason value that -- before this fix -- was silently
    /// canonicalized, hashed, and persisted onto a Pending Approval row as
    /// if it were a legitimate, staff-reviewable proposal, directly
    /// violating Principle 1 (Nivora's backend, not the AI, must
    /// independently reject invalid input before anything is hashed or
    /// approved).
    /// </summary>
    [Fact]
    public async Task ProposeAsync_OutOfRangeNumericReason_RefusesAndPersistsNoApproval()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (business, customer, conversation) = await SeedAsync(db, resolveCustomer: true);

        var tool = new OrderCancelProposalTool(db);
        var context = new ToolDispatchContext(business.Id, customer!.Id, conversation.Id, new CustomerReplyCorrelation());
        var input = Args(new { orderId = "gid://shopify/Order/12345", restock = true, reason = "99" });

        var result = await tool.ProposeAsync(context, input, CancellationToken.None);

        Assert.True(result.DispatchResult.IsError);
        Assert.Null(result.ApprovalId);

        var approvalCount = await db.Approvals.IgnoreQueryFilters().CountAsync(a => a.ConversationId == conversation.Id);
        Assert.Equal(0, approvalCount);
    }

    /// <summary>
    /// Same underlying gap as
    /// ProposeAsync_OutOfRangeNumericReason_RefusesAndPersistsNoApproval,
    /// for the "refundMethod" field -- "99" has no corresponding
    /// RefundMethod member (only 0 and 1 are defined).
    /// </summary>
    [Fact]
    public async Task ProposeAsync_OutOfRangeNumericRefundMethod_RefusesAndPersistsNoApproval()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (business, customer, conversation) = await SeedAsync(db, resolveCustomer: true);

        var tool = new OrderCancelProposalTool(db);
        var context = new ToolDispatchContext(business.Id, customer!.Id, conversation.Id, new CustomerReplyCorrelation());
        var input = Args(new { orderId = "gid://shopify/Order/12345", restock = true, reason = "Customer", refundMethod = "99" });

        var result = await tool.ProposeAsync(context, input, CancellationToken.None);

        Assert.True(result.DispatchResult.IsError);
        Assert.Null(result.ApprovalId);

        var approvalCount = await db.Approvals.IgnoreQueryFilters().CountAsync(a => a.ConversationId == conversation.Id);
        Assert.Equal(0, approvalCount);
    }

    [Fact]
    public async Task ProposeAsync_ActionHash_MatchesIndependentlyComputedHash()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (business, customer, conversation) = await SeedAsync(db, resolveCustomer: true);

        var tool = new OrderCancelProposalTool(db);
        var context = new ToolDispatchContext(business.Id, customer!.Id, conversation.Id, new CustomerReplyCorrelation());
        var input = Args(new { orderId = "gid://shopify/Order/99999", restock = false, reason = "Fraud", refundMethod = "StoreCredit" });

        var result = await tool.ProposeAsync(context, input, CancellationToken.None);
        var approval = await db.Approvals.IgnoreQueryFilters().SingleAsync(a => a.Id == result.ApprovalId);

        // Independently computed, not copied from the implementation —
        // same canonicalizer and hasher the tool itself calls, but invoked
        // here directly against known, hand-supplied inputs.
        var expectedCanonicalArguments = ToolArgumentCanonicalizer.CanonicalizeOrderCancellationArgs(
            restock: false, method: RefundMethod.StoreCredit, reason: OrderCancelReason.Fraud);
        var expectedHash = ActionHasher.Derive(
            business.Id, conversation.Id, customer.Id, "cancelOrder", "gid://shopify/Order/99999", expectedCanonicalArguments);

        Assert.Equal(expectedCanonicalArguments, approval.CanonicalArguments);
        Assert.Equal(expectedHash, approval.ActionHash);
    }

    [Fact]
    public async Task ProposeAsync_ExpiresAt_IsExactlyFifteenMinutesAfterCreatedAt()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (business, customer, conversation) = await SeedAsync(db, resolveCustomer: true);

        var tool = new OrderCancelProposalTool(db);
        var context = new ToolDispatchContext(business.Id, customer!.Id, conversation.Id, new CustomerReplyCorrelation());
        var input = Args(new { orderId = "gid://shopify/Order/55555", restock = true, reason = "Other" });

        var result = await tool.ProposeAsync(context, input, CancellationToken.None);
        var approval = await db.Approvals.IgnoreQueryFilters().SingleAsync(a => a.Id == result.ApprovalId);

        Assert.Equal(approval.CreatedAt.AddMinutes(15), approval.ExpiresAt);
    }

    private class FixedTenantAccessor(Guid? businessId) : ICurrentTenantAccessor
    {
        public Guid? BusinessId => businessId;
    }
}
