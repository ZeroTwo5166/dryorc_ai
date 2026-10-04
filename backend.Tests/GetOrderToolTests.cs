using backend.Constants;
using backend.Data;
using backend.Enums;
using backend.Interfaces;
using backend.Models;
using backend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace backend.Tests;

/// <summary>
/// Step getOrder — security/functional coverage for GetOrderTool.ExecuteAsync
/// itself (design §7's "Security"/"Functional" sections), using fake
/// ILevel2VerificationChecker/IOrderOwnershipAuthorizationService/
/// IShopifyOrderLookupService substitutes so this suite exercises
/// GetOrderTool's own sequencing/branching without a real Shopify call. Real
/// Postgres for the Business/Widget/Customer/Conversation/Integration rows
/// GetOrderTool's own db.Integrations query and audit-log FK constraints
/// require — same convention as ShopifyOrderLookupServiceTests.
///
/// The fake IShopifyOrderLookupService's LookupOrderOwnerAsync/GetCancellationStateAsync
/// implementations throw NotSupportedException unconditionally — this is a
/// deliberate regression guard, not an oversight: GetOrderTool must never
/// call either (design §1.4/§2.1a), and a future accidental call to the
/// wrong method fails this suite loudly instead of silently passing.
/// </summary>
public class GetOrderToolTests : IAsyncLifetime
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

        var services = new ServiceCollection()
            .AddSingleton<ICurrentTenantAccessor>(new FixedTenantAccessor(null))
            .AddDbContext<AppDbContext>(o => o.UseNpgsql(
                PostgresConnectionString.FromUri(connectionUri), x => x.UseVector()));
        _provider = services.BuildServiceProvider();
        // GetOrderTool's own db.Integrations query uses IgnoreQueryFilters(), so
        // the fixed-null tenant accessor above is never actually consulted —
        // registered only because AppDbContext's constructor requires one.

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_businessIdsToClean.Count > 0)
        {
            await using var scope = _provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var ids = _businessIdsToClean.ToArray();

            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"AuditLogs\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Integrations\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Conversations\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Customers\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Widgets\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Businesses\" WHERE \"Id\" = ANY({ids})");
        }

        await _provider.DisposeAsync();
    }

    private async Task<(Guid BusinessId, Guid ConversationId, Guid CustomerId)> SeedBusinessAndConversationAsync(bool withActiveIntegration)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var business = new Business
        {
            Id = Guid.NewGuid(),
            Name = $"GetOrderTool Test {Guid.NewGuid():N}",
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
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id,
            Name = "Test Customer",
            Email = "test.customer@marker-test.invalid",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id,
            WidgetId = widget.Id,
            CustomerId = customer.Id,
            VisitorSessionId = Guid.NewGuid().ToString("N"),
            Channel = "Website",
            Status = ConversationStatus.Open,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        db.Businesses.Add(business);
        db.Widgets.Add(widget);
        db.Customers.Add(customer);
        db.Conversations.Add(conversation);

        if (withActiveIntegration)
        {
            db.Integrations.Add(new Integration
            {
                Id = Guid.NewGuid(),
                BusinessId = business.Id,
                Platform = ShopifyIntegrationOAuthProvider.PlatformName,
                EncryptedAccessToken = "unused-in-this-suite-shopify-is-faked",
                ExternalAccountIdentifier = $"test-shop-{Guid.NewGuid():N}",
                IsActive = true,
                ConnectedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }

        await db.SaveChangesAsync();
        _businessIdsToClean.Add(business.Id);
        return (business.Id, conversation.Id, customer.Id);
    }

    private static Dictionary<string, JsonElement> Input(string orderIdentifier) => new()
    {
        ["orderIdentifier"] = JsonSerializer.SerializeToElement(orderIdentifier),
    };

    // ── Security ──────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_NotLevel2Verified_ReturnsGroupARefusal_NeverCallsShopify()
    {
        var (businessId, conversationId, customerId) = await SeedBusinessAndConversationAsync(withActiveIntegration: true);
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var level2 = new FakeLevel2VerificationChecker(verified: false);
        var orderLookup = new FakeShopifyOrderLookupService();
        var tool = new GetOrderTool(db, level2, orderLookup, new FakeOwnershipAuthorizationService(), new FakeAuditLogService());

        var result = await tool.ExecuteAsync(
            new ToolDispatchContext(businessId, customerId, conversationId, new CustomerReplyCorrelation()), Input("#1001"), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("verify", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, orderLookup.ResolveOrderNameCallCount);
        Assert.Equal(0, orderLookup.LookupOrderOwnerForReadToolCallCount);
        Assert.Equal(1, level2.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_NullConversationId_ReturnsGroupARefusal_NeverCallsCheckerOrShopify()
    {
        var (businessId, _, customerId) = await SeedBusinessAndConversationAsync(withActiveIntegration: true);
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var level2 = new FakeLevel2VerificationChecker(verified: true);
        var orderLookup = new FakeShopifyOrderLookupService();
        var tool = new GetOrderTool(db, level2, orderLookup, new FakeOwnershipAuthorizationService(), new FakeAuditLogService());

        var result = await tool.ExecuteAsync(
            new ToolDispatchContext(businessId, customerId, ConversationId: null, new CustomerReplyCorrelation()), Input("#1001"), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("verify", result.Content, StringComparison.OrdinalIgnoreCase);
        // Never synthesize a conversation id to call IsVerifiedAsync with (design §3 step 2).
        Assert.Equal(0, level2.CallCount);
        Assert.Equal(0, orderLookup.ResolveOrderNameCallCount);
        Assert.Equal(0, orderLookup.LookupOrderOwnerForReadToolCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_NoActiveIntegration_ReturnsGroupDRefusal_NoUncaughtException()
    {
        var (businessId, conversationId, customerId) = await SeedBusinessAndConversationAsync(withActiveIntegration: false);
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var orderLookup = new FakeShopifyOrderLookupService();
        var tool = new GetOrderTool(
            db, new FakeLevel2VerificationChecker(verified: true), orderLookup, new FakeOwnershipAuthorizationService(), new FakeAuditLogService());

        var result = await tool.ExecuteAsync(
            new ToolDispatchContext(businessId, customerId, conversationId, new CustomerReplyCorrelation()), Input("#1001"), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("trouble reaching", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, orderLookup.ResolveOrderNameCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_OwnershipFails_ReturnsGroupBRefusal_WritesOwnershipRefusalAudit()
    {
        var (businessId, conversationId, customerId) = await SeedBusinessAndConversationAsync(withActiveIntegration: true);
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var orderLookup = new FakeShopifyOrderLookupService
        {
            OwnershipResultFactory = () => new VerifiedOrderOwnership("gid://shopify/Order/1", "gid://shopify/Customer/999"),
        };
        var ownership = new FakeOwnershipAuthorizationService(
            new OwnershipCheckResult(false, OwnershipCheckFailureReason.OwnerMismatch));
        var audit = new FakeAuditLogService();
        var tool = new GetOrderTool(db, new FakeLevel2VerificationChecker(verified: true), orderLookup, ownership, audit);

        var result = await tool.ExecuteAsync(
            new ToolDispatchContext(businessId, customerId, conversationId, new CustomerReplyCorrelation()),
            Input("gid://shopify/Order/1"), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("I can't find an order matching that for your account.", result.Content);

        var call = Assert.Single(audit.Calls);
        Assert.Equal(businessId, call.BusinessId);
        Assert.Equal(AuditEntityTypes.Order, call.EntityType);
        Assert.Equal("gid://shopify/Order/1", call.EntityId);
        Assert.Equal(AuditEventTypes.OrderLookupPerformed, call.EventType);
        Assert.Equal(ActorType.Ai, call.Actor);
        Assert.Equal(conversationId, call.ConversationId);
        Assert.Equal("OwnershipVerificationFailed:OwnerMismatch", call.NewValue);
        // FakeAuditLogService only records the call — it never touches db,
        // so there is nothing to assert against db.AuditLogs here; whether
        // the real IAuditLogService implementation persists correctly is
        // that service's own, already-covered test suite's concern, not
        // GetOrderTool's. This test's scope is GetOrderTool calling
        // LogAsync with the right arguments, asserted above.
    }

    [Fact]
    public async Task ExecuteAsync_Success_ReturnsFormattedDetail_WritesSuccessAudit()
    {
        var (businessId, conversationId, customerId) = await SeedBusinessAndConversationAsync(withActiveIntegration: true);
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var detail = new OrderDetailResult(
            "#1001", "PAID", "FULFILLED", null,
            [new OrderLineItemDetail("Widget", 2, "9.99")],
            [new OrderTrackingInfo("1Z999", "https://track.example/1Z999"), new OrderTrackingInfo("1Z888", null)]);

        var orderLookup = new FakeShopifyOrderLookupService
        {
            OwnershipResultFactory = () => new VerifiedOrderOwnership("gid://shopify/Order/1", "gid://shopify/Customer/1"),
            DetailResultFactory = () => detail,
        };
        var audit = new FakeAuditLogService();
        var tool = new GetOrderTool(
            db, new FakeLevel2VerificationChecker(verified: true), orderLookup,
            new FakeOwnershipAuthorizationService(new OwnershipCheckResult(true, OwnershipCheckFailureReason.None)), audit);

        var result = await tool.ExecuteAsync(
            new ToolDispatchContext(businessId, customerId, conversationId, new CustomerReplyCorrelation()),
            Input("gid://shopify/Order/1"), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("#1001", result.Content);
        Assert.Contains("PAID", result.Content);
        Assert.Contains("FULFILLED", result.Content);
        Assert.Contains("Widget", result.Content);
        Assert.Contains("1Z999", result.Content);
        Assert.Contains("1Z888", result.Content);

        var call = Assert.Single(audit.Calls);
        Assert.Equal("Success", call.NewValue);
        Assert.Equal("gid://shopify/Order/1", call.EntityId);
    }

    [Fact]
    public async Task ExecuteAsync_GidInput_SkipsNameResolution()
    {
        var (businessId, conversationId, customerId) = await SeedBusinessAndConversationAsync(withActiveIntegration: true);
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var orderLookup = new FakeShopifyOrderLookupService
        {
            OwnershipResultFactory = () => new VerifiedOrderOwnership("gid://shopify/Order/42", "gid://shopify/Customer/1"),
            DetailResultFactory = () => new OrderDetailResult("#42", null, null, null, [], []),
        };
        var tool = new GetOrderTool(
            db, new FakeLevel2VerificationChecker(verified: true), orderLookup,
            new FakeOwnershipAuthorizationService(new OwnershipCheckResult(true, OwnershipCheckFailureReason.None)), new FakeAuditLogService());

        var result = await tool.ExecuteAsync(
            new ToolDispatchContext(businessId, customerId, conversationId, new CustomerReplyCorrelation()),
            Input("gid://shopify/Order/42"), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(0, orderLookup.ResolveOrderNameCallCount);
        Assert.Equal(1, orderLookup.LookupOrderOwnerForReadToolCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_NameResolutionNotFound_ReturnsGroupBRefusal_NoOwnershipCall()
    {
        var (businessId, conversationId, customerId) = await SeedBusinessAndConversationAsync(withActiveIntegration: true);
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var orderLookup = new FakeShopifyOrderLookupService
        {
            ResolveResultFactory = () => new OrderNameResolutionResult(OrderNameResolutionOutcome.NotFound, null),
        };
        var ownership = new FakeOwnershipAuthorizationService();
        var tool = new GetOrderTool(db, new FakeLevel2VerificationChecker(verified: true), orderLookup, ownership, new FakeAuditLogService());

        var result = await tool.ExecuteAsync(
            new ToolDispatchContext(businessId, customerId, conversationId, new CustomerReplyCorrelation()), Input("#9999"), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("I can't find an order matching that for your account.", result.Content);
        Assert.Equal(0, orderLookup.LookupOrderOwnerForReadToolCallCount);
        Assert.Equal(0, ownership.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_NameResolutionAmbiguous_ReturnsGroupCRefusal()
    {
        var (businessId, conversationId, customerId) = await SeedBusinessAndConversationAsync(withActiveIntegration: true);
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var orderLookup = new FakeShopifyOrderLookupService
        {
            ResolveResultFactory = () => new OrderNameResolutionResult(OrderNameResolutionOutcome.Ambiguous, null),
        };
        var tool = new GetOrderTool(
            db, new FakeLevel2VerificationChecker(verified: true), orderLookup, new FakeOwnershipAuthorizationService(), new FakeAuditLogService());

        var result = await tool.ExecuteAsync(
            new ToolDispatchContext(businessId, customerId, conversationId, new CustomerReplyCorrelation()), Input("1001"), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("more than one order", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_ShopifyAuthFailureFromOwnershipLookup_ReturnsGroupDRefusal_DoesNotPropagate()
    {
        var (businessId, conversationId, customerId) = await SeedBusinessAndConversationAsync(withActiveIntegration: true);
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var orderLookup = new FakeShopifyOrderLookupService
        {
            OwnershipResultFactory = () => throw new ShopifyAuthFailureException(),
        };
        var tool = new GetOrderTool(
            db, new FakeLevel2VerificationChecker(verified: true), orderLookup, new FakeOwnershipAuthorizationService(), new FakeAuditLogService());

        var result = await tool.ExecuteAsync(
            new ToolDispatchContext(businessId, customerId, conversationId, new CustomerReplyCorrelation()),
            Input("gid://shopify/Order/1"), CancellationToken.None);

        // Must be the reauthorization-safe Group D text, never the generic
        // "internal error" text ToolDispatchService's own backstop would
        // produce if this exception had been allowed to propagate uncaught
        // (design §1.5/§3).
        Assert.True(result.IsError);
        Assert.Contains("trouble reaching", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("internal error", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_MissingOrderIdentifier_ReturnsError()
    {
        var (businessId, conversationId, customerId) = await SeedBusinessAndConversationAsync(withActiveIntegration: true);
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var tool = new GetOrderTool(
            db, new FakeLevel2VerificationChecker(verified: true), new FakeShopifyOrderLookupService(),
            new FakeOwnershipAuthorizationService(), new FakeAuditLogService());

        var result = await tool.ExecuteAsync(
            new ToolDispatchContext(businessId, customerId, conversationId, new CustomerReplyCorrelation()),
            new Dictionary<string, JsonElement>(), CancellationToken.None);

        Assert.True(result.IsError);
    }

    // ── Test doubles ──────────────────────────────────────────────────

    private sealed class FixedTenantAccessor(Guid? businessId) : ICurrentTenantAccessor
    {
        public Guid? BusinessId => businessId;
    }

    private sealed class FakeLevel2VerificationChecker(bool verified) : ILevel2VerificationChecker
    {
        public int CallCount { get; private set; }

        public Task<bool> IsVerifiedAsync(Guid businessId, Guid conversationId, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(verified);
        }
    }

    private sealed class FakeOwnershipAuthorizationService(OwnershipCheckResult? result = null) : IOrderOwnershipAuthorizationService
    {
        public int CallCount { get; private set; }

        public Task<OwnershipCheckResult> AuthorizeAsync(
            Guid businessId, Guid customerId, VerifiedOrderOwnership orderOwnership, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(result ?? new OwnershipCheckResult(true, OwnershipCheckFailureReason.None));
        }
    }

    private sealed class FakeAuditLogService : IAuditLogService
    {
        public List<(Guid BusinessId, string EntityType, string EntityId, string EventType, ActorType Actor, string? NewValue, Guid? ConversationId)> Calls { get; } = [];

        public Task LogAsync(
            Guid businessId, string entityType, string entityId, string eventType, ActorType actor,
            Guid? changedByUserId = null, string? oldValue = null, string? newValue = null,
            Guid? conversationId = null, Guid? correlationId = null, CancellationToken cancellationToken = default)
        {
            Calls.Add((businessId, entityType, entityId, eventType, actor, newValue, conversationId));
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// LookupOrderOwnerAsync/GetCancellationStateAsync throw unconditionally
    /// — GetOrderTool must never call either (design §1.4/§2.1a); a mistaken
    /// call fails loudly instead of silently passing.
    /// </summary>
    private sealed class FakeShopifyOrderLookupService : IShopifyOrderLookupService
    {
        public int ResolveOrderNameCallCount { get; private set; }
        public int LookupOrderOwnerForReadToolCallCount { get; private set; }
        public int GetOrderDetailCallCount { get; private set; }

        public Func<OrderNameResolutionResult>? ResolveResultFactory { get; set; }
        public Func<VerifiedOrderOwnership>? OwnershipResultFactory { get; set; }
        public Func<OrderDetailResult>? DetailResultFactory { get; set; }

        public Task<VerifiedOrderOwnership> LookupOrderOwnerAsync(Guid businessId, string externalOrderId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("getOrder must never call LookupOrderOwnerAsync — see design §1.4/§2.1a.");

        public Task<ShopifyOrderCancellationState> GetCancellationStateAsync(Guid businessId, string externalOrderId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by getOrder.");

        public Task<VerifiedOrderOwnership> LookupOrderOwnerForReadToolAsync(Guid businessId, string externalOrderId, CancellationToken cancellationToken)
        {
            LookupOrderOwnerForReadToolCallCount++;
            return Task.FromResult(OwnershipResultFactory!.Invoke());
        }

        public Task<OrderNameResolutionResult> ResolveOrderNameAsync(Guid businessId, string orderName, CancellationToken cancellationToken)
        {
            ResolveOrderNameCallCount++;
            return Task.FromResult(ResolveResultFactory!.Invoke());
        }

        public Task<OrderDetailResult> GetOrderDetailAsync(Guid businessId, string externalOrderId, CancellationToken cancellationToken)
        {
            GetOrderDetailCallCount++;
            return Task.FromResult(DetailResultFactory!.Invoke());
        }
    }
}
