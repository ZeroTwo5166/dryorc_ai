using System.Text.Json;
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
/// Service-level tests for RefundProposalTool — same pattern as
/// OrderCancelProposalToolTests. Covers proposal creation only (Step
/// refundOrder Phase 2's own scope, per docs/handoffs/step-refundorder-design.md
/// §21) — no Level 2, no ownership, no Shopify call, no Execute, and no
/// Refund row (deliberately never created by this class — see
/// RefundProposalTool's own class doc comment).
/// </summary>
public class RefundProposalToolTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;
    private readonly List<Guid> _businessIdsToClean = [];

    public Task InitializeAsync()
    {
        var configuration = new ConfigurationBuilder().AddUserSecrets<Program>().Build();
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

            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Conversations\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Customers\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Widgets\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Businesses\" WHERE \"Id\" = ANY({ids})");
        }

        await _provider.DisposeAsync();
    }

    private async Task<(Business Business, Customer? Customer, Conversation Conversation)> SeedAsync(AppDbContext db, bool resolveCustomer)
    {
        var business = new Business
        {
            Id = Guid.NewGuid(),
            Name = $"RefundProposalTool Test {Guid.NewGuid():N}",
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

    private static object OneItem(string lineItemId = "gid://shopify/LineItem/1", int quantity = 1) =>
        new[] { new { externalLineItemId = lineItemId, quantity } };

    [Fact]
    public async Task ProposeAsync_ResolvedCustomer_CreatesPendingApproval_ReturnsApprovalId()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (business, customer, conversation) = await SeedAsync(db, resolveCustomer: true);

        var tool = new RefundProposalTool(db);
        var context = new ToolDispatchContext(business.Id, customer!.Id, conversation.Id, new CustomerReplyCorrelation());
        var input = Args(new { orderId = "gid://shopify/Order/12345", items = OneItem(), refundMethod = "OriginalPaymentMethod" });

        var result = await tool.ProposeAsync(context, input, CancellationToken.None);

        Assert.False(result.DispatchResult.IsError);
        Assert.NotNull(result.ApprovalId);

        var approval = await db.Approvals.IgnoreQueryFilters().SingleAsync(a => a.Id == result.ApprovalId);
        Assert.Equal(ApprovalStatus.Pending, approval.Status);
        Assert.Equal(business.Id, approval.BusinessId);
        Assert.Equal(conversation.Id, approval.ConversationId);
        Assert.Equal(customer.Id, approval.CustomerId);
        Assert.Equal("refundOrder", approval.ToolName);
        Assert.Equal("gid://shopify/Order/12345", approval.ResourceId);
        Assert.False(string.IsNullOrWhiteSpace(approval.DisplaySummary));
        // Never populated by proposal — see class doc comment. Execution-
        // intent creation belongs to a future RefundApprovedActionExecutor
        // (Phase 3), inside its own atomic-claim transaction.
        Assert.Null(approval.RefundId);
        Assert.Null(approval.OrderCancellationId);
    }

    [Fact]
    public async Task ProposeAsync_NoResolvedCustomer_RefusesAndPersistsNoApproval()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (business, _, conversation) = await SeedAsync(db, resolveCustomer: false);

        var tool = new RefundProposalTool(db);
        // Deliberately supplies a non-null ToolDispatchContext.CustomerId
        // that does NOT match Conversation.CustomerId in the DB (which is
        // null) — proves ProposeAsync re-derives the customer fresh from the
        // Conversation row rather than trusting the context value.
        var context = new ToolDispatchContext(business.Id, Guid.NewGuid(), conversation.Id, new CustomerReplyCorrelation());
        var input = Args(new { orderId = "gid://shopify/Order/12345", items = OneItem(), refundMethod = "OriginalPaymentMethod" });

        var result = await tool.ProposeAsync(context, input, CancellationToken.None);

        Assert.True(result.DispatchResult.IsError);
        Assert.Null(result.ApprovalId);

        var approvalCount = await db.Approvals.IgnoreQueryFilters().CountAsync(a => a.ConversationId == conversation.Id);
        Assert.Equal(0, approvalCount);
    }

    [Fact]
    public async Task ProposeAsync_EmptyItemsArray_RefusesAndPersistsNoApproval()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (business, customer, conversation) = await SeedAsync(db, resolveCustomer: true);

        var tool = new RefundProposalTool(db);
        var context = new ToolDispatchContext(business.Id, customer!.Id, conversation.Id, new CustomerReplyCorrelation());
        var input = Args(new { orderId = "gid://shopify/Order/12345", items = Array.Empty<object>(), refundMethod = "OriginalPaymentMethod" });

        var result = await tool.ProposeAsync(context, input, CancellationToken.None);

        Assert.True(result.DispatchResult.IsError);
        Assert.Null(result.ApprovalId);

        var approvalCount = await db.Approvals.IgnoreQueryFilters().CountAsync(a => a.ConversationId == conversation.Id);
        Assert.Equal(0, approvalCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ProposeAsync_NonPositiveQuantity_RefusesAndPersistsNoApproval(int quantity)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (business, customer, conversation) = await SeedAsync(db, resolveCustomer: true);

        var tool = new RefundProposalTool(db);
        var context = new ToolDispatchContext(business.Id, customer!.Id, conversation.Id, new CustomerReplyCorrelation());
        var input = Args(new { orderId = "gid://shopify/Order/12345", items = OneItem(quantity: quantity), refundMethod = "OriginalPaymentMethod" });

        var result = await tool.ProposeAsync(context, input, CancellationToken.None);

        Assert.True(result.DispatchResult.IsError);
        Assert.Null(result.ApprovalId);

        var approvalCount = await db.Approvals.IgnoreQueryFilters().CountAsync(a => a.ConversationId == conversation.Id);
        Assert.Equal(0, approvalCount);
    }

    [Fact]
    public async Task ProposeAsync_DuplicateLineItemWithinRequest_RefusesAndPersistsNoApproval()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (business, customer, conversation) = await SeedAsync(db, resolveCustomer: true);

        var tool = new RefundProposalTool(db);
        var context = new ToolDispatchContext(business.Id, customer!.Id, conversation.Id, new CustomerReplyCorrelation());
        var input = Args(new
        {
            orderId = "gid://shopify/Order/12345",
            items = new[]
            {
                new { externalLineItemId = "gid://shopify/LineItem/1", quantity = 1 },
                new { externalLineItemId = "gid://shopify/LineItem/1", quantity = 2 },
            },
            refundMethod = "OriginalPaymentMethod",
        });

        var result = await tool.ProposeAsync(context, input, CancellationToken.None);

        Assert.True(result.DispatchResult.IsError);
        Assert.Null(result.ApprovalId);

        var approvalCount = await db.Approvals.IgnoreQueryFilters().CountAsync(a => a.ConversationId == conversation.Id);
        Assert.Equal(0, approvalCount);
    }

    [Fact]
    public async Task ProposeAsync_OutOfRangeNumericRefundMethod_RefusesAndPersistsNoApproval()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (business, customer, conversation) = await SeedAsync(db, resolveCustomer: true);

        var tool = new RefundProposalTool(db);
        var context = new ToolDispatchContext(business.Id, customer!.Id, conversation.Id, new CustomerReplyCorrelation());
        // Mirrors OrderCancelProposalToolTests' own regression coverage for
        // the same Enum.TryParse-without-IsDefined gap (Step 33) — "99" has
        // no corresponding RefundMethod member.
        var input = Args(new { orderId = "gid://shopify/Order/12345", items = OneItem(), refundMethod = "99" });

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

        var tool = new RefundProposalTool(db);
        var context = new ToolDispatchContext(business.Id, customer!.Id, conversation.Id, new CustomerReplyCorrelation());
        var input = Args(new
        {
            orderId = "gid://shopify/Order/99999",
            items = new[]
            {
                new { externalLineItemId = "gid://shopify/LineItem/1", quantity = 2 },
                new { externalLineItemId = "gid://shopify/LineItem/2", quantity = 1 },
            },
            refundMethod = "StoreCredit",
        });

        var result = await tool.ProposeAsync(context, input, CancellationToken.None);
        var approval = await db.Approvals.IgnoreQueryFilters().SingleAsync(a => a.Id == result.ApprovalId);

        // Independently computed, not copied from the implementation — same
        // canonicalizer and hasher the tool itself calls, but invoked here
        // directly against known, hand-supplied inputs.
        var expectedCanonicalArguments = ToolArgumentCanonicalizer.CanonicalizeRefundArgs(
            RefundMethod.StoreCredit,
            [("gid://shopify/LineItem/1", 2), ("gid://shopify/LineItem/2", 1)]);
        var expectedHash = ActionHasher.Derive(
            business.Id, conversation.Id, customer.Id, "refundOrder", "gid://shopify/Order/99999", expectedCanonicalArguments);

        Assert.Equal(expectedCanonicalArguments, approval.CanonicalArguments);
        Assert.Equal(expectedHash, approval.ActionHash);
    }

    [Fact]
    public async Task ProposeAsync_ItemOrderShuffled_ProducesSameCanonicalArgumentsAndHash()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (business, customer, conversation) = await SeedAsync(db, resolveCustomer: true);

        var tool = new RefundProposalTool(db);
        var context = new ToolDispatchContext(business.Id, customer!.Id, conversation.Id, new CustomerReplyCorrelation());

        var inOrderInput = Args(new
        {
            orderId = "gid://shopify/Order/1",
            items = new[]
            {
                new { externalLineItemId = "gid://shopify/LineItem/1", quantity = 1 },
                new { externalLineItemId = "gid://shopify/LineItem/2", quantity = 2 },
            },
            refundMethod = "OriginalPaymentMethod",
        });
        var shuffledInput = Args(new
        {
            orderId = "gid://shopify/Order/1",
            items = new[]
            {
                new { externalLineItemId = "gid://shopify/LineItem/2", quantity = 2 },
                new { externalLineItemId = "gid://shopify/LineItem/1", quantity = 1 },
            },
            refundMethod = "OriginalPaymentMethod",
        });

        var inOrderResult = await tool.ProposeAsync(context, inOrderInput, CancellationToken.None);
        var shuffledResult = await tool.ProposeAsync(context, shuffledInput, CancellationToken.None);

        var inOrderApproval = await db.Approvals.IgnoreQueryFilters().SingleAsync(a => a.Id == inOrderResult.ApprovalId);
        var shuffledApproval = await db.Approvals.IgnoreQueryFilters().SingleAsync(a => a.Id == shuffledResult.ApprovalId);

        Assert.Equal(inOrderApproval.CanonicalArguments, shuffledApproval.CanonicalArguments);
        Assert.Equal(inOrderApproval.ActionHash, shuffledApproval.ActionHash);
    }

    [Fact]
    public async Task ProposeAsync_ExpiresAt_IsExactlyFifteenMinutesAfterCreatedAt()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (business, customer, conversation) = await SeedAsync(db, resolveCustomer: true);

        var tool = new RefundProposalTool(db);
        var context = new ToolDispatchContext(business.Id, customer!.Id, conversation.Id, new CustomerReplyCorrelation());
        var input = Args(new { orderId = "gid://shopify/Order/55555", items = OneItem(), refundMethod = "OriginalPaymentMethod" });

        var result = await tool.ProposeAsync(context, input, CancellationToken.None);
        var approval = await db.Approvals.IgnoreQueryFilters().SingleAsync(a => a.Id == result.ApprovalId);

        Assert.Equal(approval.CreatedAt.AddMinutes(15), approval.ExpiresAt);
    }

    // ── Structural boundary-verification tests (Phase 2 mandatory) ──────

    /// <summary>
    /// RefundProposalTool implements only IActionProposalTool, which
    /// extends IDispatchTool and exposes no ExecuteAsync-shaped member
    /// anywhere in its interface chain (IActionProposalTool's own class doc
    /// comment: "Structurally cannot reach WriteToolBase.ExecuteMutationAsync-
    /// shaped logic"). This is a compile-time/reflection-provable structural
    /// guarantee, not a runtime behavior test — there is no method on this
    /// type or its interfaces that could call Shopify, mark a Refund
    /// Confirmed/Failed, or invoke any IApprovedActionExecutor, because none
    /// of those capabilities are reachable through any member this class
    /// exposes.
    /// </summary>
    [Fact]
    public void RefundProposalTool_ImplementsOnlyIActionProposalTool_NoExecutorOrWriteCapability()
    {
        var toolType = typeof(RefundProposalTool);
        var interfaces = toolType.GetInterfaces();

        Assert.Contains(typeof(IActionProposalTool), interfaces);
        Assert.DoesNotContain(typeof(IApprovedActionExecutor), interfaces);
        Assert.DoesNotContain(typeof(IWriteDispatchTool), interfaces);

        // No method on the concrete type or its interfaces is named/shaped
        // like a mutation entry point, an executor entry point, or either of
        // IRefundService's own terminal-state transitions.
        var allMethodNames = toolType.GetMethods()
            .Concat(interfaces.SelectMany(i => i.GetMethods()))
            .Select(m => m.Name)
            .ToHashSet();

        Assert.DoesNotContain("ExecuteAsync", allMethodNames);
        Assert.DoesNotContain("ExecuteApprovedActionAsync", allMethodNames);
        Assert.DoesNotContain(nameof(IRefundService.MarkConfirmedAsync), allMethodNames);
        Assert.DoesNotContain(nameof(IRefundService.MarkFailedAsync), allMethodNames);
    }

    /// <summary>
    /// RefundProposalTool's only constructor dependency is AppDbContext —
    /// it does not take an IRefundService, an IRefundExecutor (does not
    /// exist), an HttpClient, or any Shopify-calling dependency. Reflected
    /// directly against the constructor, not asserted from prose: if a
    /// future change ever added such a dependency, this test would fail.
    /// </summary>
    [Fact]
    public void RefundProposalTool_Constructor_TakesOnlyAppDbContext()
    {
        var constructors = typeof(RefundProposalTool).GetConstructors();
        var constructor = Assert.Single(constructors);
        var parameterTypes = constructor.GetParameters().Select(p => p.ParameterType).ToList();

        Assert.Equal([typeof(AppDbContext)], parameterTypes);
    }

    private class FixedTenantAccessor(Guid? businessId) : ICurrentTenantAccessor
    {
        public Guid? BusinessId => businessId;
    }
}
