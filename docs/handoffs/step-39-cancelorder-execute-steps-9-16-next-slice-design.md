# Step 39 — `cancelOrder` `Execute`: Steps 9–16 Audit and Next-Slice Design

**DESIGN PASS ONLY — NO IMPLEMENTATION.** No `.cs` file was created, modified, or touched by this pass. Every claim below was re-derived by directly reading live source and the cited handoff documents this session — nothing is restated from any prior handoff's summary without independent confirmation against the actual file or current source. The only artifact this pass creates is this document.

---

## 0. Starting state, re-verified independently

`git rev-parse HEAD`: `6aa1090fa7d1f435853c53c3fed8514fcf392b38` — confirmed at the start of this pass and unchanged throughout (re-checked before writing this file). `git status --porcelain` shows 110 lines of pre-existing working-tree diff (the same long-standing set this track has carried since before Step 24, unaffected by this pass).

**`OrderCancelApprovedActionExecutor.cs`, re-read directly via `cat`, not trusted from any handoff's description**: confirmed it currently implements exactly steps 3–8 of the execution ordering — `Approval` load + tenant-fold `ApprovalNotFound` refusal (step 3/4), `Status != Approved` → `Refused(ApprovalNotApproved, ...)` with an `AuditLog` write (step 6), `ExpiresAt <= now` → `Refused(ApprovalExpired, ...)` with an `AuditLog` write (step 7), and a fresh `Conversation.CustomerId` re-derivation → `Refused(CustomerUnresolved, ...)` with **no** `AuditLog` write (step 8). Everything past that point is an unconditional `throw new NotImplementedException(...)`. This confirms the premise stated in the task — re-derived fresh from the committed source, not copy-forwarded from `step-38`'s own description of an earlier, pre-commit state.

**Next available step number, derived from disk, not assumed**: `ls docs/handoffs/ | sort -V` shows the highest existing file is `step-38-cancelorder-execute-next-slice-design.md`; no `step-39-*` file exists. This document is therefore **Step 39**.

---

## 1. Sources re-read this pass, and what each confirms

- `backend/Services/OrderCancelApprovedActionExecutor.cs` — current state, confirmed above.
- `docs/handoffs/step-38-cancelorder-execute-next-slice-design.md` — re-read in full (both correction passes' final text, not the pre-correction drafts). Its §2 table (steps 6–16) and §9 summary table are the basis for §2 below, independently re-confirmed against live source rather than merely copied forward.
- `docs/handoffs/step-24-cancelorder-executor-design.md` — re-read in full (both halves). The 16-step pseudocode (§2 of that document) is the concrete code shape steps 9–16 below are drawn from; the failure-mode mapping table (§5) and the transaction-composition analysis (§4) are cited directly, not paraphrased from a downstream summary.
- `backend/Services/IApprovedActionExecutor.cs` — re-read in full, fresh. **`ExecutionRefusalReason` still has exactly 8 members** (`ApprovalNotFound`, `ApprovalExpired`, `ApprovalNotApproved`, `CustomerUnresolved`, `Level2VerificationFailed`, `OwnershipVerificationFailed`, `ActionHashMismatch`, `DuplicatePendingCancellation`), `ExecutionResultStatus` still exactly 3 (`ExternallySatisfied`, `Failed`, `UnknownNeedsReconciliation`, no `Confirmed` member), the `Refused`/`Executed` closed hierarchy unchanged. **Nothing about `ExecutionOutcome`'s shape has changed since Step 29/`step-38`** — re-confirmed directly, not assumed unchanged.
- `backend/Services/ApprovalExecutionKeyDeriver.cs`, `backend/Services/ActionHasher.cs`, `backend/Services/IdempotencyKeyDeriver.cs`, `backend/Models/Approval.cs` — re-read in full. Signatures and doc comments unchanged from every prior pass's citation — see §6 below.
- `backend/Services/OrderCancellationService.cs` + `backend/Data/AppDbContext.cs` (the `OrderCancellation` entity configuration block) — re-read/re-grepped fresh. **Found the task's own premise about the Pending-row guard is stale — see §4 below; corrected, not silently accepted.**
- `backend/Models/OrderCancellation.cs` — read in full, fresh. Its own doc comment is the most precise, load-bearing statement in the whole repository of exactly how the async-job problem is already solved — see §2a below.
- `NIVORA_SCHEMA_AND_LOGIC_REFERENCE.md` §2 (`OrderCancellation` entry) and §3.4 (idempotent external action execution pattern) — re-read directly. Confirms the same advisory-lock drift `step-38` §5 already flagged (still present, still not corrected, still out of scope for a design pass to fix) and the same "hard blocker" framing `step-38` §5 already found stale in one specific respect (the check is built, Step 20; not yet wired, since `Execute` still doesn't reach step 10).
- `docs/handoffs/step-22-ai-action-safety-model-owner-decisions.md` — **re-read both §0 and §4.5 this pass, not just §0** (per the task's explicit instruction, citing the exact mistake a prior session made). Confirmed: §0's table (Step 22O) lists 7 decisions adopted + Decision 8 deferred; §4.5, amended afterward by **Step 22S**, adds two more explicitly-adopted decisions (**5a** — reopenability, **5b** — stuck-pending threshold) directly into its own body text, never reflected back into §0's table. **The correct, current total is 9 adopted + 1 deferred** — re-confirmed by this pass independently, matching `step-38`'s own already-corrected §4.
- `backend/Program.cs` — grepped fresh for every DI registration steps 9–16 depend on: `ILevel2VerificationChecker`, `IShopifyOrderLookupService`, `IOrderOwnershipAuthorizationService`, `IOrderCancellationService`, `IOrderCancellationExecutor` are all still `AddScoped`/`AddTransient`-registered, unchanged.
- Fresh repository-wide grep for `ILevel2VerificationChecker`/`IOrderOwnershipAuthorizationService` call sites, excluding their own definition/implementation files and `Program.cs`'s DI registrations: **still zero call sites anywhere** — re-confirmed unchanged from `step-24`/`step-38`'s own finding, not assumed.

---

## 2. Full ordered remaining step set (steps 9–16), re-classified from source

Base ordering is unchanged, LOCKED, `step-22z-approval-proposal-approval-lifecycle-design.md` §10 — not reopened by this pass. Steps 1–8 are done (steps 1/2/5 outside `Execute`; 3/4/6/7/8 implemented and committed, `6aa1090`).

| Step | What it does | Refusal / outcome on failure | Classification |
|---|---|---|---|
| 9 | `ILevel2VerificationChecker.IsVerifiedAsync(businessId, conversationId, ct)` | `Refused(Level2VerificationFailed, ...)` | **LOCKED** — Decision 2, ADOPTED (execution-time only), re-confirmed directly against the owner-decisions doc this pass (§3 below). No infra-failure branch exists for this step: `Level2VerificationChecker` performs only local DB reads (`Conversations`/`ExternalIdentities`, both `IgnoreQueryFilters` + explicit `BusinessId`), no external HTTP dependency — carried forward from `step-24` §5's own direct read of the concrete class, not independently re-opened by this pass. |
| 10 | `IShopifyOrderLookupService.LookupOrderOwnerAsync(...)` (throws uncaught on infra failure) then `IOrderOwnershipAuthorizationService.AuthorizeAsync(...)` | `Refused(OwnershipVerificationFailed, ownershipResult.Reason.ToString())`, or an uncaught exception on infra failure | **LOCKED** requirement (the schema reference's own §3.2 step 3 "hard-blocker cross-check"; mechanism already built, Step 20 — confirmed still unwired, §1/§4). **First step that makes an external HTTP call and the first step with a genuine, exercisable infra-failure-uncaught-exception path.** Infra-failure-stays-exception convention: **OWNER DECISION REQUIRED (confirm before implementation locks it in)** — restated unchanged from `step-23u` §10.3, still not resolved by any pass since, re-confirmed this pass (no new document addresses it). |
| 11 | Recompute `ActionHasher.Derive(...)`, compare byte-for-byte to `Approval.ActionHash` | `Refused(ActionHashMismatch, ...)` | **LOCKED** requirement to perform the recompute. Detection scope **ENGINEERING-DETERMINED, clarifying** (per `step-24` §2a): given `IApprovedActionExecutor`'s locked, `approvalId`-only signature, only `CustomerId` is genuinely re-derived from a live source — a mismatch here can only mean customer-identity drift since proposal time, never tampered/re-submitted arguments. |
| 12 | `ApprovalExecutionKeyDeriver.Derive(BusinessId, ToolName, ResourceId, CanonicalArguments, ApprovalId)` | N/A (pure computation, no failure mode) | **LOCKED** — mechanical, exact input tuple re-confirmed by direct re-read this pass (§6). No constructor dependency — a static class. |
| 13 | ATOMIC: open `Database.BeginTransactionAsync`; guarded `ExecuteUpdateAsync` claiming `Approved→Consumed` (`WHERE Status='Approved' AND ExpiresAt>now`); decode `Approval.CanonicalArguments` via `ToolArgumentCanonicalizer.DecodeOrderCancellationArgs` (already implemented and `Enum.IsDefined`-hardened, Steps 29/31/32); call `IOrderCancellationService.RequestCancellationAsync(...)` with the derived `idempotencyKey`; second `ExecuteUpdateAsync` writing `Approval.OrderCancellationId`; commit | Claim-loss (0 rows affected) → `Refused(ApprovalNotApproved, "Lost the claim race...")`; `DuplicatePendingOrderCancellationException` → rollback, `Refused(DuplicatePendingCancellation, ex.Message)`; raw transaction/DB failure → uncaught exception | Transaction composition/two-write mechanic: **ENGINEERING-DETERMINED, resolved by `step-24` §4**, directly precedented (`VerificationCodeService.cs`). Catch-inside-`Execute` placement: **ENGINEERING-DETERMINED** (23U's own choice, not reopened). **TOCTOU claim-race-loss folded into `ApprovalNotApproved` vs. a distinct enum value: OWNER DECISION REQUIRED — re-confirmed still unresolved this pass** (`ExecutionRefusalReason` still has exactly 8 members, re-verified fresh, §1 above). **The `IX_OrderCancellations_BusinessId_ExternalOrderId_Pending` Pending-row guard this step relies on is already built — see §4, a correction to this task's own stated premise.** |
| 14 | First action after commit: `IShopifyOrderLookupService.GetCancellationStateAsync(...)` (throws uncaught on infra failure). If `CancelledAt != null`: `MarkConfirmedAsync` + `Executed(id, ExternallySatisfied, ...)` | Uncaught exception on infra failure (state already durably `Consumed`/`Pending`, safe) | **LOCKED** requirement (Step 22R, architecturally resolved) — re-confirmed against `step-22z` §16 directly this pass. This is the read-only "ground truth" check `NoAutomaticRetry` explicitly permits (§2a below) — a negative finding here (`CancelledAt == null`) authorizes proceeding to the mutation call, never a retry of anything. |
| 15 | `IOrderCancellationExecutor.ExecuteAsync(...)` (void-returning, throws uncaught on infra failure) — **the actual Shopify `orderCancel` mutation call, the first and only one in the entire 16-step ordering**; re-read `OrderCancellation.Status` afterward | `Failed` → `Executed(id, Failed, finalRow.FailureReason)`; else → `Executed(id, UnknownNeedsReconciliation, null)` | **LOCKED** requirement for ordering/existence; the exact status-remap is **ENGINEERING-DETERMINED**, built from already-locked enum members. `ExternallySatisfied` remains reachable via only the step-14 ground-truth path, never the mutation-response path, until Shopify's exact "already cancelled" rejection shape is externally verified (Problem B) — **NOT ESTABLISHED FROM SOURCE / EXTERNAL VERIFICATION REQUIRED**, re-confirmed still open, non-blocking. |
| 16 | Reconciliation resolves ambiguity on its own existing cadence | N/A | **Out of `Execute`'s scope entirely** — already shipped (`OrderCancellationReconciliationService`, a running `IHostedService`), unmodified, not touched by any step above. **This is the already-built job-completion mechanism the async-job problem (§2a) relies on — not something this or any future `Execute`-focused slice needs to design.** |

---

## 2a. `orderCancel` is asynchronous — confirmed already fully accounted for, not a gap requiring new design

Re-read `backend/Models/OrderCancellation.cs`'s own doc comment and `NIVORA_SCHEMA_AND_LOGIC_REFERENCE.md`'s `OrderCancellation` entry directly, fresh this pass — both state the same fact in nearly identical words: **"orderCancel is ASYNCHRONOUS — Shopify returns a job, not an immediate result. Do not mark `Status = Confirmed` just because the mutation was accepted."**

**This is not an open design question `Execute` needs to solve — it is already solved, by already-shipped infrastructure, confirmed directly from source this pass:**

- `OrderCancellation.cs`'s own doc comment states plainly: *"`ShopifyOrderCancellationExecutor` never itself calls `MarkConfirmedAsync` — only `OrderCancellationReconciliationService` does, by polling Shopify's own `Order.cancelledAt` as ground truth, so exactly one code path ever performs that transition."*
- This is exactly what step 15's design (§2 above) already reflects: `IOrderCancellationExecutor.ExecuteAsync` never returns a "success" outcome that maps to `Confirmed` — its only two effects on `OrderCancellation.Status` are leaving it `Pending` (accepted, still processing, or genuinely ambiguous) or setting it `Failed` (a definite Shopify rejection). `Execute`'s own step-15 remap (§2) correctly reflects only these two possibilities — `ExecutionResultStatus` has no `Confirmed`/success member reachable from this path at all, by design (§1, `ExecutionResultStatus`'s own doc comment: *"a plain, Nivora-caused Confirmed is not reachable as a synchronous return value from any code path that exists today"*).
- Job-completion tracking already exists and is already running: `OrderCancellationReconciliationService` (confirmed elsewhere in this track as an already-shipped `IHostedService`, polling-based, not webhook-based) is the **only** caller of `MarkConfirmedAsync` anywhere in the codebase, and it operates entirely outside `Execute`'s own call graph — step 16 (§2 above).

**Consequence for this task's own explicit instruction not to invent a job-completion mechanism**: there is nothing to invent. The repository already establishes, and already runs, exactly one such mechanism (polling reconciliation), and `Execute`'s own design (steps 14/15) already correctly defers to it rather than attempting to track job completion itself. **Classification: LOCKED, already resolved by already-shipped infrastructure — not a gap, not blocking, not this or any future slice's own design problem to solve.** No new mechanism is proposed here, consistent with the task's explicit instruction.

---

## 2b. `NoAutomaticRetry` — which step first touches retry-relevant territory, and how

Re-confirmed directly from `step-22z` §16 and the owner-decisions doc (Decision 6, §3 below): `NoAutomaticRetry` governs whether a *second Shopify mutation attempt* may ever be authorized from a negative or ambiguous ground-truth observation. Tracing exactly where this territory begins in the remaining steps:

- **Steps 9–12 never touch it** — pure reads/computation, no `OrderCancellation` row exists or is referenced, no idempotency key is derived against anything Shopify-facing yet (step 12 only *computes* the key; nothing has been submitted anywhere).
- **Step 13 is the first step that creates a durable execution-intent row and derives the idempotency key that will govern any subsequent retry safety** — this is where `NoAutomaticRetry`'s domain genuinely begins, per `step-24` §7's own already-verified retry-safety analysis (a same-`Approval` retry loses the claim race or finds `Status != Approved`; a second, independently-proposed `Approval` for the same order is blocked by the already-built Pending-row guard, §4 below — neither path ever re-attempts a mutation from a negative ground-truth finding).
- **Step 14 (`GetCancellationStateAsync`) is the ground-truth read `NoAutomaticRetry` explicitly permits** — a negative finding (`CancelledAt == null`) authorizes proceeding to attempt the mutation for the *first* time on this execution-intent row, never a second attempt of anything.
- **Step 15 (`IOrderCancellationExecutor.ExecuteAsync`) is the actual, singular mutation call** — the one point in the entire 16-step ordering that ever sends `orderCancel` to Shopify. `NoAutomaticRetry`'s core prohibition is precisely: nothing may cause this step to run a second time for the same execution intent based on an ambiguous or negative later observation. That enforcement already exists structurally — `Approval.Status` is `Consumed` (terminal) the moment step 13 commits, so no subsequent `Execute` call for the same `Approval` can ever reach step 15 again (it refuses at step 6 instead, per the already-implemented, already-tested check).

**Consequence for the next slice proposed in §5**: since the next slice (Step 9 alone) sits strictly before step 13, it never reaches any step in `NoAutomaticRetry`'s domain — respected trivially, by the same structural argument `step-38` §8 already made for steps 6–8, extended one step further.

---

## 3. Owner decisions re-derived from §0 *and* §4.5 — none blocks steps 9–16

Re-read `step-22-ai-action-safety-model-owner-decisions.md`'s full §0 table and §4.5's later additions this pass, not merely §0 alone (§1 above). The complete, current list — **9 adopted, 1 deferred**:

| # | Decision | Touches steps 9–16? |
|---|---|---|
| 1 | Approval TTL — 15 minutes | No — governs step 7 (already implemented), unaffected here. |
| 2 | Level 2 verification staging — execution-time only | **Yes — governs step 9 directly, already fully resolved** ("execution-time only" is unambiguous; the mechanism, `ILevel2VerificationChecker.IsVerifiedAsync`, is a pure predicate already built). No blocker. |
| 3 | Agent approval authority (+ proposer≠approver) | No — governs steps 1/2/5, outside `Execute` entirely. |
| 4 | `AutoRefundLimit` — remains dormant | No — not consulted anywhere in the 16-step ordering. |
| 5 | Manual recovery authority | No — governs a separate, not-yet-designed-in-code endpoint outside `Execute`'s own 16 steps (step 16/reconciliation and manual recovery sit outside `Execute`). |
| 5a | Reopenability (Step 22S) | No — governs manual recovery only. |
| 5b | Stuck-pending threshold (Step 22S), 4 hours | No — governs `OrderCancellationReconciliationService`'s own escalation timing, not `Execute`'s logic; its own adoption text explicitly states no bearing on `NoAutomaticRetry`. |
| 6 | Cancellation by another actor — externally satisfied, no causation attribution | **Yes — governs step 14's `ExternallySatisfied` outcome directly, already fully resolved.** No blocker. |
| 7 | `Approved→Denied` authority | No — governs the separate `Deny` endpoint. |
| 8 | Per-business/per-tool automation tiers | **DEFERRED, not adopted** — no bearing on anything in steps 9–16. |

**None of the 9 adopted decisions, nor the deferred 8th, blocks or leaves ambiguous anything in steps 9–16** — the two decisions that do touch this range (2 and 6) are both already fully resolved and already correctly reflected in §2's table above. This matches and re-confirms `step-38` §4's own finding for steps 6–8, extended to steps 9–16 without finding any new dependency.

---

## 4. The `OrderCancellation` Pending-row guard — CORRECTED: already built, not "designed but not yet built"

**This task's own framing ("designed in 22R, not yet built per last tracking") is stale and is corrected here, not accepted at face value.** Fresh grep and direct source read this pass:

- `backend/Services/OrderCancellationService.cs:27`: `private const string PendingGuardIndexName = "IX_OrderCancellations_BusinessId_ExternalOrderId_Pending";`
- `backend/Services/OrderCancellationService.cs:121`: `catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505", ConstraintName: PendingGuardIndexName }) { ...  throw new DuplicatePendingOrderCancellationException(businessId, externalOrderId); }`
- `backend/Data/AppDbContext.cs:587`: `o.HasIndex(x => new { x.BusinessId, x.ExternalOrderId }, "IX_OrderCancellations_BusinessId_ExternalOrderId_Pending").IsUnique().HasFilter("\"Status\" = 'Pending'")`

**The guard was designed in Step 22R, then actually implemented and independently verified in the Step 23Q→23T track** (a distinct, already-completed sub-track of this same overall design series, predating even `step-24`) — this is not new information this pass discovered, but it directly contradicts the "not yet built" premise this task's prompt stated, and the task explicitly asked me to check current source rather than assume. **Consequence for step 13 (§2 above): nothing needs to be built for this guard as part of implementing step 13.** `Execute`'s own step 13 design already correctly calls `IOrderCancellationService.RequestCancellationAsync(...)` exactly as it exists today and catches `DuplicatePendingOrderCancellationException` — no new migration, no new index, no new catch clause shape is required; the guard is a pre-existing, already-audited dependency, not a co-requisite of this design.

---

## 5. `ExecutionOutcome` and staff-actor-identity gaps — re-confirmed still open, neither invented around

**`ExecutionOutcome` itself**: re-confirmed unchanged (§1 above) — 8-member `ExecutionRefusalReason`, 3-member `ExecutionResultStatus`, unchanged hierarchy. The four `OWNER DECISION REQUIRED` items carried since `step-23u` §10 remain exactly as unresolved as before this pass:
1. `ExecutionIntentId` vs. `OrderCancellationId` naming — unaffected by steps 9–12; relevant only once step 13/15 are implemented (the field is already named `ExecutionIntentId` in shipped code, a de facto choice not yet owner-ratified).
2. TOCTOU claim-race-loss folded into `ApprovalNotApproved` vs. a distinct value — directly relevant to step 13 specifically (§2 above); **does not block steps 9–12**, which never reach the atomic claim.
3. Infra-failures-stay-exceptions convention — directly relevant to step 10 (first genuine infra-failure-uncaught-exception path) and steps 14/15; **does not block step 9**, which has no infra-failure branch at all (§2).
4. `Refused.Detail`'s freeform-vs-structured shape — already in use as freeform strings throughout the shipped code (steps 6/7/8); unaffected either way by steps 9–16.

**None of these four blocks step 9 specifically** — all are either already resolved for step 9's own concerns (item 1 doesn't apply; items 2/3 apply only to steps 13/10 respectively) or are pre-existing conventions already exercised safely by the shipped steps 6–8 code (item 4).

**Staff-actor-identity gap**: re-confirmed still open — `IApprovedActionExecutor.ExecuteApprovedActionAsync`'s signature is unchanged (`approvalId`, `CancellationToken` only), no document since the steps-6-8 work resolves what `Execute` should pass for `AuditLog.Actor`/`ChangedByUserId` when it does need to write one. **This does not, by itself, block a future slice that needs an audit write** — the already-shipped steps 6/7 code demonstrates the accepted interim pattern (`ActorType.NivoraSystem`, `ChangedByUserId: null`, documented as a deliberate placeholder). Whether step 9 needs this same treatment is addressed directly below, in §5a — **not silently assumed either way**.

### 5a. Step 9's audit-write question — DECIDED by explicit owner decision, not by source

**[Owner decision recorded here, superseding this section's original "not resolved here" framing]** `step-22z` §18's "unauthorized execution blocked" audit requirement (the same provision that, after two correction passes, was found to cover both step 6 `ApprovalNotApproved` and step 7 `ApprovalExpired`) was resolved for those two by a specific argument: both are checks on the `Approval`'s **own authorization state**, gated by the identical `Status`/`ExpiresAt` condition reused at the atomic claim, and the named example list itself was shown (by cross-document comparison) to rotate between drafts rather than forming a closed enumeration — meaning the catch-all ("or any other execution-time security refusal") sweeps broadly.

`Level2VerificationFailed` (step 9) was found, by independent verification, to share **none of the three anchors** that resolved steps 6/7/11: it is not gated by the same `Status`/`ExpiresAt` `WHERE` clause steps 6/7 share at the atomic claim (it is a live call to a separate service, `ILevel2VerificationChecker`, not a field comparison on the already-loaded `Approval` row); it has never been named as an illustrative example in either design draft (unlike step 7's "expired" or step 10's "ownership failure"); and it has no dedicated, standalone confirming statement anywhere in source the way `step-22z` §13 gives `ActionHashMismatch` ("audited as an `UnauthorizedActionBlocked`-class event"). This confirms the distinction this section originally flagged is **real, not illusory** — source genuinely does not settle this question either way.

**DECIDED (owner decision, explicit, not source-derived): Step 9's `Level2VerificationFailed` refusal DOES require an `AuditLog` write**, using the exact same shape steps 6/7 already established: `AuditEventTypes.UnauthorizedActionBlocked` (reused, not a new constant), `ActorType.NivoraSystem` (the same value steps 6/7 use, re-confirmed directly from `OrderCancelApprovedActionExecutor.cs` before this decision was recorded — no staff actor is captured for an automated refusal here either, the identical situation steps 6/7 already document), `ChangedByUserId: null` (the same established placeholder rationale — `IApprovedActionExecutor`'s locked signature carries no actor/identity parameter, unchanged since §5 above).

**This is recorded explicitly as an owner decision, not a source-derived conclusion, for future traceability**: nothing above changed — the source-level analysis (no shared mechanical gate, no draft naming, no standalone statement) is exactly as inconclusive as it was before this decision. The owner chose to resolve the resulting ambiguity in favor of parity with steps 6/7, rather than leaving step 9 unaudited or requiring further analysis. A future reader should not mistake this for a newly-discovered source-level resolution — it is a policy choice made explicitly because source itself did not settle the question.

**Classification: LOCKED (owner-decided)** — no longer `OWNER DECISION REQUIRED`; the decision has been made and is recorded here.

---

## 6. The four identity concepts — re-confirmed properly separated; none touched by the proposed slice

Re-read `ApprovalExecutionKeyDeriver.cs`, `ActionHasher.cs`, `IdempotencyKeyDeriver.cs`, `Approval.cs` fresh this pass — no change in any signature, doc comment, or field since `step-38`'s own citation.

| Concept | Touched by (remaining steps) | Touched by the proposed slice (§7)? |
|---|---|---|
| `IdempotencyKeyDeriver` | No step in `Execute` at all — unchanged finding, re-confirmed | No |
| `Approval.Id` | Step 12 (input to `ApprovalExecutionKeyDeriver.Derive`), step 13 (claim predicate, `Consumed` transition, `OrderCancellationId` back-write) | No — the proposed slice stops at step 9, which reads `Approval.BusinessId`/`ConversationId`, not `Approval.Id` as a distinguishing input |
| `ActionHasher` | Step 11 only | No |
| `ApprovalExecutionKeyDeriver` | Step 12 only | No |

**The collapse risk `step-38` §3 flagged (`ActionHasher`/`ApprovalExecutionKeyDeriver`'s near-identical construction and adjacent placement at steps 11/12) remains exactly as described, unchanged, and remains not live in the slice this document proposes** — step 9 reaches neither step 11 nor step 12. This risk becomes live only once a future slice actually reaches steps 11–13 together, restated here for continuity, not re-litigated.

---

## 7. Proposed next implementation slice: step 9 only (`ILevel2VerificationChecker.IsVerifiedAsync`)

**Extend `OrderCancelApprovedActionExecutor.ExecuteApprovedActionAsync` to implement, immediately after the existing step 8 (`CustomerUnresolved`) check:**

1. Call `ILevel2VerificationChecker.IsVerifiedAsync(approval.BusinessId, approval.ConversationId, cancellationToken)`.
2. If `false` → `Refused(Level2VerificationFailed, "Conversation is not currently Level 2 verified.")` (exact wording an implementation-time detail, not fixed here).
3. **DECIDED by explicit owner decision (§5a): this refusal DOES require an `AuditLog` write**, matching steps 6/7's exact shape: reuse `AuditEventTypes.UnauthorizedActionBlocked`, `ActorType.NivoraSystem`, `ChangedByUserId: null`, documented as the same placeholder rationale steps 6/7 already established — not a new shape.
4. Everything past that (step 10 onward) continues to throw `NotImplementedException`, exactly as steps 9+ threw before this slice.

### 7a. Why this exact boundary, not an arbitrarily different one

- **Exactly one new constructor dependency, and the lightest of the five steps 9–16 still need.** `ILevel2VerificationChecker` is `AddScoped`-registered already (`Program.cs:518`, confirmed by fresh grep this pass), performs only local DB reads (no external HTTP call, no new transaction), and is a pure predicate with no side effect of its own. Every step from 10 onward requires either the first external Shopify HTTP call (step 10), a static-class pure computation tightly coupled to the next step (steps 11/12, per §6's collapse-risk note — these should not be implemented far apart from a fresh, careful read of that distinction, nor far apart from the atomic claim they exist to feed), or the atomic transaction itself (step 13, the single most complex remaining step, per `step-24` §4's own extensive analysis). Stopping at step 9 avoids all of that in one slice, exactly mirroring how `step-38` scoped steps 6–8 to avoid step 9's own (then-next) dependency.
- **No transaction, no external call, no side effect beyond the still-open audit-write question (§5a).** Step 9 is a pure read with (pending §5a's resolution) at most the same shape of side effect steps 6/7 already introduced and tested — not a new category of risk.
- **Testability matches the existing, already-proven pattern exactly.** `Level2VerificationChecker` needs no fake/mock HTTP handler (unlike step 10's Shopify dependency) — it can be tested against the real Neon dev DB by seeding `Conversation.Level2VerifiedUntil`/`ExternalIdentity` state directly, the same real-DB pattern `OrderCancelApprovedActionExecutorTests.cs` already established for steps 6/7/8.
- **Dependency ordering matches the design's own already-resolved sequencing.** Step 9 has no ordering ambiguity relative to step 8 (already implemented) or step 10 (not yet reached) — the 16-step list is itself already LOCKED (`step-22z` §10).
- **Deliberately does NOT bundle step 10 in the same slice**, even though both are "the next thing," specifically because step 10 introduces the first external HTTP call and the first genuine infra-failure-exception path this class would need to actually exercise — a materially different, heavier category of risk and test infrastructure (a fake Shopify HTTP handler, per this codebase's own `ShopifyOrderCancellationExecutorTests.cs` convention) that deserves its own, separately-scoped slice.

---

## 8. Explicitly OUT of scope for this slice

- **Ownership/ground-truth lookup (step 10)** — new `IShopifyOrderLookupService`+`IOrderOwnershipAuthorizationService` dependencies, the first external Shopify HTTP call, the first infra-failure-uncaught-exception branch this class would need to exercise; deferred entirely.
- **Action-hash recompute (step 11)** and **idempotency-key derivation (step 12)** — deliberately deferred, per §6's collapse-risk note and per their tight coupling to the atomic claim (step 13) they exist only to feed.
- **The atomic transaction, `Approved→Consumed` claim, `OrderCancellation` row creation, and everything after (steps 13–16)** — untouched.
- ~~Resolving §5a's open question~~ — **RESOLVED by explicit owner decision, no longer out of scope**: step 9's refusal writes an `AuditLog` row, matching steps 6/7's shape (§5a, §7).
- **Any correction to the `Pending`-row-guard "not yet built" premise beyond noting it in this document (§4)** — the guard itself needs no work; nothing to build.
- **No constructor/DI change beyond `ILevel2VerificationChecker`** — adding unused parameters for steps 10+ ahead of actually needing them is premature scope expansion this slice must not do.
- **No change to `ExecutionOutcome`, `ActionHasher`, `ApprovalExecutionKeyDeriver`, `IdempotencyKeyDeriver`, `ToolArgumentCanonicalizer`, or `OrderCancelProposalTool`** — all already-locked/already-verified, not touched by step 9.
- **No resolution of any open `OWNER DECISION REQUIRED`/`REQUIRES VERIFICATION` item** named in §2/§5 (TOCTOU race-loss folding, `ExecutionIntentId` naming, `Refused.Detail` shape, infra-failures-as-exceptions confirmation for step 10, one-executor-vs-generic-dispatcher cardinality, Shopify's exact rejection shape/Problem B) — all remain exactly as open as before this slice.
- **No correction to `NIVORA_SCHEMA_AND_LOGIC_REFERENCE.md`'s already-flagged drifts** (the advisory-lock claim in §3.4, the ownership "hard blocker" framing) — still flagged for a future documentation-focused session, not this implementation slice.
- **No change to any of the 8 still-open `Enum.TryParse`/`Enum.IsDefined` sites** — a separate, unrelated track.

---

## 9. Summary classification table

| Item | Classification |
|---|---|
| Step 9 (Level 2 check) requirement | **LOCKED** (Decision 2) |
| Step 9's audit-write question | **LOCKED (owner-decided)** — source itself does not settle it (confirmed: none of the three anchors that resolved steps 6/7/11 apply), but the owner explicitly decided step 9 writes an `AuditLog` row matching steps 6/7's shape (§5a) |
| Step 10 (ownership) requirement | **LOCKED**; infra-failures-as-exceptions convention **OWNER DECISION REQUIRED**, unresolved |
| Step 11 (hash recompute) requirement | **LOCKED**; detection scope **ENGINEERING-DETERMINED, clarifying** |
| Step 12 (idempotency-key derivation) | **LOCKED**, mechanical |
| Step 13 (atomic claim + insert) | **ENGINEERING-DETERMINED** (resolved `step-24` §4); TOCTOU-folding **OWNER DECISION REQUIRED**, unresolved; Pending-row guard it depends on **already built** (§4, corrected premise) |
| Step 14 (ground-truth check) | **LOCKED** |
| Step 15 (mutation call + remap) | **LOCKED** requirement / **ENGINEERING-DETERMINED** remap; Problem B **EXTERNAL VERIFICATION REQUIRED**, unresolved, non-blocking |
| Step 16 (reconciliation) | Out of scope, unchanged, already shipped |
| `orderCancel` async-job handling (§2a) | **LOCKED, already resolved by already-shipped infrastructure** — no new mechanism needed or proposed |
| `NoAutomaticRetry` domain boundary (§2b) | Begins at step 13; steps 9–12 outside it; the proposed slice (step 9 alone) respects it trivially, by construction |
| Owner-decision re-derivation (§3) | **9 adopted + 1 deferred**, re-confirmed from §0 **and** §4.5; none blocks steps 9–16 |
| `OrderCancellation` Pending-row guard status (§4) | **CORRECTED — already built** (Step 23Q→23T), not "designed but not yet built" |
| `ExecutionOutcome` shape (§5) | **LOCKED**, unchanged; its 4 carried-forward `OWNER DECISION REQUIRED` items remain unresolved, none blocks step 9 specifically |
| Staff-actor-identity gap (§5) | Still open; does not itself block step 9, since §5a's own question (whether step 9 needs a write at all) is separately unresolved |
| Four-identity-concept separation (§6) | **LOCKED** reasoning unchanged; none touched by the proposed slice |
| Proposed slice boundary (§7/§8) | **ENGINEERING-DETERMINED** — step 9 alone, justified by dependency weight, transaction/side-effect boundary, and testability, mirroring `step-38`'s own methodology |

---

## 10. Files inspected this pass

`backend/Services/OrderCancelApprovedActionExecutor.cs` (full, via direct `cat`); `docs/handoffs/step-38-cancelorder-execute-next-slice-design.md` (full, both correction passes' final text); `docs/handoffs/step-24-cancelorder-executor-design.md` (full, both halves, re-confirmed via targeted re-reads this session plus prior full reads earlier in this same continuing session); `docs/handoffs/step-22z-approval-proposal-approval-lifecycle-design.md` (§10, §16, §18, re-confirmed); `backend/Services/IApprovedActionExecutor.cs` (full, fresh); `backend/Services/ApprovalExecutionKeyDeriver.cs`, `backend/Services/ActionHasher.cs`, `backend/Services/IdempotencyKeyDeriver.cs`, `backend/Models/Approval.cs` (all re-confirmed unchanged from prior full reads this session); `backend/Services/OrderCancellationService.cs` (full, fresh — Pending-guard confirmation); `backend/Data/AppDbContext.cs` (`OrderCancellation` entity configuration block, fresh grep); `backend/Models/OrderCancellation.cs` (full, fresh — async-job doc comment); `NIVORA_SCHEMA_AND_LOGIC_REFERENCE.md` (§2 `OrderCancellation` entry, §3.4, fresh); `docs/handoffs/step-22-ai-action-safety-model-owner-decisions.md` (§0 and §4.5, both re-read fresh this pass, not just §0); `backend/Program.cs` (grepped fresh for every DI registration named in §1/§2/§7). Commands run: `git rev-parse HEAD` (start and end), `git status --porcelain`, `ls docs/handoffs/ | sort -V` (next step number), `grep` for `PendingGuardIndexName`/the Pending-row unique index, `grep` for `ILevel2VerificationChecker`/`IOrderOwnershipAuthorizationService` call sites (repository-wide, excluding definitions/DI).

---

## 11. Stop-rule compliance and final git status

No `.cs`, `.csproj`, migration, or any other source file was created or modified by this pass. No test was run (design-only, no build/test verification is applicable to a document-only change). `git rev-parse HEAD` at the end of this pass: `6aa1090fa7d1f435853c53c3fed8514fcf392b38` — unchanged. `git status --porcelain` gains exactly one new untracked file, `docs/handoffs/step-39-cancelorder-execute-steps-9-16-next-slice-design.md` (this document) — every other line is identical to the pre-pass baseline (110 lines, same set this track carried before this pass began).
