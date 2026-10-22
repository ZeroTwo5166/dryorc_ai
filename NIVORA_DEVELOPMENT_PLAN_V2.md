# Nivora — Production Development Plan & Data Policy (v2)

**Status:** Updated after a full Phase 1 schema restart — every table reviewed and rebuilt table-by-table, several new tables and features added, several architectural decisions locked that weren't in v1. This supersedes `NIVORA_DEVELOPMENT_PLAN.md` (v1) wherever they conflict. Sections unchanged from v1 are summarized; sections that changed are written out in full.

**Current build state (as of this revision, reconciled against `git log` up to commit `8684223`):** Steps 1-19 are complete, tested, and committed — see `NIVORA_FULL_PLAN_CHECKLIST_V2.md` for the authoritative, per-step checklist (this paragraph is a summary, not the source of truth). Highlights since the original Step 3/4 state described below: the full Next.js dashboard, embeddable widget, Claude integration, RAG (Steps 10-12), AI guardrails (13A/13B), customer profiles (14), human handoff (15), tickets (16), notifications (17), and the generic OAuth integration framework + Shopify provider (18/19) are all live. Section 14 (new, added in this reconciliation pass) documents the as-built Step 18/19 architecture, which deviates from what this document originally described. Nothing past Step 19 is built. The original text describing Step 3/4's build state and the tenant-filter bug found while building it (below) is preserved as accurate historical record of that period, not a claim about current state.

A prior implementation reportedly reaching Step 20 (per old memory notes, referring to a *separate* codebase — `Nivora old`, a 42-commit sibling directory, see `NIVORA_FULL_PLAN_CHECKLIST_V2.md`'s loose ends) is unrelated to the from-scratch rebuild this document describes, and remains unreconciled — not a claim about the current `Nivora` codebase's own Step 19 status, which is independently verified above.

**Real bug found and fixed while building Step 4's cross-tenant test:** `AppDbContext`'s global query filter originally snapshotted `ICurrentTenantAccessor.BusinessId` **once, in the constructor** (`private readonly Guid? _currentBusinessId = tenantAccessor.BusinessId;`). This is broken under real cookie authentication: ASP.NET Core Identity's `SecurityStampValidator` can resolve `UserManager` (and therefore `AppDbContext`, via `TenantAwareUserStore`) from the *same request's DI scope* **during** `UseAuthentication()` itself — before `HttpContext.User` has been set to the authenticated principal for that request — confirmed via a real, captured stack trace, and via reflecting on the installed `Microsoft.AspNetCore.Identity.dll`: `SecurityStampValidator<TUser>`'s constructor unconditionally requires `SignInManager<TUser>`, so *if* Identity resolves that validator, `AppDbContext` construction is unavoidable. Since `AppDbContext` is Scoped, that same (poisoned) instance would be reused for the rest of the request, including the controller — so a filtered *read* (`db.Customers.ToListAsync()`, etc.) would silently return zero rows, while a *write* (which doesn't go through the filter) would work fine — the exact "list is empty but create works" symptom that surfaced the bug.

**Correction, found while building a permanent regression test:** whether Identity actually triggers that early resolution on any *given* request turned out to be gated by something interval/timing-related inside `SecurityStampValidator` that a fast, tight request loop doesn't reliably cross — a dedicated attempt to force it naturally on every run of an HTTP-level test was **not** reproducible on demand, across repeated attempts, even though the exact same code path fired once, for real, under the original (slower, logging-heavy) debugging session. So "poisoned for every authenticated request" was an overclaim — it's real, and it's a genuine live hazard (one confirmed occurrence, one confirmed structural precondition), but not provably deterministic on every request. The regression test that ships (`backend.Tests/AppDbContextTenantFilterTests.cs`) does not depend on reproducing Identity's natural timing at all — it deterministically manufactures the exact interleaving (an `AppDbContext` constructed while the tenant accessor returns null, followed by the accessor's value changing within the same scope) and proves the fix — reading `.BusinessId` live inside the filter, not snapshotting it — resolves correctly regardless. Confirmed as a real regression guard, not a vacuous pass: reverting the fix makes this exact test fail.

---

## 1. Product Vision (unchanged from v1)

Multi-tenant B2B SaaS for AI-powered customer support and business intelligence, EU/Danish e-commerce businesses, Shopify-first. Widget-embedded AI chat, RAG-grounded answers, human handoff, controlled AI tool actions (order lookup, cancellation, refund, customer update), analytics, reports, automation.

---

## 2. Technology Stack (unchanged, confirmed as-built)

Next.js/React frontend · ASP.NET Core Web API + EF Core backend · Neon PostgreSQL + pgvector (confirmed: `vector 0.8.1` extension active) · Claude API (Sonnet 5, switched from Opus 5 for cost) · Voyage AI embeddings, **`voyage-3`, 1024 dimensions — now fixed in schema** (`KnowledgeChunk.Embedding` is `vector(1024)`, HNSW index built against this fixed dimension) · ASP.NET Core Identity, HttpOnly cookies · Modular monolith, no microservices/Kafka/K8s until justified.

---

## 3. Multi-Tenant Architecture — one addition

Everything from v1 stands (never Business A → Business B, cross-tenant CI test still to be written, `IgnoreQueryFilters()` requires review).

**New, confirmed as-built:** the EF Core global query filter is now live — every `ITenantOwned` entity is filtered via reflection over `OnModelCreating`, reading `BusinessId` from `ICurrentTenantAccessor`. **Fail-closed by design:** if the accessor returns `null` (no tenant resolved), the filter matches zero rows, not all rows. Currently backed by `PlaceholderCurrentTenantAccessor` (always returns `null`) until Step 4 auth wiring replaces it with a real claims-based implementation.

**Architectural decision — the Identity/tenant-filter conflict — implemented in Step 4:** `ApplicationUser` implements `ITenantOwned`, but Identity's own sign-in flow needs to look up a user by email *before* any tenant/`BusinessId` is known. Implemented as designed: `Auth/TenantAwareUserStore.cs` subclasses the default EF Core `UserStore<...>`, overriding only `FindByNameAsync` (the one method actually invoked pre-authentication — confirmed empirically via an instrumented store subclass exercising a real `SignInManager.PasswordSignInAsync` call, not assumed) to `IgnoreQueryFilters()`. No second unfiltered `DbContext` was introduced. This is narrowly scoped — every other method on the store, and the rest of the app, remains fully tenant-filtered.

---

## 4. Customer Identity & Authorization Model (unchanged from v1, reinforced)

Level 1 (anonymous, general KB questions) / Level 2 (verified, account-specific data) unchanged. Signed-token (business vouches) preferred, OTP fallback unchanged. **The overarching rule is unchanged and now has more schema teeth behind it:** verified conversation identity ≠ verified for a *specific resource*. Every write-tool action must independently confirm the specific order/customer record belongs to the verified customer — this is a documented, still-unresolved hard blocker (see Section 9).

**New schema detail:** `ExternalIdentity.RevokedAt IS NULL` alone does **not** mean an identity is valid — it may be expired. Validity is always `RevokedAt == null AND (ExpiresAt == null OR ExpiresAt > now)`, via `ExternalIdentityExtensions.IsCurrentlyValid()`. `VerificationCode` (OTP) is conversation-scoped — a code issued in one conversation can never verify a different one for the same customer.

---

## 5. Data Model — Full Current Schema

This replaces v1's Section 5 entity list entirely. 22 domain tables (plus 7 ASP.NET Identity tables), organized by area.

### Core tenancy
- **Business** — tenant root. Fields: `RetentionDays`, `Status` (enum: Active/Paused/Cancelled), `PlanTier` (string), `TimeZone` (required, no default), `ContactEmail`/`Phone`, `VatNumber`, `ExtendedAuditLoggingEnabled`, `CancelOrderRestocksInventory`, `AutoRefundLimit`/`AutoRefundLimitCurrency`, `StoreCreditRefundsEnabled`, `CreatedAt`/`UpdatedAt`. `AgentsCanApproveActions` (Safety Model gate) is **designed but not yet added** — deferred until the human-approval pipeline itself is designed.
- **Widget** — one row per embeddable widget. `PublicKey` separate from `Id` (rotatable). `AllowedDomains` is a soft filter, not the security boundary.
- **Customer** — never hard-deleted, anonymized in place (`IsAnonymized`, `AnonymizedAt`). `ExternalId` = commerce lookup key. `ExtendedLoggingEnabled` = current-state opt-in fraud-monitoring flag.
- **Conversation** — `VisitorSessionId` not unique (multiple conversations per session over time allowed). `Channel` required, no default, drives SLA matching. `Tags` (text[]) drives conversation-mode computation. `LastMessageAt` denormalized for inbox sort.
- **Message** — `Role` enum now includes `Agent` (was previously only `Customer`/`Ai` in early scaffolding — corrected this session). `AuthorUserId` required iff `Role == Agent`, DB-enforced.

### Audit / compliance (new this session)
- **AuditLog** — general-purpose, append-only. PII-forbidden in `OldValue`/`NewValue` (metadata only). All writes go through `IAuditLogService.LogAsync(...)`. Only write/authorization-relevant events logged, not every read tool call. `ActorType` enum (`NivoraBusinessStaff`/`BusinessInternalSystem`/`NivoraSystem`/`Ai`) records request origin, never executor (Nivora's backend always executes).
- **CustomerExtendedLoggingHistory** — full enable/disable history for a customer's extended-logging flag. One active row per customer max (partial unique index).
- **FlaggedCustomerActivityLog** — opt-in, two-layer-gated (`Business.ExtendedAuditLoggingEnabled` AND `Customer.ExtendedLoggingEnabled`) fraud-investigation log of read-level tool activity for a specific flagged customer. Cascade-deletes with the customer (unlike most PII-adjacent tables), since it exists only for near-term monitoring.

### Knowledge / RAG
- **KnowledgeDocument** — `IsActive` soft-delete.
- **KnowledgeChunk** — `Embedding vector(1024)` (Voyage `voyage-3`), HNSW index, `EmbeddingModel` tracked to prevent cross-model comparison.
- **MessageKnowledgeSource** *(new)* — records which chunks an AI answer used, with a **content snapshot** at generation time (survives later document edits/deletion) — enables citation UI and dispute defense.

### Identity / verification
- **ExternalIdentity** — per-channel verification links. `Channel` + `VerificationMethod` (SignedToken/Otp) are separate fields. Partial unique index on active links only.
- **VerificationCode** — OTP, conversation-scoped, `Used`/`AttemptCount`/`MaxAttempts`/`SupersededAt` state model (no `IsExhausted` bool — derived from `AttemptCount >= MaxAttempts`). Resend must atomically supersede the prior active code for the same `(BusinessId, CustomerId, ConversationId, Channel)` scope.

### Integrations / commerce
- **Integration** — one row per `(Business, Platform)`, not one per business total (v1's original schema wrongly constrained this to one integration ever — fixed). `Scopes` (text[]) is a cache, not a live guarantee; always handle live `403`s gracefully. `ExternalAccountIdentifier` (renamed from `ShopDomain`) is platform-agnostic.
- **OrderCancellation** — `orderCancel` is **asynchronous** (Shopify returns a job — don't mark `Confirmed` on mutation acceptance alone). `Restock`/`Refund` are both `orderCancel` parameters, policy-snapshotted per row. `CustomerId` required, ownership cross-check still a hard blocker.
- **CustomerUpdateAction** — `customerUpdate` is **synchronous**. `FieldsChanged` (text[]) records field names only, never values. Deliberately has no `ExternalCustomerId` snapshot (removed to avoid a redundant re-identification path past anonymization — contrast with `OrderCancellation.ExternalOrderId`, which is kept).
- **Refund / RefundItem** *(new)* — a **separate tool/action from `OrderCancellation`**: a refund can happen without cancelling an order. `refundCreate` is **synchronous**. `TotalAmount` nullable, set only from Shopify's response, never derived by summing `RefundItem.Amount` (which is the *requested* amount). `Method` (`OriginalPaymentMethod`/`StoreCredit`) is a Nivora-level concept, translated by the service layer into Shopify's actual parameters — no assumed 1:1 mapping. Two-layer authorization: `Business.StoreCreditRefundsEnabled` gates the option, then the customer chooses. `ExternalRefundId` is kept (unlike `CustomerUpdateAction`'s equivalent) — treated as an operational fact, not a redundant identity pointer.

### Support / routing
- **Ticket** — redesigned this session. **Always requires `CustomerId`** (no anonymous tickets); `ConversationId` is now **optional** (a ticket can originate from a staff-logged phone call, not just a chat). `Channel` is a snapshot at creation, not derived live. `SlaDeadline` computed once at creation, never recalculated. **`FirstRespondedAt` counts only human (`Agent`-role) replies — AI replies must never set it**, or SLA metrics become meaningless (trivially ~100% "met"). `Status` includes `Reopened` (manual-only by product rule, not DB-enforced).
- **SlaTarget** — `Priority` is now a real enum (was a loose string). `Channel` nullable = channel-agnostic fallback rule; lookup checks exact match first, falls back only if none exists. `TargetMinutes` measures time-to-first-**human**-response.
- **TagModeMapping** — `IsMinimumFloor` generalized (not locked to `Mode = Manual` specifically) so it can override any computed mode. **This table is routing policy only** — monetary refund thresholds are a separate mechanism (see below). Changes write an `AuditLog` entry (safety-relevant control).
- **ConversationHandoff** *(new)* — append-only AI↔human handoff event log. Reuses `AuditLog`'s `ActorType`. `ConversationId` is `SetNull` (survives conversation retention, deliberately, for future escalation analytics).

---

## 6. New Feature: AI Refund Autonomy Model (not in v1 at all)

This is a genuinely new piece of product logic designed this session, not present in the original plan.

**The rule:** a business can authorize Nivora to autonomously (`Auto` mode) execute refunds up to a configured limit, without human approval.

```
Business.AutoRefundLimit = null   → auto-refund disabled entirely (safe default)
Business.AutoRefundLimit = €100   → refunds ≤ €100 eligible for Auto; > €100 forces Manual
```

**Effective conversation mode computation order** (each step can only make the mode *stricter*, never relax a stricter result from an earlier step):
1. Base mode from `TagModeMappings` (tag → mode).
2. Refund-amount check against `Business.AutoRefundLimit` — over-limit forces at least `Manual`, triggers `Ticket` with `PriorityReason = RefundLimitReached`.
3. `IsMinimumFloor` tag overrides.
4. Verification/security requirement overrides (e.g. failed Level 2 verification forces `Manual`).

This computation (`ConversationModeService`) is not yet implemented — schema support exists, service logic does not.

---

## 7. New Feature: Store Credit Refunds (not in v1)

`Business.StoreCreditRefundsEnabled` (default `false`) gates whether store credit is offered as a refund method at all. If enabled, the customer chooses between `OriginalPaymentMethod` and `StoreCredit` per refund (`Refund.Method`). The AI relays customer intent; the backend validates eligibility against the business toggle — the AI never decides this unilaterally. Service layer must translate Nivora's `RefundMethod` into Shopify's actual `refundCreate` parameters (transactions/gateways/store-credit mechanics) — not a direct 1:1 mapping.

---

## 8. Confirmed Shopify API Behavior (verified via search this session — re-verify if building against a different API version)

| Mutation | Sync/Async | Notes |
|---|---|---|
| `orderCancel` | **Asynchronous** — returns a job, not an immediate result | Do not mark `Confirmed` on mutation acceptance; poll/webhook for job completion |
| `customerUpdate` | **Synchronous** — result in the same response | Status settable immediately |
| `refundCreate` | **Synchronous** — result in the same response, includes native idempotency directive support | Status settable immediately |

---

## 9. Known Hard Blockers (unresolved, carried forward)

1. **Customer/order ownership cross-check** — the backend does not yet enforce "does this order actually belong to the verified customer" independent of "is the conversation verified for someone." Documented, must be built before `cancelOrder`/`refundOrder`/`updateCustomer` go live.
2. **`AuditLog.LogAsync` PII allowlist validation** — designed conceptually (validate serialized payload keys per `EventType`), not implemented. PII rule currently relies on discipline at each call site.
3. **Retention enforcement job does not exist** — `Business.RetentionDays` is real and validated, nothing acts on it yet. See Section 10 for the full retention picture — it's now six separate clocks, not one.
4. **Whether a `Refund` may exist with zero `RefundItem` rows** — undecided; can't be DB-enforced either way, needs a product decision then service-layer validation.
5. **`RefundItem.BusinessId` vs `Refund.BusinessId` consistency** — not DB-enforced (no composite FK); relies on service-layer verification before insert, consistent with the same gap in `MessageKnowledgeSource`.
6. **The old "Steps 1-20 implemented" memory record could not be located in the current backend directory.** Treat as unverified — either investigate and find it, or formally correct the memory record. Not yet resolved as of this revision.

---

## 10. GDPR / Retention — Updated, Materially More Detailed Than v1

v1 described a single conversation-retention clock. That's now understood to be one of **at least seven** distinct retention questions across the schema, most still open.

| # | What | Status |
|---|---|---|
| 1 | `Conversation`/`Message` (`Business.RetentionDays`) | Column real, business-configurable (30–730 days) — enforced by `RetentionEnforcementService`, run by `DataLifecycleBackgroundService` (`af73918`; DEC D16 of the staff invite workstream records this lag) |
| 2 | `AuditLog` | Not numerically decided — needs legal input; lower urgency since the table is PII-free by design |
| 3 | `FlaggedCustomerActivityLog` | Not decided — likely short (30–90 days floated, not locked); holds real customer PII |
| 4 | `VerificationCode` | Not decided — likely very short (days); no long-term reason to retain |
| 5 | `OrderCancellation.ExternalOrderId` / `Refund.ExternalRefundId` | Currently kept indefinitely (treated as durable operational fact, not identity pointer) — not yet reviewed against a defined necessity window |
| 6 | Backup retention | Infrastructure-level, must be documented separately per original Section 10.4 |
| 7 | `StaffInvitations` | StaffInvitations: deleted 30 days after ExpiresAt; accept and revoke delete at once (`StaffInvitationCleanupService`, a `DataLifecycleBackgroundService` job, C6 `136c10d`; DEC D9) |

**New, deliberate architectural pattern established this session:** distinguish "durable operational fact" (kept, e.g. `ExternalOrderId`, `ExternalRefundId`) from "redundant identity-linking field" (removed, e.g. `CustomerUpdateAction.ExternalCustomerId`) on a case-by-case basis — not a blanket rule. `Customer.ExternalId` itself is nulled on anonymization; anything that's just a live-joinable duplicate of it should generally be removed rather than snapshotted, while genuinely new operational facts (an order ID, a refund ID) are kept.

**Customer anonymization is now fully specified** (was vaguer in v1): null `Email`/`Name`/`ExternalId`, set `IsAnonymized`/`AnonymizedAt`, clear `PendingErasureAt`, explicitly delete `ExternalIdentity`/`FlaggedCustomerActivityLog`/`CustomerExtendedLoggingHistory`/`VerificationCode` rows and any non-`Consumed` `Approval` (not automatic); each conversation is purged — `Message` (+ `MessageKnowledgeSource`) and `Notification` rows hard-deleted, `Conversation` itself kept as an anonymized shell (`VisitorSessionId` nulled, `IsAnonymized`/`AnonymizedAt` set), never deleted; `Ticket`/`OrderCancellation`/`CustomerUpdateAction`/`Refund`/`RefundItem`/`AuditLog`/`ConversationHandoff` and any `Consumed` `Approval` are left untouched, because `Customer`/`Conversation` rows are never deleted, so no FK delete behavior ever fires.

---

## 11. Naming & Schema Conventions (new — codify for all future tables)

- **Real C# enum** for small, closed, stable value sets (`ActorType`, `TicketStatus`, `TicketPriority`, `ConversationMode`, `RefundStatus`, `RefundMethod`, `OrderCancellationStatus`, `CustomerUpdateActionStatus`, `MessageRole`) — `HasConversion<string>()`.
- **String + `static class` constants** for open-ended, growing value sets (`Channel`, `AuditLog.EntityType`/`EventType`, `Ticket.PriorityReason`, `ConversationHandoff.Reason`) — no migration needed to add new values.
- **CHECK constraints** for genuinely small fixed sets even when kept as strings (`ExternalIdentity.VerificationMethod`), and for cross-column invariants (`Status = Confirmed ⟺ CompletedAt IS NOT NULL`, applied consistently across `OrderCancellation`/`CustomerUpdateAction`/`Refund`/`Message`/`CustomerExtendedLoggingHistory`/`ConversationHandoff`).
- **Snapshot, don't live-join**, for any field recording a historical fact where the live source could later change or be nulled (`Ticket.Channel`, `OrderCancellation.ExternalOrderId`, `MessageKnowledgeSource`'s content snapshots) — except where the snapshot would itself be a redundant re-identification risk (see Section 10).
- **Redundant index cleanup**: a composite index starting with `BusinessId` makes a standalone single-column index on the trailing field redundant, because every real query path is tenant-scoped — not because of generic B-tree prefix matching alone.
- **Tenant-consistency on insert is never automatic** — the EF global query filter protects reads only. Any service creating a row that references multiple other tenant-owned entities must explicitly verify they share the same `BusinessId`.

---

## 12. Revised Build Priority (reflects actual current state, not v1's original sequencing)

```
✅ 1. Neon PostgreSQL + pgvector (voyage-3, 1024-dim, HNSW confirmed)
✅ 2. Database schema — full table-by-table redesign complete (this document, Section 5)
✅ 3. ASP.NET Core backend + EF Core global query filters — wired, migration applied
✅ 4. Authentication + authorization — Identity cookie auth, role seeding, TenantAwareUserStore, ClaimsCurrentTenantAccessor, SameTenant/role policies, register/login/logout, cross-tenant test passing
✅ 5. Next.js dashboard
✅ 6. Embeddable chat widget
✅ 7. Conversation API + rate limiting
✅ 8. Claude integration
✅ 9. Business inbox
     → cross-tenant isolation test written (backend.Tests/CrossTenantIsolationTests.cs, passing against real Neon dev DB) — actual CI pipeline wiring still not done
     → MVP milestone reached here; build continued past this point (below), not re-verified end-to-end as a discrete gate

✅ 10-12. RAG (KnowledgeDocument/Chunk chunking+embeddings, KnowledgeRetrievalService, MessageKnowledgeSource citations)
✅ 13A/13B. AI guardrails — service layer (ConversationModeService/TicketSlaService/AuditLogService) + real-time layer (TicketHub over SignalR)
✅ 14. Customer profiles / identity linking (backend + dashboard UI)
✅ 15. Human handoff — ConversationHandoff, both directions (backend + dashboard UI)
✅ 16. Tickets — creation/lifecycle/SLA (backend + dashboard UI; no "Create Ticket" dashboard entry point yet — see checklist loose ends)
✅ 17. Notifications infrastructure (see checklist for current production-wiring status)
✅ 18. Integrations — generic OAuth framework (`IIntegrationOAuthProvider`, not the `ICommerceIntegration`/`CommerceIntegrationResolver` pattern originally sketched here — see Section 14)
✅ 19. Shopify integration (`ShopifyIntegrationOAuthProvider`, public-app expiring-token model — see Section 14 for architecture and its carried-forward HMAC residual risk)

Then, in whatever order becomes appropriate (previously flattened under one "Step 20" label — see `NIVORA_FULL_PLAN_CHECKLIST_V2.md` Phase 6 for the reorganized, not-yet-ordered, not-yet-started candidate list):
   idempotency mechanism, Level 2 verification (both halves), refundOrder tool, tool-dispatch loop + design doc, read-tool registration, customer/order ownership cross-check (Section 9 hard blocker), AI Action Safety human-approval pipeline (still unscoped), write-tool registration
   → analytics → AI Business Analyst → reports → automation
   → usage tracking + billing (schema has PlanTier as a loose string, ready for Step 26 to define real tiers)
   → full security/rate-limiting → GDPR documentation finalized (Section 10 retention questions must close)
   → scaling
```

---

## 13. What Changed From v1 — Quick Diff Summary

For anyone comparing against the original plan directly:

- **6 new tables**: `AuditLog`, `MessageKnowledgeSource`, `CustomerExtendedLoggingHistory`, `FlaggedCustomerActivityLog`, `Refund`, `RefundItem`.
- **`Business` gained 11 new columns** (status, plan tier, timezone, contact info, VAT, and four policy toggles for extended logging/restock/auto-refund/store-credit).
- **`Ticket` fundamentally redesigned**: now customer-required/conversation-optional (was conversation-required), gained `Channel` snapshot and `FirstRespondedAt` with human-only semantics.
- **`Integration`'s uniqueness constraint bug fixed**: was accidentally limited to one integration per business ever; now correctly one per `(Business, Platform)`.
- **New AI Refund Autonomy Model**: `Business.AutoRefundLimit` + `TagModeMapping.IsMinimumFloor` interaction, not present in v1 at all.
- **New Store Credit Refund feature**: not present in v1 at all.
- **Retention is now understood as six separate clocks**, not one — v1 only described conversation retention.
- **Multi-tenancy query filter is now actually implemented** (was a stated intention in v1, is now live code, fail-closed).
- **A documented IUserStore/tenant-filter architectural conflict and its resolution**, not anticipated in v1.

---

## 14. Step 18/19 — Business Integration Framework + Shopify OAuth (implemented — added in this reconciliation pass)

This section exists because this document's original Section 5 "Integrations / commerce" entry, and `NIVORA_FULL_PLAN_CHECKLIST_V2.md`'s original Phase 6 entries, described a planned architecture (`ITokenProtector`, `ICommerceIntegration`, `CommerceIntegrationResolver`, `ShopifyIntegration`) that was **not** what got built. This is a documentation reconciliation, not a redesign — the code was not changed to match the old plan; the plan is corrected to match the code.

**As-built architecture (Step 18, commit `74041df`; Step 19, commit `8684223`):**

- `IIntegrationTokenProtector` / `IntegrationTokenProtector` — encrypts `Integration.EncryptedAccessToken`/`EncryptedRefreshToken` at rest via ASP.NET Core Data Protection, one purpose-strung `IDataProtector` per platform (`"Nivora.Integration.{Platform}"`). This gives cryptographic purpose separation only — it is **not** blast-radius containment; a Data Protection key-ring compromise lets an attacker decrypt every platform's tokens across every tenant.
- `IIntegrationOAuthProvider` — a generic per-platform OAuth2 abstraction (`Platform`, `BuildAuthorizationUrl`, `ExchangeCodeAsync`, plus the Step-19-added `ValidateCallbackStructure`/`ValidateCallbackAuthenticity` for providers with their own callback-authenticity contract), resolved via `IEnumerable<IIntegrationOAuthProvider>` DI. Zero providers registered means every `{platform}` route 404s — this is how Step 18 shipped with no concrete provider, and how an actually-unregistered platform still behaves correctly today.
- `IIntegrationStateTokenService` — short-lived (10-minute) signed JWT "state" tokens binding a connect attempt to `(BusinessId, UserId, Platform)`. Cross-checked against the live authenticated staff cookie at callback time; never trusted alone for tenant identity. Has no server-side single-use tracking (a deliberate, bounded residual risk — see the class's own doc comment).
- `IntegrationsController` — the full connect/callback/disconnect/list framework. Callback ordering is locked: provider resolution → provider-specific structural validation of the raw query (before Nivora's own state-token check, so a tampered/duplicated query shape can never affect which state value gets validated) → Nivora state-token validation → live-cookie↔state binding check → provider-specific authenticity validation (Shopify's HMAC, then post-HMAC shop-syntax revalidation) → token exchange → existing upsert/audit/save/redirect.
- `ShopifyIntegrationOAuthProvider` — the first (and, as of this writing, only) concrete provider. **Project decision**: Nivora's Shopify app is treated as a public/distributed app (one app installed by many unrelated merchants), so it implements Shopify's **expiring offline-token model**: `expiring=1` on both the authorization and token-exchange requests, 1-hour access tokens, refresh token captured on every successful exchange. This corrected two Step-18 code comments (`Integration.EncryptedRefreshToken`, `IIntegrationOAuthProvider`'s `OAuthTokenResult`) that had asserted the opposite (Shopify tokens don't expire) — that assertion predated this project decision and Shopify's current public-app documentation. Step 19 captures the refresh token but does **not** implement refresh execution or scheduling — deliberately deferred.
- Shop validation is a two-stage SSRF boundary: at connect time, the caller-supplied shop must be a bare name (no scheme/path/port/whitespace/arbitrary domain) before `.myshopify.com` — hardcoded by Nivora, never caller-supplied — is appended; at callback time, the `shop` query value is untrusted until Shopify's HMAC passes, and is then re-validated against the same bare-name rule before it is used to construct the token-exchange URL.
- No dashboard UI exists for connecting any integration (Step 18 or Step 19) — `listIntegrations`/`connectIntegration`/`disconnectIntegration` exist in `frontend/app/lib/api.ts` with no caller. Tracked as an open item in the checklist, not folded into either step.

**Residual risk, deliberately not resolved, carried forward into `NIVORA_FULL_PLAN_CHECKLIST_V2.md`'s loose ends and any future Step 20 work:** `ShopifyIntegrationOAuthProvider`'s HMAC canonicalization operates on ASP.NET Core's decoded `Request.Query` values. Shopify's current official documentation confirms the exclusion/sort/join/HMAC-SHA256/hex-digest procedure but does not directly and unambiguously state whether raw percent-encoded query bytes or decoded values are what Shopify's own server-side computation uses. The decoded-query approach is an **explicit, documented project risk acceptance**, not a Shopify-documentation-confirmed fact. The corresponding special-character test (`ShopifyIntegrationOAuthProviderTests.ValidateCallbackAuthenticity_HandlesRealisticUrlEncodedSpecialCharactersInState`) is an internal regression/consistency test only — it proves Nivora's own canonicalization survives a realistic URL-encode/decode round trip without corruption, and it does **not** prove Shopify's production server signs the decoded representation. This can only be externally validated the first time a real merchant completes the Shopify OAuth flow and a genuine Shopify-signed callback is processed. Do not treat a passing test suite as having closed this.

**Also not pinned by Step 19, relevant to any future workstream that calls Shopify's real Admin API (see Section 8):** Step 19 never pins a Shopify Admin API version, because it only calls Shopify's unversioned OAuth endpoints (`/admin/oauth/authorize`, `/admin/oauth/access_token`). The first workstream that calls a versioned Admin API endpoint (e.g. a future `refundOrder` tool) must select and document a current version at that time — Section 8's table should be re-verified against current Shopify documentation before that happens, not assumed still accurate.
