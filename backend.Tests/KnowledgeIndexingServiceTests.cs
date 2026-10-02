using backend.Constants;
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
/// Service-level tests for KnowledgeIndexingService — real Postgres,
/// FakeVoyageEmbeddingClient (never a real Voyage call). Deliberately calls
/// IKnowledgeIndexingService directly, no controller/HTTP involved — same
/// pattern WidgetConversationServiceTests already established for this
/// codebase's service-level tests.
/// </summary>
[Collection("KnowledgeChunks collection")]
public class KnowledgeIndexingServiceTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;
    private FakeVoyageEmbeddingClient _fakeVoyage = null!;
    // A1 Slice 4b design §7.5: invocation rows are captured, never persisted,
    // so the cleanup below needs no AiProviderInvocations delete.
    private readonly ConcurrentCapturingAiInvocationRecorder _capture = new();
    private readonly List<Guid> _businessIdsToClean = [];

    public Task InitializeAsync()
    {
        var configuration = new ConfigurationBuilder()
            .AddUserSecrets<Program>()
            .Build();

        var connectionUri = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection not found via user-secrets.");

        _fakeVoyage = new FakeVoyageEmbeddingClient();

        _provider = new ServiceCollection()
            .AddSingleton<ICurrentTenantAccessor>(new FixedTenantAccessor(null))
            .AddDbContext<AppDbContext>(o => o.UseNpgsql(
                PostgresConnectionString.FromUri(connectionUri), x => x.UseVector()))
            .AddLogging()
            .AddSingleton<IVoyageEmbeddingClient>(_fakeVoyage)
            .AddSingleton<IAiInvocationRecorder>(_capture)
            .AddScoped<IVoyageEmbeddingInvoker, VoyageEmbeddingInvoker>()
            .AddScoped<IKnowledgeIndexingService, KnowledgeIndexingService>()
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

            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"KnowledgeChunks\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"KnowledgeDocuments\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Businesses\" WHERE \"Id\" = ANY({ids})");
        }

        await _provider.DisposeAsync();
    }

    private async Task<Guid> CreateBusinessAsync(AppDbContext db)
    {
        var business = new Business
        {
            Id = Guid.NewGuid(),
            Name = $"Knowledge Indexing Test {Guid.NewGuid():N}",
            TimeZone = "Europe/Copenhagen",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Businesses.Add(business);
        await db.SaveChangesAsync();
        _businessIdsToClean.Add(business.Id);
        return business.Id;
    }

    // Recomputes the exact deterministic vector FakeVoyageEmbeddingClient
    // would have produced for this text, so a persisted embedding can be
    // checked against the specific chunk text it's supposed to represent —
    // not just "some 1024-dim vector exists."
    private static float[] ExpectedVectorFor(string text)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text));
        var seed = BitConverter.ToInt32(hash, 0);
        var random = new Random(seed);
        var vector = new float[FakeVoyageEmbeddingClient.Dimension];
        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] = (float)(random.NextDouble() * 2 - 1);
        }
        return vector;
    }

    [Fact]
    public async Task CreateAsync_PersistsDocumentAndCompleteCorrectlyMappedChunkSet()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var indexing = scope.ServiceProvider.GetRequiredService<IKnowledgeIndexingService>();
        var businessId = await CreateBusinessAsync(db);

        var p1 = "MARKER_ONE " + new string('a', 300);
        var p2 = "MARKER_TWO " + new string('b', 300);
        var content = p1 + "\n\n" + p2 + "\n\n" + new string('c', 300); // 3 paragraphs, forced into 2 chunks

        var document = await indexing.CreateAsync(businessId, KnowledgeDocumentType.Faq, "Test Doc", content);

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var chunks = await verifyDb.KnowledgeChunks
            .IgnoreQueryFilters()
            .Where(c => c.KnowledgeDocumentId == document.Id)
            .OrderBy(c => c.ChunkIndex)
            .ToListAsync();

        Assert.NotEmpty(chunks);
        for (var i = 0; i < chunks.Count; i++)
        {
            Assert.Equal(i, chunks[i].ChunkIndex); // 0-based, sequential
            Assert.Equal(businessId, chunks[i].BusinessId); // explicit from parent, not ambient
            Assert.Equal(document.Id, chunks[i].KnowledgeDocumentId);
            Assert.Equal("voyage-3", chunks[i].EmbeddingModel);
            Assert.Equal(FakeVoyageEmbeddingClient.Dimension, chunks[i].Embedding.ToArray().Length); // read back from Postgres

            // Embedding actually corresponds to this specific chunk's own
            // text, not some other chunk's — proves the index-based mapping
            // held all the way through persistence.
            var expected = ExpectedVectorFor(chunks[i].Content);
            Assert.Equal(expected, chunks[i].Embedding.ToArray());
        }
    }

    [Fact]
    public async Task CreateAsync_EmptyContent_PersistsDocumentWithNoChunks_NeverCallsVoyage()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var indexing = scope.ServiceProvider.GetRequiredService<IKnowledgeIndexingService>();
        var businessId = await CreateBusinessAsync(db);

        var callCountBefore = _fakeVoyage.CallCount;
        var document = await indexing.CreateAsync(businessId, KnowledgeDocumentType.Faq, "Empty Doc", "   ");

        var chunks = await db.KnowledgeChunks.IgnoreQueryFilters().Where(c => c.KnowledgeDocumentId == document.Id).ToListAsync();
        Assert.Empty(chunks);
        Assert.Equal(callCountBefore, _fakeVoyage.CallCount);
    }

    [Fact]
    public async Task CreateAsync_VoyageFailure_PersistsNothing()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var indexing = scope.ServiceProvider.GetRequiredService<IKnowledgeIndexingService>();
        var businessId = await CreateBusinessAsync(db);

        _fakeVoyage.ExceptionToThrow = new VoyageEmbeddingException("simulated failure");
        try
        {
            await Assert.ThrowsAsync<VoyageEmbeddingException>(() =>
                indexing.CreateAsync(businessId, KnowledgeDocumentType.Faq, "Should Not Persist", "Some real content here."));

            var documents = await db.KnowledgeDocuments.IgnoreQueryFilters().Where(d => d.BusinessId == businessId).ToListAsync();
            Assert.Empty(documents);
        }
        finally
        {
            _fakeVoyage.ExceptionToThrow = null;
        }
    }

    [Fact]
    public async Task UpdateAsync_ContentUnchanged_UpdatesMetadataOnly_NeverTouchesChunksOrVoyage()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var indexing = scope.ServiceProvider.GetRequiredService<IKnowledgeIndexingService>();
        var businessId = await CreateBusinessAsync(db);

        var content = "Stable content that will not change.";
        var document = await indexing.CreateAsync(businessId, KnowledgeDocumentType.Faq, "Original Title", content);

        var chunksBefore = await db.KnowledgeChunks
            .IgnoreQueryFilters().Where(c => c.KnowledgeDocumentId == document.Id).ToListAsync();
        var callCountBefore = _fakeVoyage.CallCount;

        var updated = await indexing.UpdateAsync(document, KnowledgeDocumentType.Policy, "New Title", content, false);

        Assert.Equal("New Title", updated.Title);
        Assert.Equal(KnowledgeDocumentType.Policy, updated.Type);
        Assert.False(updated.IsActive);
        Assert.Equal(content, updated.Content);
        Assert.Equal(callCountBefore, _fakeVoyage.CallCount); // no Voyage call

        var chunksAfter = await db.KnowledgeChunks
            .IgnoreQueryFilters().Where(c => c.KnowledgeDocumentId == document.Id).OrderBy(c => c.ChunkIndex).ToListAsync();
        Assert.Equal(chunksBefore.Select(c => c.Id), chunksAfter.Select(c => c.Id)); // exact same rows, not just same count
        Assert.Equal(chunksBefore.Select(c => c.UpdatedAt), chunksAfter.Select(c => c.UpdatedAt)); // genuinely untouched
    }

    [Fact]
    public async Task UpdateAsync_ContentChanged_ReplacesChunkSetCompletely()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var indexing = scope.ServiceProvider.GetRequiredService<IKnowledgeIndexingService>();
        var businessId = await CreateBusinessAsync(db);

        var document = await indexing.CreateAsync(businessId, KnowledgeDocumentType.Faq, "Doc", "MARKER_OLD content here.");
        var oldChunkIds = (await db.KnowledgeChunks.IgnoreQueryFilters().Where(c => c.KnowledgeDocumentId == document.Id).ToListAsync())
            .Select(c => c.Id).ToHashSet();

        var updated = await indexing.UpdateAsync(document, KnowledgeDocumentType.Faq, "Doc", "MARKER_NEW content here.", true);

        var newChunks = await db.KnowledgeChunks
            .IgnoreQueryFilters().Where(c => c.KnowledgeDocumentId == document.Id).ToListAsync();

        Assert.Equal("MARKER_NEW content here.", updated.Content);
        Assert.All(newChunks, c => Assert.DoesNotContain(c.Id, oldChunkIds)); // old rows genuinely gone, not reused
        Assert.Contains("MARKER_NEW", newChunks.Single().Content);
        Assert.Equal(businessId, newChunks.Single().BusinessId);
    }

    [Fact]
    public async Task UpdateAsync_VoyageFailure_LeavesDocumentAndChunksUntouched()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var indexing = scope.ServiceProvider.GetRequiredService<IKnowledgeIndexingService>();
        var businessId = await CreateBusinessAsync(db);

        var document = await indexing.CreateAsync(businessId, KnowledgeDocumentType.Faq, "Doc", "MARKER_ORIGINAL content.");
        var originalChunkIds = (await db.KnowledgeChunks.IgnoreQueryFilters().Where(c => c.KnowledgeDocumentId == document.Id).ToListAsync())
            .Select(c => c.Id).OrderBy(id => id).ToList();

        _fakeVoyage.ExceptionToThrow = new VoyageEmbeddingException("simulated failure");
        try
        {
            await Assert.ThrowsAsync<VoyageEmbeddingException>(() =>
                indexing.UpdateAsync(document, KnowledgeDocumentType.Faq, "Doc", "MARKER_ATTEMPTED_CHANGE content.", true));
        }
        finally
        {
            _fakeVoyage.ExceptionToThrow = null;
        }

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var persisted = await verifyDb.KnowledgeDocuments.IgnoreQueryFilters().SingleAsync(d => d.Id == document.Id);
        Assert.Equal("MARKER_ORIGINAL content.", persisted.Content);

        var chunksAfter = (await verifyDb.KnowledgeChunks.IgnoreQueryFilters().Where(c => c.KnowledgeDocumentId == document.Id).ToListAsync())
            .Select(c => c.Id).OrderBy(id => id).ToList();
        Assert.Equal(originalChunkIds, chunksAfter);
    }

    /// <summary>
    /// Concurrency consistency proof: two content-changing updates race on
    /// the same document. Asserts the final KnowledgeDocument.Content always
    /// corresponds exactly to the final KnowledgeChunk set — never a mix of
    /// one update's content with the other's chunks — and that both requests
    /// complete successfully (the advisory lock prevents the raw
    /// unique-constraint-violation interleaving that would otherwise be
    /// possible on (KnowledgeDocumentId, ChunkIndex)).
    /// </summary>
    [Fact]
    public async Task UpdateAsync_ConcurrentContentChangingUpdates_NeverLeaveDocumentAndChunksInconsistent()
    {
        await using var seedScope = _provider.CreateAsyncScope();
        var seedDb = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var seedIndexing = seedScope.ServiceProvider.GetRequiredService<IKnowledgeIndexingService>();
        var businessId = await CreateBusinessAsync(seedDb);

        var document = await seedIndexing.CreateAsync(businessId, KnowledgeDocumentType.Faq, "Doc", "MARKER_INITIAL content.");

        // Two independent scopes (independent DbContext instances, like two
        // real concurrent HTTP requests each with their own scoped
        // AppDbContext), each loading its own copy of the document and
        // racing a different content change.
        async Task<KnowledgeDocument> RunUpdate(string contentMarker)
        {
            await using var scope = _provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var indexing = scope.ServiceProvider.GetRequiredService<IKnowledgeIndexingService>();
            var loaded = await db.KnowledgeDocuments.IgnoreQueryFilters().SingleAsync(d => d.Id == document.Id);
            return await indexing.UpdateAsync(loaded, KnowledgeDocumentType.Faq, "Doc", $"MARKER_{contentMarker} content.", true);
        }

        var results = await Task.WhenAll(RunUpdate("A"), RunUpdate("B"));
        Assert.All(results, r => Assert.True(r.Content is "MARKER_A content." or "MARKER_B content.")); // both succeeded, neither threw

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var finalDocument = await verifyDb.KnowledgeDocuments.IgnoreQueryFilters().SingleAsync(d => d.Id == document.Id);
        var finalChunks = await verifyDb.KnowledgeChunks
            .IgnoreQueryFilters().Where(c => c.KnowledgeDocumentId == document.Id).OrderBy(c => c.ChunkIndex).ToListAsync();

        // The final chunk set's own text must correspond to the final
        // document's Content — whichever update actually won.
        Assert.Single(finalChunks);
        Assert.Equal(finalDocument.Content, finalChunks[0].Content);
        Assert.True(finalDocument.Content is "MARKER_A content." or "MARKER_B content.");

        // No duplicate ChunkIndex values could have survived (the unique
        // index would have thrown during SaveChangesAsync otherwise) — this
        // is implied by both updates completing successfully above, but
        // re-confirmed directly here too.
        Assert.Equal(finalChunks.Select(c => c.ChunkIndex).Distinct().Count(), finalChunks.Count);
    }

    // ── A1 Slice 4b (design §10.3): invocation capture at V1/V2 ────────

    // T-V4b1. Mutations: wrong Operation; Guid.Empty as the business.
    [Fact]
    public async Task CreateAsync_RecordsOneKnowledgeIndexingRowSeq0_WithChunkCount()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var indexing = scope.ServiceProvider.GetRequiredService<IKnowledgeIndexingService>();
        var businessId = await CreateBusinessAsync(db);

        var content = "MARKER_ONE " + new string('a', 300) + "\n\n" + "MARKER_TWO " + new string('b', 300) + "\n\n" + new string('c', 300);
        var chunkCount = KnowledgeChunker.Chunk(content).Count;
        Assert.True(chunkCount > 1);

        await indexing.CreateAsync(businessId, KnowledgeDocumentType.Faq, "Capture Doc", content);

        var record = Assert.Single(_capture.ForBusiness(businessId));
        Assert.Equal(AiOperations.KnowledgeIndexing, record.Operation);
        Assert.Equal(0, record.Sequence);
        Assert.Equal(businessId, record.BusinessId);
        Assert.Null(record.ConversationId);
        Assert.Equal(AiProviders.Voyage, record.Provider);
        Assert.Equal(AiOutcomes.Succeeded, record.Outcome);
        Assert.Equal(chunkCount, record.InputItemCount);
    }

    // T-V4b2. Mutation: reuse one static/field CorrelationId.
    [Fact]
    public async Task UpdateAsync_ContentChanged_RecordsOneRowForDocumentBusiness_WithItsOwnCorrelation()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var indexing = scope.ServiceProvider.GetRequiredService<IKnowledgeIndexingService>();
        var businessId = await CreateBusinessAsync(db);

        var document = await indexing.CreateAsync(businessId, KnowledgeDocumentType.Faq, "Doc", "MARKER_OLD content here.");
        await indexing.UpdateAsync(document, KnowledgeDocumentType.Faq, "Doc", "MARKER_NEW content here.", true);

        var records = _capture.ForBusiness(businessId);
        Assert.Equal(2, records.Count);
        var (create, update) = (records[0], records[1]);
        Assert.Equal(document.BusinessId, update.BusinessId);
        Assert.Equal(AiOperations.KnowledgeIndexing, update.Operation);
        Assert.Equal(0, update.Sequence);
        Assert.Equal(AiOutcomes.Succeeded, update.Outcome);
        Assert.NotEqual(create.CorrelationId, update.CorrelationId);
    }

    // T-V4b3. Mutation: move the invoker call above the contentChanged
    // early return.
    [Fact]
    public async Task UpdateAsync_ContentUnchanged_RecordsNothing()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var indexing = scope.ServiceProvider.GetRequiredService<IKnowledgeIndexingService>();
        var businessId = await CreateBusinessAsync(db);

        var content = "Stable content that will not change.";
        var document = await indexing.CreateAsync(businessId, KnowledgeDocumentType.Faq, "Original Title", content);
        var recordsBefore = _capture.ForBusiness(businessId).Count;
        var callCountBefore = _fakeVoyage.CallCount;

        await indexing.UpdateAsync(document, KnowledgeDocumentType.Policy, "New Title", content, false);

        Assert.Equal(1, recordsBefore);
        Assert.Equal(recordsBefore, _capture.ForBusiness(businessId).Count);
        Assert.Equal(callCountBefore, _fakeVoyage.CallCount);
    }

    // T-V4b4 (create). Mutation: catch-and-swallow the Voyage failure in
    // the service (the throw assertion goes red).
    [Fact]
    public async Task CreateAsync_VoyageFailure_StillThrows_RecordsOneFailedRow()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var indexing = scope.ServiceProvider.GetRequiredService<IKnowledgeIndexingService>();
        var businessId = await CreateBusinessAsync(db);

        _fakeVoyage.ExceptionToThrow = new VoyageEmbeddingException("simulated failure");
        try
        {
            await Assert.ThrowsAsync<VoyageEmbeddingException>(() =>
                indexing.CreateAsync(businessId, KnowledgeDocumentType.Faq, "Should Not Persist", "Some real content here."));
        }
        finally
        {
            _fakeVoyage.ExceptionToThrow = null;
        }

        var record = Assert.Single(_capture.ForBusiness(businessId));
        Assert.Equal(AiOperations.KnowledgeIndexing, record.Operation);
        Assert.Equal(AiOutcomes.Failed, record.Outcome);
    }

    // T-V4b4 (update). Same mutation, on the V2 path.
    [Fact]
    public async Task UpdateAsync_VoyageFailure_StillThrows_RecordsOneFailedRow()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var indexing = scope.ServiceProvider.GetRequiredService<IKnowledgeIndexingService>();
        var businessId = await CreateBusinessAsync(db);

        var document = await indexing.CreateAsync(businessId, KnowledgeDocumentType.Faq, "Doc", "MARKER_ORIGINAL content.");

        _fakeVoyage.ExceptionToThrow = new VoyageEmbeddingException("simulated failure");
        try
        {
            await Assert.ThrowsAsync<VoyageEmbeddingException>(() =>
                indexing.UpdateAsync(document, KnowledgeDocumentType.Faq, "Doc", "MARKER_ATTEMPTED_CHANGE content.", true));
        }
        finally
        {
            _fakeVoyage.ExceptionToThrow = null;
        }

        var records = _capture.ForBusiness(businessId);
        Assert.Equal(2, records.Count);
        Assert.Equal(AiOutcomes.Succeeded, records[0].Outcome);
        Assert.Equal(AiOutcomes.Failed, records[1].Outcome);
        Assert.Single(records, r => r.Outcome == AiOutcomes.Failed);
    }

    private class FixedTenantAccessor(Guid? businessId) : ICurrentTenantAccessor
    {
        public Guid? BusinessId => businessId;
    }
}
