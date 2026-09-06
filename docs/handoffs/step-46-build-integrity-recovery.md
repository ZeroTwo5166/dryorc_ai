# Step 46 — Build-Integrity Recovery

**PROCESS HISTORY, NOT A DESIGN DOC.** This documents a git-tracking gap found during the step-45-era checklist reconciliation, the verification performed before committing anything, and the fix. Kept factual and short.

## What was found

The step-45 reconciliation pass (previous session) discovered that a clean `dotnet build` against `HEAD` failed: `backend/Services/OrderCancelApprovedActionExecutor.cs` (committed) referenced `Approval`, which did not exist in any committed file. Confirmed via `git worktree add` + `dotnet build` — `CS0246: The type or namespace name 'Approval' could not be found`.

Nine files were confirmed via `git log --all -- <path>` to have never been committed on any branch:
`backend/Models/Approval.cs`, `backend/Enums/ApprovalStatus.cs`, `backend/Services/ActionHasher.cs`, `backend/Services/ApprovalExecutionKeyDeriver.cs`, `backend/Services/IActionProposalTool.cs`, `backend/Services/OrderCancelProposalTool.cs`, `backend/Services/GetCustomerTool.cs`, and the `AddApprovalTable`/`AddOrderCancellationPendingGuard` migrations (4 files including `.Designer.cs` pairs).

This pass additionally found, while tracing what a clean build actually needed, that the gap was larger than those nine files: `DuplicatePendingOrderCancellationException` was caught by name in already-committed `OrderCancellationService.cs` but not defined in the committed `IOrderCancellationService.cs`; the committed `ToolArgumentCanonicalizer.cs` had a stale `CanonicalizeOrderCancellationArgs` signature (3-arg, boolean `refund`) incompatible with what committed callers (`OrderCancelApprovedActionExecutor.cs`) actually needed, and was missing `DecodeOrderCancellationArgs` entirely; and the `ClaudeReplyService`/`Program.cs`/`ToolDispatchService.cs` wiring that makes the tool-dispatch loop live (rather than the dormant infrastructure `f226cb2` originally shipped) was also uncommitted.

## Verification performed before committing anything

Per the explicit instruction not to bulk-assume all nine files were equally safe: each was read individually and checked for `TODO`/`HACK`/`WIP`/`scratch`/`experimental`/`draft`/`incomplete` markers (`grep`, case-insensitive) — zero hits across all nine. Each file's own doc comments were read in full and cross-referenced against its corresponding handoff document(s) (`step-22s` through `step-23t`, `step-27` through `step-34`, `step-23k`) — every one traces to an already-independently-verified design/implementation pass, several with their own dedicated independent-verification handoff doc. `GetCustomerTool.cs` and `OrderCancelProposalTool.cs` each explicitly flag their own deliberate scope limits in their doc comments (e.g. "this pass has no reason to build [a live Shopify query]... flagged explicitly, not silently mislabeled as the complete feature") — the kind of comment a finished, self-aware piece of work carries, not a sign of being unfinished.

Both migrations were confirmed already applied to the real Neon dev database via `dotnet ef migrations list` — `20260902200516_AddApprovalTable` and `20260903135104_AddOrderCancellationPendingGuard` both appear in `__EFMigrationsHistory`, in correct order, as the two most recent entries. The schema was already live; only the migration files were never committed.

`DuplicatePendingOrderCancellationException` was confirmed to already exist, fully implemented, in the working tree's uncommitted `IOrderCancellationService.cs` — colocated with the interface whose method throws it, matching this codebase's existing convention (e.g. `OrderCancellationResult` in the same file). Its catch site in `OrderCancellationService.cs` (`PendingGuardIndexName`) was confirmed to reference the exact index name the `AddOrderCancellationPendingGuard` migration creates. Nothing needed to be written from scratch — only committed.

**Conclusion: all of it was genuinely finished, verified, already-applied-where-applicable work — a process failure, not deliberately-withheld experimental code.**

## Likely root cause

Best-effort inference, not confirmed by interviewing prior sessions: each implementation session across the Step 22–23 safety-model arc appears to have staged and committed only the files most directly relevant to that session's own specific headline deliverable (e.g. `b05d832`'s own message: *"this is the first commit of `backend/Services/IApprovedActionExecutor.cs` (previously untracked)"* — committing that one file without also checking whether ITS dependencies were tracked). Several individual pieces (`Approval` persistence, `ActionHasher`, `ApprovalExecutionKeyDeriver`, the propose-side tool, `GetCustomerTool`) were each implemented and independently verified in their own dedicated session/handoff, but no session's own commit step ever appears to have done a full `git status` sanity check against every untracked file before committing — only checked that its own new work landed. This is a longstanding pattern across the whole arc, not one missed moment.

## What was committed to fix it

Six commits, landing now as their own history entries (not squashed into any earlier step's commit), in this order:

| Commit | Contents |
|---|---|
| `7d9e630` | `Approval.cs` + `ApprovalStatus.cs` (Step 22X domain model) |
| `30f5479` | Both migrations + `AppDbContext.cs` + `AppDbContextModelSnapshot.cs` (bundled — `AppDbContext.cs`'s diff mixes Approval-entity and pending-guard-index configuration and cannot be cleanly split without interactive per-hunk staging) |
| `2b6de84` | `ActionHasher.cs` + `ApprovalExecutionKeyDeriver.cs` + their tests (Steps 23D/23F) |
| `4d6c69e` | `DuplicatePendingOrderCancellationException` in `IOrderCancellationService.cs` + its catch site in `OrderCancellationService.cs` + tests (Step 23S) |
| `8079e3c` | `IActionProposalTool.cs` + `OrderCancelProposalTool.cs` + `ToolDispatchService.cs`'s dispatch case + `ToolArgumentCanonicalizer.cs` (bundled — the propose tool and its tests depend on the corrected signature) + tests (Steps 23H/23J/23N/27-34) |
| `712500b` | `GetCustomerTool.cs` + `ClaudeReplyService.cs`/`Program.cs` wiring that makes the dispatch loop live + tests (Step 23K) |

**Deliberately left uncommitted, out of this pass's scope**: `backend.Tests/KnowledgeChunksCollection.cs` (untracked) and small diffs in `backend.Tests/KnowledgeDocumentsApiTests.cs`/`KnowledgeIndexingServiceTests.cs`/`KnowledgeRetrievalServiceTests.cs`/`backend/Controllers/KnowledgeDocumentsController.cs`/`TagModeMappingsController.cs`/`TicketsController.cs`/`backend/Data/PostgresConnectionString.cs` — confirmed unrelated to the Safety Model build-integrity gap (an `Enum.IsDefined` hardening track, `step-31`-`step-34`, and separately an HNSW-recall-flake test-collection fixture) and not required for `HEAD` to build. Still sitting in the working tree, still not this pass's job to commit.

## Clean-build confirmation

Repeated the exact method that found the original problem — `git worktree add --detach <path> HEAD` (a genuinely separate checkout, not the working tree) — against the new `HEAD` (`712500b`):

- `dotnet build backend/backend.csproj` — **Build succeeded, 0 errors.**
- `dotnet build backend.Tests/backend.Tests.csproj` — **Build succeeded, 0 errors.**
- `dotnet test backend.Tests/backend.Tests.csproj --no-build` (real numbers, not assumed): **674 passed, 3 skipped, 0 failed, 677 total.** The 3 skips are the pre-existing, gated live-Claude-API tests. No HNSW-recall-flake failure on this run (see `docs/process/test-suite-reporting.md` — a clean run doesn't prove the flake is gone, it just didn't fire this time).

`HEAD` is confirmed buildable and testable from committed history alone.
