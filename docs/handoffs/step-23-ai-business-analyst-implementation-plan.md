# Step 23 — AI Business Analyst: Implementation Plan

**This is a phasing/planning pass, not a design pass.** The locked design
(`docs/handoffs/step-23-ai-business-analyst-design.md`, committed `c6d4be99`)
is treated as a fixed constraint throughout — nothing below reinterprets,
reopens, or improves any of its decisions or guarantees. This document only
answers: what gets built, in what order, and where the real (not assumed)
independent-verification boundaries fall. It is **uncommitted** and subject
to owner review before implementation begins, per this project's established
workflow.

**This plan does not assume Step 22's 3-phase shape.** Every phase boundary
below is justified by a real code dependency or a real historical precedent
found in this session — cited with commit hashes, not asserted by analogy.
Where the dependency graph and risk profile don't clearly force a boundary,
that's stated as a choice the owner can collapse, not as a forced structure.

---

## 1. The actual dependency graph

Traced directly from current source, not assumed:

- **§1a (`AnalyticsQueryService` extraction) → §1c (the 5 tools).** Real,
  hard dependency: each `IAnalystCapability` tool's `ExecuteAsync` must call
  a real, correct query method. The tools cannot be meaningfully implemented
  — only stubbed — before the extraction exists.
- **§1b (the `IAnalystCapability` interface) → §2/§3 (orchestration).**
  This is a **soft** dependency in practice, not a hard one: the interface
  shape (`Name`/`Description`/`InputSchema`/`ExecuteAsync(Guid businessId,
  ...)`) is small and stable once declared, and this project already has a
  proven pattern for testing dispatch/orchestration logic against **fake**
  implementations of a tool interface, decoupled from the real tools —
  `backend.Tests/ToolDispatchServiceTests.cs:16` (`RecordingReadOnlyTool`)
  and `:357` (`ThrowingReadOnlyTool`) are both test-only `IReadOnlyDispatchTool`
  implementations that exist solely to exercise `ToolDispatchService`'s own
  loop mechanics, never touching a real production tool. The same pattern
  applies directly to `IAnalystCapability`: the orchestration engine (§2/§3)
  can be built and adversarially tested against fake capabilities as soon as
  the interface is declared — **it does not need to wait for the real 5
  tools (§1c) to be implemented**, only for the extraction (§1a) that those
  real tools will eventually depend on, which is an entirely separate,
  parallel-track piece of work.
- **§4 (kill switch) has zero code dependency on §1/§2/§3.** The migration
  and `Business.AiBusinessAnalystEnabled` property are a pure additive
  schema change with no consumer required to compile or migrate — confirmed
  by direct precedent (§5 below). The **toggle endpoint**
  (`SetAiBusinessAnalystEnabled`, modeled on
  `BusinessController.SetCancelOrderToolEnabled`) only reads/writes that one
  boolean and audits the transition — it has no dependency on the
  orchestration engine, the tools, or the query service either.
- **§5/§6 (the actual Analyst endpoint) depends on all of the above.** The
  endpoint is where the kill-switch check (§4), the orchestration call
  (§2/§3, needing the real tools from §1c for genuine end-to-end behavior),
  and the response-shaping/authorization (§5/§6) are wired together. This is
  necessarily the last piece to land.
- **§7 (audit) and §8 (telemetry) are not separate build items.** §7
  concludes no `AuditLog` write is needed for the read path (nothing to
  build); §8's telemetry is inline `ILogger` calls inside whatever code
  actually makes the Claude calls and executes capabilities — it ships as
  part of the orchestration/endpoint work, not as its own phase.

This graph has one genuine fork (kill switch vs. everything else are
independent) and one genuine chain (query extraction → tools → orchestration
integration → endpoint), with orchestration's *mechanics* able to be built
and proven against fakes before the chain's tool step (§1c) completes.

---

## 2. Proposed phases

Five phases, each independently buildable and independently verifiable
against the current `main`/`feature/app-pages` tip without depending on a
later, not-yet-written phase. Phases 3 and 4 have only a soft dependency on
each other (§1 above) — see the note at the end of this section for the
option to merge them.

### Phase 1 — Kill switch (migration + property + toggle endpoint)

**What**: `Business.AiBusinessAnalystEnabled` (`bool`, default `false`)
migration; the toggle endpoint (`SetAiBusinessAnalystEnabled`, `OwnerOnly`,
audited on real transitions, idempotent no-op skip — mirroring
`BusinessController.SetCancelOrderToolEnabled` exactly, per design §4).

**Why this is a safe, independent boundary**: confirmed by direct
precedent, not just architectural reasoning — see §5 below. The migration
alone touches only `Business.cs` and the migration/snapshot files; no other
code references the new property yet, so nothing else can break. The toggle
endpoint only reads/writes/audits that one property.

**Nothing in Phases 2-5 needs to exist for this phase to be built, tested,
or merged.**

### Phase 2 — `AnalyticsQueryService` extraction (design §1a)

**What**: extract the 5 per-metric query bodies and the shared
range-parsing/zero-fill helpers out of `AnalyticsController.cs:59-131` into
a new `IAnalyticsQueryService`/`AnalyticsQueryService`. Rewrite
`AnalyticsController.Get` to call the same 5 methods and compose the
existing `AnalyticsResponse` — **its external contract does not change.**

**Why this is a safe, independent boundary**: it touches exactly one
existing production file (`AnalyticsController.cs`) plus new service
files, has zero dependency on the kill switch, the tools, or the
orchestration engine, and its correctness is directly checkable against
Step 21's own already-established, already-trusted test assertions (see §3
and §4 below) — this is the highest-provenance verification available for
any phase in this plan, because the exact expected values were already
locked in by Step 21's own design and tests, not invented for this pass.

**Real risk this phase must not skip**: design §6 requires exact semantic
equivalence (tenant-filter predicate, date-window computation, grouping,
zero-fill, status mappings, `getHandledBySnapshot`'s all-history behavior)
— **not assumed to carry over from Step 21's own empirical verification,
re-proven directly** (see §3).

### Phase 3 — `IAnalystCapability` interface + the 5 tools (design §1b/§1c)

**What**: declare the interface, implement the 5 concrete tools
(`getConversationsByDay`, `getConversationStatusDistribution`,
`getHandledBySnapshot`, `getHandoffsByDay`, `getTicketStatusDistribution`),
each a thin wrapper calling one `AnalyticsQueryService` method with
`businessId` passed through from the caller.

**Why this is a safe, independent boundary**: it depends only on Phase 2's
service existing (a real, hard dependency, per §1 above) — it has no
dependency on the kill switch or on any orchestration/Claude-calling code
at all. Each tool can be tested by directly calling `ExecuteAsync` with a
seeded `businessId` and asserting the output matches what
`AnalyticsQueryService`'s own (already Phase-2-verified) method returns —
no Claude, no `IMessageService`, no fake needed for this phase's own tests.

### Phase 4 — Orchestration engine (design §2/§3)

**What**: the two-phase Claude dispatch (`Tools`+`ToolChoiceAuto` selection
call, backend-enforced ≤3 ceiling with pre-execution rejection over the
limit, conditional synthesis call with `Tools` withheld), and the
outcome-classification logic (`Answered`/`Refused`/`CapabilityFailed`) this
produces.

**Why this is a safe, independent boundary**: per §1 above, this can be
built and adversarially tested entirely against **fake** `IAnalystCapability`
implementations (mirroring `ToolDispatchServiceTests`' own
`RecordingReadOnlyTool`/`ThrowingReadOnlyTool` pattern) and a fake
`IMessageService` (`SequencedFakeMessageService`, already shared
infrastructure — `backend.Tests/SequencedFakeMessageService.cs`, already
used by both `ToolDispatchServiceTests` and
`ConversationClassificationServiceTests`) — **it does not require Phase 3's
real tools to exist to be fully proven.** This is the highest-risk phase in
this plan (it carries every one of the design's structural guarantees) and
gets the most adversarial test attention (§3 below).

**Note on ordering**: Phase 4 is listed after Phase 3 above for narrative
clarity (matching the design doc's own §1→§2 ordering) and because true
end-to-end confidence eventually wants the real tools — but per the
dependency graph, **Phase 4's own build-and-test cycle has no hard
dependency on Phase 3 being merged first.** See the merge note below.

### Phase 5 — Endpoint, kill-switch integration, response/authorization shape (design §4's request-time check, §5, §6, §7/§8)

**What**: the actual `POST` Analyst endpoint — kill-switch check first
(returning immediately if disabled, per design §4, before any Claude call),
then the orchestration call (Phase 4) against the real tools (Phase 3),
`AnyStaff` authorization and `ICurrentTenantAccessor`-only tenant resolution
(§6), the 4-outcome discriminated response shape excluding raw tool
data/capability names (§5), and the telemetry logging (§8) inline in this
same code.

**Why this is last**: it is the one place all four other phases' outputs
are actually wired together — it has a real, hard dependency on Phases 1,
3, and 4 (and transitively Phase 2).

**Merge option, flagged rather than decided**: Phases 3 and 4 could be
built and committed together as one phase if the owner prefers fewer review
checkpoints — their dependency is soft (§1), and combining them would still
be independently verifiable as a unit (fakes for the orchestration side,
real DB assertions for the tool side, in the same commit). This plan keeps
them separate because it mirrors this project's own established discipline
of proving a mechanism in isolation before it is wired into a real flow
(e.g. `ConversationModeService` built and tested standalone well before
Step 22 wired it in) — but this is a preference, not a dependency
requirement, and is flagged as an **open question** (§6 of this document)
rather than decided unilaterally.

---

## 3. Test strategy per phase

### Phase 1 (kill switch)

- Standard coverage matching `SetCancelOrderToolEnabled`'s own existing test
  shape (if one exists) or the general `Business` boolean-toggle pattern:
  idempotent no-op does not audit; a real transition does, with the correct
  `AuditEventTypes` constant, `ActorType.NivoraBusinessStaff`, and
  `changedByUserId`. Nothing adversarial is required here — this is a
  well-precedented, low-risk write path.

### Phase 2 (`AnalyticsQueryService` extraction) — re-verify, don't assume

This is the phase the task explicitly warns not to assume carries over.
Concretely:

- **Re-run `AnalyticsControllerTests.cs`'s existing 14 test methods,
  unmodified in their assertions** (only the constructor call site changes
  — see §4 below). Every one of these tests already asserts *exact* counts
  against known, controlled seed data, dual-tenant, per metric
  (`Metric1_ConversationsByDay_IsolatesPerTenant_BothDirections` through
  `Metric5_TicketStatusDistribution_...`, plus the zero-fill and boundary-
  cardinality tests). If the extraction preserves exact semantic
  equivalence (design §6's requirement), **these pass unmodified in content
  — if even one assertion's expected value needs to change, the extraction
  introduced a real behavioral drift**, not a refactor.
- **New, additive**: `AnalyticsQueryServiceTests.cs`, one test per metric
  method, directly re-proving tenant isolation at the **service** layer
  (not just observed indirectly through the controller) — mirroring the
  exact dual-direction methodology `AnalyticsControllerTests.cs` already
  established (seed two businesses with overlapping-looking data, assert
  each tenant's own call excludes the other's rows, both directions). This
  is the direct, re-derived proof design §6 requires — not inherited by
  assumption from Step 21's original empirical verification
  (`step-21-analytics-design.md` §11), which was performed against the
  *pre-extraction* code and does not automatically speak for the
  post-extraction code without being re-run.
- `getHandledBySnapshot`'s specific "ignores range entirely, even for very
  old rows" behavior (already covered by
  `Metric3_HandledBySnapshot_IsolatesPerTenant_AndIgnoresRangeAndAge`) must
  be re-proven at the service-method level too, since design §1c's tool
  description depends on this being genuinely true, not merely believed
  true.

### Phase 3 (the 5 tools)

- Per tool: `InputSchema` shape assertion (the `range` enum tools require
  it; `getHandledBySnapshot` requires an empty schema — a real, checkable
  assertion, not just "no parameters mentioned in prose").
  `getHandledBySnapshot`'s `Description` field must be asserted to actually
  contain language ruling out range/trend usage (design §1c's "prominent in
  ... the tool's own `Description` field content" requirement) — a string-
  content test, not just an existence check, since this is a real behavioral
  contract the tool-selection prompt depends on.
- Per tool: `ExecuteAsync(businessId, ...)` called directly (no Claude, no
  fake needed) against seeded data, asserting the result matches
  `AnalyticsQueryService`'s own already-verified output for the same
  parameters — proving the tool is a faithful, parameter-correct pass-
  through, not a second, independently-risked query.
- An invalid `range` value passed to a tool's input must be rejected the
  same way `AnalyticsController`'s existing `InvalidRange_ReturnsBadRequest`
  test already proves for the HTTP path — reusing the same validation logic
  extracted in Phase 2, not a second, drifting implementation.

### Phase 4 (orchestration) — the genuinely adversarial tests

Every guarantee named in the task brief needs a test that proves the
*negative*, not just that the happy path works. Using
`SequencedFakeMessageService` (existing shared infrastructure) and new,
Phase-4-scoped fake `IAnalystCapability` implementations (mirroring
`RecordingReadOnlyTool`/`ThrowingReadOnlyTool`):

- **Over-limit ceiling rejection (design §2d)**: enqueue a fake Phase-1
  response containing **4 or more** `ToolUseBlock`s. Assert (a) the
  request resolves to the backend-authored over-limit failure response, (b)
  **zero** of the fake capabilities' `ExecuteAsync` were ever called
  (proving the check happens *before* execution, not that execution simply
  stopped partway), and (c) `SequencedFakeMessageService.CallCount == 1`
  (proving no synthesis call was attempted either). This is the test that
  would have caught Draft 1's withdrawn "truncate to 3 and continue"
  design — for genuine confidence, this test should first be written and
  confirmed to **fail** against a deliberately-reintroduced truncate-and-
  continue implementation before being confirmed to pass against the
  correct one, the same negative-proof discipline
  `ApplyTagsAndRatchetAsync_TwoGenuinelyOverlappingCalls_...`
  (Step 22 Phase 2) used for its own concurrency guarantee.
- **Zero-tool-selection → always `Refused`, Claude's own text never
  surfaced (design §3a/§3b) — the single most important test in this
  plan.** Enqueue a fake Phase-1 response with **zero** `ToolUseBlock`s and
  a deliberately convincing `TextBlock` (e.g. a fabricated but
  plausible-sounding sentence naming a specific, invented number — chosen
  specifically to be the kind of text a careless implementation would pass
  through unnoticed). Assert (a) the outcome is `Refused`, (b) the
  fabricated sentence's exact text is **not present anywhere** in the
  returned response (a direct string-absence assertion against the
  specific crafted text, not merely "the discriminator says Refused" —
  the discriminator alone would not have caught Draft 1's original bug,
  since Draft 1 also intended to label this case in a way that looked
  handled), and (c) no Phase 2 call was made
  (`SequencedFakeMessageService.CallCount == 1`).
- **Capability failure short-circuits before synthesis (design §3c)**:
  enqueue a fake Phase-1 response requesting exactly 1 valid tool; make
  that fake tool's `ExecuteAsync` return `ToolExecutionResult.Error(...)`.
  Assert (a) the outcome is `CapabilityFailed`, (b) the backend-authored
  failure text is returned, not anything resembling a Claude-authored
  sentence, and (c) `SequencedFakeMessageService.CallCount == 1` — the
  single strongest test for design §3c's "Phase 2 is never called at all"
  guarantee, mirroring `ConversationClassificationServiceTests`' own
  precedent of asserting real captured request counts/shapes rather than
  trusting the code path was taken.
- **Happy path — exactly 2 calls, synthesis call structurally cannot
  request a tool**: enqueue a fake Phase-1 response requesting 1-3 valid
  tools, all fake `ExecuteAsync` calls succeed; assert
  `SequencedFakeMessageService.CallCount == 2` and directly inspect
  `Requests[1].Tools`/`Requests[1].ToolChoice` to confirm it is empty or
  `ToolChoiceNone` — mirroring
  `ConversationClassificationServiceTests.ClassifyAsync_SendsForcedToolChoice_NamingExactlyTheClassificationTool`'s
  own precedent of asserting the actual `MessageCreateParams` sent, not
  just the resulting text.
- **Backend never trusts Claude's own count**: a test with exactly 3
  requested tools (at the ceiling) must succeed normally, distinguishing
  "at the ceiling" from "over the ceiling" with an off-by-one-safe
  assertion (3 succeeds, 4 is rejected) — cheap to add, closes a boundary
  case the design's prose doesn't spell out a test for explicitly.

### Phase 5 (endpoint integration)

- **Kill switch checked before any Claude work**: with
  `AiBusinessAnalystEnabled = false`, assert the response is the `Disabled`
  outcome **and** that the fake `IMessageService`'s `CallCount` is `0` —
  proving the gate is structurally first, not merely that the eventual
  response happens to say "disabled" after work was already done.
- **Tenant isolation, end-to-end**: two seeded businesses with
  distinguishable data (mirroring `AnalyticsControllerTests`' own dual-
  business seeding), asserting a request under business A's tenant context
  never reflects business B's data in its `Answered` response — the
  integration-level version of Phase 2's own re-verified isolation,
  confirming nothing in the wiring (endpoint → orchestration → tools →
  query service) introduces a leak the lower layers didn't already have.
- **`BusinessId` never taken from the client**: a request cannot
  meaningfully supply an alternate tenant scope at all under this design
  (§6 — the endpoint takes only `{ question: string }`), but a defense-in-
  depth test confirming the endpoint signature has no such parameter (or,
  if a body field were ever added for any reason, that it's ignored) is
  cheap insurance against a future accidental regression.

---

## 4. Existing tests: what's modified vs. purely additive

**Investigated directly, not assumed.** `backend.Tests/AnalyticsControllerTests.cs`
constructs the controller directly, not through DI:
`new AnalyticsController(dbA)` appears **14 times** across its test methods
(one `AppDbContext`-only constructor parameter, matching
`AnalyticsController`'s current primary-constructor signature,
`AnalyticsController.cs:46`).

**This means Phase 2's refactor requires a mechanical test-file update, not
zero changes** — if `AnalyticsController`'s constructor changes to accept
`IAnalyticsQueryService` instead of (or in addition to) `AppDbContext`
directly, every one of these 14 call sites must change to
`new AnalyticsController(new AnalyticsQueryService(dbA))` (or equivalent).
**This is exactly the same category of change as Step 22 Phase 2's own
precedent** — `5eed6ab`'s commit message for `a78a2b9` records "25
pre-existing test methods in `ClaudeReplyServiceTests.cs` were modified only
to accommodate the new `IConversationClassificationService` constructor
parameter (one mechanical line each ...), no behavioral changes." The
design doc's own "exact same DTOs, exact same response shape" guarantee
(§1a) is about response **content** equivalence — it says nothing about,
and does not protect, direct-construction call sites in tests that depend on
the constructor's own parameter list. These are two different claims, and
conflating them would have been a real mistake to carry into implementation
unchecked.

**What must NOT change**: every assertion body in those 14 tests (the
expected counts, the zero-fill checks, the boundary cardinality checks) —
per Phase 2's own test strategy above, an assertion-body change during this
refactor is a signal of a real regression, not a refactor detail to wave
through.

---

## 5. Migration sequencing — investigated against real precedent, not assumed

Two real precedents exist in this codebase for "kill-switch column +
consumer," and they are **not** the same shape — this matters, so both are
reported precisely rather than citing only the one that supports a clean
story.

**Clean precedent (recommended to follow)**: `Business.CancelOrderToolEnabled`.
Commit `29ccf13` ("feat(safety-model): add Business.CancelOrderToolEnabled
kill switch (step 48a)") touches **only** the migration/Designer/snapshot
files and `Business.cs` — no consumer anywhere in that commit. Its own
consumer (`OrderCancelProposalTool` actually being gated by it) landed 15
seconds later as a **separate** commit, `4055b3e` ("... (step 48b)"),
confirmed by `git merge-base --is-ancestor 29ccf13 4055b3e`. The
migration-only commit is independently buildable and independently
mergeable — it does not reference anything that doesn't yet exist. This
project's own history already numbers "add the flag" and "wire the flag"
as distinct sub-steps (`48a`/`48b`) for exactly this reason. **Step 23's
Phase 1 (§2 above) follows this precedent directly.**

**Messier precedent (found, and explicitly not a template to copy)**:
`Business.ShopifyReadToolsEnabled`. Commit `b6142f3` ("feat(shopify): add
getOrder AI-callable read-only tool") **already references
`business.ShopifyReadToolsEnabled`** in its own `ClaudeReplyService.cs`
diff — but the property itself wasn't added to `Business.cs` until the
**next** commit, `bcc3835`, 13 seconds later. Checking out `b6142f3` alone
would not compile: `Business` has no such property yet at that commit.
This is reported precisely because a naive "there's a separate migration
commit, so this pattern is safe" conclusion would be wrong here — this pair
was authored as one coupled unit narratively split into two commits, not
two independently-buildable increments. **This is not the shape to copy for
Step 23** — Phase 1's migration must be genuinely standalone (as `29ccf13`
is), not merely committed separately from its eventual consumer while
already being referenced by not-yet-committed code.

**Conclusion for Step 23**: `Business.AiBusinessAnalystEnabled` can and
should ship in its own Phase 1 (§2 above), genuinely independently
buildable and mergeable, well before Phase 5's endpoint exists to consume
it — this is both a basic engineering fact (an unused, defaulted boolean
column has no forward dependency) and consistent with this project's own
cleanest precedent for exactly this situation.

---

## Open questions for owner review

Both questions raised by the original pass are now resolved by owner
decision. **Zero open questions remain.**

1. **Resolved — Phases 3 and 4 stay separate, not merged.** Phase 3's tests
   prove capability/database correctness (faithful pass-through to real
   seeded data); Phase 4's tests prove orchestration/Claude-interaction
   correctness (structural guarantees against fakes) — these are different
   failure surfaces, and merging them would make a future test failure
   ambiguous about which claim broke. This follows the plan's own cited
   precedent (`ConversationModeService` proven standalone before being
   wired in).
2. **Resolved — Phase 1 ships first.** Not a dependency requirement (Phase 1
   and Phases 2-4 remain genuinely independent tracks until Phase 5), but a
   sequencing preference: Phase 1 is the smallest, cheapest, most-
   precedented piece, and shipping it first gives an early low-risk
   confirmation the kill-switch pattern still holds before committing more
   review attention to the higher-risk phases.
