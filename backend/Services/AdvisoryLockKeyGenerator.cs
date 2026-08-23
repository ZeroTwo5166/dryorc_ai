using System.Security.Cryptography;
using System.Text;

namespace backend.Services;

/// <summary>
/// Derives a deterministic bigint key for pg_advisory_xact_lock from the
/// (BusinessId, WidgetId, VisitorSessionId) tuple that identifies "this
/// visitor's in-flight first message" — the scope that must be
/// serialized to make lazy conversation creation race-free.
///
/// Deliberately SHA-256-based, not string.GetHashCode()/Guid.GetHashCode():
/// both of those are explicitly documented by .NET as unstable across
/// process runs (GetHashCode's randomization seed differs per process) and
/// are never guaranteed to agree across separate application instances —
/// unusable for a lock key that different app instances (or the same
/// instance across a restart) must derive identically for two concurrent
/// requests to actually contend on the same lock. SHA-256 of a fixed
/// canonical string has neither problem.
///
/// The 8 hash bytes are packed into the long via explicit big-endian
/// shifting, not BitConverter.ToInt64 — BitConverter's byte order is
/// platform-endianness-dependent. All realistic .NET 10 deployment targets
/// (x64/ARM64) are little-endian in practice, so this is not currently a
/// real risk, but explicit shifting removes the dependency entirely rather
/// than relying on that being true forever.
/// </summary>
public static class AdvisoryLockKeyGenerator
{
    public static long ForVisitorSession(Guid businessId, Guid widgetId, string visitorSessionId) =>
        PackToLong($"{businessId:D}:{widgetId:D}:{visitorSessionId}");

    /// <summary>
    /// Scopes a lock to one KnowledgeDocument's chunk-replacement step —
    /// serializes two concurrent content-changing updates to the same
    /// document so their delete-existing/insert-new chunk writes can never
    /// interleave (which would otherwise risk a raw unique-constraint
    /// violation on (KnowledgeDocumentId, ChunkIndex), or a chunk set left
    /// mixing rows from two different updates).
    /// </summary>
    public static long ForKnowledgeDocument(Guid businessId, Guid knowledgeDocumentId) =>
        PackToLong($"{businessId:D}:{knowledgeDocumentId:D}");

    private static long PackToLong(string canonical)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));

        long key = 0;
        for (var i = 0; i < 8; i++)
        {
            key = (key << 8) | hash[i];
        }

        return key;
    }
}
