# GDPR Retention/Anonymization Lifecycle — Implementation Plan

**Status: PHASING ONLY. NOT IMPLEMENTATION.** This document sequences the locked design at `docs/handoffs/gdpr-lifecycle-design.md` (Status: LOCKED, 2026-09-16) into independently implementable, independently testable, independently committable phases, following the Step 24/25 process (adversarial verification → commit → fresh-worktree re-verification → next phase; checklist reconciliation as its own final commit). It does not redesign anything the locked design settled. No production source, migration, test, configuration, or DI registration was created or modified to produce this document.

The locked technical design baseline this plan implements, unchanged: Decisions 1–5, resolved §7 items (1, 2, 5, 6, 8), confirmed-no-conflict §7 items (3, 4, 7). Decision 6 (numeric retention periods) remains explicitly out of scope — nothing below designs, assumes, or depends on it. Decision 1's legal-sufficiency sign-off and Decision 2's Art. 28 documented-instruction gap remain open, non-blocking, external items — unaffected by this plan.

---

## 0. Repository verification performed before phasing

Every implementation assumption the design's pseudocode depends on was re-checked directly against the current repository (not re-derived from the design document's own prior citations). Full detail in the accompanying report; summary here.

**Confirmed to match the design's "current structure" description exactly, line-for-line, including cited line numbers:**
- `ConversationPurgeService.PurgeConversationAsync` (`backend/Services/ConversationPurgeService.cs:47-105`) — still unconditionally opens its own transaction, exactly as §1.1 describes.
- `RetentionEnforcementService.EnforceRetentionAsync` (`backend/Services/RetentionEnforcementService.cs:34-66`) — still a plain discovery-then-`foreach` loop, zero claim mechanism, exactly as §2.1 describes.
- `CustomerErasureService.EraseCustomerAsync` (`backend/Services/CustomerErasureService.cs:38-97`) — still the three-phase structure §3.1 describes (own-transaction identity deletes → untransacted per-conversation purge loop → terminal `Customer` mutation).
- `CustomersController`'s `LinkIdentity`/`RevokeIdentity`/`SetExtendedLogging` patterns, `CurrentUserId`, and `NivoraPolicies.OwnerOrAdmin`/`SameTenant`/`AnyStaff` — all exist and match §6's cited precedent exactly.
- `AuditLogService.LogAsync`'s signature — already has `correlationId` (nullable `Guid?`), confirming §3.2's claim that the `AuditLog.CorrelationId` mechanism already exists and needs no new plumbing.
- `AuditEventTypes.cs`, `AuditEntityTypes.cs`, `ApprovalStatus.cs`, `Approval.cs` — all match; `AuditEventTypes.CustomerDeleted` confirmed to have zero usages anywhere in the codebase outside its own declaration (grepped directly), matching §4's claim.
- `DataLifecycleBackgroundService` — confirmed one shared `AsyncServiceScope` per tick (line 55), all four jobs run sequentially behind `RunJobAsync`'s own error boundary, `RetentionEnforcement` already resolved and called (lines 57-59) — matches §1.4/§7 item 5 exactly.
- `backend.Tests/CustomerDataLifecycleTests.cs` — exactly **5** call sites for `erasureService.EraseCustomerAsync(...)`, confirmed by direct read: `EraseCustomer_AnonymizesCustomer_DeletesIdentityRows_PurgesConversations` (:372), `EraseCustomer_UnknownCustomerId_IsSafeNoOp` (:408), `EraseCustomer_NeverModifiesAnotherBusinesssData` (:552), `EraseCustomer_RemovesCustomerExtendedLoggingHistory` (:613), `EraseCustomer_RemovesCustomersVerificationCodes` (:645) — matches §3.5's claim exactly.
- `VerificationCodeService.VerifyCodeAsync`'s unconditional `Conversation.CustomerId` overwrite (`VerificationCodeService.cs:244-249`) — confirmed byte-for-byte against §7 item 1's quoted snippet.
- `AppDbContext.cs:911-932`'s `Approval` entity config — `ConversationId` FK confirmed `Cascade` at exactly lines 930-932, matching the design's own citation.
- `ApprovalsController.Approve`/`Deny` — confirmed guarded `ExecuteUpdateAsync` + `409 Conflict` on 0 affected rows, matching §7 item 8's claim about existing concurrent-mutation behavior.
- `ConversationInactivityCloseService` — confirmed it never touches `Message`/`Notification`/`Approval`/`ConversationPurgeService` at all, matching §1.4's "zero effect on auto-close" claim.
- `FinancialRecordRetentionPolicy.cs` — confirmed its doc comment currently names only `OrderCancellation`, `CustomerUpdateAction`, `Refund` — **not yet** `Approval`, confirming Decision 4's own statement that this update is still outstanding.

**No contradiction and no newly discovered edge case/gap were found.** Every structural assumption the locked design's pseudocode depends on holds against the current repository as of this pass. Per the task's taxonomy, this means every piece of the locked design remains directly implementable as specified — nothing below assumes an unresolved outcome.

**One implementation-preference-level correction, not a contradiction or edge case** (documented here so the redesign's own justification text isn't miscopied verbatim into shipped code comments): §3.4's proposed guard —
```csharp
if (customer is null || customer.IsAnonymized) { return; }
```
— is annotated in the design as "matches today's existing guard, moved to the top." That is not quite accurate against the current source: today's actual top-of-method guard is `if (customer is null) { return; }` **only**; the `IsAnonymized` check exists separately, later, gating only Phase 3's mutation (`if (!customer.IsAnonymized) { ...anonymize...; await SaveChangesAsync(); }`). Today, calling `EraseCustomerAsync` on an already-anonymized customer still executes Phase 1's four idempotent deletes and Phase 2's full conversation-purge loop before silently skipping Phase 3 — it does not return early. The redesign's combined top-of-method guard is therefore a genuine, beneficial strengthening (a true early-return no-op, avoiding pointless work on an already-erased customer), not a relocation of already-existing behavior. This changes no phase's scope or testing — Phase 4 below implements the guard exactly as §3.4 specifies — but the phase's own inline comment should describe it accurately (a new, stricter idempotency guard) rather than reusing the design doc's "matches today's existing guard" phrasing.

---

## 1. Phase breakdown

| # | Phase | One-line purpose |
|---|---|---|
| 1 | `AuditEventTypes` constants | `CustomerErased`, `ConversationRetentionPurged`, `ConversationErasurePurged` — added first because Phases 3 and 4 both write `AuditLog` rows using them |
| 2 | `ConversationPurgeService` transaction-joining + `Approval` cleanup | §1 Option B (join-or-own) + §5's Decision 4 `Approval` delete, placed together since both touch the same method and both are prerequisites for Phases 3/4's atomic per-conversation transactions |
| 3 | `RetentionEnforcementService` atomic-claim redesign | §2's Decision 3 Ticket-exemption race fix + Decision 5 retention audit, composed on top of Phase 2's join behavior |
| 4 | `CustomerErasureService` Mechanism 2 redesign | §3's per-conversation transaction composition, `changedByUserId`, Decision 5's GDPR-erasure audit atomicity — the design's most safety-critical phase |
| 5 | `CustomersController` erasure endpoint | §6 — `POST /api/customers/{customerId:guid}/erase`, the only phase that makes `CustomerErasureService` reachable from a real request |
| 6 | Final E2E verification + checklist reconciliation | Same rhythm as Step 24/25 §9/§10 — two separate final commits |

### Reordering from the task's hypothesized list, and why

The task's own hypothesis put the `AuditEventTypes` constants at position 4, after `ConversationPurgeService`/`RetentionEnforcementService`/`CustomerErasureService`. Repository inspection confirms this ordering is backwards: `ConversationPurgeService` itself never calls `IAuditLogService` (it has no reason/actor awareness at all, by design — Decision 5's settled boundary, §0's "reason-agnosticism" note) and so Phase 2 needs no new constants. But `RetentionEnforcementService`'s redesign (§2.3) writes `AuditEventTypes.ConversationRetentionPurged`, and `CustomerErasureService`'s redesign (§3.4) writes `AuditEventTypes.ConversationErasurePurged` and `AuditEventTypes.CustomerErased` — both **inside** the same phase that redesigns the calling method, not after it. Building either phase without the constants existing first would not compile. The constants are therefore a genuine dependency of Phases 3 and 4, not a followup to them — moved to Phase 1 accordingly, exactly as the task's own conditional instruction anticipated for this case.

Everything else keeps the task's hypothesized relative order: `ConversationPurgeService` (the shared, central mechanism both `RetentionEnforcementService` and `CustomerErasureService` depend on) before either of its callers; `RetentionEnforcementService` before `CustomerErasureService` (arbitrary among two mutually-independent phases, but this order lets the simpler single-loop retention redesign validate the claim-purge-audit transaction-composition pattern once before `CustomerErasureService` reuses the identical pattern inside its own, more complex three-phase structure with an added breaking signature change); the controller endpoint last among code phases, since it is the only caller of `CustomerErasureService`'s new signature; final E2E + checklist last, unchanged from the task's own suggested tail.

---

## 2. Per-phase scope

### Phase 1 — `AuditEventTypes` constants

**Included**: three new `const string` fields in `backend/Constants/AuditEventTypes.cs`, exactly as §4 specifies:
```csharp
// Customer
public const string CustomerErased = "CustomerErased";

// Conversation / ticket
public const string ConversationRetentionPurged = "ConversationRetentionPurged";
public const string ConversationErasurePurged = "ConversationErasurePurged";
```
Placed in the existing `// Customer` and `// Conversation / ticket` sections respectively, per §4's own placement instruction. No `AuditEntityTypes` change — `Customer`/`Conversation` already exist.

**Deferred**: every caller of these constants (Phases 3, 4).

**Why the boundary is safe**: pure additive constants, zero behavior change, zero call sites yet — the same "declared before consumed" shape this file's own history already shows (e.g. `IntegrationReauthorizationRequired`, `OrderLookupPerformed`, each added in their own design's constants step before their respective caller existed).

### Phase 2 — `ConversationPurgeService` transaction-joining + `Approval` cleanup

**Included**: §1 Option B's exact redesign of `PurgeConversationAsync` — checks `db.Database.CurrentTransaction`, joins if non-null (skips `BeginTransactionAsync`/`CommitAsync`, still calls `SaveChangesAsync`, never calls `RollbackAsync` itself), owns as today if null. Adds the Decision 4 `Approval` cleanup delete (`Status != ApprovalStatus.Consumed`, conversation-scoped), placed immediately after the `Notification` delete and before the final `Conversation` mutation, per §1.4's pseudocode and §5's placement instruction. Updates `FinancialRecordRetentionPolicy.cs`'s doc comment to add `Approval` (`Consumed` rows only) to the list of entities its Art. 17(3)(b) rationale covers, per Decision 4's explicit instruction that this must be an intentional update, not an inferred exception. Corrects `ConversationPurgeService`'s own stale pre-Ticket-existence doc comment (currently: "Ticket is deliberately untouched: as of this pass, no code anywhere creates or updates a Ticket... This needs revisiting once Ticket creation actually exists" — Tickets now exist; this comment must be replaced, clarifying that `ConversationPurgeService` itself still has no Ticket awareness by design, because the exemption lives entirely in `RetentionEnforcementService`/`ConversationInactivityCloseService` instead, per Decision 3's explicit rejection of a bypass-parameter approach).

**Deferred**: every caller composing its own transaction around this method (Phases 3, 4).

**Why the boundary is safe**: every existing standalone call site (all of `CustomerDataLifecycleTests.cs`, §1.5's own analysis) keeps its current, already-proven atomicity guarantee automatically — zero changes required for the "owns" branch, which is the only branch any currently-shipped caller exercises. The "joins" branch is new, dark code until Phase 3/4 wraps a transaction around it, but is directly unit-testable in isolation (this phase's own adversarial tests, §3.2 below, exercise it directly without needing Phase 3 or 4 to exist).

### Phase 3 — `RetentionEnforcementService` atomic-claim redesign

**Included**: §2.3's exact redesign — `EnforceRetentionAsync`'s discovery query gains the Ticket-narrowing `Where` clause (optimization only, per §2.2); the per-conversation loop is replaced by a new `TryPurgeConversationAsync(businessId, conversationId, cancellationToken)` helper that: opens its own transaction, re-reads `Business.RetentionDays` fresh, performs the atomic claim (`ExecuteUpdateAsync` touching only `UpdatedAt`, re-asserting every eligibility condition including the Ticket exemption), calls `purgeService.PurgeConversationAsync` (joins, per Phase 2), writes the `ConversationRetentionPurged` audit row (`ActorType.NivoraSystem`, `ChangedByUserId: null`), `SaveChangesAsync` + `CommitAsync` together. `TryPurgeConversationAsync` is made `internal` (not `private`), mirroring `ConversationInactivityCloseService.TryCloseConversationAsync`'s own established precedent for exactly this reason — this project's existing `InternalsVisibleTo` to `backend.Tests` lets the discovery/claim race be tested directly and deterministically (§3.2's Test A below) rather than only through a real, non-deterministic thread race.

**Deferred**: nothing — this job is **already live**, already registered, already called every 24 hours by `DataLifecycleBackgroundService` for every business with `RetentionDays` configured (unlike Step 25's `ConversationInactivityCloseService`, which shipped dark behind a disabled-by-default flag until its own wiring phase). **This is a go-live-on-merge phase, not a dark-ship phase** — flagged explicitly here so it isn't assumed to carry Step 25's "not yet reachable from any running process" safety argument. The redesign is safe to ship live because it can only ever purge a **strict subset** of what today's unguarded code would purge (adds a Ticket exemption that only prevents purges, never causes new ones) and strengthens, never weakens, the audit/atomicity guarantee (adds an audit row and a stronger commit-together guarantee where none existed before) — it cannot cause additional data loss relative to current production behavior, only less-aggressive purging plus new audit coverage.

### Phase 4 — `CustomerErasureService` Mechanism 2 redesign

**Included**: §3.4's exact redesign — `EraseCustomerAsync` gains a required `Guid changedByUserId` parameter; top-of-method guard becomes `if (customer is null || customer.IsAnonymized) { return; }` (a genuine strengthening, not a relocation — see §0 above); Phase 1 (identity-row deletes + `Approval` belt-and-braces delete) unchanged in shape, own small transaction; Phase 2 becomes a loop over a new `TryPurgeAndAuditConversationAsync` helper — one small transaction per conversation, atomic claim (touches only `UpdatedAt`) + joined purge (Phase 2 of this plan) + `ConversationErasurePurged` audit row (`ActorType.NivoraBusinessStaff`, `changedByUserId`, shared `erasureCorrelationId`) — all three committed together; Phase 3 becomes its own small transaction: terminal `Customer` mutation + `CustomerErased` completion audit row (same `erasureCorrelationId`). `ICustomerErasureService.EraseCustomerAsync`'s interface signature changes to match. `TryPurgeAndAuditConversationAsync` made `internal`, same reasoning as Phase 3's helper.

**Breaking change, in-phase**: all 5 existing call sites in `CustomerDataLifecycleTests.cs` (confirmed above, §0) must be updated in this same phase to pass a `changedByUserId` — reusing the file's own existing `CreateStaffUserAsync` fixture helper (already present, already used elsewhere in the same file) rather than introducing a new one. This cannot be deferred to Phase 5 or 6 — the build does not compile otherwise.

**Deferred**: the HTTP endpoint (Phase 5) — `CustomerErasureService` remains reachable only from tests until then, unchanged from today.

**Why the boundary is safe**: `ICustomerErasureService` has no production caller yet (confirmed, §0 — "no controller calls these services yet" is still true), so this phase's behavior change is invisible to any running process until Phase 5 wires the endpoint. This phase carries the design's single most safety-critical guarantee (Decision 5's GDPR-erasure audit-atomicity requirement) and therefore carries the plan's most extensive **mandatory** adversarial verification (§3.4 below).

### Phase 5 — `CustomersController` erasure endpoint

**Included**: exactly §6's table — `POST /api/customers/{customerId:guid}/erase`, `[Authorize(Policy = NivoraPolicies.OwnerOrAdmin)]`, no request body, `204 No Content` on success, `404 NotFound` for both nonexistent and cross-tenant customer ids (load without `IgnoreQueryFilters()`, then explicit `SameTenant` check — matching `LinkIdentity`/`RevokeIdentity`'s own pattern exactly), idempotent double-call via the service's own `IsAnonymized` guard (no `409`, no `IdempotencyKey` mechanism — GDPR erasure makes no external Shopify call), thin adapter delegating to `erasureService.EraseCustomerAsync(customerId, CurrentUserId, cancellationToken)`, no controller-level `try`/`catch`.

**Deferred**: nothing — final code phase. Frontend confirmation UX is out of scope per §6's own explicit non-scope statement and per §7 item 6's LOCKED resolution (confirmation is a frontend/UX responsibility, not a backend concern this plan implements).

**Why the boundary is safe**: this is the phase that actually makes GDPR erasure callable in production. Every guarantee it depends on (audit atomicity, retry-safety, tenant isolation at the service level) was already adversarially proven in Phase 4; this phase's own adversarial coverage (§3.5 below) is scoped to the HTTP-layer concerns specifically — authorization enforcement and tenant-isolation enforcement actually blocking the call, not merely returning the right status code while still executing.

### Phase 6 — Final E2E verification + checklist reconciliation

Two separate final commits, per Step 24/25's own established discipline — implementation detail in §9/§10 below.

---

## 3. Per-phase tests

| Phase | Ordinary verification | Adversarial verification required? |
|---|---|---|
| 1 | Compiles; grep confirms no existing constant in the file shares either new string value | No |
| 2 | All 6 existing `CustomerDataLifecycleTests.cs` purge tests pass unmodified (regression proof of the "owns" branch); new `Approval` cleanup tests (`Consumed` survives, non-`Consumed` deleted); `FinancialRecordRetentionPolicy.cs` doc comment updated (diff-reviewed, not test-covered — text only) | **Yes — the join-vs-own branch is this plan's central architectural fix** (§3.1 below) |
| 3 | Existing `RetentionEnforcement_PurgesOnlyExpiredConversations` passes unmodified (signature unchanged); exactly one `ConversationRetentionPurged` `AuditLog` row per purged conversation, correct actor/null-`ChangedByUserId` | **Yes — mirrors Step 25's own Ticket-race test, applied to Decision 3** (§3.2 below) |
| 4 | Updated 5 existing tests pass with `changedByUserId` threaded through; `Consumed`/non-`Consumed` `Approval` survival at both Phase 1 and per-conversation scope; `changedByUserId`/`CorrelationId` reach every audit row | **Yes — the design's single most safety-critical guarantee; the largest adversarial set in this plan** (§3.4 below) |
| 5 | Happy path 204; nonexistent customer 404; `OwnerOrAdmin` 401/403; idempotent double-call both 204 | **Yes — cross-tenant and role-enforcement negative proofs** (§3.5 below) |
| 6 | Full pipeline smoke test | No — Phases 2–5 already carry the structural proofs; this phase is composition + observation |

### 3.1 Phase 2 — `ConversationPurgeService` join-vs-own

```
Test A: PurgeConversation_CalledInsideAmbientTransaction_DoesNotThrow
  - Open an ambient transaction on the shared AppDbContext.
  - Call PurgeConversationAsync for a seeded conversation, inside that
    transaction.
  - Assert: no exception is thrown.
  - This is the direct, literal adversarial proof of the exact bug class
    §1.3 documents (EF Core's InvalidOperationException on a second
    BeginTransactionAsync against a DbContext with an already-active
    transaction) — proving it no longer reproduces, not merely that the
    new code "should" avoid it by inspection.

Break/restore (performed once during this phase's own adversarial
verification, not committed as a permanent test): temporarily revert
PurgeConversationAsync to unconditionally call BeginTransactionAsync (as
it does today). Re-run Test A. Confirm it now fails with
InvalidOperationException. Revert; re-run; confirm passing again.

Test B: PurgeConversation_JoinsAmbientTransaction_RollsBackWithCaller
  - Open an ambient transaction; call PurgeConversationAsync inside it;
    then the CALLER (not the service) rolls back.
  - Assert: nothing committed — Message still exists, Conversation.
    IsAnonymized still false, non-Consumed Approval still exists.
  - Proves the joined path has NO transaction boundary of its own — an
    ambient rollback undoes everything this method did, exactly as §1's
    Option B requires.

Test C: PurgeConversation_JoinsAmbientTransaction_CommitsWithCaller
  - Same setup as B, but the caller commits.
  - Assert: everything committed together — Message gone, Notification
    gone, non-Consumed Approval gone, Consumed Approval survives,
    Conversation.IsAnonymized true.

Test D: PurgeConversation_ApprovalCleanup_ConsumedSurvives_NonConsumedDeleted
  - Seed one Pending, one Denied, one Approved-never-Consumed, and one
    Consumed Approval, all on the same conversation.
  - Call PurgeConversationAsync standalone (no ambient transaction —
    proves the "owns" branch also gets the new Approval cleanup, not only
    the "joins" branch).
  - Assert: the three non-Consumed rows are gone; the Consumed row
    survives untouched, against the REAL dev DB (not a fake/in-memory
    provider) — directly proving Decision 4's "confirmed zero FK/cascade/
    transaction risk" claim against actual Postgres behavior, not merely
    trusting the prior discovery-pass confirmation.

Test E: PurgeConversation_StandaloneCallers_RegressionUnchanged
  - Re-run all 6 existing CustomerDataLifecycleTests.cs purge tests
    (rollback-on-injected-failure ×2, idempotent-second-call, unknown-id
    no-op, MessageKnowledgeSource cascade, analytics-shell survival)
    completely unmodified.
  - Assert: all pass exactly as today — proves the "owns" branch is
    byte-for-byte behaviorally unchanged for every caller that doesn't
    open its own transaction.
```

### 3.2 Phase 3 — `RetentionEnforcementService` atomic-claim races

```
Test A: RetentionEnforcement_TicketCreatedBetweenDiscoveryAndClaim_NotPurged
  - Seed a stale, ticket-free conversation past its business's
    RetentionDays cutoff.
  - Obtain the candidate id via the discovery query (or call the internal
    TryPurgeConversationAsync helper directly, per the internal-exposure
    reasoning above).
  - Via a SECOND, independent AppDbContext, insert an active Ticket
    (Status = Open) linked to this conversation, committed, before the
    claim step runs.
  - Run the claim step.
  - Assert: claimed == 0 (or the helper returns false); Messages intact;
    Conversation.IsAnonymized still false; no AuditLog row written.
  - Repeat once more seeding Status = InProgress, and once more with
    Status = Reopened, proving the exemption is not narrowed to a subset
    of Decision 3's named "Status != Resolved" set.

Break/restore: temporarily remove the Ticket clause from the claim's
guarded WHERE. Confirm the test now fails (claimed == 1, the conversation
is wrongly purged underneath an active Ticket). Revert; re-confirm passing.

Test B: RetentionEnforcement_RetentionDaysChangedBetweenDiscoveryAndClaim_NotPurged
  - Seed a conversation just past the discovery-time cutoff for a business
    with RetentionDays = 30.
  - Obtain the candidate id via discovery.
  - Via a second independent AppDbContext, increase the business's
    RetentionDays (e.g. to 180) so the conversation is no longer past
    cutoff, committed, before the claim step runs.
  - Run the claim step.
  - Assert: claimed == 0; conversation untouched.
  - Proves §2.3's "fresh re-read of Business.RetentionDays, never the
    discovery-time snapshot" requirement is actually load-bearing.

Break/restore: temporarily have the claim step reuse the discovery-time
RetentionDays value instead of a fresh per-claim read. Confirm the test
now fails (claimed == 1 — purged despite the widened retention window).
Revert; re-confirm passing.

Test C: RetentionEnforcement_MessageArrivesBetweenDiscoveryAndClaim_NotPurged
  - Seed a stale, zero-message conversation (CreatedAt past cutoff,
    LastMessageAt null — the CreatedAt-fallback case).
  - Obtain the candidate id via discovery.
  - Via a second independent AppDbContext, insert a new Message and set
    LastMessageAt = now, committed, before the claim step runs.
  - Run the claim step.
  - Assert: claimed == 0; conversation untouched.

Break/restore: same pattern as A/B — remove the staleness re-check from
the claim's WHERE, confirm failure, revert, confirm passing.

Test D: RetentionEnforcement_FailureInjectedAfterClaimBeforeCommit_RollsBackPurgeAndAudit
  - Use IPurgeFailurePoint (reused test-only seam) to inject a failure
    inside the joined purge call, after the claim has already succeeded.
  - Assert: neither the purge (Messages/Notifications/Approvals/
    IsAnonymized) nor the ConversationRetentionPurged AuditLog row is
    committed — the entire per-conversation transaction rolled back
    together.
  - This is the test that proves Phase 2's join mechanism and Phase 3's
    claim-purge-audit composition actually work together, not merely in
    isolation.

Test E: RetentionEnforcement_AlreadyPurgedByPriorRun_ClaimedZero_NoDuplicateAudit
  - Run EnforceRetentionAsync once successfully against a seeded eligible
    conversation, then run it again immediately.
  - Assert: the second run's claim for that conversation returns 0
    (already IsAnonymized, excluded from discovery too); exactly one
    ConversationRetentionPurged AuditLog row exists total, not two; not
    logged as an error either time.
```

### 3.3 Phase 3 — audit content

```
Test: RetentionEnforcement_WritesCorrectAuditRow
  - Purge one eligible conversation via a real EnforceRetentionAsync run.
  - Assert exactly one AuditLog row: EventType ==
    ConversationRetentionPurged, EntityType == Conversation, EntityId ==
    conversationId.ToString(), Actor == ActorType.NivoraSystem,
    ChangedByUserId == null, CorrelationId == null (retention is not
    correlated across multiple rows the way erasure is).
```

### 3.4 Phase 4 — `CustomerErasureService` audit-atomicity and retry-safety (mandatory, largest set)

```
Test A: EraseCustomer_FailureInjectedForNthConversation_PriorCommitted_NthAndLaterUntouched
  - Seed a customer with 3 conversations (each with a Message).
  - Inject an IPurgeFailurePoint that fires only on the purge call for the
    2nd conversation processed.
  - Call EraseCustomerAsync(customerId, staffUserId); expect it to throw.
  - Assert:
    - Conversation 1: fully purged (Messages gone, IsAnonymized true),
      exactly one ConversationErasurePurged AuditLog row for it.
    - Conversation 2: completely untouched (Messages intact, IsAnonymized
      false), zero AuditLog rows for it.
    - Conversation 3: completely untouched (loop never reached it), zero
      AuditLog rows for it.
    - Customer: still NOT anonymized (Phase 3 never reached);
      Email/Name/ExternalId still populated.
  - This is the direct proof of §3.7's locked property: "no purge commits
    without contemporaneous audit coverage," verified per-conversation,
    not merely asserted by inspection of the pseudocode.

Break/restore: temporarily make TryPurgeAndAuditConversationAsync call
purgeService.PurgeConversationAsync and auditLogService.LogAsync
OUTSIDE its own BeginTransactionAsync/CommitAsync pair (i.e. revert to an
untransacted per-conversation call, closer to today's shipped shape).
Re-run Test A with the failure point moved to fire AFTER the purge but
BEFORE the audit LogAsync call specifically. Confirm the test now fails —
a state exists where conversation 2's purge is committed but no audit row
exists for it, exactly the violation the locked property forbids. Revert;
re-confirm passing.

Test B: EraseCustomer_RetryAfterPartialFailure_ConvergesSafely_NoDuplicateAudit
  - Immediately after Test A's failure (conversation 1 purged+audited,
    2 and 3 untouched), call EraseCustomerAsync again for the same
    customer, same changedByUserId, no failure injected this time.
  - Assert:
    - Conversation 1: still exactly ONE ConversationErasurePurged row
      (the retry's claim for it returns 0 — not re-audited).
    - Conversations 2 and 3: now purged, exactly one
      ConversationErasurePurged row each.
    - Customer: now anonymized, exactly one CustomerErased row.
  - Proves retry-safety and that a retried erasure cannot silently
    double-audit an already-purged conversation (§3.3's own stated risk
    for Mechanism 2 without the atomic claim).

Test C: EraseCustomer_OrphanedApprovalFromReVerifiedConversation_StillErased
  - Concrete reproduction of the confirmed §7 item 1 scenario:
    - Customer A's conversation Conv1 gets a Pending Approval proposed
      against it (Approval.CustomerId = A, Approval.ConversationId =
      Conv1).
    - Simulate a second Level 2 verification re-linking Conv1 to a
      different Customer B, using the SAME mechanism
      VerificationCodeService.VerifyCodeAsync actually uses (an
      unconditional ExecuteUpdateAsync setting Conversation.CustomerId,
      no prior-value guard) — not a hand-rolled substitute.
    - Call EraseCustomerAsync(customerA.Id, staffUserId).
  - Assert:
    - The orphaned Approval (still CustomerId == A) is deleted — caught
      by Phase 1's customer-scoped delete, keyed on Approval.CustomerId
      directly.
    - Conv1 itself is NOT purged as part of Customer A's erasure (it no
      longer matches the CustomerId == customerA.Id discovery filter —
      it now belongs to B); its Messages remain intact.
    - Customer B's own data (Conv1, its Messages, Customer B's own row)
      is completely unaffected by Customer A's erasure.
  - This is the single highest-value adversarial test in this plan — the
    one confirmed, non-hypothetical edge case the design's own review
    pass found, and the reason §3.4's customer-scoped Approval delete is
    NOT redundant with the conversation-scoped one.

Test D: EraseCustomer_ConsumedApprovalSurvives_NonConsumedDeleted_BothScopes
  - Case 1 (Phase 1, customer-scoped): a Consumed Approval whose
    ConversationId no longer belongs to this customer (mirrors Test C's
    setup) survives; a non-Consumed one in the same orphaned state is
    deleted.
  - Case 2 (per-conversation purge, inherited from Phase 2 of this plan):
    a Consumed Approval still linked to a conversation this customer
    still owns survives that conversation's purge; a non-Consumed one on
    the same still-owned conversation is deleted.

Test E: EraseCustomer_ChangedByUserIdAndCorrelationId_ReachEveryAuditRow
  - Customer with 2 conversations; call EraseCustomerAsync with a known
    staffUserId.
  - Assert: every AuditLog row this one call produces (N
    ConversationErasurePurged rows + 1 CustomerErased row) has
    ChangedByUserId == staffUserId and the SAME non-null CorrelationId —
    proving §3.4's erasureCorrelationId is generated once and threaded
    through every write, not regenerated per call.

Test F: EraseCustomer_AlreadyAnonymized_TrueNoOp
  - Call EraseCustomerAsync twice in immediate succession with no
    injected failure.
  - Assert: second call performs zero deletes, zero purges, zero new
    AuditLog rows (proves the strengthened top-of-method guard from §0's
    correction actually short-circuits Phases 1/2/3 entirely on the
    second call, not merely Phase 3 as today's shipped code does).
```

### 3.5 Phase 5 — HTTP-layer authorization and tenant isolation

```
Test A: Erase_CrossTenantCustomer_Returns404_NeverAnonymizes
  - Staff authenticated for Business B calls POST .../erase for a
    Customer belonging to Business A.
  - Assert: 404 (never 403 — matching LinkIdentity's established
    cross-tenant-existence-hiding convention); Business A's customer is
    NOT anonymized afterward (proves the SameTenant check actually blocks
    the call, not merely returns the wrong status while still executing
    EraseCustomerAsync underneath).

Test B: Erase_AgentRole_Returns403_NeverAnonymizes
  - Staff with the Agent role (excluded by OwnerOrAdmin) calls the
    endpoint for a customer in their own business.
  - Assert: 403; customer NOT anonymized.

Test C: Erase_CalledTwice_SecondCallReturns204_NoExtraSideEffects
  - Call the endpoint twice for the same, valid, same-tenant customer.
  - Assert: both calls return 204; AuditLog row count for this customer
    is identical after the 2nd call to what it was after the 1st (proves
    Phase 4 Test F's guarantee holds through the real HTTP path too).

Test D: Erase_ValidOwnerOrAdminCall_ChangedByUserIdReachesAudit
  - Authenticate as a known staff user; call the endpoint.
  - Assert: the resulting CustomerErased AuditLog row's ChangedByUserId
    equals that staff user's id — proving CurrentUserId is correctly
    threaded from the authenticated principal through to the service
    call, not merely unit-tested in isolation at the service layer.
```

No break/restore steps are required for Phase 5 — these are standard authorization-precedent tests (the same shape as every other controller's existing `SameTenant`/role tests in this codebase), not proofs against a bespoke concurrency guard.

---

## 4. Dependency ordering

```
Phase 1 (AuditEventTypes) ──────────────────────────┐
Phase 2 (ConversationPurgeService join + Approval) ──┴─► Phase 3 (RetentionEnforcementService) ──┐
                                                      └─► Phase 4 (CustomerErasureService) ───────┴─► Phase 5 (Controller) ─► Phase 6 (E2E + checklist)
```

- **Phase 1 has no dependencies.** It is a prerequisite for Phases 3 and 4 (both write `AuditLog` rows using its new constants) but not Phase 2 (`ConversationPurgeService` never calls `IAuditLogService`, by design).
- **Phase 2 has no dependencies on Phase 1.** It is a prerequisite for Phases 3 and 4: both redesign their caller to open its own transaction and call `PurgeConversationAsync` expecting it to *join* rather than open a second, conflicting transaction — that behavior does not exist until Phase 2 ships. Attempting Phase 3 or 4 before Phase 2 would hit the exact `InvalidOperationException` §1.3 documents.
- **Phases 3 and 4 are mutually independent** — `RetentionEnforcementService` and `CustomerErasureService` share no code path beyond calling the same, already-redesigned `ConversationPurgeService`; either could be implemented first. This plan sequences 3 before 4 per §1's own reasoning (validate the claim-purge-audit pattern once, in the simpler single-loop case, before reusing it inside the more complex three-phase service that also carries a breaking signature change).
- **Phase 5 requires Phase 4** — it calls `EraseCustomerAsync(customerId, CurrentUserId, cancellationToken)`, the new 3-argument signature; it does not require Phase 3 (the controller never touches retention).
- **Phase 6 requires all of 1–5.**
- **Unlike Step 25, no dedicated "wiring/go-live" phase is needed for retention.** `RetentionEnforcementService` is already registered and already called every tick by the live `DataLifecycleBackgroundService` (confirmed, §0) — Phase 3 changes its internals in place; no new DI registration or background-service call is added. **This means Phase 3 goes live in production immediately on merge**, unlike Step 25's `ConversationInactivityCloseService`, which stayed dark behind a disabled-by-default flag until its own dedicated wiring phase. Phase 3's own "why the boundary is safe" note (§2 above) explains why this is acceptable: the redesign can only narrow, never widen, what gets purged, and can only add audit coverage, never remove it.
- **`CustomerErasureService` remains unreachable from any running process until Phase 5** (confirmed, §0 — "no controller calls these services yet" holds through Phase 4) — Phase 5 is this design's equivalent of Step 25's dedicated wiring phase, and carries an equivalent "zero real customers affected until this phase" safety argument up through Phase 4.

---

## 5. Intermediate repository state

| After phase | Repository can... | Cannot yet... | Known-acceptable "unused" surface |
|---|---|---|---|
| 1 | Reference the three new `AuditEventTypes` constants | Write any `AuditLog` row using them | The 3 constants — no caller yet, precedented by this file's own established "declare before consume" pattern |
| 2 | Join an ambient transaction correctly (adversarially proven); clean up non-`Consumed` `Approval`s on every purge, both callers, automatically | Compose a claim+purge+audit transaction (no caller does yet) | The "joins" branch — only exercised by this phase's own direct unit tests until Phase 3/4 |
| 3 | **Live in production immediately**: retention purges now Ticket-exempt and atomically audited | — | Nothing dark — this phase changes already-live behavior |
| 4 | Fully erase a customer with atomic per-conversation audit coverage and safe retries, directly testable | Be triggered by any real HTTP request | `CustomerErasureService`'s new signature — unreachable from production until Phase 5, same safety argument Step 25 Phase 3 used |
| 5 | **Run the complete feature live**, for any Owner/Admin, any business | — | Nothing — completion phase for the backend surface. No frontend confirmation UX exists yet (explicitly out of scope, §6) |
| 6 | — | — | — |

---

## 6. Pre-commit verification (per phase)

| Check | 1 | 2 | 3 | 4 | 5 |
|---|:-:|:-:|:-:|:-:|:-:|
| Targeted new tests (this phase's own) | — | ✔ | ✔ | ✔ | ✔ |
| Relevant existing suite (`CustomerDataLifecycleTests.cs`) | — | ✔ | ✔ | ✔ | — |
| Full backend suite | ✔ | ✔ | ✔ | ✔ | ✔ |
| Frontend suite | — | — | — | — | — |
| Migration | — | — | — | — | — |
| Database verification (real dev DB, not in-memory) | — | ✔ (join/rollback tests themselves — a fake provider cannot distinguish a real joined transaction from a mocked one, same reasoning Step 25 used for its own guarded-`ExecuteUpdateAsync` tests) | ✔ | ✔ | ✔ |
| Diff inspection (only this phase's intended files changed) | ✔ | ✔ | ✔ | ✔ | ✔ |
| Tenant-isolation verification | — | — (no cross-tenant read added) | — (no cross-tenant read added — cross-tenant test already exists and is unmodified) | ✔ (`EraseCustomer_NeverModifiesAnotherBusinesssData`, unmodified except for the new parameter) | ✔ (§3.5 Test A) |

**No migration in any phase** — this design adds no new column, table, or index; every mechanism (atomic claims, correlation ids, transaction composition) reuses existing schema.

**Frontend suite never required** — entirely backend scope, matching §6's own explicit non-scope statement.

---

## 7. Commit boundary

Every phase (1–5) ends with, in order: implementation complete → this phase's ordinary + (where required) adversarial verification complete (§3) → full backend suite clean → diff reviewed for scope (only files this phase's own §2 scope names — in particular, Phase 2's diff must not touch `RetentionEnforcementService.cs`/`CustomerErasureService.cs`, and Phase 3's diff must not touch `CustomerErasureService.cs`) → one commit, containing only that phase's work. No checklist changes bundled into any of Phases 1–5 (checklist reconciliation is §10, its own separate final commit). No unrelated cleanup or opportunistic refactoring — if any phase's implementation reveals an unrelated pre-existing issue elsewhere, that is reported and deferred, not fixed inside that phase's commit, per this project's own standing "stop on unexpected scope" convention.

---

## 8. Independent post-commit verification

After **every** phase's commit, before starting the next phase — identical discipline to Step 24/25 §8:

1. Fresh, genuinely separate checkout: `git worktree add --detach <scratch-path> <phase-commit-sha>`.
2. Exact phase-commit SHA — never a moving branch tip.
3. Clean `dotnet build backend.sln` (0 errors) + full backend test suite, from that isolated checkout.
4. Database checks: this phase's own adversarial tests re-run from the fresh worktree against the real dev DB.
5. `git show --stat <phase-commit-sha>` reviewed against §2's "Included" list for that phase.
6. `git diff <previous-phase-commit-sha> <this-phase-commit-sha> --stat` cross-checked against §2 — flags any file touched outside that phase's stated scope (e.g. Phase 2 accidentally touching `RetentionEnforcementService.cs`, or Phase 3 accidentally touching `CustomerErasureService.cs`).
7. Remove the scratch worktree once verification passes.

Only once this independent re-verification passes does the next phase begin.

---

## 9. Final end-to-end verification phase

A dedicated phase after all five feature phases are committed and independently re-verified (§8). Not a Claude/live-model validation — this feature calls no Anthropic API anywhere in its own logic.

### Successful path — erasure
1. Seed a throwaway `Business`, a staff `ApplicationUser` (Owner role), a `Customer` with 2 conversations (each with Messages), one Pending `Approval` and one Consumed `Approval` on one of those conversations.
2. Authenticate as the seeded Owner; call `POST /api/customers/{customerId}/erase` through the real HTTP pipeline (not a direct service call).
3. Verify: `204`; `Customer` anonymized (`Email`/`Name`/`ExternalId` null, `IsAnonymized` true); both conversations purged (`Messages` gone, `IsAnonymized` true); the Pending `Approval` deleted; the Consumed `Approval` survives; exactly 2 `ConversationErasurePurged` rows + 1 `CustomerErased` row, all sharing one `CorrelationId`, all with `ChangedByUserId` == the seeded Owner's id.

### Successful path — retention (already-live job)
4. Trigger one poll tick through the **actual** `DataLifecycleBackgroundService` path (or, if impractical to drive the real `PeriodicTimer` directly, call `IRetentionEnforcementService.EnforceRetentionAsync` directly, immediately preceded by confirming the DI registration resolves it — same allowance Step 24/25 §9 used) against a seeded stale conversation with no Ticket, and a second stale conversation with an active `Open` Ticket.
5. Verify: the ticket-free conversation is purged with exactly one `ConversationRetentionPurged` row (`ActorType.NivoraSystem`, `ChangedByUserId` null); the ticket-active conversation remains un-purged, no audit row for it.

### Suppressed / edge paths (cheap given the harness above)
- Already-anonymized customer: call the erase endpoint twice → second call `204`, zero additional side effects (§3.5 Test C, exercised through the real endpoint).
- Cross-tenant: staff from a second throwaway business calls erase on the first business's customer → `404`, no mutation (§3.5 Test A, exercised end-to-end).
- The §7 item 1 orphaned-`Approval` scenario (§3.4 Test C), run once through the real HTTP endpoint rather than only at the service level: re-verify a conversation to a different customer mid-flow, then erase the original customer via the endpoint, confirm the orphaned `Approval` is still caught.

### Concurrency path
Reuse §3.2's Test A shape against the throwaway business's real seeded rows for retention: a Ticket created for a candidate conversation between discovery and claim → conversation survives, no audit row.

### Cleanup
Delete, in FK-safe order, everything this phase seeded: `AuditLog` rows → `Approval` rows → `Ticket` rows (if any seeded) → `Message` rows → `Conversation` rows → `Customer` rows → the staff `ApplicationUser` → the throwaway `Business`(es). Performed as explicit teardown code in the test itself, matching this codebase's established convention. No temporary validation code or throwaway data may remain in the repository or the dev DB once this phase's commit lands.

---

## 10. Checklist reconciliation

A separate, final commit, **after** §9's end-to-end verification passes — never bundled into Phase 5 or any earlier phase.

- Updates `NIVORA_FULL_PLAN_CHECKLIST_V2.md`'s Phase 10 — GDPR / Privacy section. Specifically, the entry at line 102 currently reads (in part): *"...Remaining gap: no authenticated HTTP endpoint / manual trigger exists for on-demand customer erasure — `CustomerErasureService` is only reachable from code/tests today; the customer-vs-staff-initiated trigger design is not yet decided."* This entry is updated to reflect that the endpoint now exists (`POST /api/customers/{customerId}/erase`, Owner/Admin-only, per Decision 2's now-locked trigger design), that `ConversationPurgeService`'s transaction ownership and `CustomerErasureService`'s audit-atomicity gap (both flagged only in this design/plan, not in the checklist's current text) are resolved, and that the `Approval` retention/redaction split (Decision 4) is implemented. Line 101's retention-enforcement entry is updated to note the added Ticket exemption (Decision 3) and the now-atomic per-conversation audit write (Decision 5), without re-claiming anything about Decision 6's still-undecided numeric retention periods (line 107's entry, untouched by this plan, stays as-is).
- Contains no feature implementation, no test changes, no migration — text-only, one file.
- Independently re-verified the same way as every other phase (§8).

---

## 11. Final GDPR lifecycle completion gate

Complete only when **all** of the following hold:

1. All five feature phases (§1) committed, each as its own commit containing only that phase's work.
2. Every phase's ordinary verification passed (§3); Phase 2's join/rollback tests, Phase 3's Ticket/RetentionDays/Message race tests, and Phase 4's audit-atomicity/retry-safety/orphaned-`Approval` tests (§3.1, §3.2, §3.4) all passed, including each one's own break/restore step having actually been performed and observed to fail with the documented signature during that phase's own verification (not merely asserted in this plan to be possible).
3. Every phase's commit independently re-verified from a fresh, detached `git worktree` (§8).
4. Full backend test suite passes at every phase boundary and at the final state.
5. §3.2's Decision 3 five-condition-equivalent claim guarantee (Ticket, `RetentionDays`, staleness, re-anonymization) is reflected unchanged in the shipped `RetentionEnforcementService` — no implementation shortcut reduces the guarded `WHERE` to a subset of what §2.3 specifies.
6. §3.7's locked audit-atomicity property is reflected unchanged in the shipped `CustomerErasureService` — every conversation purge that commits does so inside the same transaction as its own `ConversationErasurePurged` row, with no code path violating this, verified by Phase 4 Test A's break/restore actually having failed the expected way and been reverted.
7. Decision 4's `Approval` split (`Consumed` survives, non-`Consumed` erasure/purge-eligible) is reflected unchanged, at both the conversation-scoped (`ConversationPurgeService`) and customer-scoped (`CustomerErasureService` Phase 1) delete sites.
8. Decision 2's trigger design and §7 item 6's no-additional-confirmation resolution are reflected unchanged in the shipped `CustomersController` endpoint (`OwnerOrAdmin`, bare authenticated `POST`, no request-body confirmation gate).
9. §9's final end-to-end pipeline passes: the successful erasure path, the successful retention path, all suppressed/edge paths, and the concurrency path.
10. All §9 throwaway/seeded data confirmed cleaned up — working tree and dev DB both.
11. Checklist reconciliation (§10) committed separately, after everything above, independently re-verified like every other commit.
12. Final working tree reviewed clean — no stray files, no uncommitted feature work, only the permitted uncommitted handoff-doc convention this project already follows for design/plan documents themselves.
13. No unresolved blocking decision remains open. Decision 6 (numeric retention periods) and the two external/non-blocking items (Decision 1's legal sign-off, Decision 2's Art. 28 documented-instruction gap) are unaffected and unresolved by this plan, exactly as the locked design's own scope states — not reopened, not silently assumed resolved.

---

## Genuine blocking ambiguity discovered during this pass

**None found.** Every implementation assumption the locked design's pseudocode depends on was directly re-verified against the current repository (§0) and matches exactly, with one non-blocking documentation-accuracy correction (§0's guard-comment note — an implementation-preference-level fix to a design-doc comment's own justification text, not a change to any phase's scope, tests, or shipped behavior). No contradiction and no newly discovered edge case/gap requiring an owner decision was found — every piece of the locked design (§1's Option B, §2's claim mechanism, §3's Mechanism 2, §4's constants, §5's `Approval` placement, §6's endpoint) remains directly implementable exactly as specified.
