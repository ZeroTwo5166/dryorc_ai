# Step 23 — AI Business Analyst: Owner Decisions

Following the discovery pass
(`docs/handoffs/step-23-ai-business-analyst-discovery.md`). This document
records what's decided vs. what's still open. Do not begin design work until
this is committed and reviewed — this is a decision-recording step, not a
design pass. No implementation may proceed from this document alone; it must
be followed by a formal design doc, which the owner reviews and signs off on
separately before any code is written.

The five decisions below were explicitly made and locked by the product
owner. They are authoritative and must not be reinterpreted, expanded,
narrowed, "improved," corrected, or redesigned by any subsequent pass. This
document's purpose is to record them exactly and clearly so the design phase
can treat them as fixed constraints. These decisions constrain the
subsequent design pass — they do not themselves constitute an implementation
design; no tool contract, schema, API shape, prompt, or dispatch
architecture is decided here.

The discovery pass found that the existing planning documents
(`NIVORA_DEVELOPMENT_PLAN_V2.md`, `NIVORA_FULL_PLAN_CHECKLIST_V2.md`) do not
define Step 23's scope or its relationship to Step 24 at all — Decision 2
below exists specifically to close that gap. It is an explicit owner
decision, not a requirement recovered from the repository.

## Locked owner decisions

1. **Data scope.** Step 23 is an on-demand natural-language interpretation
   layer over approved, bounded business-analytics capabilities. Step 21's
   five metrics are the initial dataset. Arbitrary SQL/database access is
   prohibited. Future datasets require explicit bounded analytics
   capabilities.

1a. **Analytics capability shape.** The initial analytics capabilities are
   exposed as separate, purpose-built read-only capabilities/tools
   corresponding to distinct approved metrics or analytics domains, rather
   than one monolithic `GetAnalytics` capability, following Nivora's
   existing bounded-tool philosophy. The exact tool contracts and whether a
   single Analyst request may invoke one or multiple tools are design
   questions, not owner decisions.

2. **Step 23 / Step 24 relationship.** The existing planning docs do not
   sufficiently define this relationship — this is an explicit owner
   decision, not a recovered requirement. Step 23 is the on-demand Analyst
   surface. Step 24 is the scheduled/proactive reporting surface. They are
   distinct product surfaces; they may share underlying analytics
   capabilities in a later design, but that sharing is not itself decided
   here.

3. **Interaction model.** Step 23 v1 is single-turn. There is no
   persistent/multi-turn Analyst session in v1. Multi-turn conversational
   analysis is a future follow-up, not a v1 requirement.

4. **Question scope and boundary.**

   **In scope** — Step 23 may answer:
   - summary questions;
   - trend questions;
   - single-metric questions;
   - bounded comparisons across the approved Step 21 metrics where the
     available data supports the comparison.

   **Out of scope for v1** — Step 23 does not answer questions requiring:
   - Refund/OrderCancellation financial analysis;
   - Tags/EffectiveMode analysis;
   - customer-level analysis;
   - per-conversation analysis;
   - PII/customer profiling;
   - churn prediction;
   - revenue/profit analysis;
   - inventory/product analytics;
   - any metric requiring an unapproved capability.

   Unsupported questions receive a **hard refusal**. The response may
   describe currently supported capabilities, but must not approximate,
   infer, or otherwise answer the unsupported question via proxy metrics.

   The Analyst must not fabricate causal explanations from mere metric
   correlation. It may describe an observed relationship between approved
   metrics, but must not assert a cause that the available data does not
   establish.

   **Core invariant:** the Analyst must never infer, fabricate, estimate,
   approximate, or otherwise manufacture an answer from unavailable or
   unapproved data.

5. **Cost and operational boundaries.** A single Analyst request may invoke:
   - at most **3 approved bounded analytics capabilities**; and
   - exactly **1 Claude synthesis call**.

   The 3-capability limit is a **maximum, not a requirement** — simple
   questions should use fewer capabilities where possible. No unbounded tool
   loops or recursive Analyst calls are permitted in v1.

   Step 23 has a business-level kill switch: `AiBusinessAnalystEnabled`,
   defaulting to `false`. When disabled, the Analyst must not invoke Claude
   or its analytics capabilities.

   No per-user or per-business daily/monthly quota is required for v1.
   Usage quotas may be revisited later based on observed usage and cost.

   If an underlying analytics capability or Claude fails, the Analyst must
   not fabricate or approximate the answer. The exact failure/degradation
   UX is a design-phase concern.

## Owner decision vs. design question — explicit boundary

The owner has decided **what Step 23 is allowed to do** (the five decisions
above). The owner has **not** decided, and the following remain open for the
design phase to resolve:

- exact analytics tool contracts;
- exact request/response schemas;
- whether multiple bounded capabilities are invoked sequentially or in
  parallel;
- exact Claude prompting/orchestration;
- exact dispatch architecture;
- exact failure/degradation UX;
- exact API/controller structure;
- exact frontend implementation;
- persistence/session architecture;
- implementation details of the kill switch;
- exact tests.

**Do not invent a forced one-tool architecture.** Decision 1a intentionally
establishes separate bounded capabilities. It does not decide that an
Analyst request must invoke exactly one capability. This must not be derived
from the existing `ConversationClassificationService`'s own single-forced-
tool-call shape (noted in the discovery pass as one existing structural
analog, not as a template this decision adopts). Whether one request may
invoke multiple capabilities is explicitly left to design, subject to
Decision 5's maximum of three.

**Do not turn Step 23 into text-to-SQL.** Arbitrary SQL/database access is
explicitly prohibited (Decision 1). Claude is an interpreter/orchestrator
over approved bounded analytics capabilities, not a database query engine.

**Do not collapse Step 23 and Step 24.** Step 23 is the on-demand Analyst;
Step 24 is scheduled/proactive reporting. They may share underlying
analytics capabilities in a later design, but they are distinct product
surfaces (Decision 2).

**Do not add multi-turn behavior.** Single-turn is explicitly locked for v1
(Decision 3). Do not introduce Analyst conversation sessions, persistent
Analyst history, follow-up question state, conversational memory, or
multi-turn context.

**Do not add unsupported analytics.** Do not silently broaden the initial
dataset beyond the five Step 21 metrics (Decision 1). Do not introduce new
analytics merely because the repository contains related data.

## Explicitly not decided here

No tool contract, schema, API shape, prompt, or dispatch architecture is
decided by this document. Do not treat any item in the "Owner decision vs.
design question" list above as pre-decided, and do not derive an
architecture from these decisions beyond what is stated. Bring design-phase
findings and a recommendation back to the owner for sign-off before writing
implementation code, per the established workflow. This document does not
authorize implementation of the Analyst, its tools/capabilities, its Claude
integration, the `AiBusinessAnalystEnabled` flag, any controller/endpoint,
any UI, any migration, or any test — those are future design/implementation
work, and Step 24 remains entirely out of scope for this document.
