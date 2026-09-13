using System.Text.Json;
using Anthropic.Models.Messages;
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
using Xunit;

namespace backend.Tests;

/// <summary>
/// Real Postgres, same fixture shape as ConversationModeServiceTests —
/// ConversationClassificationService.ClassifyAsync writes through
/// IConversationModeService.ApplyTagsAndRatchetAsync, a real lock/
/// transaction, not mockable without losing exactly what these tests need
/// to prove. IMessageService is SequencedFakeMessageService, so the real
/// ToolChoiceTool request construction, tool-name matching, Input parsing,
/// and Usage/token reading all run for real — only the network call is
/// faked (same philosophy as ToolDispatchServiceTests/ClaudeReplyServiceTests).
/// </summary>
public class ConversationClassificationServiceTests : IAsyncLifetime
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

            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Conversations\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Widgets\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"TagModeMappings\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Businesses\" WHERE \"Id\" = ANY({ids})");
        }

        await _provider.DisposeAsync();
    }

    private async Task<Guid> CreateBusinessAsync(AppDbContext db)
    {
        var business = new Business
        {
            Id = Guid.NewGuid(),
            Name = $"Classification Test {Guid.NewGuid():N}",
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
        AppDbContext db, Guid businessId, Guid widgetId, List<string> tags, string? primaryTag,
        ConversationMode effectiveMode)
    {
        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            WidgetId = widgetId,
            Channel = "Website",
            Status = ConversationStatus.Open,
            HandledBy = HandledBy.Ai,
            Tags = tags,
            PrimaryTag = primaryTag,
            EffectiveMode = effectiveMode, // seeded directly — test fixture, not going through the ratchet API
            Level2VerifiedUntil = DateTimeOffset.UtcNow.AddHours(1),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync();
        return conversation;
    }

    private static async Task SeedTagModeMappingAsync(
        AppDbContext db, Guid businessId, string tag, ConversationMode mode)
    {
        db.TagModeMappings.Add(new TagModeMapping
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            Tag = tag,
            Mode = mode,
            IsMinimumFloor = false,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static Usage FakeUsage(long inputTokens, long outputTokens) => new()
    {
        CacheCreation = null,
        CacheCreationInputTokens = null,
        CacheReadInputTokens = null,
        InferenceGeo = null,
        InputTokens = inputTokens,
        OutputTokens = outputTokens,
        OutputTokensDetails = null,
        ServerToolUse = null,
        ServiceTier = null,
    };

    private static Anthropic.Models.Messages.Message ToolUseResponse(object input, long inputTokens = 111, long outputTokens = 22) => new()
    {
        ID = "msg_classify",
        Container = null,
        Content = [new ToolUseBlock
        {
            ID = "toolu_classify",
            Name = "classifyConversation",
            Input = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(input))!,
            Caller = null,
        }],
        Model = "claude-sonnet-5",
        StopDetails = null,
        StopReason = "tool_use",
        StopSequence = null,
        Usage = FakeUsage(inputTokens, outputTokens),
    };

    private static readonly List<MessageParam> EmptyHistory = [];

    // ── §B: cross-tenant allowed-tag union — repository-backed ───────

    [Fact]
    public async Task BuildAllowedTagsAsync_CoreTagsAlwaysIncluded_BusinessCustomTagIncluded_OtherBusinessCustomTagExcluded()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var modeService = scope.ServiceProvider.GetRequiredService<IConversationModeService>();
        var logger = new CapturingLogger<ConversationClassificationService>();
        var fakeMessages = new SequencedFakeMessageService();
        var classification = new ConversationClassificationService(db, fakeMessages, modeService, logger);

        var businessAId = await CreateBusinessAsync(db);
        var businessBId = await CreateBusinessAsync(db);
        await SeedTagModeMappingAsync(db, businessAId, "vip-customer", ConversationMode.Assist);
        await SeedTagModeMappingAsync(db, businessBId, "wholesale-account", ConversationMode.Assist);

        var allowedForA = await classification.BuildAllowedTagsAsync(businessAId, CancellationToken.None);

        foreach (var coreTag in NivoraCoreTags.All)
        {
            Assert.Contains(coreTag, allowedForA); // core vocabulary always present
        }
        Assert.Contains("vip-customer", allowedForA); // business A's own custom tag
        Assert.DoesNotContain("wholesale-account", allowedForA); // business B's custom tag — must never leak into A's set
    }

    /// <summary>
    /// Same probe, seeding a differently-cased/whitespace-padded custom tag
    /// on business B specifically to prove exclusion survives normalization
    /// too — not just an exact-string absence that would trivially pass.
    /// </summary>
    [Fact]
    public async Task BuildAllowedTagsAsync_OtherBusinessCustomTag_ExcludedEvenAfterNormalization()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var modeService = scope.ServiceProvider.GetRequiredService<IConversationModeService>();
        var classification = new ConversationClassificationService(
            db, new SequencedFakeMessageService(), modeService, new CapturingLogger<ConversationClassificationService>());

        var businessAId = await CreateBusinessAsync(db);
        var businessBId = await CreateBusinessAsync(db);
        await SeedTagModeMappingAsync(db, businessBId, "  Wholesale-Account  ", ConversationMode.Assist);

        var allowedForA = await classification.BuildAllowedTagsAsync(businessAId, CancellationToken.None);

        Assert.DoesNotContain("wholesale-account", allowedForA);
    }

    // ── §B / §A: all_tags_invalid — asserted against pre-existing,
    // non-empty state, not empty defaults ─────────────────────────────

    [Fact]
    public async Task ClassifyAsync_AllReturnedTagsInvalid_LeavesPreExistingStateUnchanged_LogsDistinctOutcome()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var modeService = scope.ServiceProvider.GetRequiredService<IConversationModeService>();
        var logger = new CapturingLogger<ConversationClassificationService>();
        var fakeMessages = new SequencedFakeMessageService();
        var classification = new ConversationClassificationService(db, fakeMessages, modeService, logger);

        var businessId = await CreateBusinessAsync(db);
        var widgetId = await CreateWidgetAsync(db, businessId);
        await SeedTagModeMappingAsync(db, businessId, "refund", ConversationMode.Manual);
        // Pre-existing, non-empty state — deliberately not the empty
        // defaults every field would already have on a fresh row, so
        // "stayed the same" is actually provable, not indistinguishable
        // from "never touched."
        var conversation = await CreateConversationAsync(
            db, businessId, widgetId, tags: ["refund"], primaryTag: "refund", effectiveMode: ConversationMode.Manual);

        // Both named values are real strings but neither is in the allowed
        // set for this business — a structurally valid response, every
        // value hallucinated against the live enum.
        fakeMessages.Enqueue(ToolUseResponse(new { primaryTag = "not-a-real-tag", secondaryTags = new[] { "also-not-real" } }));

        await classification.ClassifyAsync(businessId, conversation.Id, EmptyHistory, CancellationToken.None);

        var persisted = await db.Conversations.IgnoreQueryFilters().SingleAsync(c => c.Id == conversation.Id);
        Assert.Equal(["refund"], persisted.Tags);
        Assert.Equal("refund", persisted.PrimaryTag);
        Assert.Equal(ConversationMode.Manual, persisted.EffectiveMode);

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("AllTagsInvalid"));
        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("UnparseableResult"));
    }

    // ── §B addendum: the deliberate {null, []} success case ──────────

    [Fact]
    public async Task ClassifyAsync_DeliberateNoTagsApply_ReplacesPriorTagsWithEmpty_DoesNotRatchetModeBackward_LogsSuccess()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var modeService = scope.ServiceProvider.GetRequiredService<IConversationModeService>();
        var logger = new CapturingLogger<ConversationClassificationService>();
        var fakeMessages = new SequencedFakeMessageService();
        var classification = new ConversationClassificationService(db, fakeMessages, modeService, logger);

        var businessId = await CreateBusinessAsync(db);
        var widgetId = await CreateWidgetAsync(db, businessId);
        var conversation = await CreateConversationAsync(
            db, businessId, widgetId, tags: ["shipping"], primaryTag: "shipping", effectiveMode: ConversationMode.Assist);

        fakeMessages.Enqueue(ToolUseResponse(new { primaryTag = (string?)null, secondaryTags = Array.Empty<string>() }));

        await classification.ClassifyAsync(businessId, conversation.Id, EmptyHistory, CancellationToken.None);

        var persisted = await db.Conversations.IgnoreQueryFilters().SingleAsync(c => c.Id == conversation.Id);
        Assert.Empty(persisted.Tags); // replaced, not appended to
        Assert.Null(persisted.PrimaryTag);
        Assert.Equal(ConversationMode.Assist, persisted.EffectiveMode); // Stricter(Assist, Auto) = Assist — ratchet holds

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("Success"));
        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("AllTagsInvalid"));
    }

    // ── §B addendum, Option A: invalid primary + valid secondary ─────

    [Fact]
    public async Task ClassifyAsync_InvalidPrimaryWithValidSecondary_PersistsNullPrimaryAndValidSecondary_RatchetsMode()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var modeService = scope.ServiceProvider.GetRequiredService<IConversationModeService>();
        var logger = new CapturingLogger<ConversationClassificationService>();
        var fakeMessages = new SequencedFakeMessageService();
        var classification = new ConversationClassificationService(db, fakeMessages, modeService, logger);

        var businessId = await CreateBusinessAsync(db);
        var widgetId = await CreateWidgetAsync(db, businessId);
        await SeedTagModeMappingAsync(db, businessId, "refund", ConversationMode.Manual);
        var conversation = await CreateConversationAsync(
            db, businessId, widgetId, tags: [], primaryTag: null, effectiveMode: ConversationMode.Auto);

        fakeMessages.Enqueue(ToolUseResponse(new
        {
            primaryTag = "not-a-real-tag", // invalid — must not survive
            secondaryTags = new[] { "refund", "shipping" }, // both valid core tags
        }));

        await classification.ClassifyAsync(businessId, conversation.Id, EmptyHistory, CancellationToken.None);

        // Full resulting triple, explicitly — not just "outcome succeeded."
        var persisted = await db.Conversations.IgnoreQueryFilters().SingleAsync(c => c.Id == conversation.Id);
        Assert.Null(persisted.PrimaryTag);
        Assert.Equal(["refund", "shipping"], persisted.Tags);
        Assert.Equal(ConversationMode.Manual, persisted.EffectiveMode); // ratcheted via the "refund" TagModeMapping

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("Success"));
    }

    // ── §A: SDK Usage/token path, exercised for real, not just compiled ──

    [Fact]
    public async Task ClassifyAsync_Success_LogsRealInputAndOutputTokenCountsFromSdkUsage()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var modeService = scope.ServiceProvider.GetRequiredService<IConversationModeService>();
        var logger = new CapturingLogger<ConversationClassificationService>();
        var fakeMessages = new SequencedFakeMessageService();
        var classification = new ConversationClassificationService(db, fakeMessages, modeService, logger);

        var businessId = await CreateBusinessAsync(db);
        var widgetId = await CreateWidgetAsync(db, businessId);
        var conversation = await CreateConversationAsync(
            db, businessId, widgetId, tags: [], primaryTag: null, effectiveMode: ConversationMode.Auto);

        // Deliberately distinctive, easy-to-miss-if-wrong values — proves
        // the logged numbers came from response.Usage.InputTokens/
        // OutputTokens specifically, not a placeholder/zero/miscounted field.
        fakeMessages.Enqueue(ToolUseResponse(new { primaryTag = "general", secondaryTags = Array.Empty<string>() },
            inputTokens: 4321, outputTokens: 987));

        await classification.ClassifyAsync(businessId, conversation.Id, EmptyHistory, CancellationToken.None);

        Assert.Contains(logger.Entries, e => e.Message.Contains("4321") && e.Message.Contains("987"));
    }

    // ── ToolChoiceTool request shape, asserted against what was actually sent ──

    [Fact]
    public async Task ClassifyAsync_SendsForcedToolChoice_NamingExactlyTheClassificationTool()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var modeService = scope.ServiceProvider.GetRequiredService<IConversationModeService>();
        var fakeMessages = new SequencedFakeMessageService();
        var classification = new ConversationClassificationService(
            db, fakeMessages, modeService, new CapturingLogger<ConversationClassificationService>());

        var businessId = await CreateBusinessAsync(db);
        var widgetId = await CreateWidgetAsync(db, businessId);
        var conversation = await CreateConversationAsync(
            db, businessId, widgetId, tags: [], primaryTag: null, effectiveMode: ConversationMode.Auto);

        fakeMessages.Enqueue(ToolUseResponse(new { primaryTag = (string?)null, secondaryTags = Array.Empty<string>() }));

        await classification.ClassifyAsync(businessId, conversation.Id, EmptyHistory, CancellationToken.None);

        Assert.Equal(1, fakeMessages.CallCount);
        var request = fakeMessages.Requests.Single();
        Assert.NotNull(request.ToolChoice);
        var toolChoiceTool = Assert.IsType<ToolChoiceTool>(request.ToolChoice!.Value);
        Assert.Equal("classifyConversation", toolChoiceTool.Name);
        Assert.Single(request.Tools);
    }

    private class FixedTenantAccessor(Guid? businessId) : ICurrentTenantAccessor
    {
        public Guid? BusinessId => businessId;
    }
}
