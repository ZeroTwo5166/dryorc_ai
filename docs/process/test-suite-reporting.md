# Test-suite reporting — standing rule

Added after the step-45 independent verification found a discrepancy between the implementing session's reported full-suite figures and what a clean re-run actually produced.

## Rule

**Full-suite pass/fail counts must not be reported as a single-run figure without flagging known-flaky areas.** A single `dotnet test` run in this repo can look clean by chance even when a known-flaky test class is present — report the run honestly, and name the flake class if it's the kind of area that's failed before, whether or not it failed on this particular run.

## Known flake: `KnowledgeRetrievalServiceTests`' HNSW-recall tests

Tests that assert exact top-K recall against the real Postgres/pgvector HNSW index (e.g. `RetrieveAsync_HnswTenantFilterRecall_...`) are approximate-search-sensitive and have intermittently failed across multiple sessions, independent of any change to `KnowledgeRetrievalService` itself. History:

- `docs/handoffs/step-35-hnsw-recall-test-triage.md` — original triage.
- `docs/handoffs/step-36-hnsw-recall-test-flake-fix.md` — implemented a mitigation (serializing the test classes that write into the shared `KnowledgeChunks` table) and explicitly disclaims it as a guarantee: *"this fix does not, and cannot, guarantee zero future flakiness... if a different mechanism exists (e.g., genuine HNSW approximation variance even under fully serialized execution)... it would not be addressed by this change."*

Confirmed still live as of the step-45 independent verification (2026-09-06-ish session): two separate full-suite runs each produced one failure in this test class (different specific test each time), while the isolated single-test re-run and the target class under verification (`OrderCancelApprovedActionExecutorTests`) were clean on every run.

**Do not re-attempt fixing this flake as a side effect of unrelated work.** Step-36 already tried the identified mechanism; a further attempt needs its own scoped design/triage pass, not a drive-by fix bundled into whatever the current task happens to be.

## What to actually report

When reporting full-suite results (in a handoff doc, a commit message, or a verification report):

- State the actual run's numbers plainly (passed/failed/skipped/total).
- If a failure occurred in `KnowledgeRetrievalServiceTests`' HNSW-recall-sensitive tests, name it as the known flake and link to step-35/step-36 rather than treating it as a new regression.
- If the run was clean, don't imply that proves the flake is gone — a clean run is expected most of the time even with the flake present. Re-running once or twice to distinguish "this specific change broke something" from "the known flake fired" is reasonable; don't stop at a single ambiguous run when a discrepancy is suspected.
- If a *different* test fails, or the same test fails in a way inconsistent with approximate-recall variance (e.g., a deterministic assertion on non-vector logic), treat it as a real regression, not the known flake — don't reflexively attribute every red run to this note.
