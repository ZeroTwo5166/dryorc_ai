using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace backend.Tests;

/// <summary>
/// Step 22 Phase 3 — a genuine, non-invasive way to hold one writer's
/// transaction open under direct test control, without needing access to
/// that writer's own internal mutator delegate (unlike the Phase 2
/// concurrency test, which paused inside a test-supplied mutator lambda —
/// not available here, since ConversationClassificationService.ClassifyAsync
/// and ConversationTagCorrectionService.CorrectTagsAsync each own their own
/// internal mutator, not exposed to callers). Fires immediately before EF
/// Core actually commits a transaction on the attached DbContext —
/// regardless of which service or method opened it — so a test can await
/// an arbitrary signal there before letting the commit (and the advisory
/// lock release it implies) proceed. Zero production code changes; wired
/// in only via a test-specific DbContextOptions.
/// </summary>
public class TransactionCommitGateInterceptor : DbTransactionInterceptor
{
    public Func<Task>? BeforeCommit { get; set; }

    public override async ValueTask<InterceptionResult> TransactionCommittingAsync(
        DbTransaction transaction, TransactionEventData eventData, InterceptionResult result,
        CancellationToken cancellationToken = default)
    {
        if (BeforeCommit is not null)
        {
            await BeforeCommit();
        }

        return await base.TransactionCommittingAsync(transaction, eventData, result, cancellationToken);
    }
}
