# Admin dashboard metrics (2026-11-18)

Endpoint `GET /api/admin/after-sales/dashboard` (Admin only) and `…/dashboard/export` (CSV of the same rows). Source:
`AfterSalesDashboard` + `AfterSalesReportQueries`. Period = local dates `[from, to]` (scheduling time zone) converted to UTC `[fromUtc, toUtc)`;
default: this month vs the previous month. Filters `operationalZoneId`, `serviceType`, `status` run in SQL. Currency: VND, whole numbers.

## Money (changed 2026-11-18)

Received money = payment attempts in `PAID`, `REFUND_REQUIRED` or `REVIEW_REQUIRED` with `amount_paid` set, dated by
`provider_transaction_at ?? paid_at ?? updated_at` (PayOS: provider time; CASH: confirmation time). Never counted: PENDING/PROCESSING attempts,
a cash choice nobody confirmed, CANCELLED/EXPIRED/FAILED links, accepted quotations, issued contracts.

| Key | Definition | Formula | Double-count rule |
|---|---|---|---|
| `collected_contracts` | Installation contract money applied | Σ amount of SETTLED `CONTRACT` lines of PAID attempts + Σ `amount_paid` of PAID attempts without lines (contract-only, online or cash) | a combined checkout contributes its CONTRACT line only |
| `collected_packages` | Package money applied | Σ amount of SETTLED `MAINTENANCE_PACKAGE` / `DISASTER_PACKAGE` lines of PAID attempts | split by lines, never the checkout total |
| `collected_services` | Repair / one-time maintenance money applied | Σ amount of SETTLED `REPAIR_QUOTATION` / `MAINTENANCE_QUOTATION` lines of PAID attempts | same |
| `collected_unapplied` | Money received but applied to nothing (duplicate payment of a paid contract, wrong amount, contract not payable, package/quotation paid after its deadline) | Σ `amount_paid` of REFUND_REQUIRED / REVIEW_REQUIRED attempts + (`amount_paid` − settled lines) of PAID attempts | never revenue; a duplicate webhook delivery is deduplicated before it can be counted |
| `collected_total` | Gross money received | contracts + packages + services + unapplied = Σ `amount_paid` of received attempts | – |
| `refunded` | Refunds actually returned | Σ `approved_amount` of `refund_record` with status REFUNDED and `refunded_at` in the period | refunds are not processed by the application today (business decision 2026-11-13): stays 0 for new data |
| `net_collected` | Gross minus refunded | `collected_total − refunded` | – |

Zone filter: an attempt belongs to the zone when its contract's project is in the zone, or any of its lines (package → contract → project; quotation →
request → project) is.

**Change**: before 2026-11-18 the three category metrics included REFUND_REQUIRED / REVIEW_REQUIRED money, so a contract paid in cash and paid again
online appeared twice in `collected_contracts`. `collected_total` and `net_collected` values are unchanged by the fix (the money moved to
`collected_unapplied`).

## Counts (unchanged definitions)

`total_customers` (non-deleted CUSTOMER accounts created before the end of the period), `new_customers`, `installation_projects` (created in period),
`completed_installations` (`handover_at` in period), `active_installation_warranties` (handover + contract warranty months covers the end of the period),
`fault_requests`, `completed_repairs`, `completed_maintenance_visits` (`completed_at` in period, status COMPLETED), `active_maintenance_subscriptions`,
`active_disaster_subscriptions` (term covers the end of the period, not cancelled), `package_purchases` (`paid_at` in period), `active_technicians`
(distinct technicians with a non-declined/cancelled/replaced assignment starting in the period), `technician_service_visits` (visits REPORTED in period).
`changePercent` is null when the comparison value is 0.

## Verified totals (automated)

`DashboardReconciliationTests.DASHBOARD_001`: contract A + 2 packages paid in one cash checkout, contract B paid in cash then paid again online
(webhook delivered twice), contract C cash chosen but not confirmed →
`collected_contracts = A + B`, `collected_packages = 3 500 000`, `collected_services = 0`, `collected_unapplied = B`,
`collected_total = A + B + 3 500 000 + B` = Σ `amount_paid` of every received attempt (reconciled with the detail rows), `package_purchases = 2`.
`DASHBOARD_002`: zero data (every metric 0, no change %), date boundaries (money confirmed today is in `[today, today]`, not in the previous 5 days or
the next 3). Existing: `AfterSalesOperationsTests` (period comparison, category split, CSV, zone filter).

## Operational overview (second pass) — `GET /api/admin/after-sales/dashboard/overview`

Source: `AdminOverview` + `AdminOverviewQueries` (Admin only). Period `[from, to]` = local dates (scheduling time zone) → UTC `[fromUtc, toUtc)`; default
this month; max 366 days. Snapshot = state at the time of the request. Every distribution is a SQL `GROUP BY` / `COUNT`; amounts are VND.

| Field | Kind | Source | Definition / statuses | Double-count rule |
|---|---|---|---|---|
| `accounts[]` | snapshot | `user_role` ⋈ `user_account` ⋈ `role` | non-deleted accounts per role: total, active (ACTIVE), pendingVerification, inactive (SUSPENDED + DISABLED) | an account with two roles counts once per role |
| `surveyRequestsByStatus` | snapshot | `survey_request` | count per status | – |
| `outstandingSurveyAppointments` | snapshot | `technician_appointment` | PENDING_ACCEPTANCE + ESCALATED | – |
| `installationQuotationsByStatus` | snapshot | `quotation` | count per status (DRAFT, ISSUED, ACCEPTED, …) | – |
| `contractsByStatus` | snapshot | `customer_contract` | count per status (every version row) | amendments are their own rows |
| `contractsWithOfficialPdf` / `contractsWithSignedScan` | snapshot | `customer_contract` | official PDF attached / a signed scan submitted | – |
| `signedScansUploadedByStaff` | snapshot | `contract_signature_submission` | submissions whose submitter is not the contract's customer (paper scans uploaded by Sales/Manager/Admin) | – |
| `installationProjectsByStatus`, `installationSchedulesByStatus`, `openInstallationSlotOffers` | snapshot | `installation_project`, `installation_schedule_proposal`, `installation_slot_offer` (OPEN) | counts | – |
| `serviceRequestsByKindAndStatus` | snapshot | `service_request` | key `KIND:STATUS` (FAULT = warranty / repair incidents, MAINTENANCE, ONE_TIME_MAINTENANCE) | – |
| `serviceQuotationsByStatus`, `serviceQuotationsByIssuer` | snapshot | `service_quotation` | per status; per `issued_by_role` (SALES / TECHNICIAN) | versions are rows (superseded included) |
| `disasterClaimsByStatus`, `subscriptionsByKindAndStatus` | snapshot | `disaster_claim`, `service_subscription` | counts (`KIND:STATUS`) | – |
| `openSalesTasksByType`, `openServiceVisitsByStatus` | snapshot | `sales_task` (OPEN/CLAIMED/IN_PROGRESS), `service_visit` (SCHEDULED/IN_PROGRESS) | operational workload | – |
| `pendingCashChoices` | snapshot | `payment_attempt` CASH + PENDING | count and Σ `amount` — **not collected** | – |
| `refundRecordsByStatus` | snapshot | `refund_record` | count and Σ `original_amount` per status (unapplied money ledger; refunds are not processed by the application) | – |
| `paymentAttemptsCreatedByMethodAndStatus` | period (created_at) | `payment_attempt` | key `ONLINE|CASH:STATUS`, count and Σ `amount` (amount due, not money received) | – |
| `collectedByMethod` | period (provider_transaction_at ?? paid_at) | `payment_attempt` PAID with `amount_paid` | count and Σ `amount_paid` per method — verified money only | a combined checkout is one attempt; duplicate / wrong-amount money (REFUND_REQUIRED / REVIEW_REQUIRED) excluded (see `collected_unapplied` above) |
| `combinedCheckoutsPaid` | period | PAID attempts with a SETTLED CONTRACT line and ≥ 1 SETTLED package line | count and Σ settled line amounts | counted once per attempt |
| `packageRevenueBySaleChannel` | period | SETTLED package lines of PAID attempts | `WITH_INSTALLATION` (attempt has a contract line) / `SEPARATE`: count of lines and Σ amount | each line once |
| `installationQuotationValueIssued` | period (issued_at) | `quotation` | count and Σ `total_amount` — **offers, never revenue** | every issued version counts (a revision is a new offer) |
| `serviceQuotationValueIssued` | period (issued_at) | `service_quotation` | count and Σ `customer_amount` — offers | same |

Verified by `AdminOverviewTests` (known dataset: cash bundle, cash contract + duplicate online payment, pending cash, technician quotation; zero data;
invalid period) and `PostgresAfterSalesRemediationTests.DASHBOARD_…` (PostgreSQL translation, a confirmed after-sales cash payment).

**Unsupported (no data in the system):** refunds actually paid back (the application does not process refunds; `refunded` stays 0 for new data),
technician utilisation hours, customer satisfaction. AI/RAG usage and cost: existing `GET /api/admin/rag-usage/*`.

## Not covered

First pass gap (status distributions, cash vs PayOS, workforce) is closed by the overview above.

## Final acceptance pass (pass 3)

* No metric definition changed in pass 3.
* **Unapplied-money ledger** (`GET /api/manager/after-sales/unapplied-money`, read only) is the detail behind `collected_unapplied`.
  `DASHBOARD_001` now also asserts that the ledger lists the late duplicate payment **once** (status `PENDING_APPROVAL`, the PayOS attempt id), that
  Σ ledger amounts = `collected_unapplied`, and that the `REFUNDED` filter is empty (`net_collected` = `collected_total`: nothing refunded).
* The overview counts quotations by issuer and status, with no filter on request kind, so technician-issued one-time maintenance quotations
  fall under `TECHNICIAN` just as fault quotations do. `DASHBOARD_OVERVIEW_002` verifies this with fault quotations only. Accepted quotations are still never counted as revenue; only settled payment lines are.
* Numerical tests: `DASHBOARD_001`, `DASHBOARD_002`, `DASHBOARD_OVERVIEW_001..003`, and the PostgreSQL translation test
  `DASHBOARD_The_overview_translates_on_PostgreSQL_…`, all in the final regression (TEST_EXECUTION_REPORT.md).
