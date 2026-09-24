# Step 27 Slice A1: AI provider invocation and usage capture (design v11)

**Status:** Design v11 — call-chain attribution clarifications (`CorrelationId` scope confirmation, a trust-boundary rewrite with a locked mismatch-handling procedure, Voyage multi-batch usage semantics with a locked `IsUsagePartial` schema marker, and the `IKnowledgeRetrievalService.RetrieveAsync` signature change). Every owner decision raised across this pass is locked — none remain open. Not yet re-verified against live source in this exact form. Not implemented.

**Provenance:** v3 → v4 → v5 → v6 → v7 (verified CONFIRMED against live source) → v8 (folded in six locked owner decisions) → v9 (corrected four issues found in v8's fold-in, then re-verified read-only against live source) → v10 (fixed one residual `AiModels.cs` reference in §6, confirmed via a narrow re-verification pass) → v11 (this version). v10's record-shape, retention, and model-identifier content, and its independently-verified 8-call-site inventory (§3, Decision 6), are unchanged by this version and are not re-derived or re-verified here — see v10's own provenance note for that history.

v11 resolves four call-chain-attribution questions raised by independent inspection of all 8 live call sites across two design-only clarification passes conducted on top of v10:

1. Confirming `CorrelationId` scope is per logical application operation, never per customer turn — exactly what v10 §3 and §4 already defined, with no wording change to either definition, plus a worked illustration.
2. Replacing the tenant-boundary language in v10 §5 with an explicit, internally-consistent two-part trust rule. v10 §5 attributed a "consistency guard" comparison to the recorder, while v10 §4 defined the recorder's only input (`AiCallContext`) as carrying no `Conversation` entity to compare against — an unreconciled contradiction between the two sections, not merely an under-specification.
3. Specifying Voyage's multi-batch usage accounting for a partially-failed `EmbedAsync` call — a case v10's existing "token fields are null" rule (Decision 2, §2) did not anticipate and now directly conflicts with.
4. Locking the `IKnowledgeRetrievalService.RetrieveAsync` signature change needed to attribute a RAG retrieval row to a `ConversationId`, which v10 left as a bare-`BusinessId`-only call with no path to that attribution at all.

Two further owner decisions were then locked in subsequent review of this same v11 pass, replacing what its first draft had left as open questions:

5. The mismatch-handling procedure for §5(b): on a detected `BusinessId`/`ConversationId` mismatch, `ClaudeReplyService` writes an independently-persisted `AuditLog` event of type `UnauthorizedActionBlocked`, then throws, before any provider call (§5, rule (c)).
6. A schema-level `IsUsagePartial` marker (§3), distinguishing a partial-usage Voyage `Failed` row from an ordinary null-tokens one at the data level, replacing the earlier proposal to leave this undistinguishable (§2).

**A verification pass confirming all six of these clarifications against live source is required before treating this as final** — in particular, re-confirming that `WidgetConversationService`'s conversation/BusinessId construction guarantees (cited in §5) still hold, that `VoyageEmbeddingClient`'s batching loop (cited in §2) is unchanged, and that `IAuditLogService`'s shared-unit-of-work behavior (cited in §5, rule (c)) has not changed.

**Scope of this document:** design only. No code has been written. `REQUIRES VERIFICATION` items in §10 that concern implementation mechanics (not owner decisions) remain open and must be confirmed before implementation begins.

---

## Changelog (v10 → v11)

| Section | v10 reference | Change |
|---|---|---|
| Header/Status/Provenance | Title and Status, lines 1–3; Provenance paragraph and item 1's fix description, lines 5–9; closing "short verification pass" sentence, line 11 | Retitles/re-states Status and Provenance for v11; replaces v10's own item-1 fix narrative and naming-clarity note with a 6-item list of what this pass resolves (the original 4, plus 2 more locked in later review); states explicitly that no owner decision remains open; replaces the closing "short verification pass" sentence with an equivalent v11 verification-pending sentence covering all six items. |
| §2 Unit of recording | Outcome/Tokens table, lines 39–44; "Null semantics" paragraph, line 52; Decision 2, line 54 | Adds a Voyage multi-batch partial-failure row to the table (now also citing `IsUsagePartial = true`); adds an explicit carve-out pointer to the "Provider error" and "Timeout or cancellation after dispatch" rows (residual-sweep fix); expands "Null semantics" to state the null-vs-zero-vs-partial-sum distinction explicitly; carves an exception into Decision 2's "token fields are null" rule for a Voyage invocation with one or more completed batches before a later failure; adds a new "Voyage multi-batch usage" subsection with a `Sequence`-ownership rule (replacing a dangling forward-reference to §10 that this v11 pass had introduced and then had to fix), a semantics table (success/null-failure/partial-failure × `IsUsagePartial`), and the requirement that the completed-batch running total remain available to the wrapper's recording path when the invocation throws (including on cancellation/timeout). The paragraph stating that a partial-usage row could only be identified by checking for non-null tokens is deleted, superseded by `IsUsagePartial`. |
| §3 Record shape | `InputTokens` row, line 74; `Outcome` row, line 78; `ErrorCategory` row, line 79; CHECK constraints, lines 84–88 | Adds a one-sentence cross-reference to §2's new partial-usage rule on `InputTokens`. Adds a new normative sentence to the `Outcome` row: `Outcome` is the application-level invocation's own result, independent of the usage fields, which record known provider-reported consumption — so a `Failed` row may carry non-null usage. Adds a new `IsUsagePartial` field row (non-null bool, default `false`) and its own CHECK constraint. Adds one sentence to `ErrorCategory` stating it records failure cause only, never usage-known-ness. |
| §4 Capture point | "Attribution" bullet, line 100; "Ownership of correlation" bullet, line 103 | Rewrites "Attribution" to state the ordering: `AiCallContext` is constructed only after the §5 trust-boundary check passes, never before and never independently re-verified afterward. Rewrites "Ownership of correlation": changes "provider-facing operation" to "application operation" (matching §3's own `CorrelationId` field definition, which this v11 pass's first draft had drifted from), adds a worked three-operation customer-turn illustration, and adds the Analyst-pair example of one operation causing two provider invocations under one shared `CorrelationId`. |
| §5 Write path | "Tenant boundary" paragraph, line 111; "Current call-site prerequisite" paragraph, line 113 | Replaces the recorder-guard sentences in the "Tenant boundary" paragraph with the locked two-part trust rule (a)/(b), and adds a new locked rule (c): the mismatch-handling procedure (audit write, then throw), with an Actor-determination note. Replaces the "Current call-site prerequisite" paragraph's own contradicting sentence with a two-sentence "Current call-site status." Adds a new "Retrieval signature" bullet (new content, no v10 line) locking the `IKnowledgeRetrievalService.RetrieveAsync` parameter addition. |
| §8 Tests | Test 5, line 154; new items after line 161 | Replaces Test 5's blanket "null tokens" assertion with three cases plus `IsUsagePartial` assertions on each path. Rewrites item 13 from "pending an owner decision" to a locked, fully-specified assertion of the §5(c) procedure (no provider call, throw, exactly one correctly-shaped `AuditLog` row, row survives the throw). Adds item 14: the `IsUsagePartial` CHECK constraint rejects three specific invalid combinations. |
| §9 Owner decisions | Decision 2 row, line 172 | Amends the "Locked outcome" cell to reference the v11 Voyage partial-batch carve-out and `IsUsagePartial`. |
| "Open questions for owner" → "Owner decisions locked in v11" | — | Section renamed and rewritten: both former open questions (mismatch-handling mechanism, partial-usage legibility) are now stated as locked rules with cross-references to §5(c) and §3/§2, not as unresolved options. |

§1, §6, §7, and §10 are unchanged from v10 and are reproduced below only for document completeness.

---

## 1. Goal and boundary

**Goal:** every application-level provider invocation Nivora dispatches through the provider wrapper produces one persisted, tenant-scoped, correctly attributed record, with no conversation content.

**What the record is:** provider invocation telemetry that carries token usage when the provider reports it. It is not guaranteed token accounting. Failed and cancelled attempts are invocation events whose usage is unknown — this is reflected in the table's name, `AiProviderInvocation` (Decision 6): the row represents an invocation attempt, and token usage is optional data attached to it, not the defining property of every row.

**In scope:** invocation records, and a single source for model identifiers.

**Out of scope:** caps, thresholds, warnings, degradation, monetary cost, pricing, billing providers, plan/status enforcement, rate limiting, and reporting UI.

**Separation of concerns:**
- A1 observes provider usage.
- A2 decides how usage is constrained.
- Step 26 decides how usage is monetized.
- Step 28 handles request-rate abuse.

---

## 2. Unit of recording

Nivora writes one record per provider invocation attempt that reaches the provider boundary, meaning the wrapper in §4 dispatches the request.

| Outcome | Record | Tokens |
|---|---|---|
| Success | Yes | Provider-reported, where exposed |
| Provider error | Yes (Decision 2 — locked: record) | Null (except the Voyage partial-batch case below) |
| Timeout or cancellation after dispatch | Yes (Decision 2 — locked: record) | Null (unknown; tokens may still be billed — except the Voyage partial-batch case below) |
| Voyage: one or more batches succeeded, a later batch failed (v11) | Yes (Decision 2 — locked: record, with the v11 carve-out below) | Non-null — the sum of only the batches that completed before the failure; `IsUsagePartial = true` (§3). Never an estimate of the failed batch. |
| Never reached the provider | No | Not applicable |

**Unit of recording is application-level, not raw-HTTP-level.** One `AiProviderInvocation` corresponds to one wrapper invocation, not necessarily one HTTP round-trip to the provider. The Anthropic SDK in use (`Anthropic` package, verified against the installed binary) applies a default of 2 retries on transient failures before the wrapper's caller ever sees a result — confirmed against the package's `ClientOptions.DefaultMaxRetries` and Anthropic's own API documentation, which states official SDKs retry transient failures twice by default, honoring `retry-after`. This means a single recorded `Failed` event may represent up to 3 real HTTP attempts to Anthropic, and a single recorded `Succeeded` event may likewise have taken more than one underlying attempt to produce. Any future consumer of this table (including A2) must treat event count as invocation count, never as a proxy for raw provider request count.

**Retries:**
- *SDK-internal retries* are invisible to application code, so one wrapper call produces one record. This is acceptable for A1's application-level invocation model but limits the granularity of failure telemetry, and its magnitude is now known (up to 3 attempts per event, per the retry default above).
- *Application-level retries* go back through the wrapper, so each attempt produces its own record. (None exist in the codebase today — confirmed by verification — so this case is currently theoretical but the design must still account for it.)

**Null semantics (invariant), amended in v11 to state the null-vs-zero distinction explicitly:** null and zero are never interchangeable and must never be conflated in either direction.
- `null` means no usage was obtained at all. For Voyage, this is the case where zero batches completed before the invocation failed.
- `0` means the provider explicitly reported zero usage for something that did complete.
- A partially-completed Voyage invocation (one or more batches succeeded before a later one failed) reports the non-null **sum** of the completed batches' usage — this is neither the `null` case above nor an ordinary success value; see the Voyage multi-batch usage rule immediately below.

Every consumer, including A2, must handle all three states — null, zero, and a non-null partial sum on a `Failed` row — explicitly and distinctly.

**Decision 2 (locked; carved out in v11 for Voyage multi-batch invocations): record failed and cancelled invocations.** A provider invocation that reaches the provider boundary and returns an error, times out, or is cancelled after dispatch produces an `AiProviderInvocation` with `Outcome = Failed` and a category from the fixed `ErrorCategory` set (§3). **Token fields are null** — except for the Voyage partial-batch case specified below, where the invocation's token fields carry the non-null sum of whatever batches completed before the failure. Raw provider error text is never stored. Invocations that never reach the provider still produce no record. Recording failures is what makes this table's framing as invocation telemetry (rather than a success-only usage ledger) actually true, and it's the only mechanism that gives visibility into provider reliability per business — a business with zero rows in a period would otherwise be indistinguishable from a business whose every call failed.

**Voyage multi-batch usage (v11, locked).** One `AiProviderInvocation` row is written per `EmbedAsync` call — a direct consequence of this section's own application-level unit-of-recording rule above, not a new principle. `Sequence` is assigned by the caller that owns the `CorrelationId`; `VoyageEmbeddingClient`'s internal batching loop assigns no `Sequence` and produces no additional rows. `EmbedAsync` may internally dispatch more than one sequential HTTP batch (`VoyageEmbeddingClient`'s own `MaxTextsPerRequest`-bounded loop). This differs from an SDK-internal retry of one identical request: each batch is a distinct, non-overlapping slice of the input, and a batch that succeeds has genuinely consumed and been billed for its own real usage.

| Case | Tokens | `IsUsagePartial` (§3) |
|---|---|---|
| Full success (every batch in one `EmbedAsync` call succeeds) | Sum across all batches | `false` |
| Failed before any usage was obtained (for Voyage: zero batches completed) | Null | `false` |
| Voyage: one or more batches completed, then a later batch failed | Non-null sum of only the completed batches | `true` |

The failed batch's own usage is never estimated, in any of these cases. A `Failed` row's non-null partial sum describes only the completed portion of an attempted call, not the attempted call as a whole — a real, structural difference from every other `Failed` row (every Anthropic call, and a Voyage call whose very first batch fails), whose token fields remain null and whose `IsUsagePartial` is `false`.

The completed-batch running total must remain available to the wrapper's recording path at the exact moment the invocation throws — including on cancellation and on timeout, not only on a batch's own explicit failure — since that is precisely when a partial-batch `Failed` row is written.

---

## 3. Record shape: `AiProviderInvocation`

The table is `ITenantOwned`. Table name: `AiProviderInvocation` (entity), `AiProviderInvocations` (table — corrected post-Slice 1: this codebase's actual convention is PascalCase table names matching the entity name pluralized, e.g. `Businesses`, `AuditLogs`, `Tickets`; no `UseSnakeCaseNamingConvention()` is registered anywhere, so the original `ai_provider_invocations` snake_case claim did not match the real convention).

**Immutability:** the application never updates or deletes an individual event after insert. Only the retention policy (§7, Decision 4) may delete expired events.

| Field | Type | Notes |
|---|---|---|
| `Id` | Guid | |
| `BusinessId` | Guid | FK Restrict |
| `Provider` | string(32) | Constants `AiProviders.Anthropic`, `AiProviders.Voyage` |
| `Model` | string(64) | Model identifier sent — `AnthropicModels.Default` or `VoyageModels.Voyage3` (corrected post-Slice 1: `VoyageModels.Embedding` never existed in source; the real, already-shipped constant is `VoyageModels.Voyage3`) (Decision 5 — locked: compile-time constants, §6) |
| `Operation` | string(64) | Business purpose only, never a class, endpoint, or method name. Values are the `AiOperations` constants (Decision 6 — locked): `CustomerReply`, `ConversationClassification`, `AnalystSelection`, `AnalystSynthesis`, `KnowledgeIndexing`, `KnowledgeRetrievalQuery`. `CustomerReply` covers both `ClaudeReplyService`'s no-tool path and `ToolDispatchService`'s tool-loop iterations; `KnowledgeIndexing` covers both the create and update indexing paths. Each pair shares one business purpose and is distinguished by `CorrelationId`/`Sequence`, not by separate `Operation` values. **Independently re-verified against all 8 live Anthropic/Voyage call sites in the codebase — confirmed complete, with every call site mapping to exactly one value and no value left unused.** |
| `CorrelationId` | Guid | One logical application operation, however many provider invocations it causes |
| `Sequence` | int | Starts at zero and increases monotonically within a `CorrelationId`. It means ordering only; phase identity belongs in `Operation` |
| `ConversationId` | Guid? | FK SetNull — this describes database behavior *if* the referenced `Conversation` row is ever deleted. The shipped lifecycle does not delete `Conversation` rows (they are anonymized in place). At customer erasure, this field is **not** explicitly nulled (Decision 3 — locked: no explicit nulling; see §7) |
| `InputTokens` | long? | Provider-reported input usage. For Anthropic: `Usage.InputTokens` directly. For Voyage: mapped from the response's `usage.total_tokens` field (confirmed present at docs-level, re-verified directly against Voyage's official embeddings API reference; the current client does not yet parse it — parsing it is implementation scope). Voyage's embeddings API reports only a single combined token count, with no separate input/output split, so `total_tokens` is the correct and only source for this field on Voyage rows. **v11:** on a partially-failed multi-batch Voyage invocation, this is the sum of `total_tokens` across only the batches that completed — see §2's Voyage multi-batch usage rule. |
| `OutputTokens` | long? | Provider-reported output usage. For Anthropic: `Usage.OutputTokens` directly. For Voyage: always null — embeddings have no output-token concept, and Voyage's response reports none |
| `CacheCreationInputTokens`, `CacheReadInputTokens` | long? | Anthropic only. Kept because the SDK in use exposes both (`Anthropic.Models.Messages.Usage.CacheCreationInputTokens` / `CacheReadInputTokens`, both `long?`). Always null for Voyage rows |
| `InputItemCount` | int? | Number of texts in an embedding request |
| `Outcome` | string(16) | `Succeeded` or `Failed`. **v11:** `Outcome` is the result of the application-level invocation; the usage fields independently record provider-reported consumption actually known for that invocation. A `Failed` row may therefore carry non-null usage (§2). |
| `IsUsagePartial` | bool | **v11, locked.** Non-null, default `false`. `true` only on a Voyage `Failed` row carrying the non-null sum of the batches that completed before a later batch failed (§2). Never `true` for any other row shape. |
| `ErrorCategory` | string(32)? | One of `RateLimited`, `ServerError`, `Timeout`, `Cancelled`, `InvalidRequest`, `Unknown`. Never raw error text. **v11:** `ErrorCategory` records only the failure's cause, never whether usage is known — that distinction is `IsUsagePartial`'s job. |
| `CreatedAt` | DateTimeOffset | Server-generated, UTC, at the time the attempt completes or fails |

**Deliberately absent:** prompt or response text, `CustomerId`, visitor or session IDs, and monetary fields.

**CHECK constraints:**
- Every token and count field is `NULL OR >= 0`.
- `Outcome IN ('Succeeded','Failed')`.
- `ErrorCategory IS NULL` if and only if `Outcome = 'Succeeded'`.
- `Sequence >= 0`.
- **v11, locked.** `IsUsagePartial = true` implies `Outcome = 'Failed'`, `Provider = AiProviders.Voyage`, and `InputTokens IS NOT NULL`.
- **(post-v11 owner decision, Slice 1 review).** `Provider IN (...)`, enumerating `AiProviders`' two values, built from constants.
- **(post-v11 owner decision, Slice 1 review).** `ErrorCategory IS NULL OR ErrorCategory IN (...)`, enumerating `AiErrorCategories`' six values, built from constants.
- **(post-v11 owner decision, Slice 1 review).** If `Provider = 'Voyage'`, then `OutputTokens`, `CacheCreationInputTokens`, and `CacheReadInputTokens` must all be `NULL`. This section's own prose already stated "For Voyage: always null" / "Always null for Voyage rows" for these fields (see the field table above), but no earlier version specified it as a CHECK.
- **Declined (post-v11 owner decision, Slice 1 review).** No CHECK on `Operation` — its six values were judged more likely to grow than `Provider`'s two, so no DB-level enumeration was added. No CHECK pairing `Provider` to `Model` (e.g. requiring Anthropic rows to use `AnthropicModels.Default`) — considered and declined in the same review. Recorded here so their absence isn't mistaken for an oversight.

**Indexes:**
- `(BusinessId, CreatedAt)`, for period aggregation.
- ~~`(BusinessId, CorrelationId)`, for tracing an operation.~~ **Superseded (post-v11 owner decision, Slice 1 review)** — replaced by the unique index below.
- **(post-v11 owner decision, Slice 1 review).** `(BusinessId, CorrelationId, Sequence)`, unique. Enforces uniqueness only — no two rows may share the same `(CorrelationId, Sequence)` pair within a business. Does **not** enforce that `Sequence` values are monotonic or contiguous within a `CorrelationId`; that remains the application-level invariant this section's own `Sequence` field definition already states, unchanged.
- **(post-v11 owner decision, Slice 1 review).** A standalone `CreatedAt` index, for the future retention job's global (not per-business) 90-day delete (§7, Decision 4) — neither index above has `CreatedAt` as a leading column usable without a fixed `BusinessId`.
- An index supporting the `ConversationId` FK action should be evaluated, because usage rows may outlive their conversation's active state (though, per Decision 3, `ConversationId` is not cleared at erasure — this index consideration is about the general SetNull FK convention in this codebase, evaluated for consistency, not because a deletion trigger is expected to fire on this field in the shipped lifecycle). Its exact shape is ENGINEERING-DETERMINED against the actual schema. (Verification found a split precedent in the current codebase — some SetNull FKs to `Conversation` rely on EF's bare auto-index, others have an explicit `BusinessId`-led composite index.) **(post-v11 owner decision, Slice 1 review):** left to EF Core's default single-column index on the FK column itself.

---

## 4. Capture point: the provider boundary

- **Wrappers:** all Anthropic calls go through a single invoker, and all Voyage calls go through a single client wrapper. Their only invoke methods require an `AiCallContext` containing `BusinessId`, `Operation` (an `AiOperations` constant), `CorrelationId`, `Sequence`, and an optional `ConversationId`.
- **Attribution (v11, replaces the v10 wording):** for callers using the wrapper, attribution context is structurally required at compile time. `AiCallContext` is constructed only after the calling boundary's own tenant-consistency check has passed (§5) — never before, and never independently re-verified afterward. The wrapper and the recorder both trust `AiCallContext` once it exists; neither re-checks it. See §5 for exactly which layer performs that check and what each downstream caller inherits.
- **Bypass detection:** an architecture test fails if any type other than the wrapper references the raw Anthropic client or calls Voyage's endpoint. Where the DI setup allows, the raw client is registered so that only the wrapper resolves it. The goal is that bypassing is structurally difficult and continuously verified. No architecture/reflection-based invariant test exists anywhere in this codebase today — this test is new testing infrastructure for the project, not an extension of an existing pattern, and should be scoped as such.
- **Recording:** the wrapper records after the call returns or throws, then returns or rethrows. Caller behavior is unchanged.
- **Ownership of correlation (v11, adds an illustration):** the service that starts the logical operation owns the `CorrelationId` and the `Sequence` counter. `CorrelationId` scope is per logical application operation, never per customer turn — retrieval, the reply (no-tool or tool loop), and classification are three separate operations within one customer turn and get three independent `CorrelationId`s. One application operation may itself cause several provider invocations sharing that one `CorrelationId` — e.g. the Analyst pair: one `CorrelationId`, `Sequence` 0 (selection) and 1 (synthesis). Illustrating the customer-turn case: one customer reply typically produces retrieval (`KnowledgeRetrievalQuery`, `CorrelationId` R, `Sequence` 0), the reply or tool loop (`CustomerReply`, `CorrelationId` C, `Sequence` 0..N−1), and classification (`ConversationClassification`, `CorrelationId` K, `Sequence` 0).

---

## 5. Write path

- **Isolation (requirement):** the usage write is persisted independently of the request's unit of work. It never flushes the request's pending changes and survives a rollback. The mechanism is left to verification. No existing pattern in this codebase does this today — the closest analogue, the audit log writer, deliberately does the opposite, sharing the caller's own unit of work rather than isolating from it. This requirement is therefore greenfield work, not an extension of anything already built.
- **Cancellation:** the write does not use the caller's `CancellationToken`. It gets its own short timeout.
- **Tenant boundary (v11, replaces the v10 recorder-guard rule):**
  (a) `BusinessId` is trusted only when it comes from authenticated or server-resolved tenant context — a JWT claim, `ICurrentTenantAccessor`, or a database row loaded using an authenticated caller's own resolved tenant. It never comes from client input or model output.
  (b) When both `BusinessId` and `ConversationId` are present for an operation, the layer that loaded the tenant-checked `Conversation` verifies `conversation.BusinessId == businessId` before it constructs any downstream provider-call context. Every downstream component that receives that context — directly or indirectly — inherits this invariant rather than re-deriving or re-checking it.

  (c) **(v11, locked.)** When rule (b)'s check fails — `conversation.BusinessId != businessId` — `ClaudeReplyService` does the following, in this order, before any provider call:

  1. Writes an `AuditLog` event: `EventType = AuditEventTypes.UnauthorizedActionBlocked`, `EntityType = AuditEntityTypes.Business`, `EntityId = businessId.ToString()`, `BusinessId = businessId`, `ConversationId = null`, `Actor = ActorType.Customer`, `OldValue = null`, `NewValue = "ConversationBusinessMismatch"` (a fixed marker string, matching this codebase's existing convention of short fixed `newValue` strings for a refusal cause, e.g. `OrderCancelApprovedActionExecutor`'s `"ActionHashMismatch"`/`"Expired"`). No field of the row — not `EntityId`, not `OldValue`, not `NewValue` — references the mismatched conversation.
  2. Persists that event independently of the request's unit of work, using the same isolation requirement already specified above for the usage write ("Isolation") — so the throw in step 3 cannot roll the audit row back. `IAuditLogService`'s existing implementation is documented as adding the row to "the CALLER's own scoped `AppDbContext` change tracker" and relying on "the caller's own subsequent `SaveChangesAsync()`" to persist it, sharing whatever unit of work the caller is in — this is exactly the shared-unit-of-work behavior the "Isolation" requirement above names as insufficient, and it cannot be reused as-is for this write.
  3. Throws.

  This describes a tenant-boundary invariant violation — something that should be structurally impossible given `WidgetConversationService`'s own construction guarantees (see "Current call-site status" below) — not an accusation that the caller acted maliciously.

  **Actor determination for rule (c):** `ActorType.Customer` is used because the widget request that invoked `GenerateReplyAsync` (via `WidgetController.PostMessage`) is the actual request origin, matching `ActorType`'s own documented convention ("records the origin of the request that caused Nivora's backend to act — never who 'did' it") and `VerificationCodeService`'s identical use of `ActorType.Customer` for a customer-triggered, security-relevant `AuditLog` write with no staff involved (`VerificationCodeService.cs:57`). `OrderCancelApprovedActionExecutor.WriteUnauthorizedExecutionBlockedAsync`'s own `UnauthorizedActionBlocked` write uses `ActorType.NivoraBusinessStaff` because that flow has an actual staff actor (`executingStaffUserId`) executing an approval — that specific value does not apply here, since no staff member is involved in a widget-triggered reply. The underlying convention — matching `ActorType` to the real request origin — is what transfers, not the literal value.

  The recorder performs no ownership comparison and no database read of its own. `AiCallContext` does not gain a `Conversation` field; it remains exactly the shape §4 defines (`BusinessId`, `Operation`, `CorrelationId`, `Sequence`, optional `ConversationId`).

  **Current call-site status (v11, replaces the v10 "Current call-site prerequisite" paragraph):** in the verified production call graph, `ClaudeReplyService.GenerateReplyAsync` is the only customer-turn layer that loads the `Conversation` and performs the check in (b) above. `ToolDispatchService`'s tool loop, `ConversationClassificationService.ClassifyAsync`, and `KnowledgeRetrievalService.RetrieveAsync` each inherit that check without re-verifying it, per (b)'s inheritance rule.

  **Retrieval signature (v11, locked):** `IKnowledgeRetrievalService.RetrieveAsync` gains a new `conversationId` parameter, sourced from `ClaudeReplyService.cs:139`'s own already-checked `conversationId`. This document specifies the signature change only; implementation (including the call-site update at `ClaudeReplyService.cs:139`) is out of scope here.

- **Latency:** each write is one awaited insert; fire-and-forget is excluded.

- **Decision 1 (locked): fail open.** `AiProviderInvocation` persistence failure must never cause an otherwise successful AI provider operation to fail. The write remains an awaited insert, per the isolation requirement above — fail open governs what happens if that awaited write throws, not whether the write is synchronous. On failure, log structured, non-content diagnostics (`BusinessId`, `Operation`, `CorrelationId`, `Sequence`, exception type) sufficient to reconcile the gap later. No prompt/response content or PII in the log. `AiProviderInvocation` is telemetry, not part of the user-facing operation's success transaction. Whether to retry the write once before logging failure is implementation scope, not part of this decision.

---

## 6. Model identifiers

**Decision 5 (locked): compile-time constants.** `AnthropicModels.Default = "claude-sonnet-5"` is a compile-time constant, extending the already-shipped `VoyageModels.Voyage3` pattern to Anthropic (which currently has none — the literal model string is hardcoded independently at five call sites across four files today). Voyage rows use the existing `VoyageModels.Voyage3` constant directly (corrected post-Slice 1: this section previously named a non-existent `VoyageModels.Embedding`; the real, already-shipped constant is `VoyageModels.Voyage3`). No call site hardcodes a model string.

`AnthropicModels` and `VoyageModels` are separate constants classes, consistent with `VoyageModels.cs` already existing as its own file in the codebase — this design does not propose merging them into one shared file.

Configuration (a runtime-settable value) was rejected: it provides no benefit for embedding-model changes, since re-embedding is required regardless of whether the model identifier came from a constant or a config value, and a dimension change requires a schema/index migration either way. For Anthropic, a config-driven model value would allow a silent runtime model swap — changing cost, latency, and tool-calling behavior — without a code review or test run to catch a regression. Requiring a code change for a model swap is a deliberate safety property, not just an engineering-purity preference.

(Per-business model selection — e.g. a cheaper model for a lower pricing tier — is out of scope for this decision. If it's ever built, it has its own data shape, most likely a field on `Business` or a plan configuration, not a reason to make either constants class configurable.)

- **Provenance:** `KnowledgeChunk.EmbeddingModel` is written from the same constant the client sends, and a test enforces this.
- **Changing the embedding model:** this always requires re-embedding. A model with a different dimension also requires a schema and index change, because the column is `vector(1024)` with HNSW built at that dimension. This fact holds regardless of the constants-vs-configuration choice — it concerns vector-index mechanics, not how the model identifier is stored.

---

## 7. Retention and GDPR

- **What the record contains:** no conversation content and no direct customer identifiers. `ConversationId` is an indirect link (usage event → conversation → customer).

- **Decision 3 (locked): `ConversationId` is not explicitly nulled at customer erasure.** `Conversation` is never deleted by either the retention job or GDPR erasure — it is anonymized in place. The existing `ConversationId` SetNull foreign-key action therefore never fires through a `Conversation` deletion, because that deletion never happens. Unlike `Message`, `Notification`, and `Approval` — each of which is handled by an explicit, individual step in the purge service because they hold or lead to content/PII that must be cleared — `AiProviderInvocation` gets no equivalent step. The rationale: after erasure, the chain `AiProviderInvocation → Conversation → Customer` no longer terminates in identifiable data, because `Customer.Email`/`Name`/`ExternalId` are nulled at the same erasure event (the `Customer` row itself survives, scrubbed, so downstream FK integrity is preserved) — the retained `ConversationId` is a link to a chain that ends in a scrubbed shell, not to identifiable data. Data minimization for this table is instead enforced entirely through its retention window (Decision 4), matching the precedent already set by `AuditLog`, `Ticket`, and `ConversationHandoff`, each of which is also left untouched at erasure. This is an architecture judgment based on the current shipped erasure design, not a legal conclusion, and should be checked against applicable GDPR guidance if and when the retained-identifier question becomes load-bearing (e.g. if a regulator inquiry or DPA review specifically raises it).

- **Decision 4 (locked): 90-day retention window.** `AiProviderInvocation` rows are retained for 90 days. This is an engineering/data-lifecycle decision based on the currently established purposes of the table — operational debugging, provider-reliability analysis, and recent-window abuse/rate-limit analysis (Step 28) — not derived from an assumed GDPR or Danish bookkeeping retention requirement, and not asserted as the shortest period that would satisfy those purposes, just a reasonable one for them. A2 (usage caps) and Step 26 (billing) are not yet specified enough to justify a longer window; this decision should be revisited, not re-litigated from first principles, once those designs state a concrete historical-data requirement. Any longer-lived financial or billing record Step 26 eventually produces gets its own retention decision — it does not extend `AiProviderInvocation`'s retention by implication. **No retention enforcement job for this table exists yet; one must be built, following the same pattern as the deferred retention enforcement work already tracked for `Conversation`/`Message` (Section 9, item 1 of the dev plan) and applying it to this new table as well.**

---

## 8. Tests

Each test must be shown to fail when its invariant is removed.

1. A tool loop of N iterations produces N records with one `CorrelationId` and `Sequence` 0 to N−1.
2. A no-tool reply produces 1 record, an Analyst query 2, and a retrieval query 1 Voyage record.
3. Tenant isolation holds on reads.
4. Background-job invocations are attributed to the iterated tenant. (No background service makes any Anthropic or Voyage call today — verified across all three hosted background services. This test currently has nothing in production to exercise and should be scoped as forward-looking, not as validating existing behavior.)
5. **(v11, revised.)** A call that fails, is cancelled, or times out before any usage is obtained (for Voyage, before any batch completes) records `Failed` with null tokens and `IsUsagePartial = false`. A Voyage call that fails, is cancelled, or times out after one or more batches completed records `Failed` with the non-null sum of the completed batches and `IsUsagePartial = true`; the failed batch's own usage is never estimated. Either way, the write succeeds after the caller's token is cancelled.
6. Error categories never contain raw provider text.
7. Architecture test: no raw provider usage exists outside the wrappers. (New test infrastructure — see §4.)
8. The entity's string properties match an explicit allowlist. (Clarified post-Slice 1: this is the entity's *property set* — which string-typed properties exist on `AiProviderInvocation` — not the *values* those properties happen to hold. A reflection-based test over the property names satisfies this; a query checking stored values against a known set does not, and Slice 1's first implementation attempt built the latter by mistake.)
9. Embedding-model provenance matches the `AnthropicModels`/`VoyageModels` constant sent.
10. A simulated usage-write failure (e.g. a forced exception in the persistence layer) does not fail the caller's AI operation, and produces the structured diagnostic log described in §5's Decision 1.
11. A retention job (once built) deletes `AiProviderInvocation` rows older than 90 days and leaves newer rows untouched.
12. A Voyage invocation record has `InputTokens` set from `usage.total_tokens` and `OutputTokens` null; an Anthropic invocation record has both fields set independently from `Usage.InputTokens`/`Usage.OutputTokens`.
13. **(v11, locked.)** A `ClaudeReplyService`-level test simulating a `conversation.BusinessId != businessId` mismatch (§5, rule (b)/(c)) asserts: (a) no Anthropic or Voyage call occurs; (b) the operation throws; (c) exactly one `AuditLog` row exists with `EventType = UnauthorizedActionBlocked`, `EntityType = Business`, `EntityId`/`BusinessId` equal to the authenticated `businessId`, `ConversationId = null`, `Actor = ActorType.Customer`, and no reference anywhere in the row (including `OldValue`/`NewValue`) to the mismatched conversation; (d) that row still exists after the throw — i.e. the throw does not roll back the independently-persisted audit write.
14. **(v11, new.)** The database `CHECK` constraint on `IsUsagePartial` (§3) rejects an insert attempting `IsUsagePartial = true` on: a `Succeeded` row; an Anthropic row; and a row with null `InputTokens`.

---

## 9. Owner decisions — all locked

All six decisions previously open in this design are now locked, following discussion among Claude, GPT, and Gemini in advisory chat. Each is folded into its relevant section above; this table is a summary index, not the authoritative text — the inline text in §2, §5, §6, §7 governs.

| # | Decision | Locked outcome | Section |
|---|---|---|---|
| 1 | Usage-write failure behavior | Fail open; structured non-content diagnostics on failure | §5 |
| 2 | Record failed/cancelled invocations | Yes, `Outcome = Failed` with `ErrorCategory`, null tokens, `IsUsagePartial = false` — **except** a Voyage invocation with one or more completed batches before a later-batch failure, whose tokens are the non-null sum of the completed batches only and `IsUsagePartial = true` (v11 carve-out) | §2, §3 |
| 3 | `ConversationId` at customer erasure | Not explicitly nulled; retention is the minimization control | §7 |
| 4 | Retention window | 90 days; revisit when A2/Step 26 state a concrete need | §7 |
| 5 | Model identifiers | Compile-time constants: `AnthropicModels.Default`, `VoyageModels.Voyage3` (corrected post-Slice 1; separate classes/files) | §6 |
| 6 | Table and `Operation` naming | `AiProviderInvocation`; `AiOperations` constants class | §3 |

---

## 10. REQUIRES VERIFICATION

**Confirmed in the most recent verification pass (v9), retained here as settled — not open for re-checking unless the codebase changes:**

- **The `AiOperations` inventory (§3, Decision 6) against live call sites.** Independently re-derived (not by trusting the design's own claims) from an unrestricted repo-wide search: 8 total Anthropic/Voyage invocation sites found, all 8 map cleanly to exactly one of the six values, no value maps to zero sites. `CustomerReply` confirmed to cover both the no-tool and tool-loop paths, with no third Anthropic call site found anywhere. `KnowledgeIndexing` confirmed to cover both create and update, with `KnowledgeIndexingService` confirmed to have exactly two public methods (no third indexing entry point). `AnalystSelection`/`AnalystSynthesis` confirmed to be two textually distinct calls, not an artificial split. `KnowledgeRetrievalQuery` confirmed to map correctly at the schema level (the row is only ever written at the wrapper-dispatch boundary, i.e. exactly at the embedding call) even though the label alone could, in isolation, read as naming a broader workflow to an unfamiliar maintainer — noted as a naming-clarity observation only, not a defect. **Should be re-confirmed only if the call-site inventory changes before implementation** (e.g. a new AI-calling feature ships in the interim).

**Other implementation-mechanics items, confirmed in prior passes, remaining open (none are owner decisions):**

- The isolated persistence mechanism (§5's isolation requirement) — no existing pattern in this codebase to reuse; the specific approach (context factory, dedicated scope, or other) is implementation scope.
- Whether to retry the usage write once before logging failure (§5, explicitly out of Decision 1's scope).
- The exact index shape for `ConversationId` (§3) — ENGINEERING-DETERMINED, a real choice with precedent on both sides in this codebase.
- The retention enforcement job for this table (§7, Decision 4) does not exist yet and must be built.
- Whether Anthropic bills for tokens on a failed, retried, or timed-out request — not established from any source checked.
- Whether Voyage bills for failed or malformed embedding requests — not established from any source checked.
- Any planned future background job that would make Anthropic or Voyage calls (relevant to Test 4's current lack of a production scenario to exercise) — not established from any source checked.

**Confirmed in prior passes, retained for reference (should be re-confirmed if further commits land before implementation):**
- No streaming used anywhere (v3 pass).
- Anthropic SDK usage fields: `InputTokens`, `OutputTokens` (`long`), `CacheCreationInputTokens`, `CacheReadInputTokens` (`long?`) (v3 pass).
- Voyage's embeddings API returns `usage.total_tokens`, and only that field — no input/output split (v3 pass, freshly re-confirmed against Voyage's official API reference during the v9 pass); the current client does not parse it.
- SDK default retry count: 2 (v3 pass).
- `Message.ID` available for free; HTTP `request-id` header requires a raw-response calling convention not currently used anywhere (v3 pass) — not added to the schema.
- Knowledge indexing batches sequentially at `VoyageModels.MaxTextsPerRequest = 1000`; largest fixture found anywhere in the repository (exhaustively searched across all 12 content strings reaching the chunker) produces 7 chunks (v7 re-verification pass).
- `Conversation` is never deleted by either the retention job or GDPR erasure, only anonymized in place; re-confirmed directly against `ConversationPurgeService.cs`, `RetentionEnforcementService.cs`, and `CustomerErasureService.cs` across two separate verification passes.

---

## Owner decisions locked in v11

1. **Mismatch-handling procedure (§5, rule (c)).** On a `conversation.BusinessId != businessId` mismatch, `ClaudeReplyService` writes an independently-persisted `AuditLog` event of type `UnauthorizedActionBlocked` (fields: `EntityType = Business`, `EntityId`/`BusinessId` = the authenticated `businessId`, `ConversationId = null`, `Actor = ActorType.Customer`, `NewValue = "ConversationBusinessMismatch"`, no reference anywhere in the row to the mismatched conversation) and then throws, before any provider call. This resolves what this pass's first draft had left as an open question — combining, rather than choosing between, the two options that draft posed: the audit write happens, and the operation still throws.

   `Actor = ActorType.Customer` because the widget request that invoked `GenerateReplyAsync` is the actual request origin, per `ActorType`'s own documented convention and `VerificationCodeService`'s identical use of `ActorType.Customer` for a customer-triggered security-relevant audit event (`VerificationCodeService.cs:57`) — no staff member is involved in this call path, so `OrderCancelApprovedActionExecutor`'s own `NivoraBusinessStaff` choice for its `UnauthorizedActionBlocked` write does not transfer literally, only the underlying "match Actor to the real request origin" convention does.

2. **`IsUsagePartial` (§2, §3).** A new non-null `bool` column on `AiProviderInvocation`, default `false`, `true` only on a Voyage `Failed` row carrying the non-null sum of completed batches — with a `CHECK` constraint enforcing that `IsUsagePartial = true` implies `Outcome = 'Failed'`, `Provider = AiProviders.Voyage`, and `InputTokens IS NOT NULL` (§3). This resolves what this pass's first draft had left as an open legibility question, by adding the schema-level marker that draft explicitly declined to propose.

---

## Errata (post-Slice 1)

Corrections and additions made during Slice 1's implementation and review, applied inline at the sections cited below; this list is an index only — the inline text governs.

1. **Table name.** §3 claimed `ai_provider_invocations` (snake_case) is "per this codebase's naming convention." It wasn't — the actual convention is PascalCase (`Businesses`, `AuditLogs`, `Tickets`, ...), and no `UseSnakeCaseNamingConvention()` is registered anywhere. Corrected to `AiProviderInvocations`. — §3
2. **`VoyageModels.Voyage3`, not `.Embedding`.** §3 and §6 referenced a `VoyageModels.Embedding` constant that never existed in source. The real, already-shipped constant is `VoyageModels.Voyage3`. Corrected in both places, and in §9's summary table. — §3, §6, §9
3. **Test 8's meaning.** §8 item 8 ("The entity's string properties match an explicit allowlist") is about the entity's *property set*, not stored *values* — clarified inline after Slice 1's first test implementation made exactly that misreading. — §8
4. **Three owner-approved CHECK constraints**, not specified by v11: `Provider IN (...)`, `ErrorCategory IS NULL OR ErrorCategory IN (...)`, and a rule requiring Voyage rows to have null `OutputTokens`/cache-token fields — all built from constants, closing a gap this document's own prose implied (via the field table's Notes) but never enforced at the database level. — §3
5. **Unique `(BusinessId, CorrelationId, Sequence)` index**, replacing the non-unique `(BusinessId, CorrelationId)` index this document originally specified. Enforces uniqueness only, never monotonic or contiguous `Sequence` ordering. — §3
6. **Standalone `CreatedAt` index**, not one of this document's own two originally-locked indexes — added for the future retention job's global (not per-business) 90-day delete. — §3
7. **Declined: no `Operation` CHECK, no `Provider`↔`Model` pairing CHECK.** Both considered and declined in the same Slice 1 review, recorded so their absence isn't mistaken for an oversight. — §3
