# Business remediation report (2026-11-18)

Branch `feat/cloud-rag-security-cost-control` @ `4fb5b95`, working tree only: **nothing was committed, pushed, merged or deployed**.
Frontend repository `smart-solar` (`main`, which already carried 43 uncommitted files from other work; none of them was reverted).

## 1. Starting point (audit)

The repository was much further along than the request assumed. Inspected code and the earlier guides
(`simplified-commercial-flow.md`, `customer-to-installation-flow.md`, `after-sales-services.md`, `rate-limiting.md`) established:

| Requirement | Before (verified in code) | Classification |
|---|---|---|
| Package images | `ServicePackage.ImageUrl`, `PACKAGE_IMAGE` file purpose (Admin, jpeg/png/webp, 10 MB, verified), `imageFileId`/`imageUrl` on create/update, `imageUrl` in admin and customer lists | IMPLEMENTED (tests incomplete) |
| Technician quotation | Surveying technician's catalog cart; backend prices, snapshot at issue, revisions, customer accept/reject/revision; Sales routes retired (410) | IMPLEMENTED |
| Customer reads finalized quotation | `GET /api/customers/me/quotations[/{id}]`, drafts hidden, other customers 404 | IMPLEMENTED |
| Packages bought with the installation | `POST …/after-sales/purchases` with `includeContract`: one PayOS checkout, one `payment_attempt_item` per line, price snapshot | IMPLEMENTED for PayOS; **MISSING for cash** |
| Physical contract | Official PDF → customer uploads the scan → Manager/Admin review → SIGNED; submissions kept | PARTIAL: **Sales could not upload the paper scan** |
| Cash / PayOS for the contract | Cash choice + Manager/Admin confirmation with evidence; PayOS link, signed webhook, reconciliation, duplicate → REFUND_REQUIRED | IMPLEMENTED |
| Installation scheduling | Technician records the agreed date (PLANNED), confirmed on payment, conflicts → RESCHEDULE_REQUIRED | PARTIAL: **customer could not choose a date** |
| After-sales coverage, quotation, payment, completion | Coverage per item (warranty / package / disaster / chargeable), Sales-priced quotation, PayOS only, acceptance/rework | PARTIAL: no cash; quotation priced by Sales (see §5) |
| Pagination | 5 endpoint families paged; many growing lists unpaged, loaded whole and sorted in memory; notifications silently capped at 100 | PARTIAL |
| Rate limiting | Redis-backed distributed limiting, enveloped 429 + Retry-After, PayOS webhook IP guard | PARTIAL: **31 controller actions without any policy** (runtime routing-table inventory) |
| Admin dashboard | Real SQL metrics, period comparison, zone/type/status filters | BROKEN in one point: duplicate / wrong-amount money counted as contract or package revenue |

## 2. What changed

### 2.1 Rate limiting — every controller action now has a policy
* New policies `portal-read` (60/min/user), `portal-write` (20/min/user), `retired-endpoint` (30/min/user), configurable in `appsettings.json`.
* Applied to the 31 uncovered actions: customer profile/sites/pre-surveys, pre-survey create/update/submit/surface, survey-request lists and detail,
  simulation reads, the Sales queue (claim uses the stricter `scheduling-write`), and the 14 retired 410 routes.
* `EndpointRateLimitCoverageTests` enumerates the real routing table and fails if any action lacks a policy (regression guard).

### 2.2 Paper contract uploaded by Sales
* `POST /api/contracts/{id}/signed-document` (Sales owner of the request, Manager, Admin): VERIFIED `SIGNED_CONTRACT` PDF registered by the caller, the
  SHA-256 of the official PDF that was printed, optional `signedOn` and `note`. Same transition as the customer path: AWAITING_SIGNATURE /
  SIGNATURE_REJECTED → UNDER_REVIEW; a Manager/Admin still confirms (SIGNED). An upload never signs a contract by itself; a DRAFT cannot receive a scan.
* `SIGNED_CONTRACT` uploads now allowed for Customer, Sales, Manager, Admin (attachment still checks request ownership and state).
* History: every submission row keeps `submitted_by_user_id`; the contract event records the staff upload, signing date and note. No schema change.

### 2.3 Customer chooses the installation date
* New table `installation_slot_offer` (migration `20261117171006_InstallationSlotOffers`, additive).
* Technician: `GET …/installation-availability?from&to&durationDays` (working-day intervals checked with the same eligibility as a plan), `POST/GET
  …/installation-slot-offers` (1–5 intervals; replaces open offers; customer notified `INSTALLATION_SLOTS_OFFERED`).
* Customer: `GET /api/customers/me/contracts/{id}/installation-slot-offers` (open offers re-checked live, generic message when unavailable) and
  `POST /api/customers/me/installation-slot-offers/{id}/select`.
* Selection re-validates under the contract lock **and the technician lock**, refuses a technician interval already planned for another customer,
  and records the normal PLANNED schedule (agreement note = portal choice, `created_by` = customer). Payment confirmation, RESCHEDULE_REQUIRED and the
  start prerequisites are unchanged. `ProposeAsync` was refactored to share the locked write (no behaviour change; all previous schedule tests pass).

### 2.4 Cash for the combined checkout (installation + packages)
* `PurchaseRequest.paymentMethod`: `ONLINE` (default) or `CASH`; CASH only with `includeContract: true`.
* One CASH attempt with one line per item (contract total unchanged), no PayOS call, holds the contract's single payable slot (never together with an
  online link or another cash choice).
* `POST /api/contracts/{id}/payments/cash/confirm` now accepts the attempt's exact total (contract alone, or contract + packages) and settles every
  package line through the existing after-sales settlement in the same transaction (packages PAID; terms start at handover as before).
* Settlement: the online checkout deadline does not apply to a cash line (a pending cash choice has no deadline); the sweep already skips orders whose
  cash attempt is pending; a withdrawn cash choice releases the orders (CHECKOUT_EXPIRED) at the next sweep.

### 2.5 Opt-in server-side pagination
`page` / `pageSize` on: `GET /api/installation-projects`, `GET /api/manager/installation-schedules`, `GET /api/manager/after-sales/requests`,
`GET /api/customers/me/after-sales/requests`, `GET /api/customers/me/after-sales/notifications`. Without parameters the historical array is returned
(no FE break); with them, the repository's `PagedResult` computed in SQL (`ORDER BY created_at DESC, id DESC`, COUNT of the same filter). Limits
1–10 000 / 1–100, 400 `VALIDATION_FAILED` otherwise. Paged notifications are no longer capped at 100.

### 2.6 Dashboard money
Category revenue (`collected_contracts|packages|services`) now counts only money applied to its item; money received but not applied
(REFUND_REQUIRED / REVIEW_REQUIRED attempts, REVIEW_REQUIRED lines) is the new `collected_unapplied`. `collected_total` (gross) and `net_collected`
keep their values. Before the fix a contract paid in cash and then paid again online was counted twice in `collected_contracts`.

### 2.7 Package images
No code change was needed; PKG-IMG-001…008 are now explicit automated tests (see TEST_CASE_TRACEABILITY.md).

## 3. Files changed (backend)
API: `Program.cs`, `appsettings.json`, `Extensions/PortalRateLimitingExtensions.cs` (new), controllers `Customers`, `CustomerScheduling`, `PreSurveys`,
`PreSurveySurface`, `Simulations`, `SurveyRequests`, `Quotation` (410 routes), `Contract`, `InstallationSchedule`, `Installation`, `AfterSales`;
contracts `ContractRequests.cs`, `InstallationRequests.cs`, `AfterSalesRequests.cs`.
Modules: `Common/Paging/PageRequest.cs` (new), `Common/Notifications/CustomerNotifications.cs`, `CustomerContracts/ContractHandlers.cs`,
`Files/FilePolicies.cs`, `Installations/InstallationScheduleEntities.cs`, `InstallationScheduleHandlers.cs`, `InstallationSlotOfferHandlers.cs` (new),
`InstallationContracts.cs`, `InstallationHandlers.cs`, `Payments/PaymentService.cs`, `PaymentEntities.cs`, `AfterSales/ServicePackageHandlers.cs`,
`AfterSalesPaymentSettlement.cs`, `ServiceManagerHandlers.cs`, `ServiceCustomerHandlers.cs`, `AfterSalesContracts.cs`, `AfterSalesDashboard.cs`.
Infrastructure: `AppDbContext.cs`, `InstallationConfigurations.cs`, `InstallationUnitOfWork.cs`, `AfterSalesUnitOfWork.cs`, `AfterSalesReportQueries.cs`,
migration `20261117171006_InstallationSlotOffers` (+ Designer, snapshot).
Tests: 13 new test files, 3 support edits (see TEST_EXECUTION_REPORT.md).

Frontend (new files): `src/types/res/installationScheduleRes.ts`, `src/types/req/installationScheduleReq.ts`, `src/services/installationScheduleService.ts`,
`src/features/installations/slotOfferRules.ts`, `tests/installation-slot-offer-rules.test.mjs`. Edited (not in the pre-existing modified set):
`src/services/api/endpoints.ts`, `src/types/req/customerAfterSalesReq.ts`.

## 4. Second pass (backend only, 2026-11-18)

Scope: backend only. **No frontend file was modified in this pass** (the FE API-layer files added in the first pass are left untouched, see §7).

### 4.1 Technician-priced after-sales quotations (was BLOCKED → implemented)
* `POST /api/technicians/me/after-sales/requests/{requestId}/quotations` — the technician of a (non-declined, non-cancelled) visit of a **FAULT**
  request prices every open chargeable / disaster-protected item with catalog products and quantities only. Amount = quantity × catalog unit price
  (whole VND); `kind` LABOR for service product types (`INSTALLATION_LABOR`, `TRANSPORT`, `OTHER_SERVICE`), PARTS otherwise. Inactive, non-VND,
  unpriced or unknown products refuse the whole quotation (`SERVICE_QUOTATION_PRICE_MISSING` / `…_PRODUCT_UNAVAILABLE`, nothing written).
* Everything after pricing is the existing issue step (shared code): one open version (unique index), supersede with history, disaster support
  computed/reserved, items QUOTED, request AWAITING_CUSTOMER, customer notified, repair warranty. The catalog snapshot of every component is stored in
  the quotation lines (history survives catalog changes). `issuedByRole` = TECHNICIAN (new column, migration `20261117185657_AfterSalesTechnicianQuotation`).
* Coverage authority unchanged (Manager decides per item; free items need no quotation; the DB forbids a zero-total quotation).
* Sales: the Sales path stays available as a fallback (unchanged rules); the open quotation task is **CANCELLED** when the technician issues
  (a DONE task without an owner is refused by `ck_sales_task_owner` — found by the tests and fixed).
* Not covered by the technician path: **one-time maintenance** (no technician is assigned before it is paid; Sales keeps pricing it).
* **Configuration required:** repairs can only be priced once an Admin has created priced catalog products for labor (e.g. `OTHER_SERVICE`, unit JOB/HOUR)
  and the replacement parts; otherwise the technician gets `SERVICE_QUOTATION_PRICE_MISSING`. No price is ever invented.

### 4.2 After-sales cash payment (was not implemented → implemented)
* Customer: `POST /api/customers/me/after-sales/quotations/{id}/cash` (ACCEPTED, not expired; idempotent) and `…/cash/cancel`. A pending cash
  choice is a CASH payment attempt — not a payment. Online checkout is refused while cash is chosen (`PAYMENT_CASH_SELECTED`) and vice versa.
* Staff: `POST /api/manager/payments/{attemptId}/cash/confirm` (Manager/Admin): exact amount, unique receipt, VERIFIED evidence of the caller; the
  quotation must still be payable (ACCEPTED, not expired, waiting for this attempt) — checked again inside the transaction, otherwise
  `PAYMENT_CASH_ITEM_NOT_PAYABLE` and nothing is recorded. Success settles the line once through the existing settlement (same as PayOS): quotation
  PAID, items AUTHORIZED, request READY_FOR_WORK. `GET /api/manager/payments` (paged desk) lists pending cash choices.
* **Expiry conflict (resolved by applying the existing rule, not by a new policy):** `AfterSalesSweepService.ExpireQuotationsAsync` skipped every
  expired quotation whose attempt was still payable — correct for PayOS (money may be in flight), but a pending cash choice is not money in flight, so it
  would have extended the quotation and kept disaster support reserved indefinitely. Now the sweep withdraws a pending CASH attempt at expiry and expires
  the quotation exactly as before (support released). Confirmation after expiry is refused. Operational consequence (for the business to know): cash
  must be confirmed before the quotation's expiry; otherwise a new quotation is issued.
* Cancellation of a cash attempt (customer, rejection, sweep) never calls PayOS (previously `CancelAttemptAsync` would have asked PayOS to cancel an
  internal order code).
* Package purchases: the first-pass restriction "cash only together with the installation" is **removed** (it was not an approved rule). CASH works
  for packages of a PAID contract alone; a pending cash order blocks a second order of the same kind (`PAYMENT_CASH_SELECTED`).

### 4.3 Pagination (5 → 12 endpoint families, all opt-in, SQL paged)
Added: `GET /api/survey-requests/pending`, `/my` (Sales), `GET /api/sales/after-sales/tasks/open`, `/mine`, `GET /api/manager/after-sales/tasks`,
`GET /api/technicians/me/after-sales/visits`, `GET /api/admin/knowledge-documents`; new paged-only `GET /api/manager/payments`.
Inventory and exemptions: API_FE_INTEGRATION_HANDOFF.md §8.

### 4.4 Admin dashboard overview
`GET /api/admin/after-sales/dashboard/overview?from&to` — accounts per role, survey requests and outstanding appointments, installation quotations,
contracts with PDF / signed-scan status (incl. scans uploaded by staff), installation projects / schedules / open slot offers, after-sales requests,
quotations (by status and issuer), disaster claims, subscriptions, open Sales tasks and visits, pending cash, refund records, and for the period:
attempts by method×status, collected by method, combined checkouts, package revenue with vs without installation, quotation values offered.

### 4.5 S3 expired-link test
Root cause assessment: the test compared two clocks (host sleep vs the storage server's expiry check). Drift measured now: ≈ 15 ms, so the failing
run cannot be reproduced; most likely VM clock drift during the long run (not proven). Fix: the test reads the signed `X-Amz-Date`/`X-Amz-Expires`, asserts the
1 s lifetime was signed, and waits (bounded) for the **server's** clock to pass it before asserting 403 — the security assertion is unchanged. The production
signer was verified (`Expires = now + ttl` from `TimeProvider`).

## 5. Decisions recorded for confirmation
* Sales uploads the paper scan (first pass, requested); Manager/Admin confirm.
* A customer-chosen slot is refused if the technician already has another live plan overlapping it (first pass).
* Technician quotations cover FAULT requests; one-time maintenance stays Sales-priced (no technician before payment).
* A pending cash choice does not survive quotation expiry (§4.2).

## 6. Still open (genuine)
* **FE screens**: out of scope (backend only); the handoff documents every contract.
* **Supabase migrations**: not applied (live database used by an older API).
* **Unapplied money / refunds**: settling them remains a pending business decision (unchanged).
* **Manufacturer-warranty coverage**: still blocked by the existing undecided start rule (unchanged, pre-existing).
* **Remaining unpaged lists** are bounded per user or per object (see handoff §8); not changed.
* **NFR performance**: not measured in this change.

## 7. Frontend files touched in the first pass (left as they are)
`src/services/api/endpoints.ts`, `src/types/req/customerAfterSalesReq.ts` (edited); `src/types/res/installationScheduleRes.ts`,
`src/types/req/installationScheduleReq.ts`, `src/services/installationScheduleService.ts`, `src/features/installations/slotOfferRules.ts`,
`tests/installation-slot-offer-rules.test.mjs` (new). The `cashPurchaseAllowed` rule there encodes the removed restriction; the FE team should drop it.

## 8. Final acceptance pass (backend only, 2026-11-18, pass 3)

### 8.1 One-time maintenance priced by the technician (decision §5 bullet 3 superseded)
The pass-3 prompt asked for technician pricing of one-time maintenance. It now reuses the fault-repair process end to end:

| Step | Behavior |
|---|---|
| Request | `inspectionRequired` (default `true`): `SUBMITTED`, Managers notified, **no** Sales task. `false`: unchanged Sales-priced path |
| Inspection | Manager plans an `INSPECTION` visit (new `(ONE_TIME_MAINTENANCE, SUBMITTED)` purpose); technician records items |
| Quotation | the assigned technician prices from the active catalog (`IssueByTechnicianAsync` now accepts inspected one-time maintenance; package `MAINTENANCE` and uninspected requests are refused) |
| Payment | existing quotation cash / PayOS paths, idempotent |
| Work | `MAINTENANCE` visit; supplementary items allowed on that visit; work report → `PENDING_CUSTOMER_ACCEPTANCE` → `COMPLETED` / `MAINTENANCE_DONE` |
| Expiry | sweep: inspected request back to item-driven status + Sales `REPAIR_QUOTATION` follow-up; uninspected → Sales `MAINTENANCE_QUOTATION` |

Files: `ServiceCustomerHandlers.cs`, `ServiceManagerHandlers.cs`, `ServiceQuotationHandlers.cs`, `ServiceQuotationTechnicianPricing.cs`,
`AfterSalesSweepService.cs`, `TechnicianServiceHandlers.cs`, `AfterSalesRequests.cs`, `AfterSalesControllers.cs`. Tests MAINT-001..015
(TEST_CASE_TRACEABILITY.md).

### 8.2 After-sales pricing configuration
No new table: labor / service and parts are ordinary catalog products (`OTHER_SERVICE`, `CONNECTOR`, …) managed with the existing Admin catalog API.
Verified through the real API (`AfterSalesPricingConfigurationTests`). **No company price was entered or invented.** Only `TEST-` products exist, in
test databases. Before go-live the business must enter real labor and part prices. Until then a product priced 0 is refused with
`SERVICE_QUOTATION_PRICE_MISSING`.

### 8.3 Defects found and fixed in pass 3

| Defect | Found by | Fix |
|---|---|---|
| Concurrent requests during the first schema check got **503 `SCHEMA_NOT_READY`** (the check time was stamped before the probe answered) | NFR rate-limit burst (7×200, 93×503) | `SchemaReadinessMiddleware.IsReadyAsync` stamps the time after the probe; regression test with a slow probe |
| A lost PayOS webhook could take **about 3 minutes** to confirm (60 s tick, 2 min re-check age, 25 per tick) against the ≤ 2 min target | NFR PayOS analysis | tick 30 s, re-check age 60 s, batches drained per tick (`PaymentService.ReconcileDueAsync`); worst case 89 s |

Both were real defects in committed code. Neither was hidden by a test change.

### 8.4 Payments, refunds, manufacturer warranty (verified, no policy invented)
* **Refunds / unapplied money**: the team decision of 2026-11-13 ("refunds are not handled in the application") stands. Added only a **read-only**
  paged ledger (`GET /api/manager/after-sales/unapplied-money`) that reconciles with the dashboard. No refund is executed automatically.
  **Decision needed** to go further: who approves a refund, and through which channel it is paid back (PayOS refund or bank transfer).
* **Manufacturer warranty**: still refused with `AFTER_SALES_MANUFACTURER_WARRANTY_POLICY_PENDING` (test
  `Manufacturer_warranty_is_a_separate_category_that_cannot_be_decided_before_its_rules_are_approved`). **Minimal decision needed**: (1) the start date
  of manufacturer warranty (installation handover, or the manufacturer's registration / production date), and (2) who files the claim with the
  manufacturer and who pays parts and labor while it is pending.

### 8.5 Workflow verification (automated evidence)

| Flow | Evidence | Result |
|---|---|---|
| 1 Installation (pre-survey → quotation → contract → payment → schedule → installation → handover) | `InstallationLifecycleE2ETests` E2E_01..10, `CommercialRemediationE2ETests` E2E_R01..R04, R07 | PASS |
| 2 Warranty / repair (fault → inspection → coverage → quotation → payment → repair → acceptance) | `AfterSalesRemediationE2ETests` E2E_AS_01..05 | PASS |
| 3 Maintenance (package visits, one-time maintenance) | `ServicePackageTests`, `OneTimeMaintenanceTests` MAINT_001..014, `E2E_MAINT_015` | PASS |
| 4 Disaster protection (waiting period, reconsideration, capped support reserved / released / consumed) | `Disaster_claims_respect_the_waiting_period_and_one_reconsideration`, `Disaster_support_is_capped_…`, `Parallel_quotations_never_reserve_more_disaster_support_…`, `AFTER_QUOTE_004_…` | PASS |
| 5 Administration (catalog, users, dashboard, rate limits, pagination) | `AfterSalesPricingConfigurationTests`, `AdminOverviewTests`, `DashboardReconciliationTests`, `EndpointRateLimitCoverageTests`, paging tests | PASS |

### 8.6 Still open after pass 3
* Supabase migrations: not applied (preflight script ready, MIGRATION_VERIFICATION.md §7).
* Refund execution and manufacturer warranty: business decisions above.
* Real labor / part prices: must be entered by the business.
* RAG latency NFR: not measured (service and models not in this environment; NFR_VERIFICATION_REPORT.md).
* FE: the handoff (§12–15) must be implemented by the FE team, including dropping `cashPurchaseAllowed`.
