# Step 22 — Conversation Classification: Design

Downstream of the committed owner-decision record:
`docs/handoffs/step-22-conversation-classification-owner-decisions.md`,
commit `e485a6e` ("docs: record Step 22 owner decisions"). This design
resolves open questions A–D from that document by direct investigation of
current source. **No code has been written in this pass.** This document is
for owner review and sign-off; implementation does not begin until that
happens.

**Revision note:** this version incorporates the owner's addendum resolving
B, C, and the staff-correction/ratchet coupling (received after the first
draft of this document). B and C below are no longer open recommendations —
they are now locked mechanisms, specified here in enough detail to
implement. A is locked as previously recommended, with one added condition
(structured logging). D is confirmed unchanged. See each section for what
changed and why. **This document is still uncommitted and still requires
owner sign-off before implementation begins** — locking B/C's mechanism is
not the same as approving this document as a whole.

Every claim below cites the actual file/line it was traced from, current as
of `feature/app-pages` at `e485a6e`. Where the dev plan / schema reference
docs describe something and the real source disagrees, that is called out
explicitly — source wins, per the owner's own instruction for this pass.

---

## A. Can classification piggyback on the existing AI turn, or does it need a second Claude call? — LOCKED (owner addendum), one condition added

**Short answer: a clean, zero-added-call piggyback is not actually available
today, for a more specific and more structural reason than "it would add
latency." The existing tool-dispatch loop discards assistant text on any
turn that also contains a tool call, and the tool-dispatch path is only
reachable for conversations with a resolved `CustomerId` in the first
place.** Both facts below were traced from source, not assumed.

**Finding 1 — `ClaudeReplyService.GenerateReplyAsync` has two structurally
different reply paths, and only one of them ever sees a `Tool`:**

- `backend/Services/ClaudeReplyService.cs:180` — the tool-dispatch branch
  only runs `if (conversation.CustomerId is Guid customerId)`. An anonymous
  or not-yet-identity-linked visitor's conversation (the common case before
  Level 2 verification/identity resolution) takes the `else` branch at
  `ClaudeReplyService.cs:253` — a single `messageService.Create` call with
  **no `Tools` array at all**. `ToolDispatchContext.CustomerId` is
  non-nullable by design (`ToolDispatchContext.cs:15`, and the comment at
  `ClaudeReplyService.cs:170-179` explicitly names this as a deliberate,
  already-settled constraint, not an oversight). Any classification
  mechanism that depends on being registered as an `IDispatchTool` and
  routed through `ToolDispatchService` **structurally cannot run for a
  conversation with no resolved customer** without either loosening that
  non-nullability (undoing an existing, deliberate design decision) or
  duplicating a separate tool-call path for the no-customer branch.

- `backend/Services/ToolDispatchService.cs:103-108` — inside the loop:
  ```csharp
  var toolUses = response.Content.Select(b => b.Value).OfType<ToolUseBlock>().ToList();
  if (toolUses.Count == 0)
  {
      var text = ...; // captured only here
      return ToolDispatchOutcome.Completed(text);
  }
  // otherwise: loop again, text from this turn is never captured
  ```
  **Any assistant turn that includes one or more `tool_use` blocks has its
  text silently discarded** — only a turn with *zero* tool calls contributes
  to `outcome.ResponseText`. This means "just add a `classifyConversation`
  tool to the existing dispatch loop and let Claude call it alongside its
  reply in the same turn" does not work as stated: if Claude calls the
  classification tool in the same turn as writing customer-facing text, that
  text is thrown away and the loop iterates again expecting the *next* turn
  to be the real reply. This is existing, tested, shared infrastructure
  behind `cancelOrder`/`refundOrder`/`getOrder`/`getCustomer` — changing its
  text-capture semantics to preserve text-plus-tool-call turns is itself a
  non-trivial, independently-risky change to code this codebase has already
  built significant safety machinery around, not a safe assumption to make
  as a side effect of this step.

**Finding 2 — a genuinely single-call approach does exist, but it's a prompt
convention, not a tool call, and it works in both branches for a specific
structural reason:** the *terminal* turn of the tool-dispatch loop (the one
that ends the loop and produces `ResponseText`) is, by Finding 1's own logic,
always a **zero-tool-call, text-only** turn — structurally identical in
shape to the entire no-customer branch's single call. That means a system
prompt instruction asking Claude to append a final, delimited block (e.g.
`<classification>...</classification>`) to its plain-text reply would fire
identically whether the reply came from the tool-dispatch loop's terminal
turn or the no-tools branch's only turn — genuinely one Claude call, no
added round-trip, working uniformly across both paths. **This was not found
anywhere in this codebase as an existing pattern — it would be new.** Real,
concrete risks, not hypothetical: (1) no SDK-enforced schema — Claude could
format the tag block inconsistently, omit it, or produce unparseable
content, and there is no existing parsing/validation code in this repo to
model this on; (2) a stripping bug (a missed delimiter variant, a
customer-typed string that happens to contain the delimiter, etc.) risks
leaking raw classification markup into the **customer-facing widget
response** — a materially different risk profile than a malformed tool
argument, which stays server-side. Given `Conversation.Tags` is what
eventually gates AI autonomy (once `ConversationModeService` is wired in per
the owner decision), a malformed/dropped classification here degrading
silently to "no tags assigned" is a safety-relevant failure mode, not merely
a UX one.

**Finding 3 — a forced single-tool call is a real, available alternative,
confirmed against the actual installed SDK, not assumed:** the Anthropic C#
SDK this project depends on (`Anthropic` NuGet package, pinned
`Version="12.42.0"`, `backend/backend.csproj:11`) ships `ToolChoice` support
— confirmed directly against the installed package DLL
(`~/.nuget/packages/anthropic/12.42.0/lib/net9.0/Anthropic.dll`), which
exposes `ToolChoiceAuto`, `ToolChoiceAny`, `ToolChoiceTool`, and
`ToolChoiceNone` types. **`ToolChoiceTool` (forcing exactly one named tool
to be called) is not used anywhere in this codebase today** —
`ToolDispatchService.cs:48-66` never sets a `ToolChoice` on
`MessageCreateParams`, always leaving it at the SDK default (equivalent to
"auto," matching this loop's own free-choice design). A dedicated
classification call using `ToolChoiceTool` against a single `classify` tool
would get an SDK/API-enforced-shape JSON result — no prompt-parsing risk —
at the cost of a genuine second Claude round-trip (separate from, not
piggybacked on, the customer-facing reply call).

**Finding 4 — this codebase has no fire-and-forget precedent; every
existing async side effect on the customer-message path is `await`-ed
in-request with degrade-gracefully error handling, never detached:**
`WidgetConversationService.PostMessageAsync` (`WidgetConversationService.cs:170-173`)
`await`s its own secondary side effect
(`NotifyStaffOfCustomerReplyAsync`) directly, and that method's own class
doc comment (`WidgetConversationService.cs:47-62`) frames a notification
failure as "logged and swallowed, never allowed to fail the already-
committed customer message" — but still synchronously awaited, not detached.
`ClaudeReplyService.GenerateReplyAsync` itself is `await`-ed directly from
`WidgetController.cs:205`, inside the customer's own HTTP request. **No
`Task.Run`/detached-task pattern, no queue, no second `BackgroundService`
tied to message events exists anywhere in `backend/`** — the only two
`BackgroundService`s in the app (`DataLifecycleBackgroundService`, 24h sweep;
`OrderCancellationReconciliationBackgroundService`, 5-min sweep) are both
periodic all-rows sweeps, not enqueued-per-event workers, and neither is
wired to anything message- or conversation-level. A true non-blocking
"classify without delaying the customer's response" design does not have
infrastructure to build on and would need new, real background-job
infrastructure (a task queue + a hosted worker, at minimum) — exactly what
the owner-decision doc says must not be introduced silently as a side effect
of this decision.

**What happens on classification failure (both approaches):** in the
piggyback-prompt approach, a missing/malformed classification block should
degrade to "no tags assigned this turn" — the customer-facing reply text
(everything before the delimiter) is already fully formed and independent,
so a parse failure need not fail the reply, mirroring this codebase's
existing "RAG retrieval failure degrades gracefully, never breaks the reply"
precedent (`ClaudeReplyService.cs:140-164`). In the forced-second-call
approach, the classification call is a separate, already-decoupled operation
— a failure there (rate limit, 5xx, timeout) can be caught and swallowed
exactly the way `ToolDispatchService.cs:76-101`'s existing typed catch chain
already does for its own Claude call, with zero effect on the already-
independent customer-facing reply.

**Owner decision (addendum): confirmed.** A forced single-tool call
(Finding 3), synchronously `await`-ed in-request immediately after the
customer-facing reply path completes (matching this codebase's only existing
side-effect convention, Finding 4) — the recommendation above — is adopted
as-is. It avoids the prompt-parsing/customer-facing-leak risk of Finding 2
entirely, and avoids introducing new background-job infrastructure, at the
accepted cost of a second Claude round-trip's latency and token spend on
every customer message.

**Added condition (addendum): structured logging for the classification call.**
The owner's stated reasoning: since there is no usage/cost-tracking
infrastructure in this codebase yet (checklist Step 27, "Usage Tracking," is
still unstarted), "accept the added cost as a v1 tradeoff" must be a
**visible, revisitable number**, not an untracked one — this does not need a
dashboard or any new infrastructure, only a log line.

**Mechanism, traced against existing precedent (no dedicated latency/token-usage
logging precedent exists anywhere in this codebase today — this is new, not
a reuse of an existing pattern; confirmed via a repo-wide search for
`Stopwatch`/latency-style `ILogger` calls, zero hits):**

- **Latency**: wrap the classification call in a `System.Diagnostics.Stopwatch`
  (or `Stopwatch.GetTimestamp()`), started immediately before the
  `messageService.Create` call and stopped immediately after it returns
  (success or exception) — measuring the Claude round-trip specifically, not
  anything else in the surrounding method.
- **Token usage**: the Anthropic C# SDK's `Message` response type exposes a
  `Usage` object with `InputTokens`/`OutputTokens` — confirmed present in
  the installed SDK by the same DLL-metadata check used for `ToolChoice`
  above (`~/.nuget/packages/anthropic/12.42.0/lib/net9.0/Anthropic.dll`
  contains `Usage`/`InputTokens`/`OutputTokens` members). The exact property
  path on the `Message` object this project's SDK version exposes should be
  confirmed directly against the SDK at implementation time rather than
  assumed further here — this pass confirms the data exists in the SDK, not
  its exact access shape.
- **Log shape**: one structured `ILogger` entry per classification attempt,
  following this codebase's existing message-template convention (e.g.
  `ClaudeReplyService.cs:160-162`'s
  `logger.LogWarning("... {ExceptionType} ... {ConversationId}", ...)` shape)
  — fields for `ConversationId`, `BusinessId`, elapsed milliseconds, input
  tokens, output tokens, and outcome (success / `ToolChoiceTool` returned an
  unparseable result / Claude call failed). Logged unconditionally (success
  and failure both), at `LogInformation` on success and `LogWarning` on
  failure, mirroring the severity convention already used for the RAG
  retrieval degrade-gracefully path (`ClaudeReplyService.cs:140-164`).
  No new metrics/telemetry infrastructure — this is exactly the existing
  `ILogger<ClaudeReplyService>`/`ILogger<ToolDispatchService>` pattern
  already injected into both services, reused for a new call site.

---

## B. How is core-vs-custom taxonomy represented in the schema? — LOCKED (owner addendum): closed vocabulary, no invention

Traced against `NIVORA_SCHEMA_AND_LOGIC_REFERENCE.md` §11's own stated
conventions and existing precedent in this codebase, not proposed from
scratch:

**Existing precedent for exactly this shape** — "known, first-party set of
string values, extensible without a migration" — already exists and is
already in production: `ConversationChannels`
(`backend/Constants/ConversationChannels.cs:12-17`), a plain `static class`
of `const string` values (`Website`, `Email`, with a comment marking where
future values get added), backing the plain-string `Conversation.Channel`
column with no DB-level enum/CHECK constraint. `AuditEntityTypes`/
`AuditEventTypes` follow the identical shape for a larger, still-growing set
of values. This is precisely §11's own documented rule: *"String + `static
class` constants for open-ended, growing value sets... no migration needed
to add new values."*

**Recommendation (not decided):** a `NivoraCoreTags` (naming TBD) `static
class` of `const string` values seeded from the owner's illustrative list
(`order`, `shipping`, `refund`, `payment`, `technical`, `complaint`,
`general`), with `Conversation.Tags`/`TagModeMapping.Tag` staying exactly
the plain string/`text[]` columns they are today — **zero schema change**.
"Core" vs. "custom" becomes a derived property at read time (is this string
present in the constants class?), not a stored fact. A business's custom
tags are then naturally just whatever distinct `Tag` values exist in that
business's own `TagModeMapping` rows that aren't in the core list — no new
column needed to know what a business has added, since `TagModeMapping` is
already the one place a business's tag vocabulary is recorded
(`TagModeMappingsController.cs`, already `OwnerOnly`-gated, already audited).
**A useful, non-obvious side effect of this shape**: if a business defines a
custom `TagModeMapping` row using a string that happens to match a core tag
(e.g. a business creates its own `refund` row with different `Mode`), that
is not a conflict to resolve — it is simply that business's policy override
for the core `refund` tag, which is presumably the desired behavior, not an
edge case needing special handling.

**Alternative considered and not recommended:** a separate core-taxonomy
reference table (`CoreClassificationTag` or similar, one row per core tag).
Would only earn its cost if core tags need their own metadata beyond a
label — a localized display name, an icon, a description, ordering — none
of which any part of this discovery/decision process has identified a need
for. Introduces a migration, seed data, and (if any code ever needs to
validate "is this a real core tag" against the DB instead of the constants
class) a redundant source of truth alongside the constants class. Rejected
per §11's own explicit preference for the constants-class shape for this
category of data, not because it's unworkable.

**Owner decision (addendum): closed vocabulary, no invention.** The
classifier may only assign tags from the union of the Nivora core vocabulary
(`NivoraCoreTags` constants, per the recommendation above) and that specific
business's own already-configured `TagModeMapping.Tag` values. It may never
invent a new tag. The addendum requires this mechanism to be **deterministic
and tenant-scoped, explicitly not left implicit in the prompt** — resolved
below.

**Mechanism — the allowed-tag list is a per-call, tenant-scoped JSON Schema
`enum`, not prose, with mandatory server-side re-validation:**

- **Not prose-only, by deliberate deviation from this codebase's existing
  tool-schema convention.** Every existing `IDispatchTool`'s `InputSchema`
  constrains enum-like arguments via a plain `type: "string"` property with
  the valid values only named in the human-readable `description` string —
  e.g. `OrderCancelProposalTool.InputSchema`'s `reason` property
  (`OrderCancelProposalTool.cs:65-69`): *"The cancellation reason: Customer,
  Payment, Fraud, Inventory, StaffError, or Other"* — no JSON Schema `enum`
  keyword anywhere in this codebase's tool schemas today. That existing
  convention is exactly what "not left implicit in the prompt" rules out for
  this call.

  **Schema shape — two properties, not one, per §D's already-locked
  `PrimaryTag`/`Tags` storage split; no positional inference anywhere in the
  contract:**
  - `primaryTag`: a single nullable string value, `enum`-constrained to the
    same allowed-tag list as below. Nullable because a conversation may
    genuinely have no clear primary classification yet.
  - `secondaryTags`: an array of strings, each `enum`-constrained to the same
    allowed-tag list, may be empty.

  Both use a real JSON Schema `enum` (a standard `System.Text.Json`-serialized
  `enum` keyword inside the same `JsonSerializer.SerializeToElement(new { type =
  ..., enum = ..., description = ... })` shape every existing `InputSchema`
  property already uses, per `OrderCancelProposalTool.cs:55-79` — no new
  serialization mechanism needed, just a property shape this codebase's
  existing tools have never happened to need before) — populated, at
  call-construction time, from a live query: `NivoraCoreTags`'s constant
  values **∪** a fresh
  `db.TagModeMappings.Where(tm => tm.BusinessId == businessId).Select(tm =>
  tm.Tag).Distinct()` read for the specific business this call is for. This
  is what makes the allowed set deterministic and tenant-scoped: it is
  computed fresh, per business, per call — never a shared/static schema
  the way every existing `IDispatchTool.InputSchema` is (each of those is a
  plain computed property, business-invariant, because none of them needed
  to vary per tenant before). **This is a real architectural difference
  from the existing tool pattern**, and reinforces the earlier finding (§A)
  that classification should not be shoehorned into `IDispatchTool`/
  `ToolDispatchService` — that family's tools all assume a static,
  business-invariant `InputSchema`, which this call's core requirement
  (owner-locked, not incidental) breaks.
- **A JSON Schema `enum` constrains the model but is not a guarantee — this
  codebase has already learned that lesson the hard way and the same
  discipline applies here.** The safety-model track's own history
  (`docs/handoffs/step-30-executor-skeleton-independent-verification.md`
  through `step-34`) found and fixed exactly this class of gap twice:
  `Enum.TryParse` without `Enum.IsDefined` silently accepting an
  out-of-range value on both the decode side
  (`ToolArgumentCanonicalizer.DecodeOrderCancellationArgs`) and the
  AI-facing propose side (`OrderCancelProposalTool.ParseInput`'s
  `ParseRequiredEnum<TEnum>` helper). The equivalent discipline here: after
  the classification call returns, **every returned value in both
  `primaryTag` (if non-null) and every element of `secondaryTags` must be
  checked against that same call's own allowed-set** (the exact core ∪
  custom union used to build the schema, recomputed or reused from the same
  request) and any value not in it dropped/rejected server-side before ever
  reaching `Conversation.Tags`/`PrimaryTag` — never trusted just because the
  schema asked for an enum. This mirrors `ParseRequiredEnum<TEnum>`'s own
  precedent: schema/type hints reduce but do not eliminate the need for an
  explicit server-side membership check on AI-supplied input. See
  "Validation Outcomes" below for what happens when this check fails
  partially or entirely.

**Normalization and dedup rules for building the allowed-tag union —
specified explicitly, not left implicit.** The discovery pass's own §3
finding is the reason this needs stating plainly rather than assumed:
`TagModeMappingsController.Create`/`Update` write `request.Tag` completely
unnormalized today (`TagModeMappingsController.cs:100`, `:146` — no
`.Trim()`, no casing transform), so a business's real `TagModeMapping` rows
may already contain inconsistent casing/whitespace (`"Refund"`, `"refund "`,
`"REFUND"` could all exist as distinct rows for the same business). Building
the per-call `enum` array directly from raw `Tag` values without normalizing
would surface visible duplicate/inconsistent entries to the model and risks
the exact kind of split-identity confusion §B's core-vs-custom discussion
above explicitly said should collapse into one entry, not fragment into
several.

- **Rule**: normalize every value — both `NivoraCoreTags` constants (already
  authored lowercase, so a no-op for them) and every business's
  `TagModeMapping.Tag` value pulled into the union — via `.Trim().ToLowerInvariant()`
  before comparison, deduplication, or inclusion in the schema's `enum`
  array. This is not a new convention invented for this step: it is exactly
  `NIVORA_SCHEMA_AND_LOGIC_REFERENCE.md` §3.8's own already-documented intent
  ("lowercase/kebab-case recommended... normalize... consistently"), and
  exactly the transform `ConversationModeService.ComputeBaseModeAsync`/
  `ApplyMinimumFloorAsync` already apply at read time today
  (`ConversationModeService.cs:61`, `:84`) — this reuses that existing
  transform rather than introducing a second one.
- **Casing/whitespace collisions within a business's own custom tags**
  (`"Refund"` and `"refund "` as two separate rows): both normalize to the
  same string and collapse to **one** `enum` entry — the union is built from
  a `Distinct()` over the normalized values, not the raw ones.
- **Custom-vs-core collisions** (a business's custom `"Refund"` row against
  the core `refund` constant): normalizes to the same single entry, for the
  same reason — consistent with, and not a special case beyond, the
  "not a conflict, it's a policy override" framing already established
  above for this scenario.
- **What gets persisted**: the classifier's chosen tag is written to
  `Conversation.Tags`/`PrimaryTag` (§D) in its already-normalized
  (trimmed, lowercased) form — never echoing back whichever raw-cased
  `TagModeMapping` row happened to produce the match. This keeps
  `Conversation.Tags` itself consistently normalized going forward, even
  though the underlying `TagModeMapping` rows it was matched against may not
  be.
- **This does not, by itself, fix `TagModeMappingsController`'s own
  write-time normalization gap** — existing and future `TagModeMapping` rows
  keep whatever raw casing/whitespace they were saved with; only this
  ephemeral per-call union-construction step normalizes, defensively, for
  comparison purposes. Retroactively adding `.Trim().ToLowerInvariant()` to
  `TagModeMappingsController.Create`/`Update` themselves would close the
  underlying gap for good (finally implementing §3.8 for real, not working
  around its absence forever) — flagged as a worthwhile, small, adjacent
  cleanup, but it touches already-shipped Step 13A code outside this step's
  own new surface, so it is called out here rather than folded in
  unilaterally; the owner should confirm whether it belongs in this step's
  implementation pass or as its own separate small follow-up.

**Validation Outcomes — what happens when server-side membership validation
fails, partially or entirely. Specified explicitly, per owner addendum, so
implementation does not have to invent this behavior.**

- **All returned tags invalid — a distinct, named outcome, not folded into
  "unparseable result."** If `primaryTag` (when non-null) and every element
  of `secondaryTags` all fail the allowed-set membership check — the model
  returned a structurally valid response, but named nothing that actually
  exists in that call's own enum — this is a **different failure mode from
  an unparseable response** (Finding 3/§A: a malformed or missing
  `ToolChoiceTool` result) and must be logged as its own named outcome
  value, `all_tags_invalid`, in the structured-logging `outcome` field §A
  already specifies. Rationale for keeping it distinct from `unparseable
  result`: they diagnose different failures — `unparseable result` means the
  model didn't produce a usable structured response at all (a
  transport/format problem); `all_tags_invalid` means it did, but every
  value it chose was hallucinated against a live, correctly-constructed
  `enum` it was actually given (a model-behavior problem, the same class of
  concern §B's `ParseRequiredEnum<TEnum>` precedent already exists to catch
  and make visible, not silently absorb). Conflating the two into one
  outcome value would hide which failure mode is actually occurring in
  production, defeating the visibility §A's logging requirement exists for.
  **This is also explicitly distinct from a genuine, deliberate "no tags
  apply" classification** — `{primaryTag: null, secondaryTags: []}` is a
  normal, valid, successful response (the model considered the allowed set
  and chose nothing), never logged as `all_tags_invalid`. `all_tags_invalid`
  applies only when the model *did* name one or more values and *all* of
  them failed the membership check — never when it validly returned nothing.
  On this outcome: `Conversation.Tags`/`PrimaryTag` are left unchanged (no
  write), exactly as a "no tags assigned this turn" degrade per §A's own
  existing failure-handling framing — the only difference from that
  existing framing is that this specific condition gets its own named,
  observable log outcome rather than being indistinguishable from any other
  failure.
- **Null or invalid `primaryTag` with one or more valid `secondaryTags` —
  Option A (accepted partial result) is adopted.** When `primaryTag` is
  `null`, or is non-null but fails the membership check, while
  `secondaryTags` contains at least one value that passes validation: persist
  `PrimaryTag = null` and `Tags` populated from the valid secondary entries
  (normalized, per §B's normalization rules). This is **not** treated as a
  classification failure and does not receive the `all_tags_invalid` (or any
  other failure) outcome — it logs as an ordinary successful classification
  attempt. **Reasoning**: §D already established that `PrimaryTag` is "purely
  an additional, independent fact about the conversation," read only by
  display/UI concerns, while `ConversationModeService`'s actual safety-relevant
  matching already "treats all matching tags as equally relevant via
  strictest-match" regardless of primary/secondary status
  (`ConversationModeService.cs:53-92`, via `Aggregate(..., Stricter)`). A
  missing or invalid `primaryTag` alongside genuinely valid `secondaryTags`
  loses only a cosmetic labeling fact, not anything the mode ratchet (§C)
  depends on — discarding valid, safety-relevant secondary-tag data just
  because the primary slot was empty or wrong would be strictly worse than
  using what validated correctly. Option B (treating this as a degraded/failed
  outcome) was considered and rejected on exactly this basis: it would throw
  away real, validated signal for no safety benefit, since nothing about the
  ratchet or the mode-gating logic depends on a primary tag existing at all.

---

## C. How do primary/secondary, evolving tags interact with `ConversationModeService`'s one-way mode ratchet? — LOCKED (owner addendum): persist the ratchet

**This is the finding most likely to be a real surprise relative to the
owner-decision doc's own framing, and is flagged accordingly.**

The owner-decision doc's question C describes "this existing invariant" as
something that "must not be silently broken" by tag changes, and asks
whether "a tag change trigger[s] full mode recomputation, or only allow[s]
it to move stricter" — phrased as if there is an existing cross-time
behavior to preserve. **Traced against the real implementation, there is
not.**

`ConversationModeService.ComputeEffectiveModeAsync`
(`backend/Services/ConversationModeService.cs:22-51`) is a **pure function**:
given a `Conversation` (and, transitively, that business's current
`TagModeMapping` rows and the conversation's current `Level2VerifiedUntil`/
identity state), it computes and returns a `ConversationMode` fresh, every
time it's called. Steps 1→2→3→4 (base mode → refund-limit no-op → minimum
floor → Level 2 override) only-ever-stricter ordering is real and correctly
implemented — **but only within a single invocation.** Nothing in this
class, or anywhere else in the codebase, persists a previously-computed
mode. `Conversation` (`backend/Models/Conversation.cs`) has no
`EffectiveMode`/`ComputedMode`/similar column at all — confirmed by direct
read of the full model. And per the discovery pass and re-confirmed here,
`ComputeEffectiveModeAsync` has **zero callers** anywhere
(`grep -rn "ComputeEffectiveModeAsync"` returns only the interface
declaration, the implementation, and the DI registration at `Program.cs:244`).

**So the "one-way ratchet" as it exists in source today is entirely an
intra-call ordering guarantee** ("step 3 can only make step 1's result
stricter, within this one computation"), **not** a cross-time guarantee that
a conversation, once computed as `Manual`, can never be recomputed as
something more permissive later. There is no earlier "more permissive"
history for anything to protect, because nothing has ever been computed and
stored. The dev plan's own §6/§3.1 language ("each step can only make the
mode stricter, never revert to something more permissive established by an
earlier step") is accurate as written — but "earlier step" means an earlier
*step in the same call*, not an earlier point in the conversation's
lifetime. This is worth stating plainly since the owner-decision doc's own
phrasing could otherwise be read as describing a stronger, already-built
safety property than actually exists.

**This matters concretely once classification starts actually calling this
service (owner decision 1 — wiring it in is explicitly in scope) and tags
are allowed to evolve mid-conversation (owner decision 4):** if
`ComputeEffectiveModeAsync` is simply called fresh at each decision point
(e.g. before every `ClaudeReplyService` turn) against whatever
`Conversation.Tags` currently holds, **and** classification's own tag
storage is ever additive-only (owner decision 4's own illustrative example —
"starts as `shipping`, becomes `shipping` + `refund`" — never removes an
earlier tag), then in practice the effective mode can also only ever get
stricter over the conversation's lifetime, as a natural consequence of the
tag set only growing plus each individual computation's own correct
intra-call ordering. **But this safety property would then be an emergent
consequence of "classification never removes tags," not something
`ConversationModeService` itself enforces or protects.** If any future code
path ever replaces or prunes `Conversation.Tags` — a bug, a staff
correction (owner decision 5) that edits rather than only adds, a
reclassification that decides an earlier tag no longer applies — a plain
fresh recompute would silently and correctly (by this service's own,
unchanged logic) produce a *less* strict mode than a previous computation
did, with nothing anywhere noticing or preventing it. Given no consumer
currently exists to have ever surfaced this gap, it has never mattered
until now.

**Owner decision (addendum): direction 2 is locked — persist the ratchet,
do not rely on additive-only discipline.** The owner's own stated reasoning
tracks this document's own §-Cross-cutting finding exactly: since staff
correction (locked, in scope, owner decision 5) can remove a tag, "tags are
only additive" was never actually available as a reliable invariant to lean
on — direction 1 above was only ever as safe as the staff-correction
feature happening to never support removal, which isn't a real constraint.
Direction 1 is rejected; direction 2 is adopted.

**Locked mechanism, specified for implementation:**

- **New column: `Conversation.EffectiveMode`** (`ConversationMode`,
  non-nullable, `HasConversion<string>()` per §11's real-C#-enum convention
  for small closed sets — the same convention `ConversationMode` itself
  already uses elsewhere per `NIVORA_SCHEMA_AND_LOGIC_REFERENCE.md` §11).
  Default `ConversationMode.Auto` (the enum's own weakest value, per
  `ConversationModeService.Stricter`'s `Math.Max` ordering,
  `ConversationModeService.cs:128-129`) — a safe backfill for every existing
  conversation, since none of them have ever had a computed mode before this
  step's own first real caller.
- **New migration** adding that column, following this repo's existing
  migration-naming convention (e.g. `AddConversationEffectiveMode`).
- **`ComputeEffectiveModeAsync` itself is unchanged** — it stays exactly the
  pure function it is today (`ConversationModeService.cs:22-51`), preserving
  its existing, already-correct intra-call ordering and its existing test
  contract untouched.
- **New method on `IConversationModeService`**, e.g.
  `ComputeAndRatchetEffectiveModeAsync(Conversation, CancellationToken)`:
  internally calls the unchanged `ComputeEffectiveModeAsync` to get
  `computedMode`, then sets
  `conversation.EffectiveMode = Stricter(conversation.EffectiveMode, computedMode)`
  — reusing the existing private `Stricter` helper
  (`ConversationModeService.cs:128-129`; promote it from `private static` to
  `internal`/expose it via this new method, no change to its own logic) —
  and returns the new, persisted value. Matches this codebase's established
  pattern of a service mutating a tracked entity and leaving `SaveChangesAsync`
  to the caller's own transaction (e.g. `WidgetConversationService`'s
  `conversation.LastMessageAt = now;` at `WidgetConversationService.cs:164-165`,
  committed by the caller's own `SaveChangesAsync`, not internally).
- **This directly satisfies the owner's own stated requirement with no new
  branching logic**: because `Stricter`/`Max` is symmetric regardless of
  which side changed, a staff correction that *removes* a tag lowers what
  the **next** `computedMode` will be, but `Stricter(existingEffectiveMode,
  lowerComputedMode)` is simply `existingEffectiveMode` unchanged —
  the persisted value cannot move backward as a side effect of a tag
  change, staff-initiated or classification-initiated, without any
  special-casing of "who changed the tags" anywhere in this method. Staff
  correction (owner decision 5) is free to add or remove entries in
  `Conversation.Tags` itself; it must **never** write to
  `Conversation.EffectiveMode` directly — only
  `ComputeAndRatchetEffectiveModeAsync` may ever advance that field, and
  only upward.

**Migration, default value, and a worked example.**

- **Behavior for existing conversations when the migration adds the column:**
  every existing `Conversation` row is backfilled to `ConversationMode.Auto`
  at migration time — the enum's own weakest value. No existing conversation
  ends up in a stricter state than it was in before this migration (which had
  no concept of `EffectiveMode` at all), and none is silently left `NULL`
  (the column is non-nullable).
- **Default is both a DB-level default and an EF/C# model default — not
  either/or, and this is the codebase's own established idiom for exactly
  this situation, not a new pattern.** Traced against the one directly
  comparable precedent, `Business.CancelOrderToolEnabled`
  (`backend/Migrations/20260906142311_AddBusinessCancelOrderToolEnabled.cs:14-18`):
  its migration calls `AddColumn<bool>(..., defaultValue: false)`, which
  Postgres materializes as a real column `DEFAULT` — this is what backfills
  every pre-existing row at migration time *and* remains the column's
  standing default for any future insert that omits it. `Conversation.EffectiveMode`'s
  migration should do the same:
  `AddColumn<ConversationMode>(..., defaultValue: ConversationMode.Auto)`.
  Separately, every existing enum property on `Conversation` also carries
  its own **C# property initializer** as the in-application default —
  `Status { get; set; } = ConversationStatus.Open` and
  `HandledBy { get; set; } = HandledBy.Ai` (`Conversation.cs:26`, `:37`) —
  which is what a freshly-constructed `Conversation` object gets in
  application code (`WidgetConversationService.PostMessageAsync`'s
  `new Conversation { ... }`, `WidgetConversationService.cs:136-147`, never
  explicitly sets `Status`/`HandledBy` either, relying on exactly this).
  `EffectiveMode { get; set; } = ConversationMode.Auto` should follow the
  identical convention. Both layers are needed for the same reason both
  already exist for `Status`/`HandledBy`: the DB default backfills history
  and protects any future raw insert; the C# default is what every new
  in-process `Conversation` actually gets before it's ever saved.
- **This is a deliberate "start most-permissive" choice, not an oversight —
  stated explicitly, per the request for a one-line note:** there is no
  computed information yet to justify anything stricter for a conversation
  `ComputeAndRatchetEffectiveModeAsync` has never run against, and starting
  at any value other than the ratchet's own floor would be arbitrary. This
  is safe specifically because nothing currently reads/enforces
  `EffectiveMode` as an autonomy gate at all yet (wiring a real consumer is
  in scope per owner decision 1, but its exact call site is deferred to
  implementation, per this document's own closing section) — a bare
  `Auto` default is not itself evidence that a conversation is actually safe
  for unattended AI action; whatever future call site enforces this field
  must call `ComputeAndRatchetEffectiveModeAsync` before relying on it for
  any conversation it hasn't run against yet, never treat an unexamined
  `Auto` as a cleared conversation.
- **Worked example, confirming an existing `Auto` row correctly ratchets to
  `Manual` on first classification:** take a `Conversation` created before
  this migration, backfilled to `EffectiveMode = Auto`, `Tags = []`,
  `PrimaryTag = null`. A customer message triggers classification, which
  (per the two-property schema and closed-vocabulary mechanism in §B)
  returns `{ primaryTag: "refund", secondaryTags: [] }`, both values passing
  membership validation. Say `refund` is set stricter, e.g. a business's
  `TagModeMapping` row `{ Tag = "refund", Mode = Manual, IsMinimumFloor =
  false }`. Sequence:
  1. Classification writes `Conversation.PrimaryTag = "refund"` and
     `Conversation.Tags = ["refund"]` (per §D's storage shape: `Tags` holds
     the normalized union of `primaryTag` and `secondaryTags` — here just
     the one value, since `secondaryTags` was empty).
  2. `ComputeAndRatchetEffectiveModeAsync` is invoked. It calls the
     unchanged `ComputeEffectiveModeAsync`, which runs its existing 4 steps
     against `Tags = ["refund"]`: step 1 (`ComputeBaseModeAsync`) matches
     the `refund` → `Manual` mapping and returns `Manual`; steps 2/3/4 (no-op,
     no floor mapping present, Level 2 either already satisfied or not — either
     way `Stricter(Manual, x)` is at least `Manual`) leave it at `Manual` or
     stricter. `computedMode = Manual`.
  3. `Stricter(conversation.EffectiveMode, computedMode)` =
     `Stricter(Auto, Manual)` = `Manual` (`Math.Max((int)Auto, (int)Manual)`,
     `ConversationModeService.cs:128-129` — `Manual` is the enum's highest
     declared value, matching this service's own existing "Auto < Assist <
     Manual, using the enum's own declaration order" convention,
     `ConversationModeService.cs:12`).
  4. `conversation.EffectiveMode = Manual` is persisted.
  A second customer message that later reclassifies as just `shipping` (no
  `refund` mapping this time) would compute `Manual`'s opposite direction —
  `computedMode = Auto` — but
  `Stricter(existingEffectiveMode=Manual, computedMode=Auto) = Manual`:
  the persisted field does not move backward, exactly as intended.

**Concurrency: is the read-compute-write atomic against a concurrent
classification attempt, and what happens when staff correction and
classification run concurrently?**

Both questions get one answer, because they are the same class of race this
codebase has already built a real, tested mechanism for — reused here, not
invented fresh. **This is not a hypothetical concern to wave away**: two
concurrent `POST /api/widget/messages` calls for the *same* conversation
(a double-send, two open tabs) are exactly the scenario
`WidgetConversationService`/`ConversationsController` already had to solve
for `Conversation.HandledBy` — a plain unlocked read-modify-write on a
`Conversation` subfield is a real, demonstrated race in this codebase's own
history, not a theoretical one.

- **Without a lock, a genuine lost-update bug exists here.** Two concurrent
  classification calls for the same conversation could each read
  `EffectiveMode = Auto`, independently compute `Assist` and `Manual`
  respectively, and both call `SaveChangesAsync` — whichever commits last
  wins outright (plain last-write-wins on an untracked-for-conflicts
  property), which could leave the persisted value at `Assist` even though
  `Manual` was independently computed by the other, concurrent call. This
  would silently violate the whole point of the ratchet, not just produce a
  stale read.
- **Mechanism, reusing this codebase's own established per-conversation
  advisory-lock pattern, not a new concurrency-control idiom:**
  `AdvisoryLockKeyGenerator.ForConversationNotification(businessId,
  conversationId)` already exists precisely to serialize concurrent
  read-decide-write sequences against the same `Conversation`'s mutable
  fields — its own doc comment
  (`AdvisoryLockKeyGenerator.cs:44-69`) states it now guards *every* write
  to `Conversation.HandledBy`, both directions
  (`ConversationsController.CreateMessage`/`ReturnToAi`), precisely because
  "an unlocked writer could otherwise race a concurrent reversal into a
  genuinely ambiguous, non-serialized outcome." `EffectiveMode`/`Tags` are
  the same category of problem — a per-conversation, potentially-bidirectional
  (tags can be added or removed), externally-triggerable mutable field. Two
  options, not decided here as a naming detail: reuse this exact lock key
  (broadening its documented scope again, the same way Step 15 already
  broadened it once), or add a new, narrowly-scoped key following the exact
  same `PackToLong($"{businessId:D}:...:{conversationId:D}")` shape (e.g.
  `ForConversationTagState`). Either way, the pattern to follow is the one
  already proven at `ConversationsController.cs:277-296`/`:456-465`:
  `BeginTransactionAsync` → `pg_advisory_xact_lock` → a **fresh read** of the
  entity under the lock (`db.Entry(conversation).ReloadAsync()`, per the
  existing `ReturnToAi` precedent — never trust a copy read before the lock
  was acquired) → decide → write → commit.
- **This one mechanism answers both questions at once, by construction, not
  by two separate designs:** if classification, staff correction, *and* the
  `ComputeAndRatchetEffectiveModeAsync` ratchet step all acquire the same
  per-conversation lock around their own read-modify-write of
  `Tags`/`EffectiveMode`, then (a) two concurrent classification attempts
  serialize against each other — the second one's `Stricter()` call sees the
  first one's already-committed write, never a stale pre-lock value — and
  (b) a concurrent staff correction and classification attempt serialize the
  same way, so neither can observe the other's `Tags` write mid-flight or
  clobber its `EffectiveMode` write. **Recommended concrete shape**: every
  `Tags` mutation (classification's write, staff correction's write) is
  immediately followed, inside the same lock/transaction, by a call to
  `ComputeAndRatchetEffectiveModeAsync` — meaning there is exactly one place
  in the codebase that ever advances `EffectiveMode`, invoked uniformly
  after every `Tags` write regardless of who made it, and that place is
  never reachable outside the shared per-conversation lock. This is a
  recommendation for the implementation pass to follow, not a new open
  question — the lock-key-naming/reuse-vs-new-key choice is the only
  remaining engineering detail, in the same category as this document's
  existing "narrow engineering detail" closing note.

**Explicit scope exclusion (owner addendum — written into scope, not left as
prose):** manual staff downgrade of the persisted effective mode — a staff
member deliberately wanting to move a conversation from `Manual` back to
`Assist`/`Auto` — is **out of scope for Step 22**. No endpoint, UI action, or
service method introduced by this step may lower `Conversation.EffectiveMode`.
If this becomes a real product need later, it is its own separate,
future-scoped workstream, not an implicit consequence of shipping tag
correction now. See the "Scope Exclusions" section below and the checklist's
loose-ends entry recording this decision for future sessions.

---

## D. Storage shape for primary + secondary tags — CONFIRMED (owner addendum), unchanged

Current schema: `Conversation.Tags` (`List<string>` / Postgres `text[]`,
`Conversation.cs:53`). No ordering semantics are treated as meaningful
anywhere in existing code — `ConversationModeService.ComputeBaseModeAsync`/
`ApplyMinimumFloorAsync` (`ConversationModeService.cs:53-92`) both match
against the **full set** of tags and reduce via `Aggregate(..., Stricter)`
(strictest-of-all-matches), never treating any particular tag or array
position specially.

**Three shapes considered:**

1. **First-array-element convention** (`Tags[0]` is primary, the rest are
   secondary). Zero migration. Rejected as fragile, not recommended:
   nothing in the current column type, EF mapping, or any existing query
   enforces or even acknowledges ordering as meaningful — a future direct
   update, a `.Distinct()`, a set-based dedup, or simply appending in the
   wrong order silently corrupts "primary" with no error and no test would
   catch it structurally (only a behavioral test written specifically to
   check this convention would). This shape also does not self-document —
   a developer reading `Conversation.Tags` cold has no reason to know index
   0 is special.
2. **A separate `Conversation.PrimaryTag` column** (nullable `string`),
   alongside `Tags` continuing to hold the complete set (primary included).
   Minimal migration (one nullable column). Self-documenting. Requires
   **zero change** to `ConversationModeService`'s existing full-set matching
   logic, since `Tags` keeps meaning exactly what it means today — `PrimaryTag`
   is purely an additional, independent fact about the conversation, read
   only by whatever UI/logic cares about "the" primary classification
   (most plausibly a dashboard display concern, not a mode-computation
   concern, given §C's finding that mode computation already correctly
   treats all matching tags as equally relevant via strictest-match). Matches
   §11's own precedent for adding a narrow, purpose-specific column rather
   than restructuring an existing one when the new fact is genuinely
   independent of the old column's meaning.
3. **A normalized join table** (`ConversationTag(ConversationId, Tag,
   IsPrimary, Source, CreatedAt)` or similar). Most extensible — naturally
   supports future needs like "who/what assigned this tag" (AI vs. staff
   correction — relevant to owner decision 5), a per-tag confidence score,
   or a per-tag timestamp (relevant to owner decision 4's "evolves over
   time" framing, if a *history* of classification changes ever matters,
   not just the current state). Largest blast radius of the three: replaces
   `Tags`' existing `List<string>` shape entirely, which means every
   existing read of `Conversation.Tags`
   (`ConversationModeService.ComputeBaseModeAsync`/`ApplyMinimumFloorAsync`,
   both call sites) would need to change from reading a plain list to
   projecting from a related table, and any future direct SQL/EF query
   against tags becomes a join instead of an array scan. Not recommended
   as this step's default given nothing in the owner-decision doc's locked
   scope (which explicitly excludes "a general tagging-management system")
   currently demonstrates a need for per-tag provenance/confidence/history
   — but flagged as the shape to revisit if the owner's answer to open
   question 5's audit-trail expectations (does a staff correction need to
   show *what the AI originally said* before the correction, not just the
   corrected state?) turns out to require it.

**Owner decision (addendum): confirmed as-is, no changes.** Option 2
(`PrimaryTag` column + unchanged `Tags` array) is adopted exactly as
recommended — smallest change, zero risk to `ConversationModeService`'s
already-correct existing matching logic, consistent with §11's established
preference for a narrow additional column over restructuring an existing
one. Option 3 (a normalized join table) remains available to revisit later
if a future need for per-tag provenance/history emerges, but is not part of
this step.

---

## Cross-cutting item surfaced during this pass — now resolved by the C addendum

**Staff correction's minimal shape (owner decision 5) has a direct
dependency on C's answer.** This was flagged as an open coupling in the
first draft of this document; the owner's addendum resolves it directly by
locking C's persisted-ratchet direction (above). Staff correction may freely
add or remove entries in `Conversation.Tags` — including removing an
incorrectly-assigned tag entirely — without needing any special-casing of
its own, because `Conversation.EffectiveMode` is only ever advanced by
`ComputeAndRatchetEffectiveModeAsync`'s own `Stricter`/`Max` logic, never
written to directly by the correction endpoint. The scenario originally
flagged here (a staff removal silently un-ratcheting the persisted mode) is
now structurally prevented by C's mechanism, not by a discipline staff
correction has to separately uphold.

---

## Scope Exclusions

Recorded explicitly, per the owner's addendum, so this is a stated boundary
of Step 22's implementation rather than something a future session has to
reconstruct from git history:

- **Manual staff downgrade of `Conversation.EffectiveMode` is out of scope.**
  No endpoint, UI action, or service method shipped as part of Step 22 may
  lower a conversation's persisted effective mode (e.g. a staff member
  deliberately moving a conversation from `Manual` back to `Assist`/`Auto`).
  Only `ComputeAndRatchetEffectiveModeAsync`'s own upward-only `Stricter`
  logic may ever change `EffectiveMode`, and only upward. If a real product
  need for manual downgrade emerges later, it is a distinct, separately
  scoped future workstream — not an implicit consequence of shipping tag
  correction now. Also recorded in `NIVORA_FULL_PLAN_CHECKLIST_V2.md`'s
  Smaller Known Loose Ends section for discoverability outside this doc.
- **Staff correction stays narrowly scoped to tag correction itself** (owner
  decision 5's own guardrail, unchanged by this addendum): it must not grow
  into a general tagging-management system and must not expand into a
  broader dashboard redesign. The exact minimal API/UI shape is left to the
  implementation pass, per owner decision 5's own instruction ("Claude
  determines the smallest clean implementation").

---

## Explicitly not decided by this document

A, B, C, and D are now locked per the owner's addendum, with mechanisms
specified above in enough detail to implement, including the migration/
default-value behavior, a worked ratchet example, and the concurrency
mechanism for both concurrent-classification and staff-correction-vs-
classification races (all added in this revision). What remains genuinely
open:

- **One product-adjacent question, not purely mechanical**: whether
  retroactively normalizing `TagModeMappingsController.Create`/`Update`'s
  write path (closing §3.8's long-standing, pre-existing gap for real) is
  folded into this step's implementation pass or filed as its own separate
  follow-up (§B, normalization section). Everything else in this step's own
  design is unaffected either way — the per-call union-construction
  normalization is specified regardless of what `TagModeMappingsController`
  does.
- **Narrow engineering detail only, not requiring further owner sign-off**:
  the exact migration/column names, the exact log-field names for A's
  structured logging, the exact call site(s) inside
  `ClaudeReplyService`/`WidgetController` where
  `ComputeAndRatchetEffectiveModeAsync` and the classification call are
  invoked, and whether the concurrency lock (§C) reuses
  `AdvisoryLockKeyGenerator.ForConversationNotification` or adds a new,
  narrowly-scoped key.

**No code has been written in this pass.** This document still requires
explicit owner sign-off marking it ready before implementation begins —
locking B/C/D's mechanisms via the addendum, and closing the migration/
concurrency/normalization gaps in this revision, is not itself that
sign-off.
