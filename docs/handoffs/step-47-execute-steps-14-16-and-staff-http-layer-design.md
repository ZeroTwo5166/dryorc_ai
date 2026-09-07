# Step 47 — Execute Steps 14–16 and the Staff-Facing Approve/Deny/Execute HTTP Layer, Design

**DESIGN PASS ONLY — NO IMPLEMENTATION.** No `.cs`, `.csproj`, migration, or any other source file was created or modified by this pass. Every claim below was re-derived by directly reading live source and the cited handoff documents this session. The only artifact this pass creates is this document.

**Scope, as instructed**: the two remaining genuinely-unbuilt pieces of the AI Action Safety Model pipeline — (a) `IApprovedActionExecutor.ExecuteApprovedActionAsync`'s Execute steps 14–16 (the pre-execution ground-truth check, the actual Shopify `orderCancel` mutation call, and reconciliation's relationship to both), and (b) the staff-facing HTTP surface for listing, approving, denying, and triggering execution of an `Approval` — since no such surface exists at all today. This document does not redesign anything already locked (`ExecutionOutcome` at step-44, the atomic claim mechanics at step-43, identity threading at step-45) and does not specify `refundOrder`/`updateCustomer` or any second write tool.

---

## 0. Starting state, verified

`git rev-parse HEAD`: `6393857e3b1aaec7c4e9fa460db44881e4f2f316` — confirmed at the start of this pass. `OrderCancelApprovedActionExecutor.ExecuteApprovedActionAsync`, re-read directly: still throws `NotImplementedException` immediately after step 13's atomic claim commits (line 345) — steps 14–16 are exactly as unbuilt as the prior reconciliation reported. Repository-wide `grep` for `ApprovalController`/`ApprovalsController`: zero matches, confirmed fresh this pass, not assumed from a prior session's finding — **no staff-facing HTTP surface for `Approval` exists anywhere in this codebase today.**

---

## 1. Sources read this pass

`backend/Services/OrderCancelApprovedActionExecutor.cs`, `IApprovedActionExecutor.cs` (full, current — confirming exactly where steps 1–13 leave off). `backend/Services/IShopifyOrderLookupService.cs`, `ShopifyOrderCancellationExecutor.cs`, `IOrderCancellationExecutor.cs` (full, all three — confirming what steps 14/15 actually have available to call). `backend/Services/OrderCancellationReconciliationService.cs`/`OrderCancellationReconciliationBackgroundService.cs` (re-confirmed already running independently, per step-44 §4b, unchanged). `backend/Models/Approval.cs`, `backend/Enums/ApprovalStatus.cs` (full — confirming exactly which columns exist and which don't, e.g. no denial-reason column). `backend/Auth/NivoraPolicies.cs`, `SameTenantAuthorizationHandler.cs`, `RoleNames.cs` (full, re-confirmed unchanged since step-45). `backend/Controllers/TicketsController.cs` (read in full for this codebase's own established controller-shape convention — two-layer tenant pattern, `Response`/`Request` record DTOs, cursor-paginated list). `backend/Constants/AuditEventTypes.cs`, `NotificationTypes.cs` (full, both — confirming exactly which constants exist and which don't). `docs/handoffs/step-22z-approval-proposal-approval-lifecycle-design.md` (full — the authoritative source of the 16-step ordering, §10, and the state machine, §4/§5/§6). `docs/handoffs/step-22-ai-action-safety-model-owner-decisions.md` (full — specifically §4.2/§4.7 on the Approve-vs-Execute timing question, and the nine adopted decisions). `docs/handoffs/step-24-cancelorder-executor-design.md` §"ordering table" (confirming steps 1/2/5 are explicitly "not `Execute`'s concern," refused by `[Authorize(Policy = NivoraPolicies.OwnerOrAdmin)]` middleware). `docs/handoffs/step-38-cancelorder-execute-next-slice-design.md` (full — the steps-6-16 table, §2, is the most precise prior statement of what steps 14/15 must each do). `docs/handoffs/step-44-execution-outcome-general-contract-design.md`, `step-45-staff-actor-identity-threading-design.md` (full, both — confirming `ExecutionOutcome`'s locked shape and the identity-threading contract this new controller must satisfy, not redesign).

---

## 2. Item 1 — Current state, precisely, and where steps 14–16 were originally defined

**LOCKED — the 16-step ordering's authoritative source is `docs/handoffs/step-22z-approval-proposal-approval-lifecycle-design.md` §10**, quoted verbatim (unchanged since first written, re-confirmed by every subsequent pass through `step-45`):

```text
1.  Authenticate staff (existing cookie auth)
2.  Resolve tenant (ICurrentTenantAccessor)
3.  Load Approval by ApprovalId — the only meaningful client input
4.  Verify tenant binding (explicit SameTenant check)
5.  Verify role authorization (OwnerOrAdmin)
6.  Verify Approval.Status == Approved
7.  Verify ExpiresAt > now (pure read; re-checked atomically inside step 13's guard)
8.  Re-derive CustomerId from current Conversation state
9.  Verify Level 2 for the freshly re-derived CustomerId
10. Revalidate customer ownership (live IShopifyOrderLookupService + IOrderOwnershipAuthorizationService call)
11. Recompute canonical arguments and ActionHash; compare byte-for-byte against Approval.ActionHash
12. Establish execution identity
13. ATOMIC: Approved→Consumed transition + execution-intent row creation (one DB transaction)
   ── transaction boundary ──
14. First action outside the transaction: pre-execution ground-truth check (GetCancellationStateAsync,
    reused unmodified). If cancelledAt != null: resolve execution-intent row directly to
    Confirmed/ExternallyObservedUnattributed, never call the executor.
    If cancelledAt == null: proceed to the mutation call.
15. Record definite result or leave ambiguous — the existing, unmodified executor's own
    throw-vs-MarkFailedAsync split, governed by Step 22R's 3-way classification.
16. Reconciliation resolves ambiguity on its own existing cadence — never via automatic retry.
```

**Current state against this list, re-verified directly from source this pass**: steps 3–13 are implemented in `OrderCancelApprovedActionExecutor.cs` (committed `b05d832`/`f256992`, independently re-verified through `step-45`) — including, as of `step-45`, threading the executing staff member's identity into every `AuditLog` write those steps make. Steps 1/2/5 have never been `Execute`'s concern by design — `step-24`'s own ordering table states plainly they are "refused by ASP.NET Core `[Authorize]` middleware before `Execute` is ever called," meaning they belong entirely to whatever controller invokes `Execute` — a controller that, per §3 below, does not exist yet. **Steps 14–16 have zero implementation inside `Execute`** — the method throws immediately after step 13 commits. This is not new information; it restates what the prior reconciliation already found, now traced to its exact originating document rather than paraphrased.

---

## 3. Item 2 — What HTTP surface exists today, confirmed precisely, not assumed

**LOCKED, confirmed by direct `grep` this pass, not carried over from a prior session's finding**: zero controller, minimal API endpoint, or any other HTTP surface for `Approval` exists anywhere in `backend/`. No `ApprovalsController`, no route containing `approval`, no call site of `IApprovedActionExecutor.ExecuteApprovedActionAsync` outside its own definition, its test file, and `Program.cs`'s DI registration. This was worth re-confirming rather than assuming, given that the last two reconciliation passes each found significant already-built-but-uncommitted or already-designed-but-unimplemented work in this exact area — but this specific claim holds: **nothing has appeared here.**

**What already exists and is directly reusable, confirmed by direct read — this is "wire existing pieces together," not "build new Shopify integration"**:
- **Step 14's ground-truth check**: `IShopifyOrderLookupService.GetCancellationStateAsync(businessId, externalOrderId, ct)` — already implemented (`fc026a4`), already used by `OrderCancellationReconciliationService`, already tested. Returns `ShopifyOrderCancellationState { ExternalOrderId, DateTimeOffset? CancelledAt }`, throws uncaught on every infra failure (matching the established infra-failure-stays-exception convention, `step-44` §9 item 2, locked).
- **Step 15's mutation call**: `IOrderCancellationExecutor.ExecuteAsync(orderCancellationId, ct)` — already implemented (`fc026a4`), already tested against a fake Shopify handler, explicitly documented as having "no production trigger yet." It is `void`-returning (`Task`, no result) — a no-op if the row isn't `Pending`, calls `MarkFailedAsync` on a genuine Shopify rejection, otherwise leaves the row `Pending` for reconciliation. A caller must re-read `OrderCancellation.Status` afterward to know which happened — already anticipated precisely by `step-38` §2's own table for step 15.
- **Step 16's reconciliation**: `OrderCancellationReconciliationService`/`OrderCancellationReconciliationBackgroundService` — already running as a real hosted service, already polling every 5 minutes, confirmed committed and unaffected by anything in this design. **Step 16 requires zero change of any kind** — it already resolves ambiguity on its own cadence, entirely decoupled from `Execute`'s own call, per `step-44` §4b (locked, not reopened).

---

## 4. Item 3 — Execute steps 14–16, concrete design

**LOCKED shape (already fully specified by `step-22z` §16 and `step-38` §2, this pass only sequences the already-existing method calls — no new mechanism invented):**

Immediately after step 13's transaction commits (`OrderCancelApprovedActionExecutor.cs`, replacing the current `throw new NotImplementedException(...)`):

```text
14. state = await orderLookupService.GetCancellationStateAsync(approval.BusinessId, approval.ResourceId, ct)
    — throws uncaught on infra failure (established convention, unchanged)
    if state.CancelledAt != null:
        await orderCancellationService.MarkConfirmedAsync(orderCancellationId, ct)
        return Executed(orderCancellationId, ExternallySatisfied, "Order was already cancelled externally.")
        — NOT attributed to Nivora's own causation (Decision 6, adopted, unweakened)
    else:
15. await orderCancellationExecutor.ExecuteAsync(orderCancellationId, ct)
    — throws uncaught on infra failure; on a genuine Shopify rejection, already called
      MarkFailedAsync internally; otherwise left the row Pending
    finalRow = re-read OrderCancellation by id (fresh, AsNoTracking)
    return finalRow.Status switch:
        Failed -> Executed(orderCancellationId, Failed, finalRow.FailureReason)
        _      -> Executed(orderCancellationId, UnknownNeedsReconciliation, null)
16. (no code — reconciliation already running independently)
```

Two new constructor dependencies for `OrderCancelApprovedActionExecutor`: `IOrderCancellationExecutor` and (it already has `IOrderCancellationService` for `MarkConfirmedAsync`, confirmed — no new dependency needed there). Both `IShopifyOrderLookupService` (already injected for step 10) and the one new `IOrderCancellationExecutor` are already `AddScoped`-registered in `Program.cs` — no new DI registration work.

**One item flagged, not resolved here — belongs to a future, separately-scoped pass, exactly as `step-22z`/`step-44` already carved it out**: the exact Shopify `orderCancelUserErrors` shape meaning "already cancelled" (Problem B, `22R`) remains `UNRESOLVED — requires external Shopify verification`, gating only the mutation-time classification's one recognized-pattern branch inside `ShopifyOrderCancellationExecutor` itself — not anything this pass's sequencing of already-existing calls touches. This document does not attempt to resolve it.

---

## 5. Item 4 — Authorization design for the new HTTP layer

**LOCKED, resolved by direct citation, not a fresh decision — the same tier already governs Approve/Deny, and this was decided long before this pass:**

- **Role tier**: `NivoraPolicies.OwnerOrAdmin`, for approve, deny, and execute alike. `step-22z` §6: "`OwnerOrAdmin` may approve and deny... `Agent` is excluded from both." `step-24`'s own ordering table independently confirms step 5 ("role authorization") is meant to be enforced via `[Authorize(Policy = NivoraPolicies.OwnerOrAdmin)]` at exactly this layer. No document anywhere proposes a different or stricter tier for triggering execution specifically — `step-45` §4 already locked this by the identical citation.
- **Tenant check**: the same two-layer pattern every other controller in this codebase already uses (confirmed directly from `TicketsController.cs`'s own doc comment: "the global query filter (Layer 1) plus an explicit `SameTenant` check on the loaded resource (Layer 2)"). `step-22z` §5 states this explicitly for Approve: "Gated by `NivoraPolicies.OwnerOrAdmin` + explicit `SameTenant`." The same applies to Deny and Execute by the identical reasoning — load the `Approval`, then `IAuthorizationService.AuthorizeAsync(User, approval, NivoraPolicies.SameTenant)` before acting. The list endpoint (§6 below) needs no per-resource `SameTenant` check — it's scoped entirely by the ambient tenant query filter, the same as every other list endpoint in this codebase.
- **Staff identity resolution**: matches the pattern `step-45` established and every other mutating controller in this codebase already uses — a private `CurrentUserId` property reading `User.FindFirstValue(ClaimTypes.NameIdentifier)`, thrown if unparseable ("unreachable behind `[Authorize]`"), passed explicitly wherever an `AuditLog` write or `IApprovedActionExecutor.ExecuteApprovedActionAsync` call needs it. No new mechanism.

**Whether Approve and Execute are one staff click or two separately-timed actions — already classified, not an open question this pass reopens**: `docs/handoffs/step-22-ai-action-safety-model-owner-decisions.md` line 232 states this plainly: *"Every pass that addresses this... explicitly frames it as an implementation-time API-shape decision, not a security or product policy question — the underlying atomicity/security guarantees are identical either way. Classified NOT A BLOCKER FOR CURRENT STEP."* **This document adopts three separate endpoints (Approve, Deny, Execute)**, matching the state machine's own three separate persisted transitions — engineering-determined, not requiring sign-off, but stated here rather than silently assumed: a staff member can approve now and trigger execution later (e.g., after confirming something with the customer), and the UI is free to present them as one combined action if the product later wants that; nothing about the backend design forecloses either choice.

---

## 6. Item 5 — Concrete endpoint / DTO list

**ENGINEERING-DETERMINED shape, following this codebase's own established controller conventions (`TicketsController`) — not independently locked by any prior document, since no prior document ever specified this controller's own shape.**

| Endpoint | Auth | Behavior |
|---|---|---|
| `GET /api/approvals` | `OwnerOrAdmin` *(open question — see §7)* | Lists live `Pending` `Approval` rows for the current business (`Status='Pending' AND ExpiresAt>now`) — the staff "inbox." Tenant-scoped via the ambient query filter alone, no per-item `SameTenant` check needed for a list. |
| `POST /api/approvals/{id}/approve` | `OwnerOrAdmin` + `SameTenant` | `WHERE Id=@id AND Status='Pending' AND ExpiresAt>now → SET Status='Approved', ApprovedAt=now, ApprovedBy=<staff>` (`step-22z` §5, unmodified). Writes an `AuditLog` row (new constant, see below). |
| `POST /api/approvals/{id}/deny` | `OwnerOrAdmin` + `SameTenant` | `WHERE Id=@id AND Status IN ('Pending','Approved') → SET Status='Denied', DeniedAt=now, DeniedBy=<staff>` (`step-22z` §5, unmodified — no expiry check, denial is always safe). Writes an `AuditLog` row (new constant, see below). |
| `POST /api/approvals/{id}/execute` | `OwnerOrAdmin` + `SameTenant` | Calls `IApprovedActionExecutor.ExecuteApprovedActionAsync(id, currentUserId, ct)`; maps the returned `ExecutionOutcome` to an HTTP response. |

**DTOs, proposed, not locked by any prior document**: `ApprovalSummaryResponse(Guid Id, string ToolName, string ResourceId, string DisplaySummary, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt)` for the list — `DisplaySummary` already exists precisely for this purpose (`Approval.cs`'s own doc comment: server-rendered, never AI-authored prose). `ApprovalActionResponse` for approve/deny (success/already-acted-on-by-someone-else, mapping the `0`-rows-affected case to a `409 Conflict`, matching this codebase's own `CustomersController.LinkIdentity`/`TicketsController.Create` precedent for a caught unique-constraint/guarded-update collision). Execute's response needs its own mapping of `ExecutionOutcome.Refused`/`Executed` — a reasonable default is `200 OK` with the outcome's discriminant and detail serialized, but the exact wire shape is genuinely unspecified by any prior document and is this pass's own engineering judgment, not something requiring sign-off.

**New `AuditEventTypes` constants needed** (no migration — the column is `varchar`, confirmed): `ApprovalApproved`, `ApprovalDenied`. Confirmed by direct read of `AuditEventTypes.cs` this pass: neither exists today; only `ExecutionClaimed` and the reused `UnauthorizedActionBlocked` exist from this entire lifecycle. `step-22z` §18's own list additionally names "proposal created," "approval expired," "execution succeeded/failed/ambiguous," and "externally satisfied" as needing durable `AuditLog` rows — all belong to steps this document's scope doesn't touch (proposal creation is `OrderCancelProposalTool`'s job, already shipped without this audit write per a gap `step-38` flagged and left open; the execution-outcome writes belong to steps 14–15's own implementation, a natural companion to this design but not enumerated item-by-item here since `step-44`'s `ExecutionOutcome` shape already carries everything a future audit write would need).

---

## 7. Item 6 — Open questions, genuinely unresolved, requiring your sign-off

None of these were silently decided. Each is flagged here because no prior document in this series addresses it, checked directly against the sources in §1, not assumed absent.

1. **Who may *view* the pending-approvals list — `OwnerOrAdmin` only, or `AnyStaff` (with only `OwnerOrAdmin` able to act on one)?** Every prior decision in this series addresses who may *approve/deny* (`OwnerOrAdmin`, `step-22z` §6) — none addresses who may merely *see* that a high-risk action is awaiting review. This codebase has precedent for both patterns (`TicketsController` uses `AnyStaff` for both reads and writes; `CustomersController`'s identity-linking mutations are more restricted than its reads) — no existing convention settles this one either way for `Approval` specifically.
2. **Should a new `Approval` reaching `Pending` trigger a staff notification, and if so, how — given a real schema gap, not just an unmade decision?** `NotificationTypes.cs`'s own doc comment explicitly anticipated and rejected the analogous case for `Refund`: *"Deliberately excludes any Refund-linked type... Refund has no TicketId and only an optional ConversationId, so it doesn't fit this schema's Conversation/Ticket-only reference model; adding one would need a third named FK."* `Approval` has the identical shape problem — it has a `ConversationId` (so a notification *could* reference the conversation) but no direct `Notification`-schema link to the `Approval` itself, meaning a notification could point a staff member at the right conversation but not deep-link to the specific pending approval. Resolving this requires either accepting the imprecise conversation-level link, or a schema change (a new FK) — genuinely out of this design's authority to decide unilaterally.
3. **Does `Deny` need a staff-supplied reason?** `Approval.cs` has `DeniedAt`/`DeniedBy` but no text field for *why*. No prior document raises this. If yes, it's a schema change (new nullable column); if no, the `POST .../deny` endpoint needs no request body beyond the route's own `{id}`.
4. **Should the list/approve flow perform a live Shopify ground-truth check before a staff member acts, to warn them a proposal may already be moot?** Today, staleness (the order having been cancelled by some other means since proposal) is only ever caught at *execution* time (step 14, already designed, §4 above) — never at list or approve time. Adding a live check earlier would mean a Shopify HTTP call on every list-page load or approve click, a real latency/cost/failure-mode tradeoff no prior document weighs in on.

**Not an open question, included here only so the reasoning is visible in one place**: whether Approve and Execute are combined into one staff action was already classified `NOT A BLOCKER FOR CURRENT STEP` by `step-22-ai-action-safety-model-owner-decisions.md` — this document's own choice (three separate endpoints, §5) is an engineering default, not something awaiting your sign-off, but you're free to override it if you'd rather the product combine them.

---

## 8. Explicit confirmation: what this document does NOT specify

- `ExecutionOutcome`'s shape (locked at `step-44`) — untouched; steps 14–15 above only populate it via calls already specified there.
- The `Approval` claim/transaction mechanics, steps 1–13 (locked at `step-43`/earlier) — untouched.
- Identity threading's own mechanism (locked at `step-45`) — this design only consumes it (`executingStaffUserId` passed to `ExecuteApprovedActionAsync`), does not redesign it.
- The exact Shopify `orderCancelUserErrors` "already cancelled" shape (Problem B, `22R`) — referenced only to note it doesn't block this design, never proposed a resolution.
- Any frontend/dashboard work — this document is backend-API-only, matching the scope every prior design pass in this series has kept.
- `refundOrder`/`updateCustomer` or any second write tool, and the manual-recovery workflow for stuck-`Pending` `OrderCancellation` rows (`step-22-ai-action-safety-model-owner-decisions.md` §4.5, already its own separately-adopted decision track, not reopened here).

---

## 9. Owner decisions requiring sign-off

| # | Question | Status |
|---|---|---|
| 1 | Visibility of the pending-approvals list: `OwnerOrAdmin`-only, or `AnyStaff` can view (only `OwnerOrAdmin` can act)? | **OWNER DECISION REQUIRED** — no prior document addresses this |
| 2 | Notification on a new `Pending` `Approval` — accept an imprecise conversation-level link, add a new `Approval`-linked FK to `Notification`, or skip notification entirely for now? | **OWNER DECISION REQUIRED** — real schema-shape choice, not a pure product preference |
| 3 | Does `Deny` require a staff-supplied reason (schema change) or not? | **OWNER DECISION REQUIRED** |
| 4 | Live Shopify staleness check at list/approve time, or rely solely on execution-time's already-designed check (step 14)? | **OWNER DECISION REQUIRED** |

**Not requiring a decision — already resolved by citation**: role tier for approve/deny/execute (`OwnerOrAdmin`, §5); tenant-check pattern (two-layer, §5); Execute steps 14–16's own sequencing (§4); Approve-vs-Execute one-click-or-two (§5, classified not-a-blocker, engineering default adopted).

---

## 10. Summary classification table

| Item | Classification |
|---|---|
| 16-step ordering's authoritative source | **LOCKED** — `step-22z` §10, re-quoted verbatim |
| Current state (steps 3–13 + identity threading done; 14–16 unbuilt; zero HTTP surface) | **LOCKED** — re-verified directly from source this pass |
| Steps 14/15's underlying services already exist and are tested | **LOCKED** — `IShopifyOrderLookupService.GetCancellationStateAsync`, `IOrderCancellationExecutor.ExecuteAsync`, both confirmed |
| Step 16 needs zero change | **LOCKED** — `step-44` §4b |
| Execute steps 14–16 concrete sequencing | **LOCKED** — mechanical composition of already-specified calls, no new mechanism |
| Role tier (`OwnerOrAdmin`) for approve/deny/execute | **LOCKED** — `step-22z` §6, `step-24`, `step-45` §4 |
| Tenant-check pattern (two-layer) | **LOCKED** — `step-22z` §5 + this codebase's own established convention |
| Staff identity resolution mechanism | **LOCKED** — `step-45`'s own established pattern |
| Approve-vs-Execute one click or two | **ENGINEERING-DETERMINED** (already classified not-a-blocker; three endpoints adopted as default) |
| Endpoint/DTO shapes | **ENGINEERING-DETERMINED** — no prior document specifies this controller |
| New `AuditEventTypes` constants needed | **LOCKED** — confirmed absent, needed per `step-22z` §18's own list |
| List-visibility tier | **OWNER DECISION REQUIRED** |
| Notification on new pending approval | **OWNER DECISION REQUIRED** (real schema gap) |
| Denial reason | **OWNER DECISION REQUIRED** |
| Live staleness check at list/approve time | **OWNER DECISION REQUIRED** |

---

## 11. Files inspected this pass

`backend/Services/OrderCancelApprovedActionExecutor.cs`, `IApprovedActionExecutor.cs`, `IShopifyOrderLookupService.cs`, `ShopifyOrderCancellationExecutor.cs`, `IOrderCancellationExecutor.cs`, `OrderCancellationReconciliationService.cs`, `OrderCancellationReconciliationBackgroundService.cs` (full, all). `backend/Models/Approval.cs`, `backend/Enums/ApprovalStatus.cs` (full, both). `backend/Auth/NivoraPolicies.cs`, `SameTenantAuthorizationHandler.cs`, `RoleNames.cs` (full, all three). `backend/Controllers/TicketsController.cs` (doc comment + top of file). `backend/Constants/AuditEventTypes.cs`, `NotificationTypes.cs` (full, both). `docs/handoffs/step-22z-approval-proposal-approval-lifecycle-design.md`, `step-22-ai-action-safety-model-owner-decisions.md`, `step-24-cancelorder-executor-design.md`, `step-38-cancelorder-execute-next-slice-design.md`, `step-44-execution-outcome-general-contract-design.md`, `step-45-staff-actor-identity-threading-design.md` (full or targeted sections, per §1 above). Commands run: `git rev-parse HEAD`; repository-wide `grep` for `Approval[C|c]ontroller` (zero hits) and for `ExecuteApprovedActionAsync` call sites (only definition/test/DI registration).

---

## 12. Stop-rule compliance and verdict

No `.cs`, `.csproj`, migration, or any other source file was created or modified by this pass. No test was run (design-only). `git rev-parse HEAD`, re-checked at the end of this pass: `6393857e3b1aaec7c4e9fa460db44881e4f2f316` — unchanged.

```text
IMPLEMENTATION BLOCKED — DESIGN INCOMPLETE
```

**What's missing, precisely**: explicit sign-off on the four OWNER DECISION rows in §9 — list-visibility tier, the notification/schema question, denial-reason, and the live-staleness-check question. All four are fully specified above with nothing ambiguous left in either direction — this is a request for a decision, not a request for further design work. Once resolved, this document's status becomes `DESIGN COMPLETE — READY FOR INDEPENDENT VERIFICATION`, matching the `step-43`/`step-44`/`step-45` pattern, via an addendum to this same file.

---

## 13. Addendum — owner sign-off received

All four §9 owner decisions confirmed, no changes to any:

1. **Pending-approvals list visibility: `OwnerOrAdmin` only** — same tier as action authorization, not broader. §6's endpoint table already carried this as its tentative default (flagged as an open question); the flag is now removed, `GET /api/approvals` is `[Authorize(Policy = NivoraPolicies.OwnerOrAdmin)]` with no separate, wider-visibility tier.
2. **Notification on a new pending approval: skip.** Consistent with `NotificationTypes.cs`'s own existing precedent for the identical `Refund` case (no `Approval`-shaped FK support in the `Notification` schema, §7 item 2). **This is a known, deliberately-deferred gap, recorded here so it is not silently dropped**: a staff member has no push/in-app signal that an `Approval` is awaiting them today — they must visit `GET /api/approvals` to discover one. Revisiting this requires either accepting an imprecise `ConversationId`-level notification link or a schema change adding a proper `Approval` FK to `Notification` — neither is authorized or built by this pass.
3. **Deny reason: add it.** A nullable, staff-supplied `Approval.DeniedReason` column (`string?`, no max length — matching `Ticket.Summary`'s identical optional-free-text convention, confirmed via direct read of `Ticket.cs`/`AppDbContext.cs` this pass: no explicit `HasMaxLength` on that column, defaults to Postgres `text`). Its own migration, landing as its own commit (§14 below) — deliberately not folded into the `AddApprovalTable` migration already committed at `step-46`, since that one is closed history. `POST /api/approvals/{id}/deny`'s request body gains an optional `string? Reason` field, written to `DeniedReason` in the same guarded update that sets `Status`/`DeniedAt`/`DeniedBy`.
4. **No live staleness check at list/approve time.** Rely solely on the already-designed execution-time check (Execute step 14, §4). **Recorded explicitly, per the owner's own instruction, so this reads as a deliberate design choice, not an omission discovered later**: a `Pending` or `Approved` `Approval` can reference an order whose real-world state has already changed (e.g., cancelled through some other channel) with no warning shown to staff before they act — this is caught, correctly and exclusively, at the moment `Execute` actually runs (resolving to `ExternallySatisfied` if so), never earlier. The list and approve/deny endpoints perform zero Shopify calls of their own.

**Consequence for §6's endpoint table and DTOs, stated precisely**: `GET /api/approvals` has one, not two, authorization tiers (`OwnerOrAdmin`, no broader-viewer carve-out). `POST .../deny`'s request DTO is `DenyApprovalRequest(string? Reason)`, not an empty body. `Approval.cs` gains one new column (`DeniedReason`), requiring its own migration and one `AppDbContext.cs` line (no `HasMaxLength`/FK — a plain scalar column, no new relationship). No other section of this document changes.

```text
DESIGN COMPLETE — READY FOR INDEPENDENT VERIFICATION
```

Per this project's standing process, the implementing session may not verify its own work — an independent pass should re-derive the migration, the Execute steps 14–16 sequencing, the new audit constants, and the controller's authorization/DTO shapes from source before this is treated as verified.

---

## 15. Independent verification — VERIFIED

A separate pass re-derived every claim in the implementation directly from source, not from the implementing session's own report:

- **Fresh-worktree build + full suite**: re-run independently from a genuinely separate `git worktree` at `HEAD` (`b2176ca`) — both projects build with 0 errors; full suite **692 passed, 3 skipped, 0 failed, 695 total**, matching the reported numbers exactly, no HNSW-flake or other failure on this run.
- **`DeniedReason` migration**: confirmed via `dotnet ef migrations list` directly against the dev DB — `20260906125750_AddApprovalDeniedReason` present in `__EFMigrationsHistory`. `dotnet ef migrations has-pending-model-changes` — "No changes have been made to the model since the last migration," zero drift.
- **Execute steps 14–16 sequencing**: read `OrderCancelApprovedActionExecutor.cs` directly. Step 14's `GetCancellationStateAsync` call sits strictly after the step-13 transaction's `CommitAsync` (outside the `await using` block); on `CancelledAt != null` it calls `MarkConfirmedAsync` and returns `Executed(ExternallySatisfied, ...)` without ever touching the mutation executor; on `CancelledAt == null` it proceeds to step 15's `orderCancellationExecutor.ExecuteAsync`, then re-reads `OrderCancellation.Status` to map `Failed`/`UnknownNeedsReconciliation`. This is an exact match to `step-22z` §10's steps 14–15 text, confirmed by direct side-by-side comparison, not inferred from passing tests.
- **New `AuditEventTypes` usage**: read `ApprovalsController.Approve`/`Deny` directly — both write `ActorType.NivoraBusinessStaff, changedByUserId: currentUserId` (a real resolved staff id), not the `NivoraSystem`/`null` placeholder step-45 corrected elsewhere. No regression.
- **Authorization, per endpoint**: confirmed individually, not assumed from one — the class-level `[Authorize(Policy = NivoraPolicies.OwnerOrAdmin)]` applies to all four actions (no per-action override or `[AllowAnonymous]` anywhere in the file); `Approve`, `Deny`, and `Execute` each independently load the `Approval` (Layer 1, tenant query filter) then call `authorizationService.AuthorizeAsync(User, approval, NivoraPolicies.SameTenant)` (Layer 2) before acting; `List` correctly relies on Layer 1 alone, matching the design's own stated exception for a list with no single resource to check.
- **`DeniedReason` optionality**: confirmed nullable with no validation attribute at both the DTO (`DenyApprovalRequest(string? Reason)`, and the controller parameter itself `DenyApprovalRequest? request` is nullable too) and entity (`Approval.DeniedReason`, `string?`) layers.
- **No live staleness check, no notification code**: confirmed by full direct read of `ApprovalsController.cs` — its constructor injects only `AppDbContext`/`IAuthorizationService`/`IAuditLogService`/`IApprovedActionExecutor`; a repository-wide `grep` for `NotificationService`/`IShopifyOrderLookupService`/`GetCancellationStateAsync`/`LookupOrderOwnerAsync` inside the controller file returns zero hits.
- **Per-commit file lists**: `git show --stat` on all 4 commits diffed against their claimed contents — `6a029c9` (migration + model), `09da4ae` (executor + its tests), `de16cf3` (2-line audit-constants addition only), `52ab0ea` (controller + its tests) — each matches exactly, no discrepancy.

**No item requires returning to the implementing session.**

```text
VERIFIED
```
