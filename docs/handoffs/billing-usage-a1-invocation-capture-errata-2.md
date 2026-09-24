# Step 27 Slice A1: errata 2 — Slice 2 post-design owner decisions

Addendum to `docs/handoffs/billing-usage-a1-invocation-capture-design-v11.md`, as amended by errata 1 (commit `506759c`, applied inline to v11 with a closing "Errata (post-Slice 1)" index). Unlike errata 1, this addendum is a standalone file and does not edit v11's text. Where this file and v11 differ, this file governs for the items below. v11 governs everything else.

Each item is marked with its source:
- **(v11)**: stated by v11 itself.
- **(post-v11 owner decision, Slice 2)**: decided during Slice 2. v11 does not state it.
- **(derived)**: follows from a pre-existing codebase contract, not from v11 or a new decision.

Date: 2026-09-25.

---

## 1. Slice plan

v11 has no "Slice 2" section by that name. v11 has no slice plan at all.

The 6-slice breakdown below is a post-v11 breakdown, agreed in the advisory chat. It is not sourced from v11's own text. **(post-v11 owner decision.)**

1. Schema (`AiProviderInvocation` entity, migration, constraints). Shipped: `5cf4f8f`, `b37f70e`, merged in `2bf7792`.
2. Isolated recorder (`IAiInvocationRecorder`) and isolated rule (c) audit writer (`IUnauthorizedActionAuditWriter`).
3. Provider wrappers.
4. Call-site migration, in two parts.
5. Architecture test (v11 §8 test 7) and DI lockdown.
6. Retention job (v11 §7, §8 test 11).

From this point forward, **this file is the source of record for the slice plan**. Code comments and later design documents that refer to "Slice N" cite this file, not v11.

---

## 2. D1: internal write timeout

**(post-v11 owner decision, Slice 2)**

- **Value:** 3 seconds, held in `IsolatedWriteLimits.WriteTimeout` (`backend/Constants/IsolatedWriteLimits.cs`).
- **Rationale:** v11 §5's "Cancellation" bullet requires "its own short timeout" but gives no value. v11 §5 "Latency" makes the recorder's write an awaited insert on the customer-turn path. The timeout is therefore a customer-facing latency ceiling, not only a durability mechanism. 3 s was chosen over the original 5 s placeholder on that basis.
- **Scope:** the countdown starts when each writer creates its `CancellationTokenSource`. It covers the isolated `SaveChangesAsync` call, including connection open, any lock wait on the insert, and the insert itself.
- **Not covered:** DI scope creation, service resolution, scope disposal, and Npgsql's own work after cancellation (the cancel request and its `CancellationTimeout`). It is **not** an end-to-end guarantee on a writer's total duration.
- **Single constant:** there is exactly one timeout constant, shared by `AiInvocationRecorder` and `UnauthorizedActionAuditWriter` (see D2). Neither class holds any other timeout value.

---

## 3. D2: audit writer timeout

**(post-v11 owner decision, Slice 2)**

- The rule (c) audit writer (`UnauthorizedActionAuditWriter`) uses the same `IsolatedWriteLimits.WriteTimeout` as the recorder.
- **Why a decision was needed:** v11 §5's "Cancellation" bullet is scoped to "the write", meaning the usage write. Rule (c) step 2 cross-references only the "Isolation" requirement ("using the same isolation requirement already specified above for the usage write"), not "Cancellation". v11 therefore does not say whether the audit writer needs a timeout.
- **Decision:** it does. The caller throws only after the writer returns (rule (c) step 3). An unbounded writer would mean an unbounded delay before the block takes effect.

---

## 4. D3: audit writer failure policy

**(post-v11 owner decision, Slice 2)**

- **Fail open.** On any failure, `UnauthorizedActionAuditWriter` logs and returns without throwing. It never masks or replaces the caller's own throw (rule (c) step 3).
- **Rationale:** rule (c) step 3 blocks the action whether or not the audit row persisted. Failing closed would add nothing to enforcement. It would only replace the caller's intended exception with a database exception.
- **Dedicated alertable event.** A lost audit row would otherwise go unnoticed, so every failure path emits exactly one dedicated log event instead of a generic error log:
  - `EventId` 27001, name `UnauthorizedActionAuditWriteFailed` (`UnauthorizedActionAuditWriter.WriteFailedEventId`).
  - Error level.
  - Exactly 4 structured fields:
    - `BusinessId`
    - `EventType` (`UnauthorizedActionBlocked`)
    - `NewValue` (the marker `"ConversationBusinessMismatch"`)
    - `ExceptionType` (the exception's simple type name)
  - No exception object and no other content. The message template is fixed.
- **Differs from Decision 1, deliberately.** The recorder's Decision 1 diagnostic *does* attach the exception object. The writer's D3 event does not. This asymmetry is intended (see item 6) and is not an inconsistency to fix.

---

## 5. Rule (c) row fields v11 does not specify

**(derived)**

v11 §5 rule (c) step 1 lists `EventType`, `EntityType`, `EntityId`, `BusinessId`, `ConversationId`, `Actor`, `OldValue` and `NewValue`. It does not mention two other `AuditLog` fields. Both are left null:

- **`CorrelationId` = null.** This is **not** derived from v11 §4's `AiCallContext`. That `CorrelationId` is `AiProviderInvocation`'s concept, not `AuditLog`'s. It follows from `AuditLog.CorrelationId`'s own documented contract (`backend/Models/AuditLog.cs`): "Links multiple AuditLog rows belonging to the same logical operation … Null for standalone events." The rule (c) event is standalone.
- **`ChangedByUserId` = null.** Its contract (`backend/Models/AuditLog.cs`) is "Populated only when Actor == NivoraBusinessStaff". The rule (c) actor is `ActorType.Customer`.

v11 states neither value. Both follow from `AuditLog`'s existing field contracts applied to rule (c). Slice 2's row-shape test asserts both are null.

---

## 6. Recorder vs. writer diagnostic asymmetry

**(post-v11 owner decision, Slice 2; accepted, not a defect)**

- The recorder's Decision 1 diagnostic attaches the exception object, so the log gets its message and stack. The writer's D3 event logs only the exception's simple type name.
- **Why this is intended:**
  - Decision 1 requires "structured, non-content diagnostics" and no prompt/response content or PII. It does not forbid extra context, and `AiProviderInvocation` contains identifiers, counts, and provider/model/operation metadata, and no prompt/response content.
  - The writer's event is capped at exactly 4 fields because it is meant to be a stable, alertable signal, not a diagnostic dump.
- **Standing configuration dependency (not enforced by any test in this slice).** The asymmetry is safe only while both of these stay **off** in production:
  - Npgsql's `Include Error Detail`.
  - EF Core's sensitive-data logging (`EnableSensitiveDataLogging`).

  Either one would put parameter values inside the logged exception's message. As of 2026-09-25, `backend/` contains no `IncludeErrorDetail`, `EnableSensitiveDataLogging` or `EnableDetailedErrors` setting, and `PostgresConnectionString.FromUri` sets neither, so both are off by default. **Flagged for Step 29 — Security hardening (Phase 9 — SaaS / Production Hardening, `NIVORA_FULL_PLAN_CHECKLIST_V2.md`) to confirm explicitly**, preferably with an enforcing test or a startup guard.

---

## 7. Boundary of the recorder's "no database read" test

**(derived: a statement of test scope, not a decision)**

v11 §5 "Tenant boundary" says: "The recorder performs no ownership comparison and no database read of its own." `AiInvocationRecorderTests.RecordAsync_ExecutesOnlyItsInsert_NoDatabaseRead` enforces that as **"no read observed through EF Core's command interception"**:

- It captures every command EF executes through the recorder's isolated context: reader, non-query and scalar, sync and async.
- It **cannot** detect a raw ADO.NET read that bypasses EF, such as a command run on `db.Database.GetDbConnection()` or on a separately opened `NpgsqlConnection`. Independent verification confirmed this by mutation: such a read passed all 6 recorder tests.
- Source review found no such usage in the current `AiInvocationRecorder`.

Record this as the test's actual boundary for anyone who extends the recorder or relies on the test later. The test's inline comment originally read "every command the recorder's isolated context executes", which stated more than the test checks; it was reworded during this errata's preparation to "every command observed through EF Core's command interception for the recorder's isolated context" (`AiInvocationRecorderTests.cs:233-235`) to match the class-level comment's accuracy. The class-level comment ("every command EF executes through this context") is accurate.

---

## 8. Verification trail

Recorded for the audit trail:

1. **Implementation session:** 6 files.
2. **Correction pass 1:** 9 changes.
3. **Fresh-session verification 1:** PASS WITH NONBLOCKING FINDINGS. Found the D2 timeout coverage gap.
4. **Correction pass 2:** writer timeout test added.
5. **Fresh-session verification 2:** self-check only, not independent.
6. **Correction pass 3:** F1–F6 fixed. F7 left as is. F8 investigated and found to need different wording than proposed.
7. **Independent fresh-session verification of the final state:** PASS WITH NONBLOCKING FINDINGS. No code change required. The only finding is the test boundary in item 7. Full suite 1310 passed / 0 failed / 3 skipped. Schema: no pending model changes. All mutations killed except the raw ADO.NET read in item 7.

**Final state:** the 7 files below were committed in the Slice 2 feature commit that follows this one on `worktree-a1-slice2-isolated-writers`, parent `2bf7792`:

- `backend/Constants/IsolatedWriteLimits.cs`
- `backend/Services/IAiInvocationRecorder.cs`
- `backend/Services/AiInvocationRecorder.cs`
- `backend/Services/IUnauthorizedActionAuditWriter.cs`
- `backend/Services/UnauthorizedActionAuditWriter.cs`
- `backend.Tests/AiInvocationRecorderTests.cs`
- `backend.Tests/UnauthorizedActionAuditWriterTests.cs`
