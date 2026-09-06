# Step 45 — Staff Actor Identity Threading Design

**DESIGN PASS ONLY — NO IMPLEMENTATION.** No `.cs`, `.csproj`, migration, or any other source file was created or modified by this pass. Every claim below was re-derived by directly reading live source and the cited handoff documents this session. The only artifact this pass creates is this document.

**Scope, as instructed**: how the executing staff member's identity enters `IApprovedActionExecutor.ExecuteApprovedActionAsync`, how it's authorized, how it's recorded in the `AuditLog` writes steps 6–13 already make, and how it's bound to execution. This document does **not** redesign `ExecutionOutcome` (locked at step-44), does **not** redesign the `Approval` claim/transaction mechanics (locked at step-43), and does **not** specify any Shopify-mutation behavior belonging to steps 14–16.

---

## 0. Starting state, verified

`git rev-parse HEAD`: `b05d8327765e3f11fbabd9d7ae85404c727c3c02` — confirmed at the start of this pass, matches the task's stated expectation. Unchanged throughout (re-checked before writing this file).

Repository-wide `grep` for `IApprovedActionExecutor`/`OrderCancelApprovedActionExecutor`, re-run this pass: the only matches are `OrderCancelApprovedActionExecutor.cs` (the implementation), `IApprovedActionExecutor.cs` (the interface), `Program.cs` (DI registration only, line 335), and incidental doc-comment mentions in `OrderCancelProposalTool.cs`/`ToolArgumentCanonicalizer.cs`/`IActionProposalTool.cs`/`ApprovalExecutionKeyDeriver.cs`. **`step-44` §0's finding still holds, unchanged**: `ExecuteApprovedActionAsync` has zero production call sites. `Glob` for `ApprovalController`/`ApprovalsController` confirms no such controller exists anywhere in `backend/`. There is no staff-facing HTTP endpoint for approve, deny, or execute today.

---

## 1. Sources read this pass

`backend/Services/OrderCancelApprovedActionExecutor.cs` (full, current, post-step-43 — every `AuditLog` write site read directly, not summarized from any handoff); `backend/Services/IApprovedActionExecutor.cs` (full); `backend/Enums/ActorType.cs` (full, its doc comment read verbatim); `backend/Models/Approval.cs` (full); `backend/Auth/NivoraPolicies.cs`, `SameTenantAuthorizationHandler.cs`, `ClaimsCurrentTenantAccessor.cs`, `NivoraClaimsPrincipalFactory.cs`, `RoleNames.cs` (full, all five); `backend/Services/IAuditLogService.cs` (full); `backend/Constants/AuditEventTypes.cs`, `AuditEntityTypes.cs` (full, both); `backend/Services/IOrderCancellationService.cs` (full — confirmed no actor/staff-id parameter exists on `RequestCancellationAsync`, and this document does not propose adding one); `backend/Services/WidgetConversationService.cs` (the `ActorType.NivoraSystem` call site the executor's own doc comment cites as precedent — read directly to verify the analogy, see §4c). `docs/handoffs/step-22z-approval-proposal-approval-lifecycle-design.md` (full — §5 state-machine guards, §6 approval authority, §10 the 16-step ordering including steps 1/2/5, §18 audit requirements); `docs/handoffs/step-38-cancelorder-execute-next-slice-design.md` (full — the prior pass that first flagged the actor-identity gap as unresolved and explicitly deferred it); `docs/handoffs/step-44-execution-outcome-general-contract-design.md` (full — for the zero-call-sites confirmation and to avoid touching anything it locked). `grep`-confirmed the staff-`UserId`-in-`AuditLog` pattern's every existing call site: `TicketsController.cs`, `TagModeMappingsController.cs`, `IntegrationsController.cs`, `CustomersController.cs`, `NotificationsController.cs` — each with its own private `CurrentUserId` property.

---

## 2. Item 1 — Whose identity, structurally: the approver, or any authorized staff member?

**LOCKED — any currently-authorized staff member, not necessarily the one who approved.**

Traced to two independent, mutually-reinforcing sources, neither reopened by this pass:

**(a) The already-shipped `Approved→Consumed` guard has no `ApprovedBy` predicate.** `OrderCancelApprovedActionExecutor.cs` lines 278–282 (step 13, `b05d832`, locked at step-43): `db.Approvals.Where(a => a.Id == approvalId && a.Status == ApprovalStatus.Approved && a.ExpiresAt > now).ExecuteUpdateAsync(...)`. This is the entire authorization surface of the claim itself — it checks `Id`, `Status`, `ExpiresAt`, nothing else. `Approval.ApprovedBy` (the staff member who approved) is never read by this guard and never compared against anything. Adding such a check would mean reopening step-43's atomic claim mechanic, which this document's own scope forbids. **The absence of an `ApprovedBy` check is therefore not an open question this document can resolve differently — it is already the shipped, locked shape.**

**(b) The identical question was already asked and answered for `Deny`, and the reasoning transfers exactly.** `step-22z` §5, Decision 7: `Deny` is authorized for "any `OwnerOrAdmin`, not restricted to the original approver." `Approve` and `Deny` share one authorization boundary (`OwnerOrAdmin` + `SameTenant`, §5/§6); nothing in `step-22z` ever proposes a *different*, narrower boundary specifically for `Execute` that would single out the original approver. `Execute`'s own guard (a) confirms this was never built.

**Consequence**: whoever is threaded into `Execute` as "the executing staff member" is resolved fresh at request time (authenticated cookie session, `OwnerOrAdmin`, `SameTenant`) — it is never required to equal, and is never compared against, `Approval.ApprovedBy`. This is a statement about who is *authorized* to trigger execution, not who Nivora *records* as having triggered it — those are separate questions, addressed in §4/§5 below.

---

## 3. Item 2 — Signature change vs. injected accessor

**ENGINEERING-DETERMINED, recommended for OWNER DECISION sign-off (it modifies a signature multiple prior passes describe as locked) — see §9.**

**Recommendation: an explicit, non-nullable `Guid executingStaffUserId` parameter appended to `IApprovedActionExecutor.ExecuteApprovedActionAsync`, not an injected accessor.**

Two existing patterns in this codebase carry identity into a service call, and they are not interchangeable — this design must pick the one that actually matches what's being threaded, not the one that merely looks architecturally similar:

- **`ICurrentTenantAccessor` (injected accessor)**: `ClaimsCurrentTenantAccessor` wraps `IHttpContextAccessor`, resolves `BusinessId` from `HttpContext.User`, and is injected into classes that need tenant scoping *implicitly and pervasively* — the EF Core global query filter (`AppDbContext`, every query, every service, without each one asking for it explicitly) and `SameTenantAuthorizationHandler` (a generic, resource-shape-agnostic authorization handler that cannot know which specific caller is invoking it). The accessor pattern exists specifically because tenant scoping must apply uniformly and silently across code that has no other reason to know about the current request.
- **Staff `UserId` (explicit parameter passing)**: `grep`-confirmed, 5-for-5, with no exception anywhere in this codebase: `TicketsController`, `TagModeMappingsController`, `IntegrationsController`, `CustomersController`, `NotificationsController` each declare their own private `CurrentUserId` property (`User.FindFirstValue(ClaimTypes.NameIdentifier)`, parsed to `Guid`, throwing if unparseable) and pass its *value* explicitly into whatever call needs it — `auditLogService.LogAsync(..., changedByUserId: CurrentUserId, ...)`, `.SetProperty(t => t.ClaimedByUserId, CurrentUserId)`, etc. **No injectable staff-identity accessor exists anywhere in this codebase today** — not a partial one, not a deprecated one. Every one of the five controllers resolves the value itself and threads it down as data, never as a service dependency.

`IApprovedActionExecutor`'s existing constructor (`AppDbContext`, `IAuditLogService`, `ILevel2VerificationChecker`, `IShopifyOrderLookupService`, `IOrderOwnershipAuthorizationService`, `IOrderCancellationService`) has zero `IHttpContextAccessor`-touching dependencies today — introducing one now, solely to mirror `ICurrentTenantAccessor`'s shape, would add a new categorical dependency this class has deliberately never needed, to resolve a value the calling layer already necessarily has on hand at the moment it constructs the call (the not-yet-built controller must already read the staff `UserId` off `User` to satisfy `[Authorize(Policy = OwnerOrAdmin)]` and the `SameTenant` check, per §4 below — resolving it a second time via an injected accessor inside the executor would be redundant, not simpler).

**Why this doesn't reopen the interface doc comment's "accepts no other parameter" rule**: `IApprovedActionExecutor.cs`'s doc comment (lines 149–155) forbids accepting `BusinessId`/`CustomerId`/`ToolName`/`ResourceId`/`CanonicalArguments`/`ConversationId`/`ActionHash` as caller-supplied values specifically because each of those is *compared against* a value already stored on the `Approval` row — accepting a caller-supplied copy would create two disagreeing sources of truth for a value the hash-comparison mechanism depends on. `executingStaffUserId` is categorically different: it is not compared against anything stored on `Approval` (there is no `ExecutedBy` column, §5 below), it does not participate in `ActionHasher.Derive`'s six-value tuple (§9 of `step-22z`, unchanged, not reopened), and its only use is as an opaque value written into `AuditLog.ChangedByUserId`. Accepting it as a parameter does not recreate the dual-source-of-truth risk that doc comment guards against.

**Exact proposed shape**: `Task<ExecutionOutcome> ExecuteApprovedActionAsync(Guid approvalId, Guid executingStaffUserId, CancellationToken cancellationToken)`. Non-nullable, per §7 below.

---

## 4. Item 3 — Authorization for triggering execution

**LOCKED — same tier as Approve/Deny (`OwnerOrAdmin` + `SameTenant`), enforced entirely upstream of `IApprovedActionExecutor`, not inside it.**

`step-22z` §10's 16-step ordering (re-read directly, quoted in `step-38` §2 verbatim) places role authorization at **step 5**: `"5. Verify role authorization (OwnerOrAdmin)"` — alongside step 1 (`"Authenticate staff (existing cookie auth)"`) and step 2 (`"Resolve tenant (ICurrentTenantAccessor)"`). `step-38` §4 item 3 independently confirms: *"Agent approval authority... governs steps 1/2/5, outside `Execute` entirely."* `step-38` §2's own table header states plainly: *"Steps 1/2/5 are outside `Execute` (ASP.NET Core `[Authorize]` middleware)."*

This is the identical authorization boundary `NivoraPolicies.cs`'s own doc comment already describes for every protected operation in this codebase (`[Authorize]` for authentication → `ICurrentTenantAccessor` for tenant → `SameTenant` policy for resource-tenant binding → role-tier policy for permission) and the identical boundary `step-22z` §5/§6 already locks for `Approve`/`Deny` (`NivoraPolicies.OwnerOrAdmin` + explicit `IAuthorizationService.AuthorizeAsync(User, approval, SameTenant)`). **No additional or stricter role tier is required for execution than for approval** — no document proposes one, and `step-22z` §6's "Approval authority" section makes no distinction between the authority to approve and the authority to execute an already-approved row.

**Consequence for the not-yet-built controller** (out of this document's scope to fully design, since the controller itself doesn't exist — noted for the next implementation pass to follow, not designed here): the `Execute` endpoint must carry `[Authorize(Policy = NivoraPolicies.OwnerOrAdmin)]`, must load the `Approval` (or otherwise establish `SameTenant`) before invoking `IApprovedActionExecutor`, and must resolve `executingStaffUserId` the same way the five existing controllers resolve `CurrentUserId` — `User.FindFirstValue(ClaimTypes.NameIdentifier)`. **`IApprovedActionExecutor` itself performs zero authorization of its own** — consistent with steps 1/2/5 being explicitly outside its 16-step responsibility, and consistent with the executor's already-implemented steps starting at step 6, never re-implementing 1–5.

---

## 5. Item 4 — Populating `AuditLog.Actor`/`ChangedByUserId` at each existing write site (the crux)

**Every current write site uses the identical placeholder — confirmed by direct read, not assumed from the "NivoraSystem/null throughout" characterization.**

`OrderCancelApprovedActionExecutor.cs`, re-read in full this pass, has exactly **two distinct call sites** to `IAuditLogService.LogAsync`:

1. **`WriteUnauthorizedExecutionBlockedAsync`** (lines 381–389), a private helper reused at five locations: step 6 refusal (line 113, `ApprovalNotApproved` on the initial `Status != Approved` check), step 7 refusal (line 125, `ApprovalExpired`), step 9 refusal (line 175, `Level2VerificationFailed`), step 10 refusal (line 202, `OwnershipVerificationFailed`), step 11 refusal (line 225, `ActionHashMismatch`). Every one of these five calls passes `ActorType.NivoraSystem, changedByUserId: null` — confirmed by reading the helper's single implementation, not by inference.
2. **The inline "execution claimed" write** (lines 300–303, step 13, inside the atomic transaction), using `AuditEventTypes.ExecutionClaimed, ActorType.NivoraSystem, changedByUserId: null`.

**Two step-13 sub-paths deliberately write nothing at all**, already locked by the step-43 addendum owner decision, not reopened here: the claim-loss branch (`claimed == 0`, line 284–289) and the `DuplicatePendingOrderCancellationException` catch (line 320–331) — both are ordinary concurrency handling between legitimate callers, not security refusals, and per step-43 §15b receive no `AuditLog` row regardless of what identity is available. **This document does not change that.**

**Step 8's `CustomerUnresolved` refusal (line 149–154) writes no `AuditLog` row today, and whether it should is explicitly out of this document's scope** — `step-38` §2a/§9 already classifies this as **NOT ESTABLISHED FROM SOURCE** (it is a data-availability failure, not a check on `Approval`'s own authorization state, and is not swept in by the same reasoning that locked steps 6/7's audit requirement). This document does not resolve whether step 8 should audit; it only specifies, in §5b below, what actor value step 8 would carry *if* a future pass decides it should.

### 5a. The current placeholder's own justification, checked against `ActorType`'s actual semantics

The helper's doc comment (lines 364–375) defends `NivoraSystem`/`null` as deliberate, not an oversight: *"this determination is made by Nivora's own backend logic, not a captured human/AI/customer identity."* Checked directly against `ActorType.cs`'s own doc comment (the canonical authority for this enum, re-read verbatim this pass): *"Records the origin of the request that caused Nivora's backend to act — never who 'did' it."*

**These are two different questions, and the executor's comment answers the wrong one.** "Who determined the outcome" (Nivora's backend logic evaluating `if (approval.Status != ApprovalStatus.Approved)`) is true of literally every write this codebase makes — it is equally true of `TicketsController`'s `ClaimedByUserId`/`AuditLog` writes, which nonetheless correctly use `ActorType.NivoraBusinessStaff` + a real `CurrentUserId`, because `ActorType` records **the origin of the request**, not who performed the final `if`-check. For every one of the five refusal sites and the `ExecutionClaimed` write, **the origin of the request is, today and for every anticipated caller, an authenticated staff member's HTTP call** (§7 below) — never a scheduled job, never `BusinessInternalSystem`, never `Ai` (which cannot reach `Execute` at all, per `step-22z` §7/§15). `NivoraSystem`'s own doc-comment definition — *"a scheduled/background job (retention cleanup, reconciliation) triggered the action"* — does not describe any of these six call sites; none of them is triggered by a scheduled job.

### 5b. The cited precedent (`WidgetConversationService.cs`) does not actually support the current placeholder

The helper's doc comment cites `WidgetConversationService.cs`'s own `ActorType.NivoraSystem` usage as "exactly that shape of event." Read directly this pass (line 242): that call fires when a **customer's** reply triggers a staff-notification fan-out — the origin is arguably the customer, arguably Nivora's own notification-dispatch logic deciding to notify, but in no reading is it a staff member's own request. This is genuinely a case with no clean human origin to attribute. **It is not analogous to `Execute`'s refusal/claim writes**, where a specific, identifiable, currently-authenticated staff member's own HTTP request is the one and only reason the code path ran at all. The analogy the current comment draws does not hold up under direct re-read.

### 5c. Corrected assignment — one rule, applied uniformly

**OWNER DECISION (recommended, not silently applied) — see §9**: once `executingStaffUserId` is threaded in per §3, **all six current call sites** (the five `WriteUnauthorizedExecutionBlockedAsync` invocations and the `ExecutionClaimed` write) should change from `ActorType.NivoraSystem, changedByUserId: null` to **`ActorType.NivoraBusinessStaff, changedByUserId: executingStaffUserId`** — no exceptions, no per-step variation. This is not six independent decisions; it is one rule (`Actor` records request origin; the origin of every one of these six writes is the executing staff member's own authenticated request) applied identically everywhere it appears in this class, matching the uniform treatment `TagModeMappingsController`/`TicketsController`/`CustomersController`/`IntegrationsController` already give this exact `ActorType`/`ChangedByUserId` pair for their own staff-triggered writes.

**If step 8 (`CustomerUnresolved`) is ever given an `AuditLog` write by a future pass** (§5, not decided here), it would follow the identical rule for the identical reason — its origin is the same staff request as every other step in this method — but this document does not decide *whether* step 8 gets a write, only what actor it would carry if one is added.

**This is a correction to shipped, committed code** (`b05d832`), not new-feature design — flagged explicitly as reversing a comment that states "this is deliberate, not an oversight." Per this project's standing practice, a deliberate prior justification is not silently overridden even when a fresh, direct re-read finds it unsound — hence OWNER DECISION, not LOCKED, despite the reasoning above being source-derived and, in this author's assessment, conclusive.

---

## 6. Item 5 — Durable capture beyond `AuditLog`

**LOCKED (out of scope by this task's own explicit boundary) — `AuditLog` is the sole and sufficient durable record; no new column may be added by this design.**

`Approval.cs` (re-read in full, §3 above) has `ApprovedBy`/`DeniedBy` — both nullable `Guid` FKs populated by the `Approve`/`Deny` transitions — but **no `ExecutedBy`/`ConsumedBy` column**, and none is proposed here. Adding one would require: a new migration, a change to `AppDbContext`'s `Approval` entity configuration, and — critically — a change to step 13's already-locked atomic claim `ExecuteUpdateAsync` (currently `SetProperty(a => a.Status, ApprovalStatus.Consumed).SetProperty(a => a.ConsumedAt, now)`, per `step-43`, locked, not to be reopened by this pass). This document's own scope explicitly forbids redesigning the step-43 claim mechanics, so adding a durable `Approval`-level column for executing-staff identity is not an option this document can adopt regardless of its merits.

`OrderCancellation.cs` likewise has no analogous column, and adding one is equally out of scope for the identical reason (its schema is a separate, already-locked concern per `step-22`/`step-23` series, and this document does not touch tool-specific execution-intent schemas).

**Consequence**: once §5c's correction is adopted, `AuditLog`'s `ExecutionClaimed` row (step 13) becomes the one durable, queryable record of which staff member's request caused a given `Approval` to be consumed — `ChangedByUserId` on that specific row, joined to `AuditLog.EntityId == approval.Id.ToString()`. This is a real, if minor, ergonomic gap relative to a dedicated column (a staff-facing UI wanting "who executed this" must query `AuditLog` rather than reading it directly off `Approval`), but it is the only mechanism this document's scope permits, and it is sufficient — nothing in any prior document requires a structured, directly-joinable column for this value, only that it be durably recorded somewhere (`step-22z` §18's own audit-requirements list, unweakened).

---

## 7. Item 6 — Null/unauthenticated identity and non-human execution triggers

**LOCKED — non-nullable parameter; non-human execution triggers are explicitly out of scope, not merely unhandled.**

Two independent lines of evidence, neither requiring a new decision:

**(a) Every anticipated caller is an authenticated staff HTTP request, by explicit prior design, not by omission.** `IApprovedActionExecutor.cs`'s own doc comment: *"resolved only by the staff-facing execution endpoint's own DI scope."* `step-22z` §7/§15: `IActionProposalTool` structurally cannot reach any execution-shaped member — *"mutation capability first becomes reachable only inside the staff-triggered `Execute` operation... never from anything `ToolDispatchService`'s AI-facing tool collection can resolve."* No document anywhere — across the entire step-19 through step-44 series — proposes a background-job-triggered or system-triggered call to `ExecuteApprovedActionAsync`. Reconciliation (step 16) operates entirely independently, polling `OrderCancellation` rows directly (`step-44` §4b, locked, re-confirmed: it never calls `IApprovedActionExecutor` at all).

**(b) The existing codebase precedent for this exact shape already chooses non-nullable-and-throw, not nullable-and-branch.** Every one of the five existing `CurrentUserId` controller properties (`TicketsController.cs` line 81 quoted directly: *"Authenticated but no parsable user id claim — unreachable behind `[Authorize]`"*) throws rather than returning a nullable value, on the reasoning that reaching this code at all without a valid identity would mean the `[Authorize]` middleware itself failed — an infra-level bug, not a business scenario the calling code should branch on. `Execute`'s future controller should follow the identical convention: resolve `executingStaffUserId` the same way, non-nullably, and let an authorization-middleware failure surface as an exception rather than as a designed `ExecutionOutcome` case.

**Consequence**: `executingStaffUserId : Guid` (non-nullable) on `ExecuteApprovedActionAsync`'s signature, per §3. This document does **not** design a code path for a null/system-triggered execution, because no such path is anticipated by any existing design document, and inventing one now would be speculative scope this task's own instructions warn against ("classify honestly rather than assuming"). If a future tool or automation tier ever needs a genuinely non-human execution trigger, that would require its own owner decision and its own design pass — not a `null` fallback bolted onto this signature.

---

## 8. Explicit confirmation: what this document does NOT specify

- `ExecutionOutcome`'s shape, cases, or data (locked at `step-44`) — untouched.
- The `Approval` claim/transaction mechanics, the atomic `Approved→Consumed` guard, or `OrderCancellation` row creation (locked at `step-43`) — untouched. `executingStaffUserId` is never written into any `WHERE` clause or `SetProperty` call anywhere in step 13's transaction.
- Any Shopify-mutation behavior belonging to steps 14–16 — untouched.
- The actual `Approve`/`Deny`/`Execute` controller's full implementation — its existence and required policy gates are described (§4) since they're the direct authorization context this design depends on, but its full route/DTO/error-handling shape is a separate, not-yet-scoped implementation task.
- Whether step 8 (`CustomerUnresolved`) should receive an `AuditLog` write at all (§5) — a distinct, already-flagged, still-open question this document does not resolve, only extends with "if yes, what actor" (§5c).
- Any change to `ActionHasher.Derive`'s six-value tuple or `ApprovalExecutionKeyDeriver.Derive`'s five-value tuple — `executingStaffUserId` is not a hash input for either, and this document does not propose making it one (staff identity is not part of "what was approved," per §3's own reasoning distinguishing it from the interface's hash-relevant fields).

---

## 9. Owner decisions requiring sign-off

| # | Decision | Recommendation | Status |
|---|---|---|---|
| 1 | Add `Guid executingStaffUserId` as a new, non-nullable parameter to `IApprovedActionExecutor.ExecuteApprovedActionAsync` (and `OrderCancelApprovedActionExecutor`'s implementation) | **Adopt** — explicit parameter, not an injected accessor (§3); non-nullable (§7) | **OWNER DECISION REQUIRED** — modifies a signature multiple prior passes (`step-22-ai-action-safety-model-concrete-contract-design.md` §3, re-verified `step-28` §1) describe as locked |
| 2 | Correct all six existing `AuditLog` write sites in `OrderCancelApprovedActionExecutor.cs` from `ActorType.NivoraSystem, changedByUserId: null` to `ActorType.NivoraBusinessStaff, changedByUserId: executingStaffUserId` | **Adopt** — uniformly, no per-step variation (§5c) | **OWNER DECISION REQUIRED** — reverses a comment in shipped code (`b05d832`) that states this placeholder is "deliberate, not an oversight" |

**Not requiring a decision — already resolved by citation, not reopened**:
- Whose identity, structurally (§2) — LOCKED, any currently-authorized staff member, via the already-shipped guard's absence of an `ApprovedBy` check plus Decision 7's identical reasoning.
- Authorization tier for triggering execution (§4) — LOCKED, `OwnerOrAdmin` + `SameTenant`, entirely upstream of the executor, per `step-22z` §10 step 5.
- Durable capture beyond `AuditLog` (§6) — LOCKED, out of scope by this task's own boundary; `AuditLog` is sufficient and the only permitted mechanism.
- Null/non-human execution triggers (§7) — LOCKED, non-nullable parameter; genuinely out of scope per every existing design document.

**Non-blocking items, not resolved by this pass, not silently downgraded**:
- The full `Execute` controller's own design (route, DTO, error mapping) — not yet scoped by any document; this pass only describes the authorization gates it must carry (§4).
- Whether step 8 (`CustomerUnresolved`) should receive an `AuditLog` write at all — remains **NOT ESTABLISHED FROM SOURCE**, per `step-38` §9, unchanged by this pass.

---

## 10. Summary classification table

| Item | Classification |
|---|---|
| Whose identity, structurally (approver vs. any authorized staff) | **LOCKED** — any currently-authorized `OwnerOrAdmin`, via the shipped guard's absence of an `ApprovedBy` check + Decision 7 |
| Signature mechanism (parameter vs. injected accessor) | **ENGINEERING-DETERMINED** — explicit non-nullable parameter, matching the 5-for-5 existing `CurrentUserId` precedent |
| Signature change itself (adding the parameter) | **OWNER DECISION REQUIRED** — modifies a previously-locked signature |
| Authorization tier for triggering execution | **LOCKED** — `OwnerOrAdmin` + `SameTenant`, upstream of the executor, per `step-22z` §10 step 5 |
| `AuditLog` actor/user-id correction at all six write sites | **OWNER DECISION REQUIRED** — reasoning is source-derived and conclusive, but reverses a deliberate prior comment in shipped code |
| Whether step 8 gets an `AuditLog` write at all | **NOT ESTABLISHED FROM SOURCE** — unchanged from `step-38`, not this document's to resolve |
| Durable capture beyond `AuditLog` | **LOCKED** — out of scope by this task's own boundary (would reopen step-43); `AuditLog` is sufficient |
| Null/unauthenticated/non-human execution trigger | **LOCKED** — non-nullable parameter; explicitly out of scope, not merely unhandled |

---

## 11. Files inspected this pass

`backend/Services/OrderCancelApprovedActionExecutor.cs` (full); `backend/Services/IApprovedActionExecutor.cs` (full); `backend/Enums/ActorType.cs` (full); `backend/Models/Approval.cs` (full); `backend/Auth/NivoraPolicies.cs`, `SameTenantAuthorizationHandler.cs`, `ClaimsCurrentTenantAccessor.cs`, `NivoraClaimsPrincipalFactory.cs`, `RoleNames.cs` (full, all five); `backend/Services/IAuditLogService.cs` (full); `backend/Constants/AuditEventTypes.cs`, `AuditEntityTypes.cs` (full, both); `backend/Services/IOrderCancellationService.cs` (full); `backend/Services/WidgetConversationService.cs` (the cited `NivoraSystem` call site, read directly); `backend/Controllers/TicketsController.cs` (the `CurrentUserId` property, read directly). `docs/handoffs/step-22z-approval-proposal-approval-lifecycle-design.md` (full); `docs/handoffs/step-38-cancelorder-execute-next-slice-design.md` (full); `docs/handoffs/step-44-execution-outcome-general-contract-design.md` (full). Commands run: `git rev-parse HEAD` (start); repository-wide `grep` for `IApprovedActionExecutor`/`OrderCancelApprovedActionExecutor` call sites (zero production sites found, confirming `step-44` unchanged); `grep` for `CurrentUserId`/`NivoraClaimTypes`/`ICurrentUserAccessor` (confirmed no injectable staff-identity accessor exists anywhere); `Glob` for `ApprovalController`/`ApprovalsController` (none found).

---

## 12. Stop-rule compliance and final verdict

No `.cs`, `.csproj`, migration, or any other source file was created or modified by this pass. No test was run (design-only). `git rev-parse HEAD`, re-checked at the end of this pass: `b05d8327765e3f11fbabd9d7ae85404c727c3c02` — unchanged.

Two items in §9 are genuinely unresolved by this pass alone — not because the underlying reasoning is ambiguous (both are traced to a specific, checkable source-derived conclusion in §3/§5), but because both reverse or modify something a prior pass explicitly marked locked or deliberate, and this project's standing practice (matching `step-43`/`step-44`'s own treatment of their own owner decisions) is that such reversals require explicit sign-off in this same conversation before being treated as adopted, not silently applied on the strength of this document's own reasoning.

```text
IMPLEMENTATION BLOCKED — DESIGN INCOMPLETE
```

**What's missing, precisely**: explicit sign-off on the two OWNER DECISION rows in §9 — (1) adding `executingStaffUserId` to `IApprovedActionExecutor`'s signature as a plain parameter, and (2) correcting all six existing `AuditLog` write sites from `NivoraSystem`/`null` to `NivoraBusinessStaff`/`executingStaffUserId`. Both are fully specified above, with nothing ambiguous left in either — this is a request for confirmation, not a request for further design work. Once both are confirmed, this document's status becomes `DESIGN COMPLETE — READY FOR INDEPENDENT VERIFICATION`, matching the `step-43`/`step-44` pattern, via an addendum to this same file rather than a new one.

---

## 13. Addendum — owner sign-off received, pre-implementation reachability re-check, and implementation

**Owner sign-off received, both decisions adopted as specified in §9, no changes to either**:
1. `Guid executingStaffUserId` (non-nullable) added to `IApprovedActionExecutor.ExecuteApprovedActionAsync`'s signature.
2. All six `AuditLog` write sites in `OrderCancelApprovedActionExecutor.cs` corrected from `ActorType.NivoraSystem, changedByUserId: null` to `ActorType.NivoraBusinessStaff, changedByUserId: executingStaffUserId`.

### 13a. Pre-implementation reachability re-check, per the owner's explicit instruction

Before locking in the non-nullable signature, every implementation of `IApprovedActionExecutor` (not just `OrderCancelApprovedActionExecutor`) was re-checked for reachability from a non-HTTP context. Repository-wide `grep` for `IApprovedActionExecutor`/`class .*: .*IApprovedActionExecutor` across `backend/` and `backend.Tests/`, re-run at this pass's start (`HEAD` still `b05d832`, unchanged from §0/§11): **exactly one implementation exists — `OrderCancelApprovedActionExecutor`.** No test fake, mock, or stub implements the interface either (the test suite exercises the real class directly, confirmed by the same `grep` returning zero hits in `backend.Tests/` for a second implementing type). `Program.cs` registers it once, scoped, at line 335 — the only DI registration for this interface.

Its own reachability was independently re-confirmed unchanged from §0/§7: zero production call sites exist (no controller, no queued job, no webhook handler references `ExecuteApprovedActionAsync` or `IApprovedActionExecutor` anywhere); `OrderCancellationReconciliationService` — the only background/queued process in this codebase that touches `OrderCancellation` rows — never calls it, confirmed by `step-44` §4b (unmodified, not re-derived, since this design does not touch reconciliation).

**Result: no implementation could be invoked from a non-HTTP context, today or as anticipated by any existing design document. Nothing to flag back.** The non-nullable signature from §3/§7 is adopted as specified, with no exception carved out.

### 13b. Semantic cutover point — existing `AuditLog` rows are not a change in real behavior, and are durably distinguishable

Per the owner's explicit instruction, stated here rather than left implicit: any `AuditLog` row already written by the prior placeholder code (`EntityType = 'Approval'`, `EventType IN ('UnauthorizedActionBlocked', 'ExecutionClaimed')`, `Actor = 'NivoraSystem'`, `ChangedByUserId IS NULL`) reflects **a logging bug being corrected, not a real prior mode of operation that once genuinely lacked a staff identity.** Every one of those six call sites was always triggered by an authenticated staff member's own HTTP request (§5a) — the `NivoraSystem`/`null` value was never an accurate record of what happened, only a placeholder forced by the interface's prior signature having nowhere to carry the real identity. Nothing about *who actually took the action* changes at this cutover — only whether `AuditLog` accurately records it.

**How a future reader tells old rows from new ones, without re-deriving this from scratch**: the predicate `EntityType = 'Approval' AND EventType IN ('UnauthorizedActionBlocked', 'ExecutionClaimed') AND Actor = 'NivoraSystem' AND ChangedByUserId IS NULL` durably and exactly identifies every row written before this fix, for as long as `OrderCancelApprovedActionExecutor` remains the only code path that ever writes those two event types against that entity type (true as of this pass, confirmed by `grep` — `AuditEventTypes.ExecutionClaimed` and the `Approval`-entity-typed `UnauthorizedActionBlocked` writes have exactly one producer). After this fix, no code path can ever produce that exact combination again for a genuinely new event — every new row for these two event types on this entity type carries `Actor = 'NivoraBusinessStaff'` and a real `ChangedByUserId`. A query need not know a deploy date or commit hash to draw the line; the placeholder combination is self-identifying. **This durably-queryable distinction is recorded here because it is the only artifact this fix produces that makes the cutover legible — no schema change accompanies it**, consistent with §6's locked scope boundary against adding new columns. This note should be treated as the canonical reference for that distinction; a reader encountering old rows should cite this document rather than re-deriving the reasoning independently. As of this pass, no such historical rows are known to exist in the shared dev database (the feature has zero production call sites and every test run cleans up its own rows), but the distinction is recorded regardless, for whenever a real execution first occurs.

### 13c. Implementation performed

Strictly the identity-threading fix signed off above — no other file, and no other executor's internals, touched:

- `backend/Services/IApprovedActionExecutor.cs`: `ExecuteApprovedActionAsync`'s signature gained `Guid executingStaffUserId`, inserted between `approvalId` and `cancellationToken`. Doc comment extended to explain why this parameter doesn't reopen the interface's existing "no other parameter" rule (§3).
- `backend/Services/OrderCancelApprovedActionExecutor.cs`: `ExecuteApprovedActionAsync`'s own signature updated to match. All five call sites of the private `WriteUnauthorizedExecutionBlockedAsync` helper (steps 6, 7, 9, 10, 11) now pass `executingStaffUserId` through; the helper's own signature gained the parameter and its body now writes `ActorType.NivoraBusinessStaff, changedByUserId: executingStaffUserId`. The inline step-13 `ExecutionClaimed` write was corrected identically. The helper's doc comment was rewritten to remove the now-inaccurate "deliberate placeholder, not an oversight" framing and cite this document's §5 reasoning instead.
- `backend.Tests/OrderCancelApprovedActionExecutorTests.cs`: every one of the 15 pre-existing calls to `ExecuteApprovedActionAsync` updated to pass an `executingStaffUserId` argument. The 8 assertion blocks that checked `Actor`/`ChangedByUserId` updated from `ActorType.NivoraSystem`/`Assert.Null` to `ActorType.NivoraBusinessStaff`/`Assert.Equal(executingStaffUserId, ...)`. A new `CreateStaffUserAsync` fixture helper (mirroring `NotificationDataTests.cs`'s own identical-shaped helper — a direct `db.Users.Add`, not the full `UserManager.CreateAsync` path) seeds a real `ApplicationUser` row for every call that actually reaches an `AuditLog` write, and a `_userIdsToClean`-driven `DELETE FROM "AspNetUsers"` was added to `DisposeAsync`, sequenced after the existing `AuditLogs` delete and before `Businesses` — same ordering convention `NotificationDataTests.cs` already established.

**A real-schema fact surfaced only by this implementation pass, not previously stated in §0–§12 above**: `AuditLog.ChangedByUserId` carries a real foreign key to `AspNetUsers` (`FK_AuditLogs_AspNetUsers_ChangedByUserId`) — inert while the column was always `null`, but now enforced on every write these six call sites make. This is a pre-existing schema fact, not something this pass added or could have changed within its own scope; it only became observable once `ChangedByUserId` started carrying real values. Every test that reaches an `AuditLog` write (including the `SecondApprovalSameOrderWhilePending` test's first call, and its second call — which still adds an `ExecutionClaimed` row to the same tracked-context batch `RequestCancellationAsync` flushes even though that whole transaction is later rolled back by the duplicate-guard) now seeds a real backing user. This has no bearing on production: a real authenticated staff session's `Guid` always resolves to a real `AspNetUsers` row, so this constraint is expected to be satisfied trivially outside tests.

**Verification performed**: `dotnet build backend.Tests/backend.Tests.csproj` — 0 errors. `dotnet test backend.Tests/backend.Tests.csproj --filter "FullyQualifiedName~OrderCancelApprovedActionExecutorTests"` — 16/16 passed. Full suite, `dotnet test backend.Tests/backend.Tests.csproj` — 674 passed, 3 skipped (pre-existing, gated live-Claude-API tests, unrelated), 0 failed. `git rev-parse HEAD` before this implementation: `b05d8327765e3f11fbabd9d7ae85404c727c3c02` (this pass's edits are the first source changes on top of it).

**No other executor's internals, no `step-43`/`step-44` mechanics, and no claim-guard logic were touched** — confirmed by the file list above being exhaustive and by the diff being scoped exactly to the signature/audit-actor changes signed off in §9.

```text
DESIGN COMPLETE — READY FOR INDEPENDENT VERIFICATION
```

Per this project's standing process, the implementing session may not verify its own work — an independent pass should re-derive the reachability check (§13a), the six write-site corrections, and the test-fixture changes from source before this is treated as verified.

---

## 14. Independent verification — VERIFIED

A separate pass re-derived every claim in §13 directly from source, not from this document's own summary:

- **Signature** (§13, item 1): confirmed `Guid executingStaffUserId` (non-nullable) at `IApprovedActionExecutor.cs:175` and `OrderCancelApprovedActionExecutor.cs:90`; doc-comment reasoning present and coherent.
- **Reachability** (§13a): independently re-ran the repository-wide `grep` — exactly one implementation, one DI registration, zero test fakes. Personally read `OrderCancellationReconciliationService.cs` and `OrderCancellationReconciliationBackgroundService.cs` in full — neither references `IApprovedActionExecutor`. Confirmed no other `BackgroundService`/`IHostedService` does either. Nothing missed.
- **The 6 write sites**: checked individually — all 6 (5 via `WriteUnauthorizedExecutionBlockedAsync`, 1 inline `ExecutionClaimed`) confirmed `ActorType.NivoraBusinessStaff` / `changedByUserId: executingStaffUserId`, zero remaining `NivoraSystem`/`null`. Helper doc comment confirmed rewritten, no longer defends the old placeholder as deliberate.
- **Tests**: all 15 call sites checked — the 8 that reach an `AuditLog` write use a real seeded `executingStaffUserId` compared via genuine `Assert.Equal`, not a hardcoded value that would mask a wiring bug; the other 7 (paths that write no audit row) correctly use throwaway `Guid.NewGuid()`. `CreateStaffUserAsync` confirmed to perform a real `db.Users.Add` + `SaveChangesAsync` insert, not a bare Guid return — corroborated empirically by the FK failures that occurred before this helper existed.
- **Full suite**: run independently (not taken on faith). **Discrepancy found and disclosed, not softened**: the implementing session reported 674/3/0; independent runs produced 673/1/3 and 672/2/3, with failures confined to `KnowledgeRetrievalServiceTests`' HNSW-recall-sensitive tests — a pre-existing, already-documented flake (`step-35-hnsw-recall-test-triage.md`, `step-36-hnsw-recall-test-flake-fix.md`, the latter explicitly disclaiming it "cannot guarantee zero future flakiness"). `OrderCancelApprovedActionExecutorTests` itself passed 16/16 on every one of 4 independent runs. The discrepancy does not implicate this design's changes.
- **Addendum's cutover query** (§13b): confirmed exact, not approximate — `AuditLog.Actor` is persisted via `.HasConversion<string>()` (`AppDbContext.cs:110`), so `Actor = 'NivoraSystem'` is byte-accurate; the `EventType IN (...)` list matches the only two `AuditEventTypes` constants used at these 6 sites, confirmed to have no other producer anywhere in `backend/`.

**No item requires returning to the implementing session.**

```text
VERIFIED
```
