# API / FE integration handoff (2026-11-18)

Every response keeps the standard envelope `{ isSuccess, data, error: { code, message, details }, traceId }`. Error mapping is unchanged:
`*_NOT_FOUND` → 404, validation → 400, role → 403, anonymous → 401, state / staleness / conflict → 409, rate limit → 429 with `Retry-After`.
Times are ISO-8601 with offset. **No existing response shape changed**; every change is a new endpoint, a new optional field or opt-in parameters.

## 1. Paper contract uploaded by Sales

`POST /api/contracts/{contractId}/signed-document` — roles **Sales (owner of the survey request), Manager, Admin**; Customer/Technician 403; Sales of
another request 404. Rate limit `contract-write` (10/min/user).

```json
{ "fileId": "uuid", "officialDocumentSha256": "hex of the printed official PDF", "signedOn": "2026-11-18", "note": "Ký tại văn phòng" }
```
* `fileId`: VERIFIED `SIGNED_CONTRACT` PDF (≤ 20 MB) uploaded **by the caller** with the normal presigned flow (`POST /api/files/presigned-upload`,
  purpose `SIGNED_CONTRACT` — now allowed for Sales/Manager/Admin), `signedOn` optional (not in the future), `note` ≤ 300.
* 200 → `ContractView` with `status: "UNDER_REVIEW"` and the new entry in `submissions`. A Manager/Admin confirms with the existing
  `POST /api/contracts/{id}/review`.
* Errors: 409 `CONTRACT_INVALID_STATE` (not AWAITING_SIGNATURE / SIGNATURE_REJECTED, e.g. a scan already under review or a DRAFT), 409
  `CONTRACT_WRONG_DOCUMENT_VERSION`, 409 `FILE_ALREADY_LINKED`, 400 `VALIDATION_FAILED` (`signedOn`, `note`), file errors (`FILE_WRONG_PURPOSE`, …).

State: `AWAITING_SIGNATURE | SIGNATURE_REJECTED ──scan (customer or staff)──▶ UNDER_REVIEW ──review──▶ SIGNED | SIGNATURE_REJECTED`.

**FE**: Sales contract panel (read-only today) gains "Tải bản hợp đồng giấy đã ký" when `canUploadPaperScan(status)`; upload with purpose
`SIGNED_CONTRACT`, then call `uploadPaperSignedContract` (`src/services/installationScheduleService.ts`). Show the submission history (already in the view).

## 2. Installation date chosen by the customer

| Method / path | Role | Rate limit | Body / query | 200 |
|---|---|---|---|---|
| `GET /api/technicians/me/contracts/{id}/installation-availability` | surveying Technician | `installation-preview` 15/min (Redis) | `from`, `to` (yyyy-MM-dd, ≤ 31 days), `durationDays` 1–60 | `InstallationSlotAvailabilityView[]` |
| `GET /api/technicians/me/contracts/{id}/installation-slot-offers` | surveying Technician | `installation-read` | – | `InstallationSlotOfferView[]` (history) |
| `POST /api/technicians/me/contracts/{id}/installation-slot-offers` | surveying Technician | `installation-scheduling` 10/min (Redis) | `{ slots: [{ startsAt, endsAt }] (1–5), note? }` | open offers |
| `GET /api/customers/me/contracts/{id}/installation-slot-offers` | Customer (own) | `installation-read` | – | open offers, `available` re-checked |
| `POST /api/customers/me/installation-slot-offers/{offerId}/select` | Customer (own) | `installation-scheduling` | – | `InstallationScheduleView` (PLANNED, or CONFIRMED when PAID) |

`InstallationSlotOfferView`: `offerId, contractId, technicianUserId, technicianName, startsAt, endsAt, status (OPEN|SELECTED|WITHDRAWN), available,
unavailableReason, note, createdAt, selectedAt, selectedScheduleId, withdrawnAt, withdrawnReason (technician only), revision`.

Errors: 404 `INSTALLATION_SCHEDULE_CONTRACT_NOT_FOUND` / `INSTALLATION_SLOT_OFFER_NOT_FOUND` (not yours), 409 `INSTALLATION_SCHEDULE_CONTRACT_NOT_SIGNED`,
409 `INSTALLATION_SLOT_OFFER_UNAVAILABLE` (passed, technician busy/ineligible, taken by another customer), 409 `INSTALLATION_SLOT_OFFER_NOT_OPEN`
(replaced or already chosen), 409 `INSTALLATION_SCHEDULE_INSTALLATION_STARTED` / `…_ASSIGNED_TO_OTHER_TECHNICIAN` / `…_COVERAGE_UNAVAILABLE`, 400 validation.

States: offer `OPEN ──customer selects──▶ SELECTED` (others of the contract `WITHDRAWN: ANOTHER_OFFER_SELECTED`); `OPEN ──technician republishes──▶
WITHDRAWN: REPLACED_BY_NEW_OFFERS`. The selection creates the normal schedule `PLANNED ──contract PAID──▶ CONFIRMED` (or RESCHEDULE_REQUIRED).

**FE**: technician contract page — date range + duration → availability list → pick 1–5 → publish (validate with `offerSlotsProblem`). Customer
contract/project page — list `customerOfferChoices(offers)`, disable when `!choosable`, select, then show the schedule with `scheduleStatusLabel`
(never show PLANNED as confirmed). Notification type `INSTALLATION_SLOTS_OFFERED` arrives on the existing notification list.

## 3. Cash for installation + packages, and packages alone

`POST /api/customers/me/after-sales/purchases` (Customer, `payment-create` limit) — optional field `paymentMethod`:
```json
{ "contractId": "uuid", "maintenancePackageId": "uuid|null", "disasterPackageId": "uuid|null", "includeContract": true, "paymentMethod": "CASH" }
```
* `paymentMethod`: `ONLINE` (default, unchanged PayOS checkout) or `CASH`. **Second pass: CASH is no longer limited to `includeContract: true`.**
  * `includeContract: true` + SIGNED contract → one CASH attempt with a CONTRACT line + one line per package (`amount` = contract total + package prices).
  * `includeContract: false` + PAID contract → one CASH attempt with the package lines only (`contractId: null`).
  * `includeContract: false` + unpaid contract → 409 `AFTER_SALES_INVALID_STATE` (unchanged rule for every method: pay the installation first or together).
* CASH answer: `checkout.method = "CASH"`, `status = "PENDING"`, `checkoutUrl/qrCode/expiresAt = null`. Not a payment.
* Errors: 409 `PAYMENT_CASH_SELECTED` (a cash choice already exists for the contract or for this kind of package), 409 `PAYMENT_CHECKOUT_IN_PROGRESS`,
  `SERVICE_PACKAGE_INACTIVE`, `SERVICE_SUBSCRIPTION_OVERLAP`, 404 `AFTER_SALES_PROJECT_NOT_FOUND` (not your contract), 400 `AFTER_SALES_VALIDATION_FAILED`.
* Withdraw a contract cash choice: `POST /api/customers/me/contracts/{id}/payments/cancel`.
* Confirmation (Manager/Admin): either `POST /api/contracts/{contractId}/payments/cash/confirm` (contract attempts, unchanged) or the generic
  `POST /api/manager/payments/{attemptId}/cash/confirm` (§7). `amount` = the attempt's `amount` exactly.

## 4. Opt-in pagination

| Endpoint | Roles | Filters kept | Order |
|---|---|---|---|
| `GET /api/installation-projects` | Manager, Admin | `status` | `createdAt` desc, id desc |
| `GET /api/manager/installation-schedules` | Manager, Admin | `status` | `createdAt` desc, id desc |
| `GET /api/manager/after-sales/requests` | Manager, Admin | `status`, `kind` | `createdAt` desc, id desc |
| `GET /api/customers/me/after-sales/requests` | Customer (own rows only) | – | `createdAt` desc, id desc |
| `GET /api/customers/me/after-sales/notifications` | Customer (own rows only) | – | `createdAt` desc, id desc |

No `page`/`pageSize` → the historical array (FE unchanged). Either given → `{ items, page, pageSize, totalItems, totalPages }` (`page` default 1,
`pageSize` default 20, max 100, `page` ≤ 10 000; otherwise 400 `VALIDATION_FAILED` with `details.page|pageSize`). React Query keys must include
`page`, `pageSize` and every filter. Migration path: switch a screen to paging by sending `page=1&pageSize=20` and reading `data.items`; the
array form stays until every consumer has switched (then it can be retired in a coordinated change).

**Not paged (inventory, unchanged)**: Sales `GET /api/survey-requests/pending|my|escalated`, `GET /api/sales/after-sales/tasks/open|mine`,
`GET /api/manager/after-sales/tasks`, `GET /api/admin/knowledge-documents`, `GET /api/technicians/me/after-sales/visits`,
`GET /api/technicians/me/installation-projects|installation-schedules`, `GET /api/customers/me/contracts|quotations|installation-projects` (per customer,
small), `GET /api/installations/ready-contracts` (bounded by workflow). Intentionally not paged: lookups (provinces, communes, warranty options, package
catalog, zones), AI usage breakdowns (bounded aggregations), per-object histories (a contract's schedules, a project's evidence).

## 5. Rate limits added

| Policy | Default | Applied to |
|---|---|---|
| `portal-read` | 60/min/user | `GET customers/me/sites`, `customers/me/pre-surveys`, `customers/me/survey-requests[/{id}]`, `pre-surveys/{id}/surface`, `pre-surveys/{id}/simulations[/{id}]`, `survey-requests/pending`, `/my`, `/{id}` |
| `portal-write` | 20/min/user | `POST customers/me`, `POST customers/me/sites`, `POST pre-surveys`, `PUT pre-surveys/{id}`, `POST pre-surveys/{id}/submit`, `PUT pre-surveys/{id}/surface` |
| `scheduling-write` (existing) | 10/min/user | `POST survey-requests/{id}/claim` (contended state change) |
| `retired-endpoint` | 30/min/user | the 14 routes answering 410 `ENDPOINT_RETIRED` |

Configurable under `RateLimiting:PortalRead|PortalWrite|RetiredEndpoint`. 429 keeps the envelope (`AUTH_TOO_MANY_REQUESTS`) and `Retry-After`.
The PayOS webhook keeps its own IP guard (600/min); no customer policy applies to it.

## 6. Dashboard

`GET /api/admin/after-sales/dashboard` gains the metric `collected_unapplied`; the meaning of `collected_contracts|packages|services` is now "applied
money" (see ADMIN_DASHBOARD_METRICS.md). Render metrics generically by `key`; never add `collected_unapplied` to revenue.

## FE files added / edited
* New: `src/types/res/installationScheduleRes.ts`, `src/types/req/installationScheduleReq.ts`, `src/services/installationScheduleService.ts`,
  `src/features/installations/slotOfferRules.ts`, `tests/installation-slot-offer-rules.test.mjs` (10 tests).
* Edited: `src/services/api/endpoints.ts` (new routes), `src/types/req/customerAfterSalesReq.ts` (`paymentMethod`).
* Screens: not built (see BUSINESS_REMEDIATION_REPORT.md §5).

---

# Second pass (backend only, 2026-11-18)

## 7. Technician-priced after-sales quotation

`POST /api/technicians/me/after-sales/requests/{requestId}/quotations` — **Technician** (the technician of a non-declined, non-cancelled visit of the
request; anyone else 404 `SERVICE_REQUEST_NOT_FOUND`). Rate limit `quotation-write` (20/min/user). 201 → `ServiceRequestView` (staff view).

```json
{
  "lines": [
    { "itemId": "uuid of a CHARGEABLE / DISASTER_PROTECTION item", "description": "optional ≤ 300",
      "components": [ { "productId": "uuid", "quantity": 2 }, { "productId": "uuid (labor service)", "quantity": 1 } ] }
  ],
  "repairWarrantyMonths": 6,
  "note": "optional ≤ 1000"
}
```
* No amount is accepted. Each component: active catalog product priced in VND; `quantity` > 0 and ≤ 10 000; ≤ 20 components per item; each product
  once per item; each item once; every open chargeable / disaster item exactly once (else 400 `AFTER_SALES_VALIDATION_FAILED`).
* Pricing: amount = quantity × catalog unit price (whole VND). Service product types (`INSTALLATION_LABOR`, `TRANSPORT`, `OTHER_SERVICE`) → labor,
  others → parts. Disaster support is computed and reserved as before; `customerAmount = totalAmount − disasterSupportAmount`.
* `ServiceQuotationView` gains `issuedByRole` (`SALES` | `TECHNICIAN`); `lines[].components[]` = `{ productId, sku, name, productType, unit, unitPrice,
  quantity, amount, kind: PARTS|LABOR }` (snapshot; absent on Sales quotations).
* Errors: 409 `SERVICE_QUOTATION_PRICE_MISSING` (`details.items[]` with `itemId`, `productId`, `code`, `message`), 409 `SERVICE_QUOTATION_PRODUCT_UNAVAILABLE`,
  409 `AFTER_SALES_INVALID_STATE` (one-time maintenance, request not awaiting a quotation), 409 `PAYMENT_CHECKOUT_IN_PROGRESS` (customer is paying the open
  version), 409 `DISASTER_CLAIM_NOT_APPROVED`, 409 `AFTER_SALES_STALE_REVISION` (concurrent issue), 400 validation.
* State: items `OPEN → QUOTED`; request `AWAITING_QUOTATION → AWAITING_CUSTOMER`; previous open version `→ SUPERSEDED`. Customer then:
  `POST /api/customers/me/after-sales/quotations/{id}/accept | /reject` (unchanged); read via `GET /api/customers/me/after-sales/requests/{id}` (`quotations[]`).
* The Sales path `POST /api/sales/after-sales/requests/{id}/quotations` is unchanged (fallback; still needs a claimed task). One-time maintenance is
  priced by Sales only.

## 8. After-sales cash payment

| Method / path | Role | Rate limit | Result |
|---|---|---|---|
| `POST /api/customers/me/after-sales/quotations/{id}/cash` | Customer (own) | `installation-write` | `PaymentAttemptView` (CASH, PENDING); repeated call returns it |
| `POST /api/customers/me/after-sales/quotations/{id}/cash/cancel` | Customer (own) | `installation-write` | the attempt, CANCELLED |
| `POST /api/customers/me/after-sales/quotations/{id}/checkout` (unchanged) | Customer | `payment-create` | now 409 `PAYMENT_CASH_SELECTED` while cash is chosen |
| `GET /api/manager/payments?method=CASH&status=PENDING&from&to&page&pageSize` | Manager, Admin | `contract-read` | `PagedResult<StaffPaymentAttemptView>` |
| `POST /api/manager/payments/{attemptId}/cash/confirm` | Manager, Admin | `contract-write` | `PaymentAttemptView` (PAID, staff fields) |

* `StaffPaymentAttemptView` = `{ attempt: PaymentAttemptView, customerUserId, lines: [{ itemType, amount, status, contractId, subscriptionId, quotationId }] }`,
  newest first; `from`/`to` are ISO instants on `createdAt`.
* Confirm body (unchanged shape): `{ amount, receiptReference (≤ 100, unique), evidenceFileId (VERIFIED PAYMENT_EVIDENCE uploaded by the caller), note? }`.
* Errors (choose): 404 `SERVICE_QUOTATION_NOT_FOUND`, 409 `AFTER_SALES_INVALID_STATE` (not ACCEPTED), 409 `SERVICE_QUOTATION_EXPIRED`, 409 `PAYMENT_ALREADY_PAID`,
  409 `PAYMENT_CHECKOUT_IN_PROGRESS` (online link open: cancel it first). Errors (confirm): 404 `PAYMENT_ATTEMPT_NOT_FOUND`, 409 `PAYMENT_CASH_AMOUNT_MISMATCH`
  (`details.expected`), 409 `PAYMENT_CASH_RECEIPT_DUPLICATE`, 409 `PAYMENT_ALREADY_PAID`, 409 `PAYMENT_CASH_NOT_SELECTED` (withdrawn / cancelled),
  409 `PAYMENT_CASH_ITEM_NOT_PAYABLE` (quotation expired, rejected or replaced — nothing recorded), 400 validation, file errors.
* States: attempt `PENDING ──confirm──▶ PAID` | `PENDING ──customer cancel / reject / quotation expiry──▶ CANCELLED`; quotation `ACCEPTED ──paid──▶ PAID`,
  request `AWAITING_CUSTOMER ──paid──▶ READY_FOR_WORK`. **A pending cash choice never extends the quotation**: at expiry the sweep withdraws it and the
  quotation becomes EXPIRED (support released). Show the quotation's `expiresAt` next to the cash choice.

## 9. Admin dashboard overview

`GET /api/admin/after-sales/dashboard/overview?from=yyyy-MM-dd&to=yyyy-MM-dd` — **Admin**; default this month; at most 366 days; 400
`AFTER_SALES_VALIDATION_FAILED` otherwise. Rate limit `installation-read`. Response `AdminOverviewView` (every list item `{ key, count, amount? }`):
`accounts[] { role, total, active, pendingVerification, inactive }`, `surveyRequestsByStatus`, `outstandingSurveyAppointments`, `installationQuotationsByStatus`,
`contractsByStatus`, `contractsWithOfficialPdf`, `contractsWithSignedScan`, `signedScansUploadedByStaff`, `installationProjectsByStatus`,
`installationSchedulesByStatus`, `openInstallationSlotOffers`, `serviceRequestsByKindAndStatus` (key `KIND:STATUS`), `serviceQuotationsByStatus`,
`serviceQuotationsByIssuer`, `disasterClaimsByStatus`, `subscriptionsByKindAndStatus`, `openSalesTasksByType`, `openServiceVisitsByStatus`,
`pendingCashChoices`, `refundRecordsByStatus`, period: `paymentAttemptsCreatedByMethodAndStatus` (key `METHOD:STATUS`), `collectedByMethod`,
`combinedCheckoutsPaid`, `packageRevenueBySaleChannel` (`WITH_INSTALLATION` / `SEPARATE`), `installationQuotationValueIssued`,
`serviceQuotationValueIssued`. Definitions: ADMIN_DASHBOARD_METRICS.md. Render as given; do not recompute totals client-side. Detail rows: `GET /api/manager/payments`.
AI/RAG usage stays on `GET /api/admin/rag-usage/*` (unchanged).

## 10. Pagination — complete inventory (second pass)

All opt-in: no `page`/`pageSize` → historical array; either → `{ items, page, pageSize, totalItems, totalPages }`; `page` 1–10 000, `pageSize` 1–100
(default 20); 400 `VALIDATION_FAILED` otherwise. Authorization/ownership filters apply before COUNT; stable order with an id tie-breaker.

| Endpoint | Roles | Order | Since |
|---|---|---|---|
| `GET /api/installation-projects?status` | Manager, Admin | created desc | pass 1 |
| `GET /api/manager/installation-schedules?status` | Manager, Admin | created desc | pass 1 |
| `GET /api/manager/after-sales/requests?status&kind` | Manager, Admin | created desc | pass 1 |
| `GET /api/customers/me/after-sales/requests` | Customer (own) | created desc | pass 1 |
| `GET /api/customers/me/after-sales/notifications` | Customer (own) | created desc | pass 1 |
| `GET /api/survey-requests/pending` | Sales | submitted asc | pass 2 |
| `GET /api/survey-requests/my` | Sales (own) | claimed desc | pass 2 |
| `GET /api/sales/after-sales/tasks/open` | Sales | created asc | pass 2 |
| `GET /api/sales/after-sales/tasks/mine` | Sales (own) | created asc | pass 2 |
| `GET /api/manager/after-sales/tasks?status` | Manager, Admin | created asc | pass 2 |
| `GET /api/technicians/me/after-sales/visits` | Technician (own) | start asc | pass 2 |
| `GET /api/admin/knowledge-documents?status&category` | Admin | updated desc | pass 2 |
| `GET /api/manager/payments` (new, always paged) | Manager, Admin | created desc | pass 2 |

Already paged before this work: `GET /api/products`, `/api/admin/products`, `/api/admin/users`, `/api/admin/dispatch-requests`, `/api/admin/iot/devices`,
`/api/customers/me/sites`, `/api/customers/me/pre-surveys`, `/api/admin/system-settings/scheduling/history`, `/api/admin/rag-configuration/versions`.

Intentionally not paged (with reason): lookups (`/administrative-units/*`, warranty options, package catalog, operational zones, zone technicians); per-object
histories (a contract's schedules / offers / payments, a project's evidence, a quotation, a request); per-customer small sets (`/customers/me/contracts`,
`/quotations`, `/installation-projects`, `/installation-schedules`, `/after-sales/subscriptions`, chat sessions); bounded work queues
(`/installations/ready-contracts` = PAID contracts without a project; `/technicians/me/open-appointments`, `/technicians/me/appointments` (upcoming by default),
`/technicians/me/installation-projects`, `/technicians/me/installation-schedules` (current work of one technician)); aggregations (`/admin/rag-usage/*`,
dashboards); candidate searches (technician candidates, availability, slots). Escalated survey requests (`/survey-requests/escalated`) are a short
exception queue. These can be paged later with the same `PageRequest` helper if they grow.

## 11. Rate limits of the second-pass routes

All new routes carry a policy (enforced by `EndpointRateLimitCoverageTests`): technician quotation `quotation-write`, customer cash choose / cancel
`installation-write`, payment desk list `contract-read`, cash confirm by attempt `contract-write` (10/min/user — not `payment-create`, whose 5/10 min
per-contract partition would throttle a cashier), overview `installation-read`. 429 keeps the envelope and `Retry-After`; the PayOS webhook keeps its own
600/min IP guard.

# Final acceptance pass (backend only, 2026-11-18, pass 3)

No frontend code was changed in pass 3. Everything below is a contract for the FE team to implement.

## 12. One-time maintenance: inspection first, then a technician-priced quotation

`POST /api/customers/me/after-sales/one-time-maintenance` (Customer, `installation-write`)

```json
{ "projectId": "…", "preferredStartsAt": "2026-12-01T09:00:00+07:00", "note": "…", "inspectionRequired": true }
```

| `inspectionRequired` | Behavior |
|---|---|
| omitted / `true` (**default, new**) | request created in `SUBMITTED`; Managers are notified; the Manager plans an **INSPECTION** visit as for a fault |
| `false` | previous behavior: `AWAITING_QUOTATION` and a Sales `MAINTENANCE_QUOTATION` task (Sales prices it as before) |

After the inspection visit is reported with items, the **assigned technician** prices it from the catalog with the same endpoint as a fault repair:
`POST /api/technicians/me/after-sales/requests/{requestId}/quotations` (lines = catalog `productId` + quantity per item; prices come from the catalog;
an inactive / unknown / non-VND product → `SERVICE_QUOTATION_PRODUCT_UNAVAILABLE`, a product with price 0 → `SERVICE_QUOTATION_PRICE_MISSING`). The technician cannot quote a package `MAINTENANCE` request or an
uninspected one-time maintenance (409 invalid state).

The customer then accepts / rejects and pays (cash or PayOS) through the existing quotation endpoints (§7, §8). After payment the request is
`READY_FOR_WORK`; the Manager plans a **MAINTENANCE** visit; the technician reports work; the customer accepts → `COMPLETED`, outcome `MAINTENANCE_DONE`.
An expired quotation of an inspected request follows the fault path: the request status is recomputed from its items (`AWAITING_QUOTATION` while chargeable
items are open, so the technician can issue a new quotation) and a Sales `REPAIR_QUOTATION` follow-up task is opened. Without inspection
(`inspectionRequired: false`) it re-opens the Sales `MAINTENANCE_QUOTATION` task.

UI impact: show `SUBMITTED` / inspection steps for one-time maintenance; `visits[].purpose` is `INSPECTION` then `MAINTENANCE`.

## 13. Unapplied-money ledger (read only)

`GET /api/manager/after-sales/unapplied-money?status=&page=&pageSize=` (Manager, Admin; `installation-read`). Always paged
(`page` default 1, `pageSize` default 20, max 100). `status` optional: `PENDING_APPROVAL`, `APPROVED`, `REJECTED`, `REFUNDED`; anything else → 400.

Item: `recordId, status, amount, reason, customerUserId, paymentAttemptId, subscriptionId, quotationId, requestId, createdAt, approvedAmount, refundedAt`.

It lists money received that could not be applied (for example a PayOS payment that arrived after cash was confirmed). It matches the dashboard
`collected_unapplied` metric. **Nothing can be approved or refunded from the application.** Refunds stay outside the application, as the team
decided on 2026-11-13. Do not build refund buttons.

## 14. Payment reconciliation timing (no API change)

A PayOS payment whose webhook is lost is confirmed by the reconciliation worker. Defaults (`Payments` section; no values in `appsettings.json`):
`ReconciliationIntervalSeconds` = 30, `ReconcileAfterSeconds` = 60, `ReconcileBatchSize` = 25, `MaxBatchesPerTick` = 20.
Measured worst case: 89 s after payment, against a target of ≤ 2 min. The FE may keep polling the attempt status (existing endpoint) for up to 2 minutes
before showing "still processing".

## 15. Obsolete FE rule: `cashPurchaseAllowed`

The FE rule `cashPurchaseAllowed` (in `tests/installation-slot-offer-rules.test.mjs`, added in pass 1) encodes a restriction the business **removed**:
cash is not limited to packages bought together with the installation. The backend accepts cash for the installation-plus-packages checkout, for packages
alone, and for after-sales quotations. The FE team should delete the rule and its tests. The backend has no such flag.
