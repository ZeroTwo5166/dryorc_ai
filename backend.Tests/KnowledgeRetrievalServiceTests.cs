using backend.Constants;
using backend.Data;
using backend.Enums;
using backend.Interfaces;
using backend.Models;
using backend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pgvector;
using Xunit;

namespace backend.Tests;

/// <summary>
/// Service-level tests for KnowledgeRetrievalService (Step 12 — RAG
/// retrieval) — real Postgres/pgvector, FakeVoyageEmbeddingClient (never a
/// real Voyage call). Same bare-ServiceProvider pattern as
/// KnowledgeIndexingServiceTests/WidgetConversationServiceTests.
///
/// Ordering/threshold/recall tests seed KnowledgeChunk rows with
/// hand-constructed embedding vectors at exact, known cosine distances from
/// the query vector (VectorAtCosineDistance below) rather than relying on
/// FakeVoyageEmbeddingClient's per-text random vectors for the chunk side —
/// two arbitrary random 1024-dim vectors have no controllable relationship
/// to each other, so precise ordering/threshold assertions need vectors
/// constructed directly against the actual query vector Voyage (the fake)
/// will produce for the query text.
/// </summary>
[Collection("KnowledgeChunks collection")]
public class KnowledgeRetrievalServiceTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;
    private FakeVoyageEmbeddingClient _fakeVoyage = null!;
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
            .AddScoped<IKnowledgeRetrievalService, KnowledgeRetrievalService>()
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
            Name = $"Knowledge Retrieval Test {Guid.NewGuid():N}",
            TimeZone = "Europe/Copenhagen",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Businesses.Add(business);
        await db.SaveChangesAsync();
        _businessIdsToClean.Add(business.Id);
        return business.Id;
    }

    private static async Task<Guid> SeedDocumentAsync(AppDbContext db, Guid businessId, string title, bool isActive = true)
    {
        var document = new KnowledgeDocument
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            Type = KnowledgeDocumentType.Faq,
            Title = title,
            Content = "irrelevant to these tests — chunk embeddings are hand-constructed directly",
            IsActive = isActive,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.KnowledgeDocuments.Add(document);
        await db.SaveChangesAsync();
        return document.Id;
    }

    private static async Task<Guid> SeedChunkAsync(
        AppDbContext db, Guid businessId, Guid documentId, int chunkIndex, string content, float[] embedding)
    {
        var chunk = new KnowledgeChunk
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            KnowledgeDocumentId = documentId,
            ChunkIndex = chunkIndex,
            Content = content,
            Embedding = new Vector(embedding),
            EmbeddingModel = "voyage-3",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.KnowledgeChunks.Add(chunk);
        await db.SaveChangesAsync();
        return chunk.Id;
    }

    /// <summary>
    /// Constructs a vector whose pgvector CosineDistance to `reference` is
    /// exactly `distance` (up to float precision), regardless of magnitude
    /// (cosine similarity/distance is scale-invariant): normalizes
    /// `reference`, builds a unit vector orthogonal to it via Gram-Schmidt,
    /// then combines the two by cos(theta)/sin(theta) where
    /// theta = arccos(1 - distance) — since cosine distance is defined as
    /// 1 - cos(theta), this places the result at exactly that angle from
    /// `reference`.
    /// </summary>
    private static float[] VectorAtCosineDistance(float[] reference, double distance)
    {
        var n = reference.Length;
        var refNorm = Math.Sqrt(reference.Sum(x => (double)x * x));
        var qHat = reference.Select(x => x / refNorm).ToArray();

        // Any seed vector not parallel to qHat, then Gram-Schmidt-orthogonalized.
        var seed = new double[n];
        seed[0] = 1.0;
        seed[1] = 1.0;
        var dot = seed.Zip(qHat, (a, b) => a * b).Sum();
        var orthogonal = seed.Zip(qHat, (a, b) => a - dot * b).ToArray();
        var orthoNorm = Math.Sqrt(orthogonal.Sum(x => x * x));
        var orthoHat = orthogonal.Select(x => x / orthoNorm).ToArray();

        var cosTheta = 1.0 - distance;
        var sinTheta = Math.Sqrt(Math.Max(0.0, 1.0 - cosTheta * cosTheta));

        return qHat.Zip(orthoHat, (q, o) => (float)(cosTheta * q + sinTheta * o)).ToArray();
    }

    // ── Basic mechanics ──────────────────────────────────────────────

    [Fact]
    public async Task RetrieveAsync_UsesVoyageInputTypeQuery()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var retrieval = scope.ServiceProvider.GetRequiredService<IKnowledgeRetrievalService>();
        var businessId = await CreateBusinessAsync(db);

        await retrieval.RetrieveAsync(businessId, Guid.NewGuid(),"what is the return policy");

        Assert.Single(_fakeVoyage.Requests);
        Assert.Equal(VoyageInputType.Query, _fakeVoyage.Requests[0].InputType);
        Assert.Equal(["what is the return policy"], _fakeVoyage.Requests[0].Texts);
    }

    [Fact]
    public async Task RetrieveAsync_ReturnsCorrectDocumentAndChunkMetadata()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var retrieval = scope.ServiceProvider.GetRequiredService<IKnowledgeRetrievalService>();
        var businessId = await CreateBusinessAsync(db);

        var queryText = "return policy question";
        var queryVector = FakeVoyageEmbeddingClient.DeterministicVector(queryText);
        var documentId = await SeedDocumentAsync(db, businessId, "Return Policy");
        var chunkId = await SeedChunkAsync(db, businessId, documentId, 0, "30-day returns.", VectorAtCosineDistance(queryVector, 0.05));

        var results = await retrieval.RetrieveAsync(businessId, Guid.NewGuid(),queryText);

        var result = Assert.Single(results);
        Assert.Equal(chunkId, result.KnowledgeChunkId);
        Assert.Equal(documentId, result.KnowledgeDocumentId);
        Assert.Equal("Return Policy", result.DocumentTitle);
        Assert.Equal("30-day returns.", result.Content);
    }

    // ── Ordering / Top-K / threshold ─────────────────────────────────

    [Fact]
    public async Task RetrieveAsync_OrdersByCosineDistance_ClosestFirst()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var retrieval = scope.ServiceProvider.GetRequiredService<IKnowledgeRetrievalService>();
        var businessId = await CreateBusinessAsync(db);

        var queryText = "shipping question";
        var queryVector = FakeVoyageEmbeddingClient.DeterministicVector(queryText);
        var documentId = await SeedDocumentAsync(db, businessId, "Shipping Doc");
        await SeedChunkAsync(db, businessId, documentId, 0, "MARKER_MID", VectorAtCosineDistance(queryVector, 0.3));
        await SeedChunkAsync(db, businessId, documentId, 1, "MARKER_NEAR", VectorAtCosineDistance(queryVector, 0.1));
        await SeedChunkAsync(db, businessId, documentId, 2, "MARKER_FAR_BUT_INCLUDED", VectorAtCosineDistance(queryVector, 0.45));

        var results = await retrieval.RetrieveAsync(businessId, Guid.NewGuid(),queryText);

        Assert.Equal(["MARKER_NEAR", "MARKER_MID", "MARKER_FAR_BUT_INCLUDED"], results.Select(r => r.Content));
    }

    [Fact]
    public async Task RetrieveAsync_TopK_LimitsToFive_ReturningTheFiveClosest()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var retrieval = scope.ServiceProvider.GetRequiredService<IKnowledgeRetrievalService>();
        var businessId = await CreateBusinessAsync(db);

        var queryText = "top k test";
        var queryVector = FakeVoyageEmbeddingClient.DeterministicVector(queryText);
        var documentId = await SeedDocumentAsync(db, businessId, "Doc");

        // 7 chunks, all within the 0.5 threshold, at distances 0.05..0.35.
        var distances = new[] { 0.05, 0.10, 0.15, 0.20, 0.25, 0.30, 0.35 };
        for (var i = 0; i < distances.Length; i++)
        {
            await SeedChunkAsync(db, businessId, documentId, i, $"CHUNK_{i}", VectorAtCosineDistance(queryVector, distances[i]));
        }

        var results = await retrieval.RetrieveAsync(businessId, Guid.NewGuid(),queryText);

        Assert.Equal(KnowledgeRetrievalService.TopK, results.Count);
        Assert.Equal(5, results.Count);
        // The 5 closest (indices 0-4, distances 0.05-0.25) — the two
        // farthest (0.30, 0.35) must be excluded by Top-K, not just
        // truncated arbitrarily.
        Assert.Equal(["CHUNK_0", "CHUNK_1", "CHUNK_2", "CHUNK_3", "CHUNK_4"], results.Select(r => r.Content));
    }

    [Fact]
    public async Task RetrieveAsync_ThresholdFiltering_ExcludesChunksAtOrBeyondPointFive()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var retrieval = scope.ServiceProvider.GetRequiredService<IKnowledgeRetrievalService>();
        var businessId = await CreateBusinessAsync(db);

        var queryText = "threshold test";
        var queryVector = FakeVoyageEmbeddingClient.DeterministicVector(queryText);
        var documentId = await SeedDocumentAsync(db, businessId, "Doc");
        await SeedChunkAsync(db, businessId, documentId, 0, "INCLUDED_NEAR", VectorAtCosineDistance(queryVector, 0.3));
        await SeedChunkAsync(db, businessId, documentId, 1, "INCLUDED_JUST_UNDER", VectorAtCosineDistance(queryVector, 0.49));
        await SeedChunkAsync(db, businessId, documentId, 2, "EXCLUDED_JUST_OVER", VectorAtCosineDistance(queryVector, 0.51));
        await SeedChunkAsync(db, businessId, documentId, 3, "EXCLUDED_FAR", VectorAtCosineDistance(queryVector, 0.7));

        var results = await retrieval.RetrieveAsync(businessId, Guid.NewGuid(),queryText);

        Assert.Equal(["INCLUDED_NEAR", "INCLUDED_JUST_UNDER"], results.Select(r => r.Content));
    }

    // ── IsActive / empty / tenant isolation ──────────────────────────

    [Fact]
    public async Task RetrieveAsync_InactiveDocumentChunksExcluded_EvenWhenMoreRelevantThanActiveOnes()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var retrieval = scope.ServiceProvider.GetRequiredService<IKnowledgeRetrievalService>();
        var businessId = await CreateBusinessAsync(db);

        var queryText = "active document test";
        var queryVector = FakeVoyageEmbeddingClient.DeterministicVector(queryText);

        var activeDocId = await SeedDocumentAsync(db, businessId, "Active Doc", isActive: true);
        await SeedChunkAsync(db, businessId, activeDocId, 0, "FROM_ACTIVE_DOC", VectorAtCosineDistance(queryVector, 0.2));

        var inactiveDocId = await SeedDocumentAsync(db, businessId, "Inactive Doc", isActive: false);
        // Deliberately CLOSER than the active document's chunk — proves the
        // IsActive filter genuinely excludes it, not just "happens to be
        // ranked lower."
        await SeedChunkAsync(db, businessId, inactiveDocId, 0, "FROM_INACTIVE_DOC", VectorAtCosineDistance(queryVector, 0.05));

        var results = await retrieval.RetrieveAsync(businessId, Guid.NewGuid(),queryText);

        Assert.Equal(["FROM_ACTIVE_DOC"], results.Select(r => r.Content));
    }

    [Fact]
    public async Task RetrieveAsync_EmptyWhenNoChunksExist()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var retrieval = scope.ServiceProvider.GetRequiredService<IKnowledgeRetrievalService>();
        var businessId = await CreateBusinessAsync(db);

        var results = await retrieval.RetrieveAsync(businessId, Guid.NewGuid(),"anything");

        Assert.Empty(results);
    }

    [Fact]
    public async Task RetrieveAsync_TenantIsolation_NeverReturnsAnotherBusinesssChunk_EvenAsGlobalNearestNeighbor()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var retrieval = scope.ServiceProvider.GetRequiredService<IKnowledgeRetrievalService>();
        var businessIdA = await CreateBusinessAsync(db);
        var businessIdB = await CreateBusinessAsync(db);

        var queryText = "tenant isolation test";
        var queryVector = FakeVoyageEmbeddingClient.DeterministicVector(queryText);

        // Business A has the closest possible chunk (distance ~0 — same
        // content as the query itself). Business B has nothing of its own.
        var documentIdA = await SeedDocumentAsync(db, businessIdA, "Business A Doc");
        await SeedChunkAsync(db, businessIdA, documentIdA, 0, "BUSINESS_A_CONTENT", VectorAtCosineDistance(queryVector, 0.01));

        var resultsForB = await retrieval.RetrieveAsync(businessIdB, Guid.NewGuid(), queryText);

        Assert.Empty(resultsForB); // never leaks Business A's chunk, even as the only/globally-nearest candidate
    }

    // ── Voyage failure ────────────────────────────────────────────────

    [Fact]
    public async Task RetrieveAsync_VoyageEmbeddingException_ReturnsEmptyWithoutThrowing()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var retrieval = scope.ServiceProvider.GetRequiredService<IKnowledgeRetrievalService>();
        var businessId = await CreateBusinessAsync(db);

        _fakeVoyage.ExceptionToThrow = new VoyageEmbeddingException("simulated Voyage outage");
        try
        {
            var results = await retrieval.RetrieveAsync(businessId, Guid.NewGuid(),"anything");
            Assert.Empty(results);
        }
        finally
        {
            _fakeVoyage.ExceptionToThrow = null;
        }
    }

    [Fact]
    public async Task RetrieveAsync_VoyageNotConfiguredException_ReturnsEmptyWithoutThrowing()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var retrieval = scope.ServiceProvider.GetRequiredService<IKnowledgeRetrievalService>();
        var businessId = await CreateBusinessAsync(db);

        _fakeVoyage.ExceptionToThrow = new VoyageNotConfiguredException();
        try
        {
            var results = await retrieval.RetrieveAsync(businessId, Guid.NewGuid(),"anything");
            Assert.Empty(results);
        }
        finally
        {
            _fakeVoyage.ExceptionToThrow = null;
        }
    }

    // ── HNSW + tenant-filter recall (required) ───────────────────────

    /// <summary>
    /// Distinct from RetrieveAsync_TenantIsolation_* above: that test proves
    /// no cross-tenant leakage. This test proves the stronger, separate
    /// property explicitly required alongside it: that Business A's own
    /// genuinely-relevant Top-K results are NOT silently degraded (fewer
    /// than expected, or wrong ones) by the tenant filter interacting with
    /// the HNSW approximate-nearest-neighbor search when a large number of
    /// OTHER businesses' vectors — including many objectively closer to the
    /// query than Business A's own — sit in the same index.
    ///
    /// Business B seeds 60 chunks (a deliberately non-trivial number to
    /// actually exercise HNSW's candidate-set behavior, not just prove the
    /// WHERE clause exists) at distances mostly closer than Business A's own
    /// 5 relevant chunks. If tenant filtering were applied incorrectly
    /// (e.g. after an under-sized ANN candidate set rather than
    /// integrated into the index scan), Business A could plausibly receive
    /// fewer than its true 5 relevant chunks, or the wrong ones.
    /// </summary>
    [Fact]
    public async Task RetrieveAsync_HnswTenantFilterRecall_BusinessAGetsExactlyItsOwnTopFive_DespiteCloserNoiseFromOtherBusinesses()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var retrieval = scope.ServiceProvider.GetRequiredService<IKnowledgeRetrievalService>();
        var businessIdA = await CreateBusinessAsync(db);
        var businessIdB = await CreateBusinessAsync(db);

        var queryText = "hnsw recall test query";
        var queryVector = FakeVoyageEmbeddingClient.DeterministicVector(queryText);

        // Business A: exactly 5 genuinely relevant chunks (within threshold).
        var documentIdA = await SeedDocumentAsync(db, businessIdA, "Business A Relevant Doc");
        var expectedChunkIds = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            var distance = 0.10 + i * 0.05; // 0.10, 0.15, 0.20, 0.25, 0.30 — all well within 0.5
            var chunkId = await SeedChunkAsync(db, businessIdA, documentIdA, i, $"A_RELEVANT_{i}", VectorAtCosineDistance(queryVector, distance));
            expectedChunkIds.Add(chunkId);
        }

        // Business B: 60 "noise" chunks, many CLOSER to the query than any
        // of Business A's own chunks (distances 0.01 upward), to genuinely
        // stress the tenant-filter + HNSW interaction rather than trivially
        // pass because nothing else is nearby.
        var documentIdB = await SeedDocumentAsync(db, businessIdB, "Business B Noise Doc");
        for (var i = 0; i < 60; i++)
        {
            var distance = 0.01 + i * 0.006; // 0.01 .. ~0.364 — heavily overlapping/closer than Business A's range
            await SeedChunkAsync(db, businessIdB, documentIdB, i, $"B_NOISE_{i}", VectorAtCosineDistance(queryVector, distance));
        }

        var results = await retrieval.RetrieveAsync(businessIdA, Guid.NewGuid(), queryText);

        // Not just "no cross-tenant leakage" (implied — none of Business B's
        // content markers appear) but the stronger, explicitly required
        // property: Business A's own top 5 are actually all present.
        Assert.Equal(5, results.Count);
        Assert.Equal(expectedChunkIds.OrderBy(id => id), results.Select(r => r.KnowledgeChunkId).OrderBy(id => id));
        Assert.All(results, r => Assert.StartsWith("A_RELEVANT_", r.Content));
    }

    // ── A1 Slice 4a (design §6.2 V3): invocation capture at V3 ─────────

    // T-V3a. Mutations: wrong Operation; ConversationId omitted; Guid.Empty
    // as the business; revert to the raw IVoyageEmbeddingClient (0 records).
    [Fact]
    public async Task RetrieveAsync_RecordsOneKnowledgeRetrievalQueryRowSeq0_ForBusinessAndConversation()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var retrieval = scope.ServiceProvider.GetRequiredService<IKnowledgeRetrievalService>();
        var businessId = await CreateBusinessAsync(db);
        var conversationId = Guid.NewGuid();

        await retrieval.RetrieveAsync(businessId, conversationId, "what is the return policy");

        var record = Assert.Single(_capture.ForBusiness(businessId));
        Assert.Equal(AiOperations.KnowledgeRetrievalQuery, record.Operation);
        Assert.Equal(0, record.Sequence);
        Assert.Equal(businessId, record.BusinessId);
        Assert.Equal(conversationId, record.ConversationId);
        Assert.Equal(1, record.InputItemCount);
        Assert.Equal(AiProviders.Voyage, record.Provider);
        Assert.Equal(AiOutcomes.Succeeded, record.Outcome);
    }

    // T-V3b. Mutation: hoist the CorrelationId to a field.
    [Fact]
    public async Task RetrieveAsync_TwoCalls_RecordDistinctCorrelationIds()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var retrieval = scope.ServiceProvider.GetRequiredService<IKnowledgeRetrievalService>();
        var businessId = await CreateBusinessAsync(db);
        var conversationId = Guid.NewGuid();

        await retrieval.RetrieveAsync(businessId, conversationId, "first question");
        await retrieval.RetrieveAsync(businessId, conversationId, "second question");

        var records = _capture.ForBusiness(businessId);
        Assert.Equal(2, records.Count);
        Assert.NotEqual(records[0].CorrelationId, records[1].CorrelationId);
    }

    // T-V3d. Mutation: catch-and-swallow the Voyage failure before the
    // invoker is reached (0 records).
    [Fact]
    public async Task RetrieveAsync_VoyageEmbeddingException_StillReturnsEmpty_RecordsOneFailedRow()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var retrieval = scope.ServiceProvider.GetRequiredService<IKnowledgeRetrievalService>();
        var businessId = await CreateBusinessAsync(db);

        _fakeVoyage.ExceptionToThrow = new VoyageEmbeddingException("simulated Voyage outage");
        try
        {
            var results = await retrieval.RetrieveAsync(businessId, Guid.NewGuid(), "anything");
            Assert.Empty(results);
        }
        finally
        {
            _fakeVoyage.ExceptionToThrow = null;
        }

        var record = Assert.Single(_capture.ForBusiness(businessId));
        Assert.Equal(AiOperations.KnowledgeRetrievalQuery, record.Operation);
        Assert.Equal(AiOutcomes.Failed, record.Outcome);
    }

    private class FixedTenantAccessor(Guid? businessId) : ICurrentTenantAccessor
    {
        public Guid? BusinessId => businessId;
    }
}
