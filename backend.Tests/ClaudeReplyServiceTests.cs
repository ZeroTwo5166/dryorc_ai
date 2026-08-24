using System.Net;
using Anthropic.Exceptions;
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
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Messages\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"KnowledgeChunks\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"KnowledgeDocuments\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Conversations\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Widgets\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Businesses\" WHERE \"Id\" = ANY({ids})");
        }

        await _provider.DisposeAsync();
    }

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

    // ── Success path — fake, but realistic response shape ──────────

    [Fact]
    public async Task GenerateReplyAsync_FakeRealisticResponse_ExtractsTextViaRealParsing_PersistsAiMessage()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (business, conversation) = await SeedConversationWithOneCustomerMessageAsync(db);

        var fake = new FakeMessageService { ReplyText = "Yes, we ship to Denmark within 3-5 business days." };
        var service = new ClaudeReplyService(db, fake, new FakeKnowledgeRetrievalService(), NullLogger<ClaudeReplyService>.Instance);

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

    // ── Step 12: RAG retrieval wiring ────────────────────────────────

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
        var service = new ClaudeReplyService(db, fake, retrieval, NullLogger<ClaudeReplyService>.Instance);

        await service.GenerateReplyAsync(business.Id, conversation.Id);

        Assert.Equal(1, retrieval.CallCount);
        Assert.Equal("Do you ship to Denmark?", retrieval.Requests[0].QueryText); // the seeded customer message
        Assert.Equal(business.Id, retrieval.Requests[0].BusinessId);

        var sentSystemPrompt = (string)fake.Requests[0].System!.Value!;
        Assert.Contains("MARKER_CHUNK_ONE: we ship worldwide.", sentSystemPrompt);
        Assert.Contains("[1]", sentSystemPrompt);
        Assert.Contains("Knowledge base context", sentSystemPrompt);
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
        var service = new ClaudeReplyService(db, fake, retrieval, NullLogger<ClaudeReplyService>.Instance);

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
        var service = new ClaudeReplyService(db, fake, retrieval, NullLogger<ClaudeReplyService>.Instance);

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
        var service = new ClaudeReplyService(db, fake, new FakeKnowledgeRetrievalService(), NullLogger<ClaudeReplyService>.Instance); // default: returns nothing

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
        var service = new ClaudeReplyService(db, fake, retrieval, NullLogger<ClaudeReplyService>.Instance);

        var reply = await service.GenerateReplyAsync(business.Id, conversation.Id);

        Assert.Equal("Still works.", reply?.Content); // the conversation continued normally, not broken by retrieval throwing
        var aiMessage = await db.Messages.IgnoreQueryFilters()
            .SingleAsync(m => m.ConversationId == conversation.Id && m.Role == MessageRole.Ai);
        var sources = await db.MessageKnowledgeSources.IgnoreQueryFilters()
            .Where(s => s.MessageId == aiMessage.Id).ToListAsync();
        Assert.Empty(sources);
        Assert.DoesNotContain("Knowledge base context", (string)fake.Requests[0].System!.Value!);
    }

    // ── Anthropic-typed failures — each returns null, nothing persisted ─

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
        var service = new ClaudeReplyService(db, fake, new FakeKnowledgeRetrievalService(), NullLogger<ClaudeReplyService>.Instance);

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
        var service = new ClaudeReplyService(db, fake, new FakeKnowledgeRetrievalService(), NullLogger<ClaudeReplyService>.Instance);

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
        var service = new ClaudeReplyService(db, fake, new FakeKnowledgeRetrievalService(), NullLogger<ClaudeReplyService>.Instance);

        var reply = await service.GenerateReplyAsync(business.Id, conversation.Id);

        Assert.Null(reply);
        await AssertNoAiMessageOrLastMessageAtChangeAsync(db, conversation.Id);
    }

    // ── Genuine network/timeout failures — same "return null" outcome,
    //    but a structurally different exception family (not the SDK's own
    //    typed hierarchy) ─────────────────────────────────────────────

    [Fact]
    public async Task GenerateReplyAsync_HttpRequestException_ReturnsNull_PersistsNothing()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (business, conversation) = await SeedConversationWithOneCustomerMessageAsync(db);

        var fake = new FakeMessageService { ExceptionToThrow = new HttpRequestException("Simulated network failure") };
        var service = new ClaudeReplyService(db, fake, new FakeKnowledgeRetrievalService(), NullLogger<ClaudeReplyService>.Instance);

        var reply = await service.GenerateReplyAsync(business.Id, conversation.Id);

        Assert.Null(reply);
        await AssertNoAiMessageOrLastMessageAtChangeAsync(db, conversation.Id);
    }

    // ── Missing configuration — must NOT be swallowed into a null reply ─

    [Fact]
    public async Task GenerateReplyAsync_MissingApiKeyConfiguration_ThrowsDistinctException_IsNotSwallowed()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (business, conversation) = await SeedConversationWithOneCustomerMessageAsync(db);

        // No real secrets touched — this constructs the exact stub
        // Program.cs registers when Anthropic:ApiKey is absent, directly,
        // independent of whatever's actually configured in this environment.
        var service = new ClaudeReplyService(db, new MissingAnthropicApiKeyMessageService(), new FakeKnowledgeRetrievalService(), NullLogger<ClaudeReplyService>.Instance);

        await Assert.ThrowsAsync<AnthropicNotConfiguredException>(
            () => service.GenerateReplyAsync(business.Id, conversation.Id));

        // Confirms the exception genuinely propagated rather than being
        // caught-and-converted somewhere — no AI message, no LastMessageAt
        // change, same as the other failure tests, but reached via a throw
        // the caller must handle, not a quiet null.
        await AssertNoAiMessageOrLastMessageAtChangeAsync(db, conversation.Id);
    }

    // ── Step 9: HandledBy gate ───────────────────────────────────────

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
        var service = new ClaudeReplyService(db, fake, new FakeKnowledgeRetrievalService(), NullLogger<ClaudeReplyService>.Instance);

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
        var service = new ClaudeReplyService(db, fake, new FakeKnowledgeRetrievalService(), NullLogger<ClaudeReplyService>.Instance);

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

    // ── Real Anthropic API call — explicitly opt-in only ────────────

    [LiveClaudeFact]
    public async Task GenerateReplyAsync_RealApiCall_ReturnsReplyAndPersistsAiMessage()
    {
        var configuration = new ConfigurationBuilder().AddUserSecrets<Program>().Build();
        var apiKey = configuration["Anthropic:ApiKey"]!; // LiveClaudeFact already guarantees this is present

        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (business, conversation) = await SeedConversationWithOneCustomerMessageAsync(db);

        var client = new Anthropic.AnthropicClient { ApiKey = apiKey };
        var service = new ClaudeReplyService(db, client.Messages, new FakeKnowledgeRetrievalService(), NullLogger<ClaudeReplyService>.Instance);

        var reply = await service.GenerateReplyAsync(business.Id, conversation.Id);

        Assert.False(string.IsNullOrWhiteSpace(reply?.Content));

        var aiMessage = await db.Messages.IgnoreQueryFilters()
            .SingleAsync(m => m.ConversationId == conversation.Id && m.Role == MessageRole.Ai);
        Assert.Equal(reply!.Content, aiMessage.Content);
        Assert.Equal(reply.Id, aiMessage.Id);

        var updatedConversation = await db.Conversations.IgnoreQueryFilters().SingleAsync(c => c.Id == conversation.Id);
        Assert.Equal(aiMessage.CreatedAt, updatedConversation.LastMessageAt);
    }

    private class FixedTenantAccessor(Guid? businessId) : ICurrentTenantAccessor
    {
        public Guid? BusinessId => businessId;
    }
}
