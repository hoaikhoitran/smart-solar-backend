# SmartSolar — business policy decisions (updated 2026-10-31, incl. M-1..M-4) — authoritative state on top, history below

## Authoritative current state (2026-10-31, final)

* **Per decision:** see the table in [approved-business-implementation-status.md](approved-business-implementation-status.md).
* **Release:** [production-readiness-report.md](production-readiness-report.md) (BLOCKED for production; code ready for review).
* **Migrations:** seven pending (two earlier, M-1..M-4 and M-7, all authorised). Applied and verified only on disposable
  PostgreSQL databases. M-5 and M-6 are not generated.
* **Tests (after M-7):** full suite with disposable PostgreSQL **1,539 total / 1,537 passed / 0 failed / 2 skipped** (Redis tests; Redis suite 9/9 separately); without infrastructure 1,539 / 1,463 / 0 / 76; the combined PostgreSQL + Redis run is NOT VERIFIED in this environment.

**Final decisions (2026-10-31):**

| ID | Decision | Implementation |
|---|---|---|
| B-01 | Closed and withdrawn requests are final; a customer who tries again creates a new request; nothing is copied. | Implemented and tested |
| B-02 | A request with a DRAFT contract is closed only after Sales discards the DRAFT with a reason. | **Implemented with M-7 `20261031043834_ContractDraftDiscard`** (approved). See [m7-contract-draft-discard-implementation.md](m7-contract-draft-discard-implementation.md). |
| B-03 | Legacy customer answers stay withheld; no automatic release or deletion. | Implemented |
| B-04 | Manufacturer-warranty entitlement needs a verified product-specific activation date; no dates from unverified durations; no automatic coverage or reimbursement. | Verified activation recording implemented; **claim handling and payer: still pending** |
| B-05 | No equivalence exemption: every panel/inverter change needs Manager and customer approval, bound to the current comparison. | Implemented |

**Still open:**
* manufacturer-claim handling and payer (D-06/B-04);
* the commercial change process after contracting (D-02);
* the measured monitoring provider and thresholds (D-08);
* **new, from M-7:** may a new contract be made for a quotation whose draft was discarded (today: refused, `CONTRACT_DRAFT_DISCARDED`)? May an amendment draft be discarded (today: refused)?

---

**Historical record below.** Earlier sections describe each phase as it was written; where they differ from the section above, the section above applies.

Audience: Product Owner and business stakeholders.

On 2026-10-31 the Product Owner approved directions D-01 to D-10. This file records each approved direction, what the
backend does now, and the **details that are still undecided**. Per-rule status, tests and proposed migrations are in
[approved-business-implementation-status.md](approved-business-implementation-status.md).

The earlier options and recommendations (2026-10-30) are superseded by the approvals below. No legal threshold,
alert threshold, provider, reimbursement policy, contract-change fee or approver role was invented.

## D-01 Manager approval of discounts and reduced prices — APPROVED

* **Rule.** Every discount, unit price below the catalog reference and zero price needs a Manager approval. There is no
  threshold. Automatic quotations use catalog prices and never discount.
* **Backend now.**
  * Sales asks for approval with a reason. A Manager approves or rejects with a reason.
  * The approval binds to the exact quotation version and pricing values (a fingerprint). Changing the pricing
    invalidates it; a new version needs its own approval.
  * Sales cannot issue without it. The Sales owner can never approve their own quotation.
  * A catalog product without a price must be priced explicitly.
  * Admin has **no** approval authority: the approved role matrix names only the Manager.
* **Still undecided.** Nothing that blocks the rule.
  * Optional: a dedicated approval table for reporting (M-6).

## D-02 Survey amendment and quotation version integrity — APPROVED

* **Rule.** A completed survey may be corrected before issue. After issue: revision, Sales review with a reason,
  revalidation against the BOM, and new customer acceptance. Signed and paid records are immutable.
* **Backend now.**
  * Outdated quotations cannot be issued, accepted or contracted.
  * `confirm-survey-version` and issue refuse a quotation whose equipment lines do not match the current BOM.
  * Once any contract exists, the survey can no longer be amended.
* **Still undecided.**
  1. The **commercial change process after contracting**: who authorises it, re-signing, payment or refund adjustments,
     fees. Until then a post-contract change is blocked.
  2. How to correct a survey when only a **DRAFT** contract exists. This is also blocked today; a decision could allow
     superseding the draft.

## D-03 Terminal states for unsuccessful sales — APPROVED (needs migration approval)

* **Rule.** Reject, withdraw, expire, not feasible and Sales closure become explicit outcomes with actor, time and
  reason. Closed requests leave the queues. No refund through a pre-payment cancellation.
* **Backend now.**
  * Expired quotations cannot be accepted.
  * A NOT_FEASIBLE survey gets no quotation.
  * Invalid transitions are refused.
* **Still undecided / needed.**
  * Migration **M-1**: new statuses and closure columns.
  * The reopening rule: a new survey request (recommended, keeps history intact) or reopening the closed one.

## D-04 Installation prerequisites — APPROVED

* **Rule.** EVN, PCCC and structural assessment are mandatory. An exemption needs an authorised condition, a reason
  and supporting evidence. A Manager note alone is never legal authorisation.
* **Backend now.**
  * All three are mandatory on every project, including unstarted legacy projects.
  * An exemption is possible only for a code listed in `Installation:ExemptiblePrerequisiteCodes` (empty), plus a
    reason and a verified document.
  * The checklist is frozen once work starts.
* **Still undecided.** The legal applicability rules per prerequisite. Until they exist, every item stays required.

## D-05 Installed equipment vs accepted quotation — APPROVED

* **Rule.** Installed panels and inverters are checked against the accepted quotation. Substitutions need a Manager
  approval with a reason, and the customer's approval where commitments change. No handover while an approval is
  outstanding.
* **Backend now.**
  * Panels and inverters must reference their catalog product.
  * Any difference blocks the completion report until a Manager approves it, and blocks the handover until the
    customer agrees.
  * Customer approval is always required, because there is no approved equivalence rule.
* **Still undecided.**
  * An **equivalence rule**, if some substitutions should not need customer consent.
  * Migration **M-3**, for a structured substitution record.
  * Whether the customer needs an explicit "decline" action besides the existing rework request.

## D-06 Independent warranty categories — APPROVED

* **Rule.** Installation, manufacturer, paid-repair and disaster warranties are separate, each with its own source.
* **Backend now.**
  * Equipment warranty comes only from the catalog product, never from technician input.
  * Coverage decisions record the category used and the item's equipment-warranty facts.
* **Still undecided.**
  * When a **manufacturer warranty starts**: handover, commissioning or invoice date.
  * **Who handles and pays** a manufacturer claim.
  * Migration **M-4**, for a MANUFACTURER_WARRANTY coverage value.

## D-07 Knowledge base access control — APPROVED

* **Rule.** Customers never reach internal documents, including through RAG retrieval, citations or snippets. Safe
  default.
* **Backend now.**
  * Customers retrieve only documents an Admin tagged `audience:customer`; all others are internal, including every
    existing document.
  * Citations are re-checked against the same audience, and stored citations are filtered in chat history.
* **Still undecided / needed.**
  * Migration **M-2**, for an audited audience column that replaces the interim tag.
  * Confirmation that staff roles may read every published document. No finer staff permissions were approved.

## D-08 Energy monitoring and alerts — APPROVED (external dependencies)

* **Rule.** SIMULATED is distinct from MEASURED. Readings are never fabricated. Alerts need explicit, approved,
  configurable rules.
* **Backend now.** The simulated read-only view is unchanged. There is no measured data, no alerts and no real-time
  claim.
* **Still undecided / needed.**
  * The measured-data **provider** (vendor API, gateway or manual readings) and its credentials.
  * The alert **thresholds**, recipients and channel.
  * Migration **M-5**.

## D-09 Customer price visibility — APPROVED

* **Rule.** The customer sees selling prices, quantities, line totals, tax, discount and final total, but not catalog
  reference prices or diagnostics.
* **Backend now.** Implemented on every customer quotation response. Staff keep the review data.
* **Still undecided.** Nothing.

## D-10 Timezone and notifications — APPROVED

* **Rule.** APIs stay in UTC. Human-readable messages use `Asia/Ho_Chi_Minh` with timezone context.
* **Backend now.** Notification texts and the user-facing visit-period error show local time with the offset, e.g.
  "10:00 20/10/2026 (GMT+07:00)". API and stored timestamps are unchanged.
* **Still undecided.** Nothing.

## Update 2026-10-31: migrations M-1 to M-4 generated (not applied)

The Product Owner authorised M-1 to M-4. They are generated and implemented; details are in
[m1-m4-migration-implementation-report.md](m1-m4-migration-implementation-report.md). **Decisions that are still open**:

| Topic | Options | Trade-offs |
|---|---|---|
| Reopening a withdrawn or closed request (D-03) | (a) The customer starts a **new** request (recommended). (b) Reopen the closed one. | (a) keeps history immutable and needs nothing new. (b) needs reopen transitions, audit and queue rules. |
| Closing a request that only has a **DRAFT** contract (D-02/D-03) | (a) Allow, superseding the draft. (b) Keep refusing; add a "discard draft contract" action. | (a) is simplest but changes contract history. (b) is explicit but adds an action. |
| Customer answers withheld because they predate the audience rule (D-07) | (a) Keep them withheld (current). (b) Admin review and release per answer. (c) Purge after a retention period. | (a) is safest. (b) needs a review endpoint and audit. (c) needs a retention policy. |
| Manufacturer-warranty **start** (D-06) | Commissioning / handover / invoice date / product-specific activation | Decides every equipment warranty end date. Until then MANUFACTURER_WARRANTY coverage stays refused. |
| Manufacturer-claim **handling and payer** (D-06) | Installer handles and pays; manufacturer reimburses; customer pays labour; … | Without it no claim can be decided or paid. |
| Equipment **equivalence rule** (D-05) | None (current: the customer always agrees) / same brand and power class / per catalog mapping | A rule removes customer friction but needs an approved mapping. |
| Measured monitoring (D-08, M-5) | Provider, thresholds, recipients | Unchanged: external dependency. |
