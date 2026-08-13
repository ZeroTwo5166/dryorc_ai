# Nivora — Full Plan Checklist (v2)

**Reflects the `Nivora` (not `Nivora old`) codebase as of the Step 4 commit (`ef49a48`).** The original checklist described `Nivora old`'s progress (a separate, 42-commit, ~Step-20 codebase discovered mid-session, sitting untouched in a sibling folder — see the loose-ends note at the bottom). This version reflects the actual state of the from-scratch schema redesign covered in `NIVORA_DEVELOPMENT_PLAN_V2.md`.

---

## Phase 1 — Foundation
- [x] Step 1 — Neon setup (pgvector `0.8.1` confirmed active)
- [x] Step 2 — Database schema (full table-by-table redesign, 22 domain tables + 6 new ones vs. v1 — see `NIVORA_DEVELOPMENT_PLAN_V2.md` Section 5)
- [x] Step 3 — ASP.NET Core backend + EF Core global query filters (wired, `InitialCreate` migration applied — 29 tables, 89 indexes, 62 FKs, 18 CHECK constraints, HNSW index on `KnowledgeChunk.Embedding` at 1024-dim/voyage-3)
- [x] Cross-tenant isolation test — done as part of Step 4's test suite (`CrossTenantIsolationTests.cs`, `AppDbContextTenantFilterTests.cs`); **not yet wired into an actual CI pipeline** — currently run manually/locally only

## Phase 2 — Auth & Dashboard
- [x] **Step 4 — Auth & Authorization** — complete, tested, committed (`ef49a48`, `feat(auth): complete Step 4 authentication and authorization`). `AddIdentity` (not `AddIdentityCore`), Owner/Admin/Agent roles seeded, `TenantAwareUserStore` (`FindByNameAsync`-only `IgnoreQueryFilters()` exception), `ClaimsCurrentTenantAccessor`, `SameTenant` resource-based policy + role policies, register/login/logout endpoints. Real `AppDbContext` tenant-filter bug found and fixed during this step (constructor-snapshotted `BusinessId` → read live instead), with a deterministic regression test.
- [ ] Step 5 — Next.js Dashboard (basic) — **not started**. Before building, check `Nivora old` for existing dashboard/login UI worth porting (see loose ends).

## Phase 3 — Core Product (MVP)
- [ ] Step 6 — Chat Widget (`Widget` table ready — `PublicKey`, `AllowedDomains`, cryptographically-random key generation still needs implementing)
- [ ] Step 7 — Conversation API + baseline rate limiting (`Conversation`/`Message` tables ready — `LastMessageAt` transactional-update requirement, `Channel` required-with-no-default requirement both apply)
- [ ] Step 8 — Connect Claude (non-streaming)
- [ ] Step 9 — Business Inbox
- Streaming (deferred, needs own protocol spec — unchanged from v1)

## Phase 4 — AI Knowledge / RAG
- [ ] Step 10 — Knowledge Base CRUD (`KnowledgeDocument` ready — `IsActive` soft-delete)
- [ ] Step 11 — Embeddings + pgvector (`KnowledgeChunk` ready — 1024-dim, `voyage-3`, HNSW index built; `EmbeddingModel` tracking in place for future model migrations)
- [ ] Step 12 — RAG (`MessageKnowledgeSource` *new table* ready — content-snapshot citation/provenance mechanism, not in v1's plan at all)
- [ ] Step 13 — AI Guardrails: `ConversationModeService` (schema ready — `TagModeMapping` redesigned with generalized `IsMinimumFloor`, `Business.AutoRefundLimit` interaction; computation order specified in dev plan v2 Section 6), `TicketService`, `TagModeMappingsController`, SignalR `TicketHub`

## Phase 5 — Customer Support Features
- [ ] Step 14 (backend) — Customer Profiles / identity linking (`ExternalIdentity` redesigned — `Channel`+`VerificationMethod` split, `IsCurrentlyValid()` validity rule; `Customer.ExtendedLoggingEnabled` extended-logging toggle new)
- [ ] Step 14 (frontend) — Dashboard UI
- [ ] Step 15 (backend) — Human Handoff logic (`ConversationHandoff` *new table* ready — reuses `AuditLog`'s `ActorType`, survives conversation retention deliberately)
- [ ] Step 15 (frontend) — Dashboard UI
- [ ] Step 16 (backend) — Tickets (`Ticket` fundamentally redesigned: customer-required/conversation-optional, `FirstRespondedAt` human-only-reply rule, `Channel` snapshot, `SlaDeadline` computed-once; `SlaTarget` redesigned with nullable-channel fallback rule)
- [ ] Step 16 (frontend) — Dashboard UI
- [ ] Step 17 — Notifications

## Phase 6 — Business Integrations
- [ ] Step 18 (partial) — Integration framework: `ITokenProtector`/`DataProtectionTokenProtector`, `Integration` entity (redesigned — one row per `(Business, Platform)`, was buggy one-per-business-total in v1; `ExternalAccountIdentifier` renamed from `ShopDomain`, platform-agnostic; `Scopes` cache-not-guarantee discipline), encrypted token storage — **none of this built yet in `Nivora`** (existed in `Nivora old`, not ported)
- [ ] Step 18 — OAuth connection flow / dashboard UI
- [ ] Step 19 — Shopify integration (`ShopifyIntegration`, `ICommerceIntegration`, `CommerceIntegrationResolver`) — **not built in `Nivora`**
- [ ] Step 20 — Idempotency mechanism: `OrderCancellationService`/`CustomerUpdateActionService`, advisory-lock pattern (`OrderCancellation`/`CustomerUpdateAction` schemas both substantially redesigned this session — `CustomerId` added to both, `Restock`/`Refund` fields, `FieldsChanged` array, confirmed async-vs-sync Shopify behavior baked into doc comments) — **not built in `Nivora`**
- [ ] Step 20 — Level 2 verification, OTP half (`Conversation.Level2VerifiedUntil`, `Level2VerificationChecker`, `VerificationCodeService` — `VerificationCode` schema redesigned: conversation-scoped, `SupersededAt`/`MaxAttempts`, atomic resend-invalidation requirement)
- [ ] Step 20 — Level 2 verification, signed-token half (design-only, unchanged from v1)
- [ ] Step 20 — Verification wiring into `OrderCancellationService`/`CustomerUpdateActionService`
- [ ] **NEW — Step 20 — `refundOrder` tool: `RefundService`, `Refund`/`RefundItem` entities** (designed fully this session — not in v1's checklist at all. Supports full/partial/line-item refunds; `Method` = `OriginalPaymentMethod`/`StoreCredit` gated by `Business.StoreCreditRefundsEnabled`; `refundCreate` confirmed synchronous)
- [ ] Step 20 — Dispatch loop skeleton (`ClaudeService.GetReplyWithToolsAsync`) — **not built in `Nivora`**
- [ ] Step 20 — Tool dispatch design doc — **not built in `Nivora`**
- [ ] Step 20 — Register read-only tools (`getOrder`, `getCustomer`, `getProduct`, `searchProducts`, `getInventory`)
- [ ] Step 20 — Section 4 binding/cross-check logic (customer/order ownership verification) — **still a documented hard blocker**, now precisely written up in `NIVORA_DEVELOPMENT_PLAN_V2.md` Section 9, item 1
- [ ] Step 20 — Reconciliation for `Pending` `OrderCancellation` rows — **still a hard blocker**, `orderCancel`'s confirmed-async behavior makes this more precisely scoped now than in v1
- [ ] Step 20 — AI Action Safety Model human-approval pipeline — **still unscoped**; `Business.AgentsCanApproveActions` deliberately left out of the schema until this is actually designed (not a gap — a considered decision)
- [ ] Step 20 — Register write tools (`cancelOrder`, `refundOrder`, `updateCustomer`) — blocked on the above
- [x] `CustomerUpdateAction`/`OrderCancellation` reconciliation for customer updates — resolved, won't build (unchanged from v1 — **re-verify this decision still holds** against the redesigned `CustomerUpdateAction` shape before treating it as settled)

## Phase 7 — Analytics
- [ ] Step 21 — Basic Analytics
- [ ] Step 22 — Conversation Classification (also blocks Step 13's tagging/routing from activating in production)
- [ ] Step 23 — AI Business Analyst

## Phase 8 — Reports and Automation
- [ ] Step 24 — Scheduled AI Reports
- [ ] Step 25 — Automations

## Phase 9 — SaaS / Production Hardening
- [ ] Step 26 — Billing (`Business.PlanTier` exists as a loose string, ready for real tier definition)
- [ ] Step 27 — Usage Tracking
- [ ] Step 28 — Rate Limiting (full coverage; only MVP baseline planned so far)
- [ ] Step 29 — Security hardening (MFA, audit logging — `AuditLog` schema exists but `IAuditLogService`/PII-allowlist validation not built, backup/restore testing, dependency updates, **Data Protection keyring move to real secret store — still filesystem-based**)

## Phase 10 — GDPR / Privacy
- [x] Retention schema (`Business.RetentionDays`, bounded 30-730 days, CHECK-constrained) — **schema only, no enforcement**
- [ ] Data deletion/anonymization jobs — **the single highest-priority open gap across the whole plan.** Customer anonymization logic is fully specified (dev plan v2 Section 10) but not implemented; conversation/message retention enforcement job does not exist at all
- [ ] Embedding deletion lifecycle (not yet needed — no conversation embeddings exist yet, only knowledge-base embeddings)
- [ ] Data portability / export mechanism
- [ ] Anthropic DPA/retention/training-policy documentation
- [ ] Aggregated analytics vs. anonymization review
- [ ] **NEW — retention periods for `AuditLog`, `FlaggedCustomerActivityLog`, `VerificationCode` all undecided** (six total retention clocks identified this session, only one — conversation retention — has even a schema column; see dev plan v2 Section 10 table)

## Phase 11 — Scaling
Not started (deliberately deferred until justified)

---

## Smaller Known Loose Ends

- **`Nivora old` — a separate, 42-commit codebase (through roughly Step 20) discovered this session, sitting untouched at `C:\Users\subar\Desktop\Nivora old\`.** This is almost certainly the source of every "Steps 1-13/20 done" memory record from before the schema redesign. **Needs a deliberate decision**, ideally before Step 5-9 rebuild dashboard/widget/conversation-API work from scratch: assess what's salvageable (dashboard UI, Shopify integration code, tool-dispatch patterns) vs. what's fully superseded by the new schema.
- Data Protection keyring — still filesystem-based; move to Azure Key Vault (or equivalent) before multi-instance prod. Unchanged from v1, still unresolved.
- Secrets — now using `dotnet user-secrets` locally (an improvement over `.env` files), but still needs a real secret store by Step 29. **Note:** a Neon dev connection string was briefly exposed in a Claude Code transcript this session via `dotnet user-secrets list` — that specific credential should be rotated if it hasn't been already.
- `Business.CancelOrderRestocksInventory` — **now a real schema column** (was previously just a hardcoded-`false` loose end with no column at all). Remaining work is narrower now: wire `OrderCancellationService` to actually read it once that service is built.
- Email re-verification flow for returning identity-linked visitors — unchanged from v1.
- Customer/order ownership cross-check gap — unchanged in substance, more precisely documented (dev plan v2 Section 9).
- Shopify scopes — `Integration.Scopes` schema redesigned this session (cache-not-guarantee discipline, replace-not-merge rule, `ScopesUpdatedAt`) — still needs live service-layer wiring once Step 19/20 are built.
- Instagram integration (v2) — unchanged, deferred.
- WhatsApp integration (v3/v4) — unchanged, deferred.
- **NEW — Whether a `Refund` may exist with zero `RefundItem` rows is undecided** — can't be DB-enforced either way, needs a product decision then service-layer validation.
- **NEW — `RefundItem.BusinessId` vs `Refund.BusinessId` consistency is not DB-enforced** (no composite FK) — relies on service-layer verification before insert, same class of gap as `MessageKnowledgeSource`.
- **NEW — Staff invite-into-existing-business flow doesn't exist.** Step 4's registration endpoint only creates a *new* `Business` + `Owner` — there's no way yet for an Owner to invite additional staff (Admin/Agent) into their existing business. Worth scoping this before Step 5's dashboard needs an "invite team member" UI.
- **NEW — Cross-tenant isolation test currently runs locally only, not in an actual CI pipeline** — the original checklist's "Cross-tenant isolation test in CI" item is technically only half-done; the test exists and passes, but there's no automated pipeline running it on every push yet.
