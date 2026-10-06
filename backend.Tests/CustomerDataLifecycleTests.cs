using backend.Constants;
using backend.Data;
using backend.Enums;
using backend.Interfaces;
using backend.Models;
using backend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
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
    private string _adoConnectionString = null!;
    private readonly List<Guid> _businessIdsToClean = [];
    private readonly List<Guid> _userIdsToClean = [];

    public Task InitializeAsync()
    {
        var configuration = new ConfigurationBuilder()
            .AddUserSecrets<Program>()
            .Build();
        var connectionUri = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection not found via user-secrets.");

        // Identity-binding/erasure-integrity Phase 3's lock-acquisition test
        // needs a raw, independent connection (not routed through the app's
        // own DI-managed AppDbContext) to act as a deterministic
        // advisory-lock barrier — same technique
        // VerificationCodeServiceTests.cs already established for its own
        // ForVerificationCode concurrency races.
        _adoConnectionString = PostgresConnectionString.FromUri(connectionUri);

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
            // Identity-binding/erasure-integrity Phase 3 — CustomerErasureService
            // now depends on ICustomerErasureCheckpoint (Phase 0's pause/fail
            // test seam, no-op in production).
            .AddScoped<ICustomerErasureCheckpoint, NoOpCustomerErasureCheckpoint>()
            // GDPR Phase 3 — RetentionEnforcementService now depends on
            // IAuditLogService (ConversationRetentionPurged audit write).
            .AddScoped<IAuditLogService, AuditLogService>()
            .BuildServiceProvider();

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await TestBusinessCleanup.DeleteThenDisposeAsync(_businessIdsToClean, _provider);
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

    /// <summary>
    /// GDPR Phase 3 — minimal valid Ticket fixture for
    /// RetentionEnforcementService's Decision 3 active-Ticket-exemption
    /// tests below. Same shape as
    /// ConversationInactivityCloseServiceTests.CreateTicketAsync (this
    /// project's own established precedent for seeding a Ticket).
    /// </summary>
    private static async Task<Ticket> CreateTicketAsync(
        AppDbContext db, Guid businessId, Guid customerId, Guid conversationId, TicketStatus status)
    {
        var ticket = new Ticket
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            CustomerId = customerId,
            ConversationId = conversationId,
            Status = status,
            Priority = TicketPriority.Medium,
            PriorityReason = TicketPriorityReasons.ManualEscalation,
            Channel = "Website",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Tickets.Add(ticket);
        await db.SaveChangesAsync();
        return ticket;
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

        await erasureService.EraseCustomerAsync(customer.Id, staffUser.Id);

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

        var exception = await Record.ExceptionAsync(() => erasureService.EraseCustomerAsync(Guid.NewGuid(), Guid.NewGuid()));
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
        var staffUserA = await CreateStaffUserAsync(db, businessA.Id);
        var conversationA = await CreateConversationAsync(db, businessA.Id, widgetA.Id, customerA.Id, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var messageA = await CreateMessageAsync(db, businessA.Id, conversationA.Id, DateTimeOffset.UtcNow);

        var businessB = await CreateBusinessAsync(db);
        var widgetB = await CreateWidgetAsync(db, businessB.Id);
        var customerB = await CreateCustomerAsync(db, businessB.Id);
        var conversationB = await CreateConversationAsync(db, businessB.Id, widgetB.Id, customerB.Id, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var messageB = await CreateMessageAsync(db, businessB.Id, conversationB.Id, DateTimeOffset.UtcNow);

        await erasureService.EraseCustomerAsync(customerA.Id, staffUserA.Id);

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

        await erasureService.EraseCustomerAsync(customer.Id, staffUser.Id);

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
        var staffUser = await CreateStaffUserAsync(db, business.Id);
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

        await erasureService.EraseCustomerAsync(customer.Id, staffUser.Id);

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

    // ── 12. GDPR Phase 3 — RetentionEnforcementService atomic claim ──

    /// <summary>
    /// Decision 3 — an active Ticket created AFTER discovery but BEFORE
    /// this conversation's claim runs must still exempt it (the discovery
    /// query's own Ticket filter alone is insufficient — see the class doc
    /// comment). Repeated for all three of Decision 3's named active
    /// statuses, proving the exemption is not narrowed to a subset of
    /// "Status != Resolved".
    /// </summary>
    [Theory]
    [InlineData(TicketStatus.Open)]
    [InlineData(TicketStatus.InProgress)]
    [InlineData(TicketStatus.Reopened)]
    public async Task RetentionEnforcement_TicketCreatedBetweenDiscoveryAndClaim_NotPurged(TicketStatus activeStatus)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var retentionService = (RetentionEnforcementService)scope.ServiceProvider.GetRequiredService<IRetentionEnforcementService>();

        const int retentionDays = 30;
        var business = await CreateBusinessAsync(db, retentionDays);
        var widget = await CreateWidgetAsync(db, business.Id);
        var customer = await CreateCustomerAsync(db, business.Id);
        var now = DateTimeOffset.UtcNow;
        var wellPastRetention = now.AddDays(-(retentionDays + 5));
        var conversation = await CreateConversationAsync(db, business.Id, widget.Id, customer.Id, wellPastRetention, wellPastRetention);
        await CreateMessageAsync(db, business.Id, conversation.Id, wellPastRetention);

        // Simulates the discovery→claim gap via a second, independent scope
        // (and therefore AppDbContext) — no Ticket existed at discovery
        // time; one is created here, committed, before the claim below runs.
        await using (var otherScope = _provider.CreateAsyncScope())
        {
            var otherDb = otherScope.ServiceProvider.GetRequiredService<AppDbContext>();
            await CreateTicketAsync(otherDb, business.Id, customer.Id, conversation.Id, activeStatus);
        }

        var claimed = await retentionService.TryPurgeConversationAsync(business.Id, conversation.Id, CancellationToken.None);

        Assert.False(claimed);
        Assert.True(await db.Messages.IgnoreQueryFilters().AnyAsync(m => m.ConversationId == conversation.Id));
        var conversationAfter = await db.Conversations.IgnoreQueryFilters().SingleAsync(c => c.Id == conversation.Id);
        Assert.False(conversationAfter.IsAnonymized);
        Assert.Equal(0, await db.AuditLogs.IgnoreQueryFilters()
            .CountAsync(a => a.EntityId == conversation.Id.ToString() && a.EventType == AuditEventTypes.ConversationRetentionPurged));
    }

    /// <summary>
    /// The other half of Decision 3's own text: a Resolved Ticket must NOT
    /// exempt an otherwise-eligible conversation — proves the exemption
    /// isn't accidentally over-broad.
    /// </summary>
    [Fact]
    public async Task RetentionEnforcement_ResolvedTicket_DoesNotBlockPurge()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var retentionService = (RetentionEnforcementService)scope.ServiceProvider.GetRequiredService<IRetentionEnforcementService>();

        const int retentionDays = 30;
        var business = await CreateBusinessAsync(db, retentionDays);
        var widget = await CreateWidgetAsync(db, business.Id);
        var customer = await CreateCustomerAsync(db, business.Id);
        var now = DateTimeOffset.UtcNow;
        var wellPastRetention = now.AddDays(-(retentionDays + 5));
        var conversation = await CreateConversationAsync(db, business.Id, widget.Id, customer.Id, wellPastRetention, wellPastRetention);
        await CreateMessageAsync(db, business.Id, conversation.Id, wellPastRetention);
        await CreateTicketAsync(db, business.Id, customer.Id, conversation.Id, TicketStatus.Resolved);

        var claimed = await retentionService.TryPurgeConversationAsync(business.Id, conversation.Id, CancellationToken.None);

        Assert.True(claimed);
        var conversationAfter = await db.Conversations.IgnoreQueryFilters().SingleAsync(c => c.Id == conversation.Id);
        Assert.True(conversationAfter.IsAnonymized);
    }

    /// <summary>
    /// Proves §2.3's "fresh re-read of Business.RetentionDays, never the
    /// discovery-time snapshot" requirement is actually load-bearing — a
    /// business widening its retention window between discovery and claim
    /// must be honored, not silently missed.
    /// </summary>
    [Fact]
    public async Task RetentionEnforcement_RetentionDaysChangedBetweenDiscoveryAndClaim_NotPurged()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var retentionService = (RetentionEnforcementService)scope.ServiceProvider.GetRequiredService<IRetentionEnforcementService>();

        const int retentionDays = 30;
        var business = await CreateBusinessAsync(db, retentionDays);
        var widget = await CreateWidgetAsync(db, business.Id);
        var now = DateTimeOffset.UtcNow;
        // Just past the original 30-day cutoff, but NOT past a widened
        // 180-day one.
        var justPastOriginalCutoff = now.AddDays(-31);
        var conversation = await CreateConversationAsync(db, business.Id, widget.Id, null, justPastOriginalCutoff, justPastOriginalCutoff);
        await CreateMessageAsync(db, business.Id, conversation.Id, justPastOriginalCutoff);

        await using (var otherScope = _provider.CreateAsyncScope())
        {
            var otherDb = otherScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var businessRow = await otherDb.Businesses.IgnoreQueryFilters().SingleAsync(b => b.Id == business.Id);
            businessRow.RetentionDays = 180;
            businessRow.UpdatedAt = now;
            await otherDb.SaveChangesAsync();
        }

        var claimed = await retentionService.TryPurgeConversationAsync(business.Id, conversation.Id, CancellationToken.None);

        Assert.False(claimed);
        var conversationAfter = await db.Conversations.IgnoreQueryFilters().SingleAsync(c => c.Id == conversation.Id);
        Assert.False(conversationAfter.IsAnonymized);
    }

    /// <summary>
    /// A Message arriving (or, as here, a zero-message conversation's
    /// LastMessageAt being populated) between discovery and claim must
    /// re-exclude the conversation, not rely on the discovery-time
    /// snapshot of staleness.
    /// </summary>
    [Fact]
    public async Task RetentionEnforcement_MessageArrivesBetweenDiscoveryAndClaim_NotPurged()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var retentionService = (RetentionEnforcementService)scope.ServiceProvider.GetRequiredService<IRetentionEnforcementService>();

        const int retentionDays = 30;
        var business = await CreateBusinessAsync(db, retentionDays);
        var widget = await CreateWidgetAsync(db, business.Id);
        var now = DateTimeOffset.UtcNow;
        var wellPastRetention = now.AddDays(-(retentionDays + 5));
        // Zero-message, CreatedAt-fallback case — mirrors the discovery
        // query's own predicate shape.
        var conversation = await CreateConversationAsync(db, business.Id, widget.Id, null, wellPastRetention, null);

        await using (var otherScope = _provider.CreateAsyncScope())
        {
            var otherDb = otherScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var conversationRow = await otherDb.Conversations.IgnoreQueryFilters().SingleAsync(c => c.Id == conversation.Id);
            conversationRow.LastMessageAt = now;
            conversationRow.UpdatedAt = now;
            await otherDb.SaveChangesAsync();
        }

        var claimed = await retentionService.TryPurgeConversationAsync(business.Id, conversation.Id, CancellationToken.None);

        Assert.False(claimed);
        var conversationAfter = await db.Conversations.IgnoreQueryFilters().SingleAsync(c => c.Id == conversation.Id);
        Assert.False(conversationAfter.IsAnonymized);
    }

    /// <summary>
    /// The claim, the purge, and the audit write all share one transaction
    /// (Decision 5) — an injected failure after the claim has already
    /// succeeded must roll back all three together: nothing partial
    /// survives. Uses the "before-commit" checkpoint (fires after every
    /// delete and the Conversation mutation's in-memory assignment, right
    /// before SaveChangesAsync) — the strongest available proof that a
    /// fully-prepared-but-uncommitted transaction rolls back completely.
    /// </summary>
    [Fact]
    public async Task RetentionEnforcement_FailureInjectedAfterClaimBeforeCommit_RollsBackPurgeAndAudit()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var auditLogService = scope.ServiceProvider.GetRequiredService<IAuditLogService>();

        const int retentionDays = 30;
        var business = await CreateBusinessAsync(db, retentionDays);
        var widget = await CreateWidgetAsync(db, business.Id);
        var now = DateTimeOffset.UtcNow;
        var wellPastRetention = now.AddDays(-(retentionDays + 5));
        var conversation = await CreateConversationAsync(db, business.Id, widget.Id, null, wellPastRetention, wellPastRetention);
        await CreateMessageAsync(db, business.Id, conversation.Id, wellPastRetention);

        var failingPurgeService = new ConversationPurgeService(db, new FailAtCheckpoint("before-commit"));
        var failingRetentionService = new RetentionEnforcementService(db, failingPurgeService, auditLogService);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => failingRetentionService.TryPurgeConversationAsync(business.Id, conversation.Id, CancellationToken.None));

        // Verified via a SEPARATE scope, not `db`: PurgeConversationAsync's
        // own in-memory property assignments (IsAnonymized = true, etc.)
        // happen before the injected failure point fires, and EF Core's
        // change tracker does not revert tracked property values when a
        // transaction is subsequently rolled back — re-querying the same
        // entity on `db` would return that stale in-memory state rather
        // than the true, rolled-back database row (identical reasoning to
        // this file's Phase 2 PurgeConversation_JoinsAmbientTransaction_RollsBackWithCaller
        // test). A fresh context has nothing tracked and reads the real row.
        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();

        Assert.True(await verifyDb.Messages.IgnoreQueryFilters().AnyAsync(m => m.ConversationId == conversation.Id));

        var conversationAfter = await verifyDb.Conversations.IgnoreQueryFilters().SingleAsync(c => c.Id == conversation.Id);
        Assert.False(conversationAfter.IsAnonymized);

        var auditCount = await verifyDb.AuditLogs.IgnoreQueryFilters()
            .CountAsync(a => a.EntityId == conversation.Id.ToString() && a.EventType == AuditEventTypes.ConversationRetentionPurged);
        Assert.Equal(0, auditCount);
    }

    /// <summary>
    /// A retry against an already-purged conversation is ordinary
    /// idempotency, not an error — claimed == 0, no duplicate audit row.
    /// </summary>
    [Fact]
    public async Task RetentionEnforcement_AlreadyPurgedByPriorRun_ClaimedZero_NoDuplicateAudit()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var retentionService = (RetentionEnforcementService)scope.ServiceProvider.GetRequiredService<IRetentionEnforcementService>();

        const int retentionDays = 30;
        var business = await CreateBusinessAsync(db, retentionDays);
        var widget = await CreateWidgetAsync(db, business.Id);
        var now = DateTimeOffset.UtcNow;
        var wellPastRetention = now.AddDays(-(retentionDays + 5));
        var conversation = await CreateConversationAsync(db, business.Id, widget.Id, null, wellPastRetention, wellPastRetention);
        await CreateMessageAsync(db, business.Id, conversation.Id, wellPastRetention);

        var firstAttempt = await retentionService.TryPurgeConversationAsync(business.Id, conversation.Id, CancellationToken.None);
        Assert.True(firstAttempt);

        var secondAttempt = await retentionService.TryPurgeConversationAsync(business.Id, conversation.Id, CancellationToken.None);
        Assert.False(secondAttempt);

        var auditCount = await db.AuditLogs.IgnoreQueryFilters()
            .CountAsync(a => a.EntityId == conversation.Id.ToString() && a.EventType == AuditEventTypes.ConversationRetentionPurged);
        Assert.Equal(1, auditCount);
    }

    /// <summary>
    /// Audit content proof — exercised through the full public
    /// EnforceRetentionAsync() path (discovery + claim together), not just
    /// the internal claim helper.
    /// </summary>
    [Fact]
    public async Task RetentionEnforcement_WritesCorrectAuditRow()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var retentionService = scope.ServiceProvider.GetRequiredService<IRetentionEnforcementService>();

        const int retentionDays = 30;
        var business = await CreateBusinessAsync(db, retentionDays);
        var widget = await CreateWidgetAsync(db, business.Id);
        var now = DateTimeOffset.UtcNow;
        var wellPastRetention = now.AddDays(-(retentionDays + 5));
        var conversation = await CreateConversationAsync(db, business.Id, widget.Id, null, wellPastRetention, wellPastRetention);
        await CreateMessageAsync(db, business.Id, conversation.Id, wellPastRetention);

        await retentionService.EnforceRetentionAsync();

        var auditRow = await db.AuditLogs.IgnoreQueryFilters()
            .SingleAsync(a => a.EntityId == conversation.Id.ToString() && a.EventType == AuditEventTypes.ConversationRetentionPurged);

        Assert.Equal(AuditEntityTypes.Conversation, auditRow.EntityType);
        Assert.Equal(ActorType.NivoraSystem, auditRow.Actor);
        Assert.Null(auditRow.ChangedByUserId);
        Assert.Null(auditRow.CorrelationId);
        Assert.Equal(business.Id, auditRow.BusinessId);
    }

    /// <summary>
    /// Requirement B — concurrent claims against the SAME conversation must
    /// be mutually exclusive. Two independent scopes (independent
    /// AppDbContexts/transactions) race for the same conversation via
    /// Task.WhenAll — real concurrent execution against the real dev DB,
    /// not two sequential calls. Postgres's own row-level locking inside
    /// the atomic claim's ExecuteUpdateAsync is what actually serializes
    /// the two attempts: whichever reaches the lock first wins; the other
    /// blocks until the first's transaction ends, then re-evaluates its own
    /// WHERE clause against the now-committed (already-anonymized) row and
    /// correctly finds zero matching rows.
    /// </summary>
    [Fact]
    public async Task RetentionEnforcement_ConcurrentClaims_OnlyOneSucceeds_PurgesExactlyOnce()
    {
        await using var seedScope = _provider.CreateAsyncScope();
        var seedDb = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();

        const int retentionDays = 30;
        var business = await CreateBusinessAsync(seedDb, retentionDays);
        var widget = await CreateWidgetAsync(seedDb, business.Id);
        var now = DateTimeOffset.UtcNow;
        var wellPastRetention = now.AddDays(-(retentionDays + 5));
        var conversation = await CreateConversationAsync(seedDb, business.Id, widget.Id, null, wellPastRetention, wellPastRetention);
        await CreateMessageAsync(seedDb, business.Id, conversation.Id, wellPastRetention);

        await using var scopeA = _provider.CreateAsyncScope();
        await using var scopeB = _provider.CreateAsyncScope();
        var serviceA = (RetentionEnforcementService)scopeA.ServiceProvider.GetRequiredService<IRetentionEnforcementService>();
        var serviceB = (RetentionEnforcementService)scopeB.ServiceProvider.GetRequiredService<IRetentionEnforcementService>();

        var taskA = serviceA.TryPurgeConversationAsync(business.Id, conversation.Id, CancellationToken.None);
        var taskB = serviceB.TryPurgeConversationAsync(business.Id, conversation.Id, CancellationToken.None);
        var results = await Task.WhenAll(taskA, taskB);

        Assert.Single(results, r => r);
        Assert.Single(results, r => !r);

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();

        var conversationAfter = await verifyDb.Conversations.IgnoreQueryFilters().SingleAsync(c => c.Id == conversation.Id);
        Assert.True(conversationAfter.IsAnonymized);
        Assert.False(await verifyDb.Messages.IgnoreQueryFilters().AnyAsync(m => m.ConversationId == conversation.Id));

        var auditCount = await verifyDb.AuditLogs.IgnoreQueryFilters()
            .CountAsync(a => a.EntityId == conversation.Id.ToString() && a.EventType == AuditEventTypes.ConversationRetentionPurged);
        Assert.Equal(1, auditCount);
    }

    // ── 13. GDPR Phase 4 — CustomerErasureService Mechanism 2 ─────────

    /// <summary>
    /// Requirement 1 (happy path) — the purge and its ConversationErasurePurged
    /// audit row are visible together after a successful erasure.
    /// </summary>
    [Fact]
    public async Task EraseCustomer_SingleConversation_PurgeAndAuditCommitTogether()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var erasureService = scope.ServiceProvider.GetRequiredService<ICustomerErasureService>();

        var business = await CreateBusinessAsync(db);
        var widget = await CreateWidgetAsync(db, business.Id);
        var customer = await CreateCustomerAsync(db, business.Id);
        var staffUser = await CreateStaffUserAsync(db, business.Id);
        var conversation = await CreateConversationAsync(db, business.Id, widget.Id, customer.Id, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        await CreateMessageAsync(db, business.Id, conversation.Id, DateTimeOffset.UtcNow);

        await erasureService.EraseCustomerAsync(customer.Id, staffUser.Id);

        var conversationAfter = await db.Conversations.IgnoreQueryFilters().SingleAsync(c => c.Id == conversation.Id);
        Assert.True(conversationAfter.IsAnonymized);
        Assert.False(await db.Messages.IgnoreQueryFilters().AnyAsync(m => m.ConversationId == conversation.Id));

        var auditCount = await db.AuditLogs.IgnoreQueryFilters()
            .CountAsync(a => a.EntityId == conversation.Id.ToString() && a.EventType == AuditEventTypes.ConversationErasurePurged);
        Assert.Equal(1, auditCount);
    }

    /// <summary>
    /// Requirement 1 (rollback) — an injected failure inside the joined
    /// purge means neither the purge nor its audit row remains committed.
    /// </summary>
    [Fact]
    public async Task EraseCustomer_FailureDuringConversationPurge_RollsBackPurgeAndAudit()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var auditLogService = scope.ServiceProvider.GetRequiredService<IAuditLogService>();

        var business = await CreateBusinessAsync(db);
        var widget = await CreateWidgetAsync(db, business.Id);
        var customer = await CreateCustomerAsync(db, business.Id);
        var staffUser = await CreateStaffUserAsync(db, business.Id);
        var conversation = await CreateConversationAsync(db, business.Id, widget.Id, customer.Id, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        await CreateMessageAsync(db, business.Id, conversation.Id, DateTimeOffset.UtcNow);

        var failingPurgeService = new ConversationPurgeService(db, new FailAtCheckpoint("before-commit"));
        var failingErasureService = new CustomerErasureService(db, failingPurgeService, auditLogService, new NoOpCustomerErasureCheckpoint());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => failingErasureService.EraseCustomerAsync(customer.Id, staffUser.Id));

        // Verified via a SEPARATE scope — PurgeConversationAsync's own
        // in-memory property assignments happen before the injected
        // failure fires, and EF Core's change tracker does not revert
        // tracked property values when a transaction is subsequently
        // rolled back (same reasoning as this file's Phase 2/3 rollback
        // tests).
        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();

        Assert.True(await verifyDb.Messages.IgnoreQueryFilters().AnyAsync(m => m.ConversationId == conversation.Id));
        var conversationAfter = await verifyDb.Conversations.IgnoreQueryFilters().SingleAsync(c => c.Id == conversation.Id);
        Assert.False(conversationAfter.IsAnonymized);

        var auditCount = await verifyDb.AuditLogs.IgnoreQueryFilters()
            .CountAsync(a => a.EntityId == conversation.Id.ToString() && a.EventType == AuditEventTypes.ConversationErasurePurged);
        Assert.Equal(0, auditCount);

        // Phase 1 (identity-row deletes) already committed in its own,
        // separate, earlier transaction before Phase 2 ever ran — a Phase
        // 2 failure must not (and does not) roll that back too.
        var customerAfter = await verifyDb.Customers.IgnoreQueryFilters().SingleAsync(c => c.Id == customer.Id);
        Assert.False(customerAfter.IsAnonymized); // Phase 3 never reached
    }

    /// <summary>
    /// Requirement 1 (rollback, the other direction) — the purge succeeds
    /// first, then the audit write itself fails. The outer per-conversation
    /// transaction must roll back the ALREADY-SUCCEEDED purge too — proving
    /// the locked property holds even when the failure originates in the
    /// audit write, not the purge.
    /// </summary>
    [Fact]
    public async Task EraseCustomer_FailureDuringAuditWriteAfterSuccessfulPurge_RollsBackBoth()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var realAuditLogService = scope.ServiceProvider.GetRequiredService<IAuditLogService>();
        var realPurgeService = scope.ServiceProvider.GetRequiredService<IConversationPurgeService>();

        var business = await CreateBusinessAsync(db);
        var widget = await CreateWidgetAsync(db, business.Id);
        var customer = await CreateCustomerAsync(db, business.Id);
        var staffUser = await CreateStaffUserAsync(db, business.Id);
        var conversation = await CreateConversationAsync(db, business.Id, widget.Id, customer.Id, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        await CreateMessageAsync(db, business.Id, conversation.Id, DateTimeOffset.UtcNow);

        var throwingAuditService = new ThrowingOnEventType(realAuditLogService, AuditEventTypes.ConversationErasurePurged);
        var failingErasureService = new CustomerErasureService(db, realPurgeService, throwingAuditService, new NoOpCustomerErasureCheckpoint());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => failingErasureService.EraseCustomerAsync(customer.Id, staffUser.Id));

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();

        // The purge itself would have succeeded in isolation, but must be
        // rolled back together with the audit write it shares a
        // transaction with.
        Assert.True(await verifyDb.Messages.IgnoreQueryFilters().AnyAsync(m => m.ConversationId == conversation.Id));
        var conversationAfter = await verifyDb.Conversations.IgnoreQueryFilters().SingleAsync(c => c.Id == conversation.Id);
        Assert.False(conversationAfter.IsAnonymized);
        var auditCount = await verifyDb.AuditLogs.IgnoreQueryFilters()
            .CountAsync(a => a.EntityId == conversation.Id.ToString() && a.EventType == AuditEventTypes.ConversationErasurePurged);
        Assert.Equal(0, auditCount);
    }

    /// <summary>
    /// Requirement 2 — the core multi-conversation partial-failure proof.
    /// Five conversations; conversation 3 (defined below by the actual
    /// discovery-query result, not assumed insertion order — see comment)
    /// fails during its purge. Proves, via a separate verification scope:
    /// conversations 1-2 fully purged and audited; conversation 3 rolled
    /// back with no partial state and no orphaned audit row; conversations
    /// 4-5 never attempted at all.
    /// </summary>
    [Fact]
    public async Task EraseCustomer_FailureDuringOneOfFiveConversations_PriorCommitted_FailedRolledBack_LaterUntouched()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var auditLogService = scope.ServiceProvider.GetRequiredService<IAuditLogService>();
        var realPurgeService = scope.ServiceProvider.GetRequiredService<IConversationPurgeService>();

        var business = await CreateBusinessAsync(db);
        var widget = await CreateWidgetAsync(db, business.Id);
        var customer = await CreateCustomerAsync(db, business.Id);
        var staffUser = await CreateStaffUserAsync(db, business.Id);

        for (var i = 0; i < 5; i++)
        {
            var conversation = await CreateConversationAsync(db, business.Id, widget.Id, customer.Id, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            await CreateMessageAsync(db, business.Id, conversation.Id, DateTimeOffset.UtcNow);
        }

        // EraseCustomerAsync's own Phase 2 discovery query has no ORDER BY
        // (confirmed by direct inspection — CustomerErasureService.cs),
        // so "conversation 3" is not a safe assumption based on insertion
        // order. This probe issues the IDENTICAL query, against the same
        // unchanged data, immediately before EraseCustomerAsync runs its
        // own internal copy of it — no writes happen to this customer's
        // Conversations in between, so both queries return the same
        // physical scan order in practice. This mirrors this project's own
        // established discipline about not assuming unspecified Postgres
        // ordering (see NotificationApiTests' own real-ordering test) by
        // reading the actual order rather than asserting one.
        var processingOrder = await db.Conversations
            .IgnoreQueryFilters()
            .Where(c => c.BusinessId == business.Id && c.CustomerId == customer.Id)
            .Select(c => c.Id)
            .ToListAsync();
        Assert.Equal(5, processingOrder.Count);

        var priorIds = processingOrder.Take(2).ToList();
        var failingId = processingOrder[2];
        var laterIds = processingOrder.Skip(3).ToList();

        var failingPurgeService = new FailForSpecificConversation(realPurgeService, failingId);
        var failingErasureService = new CustomerErasureService(db, failingPurgeService, auditLogService, new NoOpCustomerErasureCheckpoint());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => failingErasureService.EraseCustomerAsync(customer.Id, staffUser.Id));

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Conversations 1-2: purged, audited exactly once each.
        foreach (var id in priorIds)
        {
            var conv = await verifyDb.Conversations.IgnoreQueryFilters().SingleAsync(c => c.Id == id);
            Assert.True(conv.IsAnonymized);
            Assert.Null(conv.VisitorSessionId);
            Assert.False(await verifyDb.Messages.IgnoreQueryFilters().AnyAsync(m => m.ConversationId == id));
            var auditCount = await verifyDb.AuditLogs.IgnoreQueryFilters()
                .CountAsync(a => a.EntityId == id.ToString() && a.EventType == AuditEventTypes.ConversationErasurePurged);
            Assert.Equal(1, auditCount);
        }

        // Conversation 3: transaction rolled back — no partial destructive
        // state, no orphaned audit row.
        var failingConv = await verifyDb.Conversations.IgnoreQueryFilters().SingleAsync(c => c.Id == failingId);
        Assert.False(failingConv.IsAnonymized);
        Assert.NotNull(failingConv.VisitorSessionId);
        Assert.True(await verifyDb.Messages.IgnoreQueryFilters().AnyAsync(m => m.ConversationId == failingId));
        var failingAuditCount = await verifyDb.AuditLogs.IgnoreQueryFilters()
            .CountAsync(a => a.EntityId == failingId.ToString() && a.EventType == AuditEventTypes.ConversationErasurePurged);
        Assert.Equal(0, failingAuditCount);

        // Conversations 4-5: never attempted — the outer Phase 2 loop has
        // no try/catch of its own, so the exception from conversation 3
        // propagates all the way out of EraseCustomerAsync before the
        // loop ever reaches these.
        foreach (var id in laterIds)
        {
            var conv = await verifyDb.Conversations.IgnoreQueryFilters().SingleAsync(c => c.Id == id);
            Assert.False(conv.IsAnonymized);
            Assert.NotNull(conv.VisitorSessionId);
            Assert.True(await verifyDb.Messages.IgnoreQueryFilters().AnyAsync(m => m.ConversationId == id));
            var auditCount = await verifyDb.AuditLogs.IgnoreQueryFilters()
                .CountAsync(a => a.EntityId == id.ToString() && a.EventType == AuditEventTypes.ConversationErasurePurged);
            Assert.Equal(0, auditCount);
        }

        // Phase 3 never reached.
        var customerAfter = await verifyDb.Customers.IgnoreQueryFilters().SingleAsync(c => c.Id == customer.Id);
        Assert.False(customerAfter.IsAnonymized);
    }

    /// <summary>
    /// Retry safety (§3.3) — a subsequent, unfailing call converges: the
    /// conversation already purged in the earlier failed attempt is not
    /// re-audited (claim returns 0 for it), the remaining conversations are
    /// purged and audited, and the operation completes.
    /// </summary>
    [Fact]
    public async Task EraseCustomer_RetryAfterPartialFailure_ConvergesSafely_NoDuplicateAudit()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var auditLogService = scope.ServiceProvider.GetRequiredService<IAuditLogService>();
        var realPurgeService = scope.ServiceProvider.GetRequiredService<IConversationPurgeService>();

        var business = await CreateBusinessAsync(db);
        var widget = await CreateWidgetAsync(db, business.Id);
        var customer = await CreateCustomerAsync(db, business.Id);
        var staffUser = await CreateStaffUserAsync(db, business.Id);

        for (var i = 0; i < 3; i++)
        {
            var conversation = await CreateConversationAsync(db, business.Id, widget.Id, customer.Id, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            await CreateMessageAsync(db, business.Id, conversation.Id, DateTimeOffset.UtcNow);
        }

        var processingOrder = await db.Conversations
            .IgnoreQueryFilters()
            .Where(c => c.BusinessId == business.Id && c.CustomerId == customer.Id)
            .Select(c => c.Id)
            .ToListAsync();
        Assert.Equal(3, processingOrder.Count);
        var succeededFirstId = processingOrder[0];
        var failingId = processingOrder[1];

        var failingPurgeService = new FailForSpecificConversation(realPurgeService, failingId);
        var failingErasureService = new CustomerErasureService(db, failingPurgeService, auditLogService, new NoOpCustomerErasureCheckpoint());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => failingErasureService.EraseCustomerAsync(customer.Id, staffUser.Id));

        // Retry — real services, no injected failure this time.
        var retryErasureService = scope.ServiceProvider.GetRequiredService<ICustomerErasureService>();
        await retryErasureService.EraseCustomerAsync(customer.Id, staffUser.Id);

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();

        foreach (var id in processingOrder)
        {
            var conv = await verifyDb.Conversations.IgnoreQueryFilters().SingleAsync(c => c.Id == id);
            Assert.True(conv.IsAnonymized);
            var auditCount = await verifyDb.AuditLogs.IgnoreQueryFilters()
                .CountAsync(a => a.EntityId == id.ToString() && a.EventType == AuditEventTypes.ConversationErasurePurged);
            Assert.Equal(1, auditCount); // exactly one — never re-audited, including succeededFirstId
        }

        var customerAfter = await verifyDb.Customers.IgnoreQueryFilters().SingleAsync(c => c.Id == customer.Id);
        Assert.True(customerAfter.IsAnonymized);
        var completionAuditCount = await verifyDb.AuditLogs.IgnoreQueryFilters()
            .CountAsync(a => a.EntityId == customer.Id.ToString() && a.EventType == AuditEventTypes.CustomerErased);
        Assert.Equal(1, completionAuditCount);

        _ = succeededFirstId; // named for clarity in the loop above; no separate assertion needed
    }

    /// <summary>
    /// Requirement 3 (happy path) — the terminal Customer mutation and its
    /// CustomerErased completion audit row share the same CorrelationId as
    /// the per-conversation rows and commit together.
    /// </summary>
    [Fact]
    public async Task EraseCustomer_TerminalMutationAndCompletionAudit_CommitTogether()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var erasureService = scope.ServiceProvider.GetRequiredService<ICustomerErasureService>();

        var business = await CreateBusinessAsync(db);
        var widget = await CreateWidgetAsync(db, business.Id);
        var customer = await CreateCustomerAsync(db, business.Id);
        var staffUser = await CreateStaffUserAsync(db, business.Id);
        var conversation = await CreateConversationAsync(db, business.Id, widget.Id, customer.Id, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        await CreateMessageAsync(db, business.Id, conversation.Id, DateTimeOffset.UtcNow);

        await erasureService.EraseCustomerAsync(customer.Id, staffUser.Id);

        var customerAfter = await db.Customers.IgnoreQueryFilters().SingleAsync(c => c.Id == customer.Id);
        Assert.True(customerAfter.IsAnonymized);
        Assert.NotNull(customerAfter.AnonymizedAt);

        var completionAudit = await db.AuditLogs.IgnoreQueryFilters()
            .SingleAsync(a => a.EntityId == customer.Id.ToString() && a.EventType == AuditEventTypes.CustomerErased);
        Assert.Equal(AuditEntityTypes.Customer, completionAudit.EntityType);
        Assert.Equal(ActorType.NivoraBusinessStaff, completionAudit.Actor);
        Assert.Equal(staffUser.Id, completionAudit.ChangedByUserId);
        Assert.NotNull(completionAudit.CorrelationId);

        var conversationAudit = await db.AuditLogs.IgnoreQueryFilters()
            .SingleAsync(a => a.EntityId == conversation.Id.ToString() && a.EventType == AuditEventTypes.ConversationErasurePurged);
        Assert.Equal(completionAudit.CorrelationId, conversationAudit.CorrelationId);
    }

    /// <summary>
    /// Requirement 3 (rollback) — a failure between the Customer mutation
    /// and the completion audit's commit must roll back the Customer
    /// mutation too, leaving IsAnonymized false. Already-committed
    /// per-conversation purges from Phase 2 remain committed regardless —
    /// Phase 3's own transaction is independent of Phase 2's.
    /// </summary>
    [Fact]
    public async Task EraseCustomer_FailureBeforeCompletionAuditCommit_RollsBackCustomerMutation()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var realAuditLogService = scope.ServiceProvider.GetRequiredService<IAuditLogService>();
        var realPurgeService = scope.ServiceProvider.GetRequiredService<IConversationPurgeService>();

        var business = await CreateBusinessAsync(db);
        var widget = await CreateWidgetAsync(db, business.Id);
        var customer = await CreateCustomerAsync(db, business.Id);
        var staffUser = await CreateStaffUserAsync(db, business.Id);
        var conversation = await CreateConversationAsync(db, business.Id, widget.Id, customer.Id, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        await CreateMessageAsync(db, business.Id, conversation.Id, DateTimeOffset.UtcNow);

        // Only the terminal CustomerErased write fails — the per-conversation
        // ConversationErasurePurged write in Phase 2 must succeed normally.
        var throwingAuditService = new ThrowingOnEventType(realAuditLogService, AuditEventTypes.CustomerErased);
        var failingErasureService = new CustomerErasureService(db, realPurgeService, throwingAuditService, new NoOpCustomerErasureCheckpoint());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => failingErasureService.EraseCustomerAsync(customer.Id, staffUser.Id));

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Phase 2 already committed — the conversation purge is real and
        // permanent, unaffected by Phase 3's own, later, independent
        // transaction failing.
        var conversationAfter = await verifyDb.Conversations.IgnoreQueryFilters().SingleAsync(c => c.Id == conversation.Id);
        Assert.True(conversationAfter.IsAnonymized);
        var conversationAuditCount = await verifyDb.AuditLogs.IgnoreQueryFilters()
            .CountAsync(a => a.EntityId == conversation.Id.ToString() && a.EventType == AuditEventTypes.ConversationErasurePurged);
        Assert.Equal(1, conversationAuditCount);

        // Phase 3 rolled back — Customer never anonymized, no completion audit.
        var customerAfter = await verifyDb.Customers.IgnoreQueryFilters().SingleAsync(c => c.Id == customer.Id);
        Assert.False(customerAfter.IsAnonymized);
        Assert.Null(customerAfter.AnonymizedAt);
        Assert.NotNull(customerAfter.Email); // never nulled — the mutation rolled back
        var completionAuditCount = await verifyDb.AuditLogs.IgnoreQueryFilters()
            .CountAsync(a => a.EntityId == customer.Id.ToString() && a.EventType == AuditEventTypes.CustomerErased);
        Assert.Equal(0, completionAuditCount);
    }

    /// <summary>
    /// Requirement 4 — Decision 4 exactly: Consumed survives, every other
    /// status (including Denied with a populated DeniedReason) is deleted,
    /// and an unrelated customer's Approval in the same business is
    /// completely unaffected.
    /// </summary>
    [Fact]
    public async Task EraseCustomer_ApprovalCleanup_ConsumedSurvives_NonConsumedDeleted_UnrelatedCustomerUntouched()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var erasureService = scope.ServiceProvider.GetRequiredService<ICustomerErasureService>();

        var business = await CreateBusinessAsync(db);
        var widget = await CreateWidgetAsync(db, business.Id);
        var customer = await CreateCustomerAsync(db, business.Id);
        var staffUser = await CreateStaffUserAsync(db, business.Id);
        var conversation = await CreateConversationAsync(db, business.Id, widget.Id, customer.Id, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        await CreateMessageAsync(db, business.Id, conversation.Id, DateTimeOffset.UtcNow);

        var pending = await CreateApprovalAsync(db, business.Id, conversation.Id, customer.Id, ApprovalStatus.Pending);
        var approved = await CreateApprovalAsync(db, business.Id, conversation.Id, customer.Id, ApprovalStatus.Approved);
        var consumed = await CreateApprovalAsync(db, business.Id, conversation.Id, customer.Id, ApprovalStatus.Consumed);

        var deniedWithReason = await CreateApprovalAsync(db, business.Id, conversation.Id, customer.Id, ApprovalStatus.Denied);
        deniedWithReason.DeniedReason = "Customer no longer eligible for this refund.";
        await db.SaveChangesAsync();

        // Unrelated customer, same business — must be completely untouched.
        var unrelatedCustomer = await CreateCustomerAsync(db, business.Id);
        var unrelatedConversation = await CreateConversationAsync(db, business.Id, widget.Id, unrelatedCustomer.Id, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var unrelatedApproval = await CreateApprovalAsync(db, business.Id, unrelatedConversation.Id, unrelatedCustomer.Id, ApprovalStatus.Pending);

        await erasureService.EraseCustomerAsync(customer.Id, staffUser.Id);

        Assert.False(await db.Approvals.IgnoreQueryFilters().AnyAsync(a => a.Id == pending.Id));
        Assert.False(await db.Approvals.IgnoreQueryFilters().AnyAsync(a => a.Id == approved.Id));
        Assert.False(await db.Approvals.IgnoreQueryFilters().AnyAsync(a => a.Id == deniedWithReason.Id));
        Assert.True(await db.Approvals.IgnoreQueryFilters().AnyAsync(a => a.Id == consumed.Id));

        Assert.True(await db.Approvals.IgnoreQueryFilters().AnyAsync(a => a.Id == unrelatedApproval.Id));
    }

    /// <summary>
    /// Requirement 5 — ExternalIdentity rows are explicitly deleted by
    /// Phase 1 (not merely a side effect of some cascade — the Customer
    /// row itself is never deleted, only anonymized in place, so the
    /// Cascade FK on ExternalIdentity.CustomerId never fires; this
    /// ExecuteDeleteAsync is the only mechanism that removes them). An
    /// unrelated customer's identity survives untouched.
    /// </summary>
    [Fact]
    public async Task EraseCustomer_ExternalIdentityCleanup_ExplicitDeletion_UnrelatedIdentityPreserved()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var erasureService = scope.ServiceProvider.GetRequiredService<ICustomerErasureService>();

        var business = await CreateBusinessAsync(db);
        var customer = await CreateCustomerAsync(db, business.Id);
        var staffUser = await CreateStaffUserAsync(db, business.Id);

        var identity = new ExternalIdentity
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id,
            CustomerId = customer.Id,
            Channel = "Website",
            VerificationMethod = "SignedToken",
            ExternalUserId = "to-be-erased",
            LinkedAt = DateTimeOffset.UtcNow,
        };
        db.ExternalIdentities.Add(identity);

        var unrelatedCustomer = await CreateCustomerAsync(db, business.Id);
        var unrelatedIdentity = new ExternalIdentity
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id,
            CustomerId = unrelatedCustomer.Id,
            Channel = "Website",
            VerificationMethod = "SignedToken",
            ExternalUserId = "unrelated",
            LinkedAt = DateTimeOffset.UtcNow,
        };
        db.ExternalIdentities.Add(unrelatedIdentity);
        await db.SaveChangesAsync();

        await erasureService.EraseCustomerAsync(customer.Id, staffUser.Id);

        Assert.False(await db.ExternalIdentities.IgnoreQueryFilters().AnyAsync(i => i.Id == identity.Id));
        Assert.True(await db.ExternalIdentities.IgnoreQueryFilters().AnyAsync(i => i.Id == unrelatedIdentity.Id));

        // Customer row itself survives (anonymized in place) — confirming
        // the ExternalIdentity delete above is truly explicit, not an
        // artifact of a Cascade-on-Customer-delete that never happened.
        var customerAfter = await db.Customers.IgnoreQueryFilters().SingleAsync(c => c.Id == customer.Id);
        Assert.True(customerAfter.IsAnonymized);
    }

    /// <summary>
    /// §7 item 1's confirmed, non-hypothetical edge case: Customer A's
    /// Approval survives via Phase 1's customer-scoped delete even though
    /// its Conversation was re-linked to Customer B before A's erasure ran,
    /// so Phase 2's conversation-scoped discovery never finds it. Uses the
    /// SAME mechanism VerificationCodeService.VerifyCodeAsync actually
    /// uses (an unconditional ExecuteUpdateAsync setting
    /// Conversation.CustomerId, no prior-value guard) — not a hand-rolled
    /// substitute. VerificationCodeService itself is not modified.
    /// </summary>
    [Fact]
    public async Task EraseCustomer_OrphanedApprovalFromReVerifiedConversation_StillErased()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var erasureService = scope.ServiceProvider.GetRequiredService<ICustomerErasureService>();

        var business = await CreateBusinessAsync(db);
        var widget = await CreateWidgetAsync(db, business.Id);
        var customerA = await CreateCustomerAsync(db, business.Id);
        var customerB = await CreateCustomerAsync(db, business.Id);
        var staffUser = await CreateStaffUserAsync(db, business.Id);

        var conv1 = await CreateConversationAsync(db, business.Id, widget.Id, customerA.Id, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var messageOnConv1 = await CreateMessageAsync(db, business.Id, conv1.Id, DateTimeOffset.UtcNow);
        var orphanedApproval = await CreateApprovalAsync(db, business.Id, conv1.Id, customerA.Id, ApprovalStatus.Pending);

        // Mirrors VerificationCodeService.VerifyCodeAsync's exact
        // mechanism (VerificationCodeService.cs) — unconditional
        // Conversation.CustomerId overwrite, no guard on the prior value.
        await db.Conversations
            .IgnoreQueryFilters()
            .Where(c => c.Id == conv1.Id && c.BusinessId == business.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.CustomerId, customerB.Id)
                .SetProperty(c => c.Level2VerifiedUntil, DateTimeOffset.UtcNow.AddMinutes(10)));

        await erasureService.EraseCustomerAsync(customerA.Id, staffUser.Id);

        // Verified via a SEPARATE scope: `conv1` was already tracked on
        // `db` from CreateConversationAsync's own load, and the raw
        // ExecuteUpdateAsync re-link above is a bulk operation that
        // bypasses the change tracker entirely — re-querying conv1.Id on
        // the SAME `db` would return that stale tracked instance (still
        // showing CustomerId == A in memory) rather than the true,
        // re-linked database row. A fresh context has nothing tracked and
        // reads the real row.
        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();

        // The orphaned Approval (still CustomerId == A) is deleted —
        // caught by Phase 1's customer-scoped delete.
        Assert.False(await verifyDb.Approvals.IgnoreQueryFilters().AnyAsync(a => a.Id == orphanedApproval.Id));

        // Conv1 itself, now owned by Customer B, was never purged as part
        // of Customer A's erasure — its Messages remain intact.
        var conv1After = await verifyDb.Conversations.IgnoreQueryFilters().SingleAsync(c => c.Id == conv1.Id);
        Assert.False(conv1After.IsAnonymized);
        Assert.Equal(customerB.Id, conv1After.CustomerId);
        Assert.True(await verifyDb.Messages.IgnoreQueryFilters().AnyAsync(m => m.Id == messageOnConv1.Id));

        // Customer B's own data is completely unaffected by Customer A's erasure.
        var customerBAfter = await verifyDb.Customers.IgnoreQueryFilters().SingleAsync(c => c.Id == customerB.Id);
        Assert.False(customerBAfter.IsAnonymized);

        // Customer A did complete its own erasure.
        var customerAAfter = await verifyDb.Customers.IgnoreQueryFilters().SingleAsync(c => c.Id == customerA.Id);
        Assert.True(customerAAfter.IsAnonymized);
    }

    /// <summary>
    /// changedByUserId and CorrelationId reach every audit row a single
    /// erasure produces, across multiple conversations.
    /// </summary>
    [Fact]
    public async Task EraseCustomer_ChangedByUserIdAndCorrelationId_ReachEveryAuditRow()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var erasureService = scope.ServiceProvider.GetRequiredService<ICustomerErasureService>();

        var business = await CreateBusinessAsync(db);
        var widget = await CreateWidgetAsync(db, business.Id);
        var customer = await CreateCustomerAsync(db, business.Id);
        var staffUser = await CreateStaffUserAsync(db, business.Id);

        var conversation1 = await CreateConversationAsync(db, business.Id, widget.Id, customer.Id, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        await CreateMessageAsync(db, business.Id, conversation1.Id, DateTimeOffset.UtcNow);
        var conversation2 = await CreateConversationAsync(db, business.Id, widget.Id, customer.Id, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        await CreateMessageAsync(db, business.Id, conversation2.Id, DateTimeOffset.UtcNow);

        await erasureService.EraseCustomerAsync(customer.Id, staffUser.Id);

        var allAuditRows = await db.AuditLogs.IgnoreQueryFilters()
            .Where(a => a.BusinessId == business.Id
                && ((a.EventType == AuditEventTypes.ConversationErasurePurged && (a.EntityId == conversation1.Id.ToString() || a.EntityId == conversation2.Id.ToString()))
                    || (a.EventType == AuditEventTypes.CustomerErased && a.EntityId == customer.Id.ToString())))
            .ToListAsync();

        Assert.Equal(3, allAuditRows.Count); // 2 per-conversation + 1 completion
        Assert.All(allAuditRows, a => Assert.Equal(staffUser.Id, a.ChangedByUserId));
        Assert.All(allAuditRows, a => Assert.Equal(ActorType.NivoraBusinessStaff, a.Actor));

        var correlationIds = allAuditRows.Select(a => a.CorrelationId).Distinct().ToList();
        var singleCorrelationId = Assert.Single(correlationIds);
        Assert.NotNull(singleCorrelationId);
    }

    /// <summary>
    /// Idempotency — a second call against an already-anonymized customer
    /// is a true no-op: zero additional deletes, zero additional purges,
    /// zero additional audit rows.
    /// </summary>
    [Fact]
    public async Task EraseCustomer_AlreadyAnonymized_TrueNoOp()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var erasureService = scope.ServiceProvider.GetRequiredService<ICustomerErasureService>();

        var business = await CreateBusinessAsync(db);
        var widget = await CreateWidgetAsync(db, business.Id);
        var customer = await CreateCustomerAsync(db, business.Id);
        var staffUser = await CreateStaffUserAsync(db, business.Id);
        var conversation = await CreateConversationAsync(db, business.Id, widget.Id, customer.Id, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        await CreateMessageAsync(db, business.Id, conversation.Id, DateTimeOffset.UtcNow);

        await erasureService.EraseCustomerAsync(customer.Id, staffUser.Id);

        var auditCountAfterFirst = await db.AuditLogs.IgnoreQueryFilters().CountAsync(a => a.BusinessId == business.Id);

        await erasureService.EraseCustomerAsync(customer.Id, staffUser.Id);

        var auditCountAfterSecond = await db.AuditLogs.IgnoreQueryFilters().CountAsync(a => a.BusinessId == business.Id);
        Assert.Equal(auditCountAfterFirst, auditCountAfterSecond);

        var customerAfter = await db.Customers.IgnoreQueryFilters().SingleAsync(c => c.Id == customer.Id);
        Assert.True(customerAfter.IsAnonymized);
        Assert.Null(customerAfter.Email);
        Assert.Null(customerAfter.Name);
        Assert.Null(customerAfter.ExternalId);
    }

    // ── 14. Identity-binding/erasure-integrity Phase 2 — foundational ──

    /// <summary>
    /// Phase 2 verification criterion (implementation plan): a plain
    /// round-trip for the new Customer.PendingErasureAt column — set,
    /// save, then reload through a fresh scope/DbContext (not the same
    /// tracked instance that wrote it) and confirm the persisted value
    /// matches. No CustomerErasureService/advisory-lock behavior exists
    /// yet to protect (that's Phase 3+) — this is plumbing correctness
    /// only.
    /// </summary>
    [Fact]
    public async Task Customer_PendingErasureAt_RoundTripsThroughFreshScope()
    {
        Guid businessId;
        Guid customerId;
        DateTimeOffset pendingErasureAt;

        await using (var writeScope = _provider.CreateAsyncScope())
        {
            var writeDb = writeScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var business = await CreateBusinessAsync(writeDb);
            var customer = await CreateCustomerAsync(writeDb, business.Id);
            businessId = business.Id;
            customerId = customer.Id;

            // Postgres timestamptz stores microsecond precision, not .NET's
            // 100ns ticks — truncate before the round-trip comparison, same
            // convention as ConversationsApiTests/NotificationApiTests/
            // WidgetConversationServiceTests' own TruncateToMicroseconds.
            pendingErasureAt = TruncateToMicroseconds(DateTimeOffset.UtcNow);
            customer.PendingErasureAt = pendingErasureAt;
            await writeDb.SaveChangesAsync();
        }

        await using var readScope = _provider.CreateAsyncScope();
        var readDb = readScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var reloaded = await readDb.Customers.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(c => c.Id == customerId);

        Assert.Equal(pendingErasureAt, reloaded.PendingErasureAt);
        Assert.Equal(businessId, reloaded.BusinessId);
    }

    private static DateTimeOffset TruncateToMicroseconds(DateTimeOffset value) =>
        new(value.Ticks - (value.Ticks % 10), value.Offset);

    // ── 15. Identity-binding/erasure-integrity Phase 3 — CustomerErasureService Phase 0 ──

    /// <summary>
    /// Plan's own "Lock acquisition, no consumer yet" criterion: a
    /// competing holder of ForCustomerErasure(businessId, customerId) —
    /// here a raw barrier connection, same idiom
    /// VerificationCodeServiceTests.cs already established for its own
    /// ForVerificationCode races — provably blocks EraseCustomerAsync's
    /// Phase 0 (proven via pg_locks waiter count, not a timer/sleep) until
    /// the barrier releases, and PendingErasureAt genuinely has not been
    /// set while blocked.
    /// </summary>
    [Fact]
    public async Task EraseCustomer_PhaseZero_LockAcquisitionBlocksConcurrentErasure()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var erasureService = scope.ServiceProvider.GetRequiredService<ICustomerErasureService>();

        var business = await CreateBusinessAsync(db);
        var customer = await CreateCustomerAsync(db, business.Id);
        var staffUser = await CreateStaffUserAsync(db, business.Id);

        var lockKey = AdvisoryLockKeyGenerator.ForCustomerErasure(business.Id, customer.Id);
        var (barrierConnection, barrierTransaction) = await AcquireBarrierAsync(lockKey);
        try
        {
            var eraseTask = erasureService.EraseCustomerAsync(customer.Id, staffUser.Id);

            await WaitForAdvisoryLockWaitersAsync(barrierConnection, lockKey, expected: 1, raceName: "CustomerErasure Phase 0");
            Assert.False(eraseTask.IsCompleted);

            await using var duringScope = _provider.CreateAsyncScope();
            var duringDb = duringScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var duringBlock = await duringDb.Customers.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.Id == customer.Id);
            Assert.Null(duringBlock.PendingErasureAt);

            await barrierTransaction.RollbackAsync();
            await eraseTask;
        }
        finally
        {
            await barrierConnection.DisposeAsync();
        }

        await using var afterScope = _provider.CreateAsyncScope();
        var afterDb = afterScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var after = await afterDb.Customers.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.Id == customer.Id);
        Assert.True(after.IsAnonymized);
        Assert.Null(after.PendingErasureAt);
    }

    /// <summary>
    /// Plan's own "Idempotent set" criterion: Phase 0's
    /// COALESCE(PendingErasureAt, now) must never overwrite an
    /// already-set value — simulating a resumed/retried erasure attempt.
    /// Paused at the Phase 0 checkpoint (before Phase 3's terminal clear
    /// could run) so the still-set value can be observed directly, not
    /// inferred from a full run.
    /// </summary>
    [Fact]
    public async Task EraseCustomer_PhaseZero_AlreadySetPendingErasureAt_NotOverwritten()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var purgeService = scope.ServiceProvider.GetRequiredService<IConversationPurgeService>();
        var auditLogService = scope.ServiceProvider.GetRequiredService<IAuditLogService>();

        var business = await CreateBusinessAsync(db);
        var customer = await CreateCustomerAsync(db, business.Id);
        var staffUser = await CreateStaffUserAsync(db, business.Id);

        var originalPendingErasureAt = TruncateToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(-30));
        await db.Customers.IgnoreQueryFilters()
            .Where(c => c.Id == customer.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.PendingErasureAt, originalPendingErasureAt));

        var gate = new GatedCustomerErasureCheckpoint("after-phase-0-committed");
        var erasureService = new CustomerErasureService(db, purgeService, auditLogService, gate);

        var eraseTask = erasureService.EraseCustomerAsync(customer.Id, staffUser.Id);
        await gate.Reached;

        await using var duringScope = _provider.CreateAsyncScope();
        var duringDb = duringScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var duringBlock = await duringDb.Customers.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.Id == customer.Id);
        Assert.Equal(originalPendingErasureAt, duringBlock.PendingErasureAt);

        gate.Release();
        await eraseTask;
    }

    /// <summary>
    /// Plan's own "Ordering relative to existing Phase 1" criterion:
    /// PendingErasureAt is durably non-null (independent scope) at the
    /// Phase 0 checkpoint, while the ExternalIdentity row Phase 1 would
    /// delete is still present — proving a real window where Phase 0 has
    /// committed but Phase 1 has not yet begun. Releasing the gate then
    /// lets Phase 1 actually run and delete it.
    /// </summary>
    [Fact]
    public async Task EraseCustomer_PhaseZero_CommitsBeforePhaseOneBegins()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var purgeService = scope.ServiceProvider.GetRequiredService<IConversationPurgeService>();
        var auditLogService = scope.ServiceProvider.GetRequiredService<IAuditLogService>();

        var business = await CreateBusinessAsync(db);
        var customer = await CreateCustomerAsync(db, business.Id);
        var staffUser = await CreateStaffUserAsync(db, business.Id);

        var identity = new ExternalIdentity
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id,
            CustomerId = customer.Id,
            Channel = "Website",
            VerificationMethod = "SignedToken",
            ExternalUserId = "phase-ordering-check",
            LinkedAt = DateTimeOffset.UtcNow,
        };
        db.ExternalIdentities.Add(identity);
        await db.SaveChangesAsync();

        var gate = new GatedCustomerErasureCheckpoint("after-phase-0-committed");
        var erasureService = new CustomerErasureService(db, purgeService, auditLogService, gate);

        var eraseTask = erasureService.EraseCustomerAsync(customer.Id, staffUser.Id);
        await gate.Reached;

        await using var duringScope = _provider.CreateAsyncScope();
        var duringDb = duringScope.ServiceProvider.GetRequiredService<AppDbContext>();

        var duringCustomer = await duringDb.Customers.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.Id == customer.Id);
        Assert.NotNull(duringCustomer.PendingErasureAt);
        Assert.True(await duringDb.ExternalIdentities.IgnoreQueryFilters().AnyAsync(i => i.Id == identity.Id));

        gate.Release();
        await eraseTask;

        await using var afterScope = _provider.CreateAsyncScope();
        var afterDb = afterScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await afterDb.ExternalIdentities.IgnoreQueryFilters().AnyAsync(i => i.Id == identity.Id));
    }

    /// <summary>
    /// Plan's own "Abandoned after Phase 0" criterion (design §3.8's own
    /// acceptance criterion: a failed/abandoned operation after Phase 0
    /// may leave PendingErasureAt set — an accepted outcome, not a bug).
    /// The checkpoint throws instead of pausing, mirroring
    /// IPurgeFailurePoint's own FailAtCheckpoint idiom. No cleanup or
    /// recovery logic is exercised or expected here.
    /// </summary>
    [Fact]
    public async Task EraseCustomer_PhaseZero_CheckpointThrows_PendingErasureAtSurvivesAndPhaseOneNeverRuns()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var purgeService = scope.ServiceProvider.GetRequiredService<IConversationPurgeService>();
        var auditLogService = scope.ServiceProvider.GetRequiredService<IAuditLogService>();

        var business = await CreateBusinessAsync(db);
        var customer = await CreateCustomerAsync(db, business.Id);
        var staffUser = await CreateStaffUserAsync(db, business.Id);

        var identity = new ExternalIdentity
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id,
            CustomerId = customer.Id,
            Channel = "Website",
            VerificationMethod = "SignedToken",
            ExternalUserId = "abandoned-after-phase-zero",
            LinkedAt = DateTimeOffset.UtcNow,
        };
        db.ExternalIdentities.Add(identity);
        await db.SaveChangesAsync();

        var auditCountBefore = await db.AuditLogs.IgnoreQueryFilters().CountAsync(a => a.BusinessId == business.Id);

        var failingService = new CustomerErasureService(
            db, purgeService, auditLogService, new ThrowAtCustomerErasureCheckpoint("after-phase-0-committed"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => failingService.EraseCustomerAsync(customer.Id, staffUser.Id));

        await using var afterScope = _provider.CreateAsyncScope();
        var afterDb = afterScope.ServiceProvider.GetRequiredService<AppDbContext>();

        var afterCustomer = await afterDb.Customers.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.Id == customer.Id);
        Assert.NotNull(afterCustomer.PendingErasureAt);
        Assert.False(afterCustomer.IsAnonymized);
        Assert.Null(afterCustomer.AnonymizedAt);

        // Phase 1 never began — the ExternalIdentity row it would delete survives.
        Assert.True(await afterDb.ExternalIdentities.IgnoreQueryFilters().AnyAsync(i => i.Id == identity.Id));

        // No Phase 0 audit row (C-1), and no terminal CustomerErased row
        // either, since Phase 3 never ran.
        var auditCountAfter = await afterDb.AuditLogs.IgnoreQueryFilters().CountAsync(a => a.BusinessId == business.Id);
        Assert.Equal(auditCountBefore, auditCountAfter);
    }

    /// <summary>
    /// Plan's own "Happy path (regression check)" criterion, run through
    /// the real, unmodified CustomerErasureService (no custom checkpoint —
    /// the production NoOpCustomerErasureCheckpoint from DI): Phase 0 sets
    /// PendingErasureAt, the existing Phase 1/2/3 flow runs exactly as
    /// before, and — per C-3 — PendingErasureAt is null again once Phase 3
    /// commits, alongside the existing CustomerErased audit row.
    /// </summary>
    [Fact]
    public async Task EraseCustomer_PhaseZero_HappyPath_PendingErasureAtSetThenClearedOnCompletion()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var erasureService = scope.ServiceProvider.GetRequiredService<ICustomerErasureService>();

        var business = await CreateBusinessAsync(db);
        var widget = await CreateWidgetAsync(db, business.Id);
        var customer = await CreateCustomerAsync(db, business.Id);
        var staffUser = await CreateStaffUserAsync(db, business.Id);
        var conversation = await CreateConversationAsync(db, business.Id, widget.Id, customer.Id, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        await CreateMessageAsync(db, business.Id, conversation.Id, DateTimeOffset.UtcNow);

        var identity = new ExternalIdentity
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id,
            CustomerId = customer.Id,
            Channel = "Website",
            VerificationMethod = "SignedToken",
            ExternalUserId = "phase-zero-happy-path",
            LinkedAt = DateTimeOffset.UtcNow,
        };
        db.ExternalIdentities.Add(identity);
        await db.SaveChangesAsync();

        await erasureService.EraseCustomerAsync(customer.Id, staffUser.Id);

        await using var afterScope = _provider.CreateAsyncScope();
        var afterDb = afterScope.ServiceProvider.GetRequiredService<AppDbContext>();

        var afterCustomer = await afterDb.Customers.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.Id == customer.Id);
        Assert.True(afterCustomer.IsAnonymized);
        Assert.Null(afterCustomer.PendingErasureAt);

        Assert.False(await afterDb.ExternalIdentities.IgnoreQueryFilters().AnyAsync(i => i.Id == identity.Id));

        var afterConversation = await afterDb.Conversations.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.Id == conversation.Id);
        Assert.True(afterConversation.IsAnonymized);

        await afterDb.AuditLogs.IgnoreQueryFilters()
            .SingleAsync(a => a.EntityId == customer.Id.ToString() && a.EventType == AuditEventTypes.CustomerErased);
    }

    // ── Phase 3b — "after-phase-1-committed" checkpoint ──────────────────
    //
    // Plan's own Phase 3 note (implementation-plan.md's Phase 3 section):
    // "after-phase-1-committed" after the existing Phase 1 commits too —
    // added once useful for Phases 4-7's own scenarios, per Phase 5's own
    // discovery pass. "after-phase-0-committed" alone cannot deterministically
    // pause erasure strictly after Phase 1's own ExternalIdentity sweep has
    // already run — this second checkpoint closes that gap without touching
    // ICustomerErasureCheckpoint's interface (a new checkpoint name needs no
    // contract change) and without affecting any existing Phase 3 test (both
    // GatedCustomerErasureCheckpoint and ThrowAtCustomerErasureCheckpoint
    // only react to their own configured checkpoint name, passing any other
    // name through as a no-op).

    /// <summary>
    /// Proves a real, deterministically pausable window exists strictly
    /// after Phase 1's ExternalIdentity sweep has already run and strictly
    /// before Phase 3's terminal anonymization — the window Phase 4/5/6/7's
    /// own adversarial tests need to interleave into for a genuine "identity
    /// survives a fully-completed erasure" proof, which "after-phase-0-committed"
    /// alone cannot provide (Phase 1 hasn't run yet at that earlier checkpoint).
    /// </summary>
    [Fact]
    public async Task EraseCustomer_PhaseOne_MarkerStillSetAndIdentitiesGone_AtAfterPhaseOneCheckpoint()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var purgeService = scope.ServiceProvider.GetRequiredService<IConversationPurgeService>();
        var auditLogService = scope.ServiceProvider.GetRequiredService<IAuditLogService>();

        var business = await CreateBusinessAsync(db);
        var customer = await CreateCustomerAsync(db, business.Id);
        var staffUser = await CreateStaffUserAsync(db, business.Id);

        var identity = new ExternalIdentity
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id,
            CustomerId = customer.Id,
            Channel = "Website",
            VerificationMethod = "SignedToken",
            ExternalUserId = "phase-one-ordering-check",
            LinkedAt = DateTimeOffset.UtcNow,
        };
        db.ExternalIdentities.Add(identity);
        await db.SaveChangesAsync();

        var gate = new GatedCustomerErasureCheckpoint("after-phase-1-committed");
        var erasureService = new CustomerErasureService(db, purgeService, auditLogService, gate);

        var eraseTask = erasureService.EraseCustomerAsync(customer.Id, staffUser.Id);

        // Bounded wait — if the checkpoint never fires (e.g. not yet wired
        // up), this must FAIL, not hang forever.
        await gate.Reached.WaitAsync(TimeSpan.FromSeconds(10));

        await using var duringScope = _provider.CreateAsyncScope();
        var duringDb = duringScope.ServiceProvider.GetRequiredService<AppDbContext>();

        var duringCustomer = await duringDb.Customers.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.Id == customer.Id);
        Assert.NotNull(duringCustomer.PendingErasureAt);
        Assert.False(duringCustomer.IsAnonymized);
        Assert.False(await duringDb.ExternalIdentities.IgnoreQueryFilters().AnyAsync(i => i.CustomerId == customer.Id));

        gate.Release();
        await eraseTask;

        await using var afterScope = _provider.CreateAsyncScope();
        var afterDb = afterScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var afterCustomer = await afterDb.Customers.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.Id == customer.Id);
        Assert.True(afterCustomer.IsAnonymized);
        Assert.Null(afterCustomer.PendingErasureAt);
    }

    /// <summary>
    /// Design §3.7 point 4 / §3.8's own accepted-outcome criterion, extended
    /// one phase later than the existing PhaseZero_CheckpointThrows test:
    /// an operation abandoned after Phase 1 has already run (identities
    /// deleted, marker still set) must leave PendingErasureAt set and
    /// IsAnonymized false — an accepted outcome, not a defect.
    /// </summary>
    [Fact]
    public async Task EraseCustomer_PhaseOne_CheckpointThrows_MarkerSurvivesAndCustomerNotAnonymized()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var purgeService = scope.ServiceProvider.GetRequiredService<IConversationPurgeService>();
        var auditLogService = scope.ServiceProvider.GetRequiredService<IAuditLogService>();

        var business = await CreateBusinessAsync(db);
        var customer = await CreateCustomerAsync(db, business.Id);
        var staffUser = await CreateStaffUserAsync(db, business.Id);

        var identity = new ExternalIdentity
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id,
            CustomerId = customer.Id,
            Channel = "Website",
            VerificationMethod = "SignedToken",
            ExternalUserId = "abandoned-after-phase-one",
            LinkedAt = DateTimeOffset.UtcNow,
        };
        db.ExternalIdentities.Add(identity);
        await db.SaveChangesAsync();

        var auditCountBefore = await db.AuditLogs.IgnoreQueryFilters().CountAsync(a => a.BusinessId == business.Id);

        var failingService = new CustomerErasureService(
            db, purgeService, auditLogService, new ThrowAtCustomerErasureCheckpoint("after-phase-1-committed"));

        // Bounded wait — if the checkpoint never fires, EraseCustomerAsync
        // would otherwise run to normal (non-throwing) completion; the
        // WaitAsync still bounds how long that legitimate completion can
        // take, so this never hangs either way.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => failingService.EraseCustomerAsync(customer.Id, staffUser.Id).WaitAsync(TimeSpan.FromSeconds(10)));

        await using var afterScope = _provider.CreateAsyncScope();
        var afterDb = afterScope.ServiceProvider.GetRequiredService<AppDbContext>();

        var afterCustomer = await afterDb.Customers.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.Id == customer.Id);
        Assert.NotNull(afterCustomer.PendingErasureAt);
        Assert.False(afterCustomer.IsAnonymized);
        Assert.Null(afterCustomer.AnonymizedAt);

        // Phase 1 already ran before the throw — the ExternalIdentity row is gone.
        Assert.False(await afterDb.ExternalIdentities.IgnoreQueryFilters().AnyAsync(i => i.Id == identity.Id));

        // No terminal CustomerErased row — Phase 3 never ran.
        var auditCountAfter = await afterDb.AuditLogs.IgnoreQueryFilters().CountAsync(a => a.BusinessId == business.Id);
        Assert.Equal(auditCountBefore, auditCountAfter);
    }

    private async Task<int> CountAdvisoryLockWaitersAsync(NpgsqlConnection barrierConnection, long lockKey)
    {
        var classId = unchecked((uint)(lockKey >> 32));
        var objId = unchecked((uint)lockKey);
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM pg_locks " +
            "WHERE locktype = 'advisory' AND NOT granted AND objsubid = 1 " +
            "AND classid::bigint = @classid AND objid::bigint = @objid",
            barrierConnection);
        cmd.Parameters.AddWithValue("classid", (long)classId);
        cmd.Parameters.AddWithValue("objid", (long)objId);
        return (int)(long)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// Deterministic wait-for-condition, not a fixed sleep — same
    /// established convention as VerificationCodeServiceTests.cs's own
    /// helper of this name.
    /// </summary>
    private async Task WaitForAdvisoryLockWaitersAsync(NpgsqlConnection barrierConnection, long lockKey, int expected, string raceName)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        int count;
        do
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail(
                    $"Expected {expected} concurrent advisory-lock waiters for the {raceName} race within 10s, " +
                    "but they did not appear. If the concurrency protection under test has been removed or is " +
                    "no longer reached, this is the expected failure mode.");
            }

            count = await CountAdvisoryLockWaitersAsync(barrierConnection, lockKey);
            if (count < expected)
            {
                await Task.Delay(25);
            }
        } while (count < expected);
    }

    /// <summary>Opens the barrier connection and pre-acquires the given erasure lock, holding it open until the caller disposes/rolls back the returned transaction.</summary>
    private async Task<(NpgsqlConnection Connection, NpgsqlTransaction Transaction)> AcquireBarrierAsync(long lockKey)
    {
        var connection = new NpgsqlConnection(_adoConnectionString);
        await connection.OpenAsync();
        var transaction = await connection.BeginTransactionAsync();
        await using (var lockCmd = new NpgsqlCommand($"SELECT pg_advisory_xact_lock({lockKey})", connection, transaction))
        {
            await lockCmd.ExecuteNonQueryAsync();
        }
        return (connection, transaction);
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

    /// <summary>Same throws-at-a-named-checkpoint idiom as FailAtCheckpoint above, for ICustomerErasureCheckpoint.</summary>
    private class ThrowAtCustomerErasureCheckpoint(string checkpoint) : ICustomerErasureCheckpoint
    {
        public Task ReachedAsync(string checkpointName, CancellationToken cancellationToken)
        {
            if (checkpointName == checkpoint)
            {
                throw new InvalidOperationException($"Injected test failure at '{checkpointName}'.");
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Same TaskCompletionSource-gated pause idiom as BlockingEmailSender's
    /// SendStarted/Release shape, scoped to one named checkpoint — every
    /// other checkpoint name passes through immediately, un-paused.
    /// </summary>
    private class GatedCustomerErasureCheckpoint(string checkpoint) : ICustomerErasureCheckpoint
    {
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes once the configured checkpoint has actually been reached.</summary>
        public Task Reached => _reached.Task;

        public void Release() => _releaseGate.TrySetResult();

        public async Task ReachedAsync(string checkpointName, CancellationToken cancellationToken)
        {
            if (checkpointName != checkpoint)
            {
                return;
            }

            _reached.TrySetResult();
            await _releaseGate.Task;
        }
    }

    /// <summary>
    /// GDPR Phase 4 — deterministic per-conversation fault injection for
    /// the multi-conversation partial-failure proof. Fails for exactly one,
    /// explicitly-chosen conversationId and delegates to a real
    /// IConversationPurgeService for every other one. Deliberately not
    /// checkpoint/call-count-based (unlike FailAtCheckpoint above): the
    /// discovery query CustomerErasureService.EraseCustomerAsync uses has
    /// no ORDER BY, so "the Nth call" is not a safe way to target a specific
    /// conversation — targeting by conversationId directly is.
    /// </summary>
    private class FailForSpecificConversation(IConversationPurgeService inner, Guid failingConversationId) : IConversationPurgeService
    {
        public async Task PurgeConversationAsync(Guid conversationId, CancellationToken cancellationToken = default)
        {
            if (conversationId == failingConversationId)
            {
                throw new InvalidOperationException($"Injected test failure for conversation {failingConversationId}.");
            }
            await inner.PurgeConversationAsync(conversationId, cancellationToken);
        }
    }

    /// <summary>
    /// GDPR Phase 4 — fault injection for Phase 3's terminal Customer
    /// mutation + completion-audit atomicity proof. Fails only for the
    /// named EventType (CustomerErased), letting every other audit write
    /// (in particular Phase 2's ConversationErasurePurged rows) succeed
    /// normally via the real IAuditLogService.
    /// </summary>
    private class ThrowingOnEventType(IAuditLogService inner, string throwingEventType) : IAuditLogService
    {
        public Task LogAsync(
            Guid businessId, string entityType, string entityId, string eventType, ActorType actor,
            Guid? changedByUserId = null, string? oldValue = null, string? newValue = null,
            Guid? conversationId = null, Guid? correlationId = null, CancellationToken cancellationToken = default)
        {
            if (eventType == throwingEventType)
            {
                throw new InvalidOperationException($"Injected test failure for event type '{throwingEventType}'.");
            }
            return inner.LogAsync(businessId, entityType, entityId, eventType, actor, changedByUserId, oldValue, newValue, conversationId, correlationId, cancellationToken);
        }
    }
}
