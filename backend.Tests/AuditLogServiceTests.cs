using backend.Constants;
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
/// Service-level tests for AuditLogService — same bare-ServiceProvider
/// pattern as ConversationModeServiceTests/TicketSlaServiceTests. Verifies
/// the non-committing contract (LogAsync alone persists nothing; only the
/// caller's own SaveChangesAsync() commits it) and correct field mapping.
/// </summary>
public class AuditLogServiceTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;
    private readonly List<Guid> _businessIdsToClean = [];

    public Task InitializeAsync()
    {
        _provider = new ServiceCollection()
            .AddSingleton<ICurrentTenantAccessor>(new FixedTenantAccessor(null))
            .AddDbContext<AppDbContext>(o => o.UseNpgsql(
                TestDatabase.ConnectionString, x => x.UseVector()))
            .AddScoped<IAuditLogService, AuditLogService>()
            .BuildServiceProvider();

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await TestBusinessCleanup.DeleteThenDisposeAsync(_businessIdsToClean, _provider);
    }

    private async Task<Guid> CreateBusinessAsync(AppDbContext db)
    {
        var business = new Business
        {
            Id = Guid.NewGuid(),
            Name = $"Audit Test {Guid.NewGuid():N}",
            TimeZone = "Europe/Copenhagen",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Businesses.Add(business);
        await db.SaveChangesAsync();
        _businessIdsToClean.Add(business.Id);
        return business.Id;
    }

    [Fact]
    public async Task LogAsync_WithoutCallerSaveChanges_PersistsNothing()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IAuditLogService>();
        var businessId = await CreateBusinessAsync(db);

        var entityId = Guid.NewGuid().ToString();
        await service.LogAsync(
            businessId, AuditEntityTypes.TagModeMapping, entityId, AuditEventTypes.TagModeMappingChanged,
            ActorType.NivoraBusinessStaff);

        // Deliberately no db.SaveChangesAsync() call here.

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var persisted = await verifyDb.AuditLogs.IgnoreQueryFilters().AnyAsync(a => a.EntityId == entityId);

        Assert.False(persisted);
    }

    [Fact]
    public async Task LogAsync_FollowedByCallerSaveChanges_PersistsCorrectly()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IAuditLogService>();
        var businessId = await CreateBusinessAsync(db);

        var entityId = Guid.NewGuid().ToString();
        await service.LogAsync(
            businessId, AuditEntityTypes.TagModeMapping, entityId, AuditEventTypes.TagModeMappingChanged,
            ActorType.NivoraBusinessStaff);
        await db.SaveChangesAsync();

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var persisted = await verifyDb.AuditLogs.IgnoreQueryFilters().SingleAsync(a => a.EntityId == entityId);

        Assert.Equal(businessId, persisted.BusinessId);
        Assert.Equal(AuditEntityTypes.TagModeMapping, persisted.EntityType);
        Assert.Equal(AuditEventTypes.TagModeMappingChanged, persisted.EventType);
        Assert.Equal(ActorType.NivoraBusinessStaff, persisted.Actor);
    }

    [Fact]
    public async Task LogAsync_MapsEveryParameterToItsCorrectColumn()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IAuditLogService>();
        var businessId = await CreateBusinessAsync(db);

        var entityId = Guid.NewGuid().ToString();
        var correlationId = Guid.NewGuid();
        const string oldValue = """{"Tag":"old-tag","Mode":"Auto","IsMinimumFloor":false}""";
        const string newValue = """{"Tag":"new-tag","Mode":"Manual","IsMinimumFloor":true}""";

        await service.LogAsync(
            businessId,
            AuditEntityTypes.TagModeMapping,
            entityId,
            AuditEventTypes.TagModeMappingChanged,
            ActorType.NivoraBusinessStaff,
            changedByUserId: null,
            oldValue: oldValue,
            newValue: newValue,
            conversationId: null,
            correlationId: correlationId);
        await db.SaveChangesAsync();

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var persisted = await verifyDb.AuditLogs.IgnoreQueryFilters().SingleAsync(a => a.EntityId == entityId);

        Assert.Equal(businessId, persisted.BusinessId);
        Assert.Equal(AuditEntityTypes.TagModeMapping, persisted.EntityType);
        Assert.Equal(entityId, persisted.EntityId);
        Assert.Equal(AuditEventTypes.TagModeMappingChanged, persisted.EventType);
        Assert.Equal(ActorType.NivoraBusinessStaff, persisted.Actor);
        Assert.Null(persisted.ChangedByUserId);
        Assert.Equal(oldValue, persisted.OldValue);
        Assert.Equal(newValue, persisted.NewValue);
        Assert.Null(persisted.ConversationId);
        Assert.Equal(correlationId, persisted.CorrelationId);
        Assert.NotEqual(default, persisted.CreatedAt);
    }

    /// <summary>
    /// LogAsync_MapsEveryParameterToItsCorrectColumn above only exercises
    /// ChangedByUserId/ConversationId as null (both are real FKs, so a
    /// non-null value must reference an existing row). ChangedByUserId's
    /// non-null mapping is already verified end-to-end via
    /// TagModeMappingsApiTests.Create_WritesExactlyOneAuditLogRow_WithCorrectFields.
    /// ConversationId's non-null mapping had no coverage anywhere — this
    /// closes that one gap with a real, FK-satisfying Conversation row.
    /// </summary>
    [Fact]
    public async Task LogAsync_NonNullConversationId_PersistsCorrectly()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IAuditLogService>();
        var businessId = await CreateBusinessAsync(db);

        var widget = new Widget
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            Name = "Audit Test Widget",
            PublicKey = Guid.NewGuid().ToString("N"),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Widgets.Add(widget);
        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            WidgetId = widget.Id,
            Channel = "Website",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync();

        var entityId = Guid.NewGuid().ToString();
        await service.LogAsync(
            businessId, AuditEntityTypes.TagModeMapping, entityId, AuditEventTypes.TagModeMappingChanged,
            ActorType.NivoraBusinessStaff, conversationId: conversation.Id);
        await db.SaveChangesAsync();

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var persisted = await verifyDb.AuditLogs.IgnoreQueryFilters().SingleAsync(a => a.EntityId == entityId);

        Assert.Equal(conversation.Id, persisted.ConversationId);
    }

    [Fact]
    public async Task LogAsync_TwiceInSameUnitOfWork_BothPersistTogether()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IAuditLogService>();
        var businessId = await CreateBusinessAsync(db);

        var entityIdA = Guid.NewGuid().ToString();
        var entityIdB = Guid.NewGuid().ToString();
        await service.LogAsync(businessId, AuditEntityTypes.TagModeMapping, entityIdA, AuditEventTypes.TagModeMappingChanged, ActorType.NivoraBusinessStaff);
        await service.LogAsync(businessId, AuditEntityTypes.TagModeMapping, entityIdB, AuditEventTypes.TagModeMappingChanged, ActorType.NivoraBusinessStaff);
        await db.SaveChangesAsync();

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await verifyDb.AuditLogs.IgnoreQueryFilters().AnyAsync(a => a.EntityId == entityIdA));
        Assert.True(await verifyDb.AuditLogs.IgnoreQueryFilters().AnyAsync(a => a.EntityId == entityIdB));
    }

    private class FixedTenantAccessor(Guid? businessId) : ICurrentTenantAccessor
    {
        public Guid? BusinessId => businessId;
    }
}
