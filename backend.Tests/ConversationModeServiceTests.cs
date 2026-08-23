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
/// Service-level tests for ConversationModeService — real Postgres, bare
/// ServiceProvider, same pattern as WidgetConversationServiceTests/
/// KnowledgeIndexingServiceTests. No production caller/HTTP layer involved
/// — this service has none yet, deliberately (see its own doc comment).
/// </summary>
public class ConversationModeServiceTests : IAsyncLifetime
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
            .AddScoped<IConversationModeService, ConversationModeService>()
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

            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"ExternalIdentities\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Conversations\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Widgets\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"TagModeMappings\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Customers\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Businesses\" WHERE \"Id\" = ANY({ids})");
        }

        await _provider.DisposeAsync();
    }

    private async Task<Guid> CreateBusinessAsync(AppDbContext db)
    {
        var business = new Business
        {
            Id = Guid.NewGuid(),
            Name = $"Mode Test {Guid.NewGuid():N}",
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
        AppDbContext db, Guid businessId, Guid widgetId, List<string> tags,
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
            Tags = tags,
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

    private static async Task SeedTagModeMappingAsync(
        AppDbContext db, Guid businessId, string tag, ConversationMode mode, bool isMinimumFloor)
    {
        db.TagModeMappings.Add(new TagModeMapping
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            Tag = tag,
            Mode = mode,
            IsMinimumFloor = isMinimumFloor,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
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

    // ── Step 1: base mode ────────────────────────────────────────────

    [Fact]
    public async Task ComputeEffectiveModeAsync_NoTags_ReturnsAuto()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IConversationModeService>();
        var businessId = await CreateBusinessAsync(db);
        var widgetId = await CreateWidgetAsync(db, businessId);
        var conversation = await CreateConversationAsync(db, businessId, widgetId, [], level2VerifiedUntil: DateTimeOffset.UtcNow.AddHours(1));

        var mode = await service.ComputeEffectiveModeAsync(conversation);

        Assert.Equal(ConversationMode.Auto, mode);
    }

    [Fact]
    public async Task ComputeEffectiveModeAsync_TagsPresentButNoneMatch_ReturnsAuto()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IConversationModeService>();
        var businessId = await CreateBusinessAsync(db);
        var widgetId = await CreateWidgetAsync(db, businessId);
        await SeedTagModeMappingAsync(db, businessId, "billing", ConversationMode.Manual, isMinimumFloor: false);
        var conversation = await CreateConversationAsync(
            db, businessId, widgetId, ["unrelated-tag"], level2VerifiedUntil: DateTimeOffset.UtcNow.AddHours(1));

        var mode = await service.ComputeEffectiveModeAsync(conversation);

        Assert.Equal(ConversationMode.Auto, mode);
    }

    [Fact]
    public async Task ComputeEffectiveModeAsync_SingleMatchingTag_ReturnsItsMode()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IConversationModeService>();
        var businessId = await CreateBusinessAsync(db);
        var widgetId = await CreateWidgetAsync(db, businessId);
        await SeedTagModeMappingAsync(db, businessId, "shipping-question", ConversationMode.Assist, isMinimumFloor: false);
        var conversation = await CreateConversationAsync(
            db, businessId, widgetId, ["shipping-question"], level2VerifiedUntil: DateTimeOffset.UtcNow.AddHours(1));

        var mode = await service.ComputeEffectiveModeAsync(conversation);

        Assert.Equal(ConversationMode.Assist, mode);
    }

    [Fact]
    public async Task ComputeEffectiveModeAsync_MultipleMatchingNonFloorTags_ResolvesToStrictest()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IConversationModeService>();
        var businessId = await CreateBusinessAsync(db);
        var widgetId = await CreateWidgetAsync(db, businessId);
        await SeedTagModeMappingAsync(db, businessId, "general", ConversationMode.Auto, isMinimumFloor: false);
        await SeedTagModeMappingAsync(db, businessId, "complaint", ConversationMode.Assist, isMinimumFloor: false);
        await SeedTagModeMappingAsync(db, businessId, "legal-threat", ConversationMode.Manual, isMinimumFloor: false);
        var conversation = await CreateConversationAsync(
            db, businessId, widgetId, ["general", "complaint", "legal-threat"],
            level2VerifiedUntil: DateTimeOffset.UtcNow.AddHours(1));

        var mode = await service.ComputeEffectiveModeAsync(conversation);

        Assert.Equal(ConversationMode.Manual, mode); // strictest of Auto/Assist/Manual wins, not first/last/db-order
    }

    [Fact]
    public async Task ComputeEffectiveModeAsync_TagMatching_IsCaseInsensitive()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IConversationModeService>();
        var businessId = await CreateBusinessAsync(db);
        var widgetId = await CreateWidgetAsync(db, businessId);
        await SeedTagModeMappingAsync(db, businessId, "refund-dispute", ConversationMode.Manual, isMinimumFloor: false);
        var conversation = await CreateConversationAsync(
            db, businessId, widgetId, ["REFUND-Dispute"], level2VerifiedUntil: DateTimeOffset.UtcNow.AddHours(1));

        var mode = await service.ComputeEffectiveModeAsync(conversation);

        Assert.Equal(ConversationMode.Manual, mode);
    }

    [Fact]
    public async Task ComputeEffectiveModeAsync_TenantIsolation_NeverMatchesAnotherBusinesssMapping()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IConversationModeService>();
        var businessIdA = await CreateBusinessAsync(db);
        var businessIdB = await CreateBusinessAsync(db);
        var widgetIdB = await CreateWidgetAsync(db, businessIdB);

        // Business A has a mapping that WOULD force Manual — Business B's
        // conversation uses the same tag but must never see it.
        await SeedTagModeMappingAsync(db, businessIdA, "shared-tag", ConversationMode.Manual, isMinimumFloor: false);
        var conversation = await CreateConversationAsync(
            db, businessIdB, widgetIdB, ["shared-tag"], level2VerifiedUntil: DateTimeOffset.UtcNow.AddHours(1));

        var mode = await service.ComputeEffectiveModeAsync(conversation);

        Assert.Equal(ConversationMode.Auto, mode);
    }

    // ── Step 3: minimum floor ────────────────────────────────────────

    [Fact]
    public async Task ComputeEffectiveModeAsync_MinimumFloor_MakesModeStricter()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IConversationModeService>();
        var businessId = await CreateBusinessAsync(db);
        var widgetId = await CreateWidgetAsync(db, businessId);
        await SeedTagModeMappingAsync(db, businessId, "general", ConversationMode.Auto, isMinimumFloor: false);
        await SeedTagModeMappingAsync(db, businessId, "refund-dispute", ConversationMode.Manual, isMinimumFloor: true);
        var conversation = await CreateConversationAsync(
            db, businessId, widgetId, ["general", "refund-dispute"], level2VerifiedUntil: DateTimeOffset.UtcNow.AddHours(1));

        var mode = await service.ComputeEffectiveModeAsync(conversation);

        Assert.Equal(ConversationMode.Manual, mode);
    }

    [Fact]
    public async Task ComputeEffectiveModeAsync_MultipleMinimumFloors_SelectsStrictest()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IConversationModeService>();
        var businessId = await CreateBusinessAsync(db);
        var widgetId = await CreateWidgetAsync(db, businessId);
        await SeedTagModeMappingAsync(db, businessId, "floor-assist", ConversationMode.Assist, isMinimumFloor: true);
        await SeedTagModeMappingAsync(db, businessId, "floor-manual", ConversationMode.Manual, isMinimumFloor: true);
        var conversation = await CreateConversationAsync(
            db, businessId, widgetId, ["floor-assist", "floor-manual"], level2VerifiedUntil: DateTimeOffset.UtcNow.AddHours(1));

        var mode = await service.ComputeEffectiveModeAsync(conversation);

        Assert.Equal(ConversationMode.Manual, mode);
    }

    [Fact]
    public async Task ComputeEffectiveModeAsync_MinimumFloor_NeverWeakensAnAlreadyStricterBaseMode()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IConversationModeService>();
        var businessId = await CreateBusinessAsync(db);
        var widgetId = await CreateWidgetAsync(db, businessId);
        // Base mode resolves to Manual (non-floor tag); a separate floor tag
        // specifies only Assist — must NOT weaken Manual down to Assist.
        await SeedTagModeMappingAsync(db, businessId, "legal-threat", ConversationMode.Manual, isMinimumFloor: false);
        await SeedTagModeMappingAsync(db, businessId, "floor-assist", ConversationMode.Assist, isMinimumFloor: true);
        var conversation = await CreateConversationAsync(
            db, businessId, widgetId, ["legal-threat", "floor-assist"], level2VerifiedUntil: DateTimeOffset.UtcNow.AddHours(1));

        var mode = await service.ComputeEffectiveModeAsync(conversation);

        Assert.Equal(ConversationMode.Manual, mode); // never relaxed by a less-strict floor
    }

    // ── Step 4: Level 2 verification ─────────────────────────────────

    [Fact]
    public async Task ComputeEffectiveModeAsync_NoVerification_ForcesManual()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IConversationModeService>();
        var businessId = await CreateBusinessAsync(db);
        var widgetId = await CreateWidgetAsync(db, businessId);
        var conversation = await CreateConversationAsync(db, businessId, widgetId, []); // no Level2VerifiedUntil, no CustomerId

        var mode = await service.ComputeEffectiveModeAsync(conversation);

        Assert.Equal(ConversationMode.Manual, mode);
    }

    [Fact]
    public async Task ComputeEffectiveModeAsync_ExpiredLevel2VerifiedUntil_ForcesManual()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IConversationModeService>();
        var businessId = await CreateBusinessAsync(db);
        var widgetId = await CreateWidgetAsync(db, businessId);
        var conversation = await CreateConversationAsync(
            db, businessId, widgetId, [], level2VerifiedUntil: DateTimeOffset.UtcNow.AddMinutes(-1));

        var mode = await service.ComputeEffectiveModeAsync(conversation);

        Assert.Equal(ConversationMode.Manual, mode);
    }

    [Fact]
    public async Task ComputeEffectiveModeAsync_ValidLevel2VerifiedUntil_PreventsOverride()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IConversationModeService>();
        var businessId = await CreateBusinessAsync(db);
        var widgetId = await CreateWidgetAsync(db, businessId);
        var conversation = await CreateConversationAsync(
            db, businessId, widgetId, [], level2VerifiedUntil: DateTimeOffset.UtcNow.AddHours(1));

        var mode = await service.ComputeEffectiveModeAsync(conversation);

        Assert.Equal(ConversationMode.Auto, mode); // no tags => Auto base, verification holds, no override
    }

    [Fact]
    public async Task ComputeEffectiveModeAsync_ValidExternalIdentity_PreventsOverride()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IConversationModeService>();
        var businessId = await CreateBusinessAsync(db);
        var widgetId = await CreateWidgetAsync(db, businessId);
        var customerId = await CreateCustomerAsync(db, businessId);
        await SeedExternalIdentityAsync(db, businessId, customerId, revokedAt: null, expiresAt: DateTimeOffset.UtcNow.AddDays(1));
        var conversation = await CreateConversationAsync(db, businessId, widgetId, [], customerId: customerId);

        var mode = await service.ComputeEffectiveModeAsync(conversation);

        Assert.Equal(ConversationMode.Auto, mode);
    }

    [Fact]
    public async Task ComputeEffectiveModeAsync_ExpiredExternalIdentity_DoesNotCount_ForcesManual()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IConversationModeService>();
        var businessId = await CreateBusinessAsync(db);
        var widgetId = await CreateWidgetAsync(db, businessId);
        var customerId = await CreateCustomerAsync(db, businessId);
        // Expired — RevokedAt IS NULL alone must not be treated as valid.
        await SeedExternalIdentityAsync(db, businessId, customerId, revokedAt: null, expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));
        var conversation = await CreateConversationAsync(db, businessId, widgetId, [], customerId: customerId);

        var mode = await service.ComputeEffectiveModeAsync(conversation);

        Assert.Equal(ConversationMode.Manual, mode);
    }

    [Fact]
    public async Task ComputeEffectiveModeAsync_RevokedExternalIdentity_DoesNotCount_ForcesManual()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IConversationModeService>();
        var businessId = await CreateBusinessAsync(db);
        var widgetId = await CreateWidgetAsync(db, businessId);
        var customerId = await CreateCustomerAsync(db, businessId);
        await SeedExternalIdentityAsync(db, businessId, customerId, revokedAt: DateTimeOffset.UtcNow.AddMinutes(-1), expiresAt: null);
        var conversation = await CreateConversationAsync(db, businessId, widgetId, [], customerId: customerId);

        var mode = await service.ComputeEffectiveModeAsync(conversation);

        Assert.Equal(ConversationMode.Manual, mode);
    }

    [Fact]
    public async Task ComputeEffectiveModeAsync_Level2Override_NeverRelaxesAStricterModeFromEarlierSteps()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IConversationModeService>();
        var businessId = await CreateBusinessAsync(db);
        var widgetId = await CreateWidgetAsync(db, businessId);
        await SeedTagModeMappingAsync(db, businessId, "legal-threat", ConversationMode.Manual, isMinimumFloor: false);
        var conversation = await CreateConversationAsync(
            db, businessId, widgetId, ["legal-threat"], level2VerifiedUntil: DateTimeOffset.UtcNow.AddHours(1));

        var mode = await service.ComputeEffectiveModeAsync(conversation);

        Assert.Equal(ConversationMode.Manual, mode); // verified, but step 1 already established Manual — stays Manual, not relaxed
    }

    // ── Step 2: deferred refund-limit branch ─────────────────────────

    /// <summary>
    /// Proves step 2 is genuinely a no-op, not silently "working" — a
    /// scenario that the documented rule says SHOULD force Manual (a
    /// refund request over Business.AutoRefundLimit) still returns Auto,
    /// because no mechanism exists anywhere in this codebase to signal
    /// "this is a refund request for amount X" to this method. See the
    /// TODO(Step 20) comment in ConversationModeService.ComputeEffectiveModeAsync.
    /// Deliberately does NOT introduce a fake refund-request DTO/parameter/
    /// caller to exercise this — there is nothing to call.
    /// </summary>
    [Fact]
    public async Task ComputeEffectiveModeAsync_RefundLimitScenario_StepTwoDoesNotRun_ModeUnaffected()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IConversationModeService>();
        var businessId = await CreateBusinessAsync(db);

        // A low AutoRefundLimit — if step 2 were implemented, a refund
        // request over this amount would force at least Manual. Nothing in
        // ComputeEffectiveModeAsync's signature can ever be told a refund
        // amount is being requested, so this has no effect on the result.
        var business = await db.Businesses.IgnoreQueryFilters().SingleAsync(b => b.Id == businessId);
        business.AutoRefundLimit = 10m;
        business.AutoRefundLimitCurrency = "EUR";
        await db.SaveChangesAsync();

        var widgetId = await CreateWidgetAsync(db, businessId);
        var conversation = await CreateConversationAsync(
            db, businessId, widgetId, [], level2VerifiedUntil: DateTimeOffset.UtcNow.AddHours(1));

        var mode = await service.ComputeEffectiveModeAsync(conversation);

        Assert.Equal(ConversationMode.Auto, mode); // steps 1/3/4 alone yield Auto; step 2 never ran to force Manual
    }

    private class FixedTenantAccessor(Guid? businessId) : ICurrentTenantAccessor
    {
        public Guid? BusinessId => businessId;
    }
}
