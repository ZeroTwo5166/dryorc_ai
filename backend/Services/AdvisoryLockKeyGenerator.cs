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

    /// <summary>
    /// Scopes a lock to one conversation's CustomerReplied dedup
    /// check-then-create — serializes two concurrent
    /// NotifyStaffOfCustomerReplyAsync calls for the same conversation so
    /// the "at most one unread CustomerReplied notification per
    /// conversation" invariant holds even when two customer messages land
    /// close enough together that their notification side effects would
    /// otherwise run concurrently (they no longer share the visitor-session
    /// lock by that point — see WidgetConversationService's own doc
    /// comment).
    ///
    /// Step 15 broadened this same key's scope: it now also guards every
    /// write to Conversation.HandledBy and its associated
    /// CustomerReplied-notification side effects (the Ai→Human write in
    /// ConversationsController.CreateMessage, and the Human→Ai reversal in
    /// ConversationsController.ReturnToAi), not just notification creation
    /// in isolation. HandledBy became bidirectional in Step 15 — before
    /// that, a plain unlocked read-modify-write was accepted as benign
    /// because the value only ever moved one direction within a short
    /// window; bidirectionality means an unlocked writer could otherwise
    /// race a concurrent reversal into a genuinely ambiguous, non-serialized
    /// outcome (not just a stale-but-consistent read). One shared key for
    /// all of this is deliberate: HandledBy state and CustomerReplied
    /// notification state are one atomic decision boundary per conversation
    /// now, not two independently-timed ones.
    /// </summary>
    public static long ForConversationNotification(Guid businessId, Guid conversationId) =>
        PackToLong($"{businessId:D}:notification:{conversationId:D}");

    /// <summary>
    /// Step 21 (OTP half). Scopes a lock to one (BusinessId, CustomerId,
    /// ConversationId, Channel) verification scope's atomic
    /// supersede-then-insert step — VerificationCode's own doc comment
    /// mandates this so two concurrent resend requests for the same scope
    /// can never leave two simultaneously active codes. A resend for one
    /// customer/conversation/channel scope must never affect an active code
    /// in a different scope, including a different CustomerId within the
    /// same conversation (e.g. two different emails attempted in sequence).
    /// </summary>
    public static long ForVerificationCode(Guid businessId, Guid customerId, Guid conversationId, string channel) =>
        PackToLong($"{businessId:D}:verification:{customerId:D}:{conversationId:D}:{channel}");

    /// <summary>
    /// Scopes a lock to one (BusinessId, normalized email) pair's
    /// zero-match Customer auto-create decision in
    /// VerificationCodeService.ResolveCustomerAsync — serializes two
    /// concurrent RequestCodeAsync calls for the same not-yet-existing
    /// email so at most one Customer row is ever created for it. This is
    /// deliberately a lock, not a unique-index/DB-constraint arbiter: a
    /// (BusinessId, Email) unique index would make it impossible to ever
    /// have more than one Customer sharing an email within a business —
    /// but that is explicitly legal, intended schema state (a shared
    /// family inbox, a role account), which is exactly what
    /// CustomerMatchOutcome.Ambiguous exists to handle safely. This lock
    /// only closes the race between two concurrent auto-creates; it does
    /// not, and must not, change what counts as legal Customer data.
    /// normalizedEmail must already be the same normalized form
    /// ResolveCustomerAsync compares/stores (trim+lowercase) — two
    /// differently-cased inputs for the same address must derive the same
    /// key, matching NormalizeEmail's own case-insensitivity.
    /// </summary>
    public static long ForCustomerEmailResolution(Guid businessId, string normalizedEmail) =>
        PackToLong($"{businessId:D}:customer-email:{normalizedEmail}");

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
