# Nivora — Database Schema & Business Logic Reference

Purpose of this document: explain what every table is for, how tables relate to each other, and — most importantly — the business logic and workflows that aren't visible in the schema itself but must be implemented in services/controllers. Use this alongside `NivoraDbContext.cs` and the entity files to build the service layer.

---

## 1. Core Product Principles (apply everywhere)

These aren't table-specific — they're constraints every service must respect regardless of which table it touches.

1. **Claude never determines identity or authorization. Nivora's backend always does.** Claude can *request* a tool action (get order, cancel order, refund, update customer). Nivora's backend independently verifies tenant ownership, customer identity, and input validity before any external API call happens. Claude's confidence or certainty is never sufficient authorization on its own.
2. **Verified conversation ≠ verified for a specific resource.** A conversation being Level 2 verified for Customer A does not automatically mean Customer A owns whatever order/resource is being discussed. Every write action must independently confirm the specific resource (order, customer record) belongs to the verified customer.
3. **Multi-tenancy is enforced by `BusinessId` on every table**, backed by an EF Core global query filter — but that filter only protects **reads**. Every service that creates a row referencing other tenant-owned entities must explicitly verify all referenced entities share the same `BusinessId` before inserting. This is never automatic on writes.
4. **Customers and staff users are never hard-deleted.** `Customer` is anonymized in place (PII nulled, row survives). `ApplicationUser` is deactivated (`IsActive = false`), never removed. Several CHECK constraints and FK behaviors across the schema depend on this being true — violating it will break data integrity guarantees.
5. **Idempotency is required for every external write action.** `OrderCancellation`, `CustomerUpdateAction`, `Refund` all carry an `IdempotencyKey` scoped to `(BusinessId, IdempotencyKey)`. Retries, timeouts, double-clicks, and AI tool-call retries must never cause a duplicate external action — guard execution with a Postgres advisory lock (`pg_advisory_xact_lock`) keyed on the relevant scope.

---

## 2. Table-by-Table Reference

### Business
The tenant root. Every other table hangs off `BusinessId`.

Key fields beyond the obvious:
- `RetentionDays` — how long this business's conversations/messages are retained (30–730 days). **No enforcement job exists yet — this needs to be built** (a scheduled job that finds and anonymizes/deletes expired `Conversation`/`Message` rows).
- `AgentsCanApproveActions` — governs the AI Action Safety Model's human-approval pipeline eligibility.
- `AutoRefundLimit` / `AutoRefundLimitCurrency` — the maximum amount Nivora may refund autonomously (`Auto` mode) without human approval. `null` = auto-refund disabled entirely, not "unlimited." Refunds above this trigger a `Ticket` with `PriorityReason = RefundLimitReached`.
- `CancelOrderRestocksInventory` / `StoreCreditRefundsEnabled` — policy toggles read by the cancellation/refund services; not yet wired into application code (currently hardcoded defaults).

### AuditLog
General-purpose, append-only action log — **not** a log of every AI action, only write/authorization-relevant events. Never contains raw PII in `OldValue`/`NewValue` — only metadata (field names, amounts, statuses).

**Service requirement:** all writes to this table must go through a single `IAuditLogService.LogAsync(...)` — never insert `AuditLog` rows directly from other services. This is the one enforcement point for the PII rule.

`ActorType` records the *origin of the request* (human staff, AI, business's own system, Nivora's own background job) — never the executor, since Nivora's backend always executes.

### Widget
One row per embeddable chat widget. `PublicKey` is what's embedded in the public `<script>` tag — separate from `Id` so it can be rotated if leaked. `AllowedDomains` is a soft filter (Origin header check), **not** the real security boundary — actual authorization is server-side session/token validation on every request.

### Customer
A business's own customer record. `ExternalId` is the commerce-system lookup key (e.g. Shopify customer ID). `IsAnonymized`/`AnonymizedAt` mark a GDPR-deleted customer — the row survives with PII nulled, `Id` unchanged, so every FK reference elsewhere stays valid.

`ExtendedLoggingEnabled` is the current-state flag for opt-in fraud/security monitoring (see `FlaggedCustomerActivityLog` below) — full enable/disable history lives in `CustomerExtendedLoggingHistory`, not here.

### CustomerExtendedLoggingHistory
Full audit trail of who enabled/disabled extended logging for a customer, and when. At most one "active" (open, `DisabledAt IS NULL`) row per customer, enforced by a partial unique index. **Service requirement:** toggling `Customer.ExtendedLoggingEnabled` must write the matching history row in the same transaction — the two can silently drift out of sync otherwise.

### FlaggedCustomerActivityLog
Records read-level tool activity (`getOrder`, `getInventory`, etc.) for a *specific, flagged* customer — used for fraud/security investigation. Unlike `AuditLog`, this table is allowed to reference customer-specific data, which is exactly why writes to it are gated.

**Service requirement:** a row may only be written when **both** `Business.ExtendedAuditLoggingEnabled == true` **and** `Customer.ExtendedLoggingEnabled == true` — check both, server-side, on every write, not just in the dashboard UI.

### Conversation
A chat thread — anonymous or identified. `VisitorSessionId` is not unique; the same visitor session can spawn multiple conversations over time (e.g. a resolved chat weeks ago, a new one today). `CustomerId` is null until Level 2 verification links it.

`Channel` (Website/Email/Instagram/etc.) has no default — every creation path must set it explicitly, since it drives `SlaTargets` matching. `Tags` drives `ConversationModeService`'s Auto/Assist/Manual computation via `TagModeMappings` (see section 3 below — this is one of the more involved pieces of logic to implement).

`LastMessageAt` must be updated in the same transaction as inserting a new `Message` — not a separate write.

### Message
One row per chat turn. `Role` is `Customer`/`Ai`/`Agent`. `AuthorUserId` is required when `Role == Agent`, enforced by a CHECK constraint — this only holds because `ApplicationUser` is never hard-deleted.

**Service requirement, on every message creation:**
1. Update `Conversation.LastMessageAt`.
2. If `Role == Agent`, and this conversation has an open `Ticket` with `FirstRespondedAt` still null, and this message's timestamp is after the ticket's `CreatedAt` — set `Ticket.FirstRespondedAt`. **AI-generated messages must never set this** (see Ticket section — this is deliberate, not an oversight).

### KnowledgeDocument / KnowledgeChunk
RAG source content and its embedded chunks. `KnowledgeDocument.IsActive = false` excludes it from retrieval without deleting it (since `MessageKnowledgeSource` may still reference it historically). `KnowledgeChunk.EmbeddingModel` must be checked before any similarity search — never compare embeddings generated by different models.

### MessageKnowledgeSource
Records which knowledge chunks were used to generate a given AI `Message`, with a **content snapshot** taken at generation time — so a disputed AI answer can be traced back to exactly what the knowledge base said at that moment, even if the source document has since changed or been deleted.

**Service requirement:** before inserting, verify the referenced `Message` and `KnowledgeDocument`/`KnowledgeChunk` all share the same `BusinessId` — this isn't automatic. Snapshot fields must be captured at generation time, not derived later via a live join.

### ExternalIdentity
Per-channel identity verification links (Section 4's "Level 2 verification"). A `Customer` may have multiple links across channels (Website via signed token, email via OTP, etc.).

**Critical service requirement:** `RevokedAt IS NULL` alone does **not** mean an identity is currently valid — it may be expired. Always use the rule: `RevokedAt == null AND (ExpiresAt == null OR ExpiresAt > now)`. Never check `RevokedAt` in isolation.

### Integration
One row per `(Business, Platform)` connection — a business can have Shopify, Slack, Zendesk, etc. simultaneously, but only one connection per platform. `Scopes` is a **cache**, not a live guarantee — always handle a `403` from the external API gracefully even if a local scope check passed. `Scopes` must be fully replaced (never merged) on every fresh OAuth response.

### Ticket
A trackable support case — **always tied to a `Customer`** (no anonymous tickets), **optionally tied to a `Conversation`** (a ticket can be created without one, e.g. a staff-logged phone call).

Key logic:
- `Channel` is a **snapshot** at creation time (from `Conversation.Channel` when available), used to resolve the `SlaTargets` lookup.
- `SlaDeadline` is computed **once**, at creation (`CreatedAt + SlaTargets.TargetMinutes`), and stored — never recalculated against current SLA config later.
- `FirstRespondedAt` counts **only human replies** (`Message.Role == Agent`). This is the most important non-obvious rule in the whole schema: if AI replies counted, SLA would trivially read as ~100% met regardless of actual staff responsiveness. See Message section above for the write logic.
- `Status = Reopened` is manual-only by product rule — nothing in the DB schema enforces this; the service layer must ensure a `Resolved → Reopened` transition only happens via explicit staff action, never automated routing.

### SlaTarget
Business-configured SLA rule: minutes-to-first-human-response, per `(Priority, Channel)`. `Channel` is nullable — a null-channel row is a **fallback rule** applying to any channel without a specific rule.

**Service requirement (the SLA lookup):** check for an exact `(BusinessId, Priority, Channel)` match first; only fall back to `(BusinessId, Priority, Channel = null)` if no exact match exists. Never skip the exact-match check.

### VerificationCode
OTP codes for Level 2 verification's fallback path (email/SMS code, when a signed-token handoff isn't available). **Conversation-scoped** — a code issued for one conversation can never verify a different one, even for the same customer.

A code is valid only when **all four** hold: `Used == false AND AttemptCount < MaxAttempts AND SupersededAt == null AND ExpiresAt > now`.

**Critical service requirement:** requesting a new code must supersede (mark `SupersededAt`) any existing active code for the same `(BusinessId, CustomerId, ConversationId, Channel)` scope — and this invalidate-then-create must be **atomic** (advisory lock keyed on that scope), or two concurrent "resend" requests can leave two simultaneously-valid codes.

### OrderCancellation
Idempotency-tracked record of a Shopify `orderCancel` call. `Refund`/`Restock` are both parameters of that same mutation, not separate actions — snapshotted per-row from the business's policy default at the time.

**Critical: `orderCancel` is asynchronous.** Shopify returns a job, not an immediate result — do not mark `Status = Confirmed` just because the mutation was accepted; the service must wait for job completion (poll or webhook) before confirming.

**Critical authorization requirement:** before calling Shopify, the service must verify `CustomerId` (from the verified conversation) actually owns `ExternalOrderId` — this cross-check is currently a documented, unresolved hard blocker and must be built before `cancelOrder` goes live.

### CustomerUpdateAction
Idempotency-tracked record of a Shopify `customerUpdate` call. Unlike `OrderCancellation`, this mutation is **synchronous** — status can be set immediately from the response. `FieldsChanged` records only *which* fields changed (e.g. `["email", "phone"]`), never the actual old/new values — same PII discipline as `AuditLog`.

Deliberately has **no** `ExternalCustomerId` snapshot (contrast with `OrderCancellation.ExternalOrderId`, which is kept) — this was removed specifically to avoid retaining a re-identifying link to the customer's external record past anonymization.

### TagModeMapping
Business-configured policy: what conversation mode (`Auto`/`Assist`/`Manual`) a tag implies. **This table is routing policy only — it does not encode monetary thresholds.** The refund auto-approval amount is a completely separate mechanism (`Business.AutoRefundLimit`) that combines with this table's output at evaluation time (see section 3).

`IsMinimumFloor` is a generalized hard floor — when true, no other logic can push the conversation's effective mode below what this mapping specifies, regardless of what any other computation produced.

**Service requirement:** any create/update to this table must write an `AuditLog` entry (`TagModeMappingChanged`) — this table governs AI autonomy, so changes need traceability.

### Refund / RefundItem
Idempotency-tracked record of a Shopify `refundCreate` call, supporting full/partial/line-item refunds. `refundCreate` is **synchronous**, same as `customerUpdate`.

`Refund.TotalAmount` is nullable, set **only from Shopify's response** on success — never derived by summing `RefundItem.Amount` (which records what was *requested*, not necessarily what was actually confirmed). `Method` (`OriginalPaymentMethod`/`StoreCredit`) is a Nivora-level concept — the service layer translates it into Shopify's actual API parameters, no assumed 1:1 mapping.

**Two-layer authorization for store credit:** `Business.StoreCreditRefundsEnabled` gates whether it's offered at all; only if that's true does the customer get to choose between original-payment-method and store credit. The AI relays customer intent but never decides eligibility.

### ConversationHandoff
Append-only log of AI↔human handoff events. Reuses `AuditLog`'s `ActorType` enum. `ConversationId` is nullable/`SetNull` (survives conversation retention deliberately, unlike most conversation-derived data — kept for future escalation analytics).

### StaffInvitation
A pending or expired invitation for a staff member (Admin or Agent) to join an existing business (`ITenantOwned`). Source: `docs/handoffs/staff-invite-design.md` Section 2 (DEC D4, D5, D9), table added by migration `AddStaffInvitationTable` in C2 `895ee94`.

Fields: `Id` (uuid, PK); `BusinessId` (uuid, the inviting business); `Email` (varchar(256), as entered and trimmed; becomes the user's UserName and Email at acceptance); `NormalizedKey` (varchar(256), `userManager.NormalizeName(Email)`); `Role` (varchar(16), `"Admin"` or `"Agent"`); `TokenHash` (bytea, SHA-256 over the 32 raw token bytes; the token itself is never stored); `InvitedByUserId` (uuid, the creating staff user); `CreatedAt`; `ExpiresAt` (CreatedAt or last resend + 7 days); `LastSentAt` (last create/resend send time, the resend cooldown clock). All non-null.

Constraints: FKs `BusinessId` -> Businesses and `InvitedByUserId` -> AspNetUsers, both Restrict. Unique `IX_StaffInvitations_TokenHash` and unique `IX_StaffInvitations_BusinessId_NormalizedKey` (one invitation per email per business); `IX_StaffInvitations_ExpiresAt` for the global cleanup; the default FK index on `InvitedByUserId`. CHECKs: `CK_StaffInvitations_Role` (`Role IN ('Admin','Agent')`, so no invitation can create an Owner), `CK_StaffInvitations_ExpiresAfterCreated`, `CK_StaffInvitations_LastSentAt_Range` (`LastSentAt >= CreatedAt`), `CK_StaffInvitations_TokenHash_Length` (32 bytes).

Lifecycle (DEC D9): a row exists only while pending or expired; there are no Accepted/Revoked columns. Accept deletes the row inside the acceptance transaction, revoke deletes it at once, and `StaffInvitationCleanupService` deletes rows 30 days after their current `ExpiresAt` (C6 `136c10d`). The whole feature is behind `StaffInvites:Enabled` (code default false; DEC D7).

---

## 3. Cross-Table Business Logic to Implement as Services

These are the pieces of logic that span multiple tables and need dedicated services — none of this is enforced by the schema itself.

### 3.1 Effective Conversation Mode computation (`ConversationModeService`)

The single most complex piece of derived logic in the system. Computed in this exact order — **each step can only make the mode stricter, never revert to something more permissive established by an earlier step:**

1. **Base mode** — look up all of the conversation's `Tags` against `TagModeMappings`, resolve to a base `ConversationMode`.
2. **Refund amount check** — if the interaction involves a refund request, compare the requested amount against `Business.AutoRefundLimit`/`AutoRefundLimitCurrency`. Within limit → stays eligible for `Auto`. Exceeds limit → force at least `Manual`, and trigger `Ticket` creation with `PriorityReason = RefundLimitReached`.
3. **Minimum floor enforcement** — apply any `IsMinimumFloor = true` mappings among the conversation's tags; these can only push the mode stricter.
4. **Verification/security requirements** — e.g. a failed or missing Level 2 verification forces `Manual` regardless of everything above.

### 3.2 Level 2 verification check (used by every customer-specific tool call)

Before any tool (`getOrder`, `refundOrder`, `cancelOrder`, `updateCustomer`) touches customer-specific data:
1. Confirm the conversation has a linked `CustomerId`.
2. Confirm there's a currently-valid `ExternalIdentity` for that customer (`RevokedAt == null AND (ExpiresAt == null OR ExpiresAt > now)` — never check `RevokedAt` alone) **or** `Conversation.Level2VerifiedUntil > now`.
3. **For write actions specifically**, additionally verify the actual resource (order, customer record) being acted on belongs to this specific customer — verification of identity is not verification of resource ownership. This is the documented hard-blocker cross-check gap.

### 3.3 SLA resolution and tracking (`TicketService`, on ticket creation)

1. Snapshot `Channel` from the conversation (or from staff input if conversationless).
2. Look up `SlaTargets` for `(BusinessId, Priority, Channel)` — fall back to `(BusinessId, Priority, Channel = null)` if no exact match.
3. Compute and store `SlaDeadline = CreatedAt + TargetMinutes`.
4. Never recompute this later even if `SlaTargets` config changes.

### 3.4 Idempotent external action execution (`OrderCancellationService`, `CustomerUpdateActionService`, `RefundService`)

Common pattern across all three:
1. Generate/receive an `IdempotencyKey`, scoped to the specific action attempt.
2. Acquire a Postgres advisory lock keyed on the relevant scope (e.g. order ID for cancellations).
3. Check for an existing row with that key — if found and already `Confirmed`/`Failed`, return that result rather than re-executing.
4. Insert a `Pending` row.
5. Verify tenant + customer-resource ownership (section 3.2, step 3).
6. Call the external API.
7. **For `orderCancel` (async):** do not mark `Confirmed` on mutation acceptance — track the returned job to completion before confirming.
8. **For `customerUpdate`/`refundCreate` (sync):** set `Confirmed`/`Failed` immediately from the response.
9. Write an `AuditLog` entry (`Actor = Ai`, `ConversationId` set) reflecting the outcome.
10. Release the lock (transaction commit).

### 3.5 Retention enforcement job (not yet built — high priority gap)

A scheduled background job, per business:
1. Find `Conversation` rows where `CreatedAt < now - Business.RetentionDays`.
2. Anonymize or delete per policy (Section 10.1 of the dev plan — delete or irreversibly anonymize).
3. `Message` rows (plus `Notification` and any non-`Consumed` `Approval` for the conversation) are explicitly, directly deleted by `ConversationPurgeService` — not via a fired FK cascade (a `Cascade` FK is configured toward `Conversation`, but `Conversation` rows are never deleted, so it never actually fires).
4. Handle derived data: embeddings tied to conversation content (not yet relevant — no conversation embeddings exist), `MessageKnowledgeSource` rows referencing deleted messages (a real `Cascade`, since `Message` rows genuinely are deleted), `ConversationHandoff`/`AuditLog`/`Ticket`/`OrderCancellation`/`Refund` rows referencing the conversation — they survive because `Conversation` itself is never deleted, not because a `SetNull` fired.

### 3.6 Customer anonymization (`CustomerAnonymizationService`, shipped as `CustomerErasureService`) — GDPR deletion request handling

1. Null `Customer.Email`, `Name`, `ExternalId`; set `IsAnonymized = true`, `AnonymizedAt = now`; clear `PendingErasureAt`.
2. Explicitly delete `ExternalIdentity`, `CustomerExtendedLoggingHistory`, and `VerificationCode` rows, and any non-`Consumed` `Approval`, for this customer — explicit deletes; any `Cascade` FK configured toward `Customer` never fires, since `Customer` rows are never deleted.
3. Explicitly delete `FlaggedCustomerActivityLog` rows — an explicit delete, not a fired FK cascade (a `Cascade` FK is configured toward `Customer`, but `Customer` rows are never deleted, so it never actually fires).
4. Each conversation is purged: `Message` (+ `MessageKnowledgeSource`) and `Notification` rows are hard-deleted; `Conversation` itself is kept as an anonymized shell (`VisitorSessionId` nulled, `IsAnonymized`/`AnonymizedAt` set), never deleted. `Ticket`, `OrderCancellation`, `CustomerUpdateAction`, `Refund`, `RefundItem`, `AuditLog`, `ConversationHandoff`, and any `Consumed` `Approval` are left untouched — they survive because `Customer`/`Conversation` rows are never deleted, not via FK behavior.
5. `AuditLog` needs no action — it's PII-free by design.

### 3.7 Extended logging toggle (`Customer.ExtendedLoggingEnabled`)

Toggling this must, in one transaction:
1. Update `Customer.ExtendedLoggingEnabled`.
2. Write/close a `CustomerExtendedLoggingHistory` row (open a new one on enable, close the current open one on disable).
3. Write an `AuditLog` entry.

### 3.8 Tag normalization

Tags must be normalized (lowercase/kebab-case recommended) consistently everywhere they're written — `Conversation.Tags` and `TagModeMappings.Tag` must use the same representation. Do not rely on case-insensitive database comparison; normalize at write time in whichever service assigns tags.

---

## 4. Known Open Gaps (flag these explicitly to Claude Code, don't silently build around them)

1. ~~**Retention enforcement job does not exist.** `Business.RetentionDays` is a real, validated column with no scheduled process acting on it.~~ **RESOLVED** — `RetentionEnforcementService` enforces `Business.RetentionDays`, run by `DataLifecycleBackgroundService` (`af73918`; DEC D16 of the staff invite workstream records this lag).
2. **Customer/order ownership cross-check for write tools is unresolved** — documented hard blocker, must be built before `cancelOrder`/`refundOrder`/`updateCustomer` go live.
3. **`AuditLog.LogAsync`'s PII allowlist validation** is designed conceptually but not built — currently the PII rule relies on discipline at each call site.
4. **Retention periods for `AuditLog`, `FlaggedCustomerActivityLog`, `VerificationCode` are not numerically decided** — placeholder/no enforcement yet.
5. **Whether a `Refund` can exist with zero `RefundItem` rows is undecided** — needs a product decision, then service-layer enforcement (can't be a DB constraint).
6. **`Business.CancelOrderRestocksInventory` and `AutoRefundLimit` exist in the schema but application code doesn't read from them yet** — services currently need to be pointed at these instead of hardcoded defaults.
