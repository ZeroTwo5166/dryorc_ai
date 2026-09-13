using System.Text.Json;
using Anthropic.Models.Messages;
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
/// Step 22 Phase 3 — real Postgres, same fixture shape as
/// ConversationModeServiceTests/ConversationClassificationServiceTests.
/// IConversationTagCorrectionService.CorrectTagsAsync and
/// ConversationClassificationService.ClassifyAsync both write through the
/// identical IConversationModeService.ApplyTagsAndRatchetAsync lock — these
/// tests prove that at runtime (captured lock-key values, real contention),
/// not by reading the source and trusting it.
/// </summary>
public class ConversationTagCorrectionServiceTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;
    private readonly List<Guid> _businessIdsToClean = [];
    private string _connectionUri = null!;

    public Task InitializeAsync()
    {
        var configuration = new ConfigurationBuilder().AddUserSecrets<Program>().Build();
        _connectionUri = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection not found via user-secrets.");

        _provider = new ServiceCollection()
            .AddSingleton<ICurrentTenantAccessor>(new FixedTenantAccessor(null))
            .AddDbContext<AppDbContext>(o => o.UseNpgsql(PostgresConnectionString.FromUri(_connectionUri), x => x.UseVector()))
            .AddScoped<IConversationModeService, ConversationModeService>()
            .AddScoped<IConversationTagCorrectionService, ConversationTagCorrectionService>()
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

    /// <summary>
    /// Builds a standalone AppDbContext bypassing the shared fixture's
    /// single registration — needed by tests that must observe/control the
    /// real commands/transactions a specific caller issues. Zero production
    /// configuration is touched.
    /// </summary>
    private AppDbContext CreateDbContext(params object[] interceptors) =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(PostgresConnectionString.FromUri(_connectionUri), x => x.UseVector())
            .AddInterceptors(interceptors.Cast<Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor>())
            .Options,
            new FixedTenantAccessor(null));

    private async Task<Guid> CreateBusinessAsync(AppDbContext db)
    {
        var business = new Business
        {
            Id = Guid.NewGuid(),
            Name = $"Tag Correction Test {Guid.NewGuid():N}",
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
        ConversationMode effectiveMode = ConversationMode.Auto)
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
            EffectiveMode = effectiveMode,
            Level2VerifiedUntil = DateTimeOffset.UtcNow.AddHours(1),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync();
        return conversation;
    }

    private static async Task SeedTagModeMappingAsync(AppDbContext db, Guid businessId, string tag, ConversationMode mode)
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

    private static Usage FakeUsage() => new()
    {
        CacheCreation = null,
        CacheCreationInputTokens = null,
        CacheReadInputTokens = null,
        InferenceGeo = null,
        InputTokens = 10,
        OutputTokens = 5,
        OutputTokensDetails = null,
        ServerToolUse = null,
        ServiceTier = null,
    };

    private static Anthropic.Models.Messages.Message ToolUseResponse(object input) => new()
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
        Usage = FakeUsage(),
    };

    private static readonly List<MessageParam> EmptyHistory = [];

    // ── Basic replace semantics (sanity, not explicitly required but
    // covers the endpoint's own core contract) ───────────────────────

    [Fact]
    public async Task CorrectTagsAsync_ReplacesTags_ClearsOrphanedPrimaryTag_RatchetsMode()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IConversationTagCorrectionService>();
        var businessId = await CreateBusinessAsync(db);
        var widgetId = await CreateWidgetAsync(db, businessId);
        await SeedTagModeMappingAsync(db, businessId, "refund", ConversationMode.Manual);
        var conversation = await CreateConversationAsync(db, businessId, widgetId, ["shipping"]);
        conversation.PrimaryTag = "shipping";
        await db.SaveChangesAsync();

        await service.CorrectTagsAsync(businessId, conversation.Id, ["  Refund  "]); // deliberately unnormalized input

        var persisted = await db.Conversations.IgnoreQueryFilters().SingleAsync(c => c.Id == conversation.Id);
        Assert.Equal(["refund"], persisted.Tags); // normalized, replaced (shipping gone)
        Assert.Null(persisted.PrimaryTag); // orphaned — shipping no longer in Tags
        Assert.Equal(ConversationMode.Manual, persisted.EffectiveMode); // ratcheted via the real TagModeMapping
    }

    // ── No-downgrade guarantee under a real staff-initiated removal ──

    [Fact]
    public async Task CorrectTagsAsync_StaffRemovesTagThatEarnedManual_EffectiveModeDoesNotDowngrade()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var modeService = scope.ServiceProvider.GetRequiredService<IConversationModeService>();
        var tagService = scope.ServiceProvider.GetRequiredService<IConversationTagCorrectionService>();
        var businessId = await CreateBusinessAsync(db);
        var widgetId = await CreateWidgetAsync(db, businessId);
        await SeedTagModeMappingAsync(db, businessId, "refund", ConversationMode.Manual);
        var conversation = await CreateConversationAsync(db, businessId, widgetId, []);

        // "Via a prior classification that legitimately reached it" — go
        // through the real ratchet API, not a hand-set EffectiveMode.
        await modeService.ApplyTagsAndRatchetAsync(businessId, conversation.Id, (c, _) =>
        {
            c.Tags = ["refund"];
            return Task.CompletedTask;
        });
        var afterClassification = await db.Conversations.IgnoreQueryFilters().SingleAsync(c => c.Id == conversation.Id);
        Assert.Equal(ConversationMode.Manual, afterClassification.EffectiveMode); // sanity: legitimately reached Manual

        // Staff removes the "refund" tag entirely — recomputed alone, this
        // would be Auto (no tags => Auto, per ComputeBaseModeAsync).
        await tagService.CorrectTagsAsync(businessId, conversation.Id, []);

        var afterCorrection = await db.Conversations.IgnoreQueryFilters().SingleAsync(c => c.Id == conversation.Id);
        Assert.Empty(afterCorrection.Tags); // the removal itself took effect
        Assert.Equal(ConversationMode.Manual, afterCorrection.EffectiveMode); // but the ratchet held — no downgrade
    }

    // ── Lock-identity proof: real captured runtime values, not
    // source-reading trust ────────────────────────────────────────────

    /// <summary>
    /// Calls the real classification path and the real staff-correction
    /// path, each against its own AppDbContext fitted with its own
    /// LockAcquisitionInterceptor (the exact class Phase 2's cleanup
    /// introduced — reused verbatim here, not reinvented), for the SAME
    /// (businessId, conversationId). Asserts the actual numeric key value
    /// each path sent to Postgres in its "SELECT pg_advisory_xact_lock(@p)"
    /// command is identical — proof from the real SQL parameter, not from
    /// both call sites merely naming the same helper method in source.
    /// </summary>
    [Fact]
    public async Task ClassificationAndStaffCorrection_ForTheSameConversation_UseTheIdenticalRuntimeLockKey()
    {
        await using var seedDb = _provider.CreateAsyncScope().ServiceProvider.GetRequiredService<AppDbContext>();
        var businessId = await CreateBusinessAsync(seedDb);
        var widgetId = await CreateWidgetAsync(seedDb, businessId);
        var conversation = await CreateConversationAsync(seedDb, businessId, widgetId, []);

        var classificationInterceptor = new LockAcquisitionInterceptor();
        await using var classificationDb = CreateDbContext(classificationInterceptor);
        var classificationModeService = new ConversationModeService(classificationDb);
        var fakeMessages = new SequencedFakeMessageService();
        fakeMessages.Enqueue(ToolUseResponse(new { primaryTag = (string?)null, secondaryTags = Array.Empty<string>() }));
        var classificationService = new ConversationClassificationService(
            classificationDb, fakeMessages, classificationModeService, NullLoggerFor<ConversationClassificationService>());

        await classificationService.ClassifyAsync(businessId, conversation.Id, EmptyHistory, CancellationToken.None);

        var staffCorrectionInterceptor = new LockAcquisitionInterceptor();
        await using var staffDb = CreateDbContext(staffCorrectionInterceptor);
        var staffModeService = new ConversationModeService(staffDb);
        var staffCorrectionService = new ConversationTagCorrectionService(staffModeService);

        await staffCorrectionService.CorrectTagsAsync(businessId, conversation.Id, ["shipping"]);

        var classificationKey = Assert.Single(classificationInterceptor.ObservedLockKeys);
        var staffCorrectionKey = Assert.Single(staffCorrectionInterceptor.ObservedLockKeys);
        Assert.Equal(classificationKey, staffCorrectionKey);

        // Also confirmed against the known-correct value directly, so a
        // future change to AdvisoryLockKeyGenerator's own algorithm that
        // happened to keep both call sites *mutually* equal but wrong
        // wouldn't silently pass this test.
        var expectedKey = AdvisoryLockKeyGenerator.ForConversationTagState(businessId, conversation.Id);
        Assert.Equal(expectedKey, classificationKey);
    }

    // ── Concurrency: staff correction genuinely racing classification ──

    /// <summary>
    /// Two genuinely different writers — not two instances of the same
    /// method, unlike Phase 2's concurrency test — contending on the same
    /// conversation. Deterministic, no wall-clock for the positive
    /// (lock-holds) proof. Paused via UpdateCommandPauseInterceptor, which
    /// fires immediately BEFORE call 1's SaveChangesAsync-issued UPDATE to
    /// "Conversations" is sent — earlier in the pipeline than a pre-commit
    /// hook, and deliberately so: by this checkpoint call 1 has already
    /// executed a real "SELECT pg_advisory_xact_lock(...)" but has NOT yet
    /// taken Postgres's own row-level lock (that only happens once the
    /// UPDATE is sent). This removes the row-lock confound an earlier,
    /// pre-commit-pause version of this test had — see the commit message
    /// for the investigation that found it, including an empirical
    /// break-and-restore proving that earlier version's pause point let the
    /// row lock alone mask a fully-neutered advisory lock, undetected.
    ///
    /// task2.IsCompleted, checked immediately after call 2's own
    /// call2AboutToAcquireLock checkpoint, is kept as a corroborating check
    /// but is explicitly NOT claimed as an airtight negative proof on its
    /// own — see its own inline comment and the commit message for why
    /// (classification has several real DB round trips left after its own
    /// lock attempt, so "not yet completed" is unsurprising regardless of
    /// whether the lock is actually blocking anything). A genuine attempt
    /// was made to replace it with a fully deterministic, DB-state-level
    /// proof (polling pg_stat_activity.wait_event_type for call 2's own
    /// backend process) and abandoned after empirical investigation — see
    /// the commit message for the precise reason (this project's Neon
    /// connection is pooled, and the pooled-session id Npgsql reports does
    /// not correspond to any row in pg_stat_activity on the real backend).
    ///
    /// Final assertion proves genuine cross-writer composition, not just
    /// "neither threw": call 1 commits Tags=["shipping"] first (ratchet:
    /// Auto -> Assist); call 2 then genuinely observes that already-committed
    /// Assist via its own fresh read before replacing Tags with ["refund"]
    /// and computing Stricter(Assist, Manual) = Manual.
    /// </summary>
    [Fact]
    public async Task CorrectTagsAsync_ConcurrentWithClassification_SerializesOnTheSharedLock_RatchetComposesAcrossWriters()
    {
        await using var seedDb = _provider.CreateAsyncScope().ServiceProvider.GetRequiredService<AppDbContext>();
        var businessId = await CreateBusinessAsync(seedDb);
        var widgetId = await CreateWidgetAsync(seedDb, businessId);
        await SeedTagModeMappingAsync(seedDb, businessId, "shipping", ConversationMode.Assist);
        await SeedTagModeMappingAsync(seedDb, businessId, "refund", ConversationMode.Manual);
        var conversation = await CreateConversationAsync(seedDb, businessId, widgetId, []);

        var call1BeforeUpdate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCall1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var updatePause = new UpdateCommandPauseInterceptor
        {
            BeforeUpdate = async () =>
            {
                call1BeforeUpdate.TrySetResult();
                await releaseCall1.Task;
            },
        };
        var lockInterceptor1 = new LockAcquisitionInterceptor();
        await using var db1 = CreateDbContext(lockInterceptor1, updatePause);
        var staffCorrectionService = new ConversationTagCorrectionService(new ConversationModeService(db1));

        var task1 = staffCorrectionService.CorrectTagsAsync(businessId, conversation.Id, ["shipping"]);

        // Deterministic — call 1 has genuinely acquired the real advisory
        // lock and read the conversation, and is paused strictly before its
        // own UPDATE is sent (so no row lock exists for it yet).
        await call1BeforeUpdate.Task;

        var lockInterceptor2 = new LockAcquisitionInterceptor();
        var call2AboutToAcquireLock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lockInterceptor2.OnBeforeLockAcquisition = _ => call2AboutToAcquireLock.TrySetResult();
        await using var db2 = CreateDbContext(lockInterceptor2);
        var fakeMessages = new SequencedFakeMessageService();
        fakeMessages.Enqueue(ToolUseResponse(new { primaryTag = (string?)null, secondaryTags = new[] { "refund" } }));
        var classificationService = new ConversationClassificationService(
            db2, fakeMessages, new ConversationModeService(db2), NullLoggerFor<ConversationClassificationService>());

        var task2 = classificationService.ClassifyAsync(businessId, conversation.Id, EmptyHistory, CancellationToken.None);

        await call2AboutToAcquireLock.Task; // deterministic — call 2 has genuinely reached its own lock attempt

        // NOTE — this check is a known-imperfect corroborating signal, not a
        // proven, airtight guarantee: see this test's own commit message.
        // "Not yet completed" is expected here regardless of whether the
        // lock actually blocks anything, since classification has several
        // real DB round trips left after its own lock attempt. It is kept
        // because it holds unconditionally when the lock genuinely blocks,
        // and removing it would prove less than keeping it, not because it
        // is, on its own, a reliable negative proof of the advisory lock's
        // specific contribution. A pg_stat_activity-based attempt at a
        // stronger, DB-state-level proof was made and abandoned — see the
        // commit message for why it doesn't work against this specific
        // Neon (pooled) connection.
        Assert.False(task2.IsCompleted);

        releaseCall1.SetResult();
        await Task.WhenAll(task1, task2);

        // Both interceptors observed the same real key, for the same reason
        // the dedicated lock-identity test above proves it directly.
        Assert.Equal(Assert.Single(lockInterceptor1.ObservedLockKeys), Assert.Single(lockInterceptor2.ObservedLockKeys));

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var persisted = await verifyDb.Conversations.IgnoreQueryFilters().SingleAsync(c => c.Id == conversation.Id);
        Console.WriteLine($"DEBUG persisted: Tags=[{string.Join(",", persisted.Tags)}] EffectiveMode={persisted.EffectiveMode}");

        Assert.Equal(["refund"], persisted.Tags); // call 2 (classification) committed last — its own replace wins for Tags
        Assert.Equal(ConversationMode.Manual, persisted.EffectiveMode); // Stricter(Assist [call 1's committed value], Manual) — genuine cross-writer composition
    }

    private static ILogger<T> NullLoggerFor<T>() => Microsoft.Extensions.Logging.Abstractions.NullLogger<T>.Instance;

    private class FixedTenantAccessor(Guid? businessId) : ICurrentTenantAccessor
    {
        public Guid? BusinessId => businessId;
    }
}
