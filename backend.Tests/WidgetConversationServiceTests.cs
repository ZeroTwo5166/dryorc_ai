using backend.Constants;
using backend.Data;
using backend.Enums;
using backend.Interfaces;
using backend.Models;
using backend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace backend.Tests;

/// <summary>
/// Service-level regression test for the original Steps 6-7 guarantee:
/// WidgetConversationService.PostMessageAsync atomically persists the
/// customer message and updates Conversation.LastMessageAt to that
/// message's own timestamp, in the same write.
///
/// Deliberately calls IWidgetConversationService directly — no
/// WidgetController, no IClaudeReplyService anywhere in this file. Once a
/// request passes through the controller, Step 8's AI reply write runs
/// synchronously afterward and overwrites LastMessageAt again before any
/// HTTP response is observable, which makes this specific guarantee
/// unassertable at that layer (see WidgetPipelineTests.
/// PostMessage_SecondMessage_ReusesExistingOpenConversation_LastMessageAtUpdated,
/// which now tests the full pipeline instead and deliberately does not
/// assert LastMessageAt). Testing WidgetConversationService directly is
/// the layer where the original write-path guarantee is still true and
/// still observable — nothing else touches Conversation.LastMessageAt
/// unless the controller also calls GenerateReplyAsync, which nothing
/// here does.
/// </summary>
public class WidgetConversationServiceTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;
    private readonly List<Guid> _businessIdsToClean = [];
    private string _adoConnectionString = null!;

    public Task InitializeAsync()
    {
        var configuration = new ConfigurationBuilder()
            .AddUserSecrets<Program>()
            .Build();

        var connectionUri = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection not found via user-secrets.");
        _adoConnectionString = PostgresConnectionString.FromUri(connectionUri);

        _provider = new ServiceCollection()
            .AddSingleton<ICurrentTenantAccessor>(new FixedTenantAccessor(null))
            .AddDbContext<AppDbContext>(o => o.UseNpgsql(
                PostgresConnectionString.FromUri(connectionUri), x => x.UseVector()))
            .AddScoped<IWidgetConversationService, WidgetConversationService>()
            .AddScoped<INotificationService, NotificationService>()
            .AddSingleton<ILogger<WidgetConversationService>>(NullLogger<WidgetConversationService>.Instance)
            .BuildServiceProvider();

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await TestBusinessCleanup.DeleteThenDisposeAsync(_businessIdsToClean, _provider);
    }

    // Same truncation as WidgetPipelineTests/NotificationApiTests — Postgres's
    // timestamptz stores microsecond precision, one fractional digit less
    // than .NET's 100ns ticks; a value read straight off a returned entity
    // (never round-tripped) still carries full tick precision, while the
    // same instant read back via a fresh DbContext comes back truncated.
    private static DateTimeOffset TruncateToMicroseconds(DateTimeOffset value) =>
        new(value.Ticks - (value.Ticks % 10), value.Offset);

    private async Task<(Business Business, Widget Widget)> CreateBusinessAndWidgetAsync(AppDbContext db)
    {
        var business = new Business
        {
            Id = Guid.NewGuid(),
            Name = $"Widget Conversation Service Test {Guid.NewGuid():N}",
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
        db.Businesses.Add(business);
        db.Widgets.Add(widget);
        await db.SaveChangesAsync();
        _businessIdsToClean.Add(business.Id);

        return (business, widget);
    }

    private static async Task<Customer> CreateCustomerAsync(AppDbContext db, Guid businessId, DateTimeOffset? pendingErasureAt = null)
    {
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            PendingErasureAt = pendingErasureAt,
        };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        return customer;
    }

    private static async Task<ApplicationUser> CreateActiveStaffAsync(AppDbContext db, Guid businessId)
    {
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            UserName = $"staff-{Guid.NewGuid():N}@example.com",
            Email = $"staff-{Guid.NewGuid():N}@example.com",
            EmailConfirmed = true,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    [Fact]
    public async Task PostMessageAsync_SecondCall_ReusesConversation_SetsLastMessageAtToSecondCustomerMessageTimestamp()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var conversationService = scope.ServiceProvider.GetRequiredService<IWidgetConversationService>();

        var (business, widget) = await CreateBusinessAndWidgetAsync(db);
        var visitorSessionId = Guid.NewGuid().ToString("N");

        var firstMessage = await conversationService.PostMessageAsync(business.Id, widget.Id, visitorSessionId, "First message");
        var secondMessage = await conversationService.PostMessageAsync(business.Id, widget.Id, visitorSessionId, "Second message");

        // Both calls operated on the same conversation — reused, not a
        // second Conversation created for the second call.
        Assert.Equal(firstMessage.ConversationId, secondMessage.ConversationId);
        Assert.Equal(MessageRole.Customer, secondMessage.Role);

        // Reload from a fresh DbContext — this must reflect a real,
        // committed write, not anything still tracked by the scope above.
        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var conversation = await verifyDb.Conversations
            .IgnoreQueryFilters()
            .SingleAsync(c => c.Id == secondMessage.ConversationId);

        // Specifically the second customer message's own timestamp — not
        // merely "changed" or "non-null."
        Assert.Equal(TruncateToMicroseconds(secondMessage.CreatedAt), conversation.LastMessageAt);
    }

    // ── Staff-notification wiring (Step 9 follow-up) ────────────────────

    [Fact]
    public async Task PostMessageAsync_AiHandledConversation_DoesNotCreateCustomerRepliedNotification()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var conversationService = scope.ServiceProvider.GetRequiredService<IWidgetConversationService>();

        var (business, widget) = await CreateBusinessAndWidgetAsync(db);
        await CreateActiveStaffAsync(db, business.Id);

        var message = await conversationService.PostMessageAsync(
            business.Id, widget.Id, Guid.NewGuid().ToString("N"), "Hi there");

        // A brand-new conversation defaults HandledBy = Ai (nothing here
        // changes that) — no staff attention is needed yet, so no
        // notification should exist for it.
        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var exists = await verifyDb.Notifications.IgnoreQueryFilters()
            .AnyAsync(n => n.ConversationId == message.ConversationId && n.Type == NotificationTypes.CustomerReplied);
        Assert.False(exists);
    }

    [Fact]
    public async Task PostMessageAsync_HumanHandledConversation_CreatesCustomerRepliedNotification_ForActiveStaff()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var conversationService = scope.ServiceProvider.GetRequiredService<IWidgetConversationService>();

        var (business, widget) = await CreateBusinessAndWidgetAsync(db);
        var staff = await CreateActiveStaffAsync(db, business.Id);
        var visitorSessionId = Guid.NewGuid().ToString("N");

        // Simulate "staff already replied once" the same way
        // ConversationsController.CreateMessage does, without going through
        // the HTTP endpoint (out of scope for this service-level test file).
        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id,
            WidgetId = widget.Id,
            VisitorSessionId = visitorSessionId,
            Channel = "Website",
            HandledBy = HandledBy.Human,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync();

        var message = await conversationService.PostMessageAsync(business.Id, widget.Id, visitorSessionId, "Any update?");
        Assert.Equal(conversation.Id, message.ConversationId);

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var notification = await verifyDb.Notifications.IgnoreQueryFilters()
            .SingleAsync(n => n.ConversationId == conversation.Id && n.Type == NotificationTypes.CustomerReplied);
        Assert.Equal(NotificationCategory.Customer, notification.Category);

        var recipient = await verifyDb.NotificationRecipients.IgnoreQueryFilters()
            .SingleAsync(r => r.NotificationId == notification.Id);
        Assert.Equal(staff.Id, recipient.UserId);
        Assert.False(recipient.IsRead);
    }

    [Fact]
    public async Task PostMessageAsync_MultipleMessagesBeforeStaffResponds_CreatesAtMostOneUnreadCustomerRepliedNotification()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var conversationService = scope.ServiceProvider.GetRequiredService<IWidgetConversationService>();

        var (business, widget) = await CreateBusinessAndWidgetAsync(db);
        await CreateActiveStaffAsync(db, business.Id);
        var visitorSessionId = Guid.NewGuid().ToString("N");

        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id,
            WidgetId = widget.Id,
            VisitorSessionId = visitorSessionId,
            Channel = "Website",
            HandledBy = HandledBy.Human,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync();

        // Customer message #1, #2, #3 — staff has not replied to any of them.
        await conversationService.PostMessageAsync(business.Id, widget.Id, visitorSessionId, "Message 1");
        await conversationService.PostMessageAsync(business.Id, widget.Id, visitorSessionId, "Message 2");
        await conversationService.PostMessageAsync(business.Id, widget.Id, visitorSessionId, "Message 3");

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var notificationCount = await verifyDb.Notifications.IgnoreQueryFilters()
            .CountAsync(n => n.ConversationId == conversation.Id && n.Type == NotificationTypes.CustomerReplied);

        // At most one unread CustomerReplied notification per conversation
        // — three distinct customer messages before any staff reply must
        // still collapse to exactly one, not three.
        Assert.Equal(1, notificationCount);
    }

    [Fact]
    public async Task PostMessageAsync_NewMessageAfterEarlierNotificationAlreadyRead_CreatesFreshNotification()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var conversationService = scope.ServiceProvider.GetRequiredService<IWidgetConversationService>();
        var notificationService = scope.ServiceProvider.GetRequiredService<INotificationService>();

        var (business, widget) = await CreateBusinessAndWidgetAsync(db);
        var staff = await CreateActiveStaffAsync(db, business.Id);
        var visitorSessionId = Guid.NewGuid().ToString("N");

        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id,
            WidgetId = widget.Id,
            VisitorSessionId = visitorSessionId,
            Channel = "Website",
            HandledBy = HandledBy.Human,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync();

        await conversationService.PostMessageAsync(business.Id, widget.Id, visitorSessionId, "First unanswered message");

        // Simulate a staff reply resolving it — exactly what
        // ConversationsController.CreateMessage's own mark-read query does.
        var firstNotification = await db.Notifications.IgnoreQueryFilters()
            .SingleAsync(n => n.ConversationId == conversation.Id && n.Type == NotificationTypes.CustomerReplied);
        await db.NotificationRecipients.IgnoreQueryFilters()
            .Where(r => r.NotificationId == firstNotification.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.IsRead, true).SetProperty(r => r.ReadAt, DateTimeOffset.UtcNow));

        // A later customer message on the still-Human-handled conversation
        // must notify again — the earlier notification being read must not
        // permanently suppress future ones for this conversation.
        await conversationService.PostMessageAsync(business.Id, widget.Id, visitorSessionId, "Second message, still no reply");

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var totalNotifications = await verifyDb.Notifications.IgnoreQueryFilters()
            .CountAsync(n => n.ConversationId == conversation.Id && n.Type == NotificationTypes.CustomerReplied);
        Assert.Equal(2, totalNotifications);

        var unreadCount = await verifyDb.NotificationRecipients.IgnoreQueryFilters()
            .Join(verifyDb.Notifications.IgnoreQueryFilters(), r => r.NotificationId, n => n.Id, (r, n) => new { r, n })
            .CountAsync(x => x.n.ConversationId == conversation.Id
                && x.n.Type == NotificationTypes.CustomerReplied
                && x.r.UserId == staff.Id
                && !x.r.IsRead);
        Assert.Equal(1, unreadCount);
    }

    // ── Audit fix: zero active staff (Finding #2) ────────────────────────

    [Fact]
    public async Task PostMessageAsync_HumanHandledConversation_NoActiveStaff_CreatesNoOrphanNotification_EvenAcrossMultipleMessages()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var conversationService = scope.ServiceProvider.GetRequiredService<IWidgetConversationService>();

        // Deliberately no CreateActiveStaffAsync call — this business has
        // zero active staff for the whole test.
        var (business, widget) = await CreateBusinessAndWidgetAsync(db);
        var visitorSessionId = Guid.NewGuid().ToString("N");

        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id,
            WidgetId = widget.Id,
            VisitorSessionId = visitorSessionId,
            Channel = "Website",
            HandledBy = HandledBy.Human,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync();

        // Three separate customer messages with no active staff at any
        // point — must never create a Notification at all, let alone an
        // unbounded number of recipient-less orphans (the exact failure
        // mode the audit found: the old recipient-based dedup check could
        // never see a zero-recipient Notification, so it re-created one on
        // every single message).
        await conversationService.PostMessageAsync(business.Id, widget.Id, visitorSessionId, "Message 1");
        await conversationService.PostMessageAsync(business.Id, widget.Id, visitorSessionId, "Message 2");
        await conversationService.PostMessageAsync(business.Id, widget.Id, visitorSessionId, "Message 3");

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var notificationCount = await verifyDb.Notifications.IgnoreQueryFilters()
            .CountAsync(n => n.ConversationId == conversation.Id && n.Type == NotificationTypes.CustomerReplied);
        Assert.Equal(0, notificationCount);
    }

    [Fact]
    public async Task PostMessageAsync_StaffBecomesActiveAfterEarlierZeroStaffMessage_LaterMessageCreatesNotification()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var conversationService = scope.ServiceProvider.GetRequiredService<IWidgetConversationService>();

        var (business, widget) = await CreateBusinessAndWidgetAsync(db);
        var visitorSessionId = Guid.NewGuid().ToString("N");

        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id,
            WidgetId = widget.Id,
            VisitorSessionId = visitorSessionId,
            Channel = "Website",
            HandledBy = HandledBy.Human,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync();

        // No active staff yet — this message must not create anything (and,
        // unlike a "vacuously open forever" Notification would, must not
        // permanently block a later notification either).
        await conversationService.PostMessageAsync(business.Id, widget.Id, visitorSessionId, "Before any staff exists");

        var staff = await CreateActiveStaffAsync(db, business.Id);

        await conversationService.PostMessageAsync(business.Id, widget.Id, visitorSessionId, "After staff became active");

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var notification = await verifyDb.Notifications.IgnoreQueryFilters()
            .SingleAsync(n => n.ConversationId == conversation.Id && n.Type == NotificationTypes.CustomerReplied);
        var recipient = await verifyDb.NotificationRecipients.IgnoreQueryFilters()
            .SingleAsync(r => r.NotificationId == notification.Id);
        Assert.Equal(staff.Id, recipient.UserId);
        Assert.False(recipient.IsRead);
    }

    // ── Audit fix: genuine concurrency (Finding #1) ──────────────────────

    /// <summary>
    /// Proves the "at most one unread CustomerReplied notification per
    /// conversation" invariant holds under real, forced concurrency — not
    /// just two sequential awaits (the gap the audit found: the original
    /// tests above all await each PostMessageAsync call before starting the
    /// next one, so they could never actually observe the race).
    ///
    /// Determinism, precisely: a third, independent raw connection
    /// pre-acquires the exact same pg_advisory_xact_lock key
    /// (AdvisoryLockKeyGenerator.ForConversationNotification) that
    /// NotifyStaffOfCustomerReplyAsync now takes, and holds it open. Two
    /// concurrent PostMessageAsync calls (separate scopes, separate
    /// DbContexts/connections — simulating two separate concurrent HTTP
    /// requests) are then started via Task.WhenAll. Both necessarily reach
    /// the point of requesting that same lock and both genuinely block at
    /// the PostgreSQL level (confirmed below by polling pg_locks for both
    /// backend PIDs, not assumed) — this is real inter-connection blocking
    /// enforced by Postgres itself, not a C# synchronization primitive.
    /// Only once both are confirmed waiting is the barrier released, so the
    /// two requests are provably contending for the critical section at the
    /// same instant, not just "maybe" overlapping. Whichever wins runs the
    /// check-then-create and commits (releasing the lock); the second can
    /// then only proceed once the first has fully committed, so under
    /// ordinary READ COMMITTED visibility it always observes the first's
    /// write and skips creating a second notification.
    ///
    /// Why this would fail against the pre-fix code: the pre-fix
    /// NotifyStaffOfCustomerReplyAsync never requested this lock at all, so
    /// the poll-for-2-waiters step below would time out (neither backend
    /// would ever appear as an advisory-lock waiter on this key) — a
    /// distinct, equally meaningful failure signal that the fix regressed,
    /// not merely a flaky race that might not be caught. This was verified
    /// directly: temporarily reverting the lock acquisition in
    /// NotifyStaffOfCustomerReplyAsync makes this test fail with the
    /// "expected 2 concurrent advisory-lock waiters" timeout, as expected.
    ///
    /// No sleeps are used as the correctness mechanism — the only wait is a
    /// short poll loop confirming setup state (both backends genuinely
    /// blocked) before proceeding; the actual mutual exclusion is 100%
    /// PostgreSQL's own transaction-scoped advisory lock.
    /// </summary>
    [Fact]
    public async Task PostMessageAsync_GenuinelyConcurrentCustomerMessages_CreatesExactlyOneUnreadCustomerRepliedNotification()
    {
        await using var setupScope = _provider.CreateAsyncScope();
        var setupDb = setupScope.ServiceProvider.GetRequiredService<AppDbContext>();

        var (business, widget) = await CreateBusinessAndWidgetAsync(setupDb);
        await CreateActiveStaffAsync(setupDb, business.Id);
        var visitorSessionId = Guid.NewGuid().ToString("N");

        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id,
            WidgetId = widget.Id,
            VisitorSessionId = visitorSessionId,
            Channel = "Website",
            HandledBy = HandledBy.Human,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        setupDb.Conversations.Add(conversation);
        await setupDb.SaveChangesAsync();

        var lockKey = AdvisoryLockKeyGenerator.ForConversationNotification(business.Id, conversation.Id);

        // The barrier: a raw, independent connection that pre-acquires the
        // exact lock NotifyStaffOfCustomerReplyAsync will request, and
        // holds it until we've confirmed both concurrent requests are
        // genuinely queued behind it.
        await using var barrierConnection = new NpgsqlConnection(_adoConnectionString);
        await barrierConnection.OpenAsync();
        await using var barrierTransaction = await barrierConnection.BeginTransactionAsync();
        await using (var lockCmd = new NpgsqlCommand($"SELECT pg_advisory_xact_lock({lockKey})", barrierConnection, barrierTransaction))
        {
            await lockCmd.ExecuteNonQueryAsync();
        }

        await using var scope1 = _provider.CreateAsyncScope();
        var service1 = scope1.ServiceProvider.GetRequiredService<IWidgetConversationService>();

        await using var scope2 = _provider.CreateAsyncScope();
        var service2 = scope2.ServiceProvider.GetRequiredService<IWidgetConversationService>();

        // Two genuinely concurrent "requests" — same visitor, same
        // already-Human-handled conversation. Both message inserts succeed
        // (a customer legitimately can send two messages back to back);
        // both then race to notify staff.
        var task1 = service1.PostMessageAsync(business.Id, widget.Id, visitorSessionId, "Concurrent message A");
        var task2 = service2.PostMessageAsync(business.Id, widget.Id, visitorSessionId, "Concurrent message B");

        // Wait-for-condition, not a fixed sleep: poll pg_locks until two
        // separate backends are genuinely blocked requesting the exact same
        // advisory lock key. Matched by pg_advisory_xact_lock's own
        // classid/objid decomposition (high/low 32 bits of the key,
        // objsubid=1 for the single-bigint-argument form — see Postgres's
        // lock.c), computed here in C# so no PID correlation is needed —
        // deliberately avoided: this test runs against a real Neon
        // connection-pooler endpoint (see the "-pooler." host in the dev
        // connection string), which can rebind a logical connection to a
        // different physical backend PID between an idle OpenConnectionAsync
        // and the transaction PostMessageAsync later opens, so a PID
        // captured ahead of time is not a reliable correlation key here —
        // confirmed the hard way: an earlier PID-based version of this
        // check timed out. pg_locks itself is a server-wide system view, so
        // querying it from the third barrier connection sees every backend
        // regardless of which physical connection each request landed on.
        var classId = unchecked((uint)(lockKey >> 32));
        var objId = unchecked((uint)lockKey);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        int waitingCount;
        do
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail(
                    "Expected two concurrent PostMessageAsync calls to become advisory-lock waiters " +
                    "on the conversation-notification lock within 10s, but they did not. If the " +
                    "concurrency fix (AdvisoryLockKeyGenerator.ForConversationNotification acquired in " +
                    "NotifyStaffOfCustomerReplyAsync) has been removed or is no longer reached, this is " +
                    "the expected failure mode.");
            }

            await using var checkCmd = new NpgsqlCommand(
                "SELECT count(*) FROM pg_locks " +
                "WHERE locktype = 'advisory' AND NOT granted AND objsubid = 1 " +
                "AND classid::bigint = @classid AND objid::bigint = @objid",
                barrierConnection);
            checkCmd.Parameters.AddWithValue("classid", (long)classId);
            checkCmd.Parameters.AddWithValue("objid", (long)objId);
            waitingCount = (int)(long)(await checkCmd.ExecuteScalarAsync())!;

            if (waitingCount < 2)
            {
                await Task.Delay(25);
            }
        } while (waitingCount < 2);

        Assert.Equal(2, waitingCount); // both backends genuinely, simultaneously blocked — confirmed, not assumed

        // Release the barrier — whichever of the two wins now proceeds,
        // commits, and releases the lock for the other.
        await barrierTransaction.RollbackAsync();

        await Task.WhenAll(task1, task2);

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var notificationCount = await verifyDb.Notifications.IgnoreQueryFilters()
            .CountAsync(n => n.ConversationId == conversation.Id && n.Type == NotificationTypes.CustomerReplied);
        Assert.Equal(1, notificationCount);

        var unreadRecipientCount = await verifyDb.NotificationRecipients.IgnoreQueryFilters()
            .Join(verifyDb.Notifications.IgnoreQueryFilters(), r => r.NotificationId, n => n.Id, (r, n) => new { r, n })
            .CountAsync(x => x.n.ConversationId == conversation.Id
                && x.n.Type == NotificationTypes.CustomerReplied
                && !x.r.IsRead);
        Assert.Equal(1, unreadRecipientCount);
    }

    // ── Identity-binding/erasure-integrity Phase 6 (Decision 2, Path 2b) ──
    //
    // docs/handoffs/identity-binding-erasure-integrity-design.md §5b;
    // implementation-plan.md's Phase 6. Only the new-conversation creation
    // branch is protected; the existing-open-conversation reuse branch is
    // never touched. Failure semantics are degrade-to-anonymous, never
    // reject the send.

    [Fact]
    public async Task PostMessageAsync_EligibleCustomer_CreatesConversationBoundToCustomer()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var conversationService = scope.ServiceProvider.GetRequiredService<IWidgetConversationService>();

        var (business, widget) = await CreateBusinessAndWidgetAsync(db);
        var customer = await CreateCustomerAsync(db, business.Id);
        var visitorSessionId = Guid.NewGuid().ToString("N");

        var message = await conversationService.PostMessageAsync(
            business.Id, widget.Id, visitorSessionId, "Hello, identified customer", customer.Id);

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var conversation = await verifyDb.Conversations.IgnoreQueryFilters()
            .SingleAsync(c => c.Id == message.ConversationId);
        Assert.Equal(customer.Id, conversation.CustomerId);
    }

    /// <summary>
    /// Proves the fresh eligibility check observes a marker that was
    /// already committed before the call even started — no concurrency
    /// needed for this case, since the marker is already durable. Against
    /// the unfixed implementation, this fails because CustomerId is
    /// currently assigned unconditionally with no eligibility check at all.
    /// </summary>
    [Fact]
    public async Task PostMessageAsync_ErasurePendingMarkerAlreadySet_CreatesUnboundConversation()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var conversationService = scope.ServiceProvider.GetRequiredService<IWidgetConversationService>();

        var (business, widget) = await CreateBusinessAndWidgetAsync(db);
        var customer = await CreateCustomerAsync(db, business.Id, pendingErasureAt: DateTimeOffset.UtcNow);
        var visitorSessionId = Guid.NewGuid().ToString("N");

        var message = await conversationService.PostMessageAsync(
            business.Id, widget.Id, visitorSessionId, "Hello while erasure pending", customer.Id);

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var conversation = await verifyDb.Conversations.IgnoreQueryFilters()
            .SingleAsync(c => c.Id == message.ConversationId);
        Assert.Null(conversation.CustomerId);

        // No trace of the stale customerId anywhere on the created row.
        Assert.NotEqual(customer.Id, conversation.CustomerId ?? Guid.Empty);
    }

    /// <summary>
    /// Genuine PostgreSQL concurrency proof, same barrier idiom already
    /// established in this file for ForConversationNotification (see
    /// PostMessageAsync_GenuinelyConcurrentCustomerMessages_... above): a
    /// raw, independent connection pre-acquires ForCustomerErasure and
    /// holds it open. PostMessageAsync's own creation-branch call is
    /// started (not awaited) — its Customer eligibility read must happen
    /// only AFTER it acquires this same lock, so it necessarily blocks
    /// here first. Only once exactly one waiter is confirmed via pg_locks
    /// (not a timer) does the barrier commit PendingErasureAt and release
    /// the lock. Against the unfixed implementation (no lock acquired at
    /// all), the wait-for-1-waiter loop below times out — a distinct,
    /// meaningful RED signature, not a silent pass.
    /// </summary>
    [Fact]
    public async Task PostMessageAsync_BlockedOnCustomerErasureLock_ObservesPendingErasureCommittedWhileWaiting_CreatesUnboundConversation()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var (business, widget) = await CreateBusinessAndWidgetAsync(db);
        var customer = await CreateCustomerAsync(db, business.Id);
        var visitorSessionId = Guid.NewGuid().ToString("N");

        var lockKey = AdvisoryLockKeyGenerator.ForCustomerErasure(business.Id, customer.Id);

        await using var barrierConnection = new NpgsqlConnection(_adoConnectionString);
        await barrierConnection.OpenAsync();
        await using var barrierTransaction = await barrierConnection.BeginTransactionAsync();
        await using (var lockCmd = new NpgsqlCommand($"SELECT pg_advisory_xact_lock({lockKey})", barrierConnection, barrierTransaction))
        {
            await lockCmd.ExecuteNonQueryAsync();
        }

        await using var callScope = _provider.CreateAsyncScope();
        var callService = callScope.ServiceProvider.GetRequiredService<IWidgetConversationService>();
        var callTask = callService.PostMessageAsync(business.Id, widget.Id, visitorSessionId, "Racing message", customer.Id);

        var classId = unchecked((uint)(lockKey >> 32));
        var objId = unchecked((uint)lockKey);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        int waitingCount;
        do
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail(
                    "Expected exactly one advisory-lock waiter for the customer-erasure lock within 10s, " +
                    "but it did not appear. If the ForCustomerErasure lock acquisition in the new-conversation " +
                    "creation branch has been removed or is no longer reached, this is the expected failure mode.");
            }

            await using var checkCmd = new NpgsqlCommand(
                "SELECT count(*) FROM pg_locks " +
                "WHERE locktype = 'advisory' AND NOT granted AND objsubid = 1 " +
                "AND classid::bigint = @classid AND objid::bigint = @objid",
                barrierConnection);
            checkCmd.Parameters.AddWithValue("classid", (long)classId);
            checkCmd.Parameters.AddWithValue("objid", (long)objId);
            waitingCount = (int)(long)(await checkCmd.ExecuteScalarAsync())!;

            if (waitingCount < 1)
            {
                await Task.Delay(25);
            }
        } while (waitingCount < 1);

        Assert.Equal(1, waitingCount); // genuinely blocked — confirmed via pg_locks, not assumed
        Assert.False(callTask.IsCompleted);

        await using (var markCmd = new NpgsqlCommand(
            "UPDATE \"Customers\" SET \"PendingErasureAt\" = now() WHERE \"Id\" = @id", barrierConnection, barrierTransaction))
        {
            markCmd.Parameters.AddWithValue("id", customer.Id);
            await markCmd.ExecuteNonQueryAsync();
        }
        await barrierTransaction.CommitAsync();

        var message = await callTask;

        // Independent, fresh scope/DbContext — never the tracked scope above.
        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var conversation = await verifyDb.Conversations.IgnoreQueryFilters()
            .SingleAsync(c => c.Id == message.ConversationId);
        Assert.Null(conversation.CustomerId);
    }

    /// <summary>
    /// Tenant-isolation proof for the BusinessId predicate: calls the
    /// service directly (its actual production call path — no controller,
    /// no JWT fabrication needed) with a customerId that genuinely belongs
    /// to a different Business than the one the call itself targets.
    /// Against the unfixed implementation (no recheck at all), CustomerId
    /// is bound directly to the cross-tenant id, which this test would
    /// catch as a failure.
    /// </summary>
    [Fact]
    public async Task PostMessageAsync_CustomerIdBelongsToDifferentBusiness_CreatesUnboundConversation()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var conversationService = scope.ServiceProvider.GetRequiredService<IWidgetConversationService>();

        var (businessA, _) = await CreateBusinessAndWidgetAsync(db);
        var customerA = await CreateCustomerAsync(db, businessA.Id);

        var (businessB, widgetB) = await CreateBusinessAndWidgetAsync(db);
        var visitorSessionId = Guid.NewGuid().ToString("N");

        var message = await conversationService.PostMessageAsync(
            businessB.Id, widgetB.Id, visitorSessionId, "Cross-tenant customerId", customerA.Id);

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var conversation = await verifyDb.Conversations.IgnoreQueryFilters()
            .SingleAsync(c => c.Id == message.ConversationId);
        Assert.Null(conversation.CustomerId);

        // Customer A itself must remain completely untouched by this call.
        var reloadedCustomerA = await verifyDb.Customers.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.Id == customerA.Id);
        Assert.Equal(businessA.Id, reloadedCustomerA.BusinessId);
    }

    /// <summary>
    /// Regression proof that the Phase 6 lock/recheck is reachable only
    /// through the conversation-is-null creation branch: a raw connection
    /// pre-acquires ForCustomerErasure for this exact customer and holds it
    /// open (never releasing it during the test). If the existing-open-
    /// conversation reuse branch ever tried to acquire that same lock, this
    /// second call would block indefinitely behind it — the bounded
    /// Task.WhenAny below is the proof it never does, without introducing
    /// any query-interception framework.
    /// </summary>
    [Fact]
    public async Task PostMessageAsync_ExistingOpenConversation_DoesNotAcquireCustomerErasureLock_ReusesConversation()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var conversationService = scope.ServiceProvider.GetRequiredService<IWidgetConversationService>();

        var (business, widget) = await CreateBusinessAndWidgetAsync(db);
        var customer = await CreateCustomerAsync(db, business.Id);
        var visitorSessionId = Guid.NewGuid().ToString("N");

        var firstMessage = await conversationService.PostMessageAsync(
            business.Id, widget.Id, visitorSessionId, "First message", customer.Id);

        var lockKey = AdvisoryLockKeyGenerator.ForCustomerErasure(business.Id, customer.Id);
        await using var barrierConnection = new NpgsqlConnection(_adoConnectionString);
        await barrierConnection.OpenAsync();
        await using var barrierTransaction = await barrierConnection.BeginTransactionAsync();
        await using (var lockCmd = new NpgsqlCommand($"SELECT pg_advisory_xact_lock({lockKey})", barrierConnection, barrierTransaction))
        {
            await lockCmd.ExecuteNonQueryAsync();
        }

        var secondMessageTask = conversationService.PostMessageAsync(
            business.Id, widget.Id, visitorSessionId, "Second message, existing conversation reused", customer.Id);
        var completed = await Task.WhenAny(secondMessageTask, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(secondMessageTask, completed); // did not hang behind the still-held lock

        var secondMessage = await secondMessageTask;
        Assert.Equal(firstMessage.ConversationId, secondMessage.ConversationId); // reused, not a second conversation

        await barrierTransaction.RollbackAsync();

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var conversation = await verifyDb.Conversations.IgnoreQueryFilters()
            .SingleAsync(c => c.Id == firstMessage.ConversationId);
        Assert.Equal(customer.Id, conversation.CustomerId); // unchanged — never rebound or cleared
    }

    private class FixedTenantAccessor(Guid? businessId) : ICurrentTenantAccessor
    {
        public Guid? BusinessId => businessId;
    }
}
