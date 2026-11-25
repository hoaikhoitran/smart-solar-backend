# FE handoff — Flow 1–4 fixes (backend, 2026-11-25)

Fixes for the issues the FE team reported on 2026-11-25 against `origin/main` `763cb0b`. They are in the local backend working tree together with the
scheduling early-completion / overtime work (see `guides/scheduling-early-completion/`). Nothing is deployed yet: the shared server still runs `763cb0b`
until the release (two new migrations included). Every response keeps the envelope `{ isSuccess, traceId, data, error: { code, message, details } }`.

## BUG-01 — completed surveys blocking their old slot

- A survey completed before its scheduled slot (e.g. scheduled 26/11 10:00, completed 25/11 15:56) no longer blocks that slot for new bookings,
  acceptances or assignments. Active (not completed) appointments still block; double booking is still refused by the database.
- The configured buffer applies after the **real** completion time on the day it happened.
- The approved daily survey limit is unchanged: the completed survey still counts on its **scheduled** day (26/11), not on the day it was completed.
- `startsAt`, `endsAt`, `completedAt`, status and history are unchanged. Completing earlier than `onsiteSurvey.earlyStartMinutes` before the start is still
  refused (`409 ONSITE_SURVEY_TOO_EARLY` at start); another technician gets `404 ONSITE_APPOINTMENT_NOT_FOUND`.
- No API change. Re-query slots (`GET /api/survey-slots`) after a completion.

## BUG-02 — accepted installations still blocking other installation schedules (`PLANNED_CONFLICT`)

- A schedule (plan) of a project that is COMPLETED or CANCELLED never blocks another plan of the technician, whatever status the old row stores
  (CONFIRMED, PLANNED, RESCHEDULE_REQUIRED). Rows are kept as history (no data repair).
- A CONFIRMED plan of an active project blocks through its installation assignment (`TECHNICIAN_SCHEDULE_CONFLICT` / `TECHNICIAN_INELIGIBLE` with the
  reason), a PLANNED / RESCHEDULE_REQUIRED plan of an active project still answers `PLANNED_CONFLICT`.
- Same rule for `POST /api/technicians/me/contracts/{contractId}/installation-schedule`, Manager `POST /api/manager/contracts/{contractId}/installation-schedule`
  and the customer slot-offer paths. No API change.

## BUG-03 — whole quantities for panels and inverters

- Lines whose catalog product type is `SOLAR_PANEL` or `INVERTER` need a positive **whole** quantity. Services (labour, transport, other services) keep
  up to 3 decimals. Other equipment types keep the previous rule until the catalog defines them (see Open decisions).
- `POST /api/technician/quotations/{quotationId}/lines` and `PUT /api/technician/quotations/{quotationId}/lines/{lineId}` with e.g. `quantity: 1.5` for a panel:

```json
{ "isSuccess": false, "traceId": "…", "error": { "code": "VALIDATION_FAILED", "message": "One or more fields are invalid.",
  "details": { "quantity": ["SOLAR_PANEL is sold by whole units: the quantity must be a positive whole number (got 1.5)."] } } }
```

  HTTP 400. Nothing is changed.
- `POST /api/technician/quotations/{quotationId}/issue` on an old draft that still holds a fractional panel / inverter line: HTTP 400 `VALIDATION_FAILED`,
  `details.quantity` lists every offending line (`"Line 1 (SKU): …"`). Fix the line, then issue.
- Prices and totals remain server-computed; per-panel package quantities follow the (now whole) panel count.

## BUG-04 — `contractId` on every quotation read

`QuotationView.contractId` is now returned by every read, not only by the `AGREED` decision response:

| Endpoint | Role |
| --- | --- |
| `GET /api/technician/quotations/{quotationId}` | Technician (own surveyed request) |
| `GET /api/technician/survey-requests/{surveyRequestId}/quotations` | Technician |
| `GET /api/quotations/{quotationId}`, `GET /api/survey-requests/{surveyRequestId}/quotations` | Sales / Manager / Admin (unchanged access rules) |

Value: the persisted contract of the quotation that is neither DISCARDED nor SUPERSEDED (latest version); `null` when none (draft, not agreed, or the
agreement was withdrawn before the deposit → contract DISCARDED). Lists resolve all ids in one query. Other technicians still get 404.

## BUG-05 — rate limiting of dispatch reads

- New policy `dispatch-read` (per user, fixed window, **20 requests / minute** — the same number as before, configurable under `RateLimiting:DispatchRead`)
  for the lightweight reads that return stored rows:
  - `GET /api/customers/me/survey-requests/{surveyRequestId}/dispatch-request`
  - `GET /api/admin/dispatch-requests`
  - `GET /api/admin/dispatch-requests/{dispatchRequestId}`
- `scheduling-read` (20 / minute) keeps the computing reads: slots, availability, candidates (`GET /api/admin/dispatch-requests/{id}/candidates`),
  technician schedules and open jobs.
- Polling a dispatch request therefore no longer consumes the availability quota. Over a limit the API still answers **429** (unchanged body / headers).
  Poll dispatch details at most every few seconds; back off on 429.

# Follow-up: FE feedback after the Flow 1–4 audit (2026-11-25)

| Issue | Classification | Backend change |
| --- | --- | --- |
| ISSUE-01 "no 30/70 contract" after AGREED | `ALREADY_FIXED_LOCALLY` (BUG-04 above) + FE navigation | None (tests added) |
| ISSUE-02 customer name on the Manager project list | `BACKEND_FIX_REQUIRED` → fixed | New field `customerName` (DTO only, no migration) |
| ISSUE-03 signature / payment UI | `FRONTEND_ONLY` / `ALREADY_SUPPORTED` / `NEEDS_FE_REPRODUCTION` per item (table below) | None (tests added) |

## ISSUE-01 — the contract after AGREED (verified)

The local backend already returns the contract everywhere. Verified with and without bundled per-panel packages, over HTTP and on PostgreSQL,
including a read on brand-new connections and handlers (nothing kept in memory, so it holds across an API restart):

1. `POST /api/technician/quotations/{quotationId}/on-site-decision` with `decision: "AGREED"`. The contract (DRAFT, 30/70) and its two installments
   are created in the same transaction. The response `data.contractId` is set.
2. `GET /api/technician/quotations/{quotationId}` and `GET /api/technician/survey-requests/{surveyRequestId}/quotations` return the same
   `contractId`. Staff reads do too.
3. Open the contract page with that id:
   - Technician, Sales, Manager or Admin: `GET /api/contracts/{contractId}`.
   - Customer: `GET /api/customers/me/contracts/{contractId}`.

   The response has `paymentPlan.installments`: sequence 1 is `DEPOSIT` at 30 % of the **full** `totalAmount`, sequence 2 is `BALANCE` at 70 %, and the
   two amounts add up to `totalAmount`. With bundled packages it also has `servicePackages`, and `installationTotal + servicePackagesTotal = totalAmount`.
4. `contractId` is `null` only when there is no live contract. Two cases:
   - the agreement was withdrawn before the deposit, so the contract is `DISCARDED`;
   - the only version is `SUPERSEDED`.

Excerpt of `GET /api/contracts/{contractId}` right after AGREED (amounts from the test catalog, illustrative only):

```json
{ "isSuccess": true, "traceId": "…", "data": {
  "contractId": "6f1c…", "contractNumber": "…", "status": "DRAFT", "quotationId": "a2e9…", "totalAmount": 57000000,
  "installationTotal": 50000000, "servicePackagesTotal": 7000000, "paymentCompleted": false,
  "paymentPlan": { "plan": "DEPOSIT_30_BALANCE_70", "stage": "DRAFT_CREATED", "total": 57000000, "paid": 0, "outstanding": 57000000, "depositPaid": false,
    "paperSigned": false, "readyForInstallation": false, "fullyPaid": false,
    "installments": [
      { "sequence": 1, "kind": "DEPOSIT", "percent": 30, "amount": 17100000, "status": "DUE", "paidAt": null, "allocation": { … } },
      { "sequence": 2, "kind": "BALANCE", "percent": 70, "amount": 39900000, "status": "PENDING", "paidAt": null, "allocation": { … } } ],
    "signature": null },
  "servicePackages": [ { "kind": "DISASTER_PROTECTION", "pricingUnit": "PER_PANEL", "quantity": 10, "totalAmount": 7000000, … } ] } }
```

**FE work:**
- Read `contractId` from the AGREED response or from any quotation read, and navigate with it.
- Do not decide "no contract" from local state or from a quotation status.
- If the FE still shows "no contract", reproduce it against this build and capture the request and response. The FE may be pointing at the deployed
  `763cb0b`, which does not have BUG-04 yet.

## ISSUE-02 — `customerName` on the Manager project list (new field)

Endpoint: `GET /api/installation-projects` (Manager, Admin). Note: the route is **not** `/api/manager/...`. Without `page`/`pageSize` it returns an
array; with them it returns `{ items, page, pageSize, totalItems, … }`. Each item now has `customerName`:

```json
{ "isSuccess": true, "traceId": "…", "data": [
  { "projectId": "…", "contractId": "…", "status": "PENDING_ASSIGNMENT", "responsibleTechnicianUserId": null, "responsibleTechnicianName": null,
    "technicianAccepted": false, "scheduledStartsAt": null, "scheduledEndsAt": null, "operationalZoneId": "…", "createdAt": "…", "revision": 1,
    "customerName": "Nguyễn Văn An" } ] }
```

- The value is the customer account's display name (`CustomerUserId`).
- Only the name is exposed: no phone, no email.
- The names of the whole list or page come from one query, together with the technician names. There is no per-row query.
- Fallbacks:
  - A soft-deleted customer keeps their stored name, because accounts are kept for history.
  - A missing account gives `""`, the same as `site.customerName` on the detail.
  - The FE should show a placeholder for `""`.
- All existing fields, the envelope, the `status` filter, paging validation (400) and roles (Manager/Admin; 401 anonymous; 403 others) are unchanged.
- The project detail `GET /api/installation-projects/{projectId}` already shows the customer under `site.customerName` (and `site.customerPhone`).
  `site` is `null` only when the property site is missing. No detail change was needed.

## ISSUE-03 — contract signature and payment UI

| Item | Classification | What the backend does / what the FE should do |
| --- | --- | --- |
| Paper signature flow | `ALREADY_SUPPORTED` | Technician steps:<br>1. `POST /api/technician/contracts/{id}/signature-set` `{ expectedPageCount, signedOn?, note? }`<br>2. `PUT …/signature-set/pages/{n}` `{ fileId }` (a VERIFIED `SIGNED_CONTRACT_PAGE` file)<br>3. `POST …/signature-set/finalize` → contract `SIGNED`<br>Before the deposit is verified, step 1 answers **409 `CONTRACT_DEPOSIT_REQUIRED`**: show the deposit step first and do not offer the signature. Read the pages with `GET /api/contracts/{id}/signature-sets`. Unchanged. |
| "Contract completed" text | `FRONTEND_ONLY` | Choose the wording from `paymentPlan.stage`: `DRAFT_CREATED`, `DEPOSIT_VERIFIED`, `READY_FOR_INSTALLATION`, `BALANCE_DUE`, `BALANCE_OVERDUE`, `FULLY_PAID`, `DISCARDED`. Also usable: `paperSigned`, `fullyPaid`. `paymentCompleted` is true only when **both** installments are paid. `SIGNED` means the paper was signed after the deposit; it does not mean paid. Never show "completed" for `SIGNED` or `DEPOSIT_VERIFIED`. |
| Navigation to the contract page | `FRONTEND_ONLY` | Use `quotation.contractId` (ISSUE-01): `GET /api/contracts/{contractId}` for staff and technicians, `GET /api/customers/me/contracts/{contractId}` for the customer. |
| 14px layout overflow | `FRONTEND_ONLY` | CSS issue. The backend returns full amounts and free text unchanged (e.g. long contract numbers or package names). Wrap or ellipsize in the FE. |
| Payment receipt | `ALREADY_SUPPORTED` (data); `NEEDS_FE_REPRODUCTION` if a printable receipt document is expected | `GET …/installments/{n}/payment` → `attempts[]`. On the `PAID` attempt: `method` (`CASH` or `ONLINE`), `providerReference` (cash receipt number or bank reference), `amountPaid`, `paidAt`, `orderCode`. The cash evidence photo (`evidenceFileId`) and `confirmedByUserId` are staff-only, as before. The backend does not generate a PDF receipt. Producing one is a product decision, not a defect. |
| Removing a cash selection | `ALREADY_SUPPORTED` | Technician: `POST /api/technician/contracts/{id}/installments/{n}/payment/cancel`. Customer: `POST /api/customers/me/contracts/{id}/installments/{n}/payment/cancel`.<br>Result: `paymentState` back to `AWAITING_PAYMENT`, installment still `DUE`, `current: null`, and the attempt in `attempts[]` as `CANCELLED`. Nothing is recorded as paid. The FE can then choose cash again or PayOS.<br>Errors: a second cancel → 404 `PAYMENT_ATTEMPT_NOT_FOUND`. Once a Manager or Admin has confirmed the cash, cancel is refused and the installment stays `PAID`.<br>CASH stays available, and only a Manager or Admin confirms received cash. |

Business rules unchanged:
- The deposit is 30 % of the full contract total.
- The paper is signed only after the verified deposit.
- Customer acceptance opens the 70 % balance; it is `NOT_PAYABLE` before.
- PayOS and Manager/Admin cash confirmation both remain.

No new error codes. One DTO change: `InstallationProjectSummary.customerName`.

## Open decisions (not invented)

| Item | Owner |
| --- | --- |
| Final quota of `dispatch-read` (kept at 20 / minute / user) | Product / Security |
| Whether other equipment types (batteries, meters, mounting kits…) must also be whole units — needs a catalog flag or type list | Product / Catalog |
| Overtime hold length (currently until the end of the local day) | Operations |
