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
/// Service-level tests for the GDPR customer data lifecycle
/// (ConversationPurgeService, CustomerErasureService,
/// RetentionEnforcementService, VerificationCodeCleanupService), run
/// against the real Neon dev DB via a bare ServiceProvider — same pattern
/// as AppDbContextTenantFilterTests, not the full HTTP WebApplicationFactory
/// from CrossTenantIsolationTests, since none of this is exercised through
/// an HTTP endpoint (no controller calls these services yet).
///
/// Atomicity is proven via IPurgeFailurePoint (a real DI-registered fake
/// that throws at a named checkpoint), not by killing the process
/// mid-test — see IPurgeFailurePoint's own doc comment for why.
///
/// Every test creates its own dedicated Business(es) and cleans up via raw
/// SQL in DisposeAsync, in FK-safe order, so this suite leaves the dev DB
/// exactly as it found it — same discipline as the existing test suites.
/// </summary>
public class CustomerDataLifecycleTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;
    private readonly List<Guid> _businessIdsToClean = [];
    private readonly List<Guid> _userIdsToClean = [];

    public Task InitializeAsync()
    {
        var configuration = new ConfigurationBuilder()
            .AddUserSecrets<Program>()
            .Build();
        var connectionUri = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection not found via user-secrets.");

        // None of the services under test rely on ICurrentTenantAccessor at
        // all (they resolve BusinessId explicitly — see
        // ConversationPurgeService's doc comment) — this fixed accessor
        // only exists because AppDbContext's constructor requires one.
        _provider = new ServiceCollection()
            .AddSingleton<ICurrentTenantAccessor>(new FixedTenantAccessor(null))
            .AddDbContext<AppDbContext>(o => o.UseNpgsql(
                PostgresConnectionString.FromUri(connectionUri), x => x.UseVector()))
            .AddScoped<IConversationPurgeService, ConversationPurgeService>()
            .AddScoped<ICustomerErasureService, CustomerErasureService>()
            .AddScoped<IRetentionEnforcementService, RetentionEnforcementService>()
            .AddScoped<IVerificationCodeCleanupService, VerificationCodeCleanupService>()
            .AddScoped<IPurgeFailurePoint, NoOpPurgeFailurePoint>()
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

            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"MessageKnowledgeSources\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Messages\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"VerificationCodes\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"CustomerExtendedLoggingHistories\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"FlaggedCustomerActivityLogs\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"ExternalIdentities\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Conversations\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Customers\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Widgets\" WHERE \"BusinessId\" = ANY({ids})");

            if (_userIdsToClean.Count > 0)
            {
                var userIds = _userIdsToClean.ToArray();
                await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"AspNetUsers\" WHERE \"Id\" = ANY({userIds})");
            }

            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Businesses\" WHERE \"Id\" = ANY({ids})");
        }

        await _provider.DisposeAsync();
    }

    // ── Fixture helpers ─────────────────────────────────────────────

    private async Task<Business> CreateBusinessAsync(AppDbContext db, int retentionDays = 180)
    {
        var business = new Business
        {
            Id = Guid.NewGuid(),
            Name = $"Test Business {Guid.NewGuid():N}",
            TimeZone = "Europe/Copenhagen",
            RetentionDays = retentionDays,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Businesses.Add(business);
        await db.SaveChangesAsync();
        _businessIdsToClean.Add(business.Id);
        return business;
    }

    private async Task<Widget> CreateWidgetAsync(AppDbContext db, Guid businessId)
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
        return widget;
    }

    private async Task<Customer> CreateCustomerAsync(AppDbContext db, Guid businessId)
    {
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            Email = $"customer-{Guid.NewGuid():N}@example.com",
            Name = "Jane Doe",
            ExternalId = $"shopify-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        return customer;
    }

    private async Task<Conversation> CreateConversationAsync(
        AppDbContext db, Guid businessId, Guid widgetId, Guid? customerId,
        DateTimeOffset createdAt, DateTimeOffset? lastMessageAt)
    {
        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            WidgetId = widgetId,
            CustomerId = customerId,
            VisitorSessionId = $"session-{Guid.NewGuid():N}",
            Channel = "Website",
            Tags = ["billing"],
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            LastMessageAt = lastMessageAt,
        };
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync();
        return conversation;
    }

    private async Task<Message> CreateMessageAsync(AppDbContext db, Guid businessId, Guid conversationId, DateTimeOffset createdAt)
    {
        var message = new Message
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            ConversationId = conversationId,
            Role = MessageRole.Customer,
            Content = "Hi, I still haven't received my refund for order #4471.",
            CreatedAt = createdAt,
        };
        db.Messages.Add(message);
        await db.SaveChangesAsync();
        return message;
    }

    private async Task<ApplicationUser> CreateStaffUserAsync(AppDbContext db, Guid businessId)
    {
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            UserName = $"staff-{Guid.NewGuid():N}@example.com",
            Email = $"staff-{Guid.NewGuid():N}@example.com",
            EmailConfirmed = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        _userIdsToClean.Add(user.Id);
        return user;
    }

    /// <summary>
    /// GDPR Phase 2 — minimal valid Approval fixture for
    /// ConversationPurgeService's Decision 4 cleanup tests below. Not
    /// cleaned up explicitly in DisposeAsync: Approval.ConversationId is
    /// Cascade (AppDbContext.cs), so the existing
    /// "DELETE FROM Conversations" cleanup step already removes any
    /// surviving row before Customers/Businesses are deleted.
    /// </summary>
    private async Task<Approval> CreateApprovalAsync(
        AppDbContext db, Guid businessId, Guid conversationId, Guid customerId, ApprovalStatus status)
    {
        var approval = new Approval
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            ConversationId = conversationId,
            CustomerId = customerId,
            ToolName = "cancelOrder",
            ResourceId = $"order-{Guid.NewGuid():N}",
            CanonicalArguments = "{}",
            ActionHash = Guid.NewGuid().ToString("N"),
            DisplaySummary = "Test approval",
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
        };
        db.Approvals.Add(approval);
        await db.SaveChangesAsync();
        return approval;
    }

    // ── 1. Purge atomicity ──────────────────────────────────────────

    [Fact]
    public async Task PurgeConversation_RollsBackCompletely_WhenFailureInjectedPartway_ThenSucceedsOnRetry()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var business = await CreateBusinessAsync(db);
        var widget = await CreateWidgetAsync(db, business.Id);
        var customer = await CreateCustomerAsync(db, business.Id);
        var conversation = await CreateConversationAsync(db, business.Id, widget.Id, customer.Id, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var message = await CreateMessageAsync(db, business.Id, conversation.Id, DateTimeOffset.UtcNow);

        // ---- Attempt 1: fails after Messages are deleted but before commit ----
        var failingService = new ConversationPurgeService(db, new FailAtCheckpoint("after-message-delete"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => failingService.PurgeConversationAsync(conversation.Id));

        // Rollback must have undone the DELETE — Message still exists, Conversation unchanged.
        var messageAfterFailure = await db.Messages.IgnoreQueryFilters().FirstOrDefaultAsync(m => m.Id == message.Id);
        Assert.NotNull(messageAfterFailure);

        var conversationAfterFailure = await db.Conversations.IgnoreQueryFilters().FirstOrDefaultAsync(c => c.Id == conversation.Id);
        Assert.NotNull(conversationAfterFailure);
        Assert.False(conversationAfterFailure!.IsAnonymized);
        Assert.Null(conversationAfterFailure.AnonymizedAt);
        Assert.NotNull(conversationAfterFailure.VisitorSessionId);
        Assert.Equal(conversation.VisitorSessionId, conversationAfterFailure.VisitorSessionId);

        // Customer, untouched by this operation at all, must remain as-is too.
        var customerAfterFailure = await db.Customers.IgnoreQueryFilters().FirstOrDefaultAsync(c => c.Id == customer.Id);
        Assert.NotNull(customerAfterFailure);
        Assert.False(customerAfterFailure!.IsAnonymized);

        // ---- Attempt 2: retry with no injected failure — must succeed ----
        var realService = scope.ServiceProvider.GetRequiredService<IConversationPurgeService>();
        await realService.PurgeConversationAsync(conversation.Id);

        var messageAfterSuccess = await db.Messages.IgnoreQueryFilters().FirstOrDefaultAsync(m => m.Id == message.Id);
        Assert.Null(messageAfterSuccess);

        var conversationAfterSuccess = await db.Conversations.IgnoreQueryFilters().FirstOrDefaultAsync(c => c.Id == conversation.Id);
        Assert.NotNull(conversationAfterSuccess);
        Assert.True(conversationAfterSuccess!.IsAnonymized);
        Assert.NotNull(conversationAfterSuccess.AnonymizedAt);
        Assert.Null(conversationAfterSuccess.VisitorSessionId);
    }

    [Fact]
    public async Task PurgeConversation_RollsBack_WhenFailureInjectedBeforeAnyWrite()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var business = await CreateBusinessAsync(db);
        var widget = await CreateWidgetAsync(db, business.Id);
        var conversation = await CreateConversationAsync(db, business.Id, widget.Id, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var message = await CreateMessageAsync(db, business.Id, conversation.Id, DateTimeOffset.UtcNow);

        var failingService = new ConversationPurgeService(db, new FailAtCheckpoint("before-message-delete"));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => failingService.PurgeConversationAsync(conversation.Id));

        var messageAfterFailure = await db.Messages.IgnoreQueryFilters().FirstOrDefaultAsync(m => m.Id == message.Id);
        Assert.NotNull(messageAfterFailure);

        var conversationAfterFailure = await db.Conversations.IgnoreQueryFilters().FirstOrDefaultAsync(c => c.Id == conversation.Id);
        Assert.False(conversationAfterFailure!.IsAnonymized);
    }

    // ── 2. Purge idempotency ────────────────────────────────────────

    [Fact]
    public async Task PurgeConversation_SecondCallAfterSuccess_IsSafeNoOp()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var purgeService = scope.ServiceProvider.GetRequiredService<IConversationPurgeService>();

        var business = await CreateBusinessAsync(db);
        var widget = await CreateWidgetAsync(db, business.Id);
        var conversation = await CreateConversationAsync(db, business.Id, widget.Id, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        await CreateMessageAsync(db, business.Id, conversation.Id, DateTimeOffset.UtcNow);

        // First call succeeds.
        await purgeService.PurgeConversationAsync(conversation.Id);

        var afterFirst = await db.Conversations.IgnoreQueryFilters().FirstOrDefaultAsync(c => c.Id == conversation.Id);
        Assert.True(afterFirst!.IsAnonymized);
        var anonymizedAtAfterFirst = afterFirst.AnonymizedAt;

        // Simulates a caller (e.g. CustomerErasureService, or the retention
        // job's loop) that committed successfully but crashed before
        // recording its own completion, and so retries — must not throw,
        // must not re-do or corrupt anything.
        var exception = await Record.ExceptionAsync(() => purgeService.PurgeConversationAsync(conversation.Id));
        Assert.Null(exception);

        var afterSecond = await db.Conversations.IgnoreQueryFilters().FirstOrDefaultAsync(c => c.Id == conversation.Id);
        Assert.True(afterSecond!.IsAnonymized);
        Assert.Equal(anonymizedAtAfterFirst, afterSecond.AnonymizedAt); // untouched by the no-op retry
    }

    [Fact]
    public async Task PurgeConversation_UnknownConversationId_IsSafeNoOp()
    {
        await using var scope = _provider.CreateAsyncScope();
        var purgeService = scope.ServiceProvider.GetRequiredService<IConversationPurgeService>();

        var exception = await Record.ExceptionAsync(() => purgeService.PurgeConversationAsync(Guid.NewGuid()));
        Assert.Null(exception);
    }

    // ── 3. Customer erasure end-to-end ──────────────────────────────

    [Fact]
    public async Task EraseCustomer_AnonymizesCustomer_DeletesIdentityRows_PurgesConversations()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var erasureService = scope.ServiceProvider.GetRequiredService<ICustomerErasureService>();

        var business = await CreateBusinessAsync(db);
        var widget = await CreateWidgetAsync(db, business.Id);
        var customer = await CreateCustomerAsync(db, business.Id);
        var staffUser = await CreateStaffUserAsync(db, business.Id);

        var conversation1 = await CreateConversationAsync(db, business.Id, widget.Id, customer.Id, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var message1 = await CreateMessageAsync(db, business.Id, conversation1.Id, DateTimeOffset.UtcNow);
        var conversation2 = await CreateConversationAsync(db, business.Id, widget.Id, customer.Id, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var message2 = await CreateMessageAsync(db, business.Id, conversation2.Id, DateTimeOffset.UtcNow);

        db.ExternalIdentities.Add(new ExternalIdentity
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id,
            CustomerId = customer.Id,
            Channel = "Website",
            VerificationMethod = "SignedToken",
            ExternalUserId = "ext-123",
            LinkedAt = DateTimeOffset.UtcNow,
        });
        db.FlaggedCustomerActivityLogs.Add(new FlaggedCustomerActivityLog
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id,
            CustomerId = customer.Id,
            ToolName = "getOrder",
            Actor = ActorType.Ai,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.CustomerExtendedLoggingHistories.Add(new CustomerExtendedLoggingHistory
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id,
            CustomerId = customer.Id,
            EnabledByUserId = staffUser.Id,
            EnabledAt = DateTimeOffset.UtcNow,
        });
        db.VerificationCodes.Add(new VerificationCode
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id,
            CustomerId = customer.Id,
            ConversationId = conversation1.Id,
            Channel = "Website",
            CodeHash = "hash",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
        });
        await db.SaveChangesAsync();

        await erasureService.EraseCustomerAsync(customer.Id);

        // Customer: anonymized in place.
        var customerAfter = await db.Customers.IgnoreQueryFilters().FirstOrDefaultAsync(c => c.Id == customer.Id);
        Assert.NotNull(customerAfter);
        Assert.True(customerAfter!.IsAnonymized);
        Assert.NotNull(customerAfter.AnonymizedAt);
        Assert.Null(customerAfter.Email);
        Assert.Null(customerAfter.Name);
        Assert.Null(customerAfter.ExternalId);

        // Identity/security/logging rows: explicitly deleted.
        Assert.False(await db.ExternalIdentities.IgnoreQueryFilters().AnyAsync(e => e.CustomerId == customer.Id));
        Assert.False(await db.FlaggedCustomerActivityLogs.IgnoreQueryFilters().AnyAsync(f => f.CustomerId == customer.Id));
        Assert.False(await db.CustomerExtendedLoggingHistories.IgnoreQueryFilters().AnyAsync(h => h.CustomerId == customer.Id));
        Assert.False(await db.VerificationCodes.IgnoreQueryFilters().AnyAsync(v => v.CustomerId == customer.Id));

        // Every conversation: purged (shell survives, Messages gone).
        foreach (var (conversationId, messageId) in new[] { (conversation1.Id, message1.Id), (conversation2.Id, message2.Id) })
        {
            var conv = await db.Conversations.IgnoreQueryFilters().FirstOrDefaultAsync(c => c.Id == conversationId);
            Assert.NotNull(conv);
            Assert.True(conv!.IsAnonymized);
            Assert.Null(conv.VisitorSessionId);

            var msgExists = await db.Messages.IgnoreQueryFilters().AnyAsync(m => m.Id == messageId);
            Assert.False(msgExists);
        }
    }

    [Fact]
    public async Task EraseCustomer_UnknownCustomerId_IsSafeNoOp()
    {
        await using var scope = _provider.CreateAsyncScope();
        var erasureService = scope.ServiceProvider.GetRequiredService<ICustomerErasureService>();

        var exception = await Record.ExceptionAsync(() => erasureService.EraseCustomerAsync(Guid.NewGuid()));
        Assert.Null(exception);
    }

    // ── 4. Retention expiry ─────────────────────────────────────────

    [Fact]
    public async Task RetentionEnforcement_PurgesOnlyExpiredConversations()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var retentionService = scope.ServiceProvider.GetRequiredService<IRetentionEnforcementService>();

        const int retentionDays = 30;
        var business = await CreateBusinessAsync(db, retentionDays);
        var widget = await CreateWidgetAsync(db, business.Id);

        var now = DateTimeOffset.UtcNow;
        var wellWithinRetention = now.AddDays(-1);
        var wellPastRetention = now.AddDays(-(retentionDays + 5));

        // Recent LastMessageAt — must survive.
        var recentByLastMessage = await CreateConversationAsync(db, business.Id, widget.Id, null, wellPastRetention, wellWithinRetention);

        // Expired LastMessageAt — must be purged.
        var expiredByLastMessage = await CreateConversationAsync(db, business.Id, widget.Id, null, wellPastRetention, wellPastRetention);

        // No messages, expired CreatedAt (LastMessageAt null) — must be purged via the CreatedAt fallback.
        var expiredByCreatedAtNoMessages = await CreateConversationAsync(db, business.Id, widget.Id, null, wellPastRetention, null);

        // No messages, recent CreatedAt (LastMessageAt null) — must survive.
        var recentByCreatedAtNoMessages = await CreateConversationAsync(db, business.Id, widget.Id, null, wellWithinRetention, null);

        await retentionService.EnforceRetentionAsync();

        async Task<bool> IsAnonymized(Guid conversationId)
        {
            var c = await db.Conversations.IgnoreQueryFilters().FirstAsync(x => x.Id == conversationId);
            return c.IsAnonymized;
        }

        Assert.False(await IsAnonymized(recentByLastMessage.Id));
        Assert.True(await IsAnonymized(expiredByLastMessage.Id));
        Assert.True(await IsAnonymized(expiredByCreatedAtNoMessages.Id));
        Assert.False(await IsAnonymized(recentByCreatedAtNoMessages.Id));
    }

    // ── 5. Message cascade ──────────────────────────────────────────

    [Fact]
    public async Task PurgeConversation_MessageDeletion_CascadesToMessageKnowledgeSource()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var purgeService = scope.ServiceProvider.GetRequiredService<IConversationPurgeService>();

        var business = await CreateBusinessAsync(db);
        var widget = await CreateWidgetAsync(db, business.Id);
        var conversation = await CreateConversationAsync(db, business.Id, widget.Id, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var message = await CreateMessageAsync(db, business.Id, conversation.Id, DateTimeOffset.UtcNow);

        var sourceId = Guid.NewGuid();
        db.MessageKnowledgeSources.Add(new MessageKnowledgeSource
        {
            Id = sourceId,
            BusinessId = business.Id,
            MessageId = message.Id,
            KnowledgeDocumentId = null,
            KnowledgeChunkId = null,
            DocumentTitleSnapshot = "Refund Policy",
            ChunkContentSnapshot = "Refunds are processed within 5 business days.",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        Assert.True(await db.MessageKnowledgeSources.IgnoreQueryFilters().AnyAsync(s => s.Id == sourceId));

        // No app code deletes MessageKnowledgeSource directly anywhere in
        // ConversationPurgeService — this proves the DB's own FK cascade
        // (Cascade from Message) is what removes it.
        await purgeService.PurgeConversationAsync(conversation.Id);

        Assert.False(await db.MessageKnowledgeSources.IgnoreQueryFilters().AnyAsync(s => s.Id == sourceId));
        Assert.False(await db.Messages.IgnoreQueryFilters().AnyAsync(m => m.Id == message.Id));
    }

    // ── 6. Conversation analytics shell ─────────────────────────────

    [Fact]
    public async Task PurgeConversation_LeavesQueryableAnalyticsShell()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var purgeService = scope.ServiceProvider.GetRequiredService<IConversationPurgeService>();

        var business = await CreateBusinessAsync(db);
        var widget = await CreateWidgetAsync(db, business.Id);
        var createdAt = DateTimeOffset.UtcNow.AddDays(-10);
        var lastMessageAt = DateTimeOffset.UtcNow.AddDays(-9);
        var conversation = await CreateConversationAsync(db, business.Id, widget.Id, null, createdAt, lastMessageAt);
        conversation.Status = ConversationStatus.Closed;
        conversation.Tags = ["billing", "refund"];
        await db.SaveChangesAsync();
        await CreateMessageAsync(db, business.Id, conversation.Id, lastMessageAt);

        await purgeService.PurgeConversationAsync(conversation.Id);

        var shell = await db.Conversations.IgnoreQueryFilters().FirstOrDefaultAsync(c => c.Id == conversation.Id);
        Assert.NotNull(shell);

        // Gone: identifying/session information.
        Assert.Null(shell!.VisitorSessionId);
        Assert.False(await db.Messages.IgnoreQueryFilters().AnyAsync(m => m.ConversationId == conversation.Id));

        // Remains: analytics-relevant fields, exactly as set before purge.
        Assert.Equal("Website", shell.Channel);
        Assert.Equal(ConversationStatus.Closed, shell.Status);
        Assert.Equal(["billing", "refund"], shell.Tags);
        Assert.Equal(createdAt, shell.CreatedAt);
        Assert.Equal(lastMessageAt, shell.LastMessageAt);
        Assert.True(shell.IsAnonymized);
    }

    // ── 7. Tenant isolation ─────────────────────────────────────────

    [Fact]
    public async Task EraseCustomer_NeverModifiesAnotherBusinesssData()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var erasureService = scope.ServiceProvider.GetRequiredService<ICustomerErasureService>();

        var businessA = await CreateBusinessAsync(db);
        var widgetA = await CreateWidgetAsync(db, businessA.Id);
        var customerA = await CreateCustomerAsync(db, businessA.Id);
        var conversationA = await CreateConversationAsync(db, businessA.Id, widgetA.Id, customerA.Id, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var messageA = await CreateMessageAsync(db, businessA.Id, conversationA.Id, DateTimeOffset.UtcNow);

        var businessB = await CreateBusinessAsync(db);
        var widgetB = await CreateWidgetAsync(db, businessB.Id);
        var customerB = await CreateCustomerAsync(db, businessB.Id);
        var conversationB = await CreateConversationAsync(db, businessB.Id, widgetB.Id, customerB.Id, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var messageB = await CreateMessageAsync(db, businessB.Id, conversationB.Id, DateTimeOffset.UtcNow);

        await erasureService.EraseCustomerAsync(customerA.Id);

        // ---- Service level: Business A actually got erased ----
        var customerAAfter = await db.Customers.IgnoreQueryFilters().FirstAsync(c => c.Id == customerA.Id);
        Assert.True(customerAAfter.IsAnonymized);

        // ---- Service level: Business B is completely untouched ----
        var customerBAfter = await db.Customers.IgnoreQueryFilters().FirstAsync(c => c.Id == customerB.Id);
        Assert.False(customerBAfter.IsAnonymized);
        Assert.Equal(customerB.Email, customerBAfter.Email);
        Assert.Equal(customerB.Name, customerBAfter.Name);

        var conversationBAfter = await db.Conversations.IgnoreQueryFilters().FirstAsync(c => c.Id == conversationB.Id);
        Assert.False(conversationBAfter.IsAnonymized);
        Assert.Equal(conversationB.VisitorSessionId, conversationBAfter.VisitorSessionId);

        Assert.True(await db.Messages.IgnoreQueryFilters().AnyAsync(m => m.Id == messageB.Id));

        // ---- Database level: raw SQL row counts for Business B unchanged ----
        var messageCountB = await db.Database
            .SqlQuery<int>($"SELECT COUNT(*)::int AS \"Value\" FROM \"Messages\" WHERE \"BusinessId\" = {businessB.Id}")
            .SingleAsync();
        Assert.Equal(1, messageCountB);

        var conversationCountB = await db.Database
            .SqlQuery<int>($"SELECT COUNT(*)::int AS \"Value\" FROM \"Conversations\" WHERE \"BusinessId\" = {businessB.Id} AND \"IsAnonymized\" = false")
            .SingleAsync();
        Assert.Equal(1, conversationCountB);

        // Sanity check the other direction too — Business A's message really is gone.
        var messageCountA = await db.Database
            .SqlQuery<int>($"SELECT COUNT(*)::int AS \"Value\" FROM \"Messages\" WHERE \"BusinessId\" = {businessA.Id}")
            .SingleAsync();
        Assert.Equal(0, messageCountA);
        Assert.False(await db.Messages.IgnoreQueryFilters().AnyAsync(m => m.Id == messageA.Id));
    }

    // ── 8. CustomerExtendedLoggingHistory ───────────────────────────

    [Fact]
    public async Task EraseCustomer_RemovesCustomerExtendedLoggingHistory()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var erasureService = scope.ServiceProvider.GetRequiredService<ICustomerErasureService>();

        var business = await CreateBusinessAsync(db);
        var customer = await CreateCustomerAsync(db, business.Id);
        var staffUser = await CreateStaffUserAsync(db, business.Id);

        var historyId = Guid.NewGuid();
        db.CustomerExtendedLoggingHistories.Add(new CustomerExtendedLoggingHistory
        {
            Id = historyId,
            BusinessId = business.Id,
            CustomerId = customer.Id,
            EnabledByUserId = staffUser.Id,
            EnabledAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        await erasureService.EraseCustomerAsync(customer.Id);

        Assert.False(await db.CustomerExtendedLoggingHistories.IgnoreQueryFilters().AnyAsync(h => h.Id == historyId));
    }

    // ── 9. VerificationCode ─────────────────────────────────────────

    [Fact]
    public async Task EraseCustomer_RemovesCustomersVerificationCodes()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var erasureService = scope.ServiceProvider.GetRequiredService<ICustomerErasureService>();

        var business = await CreateBusinessAsync(db);
        var widget = await CreateWidgetAsync(db, business.Id);
        var customer = await CreateCustomerAsync(db, business.Id);
        var conversation = await CreateConversationAsync(db, business.Id, widget.Id, customer.Id, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        var codeId = Guid.NewGuid();
        db.VerificationCodes.Add(new VerificationCode
        {
            Id = codeId,
            BusinessId = business.Id,
            CustomerId = customer.Id,
            ConversationId = conversation.Id,
            Channel = "Website",
            CodeHash = "hash",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
        });
        await db.SaveChangesAsync();

        await erasureService.EraseCustomerAsync(customer.Id);

        Assert.False(await db.VerificationCodes.IgnoreQueryFilters().AnyAsync(v => v.Id == codeId));
    }

    [Fact]
    public async Task VerificationCodeCleanup_RemovesOnlyOldExpiredCodes_LeavesValidAndRecentCodes()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var cleanupService = scope.ServiceProvider.GetRequiredService<IVerificationCodeCleanupService>();

        var business = await CreateBusinessAsync(db);
        var widget = await CreateWidgetAsync(db, business.Id);
        var customer = await CreateCustomerAsync(db, business.Id);
        var conversation = await CreateConversationAsync(db, business.Id, widget.Id, customer.Id, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        var now = DateTimeOffset.UtcNow;

        // Old expired — must be removed (ExpiresAt well over 30 days ago).
        var oldExpiredId = Guid.NewGuid();
        db.VerificationCodes.Add(new VerificationCode
        {
            Id = oldExpiredId,
            BusinessId = business.Id,
            CustomerId = customer.Id,
            ConversationId = conversation.Id,
            Channel = "Website",
            CodeHash = "hash-old-expired",
            ExpiresAt = now.AddDays(-40),
        });

        // Recently expired — within the 30-day floor, must survive this pass.
        var recentlyExpiredId = Guid.NewGuid();
        db.VerificationCodes.Add(new VerificationCode
        {
            Id = recentlyExpiredId,
            BusinessId = business.Id,
            CustomerId = customer.Id,
            ConversationId = conversation.Id,
            Channel = "Website",
            CodeHash = "hash-recently-expired",
            ExpiresAt = now.AddDays(-1),
        });

        // Still valid — must survive.
        var validId = Guid.NewGuid();
        db.VerificationCodes.Add(new VerificationCode
        {
            Id = validId,
            BusinessId = business.Id,
            CustomerId = customer.Id,
            ConversationId = conversation.Id,
            Channel = "Website",
            CodeHash = "hash-valid",
            ExpiresAt = now.AddMinutes(10),
        });

        await db.SaveChangesAsync();

        await cleanupService.CleanupExpiredAsync();

        Assert.False(await db.VerificationCodes.IgnoreQueryFilters().AnyAsync(v => v.Id == oldExpiredId));
        Assert.True(await db.VerificationCodes.IgnoreQueryFilters().AnyAsync(v => v.Id == recentlyExpiredId));
        Assert.True(await db.VerificationCodes.IgnoreQueryFilters().AnyAsync(v => v.Id == validId));
    }

    // ── 10. GDPR Phase 2 — transaction ownership (Option B) ──────────

    /// <summary>
    /// Direct, literal proof of the exact bug class the design's §1.3
    /// documents: EF Core's DatabaseFacade.BeginTransactionAsync() throws
    /// InvalidOperationException when called on a DbContext that already
    /// has an active transaction. Before Option B, calling
    /// PurgeConversationAsync from inside an ambient transaction on the
    /// same context hit exactly this failure.
    /// </summary>
    [Fact]
    public async Task PurgeConversation_CalledInsideAmbientTransaction_DoesNotThrow()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var purgeService = scope.ServiceProvider.GetRequiredService<IConversationPurgeService>();

        var business = await CreateBusinessAsync(db);
        var widget = await CreateWidgetAsync(db, business.Id);
        var conversation = await CreateConversationAsync(db, business.Id, widget.Id, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        await CreateMessageAsync(db, business.Id, conversation.Id, DateTimeOffset.UtcNow);

        await using var ambientTransaction = await db.Database.BeginTransactionAsync();

        var exception = await Record.ExceptionAsync(() => purgeService.PurgeConversationAsync(conversation.Id));
        Assert.Null(exception);

        await ambientTransaction.CommitAsync();
    }

    /// <summary>
    /// Proves the joined path has NO transaction boundary of its own — an
    /// ambient rollback (the CALLER's, not the service's) undoes everything
    /// this method did, including the Approval cleanup, exactly as §1's
    /// Option B requires.
    /// </summary>
    [Fact]
    public async Task PurgeConversation_JoinsAmbientTransaction_RollsBackWithCaller()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var purgeService = scope.ServiceProvider.GetRequiredService<IConversationPurgeService>();

        var business = await CreateBusinessAsync(db);
        var widget = await CreateWidgetAsync(db, business.Id);
        var customer = await CreateCustomerAsync(db, business.Id);
        var conversation = await CreateConversationAsync(db, business.Id, widget.Id, customer.Id, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var message = await CreateMessageAsync(db, business.Id, conversation.Id, DateTimeOffset.UtcNow);
        var approval = await CreateApprovalAsync(db, business.Id, conversation.Id, customer.Id, ApprovalStatus.Pending);

        await using (var ambientTransaction = await db.Database.BeginTransactionAsync())
        {
            await purgeService.PurgeConversationAsync(conversation.Id);
            await ambientTransaction.RollbackAsync();
        }

        // Verified via a SEPARATE scope/AppDbContext, not the one that ran
        // the purge: `db` above still has the mutated Conversation entity
        // tracked in memory from PurgeConversationAsync's own SaveChangesAsync
        // call — EF Core's change tracker does not know the ambient
        // transaction was subsequently rolled back and does not revert
        // tracked property values on its own, so re-querying the same
        // entity on the same context would return the stale in-memory
        // values (IsAnonymized == true) rather than the true, rolled-back
        // database state. A fresh context has nothing tracked and reads
        // the real row.
        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();

        var messageAfter = await verifyDb.Messages.IgnoreQueryFilters().FirstOrDefaultAsync(m => m.Id == message.Id);
        Assert.NotNull(messageAfter);

        var conversationAfter = await verifyDb.Conversations.IgnoreQueryFilters().FirstOrDefaultAsync(c => c.Id == conversation.Id);
        Assert.NotNull(conversationAfter);
        Assert.False(conversationAfter!.IsAnonymized);
        Assert.NotNull(conversationAfter.VisitorSessionId);

        Assert.True(await verifyDb.Approvals.IgnoreQueryFilters().AnyAsync(a => a.Id == approval.Id));
    }

    /// <summary>
    /// Proves the joined path commits together with the caller's own
    /// transaction — the purge's Message/Notification/Approval deletes and
    /// the Conversation mutation all land together with whatever else the
    /// caller did in the same ambient transaction. Also exercises Decision
    /// 4's Approval cleanup in the JOINED path specifically (Test D below
    /// covers the standalone/owns path) — Consumed survives, non-Consumed
    /// is deleted.
    /// </summary>
    [Fact]
    public async Task PurgeConversation_JoinsAmbientTransaction_CommitsWithCaller()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var purgeService = scope.ServiceProvider.GetRequiredService<IConversationPurgeService>();

        var business = await CreateBusinessAsync(db);
        var widget = await CreateWidgetAsync(db, business.Id);
        var customer = await CreateCustomerAsync(db, business.Id);
        var conversation = await CreateConversationAsync(db, business.Id, widget.Id, customer.Id, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var message = await CreateMessageAsync(db, business.Id, conversation.Id, DateTimeOffset.UtcNow);
        var pendingApproval = await CreateApprovalAsync(db, business.Id, conversation.Id, customer.Id, ApprovalStatus.Pending);
        var consumedApproval = await CreateApprovalAsync(db, business.Id, conversation.Id, customer.Id, ApprovalStatus.Consumed);

        await using (var ambientTransaction = await db.Database.BeginTransactionAsync())
        {
            await purgeService.PurgeConversationAsync(conversation.Id);
            await ambientTransaction.CommitAsync();
        }

        Assert.False(await db.Messages.IgnoreQueryFilters().AnyAsync(m => m.Id == message.Id));

        var conversationAfter = await db.Conversations.IgnoreQueryFilters().FirstOrDefaultAsync(c => c.Id == conversation.Id);
        Assert.NotNull(conversationAfter);
        Assert.True(conversationAfter!.IsAnonymized);
        Assert.Null(conversationAfter.VisitorSessionId);

        Assert.False(await db.Approvals.IgnoreQueryFilters().AnyAsync(a => a.Id == pendingApproval.Id));
        Assert.True(await db.Approvals.IgnoreQueryFilters().AnyAsync(a => a.Id == consumedApproval.Id));
    }

    // ── 11. GDPR Phase 2 — Approval cleanup (Decision 4), owns branch ─

    /// <summary>
    /// Standalone call (no ambient transaction) — proves the "owns" branch
    /// also gets the new Approval cleanup, not only the "joins" branch
    /// exercised by Test C above. Against the real dev DB, directly proving
    /// Decision 4's "confirmed zero FK/cascade/transaction risk" claim
    /// against actual Postgres behavior.
    /// </summary>
    [Fact]
    public async Task PurgeConversation_ApprovalCleanup_ConsumedSurvives_NonConsumedDeleted()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var purgeService = scope.ServiceProvider.GetRequiredService<IConversationPurgeService>();

        var business = await CreateBusinessAsync(db);
        var widget = await CreateWidgetAsync(db, business.Id);
        var customer = await CreateCustomerAsync(db, business.Id);
        var conversation = await CreateConversationAsync(db, business.Id, widget.Id, customer.Id, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        await CreateMessageAsync(db, business.Id, conversation.Id, DateTimeOffset.UtcNow);

        var pending = await CreateApprovalAsync(db, business.Id, conversation.Id, customer.Id, ApprovalStatus.Pending);
        var denied = await CreateApprovalAsync(db, business.Id, conversation.Id, customer.Id, ApprovalStatus.Denied);
        var approvedNeverConsumed = await CreateApprovalAsync(db, business.Id, conversation.Id, customer.Id, ApprovalStatus.Approved);
        var consumed = await CreateApprovalAsync(db, business.Id, conversation.Id, customer.Id, ApprovalStatus.Consumed);

        await purgeService.PurgeConversationAsync(conversation.Id);

        Assert.False(await db.Approvals.IgnoreQueryFilters().AnyAsync(a => a.Id == pending.Id));
        Assert.False(await db.Approvals.IgnoreQueryFilters().AnyAsync(a => a.Id == denied.Id));
        Assert.False(await db.Approvals.IgnoreQueryFilters().AnyAsync(a => a.Id == approvedNeverConsumed.Id));
        Assert.True(await db.Approvals.IgnoreQueryFilters().AnyAsync(a => a.Id == consumed.Id));
    }

    // ── Test helpers ─────────────────────────────────────────────────

    private class FixedTenantAccessor(Guid? businessId) : ICurrentTenantAccessor
    {
        public Guid? BusinessId => businessId;
    }

    private class FailAtCheckpoint(string checkpoint) : IPurgeFailurePoint
    {
        public void MaybeFail(string checkpointName)
        {
            if (checkpointName == checkpoint)
            {
                throw new InvalidOperationException($"Injected test failure at '{checkpointName}'.");
            }
        }
    }
}
