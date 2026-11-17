# Customer-to-installation flow (2026-11-17)

Status: implemented in the backend working tree (not committed, not deployed). The migration
`20261117092018_DirectBookingAndInstallationSchedule` is **created and tested on disposable PostgreSQL only — not applied** to Supabase/VPS.
It builds on [simplified-commercial-flow.md](simplified-commercial-flow.md) (technician cart, auto-drafted contract, CASH/PayOS).

## 1. Business flow

```
Option A (pre-survey)                         Option B (direct booking)
Customer → PreSurvey (+ Simulation)            Customer → own PropertySite
  → submit → survey_request(source=PRE_SURVEY)   → POST /api/customers/me/survey-requests/direct
                     \                              (source=DIRECT_BOOKING, no pre-survey)
                      \____________________________/
                                   │  same booking rules: horizon, slot grid, zone coverage, verified technician capacity,
                                   │  one active appointment, dispatch queue for uncovered areas (no Sales claim)
                                   ▼
          Technician accepts → on-site survey (real measurements) → technician cart quotation → Customer ACCEPT
          → contract DRAFT (auto) → Manager/Admin issue/publish → Customer signs → Manager/Admin confirm → SIGNED
          → Technician records the date agreed with the customer (schedule PLANNED; holds no capacity)
          → Customer follows PLANNED on mobile, may ask for another date
          → PAID (PayOS webhook/reconciliation or Manager/Admin cash confirmation)
          → system: one installation project + assignment re-checked under the locks → schedule CONFIRMED (or RESCHEDULE_REQUIRED)
          → Technician starts only with the accepted assignment AND every EVN / PCCC / structural prerequisite satisfied
          → evidence, assets, completion report → Customer acceptance (handover)
```

Sales: consultation and customer care only (CRM, follow-ups); no step of quotation, contract, payment, schedule or installation needs Sales.

## 2. Actor / permission matrix (new endpoints)

| Endpoint | Customer | Technician | Sales | Manager | Admin |
|---|---|---|---|---|---|
| `POST /api/customers/me/survey-requests/direct` | own site | – | – | – | – |
| `GET /api/technicians/me/installation-schedules` | – | own surveyed SIGNED/PAID contracts | – | – | – |
| `GET/POST /api/technicians/me/contracts/{id}/installation-schedule` | – | surveying technician only (else 404) | – | – | – |
| `POST /api/technicians/me/installation-schedules/{id}/cancel` | – | own PLANNED/RESCHEDULE_REQUIRED | – | – | – |
| `GET /api/customers/me/installation-schedules`, `GET /api/customers/me/contracts/{id}/installation-schedule` | own (else 404) | – | – | – | – |
| `POST /api/customers/me/installation-schedules/{id}/change-request` | own | – | – | – | – |
| `GET /api/manager/installation-schedules?status=`, `GET /api/manager/contracts/{id}/installation-schedule` | – | – | – | ✔ | ✔ |
| `POST /api/manager/contracts/{id}/installation-schedule` (named technician, exception) | – | – | – | ✔ | ✔ |
| `POST /api/manager/contracts/{id}/installation-schedule/confirm` (retry; idempotent) | – | – | – | ✔ | ✔ |
| `POST /api/manager/installation-schedules/{id}/cancel` | – | – | – | ✔ | ✔ |
| `POST /api/chat/messages` (+ optional `advisor`), `POST /api/chat/advisor` | ✔ | ✔ | ✔ | ✔ | ✔ |

Anonymous: 401 everywhere. Other roles: 403 (tested for all five roles). Existing Manager project endpoints (assign, reassign, cancel,
checklist) are unchanged and remain the exception path.

## 3. State machines

**Survey request** (unchanged statuses; new `source`): `Pending → Scheduled → Completed`, `Cancelled` (customer), `Closed`.
`source = PRE_SURVEY` ⇔ `pre_survey_id IS NOT NULL` (check constraint). At most one open (Pending/Scheduled) DIRECT_BOOKING request per site
(partial unique index); a site with an open PRE_SURVEY request gets `409 SURVEY_REQUEST_ALREADY_OPEN` (book that one instead).

**Quotation / contract / payment**: see simplified-commercial-flow.md (unchanged).

**Installation schedule** (`installation_schedule_proposal`, one live row per contract, history kept):

```
          (technician/Manager records, contract SIGNED or PAID)
 ──────► PLANNED ──(contract PAID; project + assignment re-checked OK)──► CONFIRMED
            │  └──(re-check fails / start passed before payment)──► RESCHEDULE_REQUIRED
            │                                                            │ new date (reason) → new PLANNED row (+ confirm if PAID)
            └──(new date: reason required) → CANCELLED "REPLACED: …" + new PLANNED
 any live ──(technician/Manager cancel w/ reason, Manager assignment "SUPERSEDED", project cancelled)──► CANCELLED
 CONFIRMED ──(technician declines the assignment)──► RESCHEDULE_REQUIRED
```

`isConfirmed` is true **only** for CONFIRMED; `awaitingPayment` is true for PLANNED on an unpaid contract. Mapping to the existing
project state machine: CONFIRMED = project `SCHEDULED` with the technician's ACCEPTED assignment; no new project status was added.

## 4. Rules implemented

* PLANNED holds **no** capacity (nothing can be blocked indefinitely before payment); it is validated when recorded (future start, horizon,
  max length, technician active + zone member + working day/hours + no leave + no other commitment within the buffer).
* On PAID (after the payment transaction commits; never inside it): project created once (unique index; `manager_user_id = NULL`,
  event actor = the technician), `InstallationHandlers.AssignAsync` re-checks under technician / site / zone locks (exclusion constraint
  backstop), then the assignment is accepted on behalf of the technician who agreed the date. Conflicts → RESCHEDULE_REQUIRED, never a
  false confirmation. A sweep worker (`Installation:ScheduleWorkerEnabled`, every 60 s) retries missed confirmations and flags PLANNED rows
  whose start passed before payment. Concurrent confirmations (webhook, reconciliation, cash, sweep, Manager retry) are idempotent (tested).
* Starting work is unchanged: accepted assignment + every mandatory prerequisite (EVN, PCCC, structural) satisfied (tested).
* Notifications to the customer: `INSTALLATION_SCHEDULE_PLANNED`, `_CHANGED`, `_RESCHEDULE_REQUIRED`, `_CANCELLED`, then the existing
  `INSTALLATION_SCHEDULED` on confirmation.

## 5. Data model and migration

| Change | Detail |
|---|---|
| `survey_request.pre_survey_id` | now nullable |
| `survey_request.property_site_id` | NOT NULL, FK `fk_survey_request_property_site`; **backfilled from `pre_survey.property_id`** before NOT NULL |
| `survey_request.source` | `PRE_SURVEY` (default for existing rows) / `DIRECT_BOOKING`; checks `ck_survey_request_source`, `ck_survey_request_source_pre_survey` |
| `survey_request.contact_name / contact_phone` | optional on-site contact (direct booking) |
| `ux_survey_request_one_open_direct_booking` | one open direct booking per site |
| `installation_project.manager_user_id` | nullable (system-created projects) |
| `installation_schedule_proposal` | new table, FKs to contract, survey request, site, users, project, previous row; checks on status / interval / confirmation / reschedule / cancellation; `ux_installation_schedule_proposal_one_live` |

Before applying (read-only, operator): `SELECT count(*) FROM survey_request s LEFT JOIN pre_survey p ON p.id = s.pre_survey_id WHERE p.id IS NULL;`
must be 0 (otherwise the backfill cannot fill `property_site_id` and the migration fails without changing anything).

**Rolling-deploy compatibility** — migration `20261117101818_SurveyRequestSiteCompatibility`: a `BEFORE INSERT` trigger
(`trg_survey_request_fill_property_site`) fills `property_site_id` from the request's own pre-survey when an API build older than this
release omits it, so the old build keeps accepting pre-survey submissions until every instance runs the new image. The new build always
sets the column (the trigger then does nothing); a request without a pre-survey is never given a site (still refused). Once every API
instance runs the new build the trigger can stay (harmless) or be removed with this migration's Down.

**Deployment record (2026-11-17, Supabase pooler `aws-0-ap-northeast-2`, database `postgres`)**: backup
`~/SmartSolarBackups/smartsolar-supabase-before-SimplifiedCommercialFlow-DirectBooking-20261117T101236Z.dump` (+ `.sha256`; restore
verified: 103 tables, identical row counts and schema); rehearsal on the restored copy PASS; applied
`20261117074228_SimplifiedCommercialFlow` + `20261117092018_DirectBookingAndInstallationSchedule` 10:16:37–10:16:52Z and
`20261117101818_SurveyRequestSiteCompatibility` 10:21:17–10:21:28Z (EF Core); 29 requests backfilled (PRE_SURVEY), no existing row changed;
schema identical to a fresh database migrated to the same point.

**Rollback (`Down`)**: refuses with an exception and changes nothing while DIRECT_BOOKING requests, installation schedules or
system-created projects exist (tested). Otherwise it restores the previous schema (tested down/up). No row is ever deleted.
Code rollback without schema rollback is not supported once direct bookings exist (the previous build requires `pre_survey_id`).

## 6. Legacy matrix

| Item | Decision |
|---|---|
| PreSurvey / Surface / Simulation | KEEP (Option A, optional) |
| Survey request bound to a pre-survey only | REPLACE (site on the request; pre-survey optional) |
| Sales claim before booking | KEEP as optional CRM ownership (never required; it was not required for booking already) |
| Manager-only installation scheduling | KEEP as exception path; normal path = technician-agreed schedule |
| Manager "create project from PAID contract" | KEEP (fallback; the system creates it when a schedule exists) |
| Sales quotation / pricing review / automatic generation | DEPRECATED earlier (410), unchanged here |
| Tables / history | nothing removed |

## 7. FE / Mobile integration

Web FE (`smart-solar` @ 9bbb45b, read only): `preSurveyId` is typed `string` in `src/types/res/surveyRequestsRes.ts`,
`customerSurveyRequestsRes.ts` and `technicianAppointmentsRes.ts` — it must become `string | null`, and `source` must be read. The
simulation link of a technician job is null for direct bookings. No Mobile source was found on this machine: the contract below is the
integration reference.

* **Direct booking**: `POST /api/customers/me/survey-requests/direct`
  `{ propertySiteId, startsAt: "2026-11-25T08:00:00+07:00", contactName?, contactPhone?, note? }` → `201` (`200` for a retry) with
  `{ surveyRequestId, source: "DIRECT_BOOKING", requestCreated, booking: { appointmentId, appointmentStatus, acceptanceStatus, … } }`.
  Use `GET /api/survey-slots?propertySiteId&date` first. Failures: `404 PROPERTY_SITE_NOT_FOUND`, `422 SITE_LOCATION_REQUIRED`,
  `400 CONTACT_PHONE_REQUIRED` / validation, `409 SURVEY_REQUEST_ALREADY_OPEN` (`details.surveyRequestId`), coverage / capacity `409`
  with `details { surveyRequestId, coverageStatus, dispatchAvailable }` — the request stays Pending: pick another slot
  (`POST /api/customers/me/survey-requests/{id}/booking`) or ask for a dispatch.
* **Technician schedule**: `GET /api/technicians/me/installation-schedules` → `[{ contractId, contractNumber, contractStatus, propertyName,
  customerName, schedule | null }]`. Record / change: `POST /api/technicians/me/contracts/{id}/installation-schedule`
  `{ startsAt, endsAt, customerAgreementNote, reason?, expectedRevision? }` (`reason` required to change). Errors:
  `400 …AGREEMENT_REQUIRED / …REASON_REQUIRED / …INVALID`, `404 …CONTRACT_NOT_FOUND`, `409 …CONTRACT_NOT_SIGNED / …TECHNICIAN_INELIGIBLE
  (details.reason) / …INSTALLATION_STARTED / …ASSIGNED_TO_OTHER_TECHNICIAN / …STALE_REVISION / …COVERAGE_UNAVAILABLE`.
* **Customer mobile**: `GET /api/customers/me/installation-schedules` and `GET /api/customers/me/contracts/{id}/installation-schedule`
  (history, newest first). Show `status` + `isConfirmed` (never show PLANNED as confirmed; `awaitingPayment` → "chờ thanh toán"),
  `technicianName`, `startsAt/endsAt`, `projectStatus`. Change request: `POST /api/customers/me/installation-schedules/{id}/change-request { note }`.
  Progress stays on `GET /api/customers/me/installation-projects/{projectId}`.
* **Manager/Admin**: `GET /api/manager/installation-schedules?status=RESCHEDULE_REQUIRED` (conflict queue), `…/confirm` retry,
  `…/cancel`, existing project assignment for exceptions, existing checklist for EVN/PCCC/structural documents.
* **Chat advisor**: `POST /api/chat/messages { sessionId?, message, clientMessageId?, advisor?: { roofAreaM2, budgetVnd,
  monthlyConsumptionKwh, propertySiteId, latitude, longitude, tiltDegree, azimuthDegree, wantsBattery } }` → the usual answer plus
  `advisor` (structured result) when the question is a configuration question. `POST /api/chat/advisor` returns the structured result only
  (nothing stored). Show `status` (READY / NEEDS_INPUT / BLOCKED), `missingInputs[].question`, `configurations[]`, `assumptions`,
  `limitations`, `blocked`, `sources`, `disclaimer`. Leading the customer to Option A / B needs the customer's own explicit action.

## 8. RAG configuration advisor

Architecture: `ChatService` detects configuration questions deterministically (Vietnamese/English patterns for m², triệu/tỷ/VND,
kWh/tháng, tilt, compass direction, battery words, sizing phrases; warranty/contract/payment questions excluded) and answers them with
`SolarAdvisorService` — **no LLM call**; other questions keep the RAG path. Values from earlier user turns of the session are reused, so
"hỏi bổ sung" works without the model. Tools are read-only: ACTIVE, VND-priced catalog products (the same data as `GET /api/products`),
the caller's own site (latest pre-survey tilt/azimuth/usable area, selected non-stale simulation), PVGIS 5.3.

Rules (all documented in code and returned as `assumptions`):

1. Module footprint = catalog width × height; theoretical max panels = ⌊area ÷ footprint⌋ (upper bound, no gaps/setbacks/walkways).
2. Specific yield: selected simulation; else PVGIS 1 kWp at the given location / tilt / compass azimuth, configured loss (14 %),
   conservative `building` mounting; cached per rounded location for 24 h. No default location/tilt/azimuth is assumed (asked instead).
3. kWp for the stated use = kWh/month × 12 ÷ yield (annual balance); panels = ⌈kWp × 1000 ÷ W⌉.
4. Inverter = cheapest ACTIVE inverter whose documented `maxDcInputPowerW` ≥ array W; undocumented inverters are never "compatible".
5. Cost = panels + inverter + services priced per KWP; per-job/per-trip services listed as not included; tax = configured 8 %.
   Budget = largest count with a compatible inverter whose total fits.
6. **BLOCKED**: battery sizing (no battery capacity spec and no approved backup rule), string/MPPT design (no panel Voc/Isc), roof load.

Security: every signed-in role; customers read only their own site (other ids → `PROPERTY_SITE` question); no internal price or document
is used; the advisor has no write method; quota, concurrency cap, session ownership and rate limit are those of the chat; an advisor
failure returns `503 RAG_ADVISOR_UNAVAILABLE` without affecting the knowledge chat.

Measured (2026-11-17, this Mac, not the VPS):

| Measurement | Result |
|---|---|
| Advisor chat through the API pipeline (in-process, SQLite, 20 panels × 20 inverters, instant PVGIS fake), n = 200 | p50 2.7 ms, p95 33.4 ms, max 64.2 ms; 0 Ollama calls |
| Real PVGIS 5.3 PVcalc (1 kWp, HCMC coordinates), 3 calls | 0.86 s, 0.91 s, 1.06 s; repeated location: 0 calls (cache) |
| Knowledge RAG path with Ollama | not re-measured in this task (the embedding model is not installed locally); earlier figures in rag-local-ollama.md; VPS p95 ≤ 10 s still OPEN |

## 9. Open decisions

* Whether a PLANNED schedule should hold technician capacity for a limited time (implemented: no hold).
* Technician in-app notifications (no technician notification endpoint exists; change requests are visible on the technician's schedule views).
* Battery catalog type, capacity specification and backup-sizing rule (advisor BLOCKED until defined).
* Self-consumption / export policy for sizing (advisor sizes to 100 % of annual consumption and says so).
* Whether the chat should let an LLM rephrase advisor results (currently a deterministic template, by design).
