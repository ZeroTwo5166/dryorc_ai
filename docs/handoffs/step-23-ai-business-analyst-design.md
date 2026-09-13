# Step 23 — AI Business Analyst: Design (Draft 3 — pending owner review)

**This is a design draft, not a locked design.** Per this project's established
pattern (Step 22's design doc went through discovery → owner decisions → two
design review/addendum rounds → lock before implementation began), this
document is expected to come back with corrections. It is **uncommitted**.
No code, schema, migration, or test is written by this pass.

**Draft 2 changes**: two additional owner decisions were locked since Draft 1
(added to §0) and resolve Draft 1's most significant open question (§2c) and
one other (§7/§8). Draft 1's own investigation also surfaced several
corrections that don't require further owner input, applied directly to §2b,
§2d, §1c, §5, §6, and §8. See the "Open Questions" section for exactly which
of Draft 1's nine questions are now resolved and how. Sections not listed as
changed (§1a, §1b, §3c, §4, §6's authorization/tenant-context reasoning, §7's
audit-log conclusion, §5's raw-payload-exposure reasoning) carry forward
unchanged from Draft 1.

**Draft 3 changes (closure-only)**: the two Open Questions remaining after
Draft 2 (kill-switch UX primacy, §4; `Refused` HTTP status, §5) are closed
using the already-agreed design recommendations, both of which Draft 2 had
already stated as its own leaning. No other section, decision, or guarantee
is touched. **Open Questions is now zero.**

Inputs: `docs/handoffs/step-23-ai-business-analyst-discovery.md` (discovery)
and `docs/handoffs/step-23-ai-business-analyst-owner-decisions.md` (the five
originally-locked decisions, now amended/extended per §0 below). Where a
design choice below cannot be derived from a locked decision, from discovery,
or from an existing repository convention, it is called out as an **Open
Question** rather than resolved here.

All source citations below were re-verified directly against current source
in this same session (`ToolDispatchService.cs`, `ConversationClassificationService.cs`,
`AnalyticsController.cs`, `Business.cs`, `AuthContracts.cs`, `BusinessController.cs`,
`IDispatchTool.cs`, `FlaggedCustomerActivityLog.cs`, and the installed
`Anthropic` SDK assembly `12.42.0` via reflection), not assumed from the
discovery doc's own summaries.

---

## 0. Locked constraints, for reference

1. Data scope: Step 21's 5 metrics only; no arbitrary SQL.
1a. Separate bounded tools per metric/domain, not one monolithic tool.
2. Step 23 = on-demand Analyst; Step 24 = scheduled/proactive reporting (distinct surfaces).
3. Single-turn only in v1.
4. In-scope: summary/trend/single-metric/bounded-comparison questions over the 5 metrics. Out-of-scope: refund/cancellation/tags/customer/PII/churn/revenue/inventory or anything needing an unapproved capability → **hard refusal**, no proxy-metric approximation, no fabricated causation.
5. ≤3 bounded capability calls + exactly 1 Claude synthesis call per request, backend-enforced; `Business.AiBusinessAnalystEnabled` kill switch, default `false`; no v1 quota; failure must not be papered over.

**5 (amended, locked since Draft 1)**: "A successful tool-using Analyst
request may make one Claude tool-selection call followed by exactly one
Claude synthesis call, for a maximum of two Claude API calls total. The
synthesis call is the single 'Claude synthesis call' referred to by
Decision 5. The selection call is permitted solely to choose bounded
capabilities and must never itself be treated as the substantive Analyst
answer." This text supersedes the original Decision 5 wording for design
purposes and resolves Draft 1's §2c open question: **Reading A is
confirmed correct.**

**Telemetry (new, locked since Draft 1)**: "Do not persist or log the raw
staff Analyst question text. Operational telemetry may record non-content
metadata such as outcome, elapsed time, Claude token usage, capability
count/names, and failure/refusal classification." This resolves Draft 1's
§7 open question: the answer is no.

---

## 1. Analytics capability tools

### 1a. Required refactor of `AnalyticsController`

*(Unchanged from Draft 1.)*

**VERIFIED FROM REPOSITORY** — `AnalyticsController.Get` (`backend/Controllers/AnalyticsController.cs:59-122`)
currently computes all 5 metrics inline, as 5 private `await db.<Entity>...`
blocks inside one action method, using private static helpers
(`ValidRanges`, `ToUtcMidnight`, `ZeroFillDays`, `ZeroFillEnum`) that are not
callable from anywhere else.

**This is a genuine refactor requirement, not a thin-wrapper situation.**
None of the 5 metrics can be called individually today — the query logic
must be extracted before it can be reused per-tool. Proposed shape:

- New `IAnalyticsQueryService`/`AnalyticsQueryService`, one method per
  metric, taking the already-parsed `(rangeStart, rangeEndExclusive, allDays)`
  values (or nothing, for the one range-independent metric) and returning the
  **exact same DTOs Step 21 already defines and ships**
  (`DayCountEntry`, `StatusCountEntry` — `AnalyticsController.cs:10,12`),
  unchanged. The 5 query bodies (lines 76–107) move verbatim into this
  service — this is a pure extract-method refactor, not a rewrite, which
  matters directly for §6's tenant-filter re-verification below.
- `AnalyticsController.Get` is rewritten to call the same 5 service methods
  and compose them into the existing `AnalyticsResponse` exactly as today —
  **Step 21's own shipped contract (`GET /api/analytics?range=...`, its
  response shape, its range validation, its zero-fill guarantee) does not
  change for any existing caller.** This satisfies Decision 1's "Step 21's
  five metrics are the initial dataset" without touching Step 21's own
  already-shipped, already-verified behavior.
- The range-parsing/validation logic (`ValidRanges` dictionary,
  `BadRequest` on an invalid value, the calendar-day half-open UTC window
  computation at `AnalyticsController.cs:69-73`) is extracted alongside it,
  so each tool that accepts a `range` parameter validates and computes the
  window identically to the existing endpoint — one implementation, not two
  drifting copies.

### 1b. Why these can't be `IDispatchTool`/`IReadOnlyDispatchTool`

*(Unchanged from Draft 1.)*

**VERIFIED FROM REPOSITORY, already flagged in discovery §4**:
`ToolDispatchContext` (`backend/Services/IDispatchTool.cs:15`) is
`record ToolDispatchContext(Guid BusinessId, Guid CustomerId, Guid?
ConversationId)` — `CustomerId` is non-nullable. An Analyst request has no
customer at all. Reusing `IReadOnlyDispatchTool` would require either making
`CustomerId` nullable (a breaking change to every existing tool's contract,
out of this step's scope) or fabricating a placeholder customer id (a
correctness/audit hazard). **A new, narrower interface family is required**
— `Name`/`Description`/`InputSchema` (identical shape to `IDispatchTool`,
reused for consistency) but an `ExecuteAsync(Guid businessId, ...)` signature
that never mentions a customer at all:

```
interface IAnalystCapability
{
    string Name { get; }
    string Description { get; }
    InputSchema InputSchema { get; }
    Task<ToolExecutionResult> ExecuteAsync(
        Guid businessId, IReadOnlyDictionary<string, JsonElement> input, CancellationToken ct);
}
```

(Shown here to establish the shape being discussed, not as final code —
exact member signatures are a design-phase implementation detail per the
owner-decisions doc.) Reusing `ToolExecutionResult` (`IDispatchTool.cs:25-29`)
as-is keeps the same `IsError`/`Content` contract already proven for the
existing dispatch loop's tool_result mapping.

### 1c. The five tools

| Tool name | Input schema | Underlying service method | Output |
|---|---|---|---|
| `getConversationsByDay` | `range: "7d"\|"30d"\|"90d"` (required) | `GetConversationsByDayAsync` (extracted from `AnalyticsController.cs:76-80`) | `List<DayCountEntry>` |
| `getConversationStatusDistribution` | `range: "7d"\|"30d"\|"90d"` (required) | extracted from lines 83-87 | `List<StatusCountEntry>` |
| `getHandledBySnapshot` | **none** — see below | extracted from lines 90-93 | `List<StatusCountEntry>` |
| `getHandoffsByDay` | `range: "7d"\|"30d"\|"90d"` (required) | extracted from lines 96-100 | `List<DayCountEntry>` |
| `getTicketStatusDistribution` | `range: "7d"\|"30d"\|"90d"` (required) | extracted from lines 103-107 | `List<StatusCountEntry>` |

Each tool's `InputSchema` is built the same way
`ConversationClassificationService.BuildTool` already does
(`JsonSerializer.SerializeToElement(new { type, @enum, description })`,
`ConversationClassificationService.cs:241-273`) — a static, fixed schema per
tool (not per-tenant like classification's, since a `range` enum has no
tenant-dependent vocabulary).

**`getHandledBySnapshot`'s non-range semantics — expanded per Draft 2
correction.** This metric ignores range entirely (Decision 1 of Step 21's
own design, `AnalyticsController.cs:89-93`, "all-history, no range filter");
its `InputSchema` has an empty `Properties`/`Required`, matching the
underlying query's own no-parameter shape. This is not merely a schema
detail — it is a real capability limitation that must be stated
**prominently in the tool's own `Description` field content** (e.g.
something to the effect of: "returns the current, all-time snapshot of
AI-vs-human handling; does not accept a date range and cannot show how this
split has changed over time"), not left implicit, so that Claude's own
tool-selection judgment does not attempt to use this tool as if it were
range-scoped.

**This directly constrains Decision 4's "bounded comparisons ... where the
available data supports the comparison"**: a question framed as a trend
over this specific metric (e.g. "how did AI-vs-human handling trend over
the last 30 days") is **not** a comparison the available data supports,
because `getHandledBySnapshot` has no time dimension at all — there is no
prior snapshot to compare against, only the current one. Under §3's design,
a question requiring this is one `getHandledBySnapshot` cannot serve, and
(assuming no other tool can serve it either) the request correctly resolves
to the zero-tool-selection outcome (§2b/§3, now always `Refused` per Draft 2
correction 1), not an approximated trend answer synthesized from a single
snapshot. The tool-selection system prompt (content itself out of scope for
this design pass, per the closing section) must carry this same limitation
in whatever instructs Claude on how to use the tool, consistent with its
`Description` field.

**Grounds**: Decision 1a ("separate, purpose-built read-only
capabilities/tools corresponding to distinct approved metrics"), discovery
§2a (the exact 5 metrics and their existing query shapes).

---

## 2. Dispatch/orchestration — the central architectural question

### 2a. Why neither existing pattern applies unmodified

*(Unchanged from Draft 1.)*

**`ToolDispatchService.RunAsync`** (`backend/Services/ToolDispatchService.cs:34-143`),
read in full: it is a `for` loop of up to `MaxToolIterations = 10` (line 28),
**each iteration issuing its own separate `messageService.Create` call**
(line 48) — i.e. a separate real Anthropic API request per iteration. When a
response contains `ToolUseBlock`s, the loop executes them, appends
`tool_result`s, and loops back for **another full API call**; the loop only
terminates (`Completed`) once a response contains zero tool uses, which can
happen on any iteration from the 1st to the 10th. This is an open-ended,
potentially-multi-call, recursive-by-design loop — the exact shape Decision 5
explicitly forbids ("No unbounded tool loops or recursive Analyst calls are
permitted in v1"). It also fails §1b's `ToolDispatchContext` shape problem.
**It cannot be reused as-is.**

**`ConversationClassificationService`** (`backend/Services/ConversationClassificationService.cs:64-82`)
makes exactly one `messageService.Create` call, with `ToolChoice = new
ToolChoiceTool(ToolName)` — a **forced** single specific tool, always called,
never optional. This proves "call Claude directly outside
`ToolDispatchService`, with `IMessageService`" is a viable, already-tested
pattern — but its "always force exactly this one tool" shape does not fit a
request that may need 0 to 3 of 5 different tools depending on the question.
The owner-decisions doc (`step-23-ai-business-analyst-owner-decisions.md`,
"Do not invent a forced one-tool architecture") explicitly rules out deriving
a forced-single-tool design from this precedent.

### 2b. Confirmed shape: two Claude phases, never more

**Now a closed design point, not a proposal awaiting confirmation** — the
amended Decision 5 (§0) confirms this two-phase shape satisfies the locked
constraint exactly.

**Phase 1 — tool selection.** One `messageService.Create` call, `Tools` =
the 5 `IAnalystCapability` definitions (as `Tool`s), `ToolChoice` left at its
default (`ToolChoiceAuto`, per SDK reflection below) — Claude is free to
request zero, one, or several tools in this single response, or to answer
directly with text.

**VERIFIED FROM REPOSITORY (SDK reflection)** — the installed `Anthropic`
package (`12.42.0`, `backend/backend.csproj:11`) defines
`Anthropic.Models.Messages.ToolChoiceAuto` and `ToolChoiceAny`, both exposing
a `DisableParallelToolUse` nullable-bool property. The existence of a flag to
*disable* parallel tool use is direct evidence the SDK/API natively supports
**multiple `ToolUseBlock`s in a single assistant turn** by default — Claude
can request more than one tool "at once," in one API response, without a
second round-trip. This is the concrete mechanism that makes "0-3 tool calls
in one phase" possible without a multi-iteration loop.

Backend then enforces the 3-capability ceiling per §2d below — **corrected
in Draft 2**: an over-limit request is now rejected outright, not truncated.

**Phase 2 — synthesis.** If Phase 1 requested one or more tools (at or under
the ceiling) and all executed without error, exactly one more
`messageService.Create` call is made: the tool results are appended to
`messages` (same `tool_result`-following-`tool_use` shape
`ToolDispatchService.cs:110-136` already establishes), but this call's
`Tools` is **empty** (or `ToolChoice` is explicitly
`Anthropic.Models.Messages.ToolChoiceNone`, confirmed to exist in the SDK by
the same reflection pass). Claude is structurally unable to request a
further tool in this call — it can only produce text. This is the "exactly 1
Claude synthesis call" the amended Decision 5 names.

**If Phase 1 requested zero tools — corrected in Draft 2, see §3.** Its own
text output is **never** surfaced to the staff member as the Analyst's
answer. This case is always classified as `Refused`, using a
backend-authored response, not Claude's Phase-1 text passed through. No
Phase 2 call is made. (Draft 1 incorrectly treated Phase 1's own text as a
legitimate final answer in this case — see §3 for the corrected reasoning.)

**Total raw Anthropic API calls under this design: exactly 1 in every case
that terminates without a synthesis call — zero tools selected (→
`Refused`, this section), more than 3 tools requested (→ `Refused`, §2d), or
a requested tool failing to execute (→ `CapabilityFailed`, §3c) — or exactly
2 (1 selection + 1 synthesis) when one or more tools were requested, all at
or under the ceiling, and all executed without error. Never more, matching
the amended Decision 5's "maximum of two Claude API calls total" exactly.**

### 2c. Resolved — Decision 5 (amended) confirms this design satisfies the "exactly 1 synthesis call" constraint

Draft 1's most significant open question asked whether "exactly 1 Claude
synthesis call" permitted a prior tool-selection call at all, or required
tool selection to happen outside Claude entirely. **This is now resolved by
owner decision, not by inference**: the amended Decision 5 (§0) explicitly
names the selection call and the synthesis call as two distinct roles, caps
their combined total at two calls, and states the selection call "must
never itself be treated as the substantive Analyst answer" — which is
exactly what Draft 2's correction to the zero-tool-selection outcome (§2b,
§3) now enforces structurally, not just architecturally. No further
ambiguity remains here.

### 2d. Ceiling enforcement — backend-authoritative, never trusting Claude's compliance

Matching this project's own stated principle that a model's compliance with
a prompted instruction is never sufficient authorization (the same principle
already structurally enforced elsewhere — e.g. `ToolDispatchService.cs:156`'s
disabled-tool check happens in code regardless of what the model was told):
after Phase 1 returns, the backend counts `response.Content.OfType<ToolUseBlock>()`
directly, before executing any of them.

**Corrected in Draft 2 — over-limit requests are rejected, not truncated.**
If that count exceeds 3, **none of the requested tools are executed.** The
request resolves to a controlled, backend-authored failure response (e.g.
"this analysis needs more data than the Analyst can retrieve in one request
— try narrowing the question"), returned to the staff member without any
capability call or synthesis call being made. This check happens strictly
before any capability executes. (Draft 1's "truncate to the first 3 and
continue" design is withdrawn — silently dropping some of Claude's requested
tools while still answering as if nothing were omitted was the property this
correction removes.)

**Every attempted capability invocation counts toward the 3-capability
ceiling regardless of success or failure — a closed design point, not an
open question.** Draft 1 flagged this as open pending clarification of
whether "capabilities invoked" meant attempts or successes; under this
design's single-batch shape (all requested tools, up to the ceiling, are
issued once, in one batch, with no sequential re-request or retry round —
§2b), the distinction has no behavioral consequence to leave open: an
attempted call already counts against the ceiling at the moment it is
selected in Phase 1, before its outcome is known, which is also the only
point at which "counting" can occur in this design. (Draft 1's OQ3 is closed
by this restatement, not left pending.)

---

## 3. Hard-refusal and no-fabrication enforcement

### 3a. The core structural guarantee — corrected in Draft 2

**Draft 1 framed the gap below as an accepted residual risk for v1; that
framing is withdrawn.** Draft 2's correction to §2b (zero Phase-1 tool
selections → always `Refused`, backend-authored, Claude's own text never
surfaced) closes it structurally, without requiring any new
grounding/citation-validation mechanism. The core invariant is now:

> **No approved capability data was retrieved → no synthesized factual
> answer is returned. Full stop.**

This holds regardless of *why* Phase 1 selected no tools — whether Claude
judged the question out of scope, judged it answerable from general
knowledge without fresh data, or anything else. None of those cases are
permitted to reach the staff member as a synthesized answer; all of them
resolve to the same backend-authored `Refused` outcome. Decision 4's "hard
refusal" and "must never infer, fabricate, estimate, approximate, or
otherwise manufacture an answer from unavailable or unapproved data" are
therefore enforced by construction: the only path that ever produces a
`messageService.Create` call with `Tools` withheld (Phase 2 — the only call
whose text is ever shown to the user as a data-backed answer) is a path that
already has real, successfully-retrieved capability data attached to it.

**What this replaces**: Draft 1's own §3a additionally investigated a
different, narrower failure mode — Claude producing plausible-sounding text
*after being offered tools* but choosing not to call any, in a single
Phase-1-only response that Draft 1 treated as reachable to the user. That
specific path no longer exists in Draft 2 (§2b), because a zero-tool Phase 1
response is never surfaced. The "no grounding/citation-validation mechanism
exists in this codebase" finding from Draft 1 is still factually true and
retained for context, but it is no longer load-bearing for this invariant —
it would only become relevant again if a future revision reintroduced a path
where Claude's own free text, produced without any capability call, were
ever shown to a staff member as an answer.

### 3b. Refusal — always backend-authored when reached

Any request whose Phase 1 selects zero tools resolves to `Refused`. Per
§3a, the response shown to the staff member is backend-authored (a fixed or
templated message, which per Decision 4 "may describe currently supported
capabilities" — e.g. naming the 5 metrics this Analyst can answer questions
about), never Claude's own Phase-1 text. This is a single outcome, not two:
Draft 1's earlier "Refused vs. answered-without-tools-needed" distinction is
removed, since both of those cases now collapse into the same non-answer
outcome.

### 3c. Real capability failure — deliberately a different, backend-authored path

*(Unchanged from Draft 1.)*

This is the case Decision 5's "must not fabricate or approximate the answer"
on failure most directly concerns, and it **is** given a hard structural
guarantee: if executing any of the ≤3 requested tools throws or returns
`ToolExecutionResult.Error(...)` (mirroring `ToolDispatchService.cs:200-214`'s
own broad catch-and-convert-to-error pattern), **Phase 2 is never called at
all.** Claude never sees the failure and is never given a chance to
"smooth over" a partial or missing result with a plausible-sounding
approximation — the backend itself returns a fixed, non-Claude-authored
"this data could not be retrieved right now" response. This is strictly
stronger than relying on a prompt instruction to Claude not to guess when a
tool result looks like an error, and is the concrete mechanism that
distinguishes this case from §3b's refusal case: **both a refusal and a
capability failure are now backend-authored, never Claude's own text** — the
distinction between them is not "who wrote the words" (as Draft 1 had it)
but *why* no synthesized answer exists: a refusal means no tool was ever
selected (or too many were, per §2d); a capability failure means a tool was
correctly selected and legitimately broke.

**Of the 4 total outcomes this design produces (§5 states all 4), 3 carry no
synthesized answer** — the "no synthesized answer family" (revised from
Draft 1's two, since `Refused` no longer splits into two sub-cases per
§3b): (1) `Refused` — backend-authored, no tool was selected, or the
ceiling was exceeded; (2) `CapabilityFailed` — backend-authored, a selected
tool broke; (3) `Disabled` — the kill switch (§4). The fourth outcome,
`Answered` (§3a), is the only one that carries a synthesized answer. These
four must not collapse into fewer distinct shapes in the API response (see
§5).

---

## 4. Kill switch

*(Unchanged from Draft 1.)*

**New column**: `Business.AiBusinessAnalystEnabled` (`bool`, default
`false`), migration modeled directly on the most recent, structurally
closest precedent — `AddBusinessShopifyReadToolsEnabled`
(`backend/Migrations/20260910143559_AddBusinessShopifyReadToolsEnabled.cs`):
`AddColumn<bool>(name: ..., type: "boolean", nullable: false, defaultValue:
false)`. `ShopifyReadToolsEnabled` is the closest existing analog in shape,
not just in being "a boolean kill switch" — like this one, it gates a
*whole family* of related read-only tools together (`getOrder`/`getProduct`/
`searchProducts`/`getInventory`, `Business.cs:74-86`), not one single tool.

**Where checked**: unlike `ShopifyReadToolsEnabled`'s check (inside
`ClaudeReplyService`'s tool-offering logic, mid-conversation), Step 23's
entire surface *is* the feature — there is no broader conversation this
capability is one option within. The gate is a single, request-time check at
the very top of the (to-be-designed) Analyst endpoint action, **before any
Claude call, before any tool execution** — if `business.AiBusinessAnalystEnabled
== false`, return immediately. No second, execution-time "Layer 2" re-check
(the kind `RefundApprovedActionExecutor`/`OrderCancelApprovedActionExecutor`
need) is required, because there is no propose-now/execute-later time gap in
this synchronous, single-request flow for the gap to protect against.

**Response when disabled — investigated, no existing precedent forces one
specific shape.** Confirmed by search: no existing endpoint in this codebase
gates its *entire* action behind a single `Business.<X>Enabled` boolean the
way this would (every existing flag either gates one optional tool inside a
larger AI conversation, or gates a write-toggle's own idempotent effect) —
this draft is choosing a new shape, not reusing one. Recommendation: a `403`
response with a structured body distinguishing "feature disabled" from any
other 403 cause.

**Closed in Draft 3 (was an Open Question in Draft 1/2)**: the client hides
or removes the Analyst entry point entirely when `AiBusinessAnalystEnabled`
is `false`, following the existing `MeResponse.CancelOrderToolEnabled`
exposure pattern exactly (`backend/Auth/AuthContracts.cs:18-26`, already
lets the dashboard hide the cancel-order UI for a business that hasn't
opted in). The backend's `403` (above) remains as defense-in-depth — it
protects against a stale client, a direct API call, or any other path that
bypasses the client-side gate — but it is not the primary user-facing UX; a
normal user in the dashboard should never see it. This is the design's
locked answer, not merely a leaning.

**Toggle endpoint** (not owner-decided; the owner-decisions doc explicitly
lists "implementation details of the kill switch" as open): the existing
precedent (`BusinessController.SetCancelOrderToolEnabled`,
`backend/Controllers/BusinessController.cs:71-124`) is `OwnerOnly`
(stricter than the `AnyStaff` read path — matches this codebase's universal
pattern of gating a capability's *exposure* more strictly than its *use*),
idempotent (`business.CancelOrderToolEnabled == request.Enabled` short-circuits
before writing or auditing), and audits every real transition
(`AuditEventTypes.BusinessCancelOrderToolEnabled`/`Disabled`). A
`SetAiBusinessAnalystEnabled` endpoint following this identical shape is the
natural fit; the exact route/DTO naming is left to implementation.

---

## 5. API/request shape

**Endpoint** (exact route/verb is an implementation detail, not owner-
decided): one `POST` action, `AnyStaff`-authorized (§6), taking `{ question:
string }` and returning a discriminated response distinguishing **4 total
outcomes**: `Answered` (Claude's Phase-2 synthesized text, always backed by
real capability data, per §3a), `Refused` (backend-authored, §3b),
`CapabilityFailed` (backend-authored, §3c), and `Disabled` (§4, gated before
any of the other three can occur). Of these, `Refused`/`CapabilityFailed`/
`Disabled` are the 3 that carry no synthesized answer (§3c's "no synthesized
answer family"); `Answered` is the only one that does. Collapsing `Refused`
and `CapabilityFailed` into the same shape would violate the task's own
explicit requirement that these stay distinct — this draft keeps them as
different discriminator values so the frontend can render them differently
(e.g. a refusal might invite rephrasing; a capability failure might invite
"try again").

**Raw tool data — not exposed in v1.** *(Reasoning unchanged from Draft 1.)*
Returning the raw per-tool JSON payloads (e.g. the actual `ConversationsByDay`
array `getConversationsByDay` produced) alongside the synthesized answer was
considered and rejected as the default: it would couple the frontend to
`IAnalystCapability` output shapes that are explicitly a design/
implementation detail subject to change, and it exposes more raw aggregate
data per response than the specific question asked for (if Claude called 2
of 3 tools but only meaningfully used 1 in its answer, returning both raw
payloads over-shares relative to what was actually answered).

**Capability names — corrected in Draft 2, now a closed default, not an open
question.** Draft 1 recommended including the names of capabilities invoked
(e.g. `["getConversationsByDay"]`) in the response as a lightweight
provenance signal, and left whether to do so as open. **This is now closed:
capability names (or any other tool-selection detail) are not exposed in the
API response by default for v1.** The response carries only the synthesized
answer text and the outcome discriminator. This can be revisited later if a
concrete frontend/UX need for provenance emerges — it is not ruled out
permanently, only excluded from the v1 default. (Capability names are still
recorded in internal telemetry — §8 — which is a different surface from the
external API contract.)

**Closed in Draft 3 (was an Open Question in Draft 1/2)**: a `Refused`
outcome is returned as `200 OK`, with the outcome discriminator field set to
`Refused`. The request itself was well-formed — a staff member asked a
legitimate, on-topic-looking question that happened to fall outside
Decision 4's boundary — and a refusal is a valid, non-error outcome of that
well-formed request, not an HTTP-level failure; a `4xx` would misleadingly
read to a frontend/monitoring system as "something went wrong." This is the
design's locked answer, not merely a leaning.

---

## 6. Authorization

**Policy**: `NivoraPolicies.AnyStaff` — the same policy `AnalyticsController`
itself already uses (`backend/Controllers/AnalyticsController.cs:45`) for the
identical underlying data. No `SameTenant` resource check is needed, for the
same reason `AnalyticsController`'s own doc comment already states
(`AnalyticsController.cs:36-41`): this is an aggregate, business-wide view
with no single loaded resource to check ownership of — the global tenant
query filter is the only scoping mechanism, exactly as today.

**Tenant context — server-side only, never client-supplied.** `BusinessId`
must be resolved exclusively via `ICurrentTenantAccessor`
(`backend/Interfaces/ICurrentTenantAccessor.cs`), which
`ClaimsCurrentTenantAccessor` (`backend/Auth/ClaimsCurrentTenantAccessor.cs`)
derives from the authenticated staff cookie's own claims — fail-closed to
`null` (matching no rows) if unresolved, per that interface's own doc
comment. Every `IAnalystCapability.ExecuteAsync(Guid businessId, ...)` call
receives this value from the endpoint, never from the client request body
and never from Claude's own tool-call `input` — mirroring
`ToolDispatchContext`'s own stated principle exactly (`IDispatchTool.cs:6-13`:
trusted context comes from the server side, never from model-supplied
arguments, "even if the model happens to include one").

**Tenant filter under the new query shapes — re-verified, not assumed to
carry over. Corrected in Draft 2** (rewording only; the underlying
requirement is unchanged): because §1a's refactor is a pure extraction (the
same `db.Conversations.GroupBy(...)`/`db.ConversationHandoffs.GroupBy(...)`/
`db.Tickets.GroupBy(...)` shapes, moved out of the controller), the
empirical verification Step 21 already performed — the global tenant filter
survives `GroupBy`+`Count`+date-bucketing+projection, checked against two
real tenants with SQL inspected directly
(`docs/handoffs/step-21-analytics-design.md` §11) — applies to the extracted
service methods, **provided the implementation pass preserves exact semantic
equivalence with Step 21's already-verified queries**: the same tenant-filter
predicate, the same date-boundary/window computation, the same grouping
logic, the same zero-fill behavior, the same status-value mappings, and (for
`getHandledBySnapshot`) the same all-history/no-range-filter behavior.
Source-level formatting, variable naming, or helper placement may differ
from `AnalyticsController`'s current code; behavior may not. ("Byte-for-byte
identical," Draft 1's original phrasing, overstated the requirement — this
restates it at the level that actually matters.)

---

## 7. Audit/logging

**`AuditLog`'s own stated scope** (`backend/Models/FlaggedCustomerActivityLog.cs:9`):
"AuditLog, which only records write/authorization-relevant events for all
customers by default." An Analyst request is read-only, staff-initiated, and
operates over the same aggregate, non-PII data `AnalyticsController`'s
existing `GET /api/analytics` already serves today **without any AuditLog
entry**. No per-request authorization decision beyond "is this an
authenticated staff member" is made (that's the declarative `[Authorize]`
policy, identical to every other `AnyStaff` GET endpoint in this codebase,
none of which are audited). **By this existing, explicit convention, an
Analyst question-answering request does not warrant an `AuditLog` entry** —
this is grounded in a stated repository convention, not a fresh product-
policy call. *(This conclusion is unchanged from Draft 1.)*

This is distinct from the kill-switch **toggle**, which is a write and does
warrant one, following `BusinessController.SetCancelOrderToolEnabled`'s
existing convention exactly (§4).

**Resolved since Draft 1**: whether the raw staff question text should be
logged anywhere is now answered by the locked Telemetry decision (§0) — it
must not be. See §8 for what operational telemetry should record instead.

---

## 8. Cost/token tracking

Per discovery §4, no usage-tracking database infrastructure exists (Step 27
is entirely unstarted) and this design does not build any — that remains out
of scope. What already has a working precedent, and this draft recommends
reusing exactly: `ConversationClassificationService.LogOutcome`
(`backend/Services/ConversationClassificationService.cs:279-296`) — a plain
structured `ILogger` line per attempt, carrying elapsed time and the real
`response.Usage.InputTokens`/`OutputTokens` from the SDK, no persistence.

**Restated per the locked Telemetry decision (§0) and Draft 2's other
corrections, telemetry for an Analyst request should record**:

- the outcome type (`Answered` / `Refused` / `CapabilityFailed` / `Disabled`
  — §3, §5);
- elapsed time for the request;
- token usage for **both** Claude calls actually made — the selection call's,
  and (only when Phase 2 runs) the synthesis call's — since both are real
  Anthropic API calls with real cost, regardless of which one the amended
  Decision 5 names as "the" synthesis call for counting purposes;
- the count and **names** of capabilities actually invoked (this is
  internal telemetry, a different surface from the external API response —
  §5 excludes names from the API contract, but they may still be logged
  internally for observability);
- **never** the raw staff question text, per the locked Telemetry decision.

This produces exactly the kind of "observed usage and cost" data Decision 5
anticipates being used to "revisit later," without building any new
persisted infrastructure now, and without retaining any question content
the Telemetry decision prohibits.

---

## Open Questions for owner review

**Resolved since Draft 1** (listed for traceability, not because they remain
open):

- **Reading A vs. B for "exactly 1 synthesis call" (was §2c's OQ)** —
  resolved by the amended Decision 5 (§0): Reading A confirmed.
- **Silent truncation of excess tool requests (was §2d's OQ)** — resolved by
  Draft 2 correction: over-limit requests are now rejected outright before
  any execution, not truncated; nothing is silently omitted, so the
  disclosure question no longer applies.
- **Whether a failed capability call counts against the 3-call ceiling (was
  §2d's OQ)** — resolved: every attempted invocation counts regardless of
  outcome, a closed design point under this design's single-batch shape.
- **Prompt-only refusal as an accepted residual risk (was §3a's OQ)** —
  resolved by Draft 2's §2b/§3a correction: the zero-tool-selection outcome
  is now always backend-authored and never surfaces Claude's own
  non-tool-grounded text, closing the fabrication gap structurally rather
  than by accepted risk.
- **Whether to expose capability names in the API response (was §5's OQ)** —
  resolved: not exposed by default in v1, a closed design default (may be
  revisited later).
- **Whether raw staff question text should be logged (was §7's OQ)** —
  resolved by the locked Telemetry decision (§0): no.
- **Whether the Phase-1 selection call's own token usage should be logged
  (was §8's OQ)** — resolved: yes, both calls' token usage are logged (§8).
- **Whether the frontend hides the Analyst entry point when disabled, or
  relies on the backend `403` as the primary UX (was §4's OQ)** — resolved
  in Draft 3: the client hides/removes the entry point; the `403` is
  defense-in-depth only, not the primary UX (§4).
- **What HTTP status a `Refused` outcome carries (was §5's OQ)** — resolved
  in Draft 3: `200 OK` with the outcome discriminator set to `Refused` (§5).

**Still open: none. Open Questions is zero as of Draft 3.**

**New questions surfaced while applying this or the prior correction pass**:
none. Each correction was specific enough to apply directly without
producing a new ambiguity. This is stated explicitly, rather than left
silent, per this pass's own instruction not to manufacture questions to
fill space.

---

## Explicitly not decided or built by this document

No tool is implemented. No migration is applied. No endpoint exists. No
prompt text is written. No frontend page or component exists. No test is
written. Step 24 (Scheduled AI Reports) is untouched and out of scope, per
Decision 2. Open Questions is zero as of Draft 3, but this remains a design
draft, not an implementation authorization — it still requires the owner's
sign-off/lock before implementation begins, matching this project's
established discovery → decisions → design (review rounds) → lock →
implementation workflow.
