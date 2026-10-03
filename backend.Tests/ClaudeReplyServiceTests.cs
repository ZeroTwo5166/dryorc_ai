using System.Net;
using Anthropic.Exceptions;
using Anthropic.Services;
using backend.Constants;
using backend.Data;
using backend.Enums;
using backend.Interfaces;
using backend.Models;
using backend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Pgvector;
using Xunit;

namespace backend.Tests;

/// <summary>
/// Service-level tests for ClaudeReplyService, run against the real Neon
/// dev DB via a bare ServiceProvider — same pattern as
/// CustomerDataLifecycleTests. IMessageService is constructed directly per
/// test (FakeMessageService, a real AnthropicClient, or
/// MissingAnthropicApiKeyMessageService) rather than through a shared DI
/// registration, so each scenario gets exactly the implementation it needs
/// with no risk of one test's setup leaking into another's.
///
/// Only ONE test in this file makes a real, billed Anthropic call — gated
/// behind [LiveClaudeFact]. Every other test here uses FakeMessageService's
/// realistic Message/ContentBlock/TextBlock/Usage response shape, so
/// ClaudeReplyService's actual content-block extraction logic is exercised
/// for real, without any network dependency.
/// </summary>
[Collection("KnowledgeChunks collection")]
public class ClaudeReplyServiceTests : IAsyncLifetime
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

        // Deliberately no IMessageService/IClaudeReplyService registered
        // here — each test constructs ClaudeReplyService directly with
        // exactly the IMessageService implementation its scenario needs.
        _provider = new ServiceCollection()
            .AddSingleton<ICurrentTenantAccessor>(new FixedTenantAccessor(null))
            .AddDbContext<AppDbContext>(o => o.UseNpgsql(
                PostgresConnectionString.FromUri(connectionUri), x => x.UseVector()))
            .AddScoped<IAuditLogService, AuditLogService>()
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

            // Messages first — cascades away any MessageKnowledgeSource rows
            // referencing them. KnowledgeChunks/KnowledgeDocuments before
            // Businesses — that FK is Restrict, not Cascade, so deleting the
            // Business first would fail while either still exists.
            //
            // [Step 23M] Customers added: pre-existing gap, latent until now
            // — no test in this file created a Customer row before
            // SeedConversationWithResolvedCustomerAsync (23K), and that
            // helper's only prior caller was always [LiveClaudeFact]-skipped,
            // so this cleanup path was never actually exercised against a
            // seeded Customer until this pass's non-live tests. Conversations
            // (which reference Customers) must be deleted first, same
            // Restrict-FK reasoning as KnowledgeChunks/Documents above.
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Messages\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"KnowledgeChunks\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"KnowledgeDocuments\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Conversations\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Customers\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Widgets\" WHERE \"BusinessId\" = ANY({ids})");
            // Rule (c) audit rows: AuditLog.BusinessId is FK Restrict, so
            // AuditLogs go before Businesses.
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"AuditLogs\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Businesses\" WHERE \"Id\" = ANY({ids})");
        }

        await _provider.DisposeAsync();
    }

    // A1 Slice 4a (design §6.1): messages go behind a real AnthropicInvoker
    // with a non-persisting capture recorder, so nothing reaches
    // AiProviderInvocations and no cleanup is needed for it.
    private ClaudeReplyService NewService(
        AppDbContext db,
        IMessageService messages,
        IKnowledgeRetrievalService? retrieval = null,
        IToolDispatchService? toolDispatch = null,
        IConversationClassificationService? classification = null,
        IUnauthorizedActionAuditWriter? auditWriter = null,
        ConcurrentCapturingAiInvocationRecorder? recorder = null) =>
        new(
            db,
            new AnthropicInvoker(messages, recorder ?? new ConcurrentCapturingAiInvocationRecorder(), NullLogger<AnthropicInvoker>.Instance),
            retrieval ?? new FakeKnowledgeRetrievalService(),
            toolDispatch ?? new ToolDispatchService([], messages, NullLogger<ToolDispatchService>.Instance),
            classification ?? new NoOpConversationClassificationService(),
            auditWriter ?? new UnauthorizedActionAuditWriter(
                _provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<UnauthorizedActionAuditWriter>.Instance),
            NullLogger<ClaudeReplyService>.Instance);

    private async Task<(Business Business, Conversation Conversation)> SeedConversationWithOneCustomerMessageAsync(AppDbContext db)
    {
        var business = new Business
        {
            Id = Guid.NewGuid(),
            Name = $"Claude Reply Test {Guid.NewGuid():N}",
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
        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id,
            WidgetId = widget.Id,
            VisitorSessionId = Guid.NewGuid().ToString("N"),
            Channel = "Website",
            Status = ConversationStatus.Open,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Businesses.Add(business);
        db.Widgets.Add(widget);
        db.Conversations.Add(conversation);
        db.Messages.Add(new Message
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id,
            ConversationId = conversation.Id,
            Role = MessageRole.Customer,
            Content = "Do you ship to Denmark?",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        _businessIdsToClean.Add(business.Id);

        return (business, conversation);
    }

    /// <summary>
    /// Seeds a real KnowledgeDocument + KnowledgeChunk row so tests can hand
    /// FakeKnowledgeRetrievalService a RetrievedKnowledgeChunk with genuinely
    /// valid ids — MessageKnowledgeSource.KnowledgeChunkId/KnowledgeDocumentId
    /// are real (non-null-when-set) foreign keys, so a fabricated Guid.NewGuid()
    /// with no backing row fails with a real FK violation on insert.
    /// Embedding content is a placeholder; these tests are about
    /// ClaudeReplyService's prompt-building/persistence logic, not
    /// retrieval's own vector-search correctness (KnowledgeRetrievalServiceTests'
    /// job).
    /// </summary>
    private static async Task<(Guid DocumentId, Guid ChunkId)> SeedKnowledgeChunkAsync(
        AppDbContext db, Guid businessId, string title, string content)
    {
        var document = new KnowledgeDocument
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            Type = KnowledgeDocumentType.Faq,
            Title = title,
            Content = content,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        var chunk = new KnowledgeChunk
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            KnowledgeDocumentId = document.Id,
            ChunkIndex = 0,
            Content = content,
            Embedding = new Vector(new float[1024]),
            EmbeddingModel = "voyage-3",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.KnowledgeDocuments.Add(document);
        db.KnowledgeChunks.Add(chunk);
        await db.SaveChangesAsync();

        return (document.Id, chunk.Id);
    }

    // â”€â”€ Success path — fake, but realistic response shape â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task GenerateReplyAsync_FakeRealisticResponse_ExtractsTextViaRealParsing_PersistsAiMessage()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (business, conversation) = await SeedConversationWithOneCustomerMessageAsync(db);

        var fake = new FakeMessageService { ReplyText = "Yes, we ship to Denmark within 3-5 business days." };
        var service = NewService(db, fake, new FakeKnowledgeRetrievalService(), new ToolDispatchService([], fake, NullLogger<ToolDispatchService>.Instance), new NoOpConversationClassificationService());

        var reply = await service.GenerateReplyAsync(business.Id, conversation.Id);

        // Proves this went through ClaudeReplyService's real
        // response.Content.Select(b => b.Value).OfType<TextBlock>()
        // extraction — the fake returns a Message with a real TextBlock
        // inside a real ContentBlock union, not a pre-extracted string.
        Assert.Equal(fake.ReplyText, reply?.Content);
        Assert.Equal(1, fake.CallCount);

        var aiMessage = await db.Messages.IgnoreQueryFilters()
            .SingleAsync(m => m.ConversationId == conversation.Id && m.Role == MessageRole.Ai);
        Assert.Equal(fake.ReplyText, aiMessage.Content);

        var updatedConversation = await db.Conversations.IgnoreQueryFilters().SingleAsync(c => c.Id == conversation.Id);
        Assert.Equal(aiMessage.CreatedAt, updatedConversation.LastMessageAt);
    }

    // ── A1 Slice 4a (design §6.2 "A1"): the no-tool reply goes through IAnthropicInvoker ──

    /// <summary>
    /// T-A1a (v11 test 2). Mutation: revert A1 to the raw IMessageService
    /// (0 records); A1's NextSequence() replaced by 1; ConversationId omitted.
    /// </summary>
    [Fact]
    public async Task GenerateReplyAsync_NoToolReply_RecordsOneCustomerReplyRowAtSeq0()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (business, conversation) = await SeedConversationWithOneCustomerMessageAsync(db);

        var fake = new FakeMessageService();
        var capture = new ConcurrentCapturingAiInvocationRecorder();
        var service = NewService(db, fake, recorder: capture);

        var reply = await service.GenerateReplyAsync(business.Id, conversation.Id);

        Assert.NotNull(reply);
        var record = Assert.Single(capture.ForBusiness(business.Id));
        Assert.Equal(AiOperations.CustomerReply, record.Operation);
        Assert.Equal(0, record.Sequence);
        Assert.Equal(conversation.Id, record.ConversationId);
        Assert.Equal(business.Id, record.BusinessId);
    }

    /// <summary>T-A1b. Mutation: hoist the turn's correlation to a field.</summary>
    [Fact]
    public async Task GenerateReplyAsync_TwoTurnsOnOneService_RecordDistinctCorrelationIds()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (business, conversation) = await SeedConversationWithOneCustomerMessageAsync(db);

        var capture = new ConcurrentCapturingAiInvocationRecorder();
        var service = NewService(db, new FakeMessageService(), recorder: capture);

        await service.GenerateReplyAsync(business.Id, conversation.Id);
        await service.GenerateReplyAsync(business.Id, conversation.Id);

        var records = capture.ForBusiness(business.Id);
        Assert.Equal(2, records.Count);
        Assert.NotEqual(records[0].CorrelationId, records[1].CorrelationId);
    }

    /// <summary>
    /// T-A1e. Mutation: change the model literal. A value-equal literal is
    /// undetectable here (design §6.2, as T-A3d); the recorded model is the
    /// sent model, so only the sent model is asserted (4b errata §3b).
    /// </summary>
    [Fact]
    public async Task GenerateReplyAsync_NoToolReply_SendsAnthropicModelsDefault()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (business, conversation) = await SeedConversationWithOneCustomerMessageAsync(db);

        var fake = new FakeMessageService();
        var service = NewService(db, fake);

        await service.GenerateReplyAsync(business.Id, conversation.Id);

        Assert.NotEmpty(fake.Requests);
        Assert.All(fake.Requests, r => Assert.Equal(AnthropicModels.Default, r.Model.Raw()));
    }

    // â”€â”€ Step 12: RAG retrieval wiring â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task GenerateReplyAsync_RetrievedChunks_QueriesWithLatestCustomerMessage_AndAppearInSystemPrompt()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (business, conversation) = await SeedConversationWithOneCustomerMessageAsync(db);
        var (documentId, chunkId) = await SeedKnowledgeChunkAsync(db, business.Id, "Shipping Policy", "MARKER_CHUNK_ONE: we ship worldwide.");

        var retrieval = new FakeKnowledgeRetrievalService
        {
            ChunksToReturn =
            [
                new RetrievedKnowledgeChunk(chunkId, documentId, "Shipping Policy", "MARKER_CHUNK_ONE: we ship worldwide."),
            ],
        };
        var fake = new FakeMessageService { ReplyText = "Yes, we do." };
        var service = NewService(db, fake, retrieval, new ToolDispatchService([], fake, NullLogger<ToolDispatchService>.Instance), new NoOpConversationClassificationService());

        await service.GenerateReplyAsync(business.Id, conversation.Id);

        Assert.Equal(1, retrieval.CallCount);
        Assert.Equal("Do you ship to Denmark?", retrieval.Requests[0].QueryText); // the seeded customer message
        Assert.Equal(business.Id, retrieval.Requests[0].BusinessId);

        var sentSystemPrompt = (string)fake.Requests[0].System!.Value!;
        Assert.Contains("MARKER_CHUNK_ONE: we ship worldwide.", sentSystemPrompt);
        Assert.Contains("[1]", sentSystemPrompt);
        Assert.Contains("Knowledge base context", sentSystemPrompt);
    }

    // T-V3c (A1 Slice 4a). Mutant: pass Guid.Empty as the conversation id to
    // RetrieveAsync.
    [Fact]
    public async Task GenerateReplyAsync_PassesCheckedConversationIdToRetrieval()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (business, conversation) = await SeedConversationWithOneCustomerMessageAsync(db);

        var retrieval = new FakeKnowledgeRetrievalService();
        var fake = new FakeMessageService();
        var service = NewService(db, fake, retrieval, new ToolDispatchService([], fake, NullLogger<ToolDispatchService>.Instance), new NoOpConversationClassificationService());

        await service.GenerateReplyAsync(business.Id, conversation.Id);

        Assert.Equal(1, retrieval.CallCount);
        Assert.Equal(conversation.Id, retrieval.Requests[0].ConversationId);
    }

    [Fact]
    public async Task GenerateReplyAsync_MultipleRetrievedChunks_NumberedInOrderInSystemPrompt()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (business, conversation) = await SeedConversationWithOneCustomerMessageAsync(db);
        var (documentIdA, chunkIdA) = await SeedKnowledgeChunkAsync(db, business.Id, "Doc A", "MARKER_FIRST content.");
        var (documentIdB, chunkIdB) = await SeedKnowledgeChunkAsync(db, business.Id, "Doc B", "MARKER_SECOND content.");

        var retrieval = new FakeKnowledgeRetrievalService
        {
            ChunksToReturn =
            [
                new RetrievedKnowledgeChunk(chunkIdA, documentIdA, "Doc A", "MARKER_FIRST content."),
                new RetrievedKnowledgeChunk(chunkIdB, documentIdB, "Doc B", "MARKER_SECOND content."),
            ],
        };
        var fake = new FakeMessageService();
        var service = NewService(db, fake, retrieval, new ToolDispatchService([], fake, NullLogger<ToolDispatchService>.Instance), new NoOpConversationClassificationService());

        await service.GenerateReplyAsync(business.Id, conversation.Id);

        var systemPrompt = (string)fake.Requests[0].System!.Value!;
        var firstIndex = systemPrompt.IndexOf("[1] MARKER_FIRST content.", StringComparison.Ordinal);
        var secondIndex = systemPrompt.IndexOf("[2] MARKER_SECOND content.", StringComparison.Ordinal);
        Assert.True(firstIndex >= 0, "expected [1]-labeled first chunk in the system prompt");
        Assert.True(secondIndex > firstIndex, "expected [2]-labeled second chunk to follow the first");
    }

    [Fact]
    public async Task GenerateReplyAsync_RetrievedChunks_PersistsOneMessageKnowledgeSourcePerChunk_WithCorrectSnapshots()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (business, conversation) = await SeedConversationWithOneCustomerMessageAsync(db);

        var (documentIdA, chunkIdA) = await SeedKnowledgeChunkAsync(db, business.Id, "seed doc A", "seed content A");
        var (documentIdB, chunkIdB) = await SeedKnowledgeChunkAsync(db, business.Id, "seed doc B", "seed content B");
        var retrieval = new FakeKnowledgeRetrievalService
        {
            ChunksToReturn =
            [
                new RetrievedKnowledgeChunk(chunkIdA, documentIdA, "Refund Policy", "30-day refunds."),
                new RetrievedKnowledgeChunk(chunkIdB, documentIdB, "Shipping Policy", "Ships within 2 days."),
            ],
        };
        var fake = new FakeMessageService { ReplyText = "Here's what I found." };
        var service = NewService(db, fake, retrieval, new ToolDispatchService([], fake, NullLogger<ToolDispatchService>.Instance), new NoOpConversationClassificationService());

        var reply = await service.GenerateReplyAsync(business.Id, conversation.Id);
        Assert.NotNull(reply);

        var aiMessage = await db.Messages.IgnoreQueryFilters()
            .SingleAsync(m => m.ConversationId == conversation.Id && m.Role == MessageRole.Ai);

        var sources = await db.MessageKnowledgeSources.IgnoreQueryFilters()
            .Where(s => s.MessageId == aiMessage.Id)
            .ToListAsync();

        Assert.Equal(2, sources.Count); // exactly one row per injected chunk
        var sourceA = sources.Single(s => s.KnowledgeChunkId == chunkIdA);
        Assert.Equal(documentIdA, sourceA.KnowledgeDocumentId);
        Assert.Equal("Refund Policy", sourceA.DocumentTitleSnapshot);
        Assert.Equal("30-day refunds.", sourceA.ChunkContentSnapshot);
        Assert.Equal(business.Id, sourceA.BusinessId);

        var sourceB = sources.Single(s => s.KnowledgeChunkId == chunkIdB);
        Assert.Equal(documentIdB, sourceB.KnowledgeDocumentId);
        Assert.Equal("Shipping Policy", sourceB.DocumentTitleSnapshot);
        Assert.Equal("Ships within 2 days.", sourceB.ChunkContentSnapshot);
    }

    [Fact]
    public async Task GenerateReplyAsync_NoRetrievedChunks_NoMessageKnowledgeSourceRows()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (business, conversation) = await SeedConversationWithOneCustomerMessageAsync(db);

        var fake = new FakeMessageService { ReplyText = "Plain reply." };
        var service = NewService(db, fake, new FakeKnowledgeRetrievalService(), new ToolDispatchService([], fake, NullLogger<ToolDispatchService>.Instance), new NoOpConversationClassificationService()); // default: returns nothing

        var reply = await service.GenerateReplyAsync(business.Id, conversation.Id);
        Assert.NotNull(reply);

        var aiMessage = await db.Messages.IgnoreQueryFilters()
            .SingleAsync(m => m.ConversationId == conversation.Id && m.Role == MessageRole.Ai);
        var sources = await db.MessageKnowledgeSources.IgnoreQueryFilters()
            .Where(s => s.MessageId == aiMessage.Id).ToListAsync();
        Assert.Empty(sources);
        Assert.DoesNotContain("Knowledge base context", (string)fake.Requests[0].System!.Value!);
    }

    [Fact]
    public async Task GenerateReplyAsync_RetrievalServiceThrows_FallsBackToConversationOnlyBehavior()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (business, conversation) = await SeedConversationWithOneCustomerMessageAsync(db);

        // Simulates an unexpected failure inside IKnowledgeRetrievalService —
        // KnowledgeRetrievalService's own real implementation never throws
        // for an expected Voyage failure (see KnowledgeRetrievalServiceTests),
        // but ClaudeReplyService's own defense-in-depth catch must hold
        // regardless of the reason retrieval failed.
        var retrieval = new FakeKnowledgeRetrievalService { ExceptionToThrow = new InvalidOperationException("simulated unexpected retrieval failure") };
        var fake = new FakeMessageService { ReplyText = "Still works." };
        var service = NewService(db, fake, retrieval, new ToolDispatchService([], fake, NullLogger<ToolDispatchService>.Instance), new NoOpConversationClassificationService());

        var reply = await service.GenerateReplyAsync(business.Id, conversation.Id);

        Assert.Equal("Still works.", reply?.Content); // the conversation continued normally, not broken by retrieval throwing
        var aiMessage = await db.Messages.IgnoreQueryFilters()
            .SingleAsync(m => m.ConversationId == conversation.Id && m.Role == MessageRole.Ai);
        var sources = await db.MessageKnowledgeSources.IgnoreQueryFilters()
            .Where(s => s.MessageId == aiMessage.Id).ToListAsync();
        Assert.Empty(sources);
        Assert.DoesNotContain("Knowledge base context", (string)fake.Requests[0].System!.Value!);
    }

    // â”€â”€ Anthropic-typed failures — each returns null, nothing persisted â”€

    [Fact]
    public async Task GenerateReplyAsync_AnthropicRateLimitException_ReturnsNull_PersistsNothing()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (business, conversation) = await SeedConversationWithOneCustomerMessageAsync(db);

        var fake = new FakeMessageService
        {
            ExceptionToThrow = new AnthropicRateLimitException { StatusCode = HttpStatusCode.TooManyRequests, ResponseBody = "{}" },
        };
        var service = NewService(db, fake, new FakeKnowledgeRetrievalService(), new ToolDispatchService([], fake, NullLogger<ToolDispatchService>.Instance), new NoOpConversationClassificationService());

        var reply = await service.GenerateReplyAsync(business.Id, conversation.Id);

        Assert.Null(reply);
        await AssertNoAiMessageOrLastMessageAtChangeAsync(db, conversation.Id);
    }

    [Fact]
    public async Task GenerateReplyAsync_Anthropic5xxException_ReturnsNull_PersistsNothing()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (business, conversation) = await SeedConversationWithOneCustomerMessageAsync(db);

        var fake = new FakeMessageService
        {
            ExceptionToThrow = new Anthropic5xxException { StatusCode = HttpStatusCode.InternalServerError, ResponseBody = "{}" },
        };
        var service = NewService(db, fake, new FakeKnowledgeRetrievalService(), new ToolDispatchService([], fake, NullLogger<ToolDispatchService>.Instance), new NoOpConversationClassificationService());

        var reply = await service.GenerateReplyAsync(business.Id, conversation.Id);

        Assert.Null(reply);
        await AssertNoAiMessageOrLastMessageAtChangeAsync(db, conversation.Id);
    }

    [Fact]
    public async Task GenerateReplyAsync_AnthropicApiException_ReturnsNull_PersistsNothing()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (business, conversation) = await SeedConversationWithOneCustomerMessageAsync(db);

        var fake = new FakeMessageService
        {
            ExceptionToThrow = new AnthropicApiException("Simulated API error") { StatusCode = HttpStatusCode.BadRequest, ResponseBody = "{}" },
        };
        var service = NewService(db, fake, new FakeKnowledgeRetrievalService(), new ToolDispatchService([], fake, NullLogger<ToolDispatchService>.Instance), new NoOpConversationClassificationService());

        var reply = await service.GenerateReplyAsync(business.Id, conversation.Id);

        Assert.Null(reply);
        await AssertNoAiMessageOrLastMessageAtChangeAsync(db, conversation.Id);
    }

    // â”€â”€ Genuine network/timeout failures — same "return null" outcome,
    //    but a structurally different exception family (not the SDK's own
    //    typed hierarchy) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    // T-A1c (A1 Slice 4a): also exactly one Failed CustomerReply record.
    // Mutation (owner ruling 2026-10-03): revert A1 to the raw
    // IMessageService (0 records).
    [Fact]
    public async Task GenerateReplyAsync_HttpRequestException_ReturnsNull_PersistsNothing()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (business, conversation) = await SeedConversationWithOneCustomerMessageAsync(db);

        var fake = new FakeMessageService { ExceptionToThrow = new HttpRequestException("Simulated network failure") };
        var capture = new ConcurrentCapturingAiInvocationRecorder();
        var service = NewService(db, fake, new FakeKnowledgeRetrievalService(), new ToolDispatchService([], fake, NullLogger<ToolDispatchService>.Instance), new NoOpConversationClassificationService(), recorder: capture);

        var reply = await service.GenerateReplyAsync(business.Id, conversation.Id);

        Assert.Null(reply);
        await AssertNoAiMessageOrLastMessageAtChangeAsync(db, conversation.Id);

        var record = Assert.Single(capture.ForBusiness(business.Id));
        Assert.Equal(AiOutcomes.Failed, record.Outcome);
        Assert.Equal(AiOperations.CustomerReply, record.Operation);
    }

    // â”€â”€ Missing configuration — must NOT be swallowed into a null reply â”€

    // T-A1d (A1 Slice 4a): also no record (the invoker rethrows
    // AnthropicNotConfiguredException without one). Mutation: catch it at
    // A1 and return null (ThrowsAsync red).
    [Fact]
    public async Task GenerateReplyAsync_MissingApiKeyConfiguration_ThrowsDistinctException_IsNotSwallowed()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (business, conversation) = await SeedConversationWithOneCustomerMessageAsync(db);

        // No real secrets touched — this constructs the exact stub
        // Program.cs registers when Anthropic:ApiKey is absent, directly,
        // independent of whatever's actually configured in this environment.
        var capture = new ConcurrentCapturingAiInvocationRecorder();
        var service = NewService(db, new MissingAnthropicApiKeyMessageService(), new FakeKnowledgeRetrievalService(), new ToolDispatchService([], new MissingAnthropicApiKeyMessageService(), NullLogger<ToolDispatchService>.Instance), new NoOpConversationClassificationService(), recorder: capture);

        await Assert.ThrowsAsync<AnthropicNotConfiguredException>(
            () => service.GenerateReplyAsync(business.Id, conversation.Id));

        // Confirms the exception genuinely propagated rather than being
        // caught-and-converted somewhere — no AI message, no LastMessageAt
        // change, same as the other failure tests, but reached via a throw
        // the caller must handle, not a quiet null.
        await AssertNoAiMessageOrLastMessageAtChangeAsync(db, conversation.Id);

        Assert.Empty(capture.ForBusiness(business.Id));
    }

    // â”€â”€ Step 9: HandledBy gate â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task GenerateReplyAsync_HandledByHuman_SkipsClaudeEntirely_ReturnsNull()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (business, conversation) = await SeedConversationWithOneCustomerMessageAsync(db);

        var conversationEntity = await db.Conversations.IgnoreQueryFilters().SingleAsync(c => c.Id == conversation.Id);
        conversationEntity.HandledBy = HandledBy.Human;
        await db.SaveChangesAsync();

        var fake = new FakeMessageService { ReplyText = "Should never be sent." };
        var service = NewService(db, fake, new FakeKnowledgeRetrievalService(), new ToolDispatchService([], fake, NullLogger<ToolDispatchService>.Instance), new NoOpConversationClassificationService());

        var reply = await service.GenerateReplyAsync(business.Id, conversation.Id);

        Assert.Null(reply);
        Assert.Equal(0, fake.CallCount); // Claude is never called at all, not called-then-discarded
        await AssertNoAiMessageOrLastMessageAtChangeAsync(db, conversation.Id);
    }

    [Fact]
    public async Task GenerateReplyAsync_HandledByAi_RetainsExistingBehavior_ClaudeIsCalled()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (business, conversation) = await SeedConversationWithOneCustomerMessageAsync(db);

        // HandledBy defaults to Ai via the model's own field initializer —
        // asserted explicitly here so this test documents the contrast with
        // the Human-gated test above, not just relying on an implicit default.
        var conversationEntity = await db.Conversations.IgnoreQueryFilters().SingleAsync(c => c.Id == conversation.Id);
        Assert.Equal(HandledBy.Ai, conversationEntity.HandledBy);

        var fake = new FakeMessageService { ReplyText = "Normal AI reply." };
        var service = NewService(db, fake, new FakeKnowledgeRetrievalService(), new ToolDispatchService([], fake, NullLogger<ToolDispatchService>.Instance), new NoOpConversationClassificationService());

        var reply = await service.GenerateReplyAsync(business.Id, conversation.Id);

        Assert.Equal("Normal AI reply.", reply?.Content);
        Assert.Equal(1, fake.CallCount);
        var aiMessage = await db.Messages.IgnoreQueryFilters()
            .SingleAsync(m => m.ConversationId == conversation.Id && m.Role == MessageRole.Ai);
        Assert.Equal("Normal AI reply.", aiMessage.Content);
    }

    private static async Task AssertNoAiMessageOrLastMessageAtChangeAsync(AppDbContext db, Guid conversationId)
    {
        var messageCount = await db.Messages.IgnoreQueryFilters().CountAsync(m => m.ConversationId == conversationId);
        Assert.Equal(1, messageCount); // still just the original seeded customer message

        var conversation = await db.Conversations.IgnoreQueryFilters().SingleAsync(c => c.Id == conversationId);
        Assert.Null(conversation.LastMessageAt);
    }

    // â”€â”€ Real Anthropic API call — explicitly opt-in only â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [LiveClaudeFact]
    public async Task GenerateReplyAsync_RealApiCall_ReturnsReplyAndPersistsAiMessage()
    {
        var configuration = new ConfigurationBuilder().AddUserSecrets<Program>().Build();
        var apiKey = configuration["Anthropic:ApiKey"]!; // LiveClaudeFact already guarantees this is present

        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (business, conversation) = await SeedConversationWithOneCustomerMessageAsync(db);

        var client = new Anthropic.AnthropicClient { ApiKey = apiKey };
        var service = NewService(db, client.Messages, new FakeKnowledgeRetrievalService(), new ToolDispatchService([], client.Messages, NullLogger<ToolDispatchService>.Instance), new NoOpConversationClassificationService());

        var reply = await service.GenerateReplyAsync(business.Id, conversation.Id);

        Assert.False(string.IsNullOrWhiteSpace(reply?.Content));

        var aiMessage = await db.Messages.IgnoreQueryFilters()
            .SingleAsync(m => m.ConversationId == conversation.Id && m.Role == MessageRole.Ai);
        Assert.Equal(reply!.Content, aiMessage.Content);
        Assert.Equal(reply.Id, aiMessage.Id);

        var updatedConversation = await db.Conversations.IgnoreQueryFilters().SingleAsync(c => c.Id == conversation.Id);
        Assert.Equal(aiMessage.CreatedAt, updatedConversation.LastMessageAt);
    }

    // ── Step 23K: end-to-end tool-dispatch wiring proof ──────────────
    //
    // Everything above this point either never involves ToolDispatchService
    // at all, or (the one LiveClaudeFact above) seeds a Conversation with no
    // resolved CustomerId, which — per ClaudeReplyService's own new branch —
    // takes the original, unchanged, tool-free path. This test is the only
    // one in this file (and, per Step 23I's audit, the only one anywhere in
    // this codebase) that actually exercises: Claude is offered a real tool
    // → Claude decides to call it → ToolDispatchService routes the call →
    // GetCustomerTool executes against the real seeded DB row → the result
    // round-trips back into Claude's final reply. A real, billed Anthropic
    // call — gated behind [LiveClaudeFact], same as the test above.

    private async Task<(Business Business, Conversation Conversation)> SeedConversationWithResolvedCustomerAsync(
        AppDbContext db, string customerName, string customerEmail, string customerMessage,
        bool cancelOrderToolEnabled = false, bool refundToolEnabled = false, bool shopifyReadToolsEnabled = false)
    {
        var business = new Business
        {
            Id = Guid.NewGuid(),
            Name = $"Claude Reply Tool Test {Guid.NewGuid():N}",
            TimeZone = "Europe/Copenhagen",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            CancelOrderToolEnabled = cancelOrderToolEnabled,
            RefundToolEnabled = refundToolEnabled,
            ShopifyReadToolsEnabled = shopifyReadToolsEnabled,
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
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id,
            Name = customerName,
            Email = customerEmail,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id,
            WidgetId = widget.Id,
            CustomerId = customer.Id,
            VisitorSessionId = Guid.NewGuid().ToString("N"),
            Channel = "Website",
            Status = ConversationStatus.Open,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Businesses.Add(business);
        db.Widgets.Add(widget);
        db.Customers.Add(customer);
        db.Conversations.Add(conversation);
        db.Messages.Add(new Message
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id,
            ConversationId = conversation.Id,
            Role = MessageRole.Customer,
            Content = customerMessage,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        _businessIdsToClean.Add(business.Id);

        return (business, conversation);
    }

    // ── Step 23M: non-live coverage for the CustomerId-resolved branch ──
    //
    // Step 23L found the branch 23K added (ClaudeReplyService routing
    // through ToolDispatchService whenever Conversation.CustomerId is
    // resolved) had zero coverage from any test that runs in a normal,
    // non-live suite — only the one LiveClaudeFact test below touches it,
    // and that test is skipped by default. These two tests close that gap
    // without any live API call, using the same recording-fake-double
    // pattern FakeMessageService/FakeKnowledgeRetrievalService already
    // establish in this file, applied to IToolDispatchService.

    [Fact]
    public async Task GenerateReplyAsync_ResolvedCustomer_ConstructsCorrectToolDispatchContext_AndRoutesThroughToolDispatch()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (business, conversation) = await SeedConversationWithResolvedCustomerAsync(
            db, "Test Customer", "test.customer@marker-test.invalid", "What's my name and email on file?");

        var fakeDispatch = new FakeToolDispatchService { OutcomeToReturn = ToolDispatchOutcome.Completed("Fake dispatch reply.") };
        var messageService = new FakeMessageService(); // must never be called directly in this branch — asserted below

        var service = NewService(db, messageService, new FakeKnowledgeRetrievalService(), fakeDispatch, new NoOpConversationClassificationService());
        var reply = await service.GenerateReplyAsync(business.Id, conversation.Id);

        // Proves the CustomerId-resolved branch actually routes through
        // ToolDispatchService, not the old inline messageService.Create path.
        Assert.Equal(1, fakeDispatch.CallCount);
        Assert.Equal(0, messageService.CallCount);

        // Proves ToolDispatchContext is constructed with the correct,
        // server-derived values — not left default, not swapped/misordered.
        Assert.NotNull(fakeDispatch.LastContext);
        Assert.Equal(business.Id, fakeDispatch.LastContext!.BusinessId);
        Assert.Equal(conversation.CustomerId, fakeDispatch.LastContext.CustomerId);
        Assert.Equal(conversation.Id, fakeDispatch.LastContext.ConversationId);

        Assert.Equal("Fake dispatch reply.", reply?.Content);
        var aiMessage = await db.Messages.IgnoreQueryFilters()
            .SingleAsync(m => m.ConversationId == conversation.Id && m.Role == MessageRole.Ai);
        Assert.Equal("Fake dispatch reply.", aiMessage.Content);
    }

    [Fact]
    public async Task GenerateReplyAsync_ResolvedCustomer_AttachesRegisteredToolToRealDispatchServicesClaudeCall()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (business, conversation) = await SeedConversationWithResolvedCustomerAsync(
            db, "Test Customer", "test.customer@marker-test.invalid", "What's my name and email on file?");

        var fake = new FakeMessageService { ReplyText = "Fake reply, no tool call in this canned response." };
        // The real ToolDispatchService, exactly as Program.cs registers it in
        // production, but with GetCustomerTool as its only registered tool
        // and FakeMessageService standing in for the real Anthropic client —
        // no live network call, but the real Tools-array-construction logic
        // (ToolDispatchService.RunAsync) genuinely runs.
        var realDispatch = new ToolDispatchService([new GetCustomerTool(db)], fake, NullLogger<ToolDispatchService>.Instance);

        var service = NewService(db, fake, new FakeKnowledgeRetrievalService(), realDispatch, new NoOpConversationClassificationService());
        await service.GenerateReplyAsync(business.Id, conversation.Id);

        Assert.Equal(1, fake.CallCount);
        var toolsSent = fake.Requests[0].Tools;
        Assert.NotNull(toolsSent);
        Assert.Single(toolsSent!);
        var sentTool = (Anthropic.Models.Messages.Tool)toolsSent![0].Value!;
        Assert.Equal("getCustomer", sentTool.Name);
    }

    // ── Step 48: per-business cancelOrder kill switch ──
    //
    // Business.CancelOrderToolEnabled is checked here, in
    // GenerateReplyAsync, before ToolDispatchService.RunAsync is ever
    // called — not inside ToolDispatchService itself, which deliberately
    // stays free of a database dependency. These two tests assert the
    // disabledToolNames set FakeToolDispatchService actually received,
    // proving the gate is computed correctly in both directions.

    [Fact]
    public async Task GenerateReplyAsync_CancelOrderToolDisabled_PassesDisabledToolNameToDispatch()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (business, conversation) = await SeedConversationWithResolvedCustomerAsync(
            db, "Test Customer", "test.customer@marker-test.invalid", "Can you cancel my order?",
            cancelOrderToolEnabled: false);

        var fakeDispatch = new FakeToolDispatchService();
        var service = NewService(
            db, new FakeMessageService(), new FakeKnowledgeRetrievalService(), fakeDispatch, new NoOpConversationClassificationService());

        await service.GenerateReplyAsync(business.Id, conversation.Id);

        Assert.NotNull(fakeDispatch.LastDisabledToolNames);
        Assert.Contains(OrderCancelProposalTool.ToolName, fakeDispatch.LastDisabledToolNames!);
    }

    [Fact]
    public async Task GenerateReplyAsync_CancelOrderToolEnabled_DoesNotDisableIt()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (business, conversation) = await SeedConversationWithResolvedCustomerAsync(
            db, "Test Customer", "test.customer@marker-test.invalid", "Can you cancel my order?",
            cancelOrderToolEnabled: true);

        var fakeDispatch = new FakeToolDispatchService();
        var service = NewService(
            db, new FakeMessageService(), new FakeKnowledgeRetrievalService(), fakeDispatch, new NoOpConversationClassificationService());

        await service.GenerateReplyAsync(business.Id, conversation.Id);

        Assert.NotNull(fakeDispatch.LastDisabledToolNames);
        Assert.DoesNotContain(OrderCancelProposalTool.ToolName, fakeDispatch.LastDisabledToolNames!);
    }

    // ── Step refundOrder Phase 3: Owner Decision 3, Layer 1 (exposure) ──
    //
    // Business.RefundToolEnabled is checked here, in GenerateReplyAsync,
    // identically in shape to CancelOrderToolEnabled immediately above —
    // an independent, second per-tool flag, not a reuse of
    // CancelOrderToolEnabled's own check. These two tests assert the
    // disabledToolNames set FakeToolDispatchService actually received from
    // the real GenerateReplyAsync code path, proving the gate is computed
    // correctly in both directions — the same proof shape the existing
    // CancelOrderToolEnabled pair already establishes for that tool.

    [Fact]
    public async Task GenerateReplyAsync_RefundToolDisabled_PassesDisabledToolNameToDispatch()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (business, conversation) = await SeedConversationWithResolvedCustomerAsync(
            db, "Test Customer", "test.customer@marker-test.invalid", "Can you refund my order?",
            refundToolEnabled: false);

        var fakeDispatch = new FakeToolDispatchService();
        var service = NewService(
            db, new FakeMessageService(), new FakeKnowledgeRetrievalService(), fakeDispatch, new NoOpConversationClassificationService());

        await service.GenerateReplyAsync(business.Id, conversation.Id);

        Assert.NotNull(fakeDispatch.LastDisabledToolNames);
        Assert.Contains(RefundProposalTool.ToolName, fakeDispatch.LastDisabledToolNames!);
    }

    [Fact]
    public async Task GenerateReplyAsync_RefundToolEnabled_DoesNotDisableIt()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (business, conversation) = await SeedConversationWithResolvedCustomerAsync(
            db, "Test Customer", "test.customer@marker-test.invalid", "Can you refund my order?",
            refundToolEnabled: true);

        var fakeDispatch = new FakeToolDispatchService();
        var service = NewService(
            db, new FakeMessageService(), new FakeKnowledgeRetrievalService(), fakeDispatch, new NoOpConversationClassificationService());

        await service.GenerateReplyAsync(business.Id, conversation.Id);

        Assert.NotNull(fakeDispatch.LastDisabledToolNames);
        Assert.DoesNotContain(RefundProposalTool.ToolName, fakeDispatch.LastDisabledToolNames!);
    }

    // ── Step getOrder: per-business Shopify read-tool kill switch ──
    //
    // Business.ShopifyReadToolsEnabled is checked here, in GenerateReplyAsync,
    // identically in shape to CancelOrderToolEnabled/RefundToolEnabled above
    // — a third, independent per-business flag, not a reuse of either. These
    // two tests assert the disabledToolNames set FakeToolDispatchService
    // actually received from the real GenerateReplyAsync code path, proving
    // the gate is computed correctly in both directions — the same proof
    // shape the existing CancelOrderToolEnabled/RefundToolEnabled pairs
    // already establish for those tools.

    [Fact]
    public async Task GenerateReplyAsync_ShopifyReadToolsDisabled_PassesDisabledToolNameToDispatch()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (business, conversation) = await SeedConversationWithResolvedCustomerAsync(
            db, "Test Customer", "test.customer@marker-test.invalid", "Can you look up my order?",
            shopifyReadToolsEnabled: false);

        var fakeDispatch = new FakeToolDispatchService();
        var service = NewService(
            db, new FakeMessageService(), new FakeKnowledgeRetrievalService(), fakeDispatch, new NoOpConversationClassificationService());

        await service.GenerateReplyAsync(business.Id, conversation.Id);

        Assert.NotNull(fakeDispatch.LastDisabledToolNames);
        Assert.Contains(GetOrderTool.ToolName, fakeDispatch.LastDisabledToolNames!);
    }

    [Fact]
    public async Task GenerateReplyAsync_ShopifyReadToolsEnabled_DoesNotDisableIt()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (business, conversation) = await SeedConversationWithResolvedCustomerAsync(
            db, "Test Customer", "test.customer@marker-test.invalid", "Can you look up my order?",
            shopifyReadToolsEnabled: true);

        var fakeDispatch = new FakeToolDispatchService();
        var service = NewService(
            db, new FakeMessageService(), new FakeKnowledgeRetrievalService(), fakeDispatch, new NoOpConversationClassificationService());

        await service.GenerateReplyAsync(business.Id, conversation.Id);

        Assert.NotNull(fakeDispatch.LastDisabledToolNames);
        Assert.DoesNotContain(GetOrderTool.ToolName, fakeDispatch.LastDisabledToolNames!);
    }

    // ── Step Group B/C: getProduct/searchProducts/getInventory share
    // getOrder's existing Shopify read-tool kill switch ──
    //
    // Business.ShopifyReadToolsEnabled gates all four read-only tools
    // together — not a new, fifth flag. These two tests assert the
    // disabledToolNames set FakeToolDispatchService actually received from
    // the real GenerateReplyAsync code path, proving the gate is computed
    // correctly in both directions for GetProductTool/SearchProductsTool/
    // GetInventoryTool, the same proof shape the existing GetOrderTool pair
    // already establishes for that tool.

    [Fact]
    public async Task GenerateReplyAsync_ShopifyReadToolsDisabled_PassesGetProductAndSearchProductsToolNamesToDispatch()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (business, conversation) = await SeedConversationWithResolvedCustomerAsync(
            db, "Test Customer", "test.customer@marker-test.invalid", "Do you sell blue hoodies?",
            shopifyReadToolsEnabled: false);

        var fakeDispatch = new FakeToolDispatchService();
        var service = NewService(
            db, new FakeMessageService(), new FakeKnowledgeRetrievalService(), fakeDispatch, new NoOpConversationClassificationService());

        await service.GenerateReplyAsync(business.Id, conversation.Id);

        Assert.NotNull(fakeDispatch.LastDisabledToolNames);
        Assert.Contains(GetProductTool.ToolName, fakeDispatch.LastDisabledToolNames!);
        Assert.Contains(SearchProductsTool.ToolName, fakeDispatch.LastDisabledToolNames!);
        Assert.Contains(GetInventoryTool.ToolName, fakeDispatch.LastDisabledToolNames!);
    }

    [Fact]
    public async Task GenerateReplyAsync_ShopifyReadToolsEnabled_DoesNotDisableGetProductOrSearchProducts()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (business, conversation) = await SeedConversationWithResolvedCustomerAsync(
            db, "Test Customer", "test.customer@marker-test.invalid", "Do you sell blue hoodies?",
            shopifyReadToolsEnabled: true);

        var fakeDispatch = new FakeToolDispatchService();
        var service = NewService(
            db, new FakeMessageService(), new FakeKnowledgeRetrievalService(), fakeDispatch, new NoOpConversationClassificationService());

        await service.GenerateReplyAsync(business.Id, conversation.Id);

        Assert.NotNull(fakeDispatch.LastDisabledToolNames);
        Assert.DoesNotContain(GetProductTool.ToolName, fakeDispatch.LastDisabledToolNames!);
        Assert.DoesNotContain(SearchProductsTool.ToolName, fakeDispatch.LastDisabledToolNames!);
        Assert.DoesNotContain(GetInventoryTool.ToolName, fakeDispatch.LastDisabledToolNames!);
    }

    [LiveClaudeFact]
    public async Task GenerateReplyAsync_ResolvedCustomer_CallsGetCustomerTool_RealResultReachesFinalReply()
    {
        var configuration = new ConfigurationBuilder().AddUserSecrets<Program>().Build();
        var apiKey = configuration["Anthropic:ApiKey"]!; // LiveClaudeFact already guarantees this is present

        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Distinctive, unguessable marker values — if these exact strings
        // appear in the persisted final reply, that reply could only have
        // gotten them from GetCustomerTool's own DB read, never from the
        // model's own knowledge or from anything else in the prompt.
        const string markerName = "Zqxvo Threlkeld-89217";
        const string markerEmail = "zqxvo.89217@marker-test.invalid";
        var (business, conversation) = await SeedConversationWithResolvedCustomerAsync(
            db, markerName, markerEmail,
            "What name and email do you have on file for me? Please state them exactly as recorded, nothing else.");

        var client = new Anthropic.AnthropicClient { ApiKey = apiKey };
        var toolDispatch = new ToolDispatchService([new GetCustomerTool(db)], client.Messages, NullLogger<ToolDispatchService>.Instance);
        var service = NewService(db, client.Messages, new FakeKnowledgeRetrievalService(), toolDispatch, new NoOpConversationClassificationService());

        var reply = await service.GenerateReplyAsync(business.Id, conversation.Id);

        Assert.False(string.IsNullOrWhiteSpace(reply?.Content));
        Assert.Contains(markerName, reply!.Content);
        Assert.Contains(markerEmail, reply.Content);

        var aiMessage = await db.Messages.IgnoreQueryFilters()
            .SingleAsync(m => m.ConversationId == conversation.Id && m.Role == MessageRole.Ai);
        Assert.Equal(reply.Content, aiMessage.Content);
    }

    // ── Step 22 §A: exactly one classification attempt per reply,
    // regardless of which branch produced replyText ────────────────

    /// <summary>
    /// No-customer branch (the plain, tool-free messageService.Create call,
    /// ClaudeReplyService.cs's else branch). Proves the shared tail after
    /// db.SaveChangesAsync — not the branch itself — is what invokes
    /// classification, by observing it fires exactly once for a
    /// conversation that never enters the tool-dispatch branch at all.
    /// </summary>
    [Fact]
    public async Task GenerateReplyAsync_NoCustomerBranch_CallsClassificationExactlyOnce()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (business, conversation) = await SeedConversationWithOneCustomerMessageAsync(db);
        Assert.Null(conversation.CustomerId); // confirms this is genuinely the no-customer branch

        var fake = new FakeMessageService { ReplyText = "A plain-branch reply." };
        var classification = new NoOpConversationClassificationService();
        var service = NewService(
            db, fake, new FakeKnowledgeRetrievalService(),
            new ToolDispatchService([], fake, NullLogger<ToolDispatchService>.Instance),
            classification);

        await service.GenerateReplyAsync(business.Id, conversation.Id);

        Assert.Equal(1, classification.CallCount);
    }

    /// <summary>
    /// Tool-dispatch branch (conversation.CustomerId resolved,
    /// ToolDispatchService.RunAsync called). Same assertion as the
    /// no-customer-branch test above — exactly once, not zero, not twice —
    /// proving both branches converge on the identical single call site
    /// rather than each carrying its own classification call.
    /// </summary>
    [Fact]
    public async Task GenerateReplyAsync_ToolDispatchBranch_CallsClassificationExactlyOnce()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (business, conversation) = await SeedConversationWithResolvedCustomerAsync(
            db, "Test Customer", "test.customer@marker-test.invalid", "What's my order status?");
        Assert.NotNull(conversation.CustomerId); // confirms this is genuinely the tool-dispatch branch

        var fake = new FakeMessageService { ReplyText = "A tool-dispatch-branch reply." };
        var classification = new NoOpConversationClassificationService();
        var service = NewService(
            db, fake, new FakeKnowledgeRetrievalService(),
            new ToolDispatchService([], fake, NullLogger<ToolDispatchService>.Instance),
            classification);

        await service.GenerateReplyAsync(business.Id, conversation.Id);

        Assert.Equal(1, classification.CallCount);
    }

    // ── Step 27 Slice A1, Slice 4a: rule (b)/(c) conversation-business
    // check (v11 §5; design billing-usage-a1-slice4a-design.md §2, §6.2) ──

    /// <summary>
    /// Seeds the authenticated business B1, and a second business B2 owning
    /// conversation X with a resolved B2 customer (so a check placed after
    /// retrieval would take the tool-dispatch branch). When
    /// <paramref name="seedB1CustomerMessage"/> is set, also seeds a
    /// Customer-role Message with BusinessId = B1 and ConversationId = X, so
    /// GenerateReplyAsync(B1, X) finds history and a query text.
    /// </summary>
    private async Task<(Business B1, Conversation X)> SeedForeignConversationAsync(
        AppDbContext db, bool seedB1CustomerMessage, HandledBy handledBy = HandledBy.Ai)
    {
        var b1 = new Business
        {
            Id = Guid.NewGuid(),
            Name = $"Rule B Authenticated {Guid.NewGuid():N}",
            TimeZone = "Europe/Copenhagen",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        var b2 = new Business
        {
            Id = Guid.NewGuid(),
            Name = $"Rule B Foreign {Guid.NewGuid():N}",
            TimeZone = "Europe/Copenhagen",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        var widget = new Widget
        {
            Id = Guid.NewGuid(),
            BusinessId = b2.Id,
            Name = "Test Widget",
            PublicKey = Guid.NewGuid().ToString("N"),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            BusinessId = b2.Id,
            Name = "Foreign Customer",
            Email = "foreign.customer@marker-test.invalid",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        var x = new Conversation
        {
            Id = Guid.NewGuid(),
            BusinessId = b2.Id,
            WidgetId = widget.Id,
            CustomerId = customer.Id,
            VisitorSessionId = Guid.NewGuid().ToString("N"),
            Channel = "Website",
            Status = ConversationStatus.Open,
            HandledBy = handledBy,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Businesses.Add(b1);
        db.Businesses.Add(b2);
        db.Widgets.Add(widget);
        db.Customers.Add(customer);
        db.Conversations.Add(x);
        if (seedB1CustomerMessage)
        {
            db.Messages.Add(new Message
            {
                Id = Guid.NewGuid(),
                BusinessId = b1.Id,
                ConversationId = x.Id,
                Role = MessageRole.Customer,
                Content = "Do you ship to Denmark?",
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }
        _businessIdsToClean.Add(b1.Id);
        _businessIdsToClean.Add(b2.Id);
        await db.SaveChangesAsync();

        return (b1, x);
    }

    private async Task<List<AuditLog>> AuditRowsForBusinessAsync(Guid businessId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.AuditLogs.IgnoreQueryFilters().Where(a => a.BusinessId == businessId).ToListAsync();
    }

    // T-B1 (v11 test 13). Mutants: delete the check (no throw); move it below
    // retrieval (retrieval CallCount 1); throw before the audit write, or
    // drop the audit write (no audit row).
    [Fact]
    public async Task GenerateReplyAsync_ConversationOfAnotherBusiness_WritesAuditRowThenThrows_NoProviderCall()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (b1, x) = await SeedForeignConversationAsync(db, seedB1CustomerMessage: true);

        var fake = new FakeMessageService();
        var retrieval = new FakeKnowledgeRetrievalService();
        var fakeDispatch = new FakeToolDispatchService();
        var service = NewService(db, fake, retrieval, fakeDispatch, new NoOpConversationClassificationService());

        await Assert.ThrowsAsync<ConversationBusinessMismatchException>(
            () => service.GenerateReplyAsync(b1.Id, x.Id));

        Assert.Equal(0, fake.CallCount);
        Assert.Equal(0, retrieval.CallCount);
        Assert.Equal(0, fakeDispatch.CallCount);

        // Read through a fresh scope after the throw (v11 test 13(d)).
        var row = Assert.Single(await AuditRowsForBusinessAsync(b1.Id));
        Assert.Equal(AuditEventTypes.UnauthorizedActionBlocked, row.EventType);
        Assert.Equal(AuditEntityTypes.Business, row.EntityType);
        Assert.Equal(b1.Id.ToString(), row.EntityId);
        Assert.Equal(b1.Id, row.BusinessId);
        Assert.Null(row.ConversationId);
        Assert.Equal(ActorType.Customer, row.Actor);
        Assert.Null(row.OldValue);
        Assert.Equal("ConversationBusinessMismatch", row.NewValue);

        var foreignId = x.Id.ToString();
        Assert.DoesNotContain(foreignId, row.EntityType);
        Assert.DoesNotContain(foreignId, row.EntityId);
        Assert.DoesNotContain(foreignId, row.EventType);
        Assert.DoesNotContain(foreignId, row.NewValue!);
        Assert.NotEqual(x.Id, row.CorrelationId);
        Assert.NotEqual(x.Id, row.ChangedByUserId);
    }

    // T-B2. Mutant: move the check below the HandledBy return (returns null).
    [Fact]
    public async Task GenerateReplyAsync_ConversationOfAnotherBusiness_HandledByHuman_StillThrows()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (b1, x) = await SeedForeignConversationAsync(db, seedB1CustomerMessage: true, handledBy: HandledBy.Human);

        var service = NewService(db, new FakeMessageService(), new FakeKnowledgeRetrievalService(), new FakeToolDispatchService(), new NoOpConversationClassificationService());

        await Assert.ThrowsAsync<ConversationBusinessMismatchException>(
            () => service.GenerateReplyAsync(b1.Id, x.Id));

        Assert.Single(await AuditRowsForBusinessAsync(b1.Id));
    }

    // T-B3. Mutant: move the check below the empty-history return (returns null).
    [Fact]
    public async Task GenerateReplyAsync_ConversationOfAnotherBusiness_NoHistoryForPair_StillThrows()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (b1, x) = await SeedForeignConversationAsync(db, seedB1CustomerMessage: false);

        var service = NewService(db, new FakeMessageService(), new FakeKnowledgeRetrievalService(), new FakeToolDispatchService(), new NoOpConversationClassificationService());

        await Assert.ThrowsAsync<ConversationBusinessMismatchException>(
            () => service.GenerateReplyAsync(b1.Id, x.Id));

        Assert.Single(await AuditRowsForBusinessAsync(b1.Id));
    }

    // T-B4. Mutant: invert the check to == (this and every existing reply
    // test throws).
    [Fact]
    public async Task GenerateReplyAsync_ConversationOfSameBusiness_DoesNotThrow_WritesNoAuditRow()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (business, conversation) = await SeedConversationWithOneCustomerMessageAsync(db);

        var fake = new FakeMessageService { ReplyText = "Matched-business reply." };
        var service = NewService(db, fake, new FakeKnowledgeRetrievalService(), new ToolDispatchService([], fake, NullLogger<ToolDispatchService>.Instance), new NoOpConversationClassificationService());

        var reply = await service.GenerateReplyAsync(business.Id, conversation.Id);

        Assert.Equal("Matched-business reply.", reply?.Content);
        Assert.Empty(await AuditRowsForBusinessAsync(business.Id));
    }

    private class FixedTenantAccessor(Guid? businessId) : ICurrentTenantAccessor
    {
        public Guid? BusinessId => businessId;
    }
}
