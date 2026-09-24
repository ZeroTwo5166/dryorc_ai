namespace backend.Services;

/// <summary>
/// Isolated audit writer for A1 design v11 §5, rule (c) ONLY — the
/// <c>UnauthorizedActionBlocked</c> event written when a
/// <c>conversation.BusinessId != businessId</c> mismatch is detected. Not a
/// general-purpose isolated audit writer; a future second isolated-audit use
/// case gets its own type or an explicit, deliberate widening of this one,
/// never a silent reuse.
///
/// Persists independently of the caller's own unit of work (same "Isolation"
/// requirement as <see cref="IAiInvocationRecorder"/>, v11 §5), so the
/// caller's subsequent throw (v11 §5, rule (c), step 3 — out of this slice's
/// scope) cannot roll this row back. Never throws — the caller's own throw is
/// what blocks the provider call; this writer must never mask or replace it.
/// </summary>
public interface IUnauthorizedActionAuditWriter
{
    Task WriteConversationBusinessMismatchBlockedAsync(Guid businessId);
}
