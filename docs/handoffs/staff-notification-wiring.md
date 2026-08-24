# Handoff — Staff Notification/Unread Signal Wiring

## 1. Task

Wire the existing (already production-ready) `NotificationService` and staff-facing notification UI into two production call sites, plus add unread-count polling to the dashboard Topbar — closing the silent-failure gap where a customer message on a Human-handled conversation got no AI reply and no staff signal either.

This was explicitly scoped as a **minimal integration task**, not a notification-system build — the backend `NotificationService`/`Notification`/`NotificationRecipient` model and the staff frontend (Topbar bell/dropdown, `/notifications` page, mark-read/mark-all-read) already existed, fully built and tested, before this task started.

## 2. Current status

**Implementation is complete. Tests and verification passed.** Nothing has been staged or committed. No independent audit has been run yet — see Section 8.

## 3. Files changed

Exactly six files, all intended:

- `backend/Services/WidgetConversationService.cs` — wires `NotifyAsync` into the customer-message path
- `backend/Controllers/ConversationsController.cs` — marks the relevant notification(s) read on a successful staff reply
- `frontend/app/components/layout/Topbar.tsx` — 30-second unread-count polling
- `backend.Tests/WidgetConversationServiceTests.cs` — 4 new tests
- `backend.Tests/ConversationsApiTests.cs` — 3 new tests
- `frontend/app/components/layout/Topbar.test.tsx` — new file, 7 tests

No new controller, DTO, entity, or migration was created.

**Note:** `NIVORA_DEVELOPMENT_PLAN_V2.md` also shows as modified in the working tree (a Steps 5–9 `🔲`→`✅` tracker fix from an earlier, unrelated documentation task this session). It is **not** part of this feature and was not touched by this implementation.

## 4. Implementation decisions

- `CustomerReplied` fires only when `Conversation.HandledBy == HandledBy.Human` at the moment the customer message is persisted. AI-handled customer messages do not notify staff — an AI-handled message already gets a synchronous AI reply, so notifying on every one would be pure noise.
- The existing `NotificationService.NotifyAsync` is reused as-is; no new notification infrastructure was built.
- Notification creation failures are isolated from customer-message persistence via the same try/catch + log-and-swallow pattern `ClaudeReplyService`'s call site already establishes — a notification failure can never fail the already-persisted customer message.
- A successful staff reply marks every still-unread `CustomerReplied` notification for that conversation as read, for **all** applicable staff recipients — not just the replying staff member — using the same `ExecuteUpdateAsync` idiom `NotificationsController.MarkRead` already uses. No new `Resolved` state or lifecycle was introduced.
- **At most one unread `CustomerReplied` notification exists per conversation.** Enforced by a plain existence check (does an unread `CustomerReplied` notification already exist for this conversation?) before creating a new one — not by a database constraint or migration.
- A new notification **can** be created again after the previous one has been marked read (i.e., after a staff reply resolves it and a later customer message arrives) — verified by a dedicated test.
- `ActorType.NivoraSystem` was used for these notifications. The `ActorType` enum has exactly four values (`NivoraBusinessStaff`, `BusinessInternalSystem`, `NivoraSystem`, `Ai`) and **none represents an anonymous widget customer** — rather than inventing a new value, the existing precedent in `NotificationDataTests.cs` (which already uses `NivoraSystem` for this same class of conversation-triggered notification) was followed.
- `Notification.IdempotencyKey` was **deliberately not used** for this deduplication. Its unique constraint is `(BusinessId, IdempotencyKey) WHERE IdempotencyKey IS NOT NULL` — permanently unique, with no time or state scoping. Keying it by conversation would mean only the conversation's *very first* `CustomerReplied` notification could ever be created; every later one — even a legitimate, properly-resolved-and-re-triggered one months later — would collide with the same key forever. That would have been a real bug, not a working dedup mechanism, so the plain existence-check approach above was used instead.

## 5. Concurrency analysis

**Correction (post-handoff audit fix), added after an independent audit found this section stale:** the analysis below describes the design as it stood when this handoff was originally written. A later concurrency-audit pass, before this landed in `ae07e60`, found the unlocked check-then-create described here was genuinely racy (not merely benign) and added locking. As shipped, the staff-reply mark-read step (`ConversationsController.CreateMessage`) and the customer-message notify-check (`WidgetConversationService.NotifyStaffOfCustomerReplyAsync`) both acquire the same `pg_advisory_xact_lock`, keyed by `AdvisoryLockKeyGenerator.ForConversationNotification(businessId, conversationId)`, and are therefore fully serialized against each other rather than racing — see that lock key's own doc comment and `WidgetConversationService`'s "Concurrency (audit-fixed)" comment for the full reasoning. The "separate, unlocked transactions" framing and the "No locking... was introduced" sentence below are stale and no longer describe the shipped code; the rest of this section's reasoning (why an unlocked race would have been tolerable) is kept for historical context only.

The race between a staff reply's mark-read operation and a near-simultaneous new customer message's notify-check (separate, unlocked transactions) was explicitly traced and is considered **benign and self-correcting** under the current architecture:

- Worst case: a brand-new notification created in the narrow window between the staff reply's `SaveChangesAsync` and its mark-read query gets swept up and marked read prematurely.
- This does **not** lose or hide the underlying customer message — it remains fully persisted and visible in the conversation regardless of notification state.
- The reverse (a genuinely unaddressed, older notification incorrectly staying unread) **cannot** happen — the mark-read update is unconditional over every currently-unread `CustomerReplied` notification for that conversation at the moment it runs.
- A subsequent customer message correctly creates a fresh unread notification, so the system self-corrects.

No locking, distributed synchronization, or transactional coordination was introduced. This matches the project's existing, already-accepted tolerance for the same class of narrow timing window (see the previously-documented `HandledBy` concurrency race from Step 9).

## 6. Verification

- Backend full suite: **234 passed, 2 skipped, 0 failed** (skips are the correctly-gated live-Claude tests, unrelated to this change).
- Frontend full suite: **43 passed**.
- TypeScript typecheck: clean.
- Lint: clean.
- Production build: clean.
- `git diff --check`: clean.
- No migration generated.
- Nothing staged or committed at any point.

**New backend tests** (`WidgetConversationServiceTests.cs` +4, `ConversationsApiTests.cs` +3) cover: AI-handled customer message creates no notification; Human-handled customer message creates one, delivered to active staff; three customer messages before a staff reply produce exactly one notification (not three); a notification correctly re-created after an earlier one was resolved (marked read) and a later message arrives; a staff reply marks the conversation's unread `CustomerReplied` notification read; it is marked read for all recipients, verified at the database level for a multi-staff scenario; unrelated notifications (different type, different conversation) are left untouched.

**New frontend tests** (`Topbar.test.tsx`, new file, 7 tests) cover: initial unread-count fetch on mount; refetch every 30 seconds (via `vi.useFakeTimers`); confirms only the unread-count endpoint is polled, never the full notification list; badge updates after a poll; a failed poll leaves the previously-displayed count in place; the polling interval genuinely clears on unmount (asserted via no further fetch calls after `unmount()` plus advancing fake timers by 60s); no polling occurs while unauthenticated.

## 7. Known unrelated issue (not fixed — out of scope)

`ConversationsController.CreateMessage`'s doc comment still states "No customer-delivery mechanism exists yet." This has been stale since the customer-side polling MVP shipped (`559f5ec`) and predates this task entirely. It was intentionally left untouched — fixing it would be unrelated documentation cleanup, explicitly out of this task's scope.

## 8. Tomorrow's next step

> Next step is an independent review/audit of the implementation and diff before considering the task complete. Review correctness, scope adherence, notification deduplication, concurrency behavior, transaction boundaries, recipient handling, frontend polling cleanup, and test quality. Do not modify code during the first audit pass unless a concrete issue is found.

## 9. Git state at handoff time

- **HEAD:** `2e27f465330d3e93605f9f433bf47069c38e32d6` — `2e27f46 Reconcile Step 9 customer-delivery checklist`
- **Working tree (`git status --short`):**
  ```
   M NIVORA_DEVELOPMENT_PLAN_V2.md
   M backend.Tests/ConversationsApiTests.cs
   M backend.Tests/WidgetConversationServiceTests.cs
   M backend/Controllers/ConversationsController.cs
   M backend/Services/WidgetConversationService.cs
   M frontend/app/components/layout/Topbar.tsx
  ?? frontend/app/components/layout/Topbar.test.tsx
  ```
- Nothing staged. No commits made since `2e27f46`.
