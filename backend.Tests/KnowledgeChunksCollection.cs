using Xunit;

namespace backend.Tests;

/// <summary>
/// Step 36 — groups every test class that writes real embedding vectors
/// into the shared KnowledgeChunks table (and its one HNSW index) so xUnit
/// runs them sequentially rather than in parallel with each other.
///
/// Step 36 added it on the reasoning of
/// docs/handoffs/step-35-hnsw-recall-test-triage.md: that concurrent writes
/// against that shared index made KnowledgeRetrievalServiceTests' own
/// strict-recall test, RetrieveAsync_HnswTenantFilterRecall_..., fail
/// intermittently. That reason is not the cause. The later diagnosis
/// reproduced the same failure with nothing running beside it, five times
/// of ten (docs/handoffs/hnsw-tenant-recall-design-v3.md, "From Part 1.5,
/// beyond the one test"), and the failure was addressed in
/// KnowledgeRetrievalService (iterative HNSW scan), not by this collection.
/// The collection itself is unchanged; whether it is still wanted is a
/// separate question that has not been decided.
///
/// No shared fixture object is declared (no ICollectionFixture&lt;T&gt;) —
/// each member class already manages its own ServiceProvider/AppDbContext
/// lifecycle independently via its own IAsyncLifetime, and none of them
/// need or want state shared across the group. This definition exists
/// purely to force xUnit's collection-level serialization; a bare
/// [CollectionDefinition] with no fixture interface is sufficient for that
/// on its own — confirmed no existing [Collection]/[CollectionDefinition]
/// usage exists anywhere else in this test project to instead extend.
///
/// Membership (re-confirmed directly against source, not assumed from any
/// prior handoff's list, before this collection was created): every class
/// under backend.Tests/ that calls db.KnowledgeChunks.Add(...) —
/// ClaudeReplyServiceTests, KnowledgeDocumentsApiTests,
/// KnowledgeIndexingServiceTests, KnowledgeRetrievalServiceTests. Add any
/// future test class that writes to KnowledgeChunks to this same
/// collection.
/// </summary>
[CollectionDefinition("KnowledgeChunks collection")]
public class KnowledgeChunksCollection
{
}
