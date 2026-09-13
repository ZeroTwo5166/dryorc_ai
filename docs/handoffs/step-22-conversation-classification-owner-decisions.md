# Step 22 — Conversation Classification: Owner Decisions

Following the discovery pass
(`docs/handoffs/step-22-conversation-classification-discovery.md`, uncommitted).
This document records what's decided vs. what's still open. Do not begin
design work until this is committed and reviewed — this is a
decision-recording step, not a design pass. No implementation may proceed
from this document alone; it must be followed by a formal design doc, which
the owner reviews and signs off on separately before any code is written.

## Locked owner decisions

1. **Scope includes `ConversationModeService`.** Step 22 is not "assign tags"
   alone — it must also wire the existing, currently-uncalled
   `ConversationModeService` in so classification actually drives conversation
   mode. Do not ship a tagger with no consumer.
2. **Classification is AI-driven, not rule-based.** Keyword/pattern matching
   is rejected as too brittle for real customer language (e.g. distinguishing
   "still hasn't arrived" as a shipping issue vs. an implied refund request).
   The design pass owns exactly how this is implemented, subject to open
   question A below.
3. **Taxonomy — intent locked, mechanism open.** Nivora should provide a
   stable core classification vocabulary (illustrative only, not final:
   `order`, `shipping`, `refund`, `payment`, `technical`, `complaint`,
   `general`) while allowing each business to extend it with their own tags
   via the existing `TagModeMapping`-adjacent mechanism. The database/schema
   representation of "core Nivora tag" vs. "business-custom tag" is
   explicitly **OPEN** — see design question B.
4. **Tag evolution — intent locked, mechanism open.** A conversation's
   classification may change as the conversation develops (e.g. starts as
   `shipping`, becomes `shipping` + `refund`). Conversations may have one
   primary classification plus optional secondary tags. How this is
   represented in storage (current schema is a flat `Conversation.Tags
   text[]`) and how it interacts with `ConversationModeService`'s existing
   tag-driven computation is explicitly **OPEN** — see design question C.
5. **Staff correction is in scope for Step 22.** Staff must be able to
   correct an AI-assigned classification. This is locked, not conditional.
   Guardrails on scope: the exact API/UI is a design-pass question — Claude
   determines the smallest clean implementation — but it must explicitly not
   grow into a general tagging-management system, and must not expand into a
   broader dashboard redesign. Minimal correction capability only.

## Open questions — design pass must resolve, not assume

### A. Can classification piggyback on the existing AI turn, or does it need a second Claude call?

Investigate the actual current `ClaudeReplyService` implementation (not from
memory — read the real code) and answer:

- Can structured output / tool use extract classification alongside the
  customer-facing answer in one Claude call?
- If yes: does this add meaningful latency or token cost to every reply?
- If no: what's the tradeoff of a second Claude call (latency, cost) vs.
  building background-job infrastructure (which does not currently exist
  anywhere in this codebase) to classify asynchronously?
- What happens on classification failure — does the customer-facing reply
  still succeed independently?

If a clean single-call approach isn't practical, report the tradeoff back to
the owner before choosing an approach — do not silently introduce
background-job infrastructure as a side effect of this decision.

### B. How is core-vs-custom taxonomy represented in the schema?

Options to evaluate (not exhaustive, propose the best fit given existing
conventions in `NIVORA_SCHEMA_AND_LOGIC_REFERENCE.md` Section 11): a
boolean/enum column distinguishing Nivora-seeded vs. business-added tags on
the existing table; a separate core-taxonomy reference table; a naming
convention. State the recommendation and tradeoffs; do not implement until
this doc is reviewed.

### C. How do primary/secondary, evolving tags interact with `ConversationModeService`'s one-way mode ratchet?

`ConversationModeService`'s computation order (dev plan Section 6) is
explicit that each step can only make the effective mode stricter, never
revert to something more permissive established earlier. If tags can change
mid-conversation and mode gets recomputed, this existing invariant must not
be silently broken. Specifically address: does a tag change trigger full
mode recomputation, or only allow it to move stricter? Trace this against
the real current implementation, not just the doc description.

### D. Storage shape for primary + secondary tags.

Current schema is a flat `text[]`. Propose how primary vs. secondary is
represented — first-array-element convention, a separate column, a different
structure — with tradeoffs.

## Explicitly not decided here

Do not treat any "open question" answer as pre-decided. Bring findings and a
recommendation back to the owner for sign-off before writing implementation
code, per the established workflow.
