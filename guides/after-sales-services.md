# Flow 3 — After-Sales Services

Warranty and repairs, maintenance packages, disaster protection, the shared Sales queue, manual refunds and the Admin dashboard.
Module: `src/SmartSolar.Modules/AfterSales/` (one handler class per actor). Persistence: `src/SmartSolar.Infrastructure/Persistence/AfterSales/`.
API: `src/SmartSolar.Api/Controllers/AfterSalesControllers.cs`. Migration: `20261021023128_AfterSalesServices` (**generated, not applied**).

Flow 3 builds on the Installation Flow, so it is enabled with the same flag: while `Features__InstallationWorkflowEnabled=false`
every endpoint below returns **503 `FEATURE_PENDING_MIGRATION`**. Envelope, revision handling and the error → HTTP mapping are the
same as in [installation-workflow-api-integration.md](installation-workflow-api-integration.md) (`*_NOT_FOUND` 404, validation and
`AFTER_SALES_REASON_REQUIRED` 400, `FORBIDDEN` 403, other state conflicts 409). Timings are Admin settings: see
[system-settings.md](system-settings.md) (`afterSales.*`).

## Where to start reading

| File | What it holds |
|---|---|
| `AfterSalesEntities.cs` | Tables as classes, and every status / error code as constants |
| `AfterSalesRules.cs` | Pure calculations: calendar-month periods, disaster support caps, eligibility (unit tested in `AfterSalesRulesTests`) |
| `AfterSalesWorkflow.cs` | Shared steps used by every handler: request status from its items, reserve / release / consume support, close quotations |
| `ServiceCustomerHandlers.cs`, `ServiceManagerHandlers.cs`, `TechnicianServiceHandlers.cs`, `ServiceQuotationHandlers.cs`, `ServicePackageHandlers.cs`, `SalesTaskHandlers.cs` | One class per actor |
| `AfterSalesPaymentSettlement.cs` | Applies verified PayOS lines inside the payment confirmation transaction |
| `AfterSalesSweepService.cs` | The background sweep (`AfterSalesWorker`, every `AfterSales:PollSeconds`, default 300; switch off with `AfterSales:WorkerEnabled=false`) |
| `tests/SmartSolar.Tests/TestSupport/AfterSalesTestContext.cs` | Test fixture: a project handed over through the real Flow 1–2 handlers, plus helpers for each common step |

## Business rules

### Warranty
* The installation warranty runs from `handover_at` for `customer_contract.warranty_months`. Sales chooses the months from the
  Admin's active `INSTALLATION` warranty options (`GET /api/sales/after-sales/warranty-options`) when creating the contract or
  editing the draft (`warrantyMonths`; omitted = the default option). The value is part of the contract snapshot; later Admin
  changes never touch it.
* **A draft cannot be issued without warranty months** (`CONTRACT_WARRANTY_REQUIRED`, 400). While no options are configured, choosing
  months answers `CONTRACT_WARRANTY_VALIDATION_FAILED` ("an Admin must add them first"). Contracts issued before this rule keep
  their state; signing and payment do not check months.
* **Legacy contracts** (issued before warranty options existed) have no months. The customer's warranty view answers
  `warrantyStatus: NOT_RECORDED` (other values: `NOT_HANDED_OVER`, `ACTIVE`, `EXPIRED`); faults can still be reported, and a Manager
  cannot choose `INSTALLATION_WARRANTY` for them (clear message), so the item is handled as chargeable. No duration is ever invented.
  If the business wants to record the duration printed on a signed legacy contract, use the reviewed manual template
  [sql/after-sales-legacy-warranty-backfill.proposal.sql](sql/after-sales-legacy-warranty-backfill.proposal.sql) (per contract,
  audited, never automatic). A PAID contract cannot be amended, so the template is the only route for paid legacy customers.
* Eligibility is frozen when the customer submits the fault (`within_installation_warranty`, `within_repair_warranty`).
* A paid repair has its own warranty (`REPAIR` options, months on the quotation). It starts at the technician's completion report;
  a corrective rework report restarts it.

### Fault requests
1. The customer submits one request per problem (description required, asset optional). Other open requests of the installation are
   returned as `possibleDuplicates`, a warning only; a Manager may close a duplicate (`CLOSED_DUPLICATE`, linked to the original).
2. A Manager schedules a free **inspection** (`INSPECTION`, default 60 minutes). The technician must accept it, start it on site,
   add at least one photo, then report diagnosed **items** or **no fault found** (nothing charged; the customer confirms).
3. The Manager decides coverage **per item**: `INSTALLATION_WARRANTY` or `REPAIR_WARRANTY` (free, authorized at once),
   `DISASTER_PROTECTION` (claim approved first) or `CHARGEABLE`. Warranty coverage comes before disaster support.
   **Same-visit repair:** with `stayOnSite: true` on the inspection report, the visit (and the technician interval already reserved
   for it) stays open while the decision is made. The technician continues the same visit as the repair
   (`POST …/visits/{id}/continue-repair` with `estimatedMinutes`, `partsAvailable`) only when the request is `READY_FOR_WORK`
   (Manager decided every item; chargeable quotations accepted and PayOS-verified), the parts are available and
   `now + estimatedMinutes` fits before the visit's reserved end. Otherwise (`AFTER_SALES_PAYMENT_REQUIRED`,
   `AFTER_SALES_PARTS_UNAVAILABLE`, `AFTER_SALES_REPAIR_TIME_INSUFFICIENT`) the technician ends the wait (`POST …/finish`) and the
   Manager schedules a separate repair visit as usual; the sweep ends a wait automatically when the reserved interval is over. No new
   interval is booked for a same-visit repair, so the scheduling protections are unchanged; a Manager cannot book another visit while
   the technician waits.
4. Chargeable / disaster items open a `REPAIR_QUOTATION` task in the shared Sales queue. The Sales user who **claims** it issues the
   quotation; every open chargeable item must be priced exactly once. A new version supersedes the open one (old versions are kept
   unchanged). Quotations are valid `afterSales.quotationValidityDays` (7). Items found during the repair get a supplementary quotation.
5. The customer accepts or rejects the **whole** quotation. Accepted → PayOS checkout (`afterSales.checkoutExpiryMinutes`, never
   past the quotation expiry). **Work never starts before payment**: a repair visit needs an `AUTHORIZED` item
   (`AFTER_SALES_PAYMENT_REQUIRED`). Rejecting declines its items; if nothing else is left the request is `CANCELLED`.
6. After the work report the request is `PENDING_CUSTOMER_ACCEPTANCE`. Only the customer accepts (silence is never acceptance; the
   sweep sends reminders and opens an `ACCEPTANCE_FOLLOW_UP` task). A rejection needs a reason → `DISPUTED` → the Manager either
   requires rework (`REWORK_REQUIRED`, rework visit) or sends it back for acceptance.

Request statuses: `SUBMITTED → SCHEDULED → IN_PROGRESS → AWAITING_DECISION → AWAITING_QUOTATION → AWAITING_CUSTOMER → READY_FOR_WORK
→ SCHEDULED → IN_PROGRESS → PENDING_CUSTOMER_ACCEPTANCE → COMPLETED`, plus `DISPUTED`, `REWORK_REQUIRED`, `NOT_COMPLETED`,
`CANCELLED`, `CLOSED_DUPLICATE`. After a visit the next status is derived from the items (`AfterSalesWorkflow.NextStatus`).

### Cancellation and refunds
* Free work not started: the customer cancels at once (reason required). Paid work not started: a cancellation **request** that a
  Manager decides, keeping documented non-recoverable costs. Started work: only a Manager stop.
* Refunds are **manual**: a refund record is approved by a Manager and recorded by an Admin once the money was returned outside
  the system (the refund date drives the dashboard). Nothing calls PayOS for refunds.

### Assets
* A replacement is stored as `PENDING_ACCEPTANCE` with `replaces_asset_id` and `service_request_id`. On the customer's acceptance it
  becomes `ACTIVE` and the old asset `REPLACED` (kept as history).
* A Manager may correct equipment data with a reason; old and new values are written to `after_sales_event`.

### Packages
* Admin catalog: `MAINTENANCE` (visit plan months) and `DISASTER_PROTECTION` (covered types, percentage, per-incident and term
  limits). Price and benefits are **snapshotted** in `service_subscription` at purchase.
* A term starts when payment is verified **and** the installation is handed over (the later of the two). Terms use month
  anniversaries in local time. One term per kind at a time (database exclusion constraint `ex_service_subscription_no_overlap`).
  One prepaid renewal may wait; it starts exactly when the current term ends. There are no upgrades.
* Purchase can be combined with the contract payment in **one PayOS checkout** (`includeContract: true`). Each line is stored in
  `payment_attempt_item`, and the contract total never changes. The database allows each package or quotation once per checkout and
  settled by at most one payment. A checkout is one PayOS link: cancelling one unpaid package cancels the link, the other unpaid
  packages of it become `CHECKOUT_EXPIRED` (buy again), and the contract is simply paid again. While a contract-only payment link is
  live, a combined checkout for that contract answers `PAYMENT_CHECKOUT_IN_PROGRESS`. A line that cannot be applied (for example paid after its
  deadline, by provider transaction time) is marked `REVIEW_REQUIRED` and a `PENDING_APPROVAL` refund is created.
* Before activation a paid package can be cancelled by Manager decision (manual refund); an unpaid one at once. If the installation
  is cancelled before handover, its packages are cancelled by the sweep.

### Maintenance visits
* Each plan month is a `maintenance_entitlement` with its own period. The customer books at least
  `afterSales.maintenanceBookingLeadDays` (7) ahead, inside the period or later while the term runs.
* The sweep notifies the customer `afterSales.maintenanceNoticeLeadDays` (14) before a period opens. If there is no booking after
  `afterSales.customerResponseHours` (24 h), it opens a Sales `MAINTENANCE_FOLLOW_UP`. That follow-up closes as `UNREACHABLE`
  after `afterSales.followUpMaxDays` (7) and the visit becomes `NOT_COMPLETED`, which is not consumed and can still be booked.
* Change or cancel only until `afterSales.appointmentChangeLeadHours` (24 h) before the visit.
* A visit is **consumed only when the customer accepts** the result. Customer absence consumes nothing (the visit is bookable again).
* If SmartSolar must move a valid booking (`companyCaused` + reason), the visit may happen up to
  `afterSales.delayedMaintenanceExtensionDays` (30) after the term ends; not done by then → `EXPIRED_UNFULFILLED`.
* One-time paid maintenance: no package; Sales prices it, the customer pays, then a Manager schedules it.

### Disaster protection
* Coverage starts after `afterSales.disasterWaitingDays` (7) inside a new term; a continuous renewal has no waiting period.
* The claim is tied to the term that covered the **incident date** (also after that term expired). One reconsideration is allowed.
* Support = min(percentage × eligible cost, remaining per-incident limit, remaining term limit), whole VND rounded down. It is
  **reserved** when the quotation is issued, **consumed** on the customer's acceptance, and **released** on rejection, supersede,
  cancellation or expiry. An expired quotation is released only after PayOS confirms that its checkout was not paid.

### Scheduling visits
After-sales visits are rows of `installation_assignment` (`service_visit_id` set). They therefore share the technician exclusion
constraint, technician locks, buffer, leave and working hours with surveys and installations. A technician outside the site's zone
needs `crossZoneAuthorized` + reason. A time other than the customer's preferred one must be confirmed by the customer before the
visit starts. Pending survey holds in the interval are not moved: `SURVEY_HOLD_CONFLICT`.

### Sales queue
Tasks (`sales_task`) are claimed atomically: when several Sales users race, exactly one wins and the others get
`SALES_TASK_ALREADY_CLAIMED`. A Manager or Admin assigns, or reassigns with a reason. There are no SLA timers.

### Dashboard (Admin only)
`GET /api/admin/after-sales/dashboard` compares two local date ranges (default: this month vs the previous month). Filters:
zone, service type, request status; every filter runs in SQL (`AfterSalesReportQueries`), and the zone filter follows packages,
quotations and refunds to their installation. It reports customers, installations, active warranties, requests, repairs, maintenance,
subscriptions and technician activity. Money is split into contracts / packages / services / gross / refunded / net, using the
PayOS transaction time and the refund date. `…/dashboard/export` returns the same metrics as CSV.

## API

| Area (roles) | Base path | Endpoints |
|---|---|---|
| Customer | `api/customers/me/after-sales` | `GET installations/{projectId}/warranty` · `POST fault-requests` · `POST one-time-maintenance` · `GET requests`, `GET requests/{id}` · `POST requests/{id}/cancel`, `/completion`, `/reconsideration`, `/confirm-appointment`, `/reschedule`, `/report-fault` · `POST quotations/{id}/accept`, `/reject`, `/checkout` · `GET payments/{attemptId}` · `GET packages` · `POST purchases` · `GET subscriptions` · `POST subscriptions/{id}/cancel` · `POST maintenance/{entitlementId}/book` · `GET notifications` |
| Technician | `api/technicians/me/after-sales/visits` | `GET` · `GET {visitId}` · `POST {visitId}/respond`, `/start`, `/evidence`, `/inspection` (`stayOnSite`), `/continue-repair`, `/finish`, `/supplementary-items`, `/work-report`, `/absence` |
| Sales | `api/sales/after-sales` | `GET warranty-options` · `GET tasks/open`, `GET tasks/mine` · `POST tasks/{id}/claim`, `/start`, `/follow-up` · `GET requests/{id}` · `POST requests/{id}/quotations` |
| Manager, Admin | `api/manager/after-sales` | `GET requests`, `GET requests/{id}` · `POST requests/{id}/visits`, `/coverage`, `/claim-decision`, `/duplicate`, `/dispute-review`, `/cancellation-decision`, `/stop`, `/not-fulfilled` · `PUT assets/{id}` · `POST subscriptions/{id}/cancellation-decision` · `GET tasks` · `POST tasks/{id}/assign` · `GET refunds` · `POST refunds/{id}/decision` |
| Admin | `api/admin/after-sales` | `GET/POST packages`, `PUT packages/{id}` · `GET/PUT warranty-options` · `POST refunds/{id}/record` · `GET dashboard`, `GET dashboard/export` |

Request bodies are in `src/SmartSolar.Api/Contracts/AfterSales/AfterSalesRequests.cs`. Customer photos and technician evidence
use the file purpose `SERVICE_EVIDENCE` (prefix `after-sales/{userId}/`, jpeg/png/webp, 15 MB).
`ContractTermsRequest.warrantyMonths` (optional) selects the installation warranty when Sales creates or amends a contract.

## Database

Migration `20261021023128_AfterSalesServices` (**generated, not applied**; apply it only through the approved migration process):
* 15 new tables (`service_*`, `maintenance_entitlement`, `disaster_claim`, `sales_task`, `refund_record`, `warranty_option`,
  `payment_attempt_item`, `after_sales_event`, `after_sales_notification`) with checks, partial unique indexes and RESTRICT foreign keys.
* New nullable columns: `customer_contract.warranty_months`, `installation_assignment.service_visit_id`,
  `installed_asset.replaces_asset_id / service_request_id / specifications`, and `installed_asset.status` (default `ACTIVE`).
* `payment_attempt.contract_id` and `payment_event.contract_id` become nullable (service-only checkouts).
* Unique indexes on `payment_attempt_item`: one line per contract / package / quotation per checkout, and at most one SETTLED line
  per package / quotation.
* Contract payments are paid once on both paths (contract-only link and combined checkout): `payment_attempt.contract_id` is set on
  both, so the existing `ux_payment_attempt_one_payable` / `ux_payment_attempt_one_paid` indexes apply, and the manual key
  `fk_payment_attempt_item_attempt_contract` (`payment_attempt_item (payment_attempt_id, contract_id)` → `payment_attempt (id,
  contract_id)`, backed by the unique constraint `ux_payment_attempt_id_contract`) makes every CONTRACT line name its own checkout's
  contract. Legacy contract-only payments have no lines and are unaffected.
* RESTRICT foreign keys for the stored references `service_subscription.current_payment_attempt_id` and
  `service_quotation.current_payment_attempt_id` (→ `payment_attempt`) and `maintenance_entitlement.current_request_id`
  (→ `service_request`; that pair references each other, so `Down` drops this key first). The workflow saves each referenced row in
  the same transaction that stores the reference.
* Manual SQL: `btree_gist` (already installed by earlier migrations), the subscription exclusion constraint, the contract-line key
  above, row-level security on every new table, and REVOKE from `anon` / `authenticated`.
* `Down` refuses to run while service-only payments exist (`contract_id IS NULL`): restore from backup instead. Otherwise it restores
  `contract_id` as `NOT NULL` with no default, exactly as before (the EF-generated `Guid.Empty` default was removed).
* Additive only, guarded by `AfterSalesServicesMigrationTests` and, on disposable PostgreSQL, `PostgresAfterSalesMigrationTests`
  (Down gives a catalog identical to a database that never had the migration; a refused Down changes neither schema nor data) and
  `PostgresPaymentIntegrityTests`. Reviewed SQL:
  [sql/migration-20261021023128-after-sales-services.up.sql](sql/migration-20261021023128-after-sales-services.up.sql) and
  [.rollback.sql](sql/migration-20261021023128-after-sales-services.rollback.sql). Both were run with `psql` on a disposable
  PostgreSQL 17 database (on top of all earlier migrations, with Supabase-style default grants): Up, Down (identical catalog), Up again.

**Required Admin setup after applying** (nothing is seeded; until step 1 is done Sales cannot issue new contracts):
1. `PUT /api/admin/after-sales/warranty-options` — at least one active `INSTALLATION` option, one marked default (e.g. 120 months).
2. The same for `REPAIR` (repair-warranty months on paid quotations; without one, quotations carry no repair warranty).
3. `POST /api/admin/after-sales/packages` for the maintenance and disaster-protection packages that are sold.

## Known limitations
* Notifications are in-app records (`GET notifications`); no email or push.
* Dashboard money sums are added up in the API over the already filtered amounts (SQLite, used by the tests, cannot `SUM` decimals).
* Parts availability for a same-visit repair is the technician's confirmation; there is no stock module.
