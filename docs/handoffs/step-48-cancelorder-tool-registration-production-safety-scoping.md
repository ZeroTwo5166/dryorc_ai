# Step 48 — Registering OrderCancelProposalTool: Wiring Trace and Production-Safety Scoping

**DESIGN/SCOPING PASS ONLY — NO IMPLEMENTATION.** No `.cs`, `.csproj`, migration, or any other source file was created or modified by this pass. Every claim below was re-derived by directly reading live source and the cited handoff documents this session.

**Scope, as instructed**: confirm or correct the characterization of registering `OrderCancelProposalTool` as "one DI registration, everything downstream already built" — trace the exact wiring path, check for anything else gating it, and specifically determine whether turning it on has unscoped production-safety implications that would make this more than a formality.

**Verdict up front, not buried**: the wiring characterization is **correct — it genuinely is one line**, confirmed by direct trace, not assumed. But **the production-safety question is real, not manufactured**, and changes this from "trivial wiring" to "needs an owner decision first." See §3.

---

## 1. The wiring path, traced exactly

**Registration mechanism — confirmed by direct read of `ToolDispatchService.cs`, not assumed**: the constructor takes `IEnumerable<IDispatchTool> tools` and builds `_toolsByName = tools.ToDictionary(t => t.Name)` (line 31-32). This dictionary is the **single source of truth** for both (a) which tools `ExecuteToolAsync` can dispatch to, and (b) which tool definitions get sent to Claude at all (`RunAsync`'s own `Tools = _toolsByName.Values.Select(t => new Tool { Name = t.Name, Description = t.Description, InputSchema = t.InputSchema })`, line 53-60). **There is no separate tool-schema list, no separate "tools Claude is told about" registry** — whatever is registered in DI as `IDispatchTool` is automatically both dispatchable and visible to Claude, in the same step. This directly answers the task's item 2: nothing else needs to be added anywhere for Claude to learn the tool exists — DI registration alone does both jobs.

**The dispatch `switch` already handles `IActionProposalTool`** — confirmed by direct read of `ExecuteToolAsync` (lines 151-185): `case IActionProposalTool proposalTool: ... await proposalTool.ProposeAsync(...)` already exists, already committed (Step 23J, landed in the `8079e3c` build-integrity-recovery commit). `IActionProposalTool` extends `IDispatchTool` directly (confirmed, `IActionProposalTool.cs` line 1), so it satisfies the same `IEnumerable<IDispatchTool>` constructor parameter `GetCustomerTool` already does. **No new interface, no new switch case, no new mapping mechanism is needed.**

**The only missing piece, confirmed by direct read of `Program.cs` (lines 175-189)**: `builder.Services.AddScoped<IDispatchTool, GetCustomerTool>();` exists; the identical line for `OrderCancelProposalTool` does not. Adding `builder.Services.AddScoped<IDispatchTool, OrderCancelProposalTool>();` is mechanically sufficient for the tool to become dispatchable and visible to Claude. **The "one DI registration" characterization is correct.**

**One adjacent, non-blocking rough edge worth naming**: `ClaudeReplyService.cs`'s own tool-aware system-prompt string (line ~190) is hand-written and currently only narrates `getCustomer` ("You have a getCustomer tool that looks up..."). Registering `OrderCancelProposalTool` would not break anything — Claude can call any tool present in the `Tools` array regardless of system-prompt narration, and `OrderCancelProposalTool.Description` ("Propose cancelling a customer's Shopify order...") is self-contained — but the system prompt would then under-describe what's actually available. A real follow-up, not a blocker.

---

## 2. What else is downstream, confirmed already built (re-confirming, not re-deriving, step-45/46/47's own work)

`OrderCancelProposalTool.ProposeAsync` itself performs no Shopify call — it only validates input, derives `ActionHash`, and creates a `Pending` `Approval` row. `ApprovalsController` (list/approve/deny/execute) and `IApprovedActionExecutor`'s full 16-step ordering are committed and independently verified (`step-45`/`46`/`47`). Confirmed nothing else is missing mechanically.

---

## 3. Production-safety gate — traced, not assumed, and the answer is: none exists

**This is the substantive finding of this pass.** Checked precisely, per the task's own instruction, rather than treating registration as a no-op:

**No per-business opt-in exists.** `grep`-confirmed against `Business.cs`: the fields that exist (`CancelOrderRestocksInventory`, `AutoRefundLimit`, `AutoRefundLimitCurrency`, `StoreCreditRefundsEnabled`, `ExtendedAuditLoggingEnabled`) are all **policy parameters for how an already-live action behaves** (e.g., whether to restock), not **switches for whether the action is available at all**. `Business.AgentsCanApproveActions` — the one field previous design passes explicitly discussed as the shape such a gate might take — does not exist and was never built, confirmed directly.

**This was already raised and explicitly deferred, not merely never considered** — `docs/handoffs/step-22-ai-action-safety-model-owner-decisions.md` §4.9 (Decision 8, re-read directly this pass): *"Per-business/per-write-tool automation tiers... are explicitly out of scope for Step 22 / v1. This is a recorded, deliberate deferral, not a silently-skipped question... No field, state, interface, or mechanism was designed for it."* The same document, §"Who may approve?" (line 127), names the exact shape this gap would need to close: *"if the owner wants a per-business opt-in of this shape, its exact semantics (e.g., 'if false, write tools are unavailable entirely for that business, not auto-approved') would need separate confirmation before any migration."* That confirmation was never given, and no migration was ever drafted.

**No environment-level gate exists either.** Confirmed by direct read of `Program.cs`'s registration block (lines 175-189): the existing `GetCustomerTool` registration is a plain, unconditional `builder.Services.AddScoped<...>()` — no `builder.Environment.IsProduction()`/`IsDevelopment()` check wraps it, and the identical line for `OrderCancelProposalTool` would inherit that same unconditional shape unless deliberately written otherwise.

**No feature-flag mechanism of any kind exists in this codebase to plug into**, even if one were wanted right now — repository-wide `grep` for `FeatureFlag`/`IsEnabled` (excluding tests) returns zero hits in `backend/`. Adding a gate is not "flip an existing switch" — it would be new infrastructure.

**Consequence, stated precisely, not hedged**: the moment `OrderCancelProposalTool` is registered, `cancelOrder` proposal capability goes live **simultaneously, for every business already using this application, in every environment, with no opt-in and no way to exclude any one of them**, for every conversation where a customer identity happens to be resolved (`ClaudeReplyService`'s only gate — a per-conversation state check, not a per-business one, confirmed directly at the tool-offering `if (conversation.CustomerId is Guid customerId)` branch). This is the point where the AI can propose a real cancellation against a real, already-connected Shopify store for the first time in this project's history — not a hypothetical future risk, the actual first live exposure.

**One additional, concrete failure mode this creates, not previously noted anywhere**: `ProposeAsync` requires no Shopify integration to succeed — a business with **no** Shopify connection at all would still have its AI tell a customer "your cancellation request has been submitted for staff approval" and create a real `Pending` `Approval`. If staff then click Approve and Execute (both of which now exist and work, per `step-47`), `Execute`'s step 10 (`IShopifyOrderLookupService.LookupOrderOwnerAsync`) throws an **uncaught** `InvalidOperationException` (no active Integration) — surfacing to the staff member as a raw, unhandled 500 from `ApprovalsController.Execute`, not a clean, informative refusal. This is a direct, traceable consequence of registering the tool with no gate, not a separate concern.

---

## 4. Is this a manufactured question or a real one?

**Real, not manufactured** — every element of §3 traces to either a direct source read (the absence of any field/environment-check/feature-flag mechanism) or an explicit prior owner deferral (Decision 8) that was never subsequently resolved. This is not "no prior document addresses X" (which could plausibly mean nobody thought it mattered); it is "a prior document raised exactly this question, the owner was asked, and the owner explicitly declined to design it for v1." Registering the tool now, unconditionally, would silently reopen and resolve that deferral in the permissive direction — without ever asking again in these terms.

---

## 5. Owner decision required

**Question**: given that Decision 8 (per-business automation gating) was explicitly deferred rather than resolved, how should `OrderCancelProposalTool`'s registration proceed?

Options, named for reference, not recommended over one another (a genuine product/risk-appetite call, not an engineering one):
- **(a) Register unconditionally, as originally characterized.** Legitimate if there are currently no real businesses/Shopify integrations connected yet (pre-launch), making "global blast radius" theoretical today. Needs the owner to confirm that premise, not assume it.
- **(b) Add a minimal per-business gate first** (e.g., the `Business.AgentsCanApproveActions`-shaped boolean previously discussed, or a differently-named equivalent scoped narrowly to "is `cancelOrder` proposal capability on for this business") — a new column + a check in `OrderCancelProposalTool.ProposeAsync` (or upstream, in how tools are constructed per-request) before Claude is ever told the tool exists for that business. This reopens Decision 8 narrowly (a binary on/off, not the fuller tiered-automation system Decision 8 originally scoped) — its own exact semantics would need the same confirmation `owner-decisions.md` line 127 already named as a prerequisite.
- **(c) Add an environment-level gate only** (e.g., off by default outside Production, or behind an explicit config value) — narrower than (b), doesn't solve the per-business question but prevents accidental activation in shared dev/staging environments touching real Shopify test stores.

**Not this pass's call to make.** No implementation performed. Recorded here for sign-off before any registration lands.

---

## 6. Files inspected this pass

`backend/Services/ToolDispatchService.cs` (full), `backend/Services/IActionProposalTool.cs` (header), `backend/Services/OrderCancelProposalTool.cs` (re-confirmed unchanged from step-46), `backend/Services/ClaudeReplyService.cs` (tool-offering branch + system prompt), `backend/Program.cs` (dispatch-tool registration block), `backend/Models/Business.cs` (full field list). `docs/handoffs/step-22-ai-action-safety-model-owner-decisions.md` (§4.9 Decision 8, "Who may approve?" section, full). Commands run: `grep` for `FeatureFlag`/`IsEnabled` repository-wide (zero hits outside tests); `grep` for `CancelOrderRestocksInventory` across the propose/execute chain (zero hits, confirmed still unwired, a separate pre-existing minor gap).

---

## 7. Stop-rule compliance and verdict

No source file created or modified. No test run.

```text
IMPLEMENTATION BLOCKED — DESIGN INCOMPLETE
```

**What's missing**: a decision on §5's three options (or a fourth, owner-specified). This is not a request for further design work on the mechanical wiring, which is already fully specified and genuinely is one line — it is a request to close a previously-deferred production-safety question (Decision 8) before that one line ships.

---

## 8. Addendum — owner sign-off received: Option 2-lite, a single-tool kill switch

**Decision, as given**: `Business.CancelOrderToolEnabled` (bool, default `false`), its own migration. The dispatch/tool-exposure layer gates `OrderCancelProposalTool` behind this flag being true for the conversation's business — if false, the tool must not be offered to Claude at all, not offered-then-rejected. `OrderCancelProposalTool` is registered in `Program.cs` unconditionally at the DI level; gating happens entirely at the per-business tool-exposure layer.

**Explicitly not Decision 8.** `docs/handoffs/step-22-ai-action-safety-model-owner-decisions.md` §4.9's deferred per-write-tool automation-tier system (auto-approve under a limit, fully automatic, tiered policy) remains **genuinely deferred and untouched by this work** — this is a single boolean, single-tool, on/off switch with no tiering, no automation-level concept, and no relationship to the human-approval pipeline itself (a business with the flag on still requires full propose→approve/deny→execute, unchanged). Decision 8 stays open for whenever a future pass actually wants to design it.

### 8a. Where the gate actually lives — traced during implementation, not assumed

**`ToolDispatchService` deliberately does NOT gain a database dependency.** Its own test suite (`ToolDispatchServiceTests.cs`) is explicitly "pure unit tests — no DB, no HTTP" by design; adding `AppDbContext` to make it query `Business.CancelOrderToolEnabled` itself would break that property for a check that has nothing to do with the dispatch loop's own orchestration logic. Instead: `ClaudeReplyService.GenerateReplyAsync` **already loads the `Business` row** (line ~73, `db.Businesses...FirstOrDefaultAsync`) before ever constructing a dispatch context — the flag is available there for free, zero additional queries. `IToolDispatchService.RunAsync` gains one new parameter, `IReadOnlySet<string> disabledToolNames`, computed by `ClaudeReplyService` from `business.CancelOrderToolEnabled` and passed straight through. `ToolDispatchService` itself stays a pure function of its inputs — it excludes any name in that set from both (a) the `Tools` array actually sent to Claude, and (b) — defense in depth, since these are two different code paths that could in principle diverge — the dispatch loop's own tool-resolution check in `ExecuteToolAsync`, so a disabled tool is treated identically to an unregistered one (`"Unknown tool."`) even in the (should-be-impossible) case Claude somehow names it anyway. This is a mechanism-neutral parameter (a set of names), not a new per-tool configuration system — the *decision* of what goes into that set (checking one specific flag for one specific tool) is hardcoded in `ClaudeReplyService`, not made generic.

`OrderCancelProposalTool.Name` becomes `ToolName` (a new `public const string`) so `ClaudeReplyService` has a single, non-stringly-typed source for the name to disable, rather than a duplicated literal.

### 8b. The no-integration crash fix — traced precisely, and one deviation from the literal instruction flagged explicitly

**Where the crash is, confirmed**: `IShopifyOrderLookupService.LookupOrderOwnerAsync`, called at Execute's step 10 (ownership verification), throws an uncaught `InvalidOperationException` when no active Shopify `Integration` row exists for the business. Step 10 is the **first** Shopify-touching step in the 16-step ordering, strictly before step 13's atomic claim — meaning **no `OrderCancellation` row exists yet at the point this crash occurs**.

**This matters for the exact fix shape**: the instruction's own wording — "mark the approval/action Failed with a clear `FailureReason`" — maps naturally to `ExecutionOutcome.Executed(id, ExecutionResultStatus.Failed, reason)` + `OrderCancellation.FailureReason`, but that shape requires an execution-intent row to exist, which is structurally impossible before step 13. Moving the ownership-verification step itself to after the claim is not an option — that ordering is locked (`step-22z` §10, re-verified through `step-45`) precisely so the atomic claim never fires before ownership is confirmed. **Resolution, flagged here rather than silently chosen**: this pass adds a new pre-check, immediately before step 10 begins, for "does an active Shopify `Integration` exist for this business" (the exact same query `ShopifyOrderLookupService`/`ShopifyOrderCancellationExecutor` already use internally) — a stable, checkable business-configuration precondition, not a transient runtime failure. On failure, it returns `ExecutionOutcome.Refused` with a new `ExecutionRefusalReason.ShopifyIntegrationUnavailable` member and a clear `Detail` string — **not** `Executed(Failed, ...)`, since no row exists to attach that to. The `Approval` itself is left untouched (`Approved`, not consumed) — retriable the moment the business reconnects Shopify, which reads as strictly better behavior than burning the approval on a business-configuration problem the AI/customer/approving-staff-member had nothing to do with.

**This narrows, and does not reopen, `step-44` §9 item 2 (the infra-failure-stays-exception decision), confirmed deliberately, not by oversight**: that decision's own scope is genuinely transient/unpredictable runtime failures — token decrypt failure, non-2xx responses, malformed JSON, top-level GraphQL errors — which remain uncaught exceptions here, completely unweakened. "No active Integration" is categorically different: a stable precondition knowable in advance from a single local DB read, with no Shopify call involved in detecting it at all. Treating it as a business-outcome refusal rather than an infra exception is consistent with how `ApprovalNotFound`/`CustomerUnresolved` are already treated (checkable local-state preconditions, not infra failures), not an exception to that principle.

**No `AuditLog` write for this new refusal** — matching `CustomerUnresolved`'s own established precedent (`step-38` §2a): this is not a check on the `Approval`'s own authorization state and nobody attempted anything unauthorized; it is a business-configuration gap. Not routed through `WriteUnauthorizedExecutionBlockedAsync`.

**Scope of the fix, stated precisely**: this closes the steady-state case named in the sign-off — a business that has never connected Shopify, or has disconnected it, reaching Execute. It does not add a second guard immediately before steps 14/15 for the (much narrower, unrequested) case of the integration being deactivated in the few-millisecond window between step 10's check and step 14 — step 10 having just confirmed an active integration makes that race exceedingly unlikely, and it is not the scenario described in the sign-off or tested here.

### 8c. Consequence for §5/§7 above

Neither is reopened or corrected — recorded here only because this addendum's own choices (§8a mechanism, §8b classification) were made in implementing them, not pre-specified in that level of detail by the original design pass.

## 9. Independent verification — VERIFIED

A separate pass re-derived every claim in the implementation report directly from source, not from the implementing session's own report.

- **Fresh-worktree build + full suite**: re-run independently from a genuinely separate `git worktree` at `HEAD` (`534c29c`) — both projects build with 0 errors; full suite **698 passed, 3 skipped, 0 failed, 701 total**, matching (and slightly exceeding, since the HNSW recall flake did not reproduce on this run) the reported numbers, no regression.
- **`CancelOrderToolEnabled` migration**: confirmed via `dotnet ef migrations list` directly against the dev DB — `20260906142311_AddBusinessCancelOrderToolEnabled` present in `__EFMigrationsHistory`. `dotnet ef migrations has-pending-model-changes` — "No changes have been made to the model since the last migration," zero drift.
- **The tool is genuinely absent from what's sent to Claude, not filtered downstream**: read `ToolDispatchService.RunAsync` directly. The `Tools` property on the `MessageCreateParams` object passed straight to `messageService.Create` (the real outbound Anthropic call) is built as `_toolsByName.Values.Where(t => !disabledToolNames.Contains(t.Name))...` — there is no separate "advertised" list upstream of this; this is the actual request payload. Read `ClaudeReplyService.GenerateReplyAsync` directly: `business` is loaded once near the top of the method (`db.Businesses...FirstOrDefaultAsync`), and `disabledToolNames` is computed straight off `business.CancelOrderToolEnabled` and passed unmodified into `RunAsync`. One flag, one set, one filter, no intermediate hop.
- **Fail-closed path confirmed**: `ExecuteToolAsync`'s guard — `if (!_toolsByName.TryGetValue(toolUse.Name, out var tool) || disabledToolNames.Contains(toolUse.Name))` — sits before the `switch` that would dispatch to `IWriteDispatchTool`/`IReadOnlyDispatchTool`/`IActionProposalTool.ExecuteAsync`/`ProposeAsync`. A disabled tool named in a `tool_use` block (e.g. a stale request from before the flag was flipped off) returns `ToolExecutionResult.Error("Unknown tool.")` without ever reaching that switch — confirmed by direct read, and independently by `RunAsync_DisabledTool_IfModelRequestsItAnyway_ReturnsErrorToolResult_NeverExecutes` asserting `readTool.CallCount == 0`.
- **DI registration, read directly in `Program.cs`**: `builder.Services.AddScoped<IDispatchTool, OrderCancelProposalTool>()` (line 198) carries no conditional wrapper of any kind — unconditional at the container level, exactly as designed. `builder.Services.AddScoped<IDispatchTool, GetCustomerTool>()` (line 190) is untouched — same line, same shape, immediately above the new registration, not displaced or altered. `git show 4055b3e -- backend/Services/ClaudeReplyService.cs` confirms the `getCustomer` tool's own system-prompt exposure text is untouched by this diff — only the new `disabledToolNames` computation was added below it.
- **Crash-fix path, read directly in `OrderCancelApprovedActionExecutor.cs`**: the `hasActiveShopifyIntegration` check sits after step 9 (Level 2 verification) and strictly before the step-10 comment block and its `IShopifyOrderLookupService` call — confirmed by line order, not inferred. On a miss it returns `new ExecutionOutcome.Refused(ExecutionRefusalReason.ShopifyIntegrationUnavailable, "No active Shopify integration exists for this business.")` immediately — no `db.SaveChangesAsync` call occurs anywhere on this path (neither in the guard itself nor between it and the prior step), so no `AuditLog` row, no `OrderCancellation` row, and no `Approval` mutation of any kind is possible on this path structurally, not merely by the one test's assertion. The test itself (`ExecuteApprovedActionAsync_NoActiveShopifyIntegration_ReturnsShopifyIntegrationUnavailable_NeverThrows`) additionally confirms `Approval.Status` reads back as `Approved` (untouched) afterward, and uses `ThrowingOrderLookupService`/`ThrowingOrderCancellationExecutor` to affirmatively prove steps 10–15 are never reached.
- **Per-commit file lists**: `git show --stat` on all 4 commits diffed against their claimed contents — `29ccf13` (migration + model only), `4055b3e` (gating logic across `ClaudeReplyService`/`IToolDispatchService`/`ToolDispatchService`/`OrderCancelProposalTool` + their tests), `7999dd9` (DI registration + its own test, 2 files only), `4f18e5b` (crash-fix executor change + enum + its test) — each matches exactly, no discrepancy, no stray file.
- **Scope containment**: `git diff 7b3c106..4f18e5b` (the full step-48 diff span) greps clean for any automation-tier scaffolding — the only "tier"/"automation-tier" hits are (a) the pre-existing, unrelated `Business.PlanTier` field surfacing in the migration snapshot's full-model serialization (not touched by the `AddBusinessCancelOrderToolEnabled` migration itself), and (b) this session's own comments explicitly stating Decision 8 remains untouched. `Program.cs` still registers exactly one `IDispatchTool` beyond `GetCustomerTool` (`OrderCancelProposalTool`), and it is not an `IWriteDispatchTool` — no write tool exists in this codebase, unchanged from step-23K's own note.

**No item requires returning to the implementing session.**

```text
VERIFIED
```
