# Step 27 Slice A1: errata 3 — Slice 3 post-design owner decisions

Addendum to `docs/handoffs/billing-usage-a1-invocation-capture-design-v11.md`, as amended by errata 1 (commit `506759c`, applied inline to v11 with a closing "Errata (post-Slice 1)" index) and errata 2 (commit `59a21ad`, standalone file `billing-usage-a1-invocation-capture-errata-2.md`). Like errata 2, this addendum is a standalone file and does not edit v11's text. Where this file and v11 or errata 2 differ, this file governs for the items below. v11 and errata 2 govern everything else.

This file records Slice 3 owner decisions, post-implementation rulings, and independent-verification results (sections 1-6) that v11 and the existing errata do not state and that can't be derived from them. Slice 3's design-level decisions (Q1, OD-2, OD-4, OD-5, OD-6, OD-8) are recorded in `docs/handoffs/billing-usage-a1-slice3-decisions-addendum.md`, against the Slice 3 design pass.

Each item is marked with its source:
- **(v11)**: stated by v11 itself.
- **(post-v11 owner decision, Slice 3)**: decided during Slice 3's design. v11 does not state it.
- **(derived)**: follows from a pre-existing codebase contract, not from v11 or a new decision.

Date: 2026-09-25.

---

## 1. No custom exception type carries the usage total

**(post-v11 owner decision, Slice 3)**

- **Decision:** A1 does **not** use a custom exception type to carry the accumulated provider usage total. This earlier decision still stands.
- **Provenance:** made by the owner in the advisory chat, outside v11 and outside this repo's errata trail. v11 §2:97 states only the requirement (the completed-batch running total must be available to the wrapper's recording path when the invocation throws), not the mechanism. Neither errata 1 nor errata 2 records this ruling. It must not be cited as coming from v11 or from errata 1 or 2.
- **Where the mechanism is decided instead:** the usage total is carried by a wrapper-passed accumulator (Slice 3 decisions addendum, Q1).

---

## 2. "Slice 4a" / "Slice 4b" labels

**(post-v11 owner decision, Slice 3; a planning convention, not an architectural decision)**

- Errata 2 §1 defines Slice 4 only as "Call-site migration, in two parts" (errata-2:23). It gives the parts no names, no split and no content.
- "Slice 4a" and "Slice 4b" are the owner's internal planning labels for those two parts. They carry no architectural weight.
- They must not be cited as if errata 2, or any errata, established them. Errata 2 §1 stays the source of record for the slice plan.

---

## 3. OD-2's null usage departs from v11 §2:79's definition of null

**(post-v11 owner decision, Slice 3)**

- **v11's definition:** v11 §2:79 says "`null` means no usage was obtained at all. For Voyage, this is the case where zero batches completed before the invocation failed."
- **OD-2 (Slice 3 decisions addendum, item 2):** when Voyage returns a successful response but `usage.total_tokens` is missing or unusable, the whole invocation row's usage is recorded as `null`. This applies uniformly to both `Succeeded` and `Failed` rows.
- **The departure:** under OD-2, a row can carry `null` usage even though usage *was* obtained for some of its batches, and a `Succeeded` row can carry `null` usage. v11 §2:79's literal definition allows neither. This is a deliberate departure from that definition, not an oversight.
- **Reason, as decided:** semantic consistency. The same underlying missing-usage event must not be represented differently depending on the invocation's eventual status.
- **Scope:** only the meaning of `null` changes, and only for the OD-2 case. v11's null-vs-zero invariant (§2:78-80) is unchanged: `0` still means the provider explicitly reported zero, and `null` and `0` are never conflated.

---

## 4. C9 test-strength correction pass: record and F1 ruling

**(post-v11 owner decision, Slice 3, for F1; the rest of this section records the C9 pass and its independent verification)**

Added 2026-09-28. C9 was an owner-directed, tests-only correction pass on the uncommitted Slice 3 worktree (`a1-slice3-provider-wrappers`, HEAD = merge-base = `525ad4b`). Only `backend.Tests/AnthropicInvokerTests.cs` changed (`b2c8f8f5…` → `819e1d1d…`); `backend/Services/AnthropicInvoker.cs` is unchanged (`a3a646d7…`). The mutants cited below were built by the verifier, not reused from the implementer. Each apply and restore was hash-verified. After each restore the relevant tests were green, and the full suite was then green on two runs (§4.6).

### 4.1 Three earlier C5–C8 gaps closed by C9

1. **Provider-exception rethrow at `AnthropicInvoker.cs:70`.** C9a (`AnthropicInvokerTests.cs:506-544`) asserts, by method name only, that the test-owned provider throw site is still in the stack trace of the exception the caller catches. It covers both failure-path routes. M-a changed the bare `throw;` at `:70` to `throw ex;` (`a4b21455…`). Both C9a cases failed on the stack-trace assertion (`:531`). After restore, the tests were green.
2. **Failure-log wording.** C9b is one assertion at `AnthropicInvokerTests.cs:445`, inside the existing C5 theory. It reads the log's `{OriginalFormat}` and pins the production text `"after a failed Anthropic call"` (`AnthropicInvoker.cs:62`). M-b changed that wording to `"after a succeeded Anthropic call"` (`bb70fa8c…`). Both C5 theory cases failed at `:445`. After restore, the tests were green. The success path's `"after a succeeded Anthropic call"` (`:91`) stays pinned by C8 (`:491`).
3. **Numeric-model fallback.** C9c (`AnthropicInvokerTests.cs:556-592`) pins that a numeric raw-JSON model is **recorded as the raw value passed to the SDK** (`"42"`), with no substituted placeholder. The value was **not** sent over the wire: the SDK rejected it before any HTTP send, and the counting handler observed zero sends. The test comment (`:550-551`) states that, in this case, OD-7's "wire-sent" model means the value passed to the SDK. M-c replaced the recorded value with a placeholder for a numeric model only, leaving null/absent handling unchanged (`a3f32764…`). C9c failed with expected `"42"` and actual placeholder. C5 and C8 stayed green. After restore, the tests were green.

### 4.2 Additional coverage found during C9 verification

This is not one of the three earlier C5–C8 gaps. The row-building catch at `AnthropicInvoker.cs:59-64` does **not** rethrow; it only logs. Both failure routes (row built / row unbuildable) leave through the single bare rethrow at `:70`.

C9a's second case pins that the unbuildable-row route (C5) still exits through the bare rethrow at `:70` with the provider's stack trace intact. The verifier's M-d mutation inserted a `throw ex;` into the row-building catch (`:59-64`) (`7508e256…`). Only C9a(true) caught it: C9a(false) and C5 both stayed green, because `Assert.Same` can't tell a reset stack trace from a preserved one.

The inserted `throw ex;` existed only in the mutant. It is not a production rethrow.

**Stack-trace coverage, stated exactly:** C9a verifies stack-trace preservation on both failure-path routes (row built / row unbuildable), both of which exit via the single rethrow at `:70`. The `:42` rethrow (F1) remains unpinned.

### 4.3 F1: `AnthropicInvoker.cs:42`, accepted known test-strength gap

- **The site:** the bare `throw;` at `AnthropicInvoker.cs:42` catches and rethrows `AnthropicNotConfiguredException`, which originates from `MissingAnthropicApiKeyMessageService.Create`. This is a separate gap from C9a.
- **Provenance:** the implementer raised this gap. C9 verification independently confirmed it. The verifier's mutant changed the bare `throw;` to `throw nce;` (`efdc99e1…`), and the full suite still passed during that mutation.
- **What is still pinned:** the exception type, by T-A5 (`CreateMessageAsync_AnthropicNotConfigured_Rethrows_RecordsNothing`). No locked Slice 3 decision requires stack-trace preservation at `:42`, and no test asserts it.
- **Reachability:** `AnthropicInvoker` has no production caller yet. Its only production reference is the DI registration at `Program.cs:262`.
- **Owner ruling:** accepted as a known gap. **C10 is not authorized.**
- **Rationale:**
  - Low severity: a regression would mainly lose diagnostic stack frames originating from `MissingAnthropicApiKeyMessageService`.
  - Unreachable today: there is no production caller.
  - The exception type remains pinned.
  - No locked stack-trace ruling exists for this path.
  - Another verification pass is not justified for a gap this small.
- **Future pickup:** if `AnthropicInvoker` gains a real production caller in Slice 4a, add a stack-trace assertion for `nameof(MissingAnthropicApiKeyMessageService.Create)`.
- **Status:** nonblocking and intentionally open.

### 4.4 F2: numeric-model rejection is observed SDK behavior

- Under Anthropic SDK 12.42.0, a numeric model (`"model": 42`) causes `AnthropicInvalidDataException` (inner `System.Text.Json.JsonException`, message `42 must be of type System.String`), with zero HTTP sends.
- Observed call chain: `MessageServiceWithRawResponse.Create` → `MessageServiceHelpers.WarnIfDeprecatedThinkingConfig` → `ApiEnum.Raw()`. The exception is thrown from `ApiEnum.Raw()`, called by the deprecation-warning helper during `Create`.
- This is an observed side effect of that SDK helper, not a deliberate validation step in the SDK. It is observed behavior of SDK 12.42.0, not a guaranteed SDK contract.
- C9c's `sends == 0` assertion is intended to fail loudly if a future SDK version changes this behavior.
- That is a separate matter from C9c's invariant (§4.1 item 3): the raw numeric value passed to the SDK is recorded, with no placeholder.

### 4.5 F3: C9 test count

- C9 added **3 new test cases**: C9a ×2 and C9c ×1.
- C9b is **not** a new test case. It is one assertion added inside the existing C5 theory.
- The suite count went from 1385 to 1388 (1385 + 3).

### 4.6 Full suite and known flakes

- **Full suite:** 1388 passed / 0 failed / 3 skipped on the implementer's two runs and again on the verifier's two runs after a `--no-incremental` rebuild. The 3 skips are the `[LiveClaudeFact]` tests.
- **Known flakes:** the two pre-existing flakes (`KnowledgeRetrievalServiceTests.RetrieveAsync_HnswTenantFilterRecall_…` and `ConsumedSignedIdentityTokenCleanupServiceTests…`) stay on the backlog.
- They did not reproduce during those four clean runs or during the M-e mutation run. That does **not** establish that they were fixed or eliminated.

### 4.7 Scope of this errata edit

This errata pass is docs-only. It adds this section and changes no Slice 3 source or test file. After the edit, the 17 protected Slice 3 source/test files in the `a1-slice3-provider-wrappers` worktree were hashed and compared by value against `c9_end_hashes.txt` (`Nivora-scratch\a1-slice3\c9_prepass\`): **17/17 hashes match `c9_end_hashes.txt`.**

---

## 5. Slice 3 owner rulings not previously recorded; addendum §10 status

**(post-v11 owner decision, Slice 3)**

Added 2026-09-28, docs-only. The rulings below are the owner's recorded rulings and are the authority for this section. Each was cross-checked against the shipped source in the `a1-slice3-provider-wrappers` worktree, and no conflict was found. Source comments are cross-check evidence only; they show what shipped and do not replace the rulings. Sections 1-3 are not restated here.

### 5.1 - OD-3 locked error-category mapping

**Owner ruling (OD-3), recorded as written.**

Anthropic:
- 429 → `RateLimited`
- 5xx, including 529 → `ServerError`
- other 4xx → `InvalidRequest`
- `AnthropicIOException` → `Unknown`
- caller cancellation → `Cancelled`
- other cancellation/timeout → `Timeout`
- anything else → `Unknown`

Voyage:
- `Cancelled` / `Timeout` / `Unknown` only
- Voyage 429/5xx → `Unknown`
- status transport option (c) accepted
- `VoyageEmbeddingException` unchanged
- known gap deferred

**What it supersedes:**
- `billing-usage-a1-slice3-design-pass.md:232-246` held an earlier *proposed* OD-3 candidate. That candidate did not include 529, and it listed Voyage status-transport options (a)-(c) without choosing one.
- `billing-usage-a1-slice3-decisions-addendum.md:188-193` (§10) still lists OD-3 as open.
- The locked owner ruling above supersedes both.

**Cross-check:** `AiErrorCategorizer.cs:14-28` states the same mapping, including "5xx (incl. 529)" and "anything else → Unknown", and says Voyage "HTTP 429/5xx land as Unknown — a known deferred gap". Slice 3's change to `IVoyageEmbeddingClient.cs` adds a method only; `VoyageEmbeddingException` is unchanged.

### 5.2 - OD-7 locked model-recording rule

**Owner ruling (OD-7):**
- The recorded model is the literal value passed to the SDK on every path, including the raw-JSON fallback.
- No placeholder model is ever recorded.

"Wire-sent" here means the value passed to the SDK (see the test comment at AnthropicInvokerTests.cs:550-551), not necessarily a value that reached the network.

Two facts stay distinct:
1. OD-7's general model-recording invariant, stated above.
2. C9c's numeric-model observation, already recorded in §4.1 item 3 and §4.4 and not restated here: the numeric model was recorded as the raw value passed to the SDK, and zero sends were observed.

**Cross-check:** `AnthropicInvoker.cs:22-24` records the literal the caller sent, never the response's model. `ReadModelSent` (`:133-143`) falls back to the raw JSON. It is called only when building the row (`:106`), after the provider call returns or throws. Both row-build guards state "no placeholder model, OD-7" (`:49`, `:76`). This is consistent with OD-6, which rules that there is no wrapper-level pre-check (billing-usage-a1-slice3-decisions-addendum.md:83) and concerns when a row is written, not when the model is read.

### 5.3 - Other Slice 3 owner rulings

- **(a) Voyage status transport:** option (c) accepted, as in 5.1.
- **(b) Unusable 2xx usage after completed batches:**
  - OD-2 wins: usage is `null` and `IsUsagePartial = false`.
  - Retaining partial tokens would be a new OD-2 change.
  - The OD-2/OD-4 boundary is already recorded at `billing-usage-a1-slice3-decisions-addendum.md:55`, and the departure from v11 §2:79 is in §3 of this file.
  - **Cross-check:** `VoyageUsageAccumulator.cs:34-40` returns a `null` total once any completed batch's usage was missing or unusable.
- **(c) Empty input:** empty input -> no row: specified in v11 section 2:70 / T-I6 and ratified as the Slice 3 ruling.
  - `billing-usage-a1-slice3-decisions-addendum.md:94` had left this case to implementation.
  - **Cross-check:** `VoyageEmbeddingInvoker.cs:12-13`. T-I6 is at `billing-usage-a1-slice3-design-pass.md:396`.
- **(d) V-Q2:** recorded separately in 5.6.

### 5.4 - C5-C8 rulings

- **C5, failure-path row-build guard:** if building the telemetry row fails, log the exception type name only. Do not replace the original provider exception. Produce no row. The original exception leaves through the bare rethrow. (Cross-check: `AnthropicInvoker.cs:46-70`.)
- **C6:** Voyage recorder-throw guard tests cover the success path and the failure path. (Cross-check: `VoyageEmbeddingInvokerTests.cs:345`, `:363`.)
- **C7:** comment-only update replacing stale "OD-3 still open" comments with the locked OD-3 mapping. (Cross-check: `AiErrorCategorizer.cs:14-28` carries the locked mapping.)
- **C8, success-path row-build guard:** log the row-building exception. Produce no row. Return the same response instance. (Cross-check: `AnthropicInvoker.cs:74-99`.)

Governing principle, recorded as written: "The wrapper never changes the caller-visible outcome of the provider call."

C9 (section 4) added test coverage for C5's failure-path behavior; it made no production change.

### 5.5 - Addendum section 10 drift

- `billing-usage-a1-slice3-decisions-addendum.md:188-193` (§10, "Still open after this addendum") still lists OD-3, OD-7 and V-Q2 as open.
- OD-3 is superseded by 5.1.
- OD-7 is superseded by 5.2.
- V-Q2 remains open and deferred under 5.6.
- `AiErrorCategorizer.cs:14-15` already states that an errata records this status correction; this section is that record.
- The addendum itself is not edited.

### 5.6 - V-Q2

- The Voyage `usage.total_tokens` shape remains **REQUIRES VERIFICATION**.
- It was deferred after implementation. It is **not resolved**.
- **Follow-up:** a `[LiveVoyageFact]` test is required before Slice 4 wires call sites.
- That test is future work. This pass does not perform it, and nothing here implies that V-Q2 was verified.

17/17 hashes match c9_end_hashes.txt.

---

## 6. Voyage V-Q2 live observations

Added 2026-09-29, docs-only. The observations in 6.2 are **owner-supplied**, made on 2026-09-29 through manual PowerShell calls and live test runs. The raw call output is not stored in the repo. They are recorded here as owner-observed. Only the test file's input string, its `> 0` assertion, its gate, and the commit hashes were cross-checked against the repo. This section does not edit 5.6 and does not restate sections 1-5.

### 6.1 - Test and gate

- Commit `69b371061d536b03aec911f241e08ee684099b9b` added a `[LiveVoyageFact]` test (`backend.Tests/VoyageEmbeddingClientLiveTests.cs`) that makes one voyage-3 query embedding call through the real `VoyageEmbeddingClient`.
- It runs only when user-secrets hold `Voyage:EnableLiveIntegrationTests=true` and a non-blank `Voyage:ApiKey`. Otherwise it skips.
- Commit `a6b69b18067c9b95c03a0a536de8178ba310e8d1` changed its input to an ordinary sentence (see 6.2(b)/(c)).

### 6.2 - Observations, in order

- **(a) First live run:** HTTP 401 "Provided API key is invalid". This was a credential problem and gave no evidence about usage.
- **(b) Second live run, input "hello":** the call succeeded and assertions 1-4 held (no throw, one vector, length equal to `EmbeddingDimensions.VoyageThree` = 1024, one requested and one completed batch). The `usage.total_tokens > 0` assertion failed: the value was 0.
- **(c) Three manual raw calls** to `POST /v1/embeddings` with model `voyage-3`:
  - input "hello", input_type "query": usage `{"total_tokens":0}`.
  - input "The quick brown fox jumps over the lazy dog near the river bank.", input_type "query": usage `{"total_tokens":13}`.
  - input "hello" with no input_type: usage `{"total_tokens":1}`.
  - Each response had top-level fields `object`, `data`, `model`, `usage`. `usage` held only `total_tokens`. Each vector had length 1024.
- **(d) Corrected test (sentence input):** one live run passed all five assertions.

### 6.3 - What this establishes and does not establish

**Established, for these responses only:**
- voyage-3 returned a `usage` object containing `total_tokens` as a small non-negative integer.
- The value can be 0 for a non-empty short query input.
- The 1024 dimension held live.
- The client's parse path handled these responses.

**NOT established:**
- Why the 0 occurred. The cause is unknown, and this record does not offer one.
- Whether it depends on input length or input_type, or varies between identical requests.
- V-Q2 item 2 (full integer range), item 3 (never absent or null on a 2xx), and item 4 (other usage fields, beyond the three observed responses), all as listed at `billing-usage-a1-slice3-design-pass.md:210-214`.
- Behaviour for input_type "document".
- That every non-empty input yields `> 0`.

The corrected test's pass is one observation on one sentence. It is not a confirmation of V-Q2.

### 6.4 - Status

- V-Q2 is partially characterized and remains open.
- The 5.6 status (REQUIRES VERIFICATION, deferred) is not closed by this section. The follow-up test that 5.6 called for now exists and has been run once.
- The original assumption that a non-empty input always yields `usage.total_tokens > 0` is disproven by 6.2(b) and 6.2(c).
- No ruling in 5.1-5.3 is changed by this section.

### 6.5 - Open billing-semantics question (not decided here)

- Voyage voyage-3 reported `total_tokens = 0` for a non-empty short query input.
- Whether provider-reported usage is the authoritative source for Nivora's usage/billing, and how a provider-reported 0 should be treated, is an open owner question for later slices.
- No Slice 3 production behaviour changes. v11's null-vs-zero invariant (§2:78-80) is unchanged: `0` means the provider explicitly reported zero, and `null` means no usage was obtained, as qualified for the OD-2 case by section 3 of this errata.
