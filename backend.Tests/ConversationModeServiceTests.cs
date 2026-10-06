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
        await TestBusinessCleanup.DeleteThenDisposeAsync(_businessIdsToClean, _provider);
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

    // ── Step 22: Conversation.EffectiveMode defaults ─────────────────

    /// <summary>
    /// C# property-initializer default — no DB involved. A freshly
    /// constructed Conversation (the in-application path, e.g.
    /// WidgetConversationService's `new Conversation { ... }`) must default
    /// to Auto without EffectiveMode being set explicitly, matching
    /// Status/HandledBy's own established convention.
    /// </summary>
    [Fact]
    public void Conversation_ConstructedWithoutSettingEffectiveMode_DefaultsToAuto()
    {
        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            BusinessId = Guid.NewGuid(),
            WidgetId = Guid.NewGuid(),
            Channel = "Website",
        };

        Assert.Equal(ConversationMode.Auto, conversation.EffectiveMode);
    }

    /// <summary>
    /// DB-level column default (the migration's own `defaultValue: "Auto"`,
    /// materialized as a real Postgres column DEFAULT) — proven by inserting
    /// a row via raw SQL that omits the EffectiveMode column entirely, the
    /// same shape any pre-existing row backfilled by the migration itself
    /// took. Distinct from the C# initializer test above: this exercises
    /// the actual applied migration/schema, not application code.
    /// </summary>
    [Fact]
    public async Task Conversations_RowInsertedWithoutEffectiveModeColumn_DefaultsToAutoAtDbLevel()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var businessId = await CreateBusinessAsync(db);
        var widgetId = await CreateWidgetAsync(db, businessId);
        var conversationId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        // Deliberately omits "EffectiveMode" from the column list — relies
        // entirely on the migration's DB-level DEFAULT, not EF/application
        // code, mirroring exactly how every pre-existing row was backfilled
        // when the migration ran.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Conversations"
                ("Id", "BusinessId", "WidgetId", "Status", "HandledBy", "Channel", "Tags", "CreatedAt", "UpdatedAt")
            VALUES
                ({conversationId}, {businessId}, {widgetId}, 'Open', 'Ai', 'Website', ARRAY[]::text[], {now}, {now})
            """);

        var effectiveMode = await db.Conversations
            .IgnoreQueryFilters()
            .Where(c => c.Id == conversationId)
            .Select(c => c.EffectiveMode)
            .SingleAsync();

        Assert.Equal(ConversationMode.Auto, effectiveMode);
    }

    // ── Step 22: Stricter — Max-like, symmetric ratchet primitive ────

    [Theory]
    [InlineData(ConversationMode.Auto, ConversationMode.Auto, ConversationMode.Auto)]
    [InlineData(ConversationMode.Auto, ConversationMode.Assist, ConversationMode.Assist)]
    [InlineData(ConversationMode.Auto, ConversationMode.Manual, ConversationMode.Manual)]
    [InlineData(ConversationMode.Assist, ConversationMode.Manual, ConversationMode.Manual)]
    [InlineData(ConversationMode.Manual, ConversationMode.Manual, ConversationMode.Manual)]
    public void Stricter_ReturnsTheStricterOfTheTwoValues_RegardlessOfArgumentOrder(
        ConversationMode a, ConversationMode b, ConversationMode expected)
    {
        Assert.Equal(expected, ConversationModeService.Stricter(a, b));
        Assert.Equal(expected, ConversationModeService.Stricter(b, a)); // symmetric — Max-like, not "first wins"/"second wins"
    }

    // ── Step 22: ComputeAndRatchetEffectiveModeAsync ─────────────────

    /// <summary>
    /// The exact worked example from the step-22 design doc §C: an
    /// existing Auto row, a customer message classified with a tag whose
    /// TagModeMapping specifies Manual, ratchets EffectiveMode from Auto to
    /// Manual and persists it. Conversation is created the same way every
    /// pre-migration row would have looked (EffectiveMode left at its Auto
    /// default), not pre-seeded with a mode.
    /// </summary>
    [Fact]
    public async Task ComputeAndRatchetEffectiveModeAsync_ExistingAutoRow_RatchetsToManualOnMatchingTag()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IConversationModeService>();
        var businessId = await CreateBusinessAsync(db);
        var widgetId = await CreateWidgetAsync(db, businessId);
        await SeedTagModeMappingAsync(db, businessId, "refund", ConversationMode.Manual, isMinimumFloor: false);
        var conversation = await CreateConversationAsync(
            db, businessId, widgetId, ["refund"], level2VerifiedUntil: DateTimeOffset.UtcNow.AddHours(1));
        Assert.Equal(ConversationMode.Auto, conversation.EffectiveMode); // starting point, per the worked example

        var result = await service.ComputeAndRatchetEffectiveModeAsync(conversation);
        await db.SaveChangesAsync();

        Assert.Equal(ConversationMode.Manual, result);
        Assert.Equal(ConversationMode.Manual, conversation.EffectiveMode);
        var persisted = await db.Conversations.IgnoreQueryFilters()
            .Where(c => c.Id == conversation.Id).Select(c => c.EffectiveMode).SingleAsync();
        Assert.Equal(ConversationMode.Manual, persisted); // actually persisted, not just held on the tracked entity
    }

    /// <summary>
    /// Second half of the worked example: a later reclassification that
    /// would, on its own, compute something less strict (shipping-only, no
    /// matching mapping => Auto) must not move the already-ratcheted
    /// EffectiveMode backward. This is the entire point of §C's persisted
    /// ratchet — proven here against the real persisted value, not just the
    /// in-memory Stricter() primitive tested above in isolation.
    /// </summary>
    [Fact]
    public async Task ComputeAndRatchetEffectiveModeAsync_SubsequentLessStrictComputation_DoesNotMoveBackward()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IConversationModeService>();
        var businessId = await CreateBusinessAsync(db);
        var widgetId = await CreateWidgetAsync(db, businessId);
        await SeedTagModeMappingAsync(db, businessId, "refund", ConversationMode.Manual, isMinimumFloor: false);
        var conversation = await CreateConversationAsync(
            db, businessId, widgetId, ["refund"], level2VerifiedUntil: DateTimeOffset.UtcNow.AddHours(1));
        await service.ComputeAndRatchetEffectiveModeAsync(conversation);
        await db.SaveChangesAsync();
        Assert.Equal(ConversationMode.Manual, conversation.EffectiveMode); // ratcheted first, per the test above

        // Reclassifies to shipping-only — no TagModeMapping for it, so this
        // computation alone would yield Auto.
        conversation.Tags = ["shipping"];
        var result = await service.ComputeAndRatchetEffectiveModeAsync(conversation);
        await db.SaveChangesAsync();

        Assert.Equal(ConversationMode.Manual, result); // Stricter(Manual, Auto) = Manual — does not move backward
        var persisted = await db.Conversations.IgnoreQueryFilters()
            .Where(c => c.Id == conversation.Id).Select(c => c.EffectiveMode).SingleAsync();
        Assert.Equal(ConversationMode.Manual, persisted);
    }

    /// <summary>
    /// ComputeAndRatchetEffectiveModeAsync must not itself call
    /// SaveChangesAsync — mutating the tracked entity and leaving the
    /// commit to the caller's own transaction is the documented contract
    /// (IConversationModeService.cs's own doc comment). Proven by NOT
    /// calling SaveChangesAsync after the method returns, reloading via a
    /// fresh, untracked query, and confirming the DB still shows the
    /// pre-call value.
    /// </summary>
    [Fact]
    public async Task ComputeAndRatchetEffectiveModeAsync_DoesNotItselfCallSaveChanges()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IConversationModeService>();
        var businessId = await CreateBusinessAsync(db);
        var widgetId = await CreateWidgetAsync(db, businessId);
        await SeedTagModeMappingAsync(db, businessId, "refund", ConversationMode.Manual, isMinimumFloor: false);
        var conversation = await CreateConversationAsync(
            db, businessId, widgetId, ["refund"], level2VerifiedUntil: DateTimeOffset.UtcNow.AddHours(1));

        await service.ComputeAndRatchetEffectiveModeAsync(conversation); // caller deliberately does NOT SaveChangesAsync

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var persisted = await verifyDb.Conversations.IgnoreQueryFilters()
            .Where(c => c.Id == conversation.Id).Select(c => c.EffectiveMode).SingleAsync();
        Assert.Equal(ConversationMode.Auto, persisted); // still Auto in the DB — the method never flushed on its own
    }

    // ── Step 22: ApplyTagsAndRatchetAsync — genuine concurrency ──────

    /// <summary>
    /// Builds a standalone AppDbContext (bypassing the shared fixture's
    /// single registration) with a LockAcquisitionInterceptor attached —
    /// needed only by tests that must observe the real
    /// pg_advisory_xact_lock command as it's actually sent. Zero production
    /// configuration is touched; this is a test-only DbContextOptions.
    /// </summary>
    private static AppDbContext CreateDbContextWithLockInterceptor(LockAcquisitionInterceptor interceptor)
    {
        var configuration = new ConfigurationBuilder().AddUserSecrets<Program>().Build();
        var connectionUri = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection not found via user-secrets.");

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(PostgresConnectionString.FromUri(connectionUri), x => x.UseVector())
            .AddInterceptors(interceptor)
            .Options;

        return new AppDbContext(options, new FixedTenantAccessor(null));
    }

    /// <summary>
    /// Two callers, each on its own DbContext/DB connection (a single
    /// connection cannot self-block on pg_advisory_xact_lock, so two
    /// separate connections are required for real contention). Genuinely
    /// overlapping, and — unlike an earlier version of this test — proven
    /// via application-state signals throughout, never wall-clock time:
    ///
    /// 1. Call 1's mutator signals call1HoldsLock only once it is actually
    ///    running inside the acquired lock, then blocks on releaseCall1.
    /// 2. The test awaits call1HoldsLock (a TaskCompletionSource, not a
    ///    timer) before starting call 2 at all.
    /// 3. Call 2 is given its own LockAcquisitionInterceptor, whose
    ///    OnBeforeLockAcquisition fires synchronously, driven by EF Core
    ///    actually sending the pg_advisory_xact_lock command — not a
    ///    timer — the instant call 2 genuinely attempts the lock.
    /// 4. The test awaits that firing (call2AboutToAcquireLock), then
    ///    immediately asserts task2.IsCompleted is still false. This is
    ///    deterministic, not probabilistic: call 1 provably still holds
    ///    the lock at this point (releaseCall1 has not been set), and
    ///    Postgres guarantees pg_advisory_xact_lock cannot return to call 2
    ///    while call 1's transaction holds it — so task2 physically cannot
    ///    have completed, regardless of scheduling or network speed. This
    ///    replaces an earlier version of this test that used
    ///    Task.Delay(500) before the same check — confirmed non-load-bearing
    ///    (see this test's own history: it passed even with the lock
    ///    removed, because it was measuring elapsed time, not contention).
    /// 5. Only then is call 1 released; both calls are awaited.
    ///
    /// The two calls compute two genuinely different modes (Assist vs.
    /// Manual) so the final assertion — the stricter of the two survives
    /// regardless of which committed first — is a real test of the ratchet
    /// under concurrency, not just "neither threw."
    /// </summary>
    [Fact]
    public async Task ApplyTagsAndRatchetAsync_TwoGenuinelyOverlappingCalls_SerializeAndPersistTheStricterResult()
    {
        var interceptor1 = new LockAcquisitionInterceptor();
        await using var db1 = CreateDbContextWithLockInterceptor(interceptor1);
        var service1 = new ConversationModeService(db1);

        var businessId = await CreateBusinessAsync(db1);
        var widgetId = await CreateWidgetAsync(db1, businessId);
        await SeedTagModeMappingAsync(db1, businessId, "shipping", ConversationMode.Assist, isMinimumFloor: false);
        await SeedTagModeMappingAsync(db1, businessId, "refund", ConversationMode.Manual, isMinimumFloor: false);
        var conversation = await CreateConversationAsync(
            db1, businessId, widgetId, [], level2VerifiedUntil: DateTimeOffset.UtcNow.AddHours(1));

        var interceptor2 = new LockAcquisitionInterceptor();
        await using var db2 = CreateDbContextWithLockInterceptor(interceptor2);
        var service2 = new ConversationModeService(db2);

        var call1HoldsLock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCall1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var call2AboutToAcquireLock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        interceptor2.OnBeforeLockAcquisition = _ => call2AboutToAcquireLock.TrySetResult();

        var task1 = service1.ApplyTagsAndRatchetAsync(businessId, conversation.Id, async (c, ct) =>
        {
            call1HoldsLock.SetResult(); // fires only once genuinely inside the acquired lock
            await releaseCall1.Task; // holds the transaction (and the lock) open until released below
            c.Tags = ["shipping"]; // Assist — the weaker of the two outcomes
        });

        await call1HoldsLock.Task; // deterministic — wait until call 1 genuinely holds the DB-level lock

        var task2 = service2.ApplyTagsAndRatchetAsync(businessId, conversation.Id, (c, ct) =>
        {
            c.Tags = ["refund"]; // Manual — the stricter of the two outcomes
            return Task.CompletedTask;
        });

        await call2AboutToAcquireLock.Task; // deterministic — call 2 has genuinely reached the lock attempt

        // Deterministic by construction, not by luck: call 1 still holds
        // the lock (releaseCall1 not yet set), so Postgres itself
        // guarantees task2 cannot have completed yet — no sleep, no
        // elapsed-time assumption.
        Assert.False(task2.IsCompleted);

        releaseCall1.SetResult();
        await Task.WhenAll(task1, task2);

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var persisted = await verifyDb.Conversations.IgnoreQueryFilters()
            .Where(c => c.Id == conversation.Id).Select(c => c.EffectiveMode).SingleAsync();

        // Manual (Stricter(Assist, Manual)) regardless of which of the two
        // genuinely concurrent calls' write happened to commit last — this
        // is what a plain unlocked read-modify-write could NOT guarantee.
        Assert.Equal(ConversationMode.Manual, persisted);

        _businessIdsToClean.Add(businessId); // db1/db2 are standalone, not the shared fixture — register for cleanup
    }

    private class FixedTenantAccessor(Guid? businessId) : ICurrentTenantAccessor
    {
        public Guid? BusinessId => businessId;
    }
}
