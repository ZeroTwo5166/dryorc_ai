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
/// Service-level tests for Level2VerificationChecker — real Postgres, bare
/// ServiceProvider, same pattern as ConversationModeServiceTests. No
/// production HTTP caller exists yet for this service (see its own doc
/// comment).
/// </summary>
public class Level2VerificationCheckerTests : IAsyncLifetime
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
            .AddScoped<ILevel2VerificationChecker, Level2VerificationChecker>()
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
            Name = $"Level2 Checker Test {Guid.NewGuid():N}",
            TimeZone = "Europe/Copenhagen",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Businesses.Add(business);
        await db.SaveChangesAsync();
        _businessIdsToClean.Add(business.Id);
        return business.Id;
    }

    private static async Task<Guid> CreateWidgetAsync(AppDbContext db, Guid businessId)
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
        return widget.Id;
    }

    private static async Task<Conversation> CreateConversationAsync(
        AppDbContext db, Guid businessId, Guid widgetId,
        DateTimeOffset? level2VerifiedUntil = null, Guid? customerId = null)
    {
        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            WidgetId = widgetId,
            CustomerId = customerId,
            Channel = "Website",
            Status = ConversationStatus.Open,
            HandledBy = HandledBy.Ai,
            Level2VerifiedUntil = level2VerifiedUntil,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync();
        return conversation;
    }

    private static async Task<Guid> CreateCustomerAsync(AppDbContext db, Guid businessId)
    {
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        return customer.Id;
    }

    private static async Task SeedExternalIdentityAsync(
        AppDbContext db, Guid businessId, Guid customerId, DateTimeOffset? revokedAt, DateTimeOffset? expiresAt)
    {
        db.ExternalIdentities.Add(new ExternalIdentity
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            CustomerId = customerId,
            Channel = "Website",
            VerificationMethod = "SignedToken",
            ExternalUserId = Guid.NewGuid().ToString("N"),
            LinkedAt = DateTimeOffset.UtcNow,
            ExpiresAt = expiresAt,
            RevokedAt = revokedAt,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task IsVerifiedAsync_Level2VerifiedUntilInFuture_ReturnsTrue()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var checker = scope.ServiceProvider.GetRequiredService<ILevel2VerificationChecker>();
        var businessId = await CreateBusinessAsync(db);
        var widgetId = await CreateWidgetAsync(db, businessId);
        var conversation = await CreateConversationAsync(db, businessId, widgetId, level2VerifiedUntil: DateTimeOffset.UtcNow.AddMinutes(30));

        Assert.True(await checker.IsVerifiedAsync(businessId, conversation.Id, CancellationToken.None));
    }

    [Fact]
    public async Task IsVerifiedAsync_Level2VerifiedUntilInPast_ReturnsFalse()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var checker = scope.ServiceProvider.GetRequiredService<ILevel2VerificationChecker>();
        var businessId = await CreateBusinessAsync(db);
        var widgetId = await CreateWidgetAsync(db, businessId);
        var conversation = await CreateConversationAsync(db, businessId, widgetId, level2VerifiedUntil: DateTimeOffset.UtcNow.AddMinutes(-5));

        Assert.False(await checker.IsVerifiedAsync(businessId, conversation.Id, CancellationToken.None));
    }

    [Fact]
    public async Task IsVerifiedAsync_ValidExternalIdentityOnly_ReturnsTrue()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var checker = scope.ServiceProvider.GetRequiredService<ILevel2VerificationChecker>();
        var businessId = await CreateBusinessAsync(db);
        var widgetId = await CreateWidgetAsync(db, businessId);
        var customerId = await CreateCustomerAsync(db, businessId);
        var conversation = await CreateConversationAsync(db, businessId, widgetId, customerId: customerId);
        await SeedExternalIdentityAsync(db, businessId, customerId, revokedAt: null, expiresAt: null);

        Assert.True(await checker.IsVerifiedAsync(businessId, conversation.Id, CancellationToken.None));
    }

    [Fact]
    public async Task IsVerifiedAsync_RevokedExternalIdentityAndNoLevel2VerifiedUntil_ReturnsFalse()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var checker = scope.ServiceProvider.GetRequiredService<ILevel2VerificationChecker>();
        var businessId = await CreateBusinessAsync(db);
        var widgetId = await CreateWidgetAsync(db, businessId);
        var customerId = await CreateCustomerAsync(db, businessId);
        var conversation = await CreateConversationAsync(db, businessId, widgetId, customerId: customerId);
        await SeedExternalIdentityAsync(db, businessId, customerId, revokedAt: DateTimeOffset.UtcNow, expiresAt: null);

        Assert.False(await checker.IsVerifiedAsync(businessId, conversation.Id, CancellationToken.None));
    }

    [Fact]
    public async Task IsVerifiedAsync_NeitherConditionHolds_ReturnsFalse()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var checker = scope.ServiceProvider.GetRequiredService<ILevel2VerificationChecker>();
        var businessId = await CreateBusinessAsync(db);
        var widgetId = await CreateWidgetAsync(db, businessId);
        var conversation = await CreateConversationAsync(db, businessId, widgetId);

        Assert.False(await checker.IsVerifiedAsync(businessId, conversation.Id, CancellationToken.None));
    }

    [Fact]
    public async Task IsVerifiedAsync_UnknownConversation_ReturnsFalse()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var checker = scope.ServiceProvider.GetRequiredService<ILevel2VerificationChecker>();
        var businessId = await CreateBusinessAsync(db);

        Assert.False(await checker.IsVerifiedAsync(businessId, Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task IsVerifiedAsync_ConversationBelongsToDifferentBusiness_ReturnsFalse()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var checker = scope.ServiceProvider.GetRequiredService<ILevel2VerificationChecker>();
        var businessId = await CreateBusinessAsync(db);
        var otherBusinessId = await CreateBusinessAsync(db);
        var widgetId = await CreateWidgetAsync(db, businessId);
        var conversation = await CreateConversationAsync(db, businessId, widgetId, level2VerifiedUntil: DateTimeOffset.UtcNow.AddMinutes(30));

        Assert.False(await checker.IsVerifiedAsync(otherBusinessId, conversation.Id, CancellationToken.None));
    }

    private class FixedTenantAccessor(Guid? businessId) : ICurrentTenantAccessor
    {
        public Guid? BusinessId => businessId;
    }
}
