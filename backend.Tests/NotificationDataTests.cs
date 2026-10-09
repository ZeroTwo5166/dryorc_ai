using backend.Constants;
using backend.Data;
using backend.Enums;
using backend.Interfaces;
using backend.Models;
using backend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace backend.Tests;

/// <summary>
/// Schema/service-level tests for Notification/NotificationRecipient, run
/// against the real Neon dev DB via a bare ServiceProvider — same pattern
/// as CustomerDataLifecycleTests, since none of this is exercised through
/// an HTTP endpoint for these particular cases (cross-user HTTP isolation
/// and read-all cursor behavior live in NotificationApiTests instead).
///
/// NOTE: these tests require the AddNotificationSystem migration to be
/// applied to the target database. They will fail with Postgres error
/// 42P01 ("relation does not exist") until that migration is applied —
/// expected and reported as such, not a defect in this test file.
/// </summary>
public class NotificationDataTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;
    private readonly List<Guid> _businessIdsToClean = [];
    private readonly List<Guid> _userIdsToClean = [];

    public Task InitializeAsync()
    {
        _provider = new ServiceCollection()
            .AddSingleton<ICurrentTenantAccessor>(new FixedTenantAccessor(null))
            .AddDbContext<AppDbContext>(o => o.UseNpgsql(
                TestDatabase.ConnectionString, x => x.UseVector()))
            .AddScoped<INotificationService, NotificationService>()
            .AddScoped<IConversationPurgeService, ConversationPurgeService>()
            .AddScoped<IPurgeFailurePoint, NoOpPurgeFailurePoint>()
            .BuildServiceProvider();

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await TestBusinessCleanup.DeleteThenDisposeAsync(_businessIdsToClean, _provider);
    }

    // ── Fixture helpers ─────────────────────────────────────────────

    private async Task<Business> CreateBusinessAsync(AppDbContext db)
    {
        var business = new Business
        {
            Id = Guid.NewGuid(),
            Name = $"Test Business {Guid.NewGuid():N}",
            TimeZone = "Europe/Copenhagen",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Businesses.Add(business);
        await db.SaveChangesAsync();
        _businessIdsToClean.Add(business.Id);
        return business;
    }

    private async Task<ApplicationUser> CreateStaffUserAsync(AppDbContext db, Guid businessId, bool isActive = true)
    {
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            UserName = $"staff-{Guid.NewGuid():N}@example.com",
            Email = $"staff-{Guid.NewGuid():N}@example.com",
            EmailConfirmed = true,
            IsActive = isActive,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        _userIdsToClean.Add(user.Id);
        return user;
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

    private async Task<Conversation> CreateConversationAsync(AppDbContext db, Guid businessId, Guid widgetId)
    {
        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            WidgetId = widgetId,
            VisitorSessionId = $"session-{Guid.NewGuid():N}",
            Channel = "Website",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            LastMessageAt = DateTimeOffset.UtcNow,
        };
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync();
        return conversation;
    }

    private static Notification NewValidNotification(Guid businessId, string type, string? idempotencyKey = null) => new()
    {
        Id = Guid.NewGuid(),
        BusinessId = businessId,
        Category = NotificationCategoryMap.Resolve(type),
        Type = type,
        Title = "Test notification",
        ActorType = ActorType.NivoraSystem,
        IdempotencyKey = idempotencyKey,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static async Task<PostgresException?> CaptureConstraintViolationAsync(Func<Task> action)
    {
        var ex = await Record.ExceptionAsync(action);
        return ex switch
        {
            PostgresException pg => pg,
            DbUpdateException { InnerException: PostgresException pg } => pg,
            _ => null,
        };
    }

    // ── Test 1 — cross-tenant idempotency ───────────────────────────

    [Fact]
    public async Task IdempotencyKey_SameValue_DifferentBusinesses_BothSucceed()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var businessA = await CreateBusinessAsync(db);
        var businessB = await CreateBusinessAsync(db);
        const string key = "shared-key";

        db.Notifications.Add(NewValidNotification(businessA.Id, NotificationTypes.TeamMemberJoined, key));
        db.Notifications.Add(NewValidNotification(businessB.Id, NotificationTypes.TeamMemberJoined, key));

        var ex = await Record.ExceptionAsync(() => db.SaveChangesAsync());
        Assert.Null(ex);
    }

    // ── Test 2 — same-tenant idempotency (also proves atomic fan-out
    //             rollback, per the explicit atomicity requirement: a
    //             constraint violation on the Notification insert must
    //             leave zero rows behind, not a partial write) ────────

    [Fact]
    public async Task IdempotencyKey_SameValue_SameBusiness_SecondInsertFails23505()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var notificationService = scope.ServiceProvider.GetRequiredService<INotificationService>();

        var business = await CreateBusinessAsync(db);
        var staff = await CreateStaffUserAsync(db, business.Id);
        const string key = "duplicate-key";

        // First call goes through NotifyAsync (real transaction, real
        // fan-out) to also prove the happy path creates exactly one
        // recipient row.
        await notificationService.NotifyAsync(
            business.Id, NotificationTypes.TeamMemberJoined, new TeamMemberJoinedArgs(),
            ActorType.NivoraSystem, idempotencyKey: key);

        var countAfterFirst = await db.Notifications.IgnoreQueryFilters().CountAsync(n => n.BusinessId == business.Id);
        var recipientCountAfterFirst = await db.NotificationRecipients.IgnoreQueryFilters().CountAsync(r => r.BusinessId == business.Id);
        Assert.Equal(1, countAfterFirst);
        Assert.Equal(1, recipientCountAfterFirst);

        // Second call with the same key must fail atomically — proving
        // both idempotency enforcement AND that NotifyAsync's transaction
        // leaves no partial recipient row behind on failure.
        var pgEx = await CaptureConstraintViolationAsync(() => notificationService.NotifyAsync(
            business.Id, NotificationTypes.TeamMemberJoined, new TeamMemberJoinedArgs(),
            ActorType.NivoraSystem, idempotencyKey: key));

        Assert.NotNull(pgEx);
        Assert.Equal("23505", pgEx!.SqlState);

        var countAfterSecond = await db.Notifications.IgnoreQueryFilters().CountAsync(n => n.BusinessId == business.Id);
        var recipientCountAfterSecond = await db.NotificationRecipients.IgnoreQueryFilters().CountAsync(r => r.BusinessId == business.Id);
        Assert.Equal(1, countAfterSecond); // still just the first — no partial second row
        Assert.Equal(1, recipientCountAfterSecond);
    }

    // ── Test 3 — every NotificationType against NotificationCategoryMap ─

    [Theory]
    [InlineData(NotificationTypes.TeamMemberJoined)]
    [InlineData(NotificationTypes.StaffRoleChanged)]
    [InlineData(NotificationTypes.StaffAccessRevoked)]
    [InlineData(NotificationTypes.ConversationAssigned)]
    [InlineData(NotificationTypes.TicketSlaWarning)]
    [InlineData(NotificationTypes.CustomerReplied)]
    [InlineData(NotificationTypes.NewConversation)]
    [InlineData(NotificationTypes.ConversationEscalated)]
    [InlineData(NotificationTypes.KnowledgeImportCompleted)]
    [InlineData(NotificationTypes.IntegrationSyncCompleted)]
    public async Task ValidTypeCategoryPair_Succeeds(string type)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var business = await CreateBusinessAsync(db);
        db.Notifications.Add(NewValidNotification(business.Id, type));

        var ex = await Record.ExceptionAsync(() => db.SaveChangesAsync());
        Assert.Null(ex);
    }

    // ── Test 4 — invalid Type/Category combinations ─────────────────

    [Theory]
    [InlineData(NotificationCategory.Team, NotificationTypes.ConversationAssigned)]
    [InlineData(NotificationCategory.Team, NotificationTypes.KnowledgeImportCompleted)]
    [InlineData(NotificationCategory.Customer, NotificationTypes.TeamMemberJoined)]
    [InlineData(NotificationCategory.Customer, NotificationTypes.IntegrationSyncCompleted)]
    [InlineData(NotificationCategory.System, NotificationTypes.StaffRoleChanged)]
    [InlineData(NotificationCategory.System, NotificationTypes.CustomerReplied)]
    public async Task MismatchedTypeCategoryPair_Fails23514_CategoryTypeConsistency(
        NotificationCategory wrongCategory, string type)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var business = await CreateBusinessAsync(db);
        var notification = NewValidNotification(business.Id, type);
        notification.Category = wrongCategory; // deliberately mismatched against Type
        db.Notifications.Add(notification);

        var pgEx = await CaptureConstraintViolationAsync(() => db.SaveChangesAsync());

        Assert.NotNull(pgEx);
        Assert.Equal("23514", pgEx!.SqlState);
        Assert.Equal("CK_Notifications_CategoryTypeConsistency", pgEx.ConstraintName);
    }

    // ── Test 5 — Team references ─────────────────────────────────────

    [Fact]
    public async Task TeamNotification_WithConversationId_Fails23514_CategoryRefs()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var business = await CreateBusinessAsync(db);
        var widget = await CreateWidgetAsync(db, business.Id);
        var conversation = await CreateConversationAsync(db, business.Id, widget.Id);

        var notification = NewValidNotification(business.Id, NotificationTypes.TeamMemberJoined);
        notification.ConversationId = conversation.Id; // Team notifications must never reference a conversation
        db.Notifications.Add(notification);

        var pgEx = await CaptureConstraintViolationAsync(() => db.SaveChangesAsync());

        Assert.NotNull(pgEx);
        Assert.Equal("23514", pgEx!.SqlState);
        Assert.Equal("CK_Notifications_CategoryRefs", pgEx.ConstraintName);
    }

    // ── Test 6 — explicit fan-out authorization ─────────────────────

    [Fact]
    public async Task NotifyAsync_ExplicitTargets_OnlyActiveSameBusinessUserReceivesRecipient()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var notificationService = scope.ServiceProvider.GetRequiredService<INotificationService>();

        var businessA = await CreateBusinessAsync(db);
        var businessB = await CreateBusinessAsync(db);

        var activeSameBusiness = await CreateStaffUserAsync(db, businessA.Id, isActive: true);
        var inactiveSameBusiness = await CreateStaffUserAsync(db, businessA.Id, isActive: false);
        var activeOtherBusiness = await CreateStaffUserAsync(db, businessB.Id, isActive: true);

        await notificationService.NotifyAsync(
            businessA.Id, NotificationTypes.TeamMemberJoined, new TeamMemberJoinedArgs(),
            ActorType.NivoraSystem,
            targetUserIds: [activeSameBusiness.Id, inactiveSameBusiness.Id, activeOtherBusiness.Id]);

        var notification = await db.Notifications.IgnoreQueryFilters().SingleAsync(n => n.BusinessId == businessA.Id);
        var recipientUserIds = await db.NotificationRecipients.IgnoreQueryFilters()
            .Where(r => r.NotificationId == notification.Id)
            .Select(r => r.UserId)
            .ToListAsync();

        Assert.Single(recipientUserIds);
        Assert.Contains(activeSameBusiness.Id, recipientUserIds);
        Assert.DoesNotContain(inactiveSameBusiness.Id, recipientUserIds);
        Assert.DoesNotContain(activeOtherBusiness.Id, recipientUserIds);
    }

    // ── Test 6b — broadcast authorization ───────────────────────────

    [Fact]
    public async Task NotifyAsync_Broadcast_OnlyActiveSameBusinessStaffReceiveRecipients()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var notificationService = scope.ServiceProvider.GetRequiredService<INotificationService>();

        var businessA = await CreateBusinessAsync(db);
        var businessB = await CreateBusinessAsync(db);

        var activeOne = await CreateStaffUserAsync(db, businessA.Id, isActive: true);
        var activeTwo = await CreateStaffUserAsync(db, businessA.Id, isActive: true);
        var inactive = await CreateStaffUserAsync(db, businessA.Id, isActive: false);
        var otherBusinessActive = await CreateStaffUserAsync(db, businessB.Id, isActive: true);

        // targetUserIds omitted entirely — exercises the broadcast branch.
        await notificationService.NotifyAsync(
            businessA.Id, NotificationTypes.TeamMemberJoined, new TeamMemberJoinedArgs(), ActorType.NivoraSystem);

        var notification = await db.Notifications.IgnoreQueryFilters().SingleAsync(n => n.BusinessId == businessA.Id);
        var recipientUserIds = await db.NotificationRecipients.IgnoreQueryFilters()
            .Where(r => r.NotificationId == notification.Id)
            .Select(r => r.UserId)
            .ToListAsync();

        Assert.Equal(2, recipientUserIds.Count);
        Assert.Contains(activeOne.Id, recipientUserIds);
        Assert.Contains(activeTwo.Id, recipientUserIds);
        Assert.DoesNotContain(inactive.Id, recipientUserIds);
        Assert.DoesNotContain(otherBusinessActive.Id, recipientUserIds);
    }

    // ── Test 8 — conversation purge integration ─────────────────────

    [Fact]
    public async Task ConversationPurge_DeletesNotifications_CascadesRecipients_LeavesConversationAnonymized()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var purgeService = scope.ServiceProvider.GetRequiredService<IConversationPurgeService>();
        var notificationService = scope.ServiceProvider.GetRequiredService<INotificationService>();

        var business = await CreateBusinessAsync(db);
        var staff = await CreateStaffUserAsync(db, business.Id);
        var widget = await CreateWidgetAsync(db, business.Id);
        var conversation = await CreateConversationAsync(db, business.Id, widget.Id);

        db.Messages.Add(new Message
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id,
            ConversationId = conversation.Id,
            Role = MessageRole.Customer,
            Content = "Hello",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        await notificationService.NotifyAsync(
            business.Id, NotificationTypes.ConversationAssigned, new ConversationAssignedArgs(),
            ActorType.NivoraSystem, conversationId: conversation.Id, targetUserIds: [staff.Id]);

        var notification = await db.Notifications.IgnoreQueryFilters().SingleAsync(n => n.ConversationId == conversation.Id);

        await purgeService.PurgeConversationAsync(conversation.Id);

        Assert.False(await db.Messages.IgnoreQueryFilters().AnyAsync(m => m.ConversationId == conversation.Id));
        Assert.False(await db.Notifications.IgnoreQueryFilters().AnyAsync(n => n.Id == notification.Id));
        Assert.False(await db.NotificationRecipients.IgnoreQueryFilters().AnyAsync(r => r.NotificationId == notification.Id));

        var conversationAfter = await db.Conversations.IgnoreQueryFilters().SingleAsync(c => c.Id == conversation.Id);
        Assert.True(conversationAfter.IsAnonymized);
    }

    // ── Test 9 — purge rollback, reusing the existing failure-injection
    //             seam (IPurgeFailurePoint) rather than an arbitrary
    //             constraint violation ───────────────────────────────

    [Fact]
    public async Task ConversationPurge_RollsBack_WhenFailureInjectedAfterNotificationDelete()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var notificationService = scope.ServiceProvider.GetRequiredService<INotificationService>();

        var business = await CreateBusinessAsync(db);
        var staff = await CreateStaffUserAsync(db, business.Id);
        var widget = await CreateWidgetAsync(db, business.Id);
        var conversation = await CreateConversationAsync(db, business.Id, widget.Id);

        db.Messages.Add(new Message
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id,
            ConversationId = conversation.Id,
            Role = MessageRole.Customer,
            Content = "Hello",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        await notificationService.NotifyAsync(
            business.Id, NotificationTypes.ConversationAssigned, new ConversationAssignedArgs(),
            ActorType.NivoraSystem, conversationId: conversation.Id, targetUserIds: [staff.Id]);

        var notification = await db.Notifications.IgnoreQueryFilters().SingleAsync(n => n.ConversationId == conversation.Id);

        // The existing seam's checkpoint sits between the Messages delete
        // and the Notifications delete — "after-message-delete" now fires
        // after both deletes have executed but before the transaction
        // commits, which is exactly "after Notification deletion but
        // before commit" as required.
        var failingService = new ConversationPurgeService(db, new FailAtCheckpoint("after-message-delete"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => failingService.PurgeConversationAsync(conversation.Id));

        Assert.True(await db.Messages.IgnoreQueryFilters().AnyAsync(m => m.ConversationId == conversation.Id));
        Assert.True(await db.Notifications.IgnoreQueryFilters().AnyAsync(n => n.Id == notification.Id));
        Assert.True(await db.NotificationRecipients.IgnoreQueryFilters().AnyAsync(r => r.NotificationId == notification.Id));

        var conversationAfterFailure = await db.Conversations.IgnoreQueryFilters().SingleAsync(c => c.Id == conversation.Id);
        Assert.False(conversationAfterFailure.IsAnonymized);
    }

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
