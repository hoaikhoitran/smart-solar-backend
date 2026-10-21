# Uncovered-zone dispatch

When slots cannot serve a customer (the commune has no zone, the zone has no technician, nobody is free in the horizon, or nobody
at the wanted times), the customer still gets served: they create a **dispatch request** and a Manager/Admin arranges a technician
by hand — a direct assignment, or a proposal the customer accepts.

Business rules (approved):

* Uncovered areas accept requests; there is no automatic rejection and **never a confirmed appointment without valid capacity**.
* SmartSolar accepts requests throughout the merged Ho Chi Minh City. Every **temporarily uncovered** valid site is dispatchable —
  no zone (`NO_ZONE_COVERAGE`), a zone marked not serviceable (`ZONE_NOT_SERVICEABLE`), no technician, no capacity, a time
  conflict. Only an **invalid / out-of-scope location** is not: `SITE_LOCATION_REQUIRED` (422; set a valid commune first) — a
  province outside Ho Chi Minh City is refused when the location is set (`422 ADMINISTRATIVE_LOCATION_OUT_OF_SERVICE_AREA`).
* A technician releasing a dispatch-assigned job sends it back to this queue (§4a); the customer never re-submits.
* Review SLA: `review_due_at = created_at + dispatch.reviewSlaMinutes` (default 240, real elapsed time 24/7), snapshotted.
* Dispatch requests **never expire**: there is no `EXPIRED` status and no job that closes them.
* Cross-zone dispatch is manual only (explicit authorization + reason, audited); no routing provider, travel time or distance.
* Proposals reserve **no** capacity; acceptance re-verifies everything and books atomically.
* Proposal window: `expires_at = LEAST(created_at + dispatch.proposalResponseWindowMinutes, starts_at)` (default 1440), snapshotted;
  expiry marks only the proposal.

Code: `src/SmartSolar.Modules/Scheduling/Dispatch/` (`DispatchService`, `DispatchSweepService`), persistence
`src/SmartSolar.Infrastructure/Persistence/DispatchUnitOfWork.cs`, EF configuration `Persistence/Configurations/Scheduling/DispatchConfigurations.cs`,
controllers `src/SmartSolar.Api/Controllers/DispatchControllers.cs`, worker `src/SmartSolar.Api/Background/DispatchWorker.cs`.

## 1. Coverage statuses on slots and bookings

`GET /api/survey-slots?propertySiteId=&date=` answers **200** for the customer's own site and a date inside the booking horizon
(404 `PROPERTY_SITE_NOT_FOUND` / 400 `INVALID_BOOKING_TIME` otherwise):

```json
{ "date": "2026-10-21", "timeZone": "Asia/Ho_Chi_Minh", "appointmentDurationMinutes": 90, "zone": null, "slots": [],
  "note": "No active operational zone serves the commune of this property site yet. You can request a dispatch: …",
  "coverageStatus": "NO_ZONE_COVERAGE", "dispatchAvailable": true }
```

| `coverageStatus` | Meaning | `dispatchAvailable` |
|---|---|---|
| `COVERED` | Slots are listed | false |
| `SITE_LOCATION_REQUIRED` | The site has no valid commune | false (set the location) |
| `NO_ZONE_COVERAGE` | The commune is not mapped to an active zone | true |
| `ZONE_NOT_SERVICEABLE` | The zone is temporarily marked not serviceable | true |
| `NO_ZONE_TECHNICIAN` | The zone has no schedulable member | true |
| `NO_AVAILABLE_TECHNICIAN` | Members exist but none can take any slot in the horizon | true |
| `SCHEDULE_CONFLICT` | Members are free in the horizon, but not on this date | true |

A booking (or customer reschedule) without capacity keeps failing — no false confirmation — with the precise code and
`error.details = { "coverageStatus": "...", "dispatchAvailable": bool }`: 422 only for `SITE_LOCATION_REQUIRED` (invalid
location); 409 with `dispatchAvailable: true` for `NO_ZONE_COVERAGE`, `ZONE_NOT_SERVICEABLE`, `NO_ZONE_TECHNICIAN`,
`NO_AVAILABLE_TECHNICIAN`, `SCHEDULE_CONFLICT` (this replaces
`SURVEY_SLOT_UNAVAILABLE` on the customer paths; staff reschedule paths keep it). While a dispatch request of the survey request
is open, booking answers `409 DISPATCH_REQUEST_OPEN` (accept its proposal or cancel it first).

## 2. Data model

| Table | Purpose / key columns |
|---|---|
| `dispatch_request` | `survey_request_id`, `property_site_id`, `customer_user_id`, `operational_zone_id` (NULL for NO_ZONE_COVERAGE), `reason` (dispatchable coverage code), `status`, `customer_note`; SLA snapshot `review_sla_minutes`, `review_due_at`, `escalation_due_at`, `reminder_interval_minutes`, `settings_version`; worker metadata `overdue_at`, `escalated_at`, `last_reminder_at`, `reminder_count`; resolution `resolved_at`, `resolved_by_user_id`, `resolution`, `resolution_reason`, `appointment_id`; `created_at`, `updated_at`, `revision` |
| `dispatch_preferred_window` | `dispatch_request_id`, `starts_at`, `ends_at` (≤ 5 per request) |
| `dispatch_proposal` | `dispatch_request_id`, `technician_user_id`, `starts_at`, `ends_at`, `cross_zone`, `cross_zone_authorization_id`, `proposed_by_user_id`, `note`, `created_at`, `response_window_minutes`, `settings_version`, `expires_at`, `status`, `responded_at`, `failure_reason`, `revision` |
| `cross_zone_authorization` | `dispatch_request_id` XOR `installation_project_id`, `technician_user_id`, `target_zone_id` (the site's zone, NULL without zone), `reason` (required, ≤ 500), `authorized_by_user_id`, `authorized_at` — append-only |
| `dispatch_event` | append-only history: `event_type`, `actor_user_id`, `proposal_id`, `detail`, `created_at` |
| `dispatch_notification` | same pattern as `scheduling_notification`: `recipient_user_id`, `dispatch_request_id`, `event_id`, `type` (`DISPATCH_<EVENT>`), `delivery_status = NOT_DELIVERED` |
| `technician_appointment` (new columns) | `cross_zone_authorization_id` (FK), plus the settings snapshot `acceptance_window_minutes`, `settings_version`, `acceptance_reminder_sent_at` |

Integrity: partial UNIQUE `dispatch_request(survey_request_id) WHERE status IN ('OPEN','PROPOSAL_PENDING')` (one active request
per survey request; the customer create is idempotent on it), partial UNIQUE `dispatch_proposal(dispatch_request_id) WHERE status
= 'PROPOSED'` (one live proposal) and `… WHERE status = 'ACCEPTED'` (one accepted proposal), status/reason/resolution checks,
`expires_at <= starts_at`, `cross_zone = (cross_zone_authorization_id IS NOT NULL)`, revision concurrency tokens. The appointment
`assignment_source` check gains `MANAGER_DISPATCH`.

## 3. State machine

Dispatch request (no `EXPIRED` status exists):

| From | Action | To |
|---|---|---|
| — | customer create | `OPEN` |
| `OPEN` | staff proposal | `PROPOSAL_PENDING` |
| `PROPOSAL_PENDING` | customer decline / staff withdraw / worker expiry / failed acceptance | `OPEN` |
| `PROPOSAL_PENDING` | customer accept (booked) | `ASSIGNED` (resolution `ASSIGNED`) |
| `OPEN` | staff assign (booked) | `ASSIGNED` |
| `OPEN`, `PROPOSAL_PENDING` | staff unserviceable (reason) | `UNSERVICEABLE` |
| `OPEN`, `PROPOSAL_PENDING` | staff close (reason) | `CLOSED` (`CLOSED_BY_STAFF`) |
| `OPEN`, `PROPOSAL_PENDING` | customer cancel | `CLOSED` (`CANCELLED_BY_CUSTOMER`) |

Invalid transitions: assign or propose while `PROPOSAL_PENDING` → `409 PROPOSAL_ALREADY_PENDING`; any action on a terminal request
(`ASSIGNED`, `UNSERVICEABLE`, `CLOSED`) → `409 DISPATCH_INVALID_STATE`. Resolving with a live proposal withdraws it first. After a
terminal state the customer may create a new request for the same survey request (only one active at a time).

Proposal: `PROPOSED` → `ACCEPTED` | `DECLINED` | `WITHDRAWN` | `EXPIRED` | `FAILED` (all terminal). Answering again with the same
answer is a 200 no-op; any other answer on a non-`PROPOSED` proposal → `409 PROPOSAL_NOT_PENDING` (`409 PROPOSAL_EXPIRED` when it
expired).

## 4a. A technician releases a dispatch-assigned job

Root cause of the former defect: a release re-homed the hold only to members of the site's zone; a cross-zone / no-zone dispatch
appointment had none, became `ESCALATED` without a holder, the staff assign path required zone membership, the dispatch request
was already `ASSIGNED` (terminal) and the survey request still had an active appointment — nobody could reassign it.

Now `POST /api/technicians/me/appointments/{id}/release { reason }` on an appointment with `assignment_source = MANAGER_DISPATCH`
or a cross-zone authorization does, in ONE transaction (technician lock → appointment row at its revision → site lock):

1. The appointment becomes `CANCELLED` (`cancellation_reason = RELEASED_TO_DISPATCH`) with `RELEASED` (technician + reason) and
   `CANCELLED` events — it is kept as history, never deleted; its cross-zone authorization stays linked.
2. The **same** survey request returns to `Pending` (the customer does nothing).
3. A new `OPEN` dispatch request: reason `ASSIGNMENT_RELEASED`, `released_appointment_id`, the released time as preferred window,
   the previous customer note, a fresh SLA snapshot (`dispatch.reviewSlaMinutes`, default 240). `ASSIGNMENT_RELEASED` events are
   recorded on the new and on the previous request (which stays `ASSIGNED` as history); staff and the customer are notified.
4. Staff reassign it like any dispatch request: `POST …/assign` (same or other time, another technician; cross-zone approval +
   reason required again; eligibility, availability, capacity, overlap and the exclusion constraint re-checked; revision + locks +
   the one-active-request index prevent a duplicate assignment) or `POST …/proposals` when the time must change (no capacity hold;
   `dispatch.proposalResponseWindowMinutes`, default 1440).

The response is `200 { appointmentStatus: "CANCELLED", message: "… back to the Manager/Admin dispatch queue …" }`. Releases of
ordinary zone appointments are unchanged (re-homed within the zone or escalated). Staff detail exposes `releasedAppointmentId`.

## 4. SLA, escalation, reminders (worker)

`DispatchWorker` (every `Dispatch:PollSeconds`, default 60; disable with `Dispatch:WorkerEnabled = false`) runs
`DispatchSweepService` on **persisted snapshots only**:

1. Proposal expiry: `PROPOSED` with `expires_at ≤ now` → `EXPIRED` (lock order `dispatch_request` → `dispatch_proposal`, the same as
   acceptance), the request `PROPOSAL_PENDING → OPEN`; proposer and customer notified. Never touches an appointment.
2. Overdue: `OPEN` with `review_due_at ≤ now` and no `overdue_at` → `overdue_at` (conditional update), `OVERDUE` event, staff notified once.
3. Escalation: `OPEN`, already overdue (step 2 ran, so its event and staff notice are never skipped), `escalation_due_at ≤ now`
   (= `review_due_at` + `dispatch.escalationDelayMinutes` at creation) and no `escalated_at` → `escalated_at`, `ESCALATED` event,
   every active **Admin** notified — once. Admins are notified only through the escalation: when it is due at the same time as
   the overdue step (delay 0) they are left out of the OVERDUE notice, and they are left out of reminders after it.
4. Reminders: `OPEN`, overdue, `reminder_interval_minutes > 0` (snapshot; 0 = disabled) and (last reminder or `overdue_at`) +
   interval ≤ now → `last_reminder_at`, `reminder_count + 1` (conditional on the count: deduplicated), staff notified. Candidates
   are taken least recently reminded first, so a backlog larger than the batch cannot starve a request.

SLA metadata never bumps the request `revision` (staff views stay valid) and never changes the status: an overdue/escalated request
stays fully actionable. "Overdue" (live, in every view) = status `OPEN` and `now ≥ review_due_at`; a request waiting for the
customer (`PROPOSAL_PENDING`) is not overdue, and becomes overdue again if it returns to `OPEN` after its deadline.

Staff recipients: the zone's responsible manager when set and still an active Manager/Admin, otherwise every active Manager
(Admins when there is none).

## 5. Cross-zone rules

A technician is **cross-zone** when they are not an active member of the site's zone — or always, when the commune has no active
zone (`NO_ZONE_COVERAGE`). Using one requires `crossZoneAuthorized = true` **and** a `reason`, otherwise
`409 CROSS_ZONE_AUTHORIZATION_REQUIRED`; a `cross_zone_authorization` row (and a `CROSS_ZONE_AUTHORIZED` event) records it, and the
appointment keeps its id. The authorization bypasses **only** the zone-membership rule; account status, the scheduling flag,
working hours, leave, overlap/buffer, the daily limit, existing assignments, hold re-homing, the locks
(`technician_profile → property_site → operational_zone FOR SHARE → dispatch_request → dispatch_proposal`) and the exclusion
constraint all still apply. No travel time, distance or automatic cross-zone choice exists.

Flow 2 (installation) uses the same rule: `GET /api/installation-projects/{projectId}/technician-candidates?startsAt=&endsAt=` lists zone members first, then every other
technician with `crossZone = true`; assigning one needs `crossZoneAuthorized = true` + `reason` in the `PUT /api/installation-projects/{projectId}/assignment` body (else
`409 CROSS_ZONE_AUTHORIZATION_REQUIRED`), recorded as a `cross_zone_authorization` row with `installation_project_id`. A project on a
commune without zone can be assigned this way; `SITE_LOCATION_REQUIRED` / `ZONE_NOT_SERVICEABLE` still refuse for installation projects
(staff-only flow, unchanged in this round).

In a zone marked **not serviceable**, survey dispatch treats every technician — members included — as needing the explicit
approval (`crossZone = true` in the candidates list, `crossZoneAuthorized` + `reason` on assign/propose); all other rules apply.

## 6. API

All responses use the `ApiResponse` envelope; every action has the per-user `scheduling-read` / `scheduling-write` rate limit.
Times are ISO 8601 with offset; the appointment end is start + `Scheduling:AppointmentDurationMinutes`.

### Customer (`Customer` role; 404 for survey requests of other customers)

| Method & path | Body | Result |
|---|---|---|
| `POST /api/customers/me/survey-requests/{id}/dispatch-request` | `{ preferredWindows?: [{startsAt, endsAt}] (≤ 5, future, ≤ 24 h each, inside the booking horizon), note? (≤ 1000) }` | 201 new, 200 when an active one exists (`created: false`) |
| `GET …/dispatch-request` | — | newest request: status, reason, `reviewDueAt`, windows, `liveProposal`, `latestProposal`, resolution, `appointmentId` |
| `POST …/dispatch-request/proposals/{pid}/accept` | `{ expectedRevision? }` (proposal revision) | 200 (`status: ASSIGNED`); repeat 200 |
| `POST …/dispatch-request/proposals/{pid}/decline` | `{ reason?, expectedRevision? }` | 200 (`status: OPEN`) |
| `POST …/dispatch-request/cancel` | `{ reason? }` | 200 (`CLOSED`/`CANCELLED_BY_CUSTOMER`); repeat 200 |

Create refusals: `422 SITE_LOCATION_REQUIRED` (with `dispatchAvailable: false`; a not-serviceable zone is accepted),
`409 DISPATCH_NOT_NEEDED` (covered and bookable at the wanted times — or no windows given — book a slot instead; a covered site
whose every preferred window is taken gets reason `SCHEDULE_CONFLICT`), `409 SURVEY_BOOKING_ALREADY_EXISTS`, `409 INVALID_BOOKING_STATE`,
`400 SCHEDULING_VALIDATION_FAILED`.

```json
POST /api/customers/me/survey-requests/7b…/dispatch-request
{ "preferredWindows": [ { "startsAt": "2026-10-21T08:00:00+07:00", "endsAt": "2026-10-21T12:00:00+07:00" } ], "note": "Mornings please" }
→ 201 { "data": { "dispatchRequestId": "…", "status": "OPEN", "reason": "NO_ZONE_COVERAGE", "reviewDueAt": "2026-10-19T10:00:00+07:00",
        "liveProposal": null, "revision": 0, "created": true, … } }
```

Acceptance re-checks everything under the reservation locks — technician eligibility and capacity, conflicts, coverage, and the
**current** booking limits (`scheduling.minimumBookingLeadMinutes`, horizon): a proposal accepted too close to its start fails
with `400 INVALID_BOOKING_TIME` (proposal `FAILED`, request `OPEN` again). Accept failure (the proposal could not be honoured any more):

```json
409 { "error": { "code": "TECHNICIAN_SCHEDULE_CONFLICT", "message": "… our staff will send you another one.",
      "details": { "proposalStatus": "FAILED", "dispatchStatus": "OPEN" } } }
```

Accepting an expired proposal → `409 PROPOSAL_EXPIRED` (it is marked `EXPIRED` right away if the worker has not yet).

### Manager / Admin (`api/admin/dispatch-requests`, roles Manager, Admin)

| Method & path | Body / query | Result |
|---|---|---|
| `GET /api/admin/dispatch-requests` | `?status=&reason=&overdue=&page=1&pageSize=20` | page of items with `sla { reviewSlaMinutes, reviewDueAt, escalationDueAt, overdue, overdueAt, escalated, escalatedAt, reminderCount, … }` and `liveProposal`, oldest review deadline first |
| `GET {id}` | — | item + windows, all proposals, cross-zone authorizations, events, resolution, `currentCoverageStatus` |
| `GET {id}/candidates` | `?startsAt=` | zone members first, then other technicians `crossZone: true`; each with `isEligible`, `reason`, `requiresHoldRehoming`, `zones`, `commitmentsThatDay`, `maxAppointmentsPerDay`, `workingIntervals` (known facts only) |
| `POST {id}/assign` | `{ technicianUserId, startsAt, crossZoneAuthorized?, reason (required), expectedRevision (required) }` | 200; appointment `ASSIGNED`, `MANAGER_DISPATCH`, survey request `Scheduled`, request `ASSIGNED` |
| `POST {id}/proposals` | `{ technicianUserId, startsAt, crossZoneAuthorized?, reason? (required when cross-zone), note?, expectedRevision }` | 201; eligibility validated now, nothing reserved, request `PROPOSAL_PENDING` |
| `POST {id}/proposals/{pid}/withdraw` | `{ reason? }` | 200, request `OPEN` |
| `POST {id}/unserviceable` | `{ reason (required), expectedRevision? }` | 200 |
| `POST {id}/close` | `{ reason (required), expectedRevision? }` | 200 |

```json
POST /api/admin/dispatch-requests/5c…/proposals
{ "technicianUserId": "…", "startsAt": "2026-10-21T09:00:00+07:00", "crossZoneAuthorized": true,
  "reason": "No zone serves Sài Gòn yet", "expectedRevision": 0 }
→ 201 { "data": { "request": { "status": "PROPOSAL_PENDING", "revision": 1, … },
        "proposals": [ { "status": "PROPOSED", "crossZone": true, "expiresAt": "2026-10-20T06:00:00+07:00", "responseWindowMinutes": 1440, … } ], … } }
```

Assignment and acceptance book through the same reservation path as every survey booking (technician, site and zone locks,
eligibility re-checked under the locks, own pending holds re-homed, exclusion constraint). A concurrent second assignment loses with
`DISPATCH_INVALID_STATE` / `DISPATCH_STALE_REVISION` / `SURVEY_BOOKING_ALREADY_EXISTS`; a double accept books once and every caller
gets 200 (at worst a retryable `APPOINTMENT_CONCURRENCY_CONFLICT`).

### Error codes

| Code | HTTP | When |
|---|---|---|
| `SCHEDULING_VALIDATION_FAILED`, `INVALID_BOOKING_TIME`, `ASSIGNMENT_REASON_REQUIRED`, `DISPATCH_REASON_REQUIRED` | 400 | invalid fields, start not bookable (past, inside the minimum lead, outside the horizon), missing reason |
| `DISPATCH_REQUEST_NOT_FOUND`, `DISPATCH_PROPOSAL_NOT_FOUND`, `SURVEY_REQUEST_NOT_FOUND`, `TECHNICIAN_NOT_FOUND` | 404 | unknown or not owned |
| `SITE_LOCATION_REQUIRED`, `ADMINISTRATIVE_LOCATION_UNKNOWN` / `_SUPERSEDED` / `_OUT_OF_SERVICE_AREA` | 422 | invalid or out-of-scope location: not dispatchable |
| `NO_ZONE_COVERAGE`, `ZONE_NOT_SERVICEABLE`, `NO_ZONE_TECHNICIAN`, `NO_AVAILABLE_TECHNICIAN`, `SCHEDULE_CONFLICT` | 409 | temporarily uncovered: booking refused, dispatch available (`dispatchAvailable: true`) |
| `DISPATCH_REQUEST_OPEN` | 409 | booking while a dispatch request is open |
| `DISPATCH_NOT_NEEDED` | 409 | the site is bookable at the wanted times |
| `DISPATCH_INVALID_STATE`, `DISPATCH_STALE_REVISION` | 409 | terminal request / stale `expectedRevision` (`details.currentRevision`) |
| `PROPOSAL_ALREADY_PENDING`, `PROPOSAL_NOT_PENDING`, `PROPOSAL_EXPIRED` | 409 | proposal state |
| `CROSS_ZONE_AUTHORIZATION_REQUIRED` | 409 | cross-zone technician without `crossZoneAuthorized = true` + reason (dispatch and Flow 2) |
| eligibility codes (`TECHNICIAN_ON_LEAVE`, `TECHNICIAN_SCHEDULE_CONFLICT`, `OUTSIDE_WORKING_HOURS`, `DAILY_LIMIT_REACHED`, `TECHNICIAN_SCHEDULING_DISABLED`, `TECHNICIAN_INACTIVE`) | 409 | the technician cannot take the time |

## 7. Settings and non-retroactivity

`dispatch.reviewSlaMinutes`, `dispatch.escalationDelayMinutes`, `dispatch.reminderIntervalMinutes` are snapshotted on the request at
creation, `dispatch.proposalResponseWindowMinutes` on the proposal at creation (with `settings_version`). An Admin update never moves
an existing deadline; a new request or a new proposal on an old request uses the current values. See `guides/system-settings.md`.

## 8. Frontend checklist

- [ ] **Uncovered-area request** (customer): read `coverageStatus` / `dispatchAvailable` from slots; when there is no usable slot and
      `dispatchAvailable`, offer "Request a technician" (up to 5 preferred windows + note) → `POST …/dispatch-request`; handle 201/200
      (show the existing request), `409 DISPATCH_NOT_NEEDED` (go back to slots), 422 (fix the location). On booking errors with
      `error.details.dispatchAvailable = true`, offer the same. Show `reviewDueAt` as "we will get back to you by …".
- [ ] **Customer request page**: `GET …/dispatch-request`; show status, the live proposal (technician name, time, `expiresAt`
      countdown) with Accept / Decline, `latestProposal` outcome (DECLINED/EXPIRED/FAILED/WITHDRAWN), Cancel request; after ASSIGNED
      show the appointment from `GET /api/customers/me/survey-requests/{id}`.
- [ ] **Accept/decline**: send the proposal `revision`; on 409 with `details.proposalStatus = FAILED` explain that a new proposal will
      follow; on `PROPOSAL_EXPIRED` refresh.
- [ ] **Manager dispatch queue**: list with filters (status, reason, overdue), sort by `sla.reviewDueAt`; SLA indicator (time left,
      red when `sla.overdue`, badge when `sla.escalated`); detail page with windows, history, proposals and authorizations.
- [ ] **Candidates**: pick a start, `GET …/candidates?startsAt=`; show zone members first, then a "Cross-zone" section; show
      reasons and the known facts only (no distance/travel).
- [ ] **Cross-zone approval**: choosing a cross-zone candidate opens a confirmation with a mandatory reason → send
      `crossZoneAuthorized: true` + `reason`; handle `409 CROSS_ZONE_AUTHORIZATION_REQUIRED`. Same dialog for installation
      candidates with `crossZone: true`.
- [ ] **Assign / propose**: send `expectedRevision`; handle `PROPOSAL_ALREADY_PENDING` (offer withdraw), `DISPATCH_STALE_REVISION`
      (reload), eligibility codes; `unserviceable` / `close` require a reason.
