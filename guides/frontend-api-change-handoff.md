# Frontend API changes — business remediation (2026-10-30, updated 2026-10-31)

**2026-10-31: the approved business directions are in §0. Read it first.** §1–§6 are the 2026-10-30 changes and still apply,
except where §0 says otherwise.

## 0. Approved directions (2026-10-31)

### 0.1 Breaking or behaviour changes

| Area | Change | FE action |
|---|---|---|
| Quotation issue (Sales) | A draft with a **discount**, a **unit price below its catalog price** or a **zero price** answers `409 QUOTATION_PRICING_APPROVAL_REQUIRED` (`details.approvalStatus`, `details.adjustments[]`) until a Manager approves exactly that pricing. | Show `pricingApproval` on the Sales screen and a "Request approval" action. |
| Quotation lines | Adding a catalog product **without a catalog price** and without `unitPrice` → `400 QUOTATION_PRICE_MISSING`. | Ask Sales for the unit price. |
| `confirm-survey-version` and issue | `409 QUOTATION_BOM_MISMATCH` (`details.mismatches[]: { productId, sku, surveyQuantity, quotedQuantity }`) when the equipment lines do not match the current survey BOM (quantity of a BOM product, or a panel/inverter not in the BOM). | Show the mismatch list; Sales corrects the lines or asks the technician to amend the survey. |
| Automatic drafts | A BOM material that could not be priced (unavailable, wrong kind, non-VND) can no longer be issued with a `reviewNote`: it is a BOM mismatch. CONDITIONS_REQUIRE_REVIEW still takes a note. | Same as above. |
| Create quotation | `409 QUOTATION_SURVEY_NOT_FEASIBLE` when the survey concluded NOT_FEASIBLE. | Hide "Create quotation" for NOT_FEASIBLE surveys. |
| Customer quotation responses (list, detail, accept, revision request) | `lines[].catalogUnitPrice` is now **always `null`** (also `pricingApproval`, `pricingBasis`, `pricingReview`, `events`). | Show the quoted `unitPrice`, `lineTotal`, `discountAmount`, `taxAmount` and `totalAmount` only. |
| On-site survey amendment (Technician) | `409 ONSITE_SURVEY_COMMERCIALLY_LOCKED` once a contract (not superseded) exists for the request. | Disable "Amend" when the request has a contract; explain that a change after contracting needs Sales. |
| Installation checklist (Manager) | EVN, PCCC and structural can no longer be made optional: `isRequired: false` → `409 INSTALLATION_PREREQUISITE_MANDATORY`. `NOT_APPLICABLE` → `409 INSTALLATION_PREREQUISITE_EXEMPTION_NOT_AUTHORIZED` (no approved exemption rule exists). Where one is configured later, `NOT_APPLICABLE` needs `note` **and** a verified `evidenceFileId` (`400 INSTALLATION_EXEMPTION_EVIDENCE_REQUIRED`). Any checklist change after the work started → `409 INSTALLATION_INVALID_STATE`. Projects created before 2026-10-30 that were never started must also satisfy all three. | Manager screen: only "Satisfied (document)"; hide the waiver for these three items. |
| Asset registration (Technician) | PANEL and INVERTER assets **must** have `productId` (`400 INSTALLATION_ASSET_PRODUCT_VALIDATION_FAILED`, `assets[i].productId`). `warrantyMonths` must be omitted: it comes from the catalog. A different value → the same 400 with `assets[i].warrantyMonths`. Assets without a product carry no warranty. | Pick panels/inverters from the contracted products; remove the warranty input. |
| Completion report (Technician) | `409 INSTALLATION_SUBSTITUTION_APPROVAL_REQUIRED` (`details.differences[]`) when installed panels/inverters differ from the accepted quotation and no Manager approved it. | Show "Waiting for Manager approval of the equipment change". |
| Customer acceptance | `accept: true` → `409 INSTALLATION_SUBSTITUTION_CUSTOMER_APPROVAL_REQUIRED` until the customer agreed to the approved substitution. | Show the differences and an "I agree to the equipment change" action before "Accept". |
| Chat (Customer) | **Superseded by §7.2 (M-2): the `audience` field replaced the tag.** Customers only get answers and citations from documents classified as customer-visible. Every other document, **including every document published before 2026-10-31**, is internal. Stored citations of a document that is no longer customer-visible disappear from the session history. Staff are unchanged. | Admin screen: let the Admin add/remove the `audience:customer` tag and say clearly that untagged documents are internal. |
| Notification texts | Times in after-sales notification messages are now local, with the offset, e.g. "10:00 20/10/2026 (GMT+07:00)". API timestamps stay UTC ISO 8601. | No action (do not parse times out of message text). |
| Probes | New `GET /health/live` (200 `{status:"live"}`) and `GET /health/ready` (200 `{status:"ready"}` or `503 SCHEMA_NOT_READY`). | Not for the FE; for deployment. |

### 0.2 New endpoints

| Role | Method | Path | Body | Response / errors |
|---|---|---|---|---|
| Sales (owner) | POST | `/api/quotations/{id}/pricing-approval-request` | `{ revision, reason }` | `200 QuotationView`.<br>`400 VALIDATION_FAILED` (reason missing or > 1000).<br>`409 QUOTATION_PRICING_APPROVAL_NOT_REQUIRED`, `QUOTATION_PRICING_APPROVAL_PENDING`, `INVALID_STATE` (already approved), `QUOTATION_NOT_DRAFT`, `QUOTATION_STALE_REVISION`.<br>`404 QUOTATION_NOT_FOUND`. |
| Manager **only** (Admin 403) | GET | `/api/manager/quotations/pricing-approvals` | — | `200 QuotationView[]` whose `pricingApproval.status = PENDING` |
| Manager **only** (Admin 403) | POST | `/api/manager/quotations/{id}/pricing-approval` | `{ revision, approve: true\|false, reason }` | `200 QuotationView`.<br>`400 VALIDATION_FAILED` (approve missing, reason missing).<br>`409 QUOTATION_PRICING_APPROVAL_NOT_PENDING`, `QUOTATION_SELF_APPROVAL_NOT_ALLOWED`, `QUOTATION_STALE_REVISION`, `QUOTATION_NOT_DRAFT`.<br>`404`. |
| Manager **only** (Admin 403) | POST | `/api/installation-projects/{id}/equipment-substitution/approve` | `{ expectedRevision, reason }` | `200 InstallationStaffView`.<br>`400 VALIDATION_FAILED`, `INSTALLATION_REASON_REQUIRED`.<br>`409 INSTALLATION_NO_SUBSTITUTION`, `INSTALLATION_INVALID_STATE`, `INSTALLATION_STALE_REVISION`.<br>`404`. |
| Customer (owner) | POST | `/api/customers/me/installation-projects/{id}/equipment-substitution/approve` | — | `200 InstallationCustomerView` (a repeat is a no-op).<br>`409 INSTALLATION_NO_SUBSTITUTION`, `INSTALLATION_INVALID_STATE`.<br>`404`. |

### 0.3 New response fields (additive)

* **`QuotationView.pricingApproval`** (staff only, `null` for customers):
  `{ status, adjustments[], fingerprint, request, decision }`.
  * `status`: `NOT_REQUIRED | REQUIRED | PENDING | APPROVED | REJECTED`.
  * `adjustments[]` items are `{ kind, lineNumber, productId, catalogUnitPrice, unitPrice, amount }`, where `kind` is
    `DISCOUNT | REDUCED_UNIT_PRICE | ZERO_PRICE`.
  * `request` and `decision` are each `{ eventType, actorUserId, reason, at }`.
  * A pricing edit returns the status to `REQUIRED`.
* **`equipment`** on the installation staff, technician and customer views:
  `{ status, differences[], fingerprint, managerApproval, customerApproval }`.
  * `status`: `MATCHES | NOT_COMPARABLE | SUBSTITUTION_APPROVAL_REQUIRED | CUSTOMER_APPROVAL_REQUIRED | SUBSTITUTION_APPROVED | NOT_CHECKED_AT_HANDOVER`.
  * `differences[]` items are `{ productId, productType, name, contractedQuantity, installedQuantity }`.
  * Approvals are `{ actorUserId, reason, at }`. `actorUserId` is `null` in the customer view.
* **New quotation event types:** `PRICING_APPROVAL_REQUESTED`, `PRICING_APPROVED`, `PRICING_REJECTED`.
  `detail = "fp:{fingerprint}:{n}; reason"`.
* **New installation event types:** `EQUIPMENT_SUBSTITUTION_APPROVED`, `EQUIPMENT_SUBSTITUTION_CUSTOMER_APPROVED`.

### 0.4 New error codes

| Code | Status |
|---|---|
| `QUOTATION_PRICING_APPROVAL_REQUIRED` | 409 |
| `QUOTATION_PRICING_APPROVAL_PENDING` | 409 |
| `QUOTATION_PRICING_APPROVAL_NOT_REQUIRED` | 409 |
| `QUOTATION_PRICING_APPROVAL_NOT_PENDING` | 409 |
| `QUOTATION_SELF_APPROVAL_NOT_ALLOWED` | 409 |
| `QUOTATION_PRICE_MISSING` | 400 |
| `QUOTATION_BOM_MISMATCH` | 409 |
| `QUOTATION_SURVEY_NOT_FEASIBLE` | 409 |
| `ONSITE_SURVEY_COMMERCIALLY_LOCKED` | 409 |
| `INSTALLATION_PREREQUISITE_MANDATORY` | 409 |
| `INSTALLATION_PREREQUISITE_EXEMPTION_NOT_AUTHORIZED` | 409 |
| `INSTALLATION_EXEMPTION_EVIDENCE_REQUIRED` | 400 |
| `INSTALLATION_SUBSTITUTION_APPROVAL_REQUIRED` | 409 |
| `INSTALLATION_SUBSTITUTION_CUSTOMER_APPROVAL_REQUIRED` | 409 |
| `INSTALLATION_NO_SUBSTITUTION` | 409 |

### 0.5 Superseded statements in §1–§2

* §1 "Installation checklist": a non-empty `note` is **no longer enough** to waive EVN, PCCC or structural (see §0.1).
* §1 "Customer quotation views": `catalogUnitPrice` is now `null` as well.
* §1 "Asset registration": an omitted `warrantyMonths` is still filled from the catalog. A typed value is now refused when
  it differs from the catalog.
* §2 "issue": `PRODUCT_UNAVAILABLE` / `WRONG_ITEM_KIND` / `CURRENCY_NOT_SUPPORTED` with a `reviewNote` now meet
  `QUOTATION_BOM_MISMATCH` (§0.1).

Audience: frontend developers. Branch `feat/fe-backend-gap-fixes`. These changes come on top of
[automated-quotation-business-handoff.md](automated-quotation-business-handoff.md) and
[fe-backend-gap-fixes-handoff.md](fe-backend-gap-fixes-handoff.md). Nothing here assumes the FE already implemented them.
The envelope is unchanged.

## 1. Breaking or behaviour changes (read first)

| Area | Change | FE action |
|---|---|---|
| Every endpoint | When the database lacks a migration of the deployed build, **every request** answers `503 SCHEMA_NOT_READY` with `details.pendingMigrations[]` and a `Retry-After` header (30 s). | Show a "service is being updated" state; retry later. |
| Customer quotation views (`GET /api/customers/me/quotations`, `/{id}`, accept, revision-request responses) | `pricingBasis`, `pricingReview` and `events` are **always `null`** for the customer (`pricingBasis` was added on this branch and is now staff-only). | Do not read internal pricing diagnostics on customer screens. |
| Quotation acceptance | `canAccept` is false and `POST …/accept` answers `409 QUOTATION_SURVEY_CHANGED` when the on-site survey was amended after the quotation was made. | Show "This quotation is being updated by Sales" when `surveyChanged = true`. |
| Contract issue | `POST /api/contracts/{id}/issue` answers **`400 CONTRACT_TERMS_REQUIRED`** when the draft has no terms. Automatic quotations carry no terms by default, so contracts created from them start without terms. | Require the terms field when creating or editing a contract draft. |
| Contract creation | `409 CONTRACT_SURVEY_CHANGED` (`details.quotationSurveyVersion`, `details.currentSurveyVersion`) when the survey was amended after the accepted quotation. | Tell Sales to revise the quotation. |
| Installation checklist | New projects start with **EVN, PCCC and structural all `isRequired: true`**. Waiving one (`status: NOT_APPLICABLE`, or `isRequired: false`) needs a non-empty `note`, else `400 INSTALLATION_CHECKLIST_WAIVER_REASON_REQUIRED`. Projects created earlier are unchanged. | Manager screen: require a reason when waiving; the technician still sees `canStart` / `startBlockedReason = INSTALLATION_CHECKLIST_INCOMPLETE`. |
| Installation views `currentRound` | While a report waits for the customer (`PENDING_CUSTOMER_ACCEPTANCE`) and after `COMPLETED`, `currentRound` is the round being judged / the last round (previously it was one higher). | Display as is; the evidence `round` values are unchanged. |
| Asset registration | `400 INSTALLATION_ASSET_PRODUCT_VALIDATION_FAILED` (`details` keyed `assets[i].productId`) when `productId` is unknown/deleted or of another kind (PANEL must be a SOLAR_PANEL product, INVERTER an INVERTER). An omitted `warrantyMonths` is filled from the product's catalog warranty. | Show the field error on the asset row. |

## 2. Quotations (Sales / Manager)

* `QuotationView` gains:
  * `currentSurveyVersion` (int) and `surveyChanged` (bool): the survey was amended after this quotation's `onSiteSurveyVersion`.
  * `pricingReview` (staff only, AUTOMATIC drafts only): `{ outstanding: [ { code, message, productId } ], noteRequired }`. It is
    recomputed from the current lines and costs, so it shrinks as Sales fixes the draft.
* `POST /api/quotations/{id}/issue` body is now `{ revision, reviewNote? }` (previously `{ revision }`, still accepted).
  * AUTOMATIC drafts with outstanding `PRICE_MISSING`, `MISSING_SOLAR_PANEL`, `MISSING_INVERTER`, `LABOR_NOT_SPECIFIED` or
    `INVALID_TOTAL` → `409 QUOTATION_PRICING_REVIEW_REQUIRED`, `details.outstanding`. A missing price must be entered; a note
    cannot excuse it.
  * Remaining `PRODUCT_UNAVAILABLE`, `WRONG_ITEM_KIND`, `CURRENCY_NOT_SUPPORTED` or `CONDITIONS_REQUIRE_REVIEW` → the same code
    until `reviewNote` (≤ 1000 chars) is given. The note is stored in the ISSUED event.
  * A draft based on an older survey version → `409 QUOTATION_SURVEY_CHANGED`.
* **New** `POST /api/quotations/{id}/confirm-survey-version` (Sales owner), body `{ revision, reason }` → `200 QuotationView`.
  Re-bases a DRAFT on the current survey version after Sales reviewed its lines. Errors:
  * `400 VALIDATION_FAILED` (reason missing or > 1000 chars);
  * `409 QUOTATION_NOT_DRAFT`;
  * `409 INVALID_STATE` (already current);
  * `409 QUOTATION_STALE_REVISION`;
  * `404 QUOTATION_NOT_FOUND` (not the owner);
  * `403` for non-Sales roles.
* `POST /api/quotations/{id}/revisions` now also works for an **ACCEPTED** quotation whose survey changed and that has no
  contract. The new draft keeps the old `onSiteSurveyVersion` (its lines are copies) until `confirm-survey-version`.
* `GET /api/quotations/pending-review` now also lists every non-superseded quotation with `surveyChanged = true`.
* Events: new `SURVEY_AMENDED` (detail `survey vN`) and `SURVEY_VERSION_CONFIRMED` (detail `survey vA → vB: reason`). Events
  with the same timestamp are listed in lifecycle order.

Sales flow when a survey changes: queue item (`surveyChanged`) → `revisions` → edit lines → `confirm-survey-version` → `issue`.

## 3. Energy monitoring (new, read-only)

| Role | Method | Path |
|---|---|---|
| Customer (own projects; others 404) | GET | `/api/customers/me/installation-projects/{projectId}/energy-monitoring` |
| Manager, Admin | GET | `/api/installation-projects/{projectId}/energy-monitoring` |

Response:

```json
{
  "projectId": "…", "projectStatus": "COMPLETED", "handoverAt": "…",
  "dataSource": "SIMULATED", "measuredDataAvailable": false, "status": "NO_MEASURED_DATA",
  "simulated": { "simulationId": "…", "selectedAt": "…", "algorithmVersion": "…", "energyStatus": "…", "panelCount": 0,
                 "installedCapacityKwp": 0.0, "annualEnergyKwh": 0.0, "specificYieldKwhPerKwpYear": 0.0 },
  "alerts": [], "alertsStatus": "NOT_CONFIGURED",
  "notice": "Expected production estimated from the selected pre-installation simulation. It is not measured production; …"
}
```

(Values are placeholders.)
* `status` is `SIMULATION_UNAVAILABLE` and `simulated` is `null` when the customer never selected a simulation.
* **Always label these numbers as "expected (simulated)", never "produced".**
* There are no alerts until a measured data source and thresholds are approved (see
  [business-policy-decisions.md](business-policy-decisions.md), D-08).

## 4. Refunds of contract payments (staff screens)

When PayOS confirms money that cannot be applied, the existing refund queue (`GET /api/manager/after-sales/refunds`, Manager
decision, Admin record) now also receives a `PENDING_APPROVAL` refund with `paymentAttemptId` set and no request, subscription
or quotation. This covers a second payment of an already paid contract, a wrong amount, or a contract no longer payable.
`reason` starts with "Payment {orderCode} received but not applied". Previously such money was only a status
(`REFUND_REQUIRED` / `REVIEW_REQUIRED`) on the attempt.

## 5. Scheduling

* A cross-zone appointment dispatched by a Manager keeps its authorized technician when the customer or Sales reschedules it,
  even when the site has no zone. If that technician cannot take the new time, the reschedule is refused with the eligibility
  code (e.g. `TECHNICIAN_ON_LEAVE`) and the original appointment is unchanged.
* When Sales reschedules to another technician, the authorization is not carried over.
* No contract changes for the FE.

## 6. New error codes

| Code | Status |
|---|---|
| `SCHEMA_NOT_READY` | 503 |
| `QUOTATION_SURVEY_CHANGED` | 409 |
| `QUOTATION_PRICING_REVIEW_REQUIRED` | 409 |
| `CONTRACT_SURVEY_CHANGED` | 409 |
| `CONTRACT_TERMS_REQUIRED` | 400 |
| `INSTALLATION_CHECKLIST_WAIVER_REASON_REQUIRED` | 400 |
| `INSTALLATION_ASSET_PRODUCT_VALIDATION_FAILED` | 400 |

## 7. M-1 to M-4 (2026-10-31)

### 7.1 New endpoints

| Role | Method | Path | Body | Response / errors |
|---|---|---|---|---|
| Customer (owner) | POST | `/api/customers/me/quotations/{id}/reject` | `{ reason? }` (≤ 2000) | `200 QuotationView` (`status: REJECTED`, `closedAt`, `closeReason`); a repeat is a no-op.<br>`409 QUOTATION_NOT_ISSUED` (accepted, revised or closed).<br>`404`. |
| Customer (owner) | POST | `/api/customers/me/survey-requests/{id}/withdraw` | `{ reason? }` (≤ 1000) | `200 { surveyRequestId, status: "Cancelled", reasonCode: "CUSTOMER_WITHDREW", note, closedAt, cancelledQuotationIds }`.<br>`409 SURVEY_REQUEST_CONTRACT_EXISTS`, `SURVEY_REQUEST_APPOINTMENT_OPEN` (cancel the appointment first), `SURVEY_REQUEST_CLOSED`, `SURVEY_REQUEST_CLOSURE_CONFLICT`.<br>`404 SURVEY_REQUEST_NOT_FOUND`. |
| Sales (owner) | POST | `/api/survey-requests/{id}/close` | `{ reasonCode, note? }`: `NOT_FEASIBLE`, `CUSTOMER_DECLINED`, `NO_RESPONSE`, `OTHER` (note required) | `200` (same shape, `status: "Closed"`).<br>`400 VALIDATION_FAILED`.<br>`409 SURVEY_REQUEST_NOT_FEASIBLE_UNCONFIRMED` and the withdraw 409s.<br>`404`. |
| Admin | PUT | `/api/admin/knowledge-documents/{id}/audience` | `{ revision, audience: "INTERNAL" \| "CUSTOMER", reason }` | `200 KnowledgeDocumentView`.<br>`400 VALIDATION_FAILED`.<br>`409 RAG_DOCUMENT_AUDIENCE_UNCHANGED`, `RAG_STALE_REVISION`.<br>`404`. |

### 7.2 Behaviour changes

| Area | Change | FE action |
|---|---|---|
| Survey request status | New value `Closed` (numeric 7). Withdrawn requests use `Cancelled` (6). Both are terminal. | Show both as closed; hide every action. |
| Quotation status | New `REJECTED` and `CANCELLED`, with `closedAt`, `closeReason`. `canAccept` false. Sales may still `revisions` a REJECTED quotation once while the request is open; the rejected version stays REJECTED. | Customer: "Reject" next to "Accept"/"Request revision". |
| Quotation work on a closed request | `409 QUOTATION_REQUEST_CLOSED`. Automatic generation → `REQUEST_CLOSED`. Survey amendment → `409 ONSITE_SURVEY_REQUEST_CLOSED`. | Disable. |
| Queues | Closed requests leave `GET /api/survey-requests/pending` and `GET /api/quotations/pending-review`. Sales' own request list still shows them, with their status. | — |
| Knowledge audience | **The `audience:customer` tag no longer grants anything.** The new `audience` field is authoritative. Every document starts `INTERNAL`, including every existing one. The view adds `audience`, `legacyAudienceTag` (a document that still has the old tag: review it) and `audienceChanges[]`. | Admin screen: an audience switch with a mandatory reason, using `revision`. |
| Chat history (customers) | `messages[].withheld = true` with `content` = a fixed notice and no citations for answers created before 2026-10-31, answers retrieved for staff, and answers citing a document no longer customer-visible. | Show the notice; suggest asking again. |
| Installation `equipment` (staff view) | New `history[]` of stored substitution decisions: `{ substitutionId, status (CUSTOMER_APPROVAL_PENDING / APPROVED / CUSTOMER_DECLINED / SUPERSEDED), fingerprint, quotationId, managerUserId, managerReason, managerApprovedAt, customerApprovedAt, customerDeclinedAt, customerDeclineReason, supersededAt, items[] }`. A customer rework request while a substitution waits for them declines it, and the Manager must approve again. Registering panels or inverters supersedes the active approval. | Show the history on the Manager screen. |
| Coverage decision | `coverage: "MANUFACTURER_WARRANTY"` → `409 AFTER_SALES_MANUFACTURER_WARRANTY_POLICY_PENDING`. | Do not offer it yet (or show it disabled with the reason). |

## 8. Final phase (2026-10-31)

**Concurrency.** Closing or withdrawing a request and creating a contract can no longer both succeed. Whichever runs second gets:

| Situation | Response |
|---|---|
| Contract creation after a withdrawal or closure | `409 CONTRACT_REQUEST_CLOSED` (new) |
| Closure or withdrawal after a contract was created | `409 SURVEY_REQUEST_CONTRACT_EXISTS` |
| Quotation create, revision or generation racing a closure | `409 QUOTATION_REQUEST_CLOSED`; automatic outcome `REQUEST_CLOSED` |

**Request with a DRAFT contract (B-02).** `SURVEY_REQUEST_CONTRACT_EXISTS` now carries `details.contractStatus` (e.g. `"DRAFT"`).
* For a DRAFT the message says it must be discarded first. There is **no discard endpoint yet**: it needs the M-7 migration.
* Show "a draft contract exists" and keep the closure disabled.

**New request after a closure (B-01).** A closed or withdrawn request is never reopened. Offer the normal "new pre-survey" flow
for the same site; nothing from the old request is copied.

**Draft cancelled with its request.** A never-issued DRAFT quotation becomes `CANCELLED` with `issuedAt = null` when its request
is closed.

**Manufacturer warranty activation (B-04), new endpoint:**

| Role | Method | Path | Body | Response / errors |
|---|---|---|---|---|
| Manager, Admin (after-sales staff controller) | POST | `/api/manager/after-sales/assets/{assetId}/warranty-activation` | `{ activatedAt (ISO 8601 with offset, not in the future), evidenceFileId (verified PROJECT_DOCUMENT), reason }` | `200 AssetView`.<br>`400` (validation, reason).<br>`409 AFTER_SALES_WARRANTY_SOURCE_UNVERIFIED` (no catalog duration, or `LEGACY_UNVERIFIED`), `AFTER_SALES_WARRANTY_ACTIVATION_RECORDED` (only once).<br>`404 AFTER_SALES_ASSET_NOT_FOUND`. |

* The after-sales `AssetView` gains `warrantyMonths`, `warrantySource`, `warrantyStartsAt` and `warrantyEndsAt`. Dates exist only
  after a verified activation.
* The MANUFACTURER_WARRANTY coverage decision is still refused (`AFTER_SALES_MANUFACTURER_WARRANTY_POLICY_PENDING`).

| New error code | Status |
|---|---|
| `CONTRACT_REQUEST_CLOSED` | 409 |
| `AFTER_SALES_WARRANTY_SOURCE_UNVERIFIED` | 409 |
| `AFTER_SALES_WARRANTY_ACTIVATION_RECORDED` | 409 |

## 9. M-7: discard a DRAFT contract (2026-10-31)

The flow for a sale that ends after a contract draft was made:
1. Sales discards the DRAFT contract (new endpoint).
2. Sales closes the request (`POST /api/survey-requests/{id}/close`, unchanged).

These are two explicit steps. Closing never discards anything by itself.

**New endpoint:**

| Role | Method | Path | Body | Response / errors |
|---|---|---|---|---|
| Sales (owner) **only** | POST | `/api/contracts/{contractId}/discard` | `{ revision, reason }` (reason 1–1000 characters) | `200 ContractView` with `status: "DISCARDED"` and `discard: { discardedAt, discardedByUserId, reason }`; a repeat returns the same view.<br>`400 VALIDATION_FAILED` (no `revision`), `400 CONTRACT_DISCARD_REASON_REQUIRED`.<br>`403` Customer, Manager, Admin, Technician.<br>`404 CONTRACT_NOT_FOUND` (another Sales owner's contract too).<br>`409 CONTRACT_NOT_DISCARDABLE` (issued or later, or an amendment draft), `409 CONTRACT_STALE_REVISION`. |

**Behaviour changes:**
* **New contract status `DISCARDED`** (staff views only; customers never see drafts, discarded or not). It is terminal:
  issue, terms edit, amendment, signing, payment and installation-project creation all refuse it.
* **Creating a contract** for a quotation whose draft was discarded → `409 CONTRACT_DRAFT_DISCARDED`. Re-contracting is not
  defined; close the request instead.
* **`SURVEY_REQUEST_CONTRACT_EXISTS`** with `details.contractStatus = "DRAFT"` means: offer "Discard draft contract" first.
  For any other status, closure stays impossible.
* **A discarded contract no longer counts as live:** it does not block closure, the survey amendment lock or a stale
  quotation revision.

| New error code | Status |
|---|---|
| `CONTRACT_NOT_DISCARDABLE` | 409 |
| `CONTRACT_DRAFT_DISCARDED` | 409 |
| `CONTRACT_DISCARD_REASON_REQUIRED` | 400 |

