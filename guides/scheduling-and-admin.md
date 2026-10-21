# SmartSolar backend — Flow 1 scheduling, admin user management and operations

This guide describes the implemented behaviour of:

- Flow 1: pre-survey booking with an internal capacity hold, technician **self-acceptance** in their operational zones,
  escalation to staff, Sales fallback and Manager override, Sales commercial ownership;
- administrative locations (official catalog) + operational zones, and technician configuration;
- administrative account management and technician provisioning;
- configuration, database changes and tests.

There is no routing provider: Vietmap and all travel-time estimation were removed. Consecutive commitments of one
technician keep a fixed `Scheduling:BufferBetweenAppointmentsMinutes` gap (default 30) — a conservative buffer, not a
travel time. Flow 1 ends when the on-site survey is completed (Installation Flow), which marks the appointment
`COMPLETED`.

All responses use the envelope `{ "isSuccess", "traceId", "data", "error": { "code", "message", "details" } }`.
Timestamps with an offset are local `Asia/Ho_Chi_Minh` (`+07:00`); `…Utc` fields and storage are UTC.

---

## 1. Frontend call sequence

| # | Role | Call |
|---|---|---|
| 0 | Operator | `dotnet SmartSolar.Api.dll admin-catalog import --dataset … --confirm-host … --confirm-database … --confirm-user … --apply` (official catalog, once per dataset) |
| 0 | Admin | `POST /api/admin/operational-zones`, then `PUT /api/admin/operational-zones/{id}/communes { communeCodes, revision }` (Admin only) |
| 0 | Admin | `POST /api/admin/users` (create TECHNICIAN/SALES/…); user opens the emailed setup link |
| 0 | Admin/Manager | `PUT /api/admin/technicians/{id}/profile`, `PUT …/zones` (Manager: only zones they are responsible for), `PUT …/working-schedule`, `POST …/unavailable-periods` |
| 1 | Anyone | `GET /api/administrative-units/provinces`, `GET /api/administrative-units/communes?provinceCode=79&q=`, `GET /api/administrative-units/legacy-lookup?q=` (address dropdowns; anonymous allowed) |
| 1 | Customer | Profile, site **with its official location** (`POST /api/customers/me/sites` accepts `provinceCode`, `communeCode`; or `PUT /api/customers/me/sites/{siteId}/administrative-location`), `POST /api/pre-surveys`, optional surface/simulation |
| 2 | Customer | `POST /api/pre-surveys/{id}/submit` → `surveyRequestId` |
| 3 | Customer | `GET /api/survey-slots?propertySiteId=…&date=YYYY-MM-DD` → always 200 for an own site and a date in the horizon: `slots`, `coverageStatus` (`COVERED` or why not) and `dispatchAvailable` |
| 3b | Customer | No usable slot and `dispatchAvailable = true` → `POST /api/customers/me/survey-requests/{id}/dispatch-request` (uncovered-zone dispatch, see `guides/dispatch-and-uncovered-zones.md`) |
| 4 | Customer | `POST /api/customers/me/survey-requests/{id}/booking` → `PENDING_ACCEPTANCE`, `acceptanceStatus = WAITING_FOR_TECHNICIAN` |
| 5 | Technician | `GET /api/technicians/me/open-appointments[/{id}]` → `POST /api/technicians/me/appointments/{id}/accept { expectedRevision? }` |
| 6 | Customer | `GET /api/customers/me/survey-requests[/{id}]` (`acceptanceStatus`), `PUT …/booking` (reschedule), `POST …/booking/cancel` |
| 7 | Sales | `GET /api/survey-requests/pending` (unclaimed) → `POST …/{id}/claim` (any time while Pending/Scheduled/Completed) → `GET …/{id}` |
| 8 | Sales | `GET /api/survey-requests/escalated`, `GET /api/technicians/availability?surveyRequestId=…`, `POST /api/survey-requests/{id}/assign-technician { technicianUserId, reason, expectedRevision? }` |
| 9 | Manager/Admin | `POST /api/admin/survey-appointments/{appointmentId}/assign-technician`, `PUT /api/admin/survey-requests/{id}/sales-owner { saleUserId, reason }` |
| 10 | Technician | `GET /api/technicians/me/appointments[/{id}]` (confirmed jobs), `POST …/appointments/{id}/release { reason }` |
| 11 | Technician | On-site survey (Installation Flow): requires ACCEPTED/ASSIGNED; completion → appointment `COMPLETED` |
| – | Dashboards | `GET /api/technicians/{id}/schedule?date=` (MANAGER, ADMIN; includes capacity holds), `GET /api/technicians/me/schedule?date=` |

`POST /api/technicians/me/appointments/{id}/acknowledge` **was removed** (acceptance confirms the job).
`PUT /api/survey-requests/{id}/reassign-technician` remains an alias of `assign-technician`.

Survey request status: `Pending` (no confirmed technician; booked or not) → `Scheduled` (appointment ACCEPTED/ASSIGNED,
`scheduledAt` = start) → `Completed` (on-site survey done); `Cancelled`. Claiming changes **only** `assignedSaleId`
(commercial owner); release, cancellation or a reschedule needing re-acceptance return the request to `Pending`.
The numeric API values stay: Pending 1, Scheduled 4, Completed 5, Cancelled 6 (2 Assigned / 3 Reviewing no longer exist).

---

## 2. Admin user management (`/api/admin/users`, ADMIN only)

MANAGER has no identity administration, so only ADMIN can grant or revoke ADMIN. Responses never
contain password hashes, refresh tokens, verification/reset/setup tokens or links.

| Method | Route | Purpose |
|---|---|---|
| GET | `/api/admin/users?page=1&pageSize=20&search=&role=&status=&sort=createdAt\|name\|status&direction=asc\|desc&includeDeleted=false` | Paged list (paging and filters run in the database; max page size 100) |
| GET | `/api/admin/users/{id}` | Detail, roles, and a safe technician profile summary |
| POST | `/api/admin/users` | Create an account (no password field) |
| PUT | `/api/admin/users/{id}` | Update full name, phone, optionally email |
| PATCH | `/api/admin/users/{id}/status` | `ACTIVE` / `SUSPENDED` / `DISABLED` |
| PUT | `/api/admin/users/{id}/roles` | Replace the role set (validated against `RoleCodes`) |
| DELETE | `/api/admin/users/{id}?reason=` | Soft delete (idempotent, 204) |
| POST | `/api/admin/users/{id}/send-password-reset` | Email the right one-time link (202) |

Rate limits (per admin): `admin-users-read` 60/min, `admin-users-write` 20/min, `admin-users-recovery` 5 per 10 min.

### Create

```http
POST /api/admin/users
{ "email": "technician@example.com", "fullName": "Nguyen Van A", "phone": "0900000000", "roles": ["TECHNICIAN"] }
```
```json
{ "isSuccess": true, "traceId": "00-…", "error": null,
  "data": {
    "user": { "id": "…", "email": "technician@example.com", "fullName": "Nguyen Van A", "phone": "0900000000",
              "status": "PENDING_VERIFICATION", "roles": ["TECHNICIAN"], "emailVerifiedAt": null, "lastLoginAt": null,
              "createdAt": "…", "updatedAt": "…", "deletedAt": null, "hasPassword": false,
              "technician": { "hasProfile": false, "isSchedulingActive": false, "maxAppointmentsPerDay": null,
                              "hasCustomWorkingSchedule": false, "zoneCodes": [] } },
    "activation": { "status": "PENDING_ACCOUNT_SETUP", "emailDelivery": "QUEUED",
                    "setupLinkExpiresAt": "…", "message": "A one-time link was queued for delivery to the account's email address." } } }
```

- The email is normalised with `EmailNormalizer`; uniqueness is enforced by the unique index (race → `409 AUTH_EMAIL_ALREADY_EXISTS`).
- No password is generated, stored or returned. The account is `PENDING_VERIFICATION` with no password.
- An `ACCOUNT_SETUP` action token (hash only, default 72 h, `Auth:AccountSetup:LifetimeMinutes`) is emailed through the
  existing broker (`AccountSetupRequestedEvent` → queue `account-setup`).
- `emailDelivery`: `QUEUED`, `EMAIL_DELIVERY_DISABLED` (RabbitMQ or SMTP disabled: the link was NOT sent and
  the account stays pending), or `PUBLISH_FAILED`. Nothing is bypassed in either case.
- `UserRole.AssignedBy` records the admin; `user_account_audit` records `ACCOUNT_CREATED` and `ACCOUNT_SETUP_LINK_ISSUED`.

### Activation (technician provisioning)

1. ADMIN creates the account with role `TECHNICIAN`.
2. The user opens the link (`Frontend:AccountSetupUrl`, defaulting to `Frontend:PasswordResetUrl`) and the page calls
   `POST /api/auth/reset-password { "token": "…", "newPassword": "…" }`. The reset endpoint accepts setup tokens: the
   password is set, the email becomes verified (the link proves ownership), and the status becomes `ACTIVE`. Single use.
3. ADMIN/MANAGER configures `PUT /api/admin/technicians/{id}/profile` `{ isSchedulingActive, maxAppointmentsPerDay? }`
   and `PUT /api/admin/technicians/{id}/zones { zoneIds: [] }` (there is no base location or departure time:
   scheduling uses operational zones and a fixed buffer, never coordinates).
4. Optionally `PUT /api/admin/technicians/{id}/working-schedule`.
5. The technician is bookable only when the account is ACTIVE and not deleted, holds TECHNICIAN, the profile has
   `isSchedulingActive`, they have an active membership in the active zone of the site's commune, and their schedule has
   working time that day. Every scheduling query enforces all of this.

### Status lifecycle

| From → To | Rule |
|---|---|
| any → `PENDING_VERIFICATION` | refused (`ADMIN_INVALID_STATUS_TRANSITION`) |
| any → `ACTIVE` | only when the email is verified (otherwise send the setup/verification link) |
| `ACTIVE` → `SUSPENDED` | allowed (only ACTIVE can be suspended) |
| any → `DISABLED` | allowed |
| same status | idempotent no-op |

Leaving `ACTIVE` (and soft delete) is refused with `409 ACCOUNT_HAS_ACTIVE_WORK` when the account still holds
upcoming technician work (confirmed ACCEPTED/ASSIGNED appointments, pending capacity holds, or — with Installation
Flow — current installation assignments, status `INSTALLATION_ASSIGNMENT`) or survey requests it owns as Sales in `Pending` or `Scheduled`. `details` lists them, and
nothing is cancelled automatically. Disable the technician's scheduling first (pending holds then move to other
technicians automatically), reassign confirmed appointments, and move owned requests with
`PUT /api/admin/survey-requests/{id}/sales-owner`. Removing the last
active ADMIN → `409 ADMIN_LAST_ACTIVE_ADMIN`; admins cannot change their own status or delete themselves
(`409 ADMIN_CANNOT_MODIFY_OWN_ACCOUNT`).

### Roles

`PUT /api/admin/users/{id}/roles { "roles": ["TECHNICIAN", "SALES"] }` replaces the set (multiple roles supported).
Unknown codes → `400 ADMIN_INVALID_ROLE`. Removing TECHNICIAN or SALES from an active account with live work, or
ADMIN from the last active admin, is refused as above. `role` reference data is never modified.

### Token invalidation

`user_account.auth_version` is incremented on every status change, role change, email change and soft delete. Access
tokens carry it as claim `av`. On **every authenticated request** `JwtBearerEvents.OnTokenValidated` reads the account
by primary key and rejects (401) unless it exists, is ACTIVE, is not deleted and `av` matches. Tokens without `av`
(issued before this release) count as version 0. Deactivation and email changes also revoke refresh tokens. After a
role change the refresh token keeps working but mints tokens with the new roles only. No cache is used, so revocation
is immediate on every instance (cost: one indexed read per authenticated request).

### Update, delete, recovery

- `PUT` changes `fullName`, `phone`; an `email` change clears `emailVerifiedAt`, bumps `auth_version`, revokes refresh
  tokens and sends a verification link (or a setup link if no password exists yet) to the new address.
- `DELETE` is a soft delete: `deleted_at` set, status `DISABLED`, version bumped, refresh tokens revoked, unused action
  tokens removed. All rows (roles, appointments, survey requests, audit) stay for history and foreign keys. Deleted
  accounts cannot sign in, never receive work, and are hidden from lists unless `includeDeleted=true`. The email stays
  reserved by the unique index.
- `send-password-reset` chooses: never set a password → new setup link; password set but email unproven → verification
  link; ACTIVE → password reset link (existing reset flow). Suspended/disabled/deleted → `409 ADMIN_RECOVERY_NOT_ALLOWED`.
  The admin never sees a password, token or link.

| Code | HTTP |
|---|---|
| `AUTH_VALIDATION_FAILED`, `ADMIN_INVALID_ROLE` | 400 |
| `ADMIN_USER_NOT_FOUND` | 404 |
| `AUTH_EMAIL_ALREADY_EXISTS`, `ADMIN_INVALID_STATUS_TRANSITION`, `ADMIN_LAST_ACTIVE_ADMIN`, `ADMIN_CANNOT_MODIFY_OWN_ACCOUNT`, `ACCOUNT_HAS_ACTIVE_WORK`, `ADMIN_USER_DELETED`, `ADMIN_RECOVERY_NOT_ALLOWED` | 409 |

---

## 3. Administrative locations, operational zones and technician configuration

### Administrative locations and operational zones

Coverage no longer uses the 3-level service-area model (`service_area`, dotted codes `79.769.26794`); it was replaced by
the **official administrative catalog** (province + commune after the 2025 reorganisation, there is no district level) and
internal **operational zones**. Full model, import process, API contracts and the frontend checklist:
[administrative-locations-and-zones.md](administrative-locations-and-zones.md). In short:

- A site stores `provinceCode` (2 digits) and `communeCode` (5 digits) — active official catalog rows that belong together
  (`422 ADMINISTRATIVE_LOCATION_UNKNOWN` / `ADMINISTRATIVE_LOCATION_SUPERSEDED`). The customer sets them on
  `POST /api/customers/me/sites` or `PUT /api/customers/me/sites/{siteId}/administrative-location { provinceCode, communeCode }`.
- Coverage: site → `commune_code` → its ACTIVE mapping in `operational_zone_commune` → the zone (must be active). A commune
  belongs to at most one zone at a time (history kept). Eligibility requires an active membership in that zone
  (`TECHNICIAN_OUTSIDE_ZONE`); a technician may belong to several zones.
- Codes: `SITE_LOCATION_REQUIRED` (replaces `SITE_AREA_REQUIRED`), `NO_ZONE_COVERAGE` (replaces `AREA_NOT_COVERED`),
  `ZONE_NOT_SERVICEABLE` — `SITE_LOCATION_REQUIRED` is 422 everywhere; `NO_ZONE_COVERAGE` / `ZONE_NOT_SERVICEABLE` are 409
  + dispatch on customer booking (temporarily uncovered) and 422 for installation; `NO_ZONE_TECHNICIAN`,
  `NO_AVAILABLE_TECHNICIAN`, `SCHEDULE_CONFLICT` come from the shared coverage evaluation (`ZoneCoverageService`).
- The appointment (and installation project) records the zone in `operational_zone_id`. Changing the site location is refused
  with `409 SITE_HAS_ACTIVE_BOOKING` while an open or confirmed appointment that records a zone exists (open appointments whose
  start passed are ignored), and with `409 SITE_HAS_ACTIVE_INSTALLATION` while an installation project of the site is
  unfinished. Re-sending the current location is a no-op.
- Zones are managed with `/api/admin/operational-zones*` (ADMIN, MANAGER): deactivating a zone or marking it not serviceable
  is refused with `409 ZONE_IN_USE` while unfinished work records it (same rule as the former `SERVICE_AREA_IN_USE`); a
  commune of another active zone → `409 COMMUNE_ALREADY_MAPPED`; removing/moving a commune whose sites have unfinished work →
  `409 COMMUNE_IN_USE` (confirmed work is never silently reassigned).

**Legacy bookings (migration note).** Appointments created before zones exist have `operational_zone_id` NULL and their sites
usually carry no commune code. While a site has no valid commune, accept, Sales assignment, Manager override and reschedule
answer `422 SITE_LOCATION_REQUIRED` (self-acceptance on a site outside the technician's zones stays `404
APPOINTMENT_NOT_FOUND`). The customer can still set the location when **every** active appointment of the site has
`operational_zone_id` NULL: in the same transaction, under the site lock, those appointments get the active zone now mapping
the commune (their revision is bumped) and can be accepted/assigned normally afterwards (rule M1). As soon as one active
appointment records a zone, location changes are refused again with `409 SITE_HAS_ACTIVE_BOOKING`.

All `/api/admin/operational-zones*`, `/api/admin/technicians*`, `PUT /api/customers/me/sites/{id}/administrative-location`, `PUT /api/admin/survey-requests/{id}/sales-owner`,
`POST /api/admin/survey-appointments/{id}/assign-technician` and the Sales queue `GET /api/survey-requests/escalated` endpoints are rate limited per user: reads with `scheduling-read`
(`RateLimiting:SchedulingRead`, 20/min), writes with `scheduling-write` (`RateLimiting:SchedulingWrite`, 10/min).

### Technicians (`/api/admin/technicians`, ADMIN and MANAGER)

| Method | Route |
|---|---|
| GET | `/api/admin/technicians[?zoneId=]` (each item lists `zones`) |
| PUT | `/api/admin/technicians/{id}/profile` `{ isSchedulingActive, maxAppointmentsPerDay }` |
| PUT | `/api/admin/technicians/{id}/zones` `{ zoneIds: [] }` (replaces the set; removed memberships are soft-removed with who/when; unknown zone → `404 ZONE_NOT_FOUND`; profile required → `409 TECHNICIAN_PROFILE_REQUIRED`) |
| GET / PUT | `/api/admin/technicians/{id}/working-schedule` |
| GET / POST | `/api/admin/technicians/{id}/unavailable-periods` |
| DELETE | `/api/admin/technicians/{id}/unavailable-periods/{periodId}` |

Base location, base label and earliest base departure no longer exist (no travel rule).

**Changes that strand confirmed work are refused** with `409 TECHNICIAN_HAS_CONFLICTING_APPOINTMENTS` and
`details.conflictingAppointments`: removing the zone of a future ACCEPTED/ASSIGNED appointment
(`TECHNICIAN_OUTSIDE_ZONE`), disabling scheduling with any (`TECHNICIAN_SCHEDULING_DISABLED`; also refused with upcoming
installation assignments, status `INSTALLATION_ASSIGNMENT`), leave over one (`TECHNICIAN_ON_LEAVE`), a weekly schedule that leaves one outside
working hours (`OUTSIDE_WORKING_HOURS`). Installation assignments are never moved automatically: leave over one, a zone change
that no longer covers its site, or a weekly schedule that no longer covers its days is refused the same way. **Pending capacity holds** made impossible by the change are moved, in the same
transaction, to another eligible technician (event `HOLD_MOVED`), or the appointment escalates without a holder.
The zones response lists `rehomedHolds`. Lowering `maxAppointmentsPerDay` re-homes the latest pending holds of every
day above the new cap; a day whose confirmed (ACCEPTED/ASSIGNED/COMPLETED) appointments alone exceed it refuses the change
(`DAILY_LIMIT_REACHED`). Each change locks the technician and every technician who could take over their holds; if the
technician's open holds changed between the first read and the lock (a booking committed meanwhile), the transaction is
rolled back and retried with the new candidates locked (bounded by `Scheduling:MaxReservationAttempts`).

### Weekly working schedule

```http
PUT /api/admin/technicians/{id}/working-schedule
{ "useDefault": false,
  "periods": [ { "day": "MONDAY", "start": "08:00", "end": "12:00" }, { "day": "MONDAY", "start": "13:00", "end": "17:00" } ] }
```

Local time; the list is the full week (days without periods are days off); periods per day must not overlap (400);
`useDefault: true` returns to `Scheduling:WorkingHours` on `Scheduling:WorkingDays`. Slots are the union of the zone
technicians' grids; each appointment must fit inside one working period of the technician holding or taking it.

---

## 4. Eligibility, capacity and the acceptance lifecycle

### Technician eligibility (shared `TechnicianEligibility`, also used by installation planning)

In order, a technician may take an interval when: account ACTIVE with the TECHNICIAN role (`TECHNICIAN_INACTIVE`);
`is_scheduling_active` (`TECHNICIAN_SCHEDULING_DISABLED`); an active membership in the site's active zone
(`TECHNICIAN_OUTSIDE_ZONE`); inside one working period (`OUTSIDE_WORKING_HOURS`); no leave overlap
(`TECHNICIAN_ON_LEAVE`); every other commitment — survey appointments they hold or are confirmed on, and current (PENDING/ACCEPTED)
installation assignments when Installation Flow is enabled — satisfies `prevEnd + buffer <= start` and `end + buffer <= next.start`
(`TECHNICIAN_SCHEDULE_CONFLICT`); fewer than the daily limit of survey appointments, holds included
(`DAILY_LIMIT_REACHED`; installation work does not count). These codes are returned by acceptance/assignment (409) and as
`reason` on availability and open-job views.

### Capacity: per-technician hold, re-homed on acceptance

Booking picks a **capacity holder** among the eligible technicians of the area (fewest survey commitments that day, then
id) and inserts `PENDING_ACCEPTANCE` with `reserved_technician_user_id` = holder and
`acceptance_deadline_at = LEAST(booked_at + scheduling.acceptanceWindowMinutes, starts_at − scheduling.acceptanceMinimumLeadMinutes)`
(admin-editable system settings read at booking time; the deadline is stored with `acceptance_window_minutes` and
`settings_version` and never moves when the settings change later — see `guides/system-settings.md`).
Slots are offered only where at least one holder exists. Every pending appointment therefore keeps one real technician's
time and the PostgreSQL exclusion constraint on the holder still guarantees no overbooking. The holder is internal: never
shown to customers, never an acceptance.

**Accept** (`POST /api/technicians/me/appointments/{id}/accept`): in one transaction the profiles of the accepting
technician, the current holder and every technician who could take over the accepter's conflicting holds are locked
(sorted); the appointment is re-read (open, future, same revision); the accepter is checked ignoring their own pending
holds; each conflicting hold of the accepter (and, if the daily limit requires it, further holds of that day) is moved
to another eligible technician — the previous holder included, since the appointment leaves them. If any cannot be
moved → `409 TECHNICIAN_SCHEDULE_CONFLICT` and nothing changes. Then a revision-checked update sets `ACCEPTED`,
holder = assigned = accepter, `accepted_at`, `assigned_at`, `assignment_source = SELF_ACCEPTED`; the survey request
becomes `Scheduled`. Of concurrent accepts/assignments exactly one wins; the loser gets `409 APPOINTMENT_ALREADY_TAKEN`,
the same technician repeating gets `200` with `changed: false`. A stale `expectedRevision` → `409 APPOINTMENT_CONCURRENCY_CONFLICT`.

### Statuses

| Status | Meaning | Customer `acceptanceStatus` |
|---|---|---|
| `PENDING_ACCEPTANCE` | Booked, capacity held, nobody accepted | `WAITING_FOR_TECHNICIAN` (or `NEEDS_STAFF` once past the deadline) |
| `ESCALATED` | Deadline passed or no holder could be kept; staff should act; still acceptable | `NEEDS_STAFF` |
| `ACCEPTED` | Self-accepted (`SELF_ACCEPTED`) | `CONFIRMED` (technician name shown) |
| `ASSIGNED` | Staff-assigned (`SALES_ASSIGNED` / `MANAGER_OVERRIDE`, reason required) or booked directly from an uncovered-zone dispatch request (`MANAGER_DISPATCH`: Manager/Admin assignment or an accepted proposal; `cross_zone_authorization_id` set for a cross-zone technician) | `CONFIRMED` |
| `COMPLETED` | On-site survey completed | `COMPLETED` |
| `CANCELLED` | Cancelled, replaced by a reschedule (`RESCHEDULED`, `RESCHEDULED_BY_SALES`) or never accepted before its start (`NOT_ACCEPTED_BEFORE_START`) | `CANCELLED` (also shown for a `PENDING_ACCEPTANCE`/`ESCALATED` appointment whose start passed, before the worker cancels it) |

### Escalation and the staff queue

`AppointmentEscalationWorker` (every `Scheduling:EscalationPollSeconds`, disable with `Scheduling:EscalationWorkerEnabled`)
runs three steps per tick (batches of 50):

1. **Auto-cancel.** An open (`PENDING_ACCEPTANCE`/`ESCALATED`) appointment whose `starts_at` passed can no longer be accepted,
   assigned, rescheduled or cancelled, and would block a new booking of its request forever (one active appointment per
   request). It is cancelled with a conditional update (`WHERE status IN (PENDING_ACCEPTANCE, ESCALATED) AND starts_at <= now
   AND revision = read revision`), `cancellation_reason = NOT_ACCEPTED_BEFORE_START`, `cancelled_at`, a `CANCELLED` event
   (reason `NOT_ACCEPTED_BEFORE_START`, no actor; there is no event-type check constraint, but the existing type is kept for
   clients) and a `scheduling_notification` for the Sales owner. The capacity hold is released (cancelled rows never count;
   the holder stays on the row as history) and the survey request returns to `Pending` (`scheduled_at` NULL). Concurrent
   workers cancel each appointment exactly once; an acceptance cannot race it because acceptance requires a future start
   (re-checked inside the acceptance transaction). A customer booking the same request again before the worker ran cancels
   the dead appointment the same way first, so rebooking never waits for the sweep.
2. **Escalation.** `PENDING_ACCEPTANCE` rows past their deadline **that have not started** move to `ESCALATED` (conditional
   update), with an `ESCALATED` event and a notification for the Sales owner. Started ones are left to step 1.
3. **Acceptance reminder.** When `scheduling.acceptanceReminderLeadMinutes` (system setting, read every tick; **0 = disabled**,
   the default) is > 0, each `PENDING_ACCEPTANCE` appointment whose PERSISTED `acceptance_deadline_at` is within that lead (and
   still in the future) gets ONE reminder: a conditional update sets `acceptance_reminder_sent_at` (only while it is NULL; the
   revision is not bumped), then an `ACCEPTANCE_REMINDER` event and a `scheduling_notification` for the capacity holder. The deadline
   itself is never recomputed; a release opens a new window and resets the marker.

One failing appointment is logged by id (and exception type only) and skipped; the rest of the batch continues and the next
tick retries it. Queries do not depend on the worker: `GET /api/survey-requests/escalated` (Sales, `scheduling-read`) lists
`ESCALATED` and pending-past-deadline **future** appointments (owned and unowned, `ownedByMe`), with the area and
`hasEligibleTechnician`; the technician open-appointment list shows future ones only; the customer sees a started open
appointment as `CANCELLED`; area deactivation and technician conflict lists ignore started open holds. Both list views load
site coverage, memberships and technician days in batches, so their query count does not grow with the number of rows.

### Sales fallback and Manager override

`POST /api/survey-requests/{id}/assign-technician { technicianUserId, reason (≤500, required), expectedRevision? }`: only the
commercial owner (`403 SURVEY_REQUEST_NOT_OWNED` otherwise — claim first; the owner is re-checked inside the assignment
transaction, after the request row is write-locked, so an owner change between the check and the write also ends in `403`
with nothing changed); allowed for PENDING_ACCEPTANCE, ESCALATED,
ACCEPTED or ASSIGNED appointments that have not started (`409 INVALID_BOOKING_STATE` otherwise). Same eligibility, hold
re-homing and revision-checked update; result `ASSIGNED` / `SALES_ASSIGNED`. Replacing a confirmed technician writes
`REASSIGNED` with the old and new technician and notifies both. Manager/Admin: `POST /api/admin/survey-appointments/{appointmentId}/assign-technician`
(same body) bypasses ownership, source `MANAGER_OVERRIDE`. `GET /api/technicians/availability?surveyRequestId=` (owner or
Manager/Admin) lists `technicians` (eligible; `requiresHoldRehoming` when only their own pending holds are in the way) and
`ineligible` members of the covering areas with reason codes.

### Release

`POST /api/technicians/me/appointments/{id}/release { reason }` (confirmed technician, until `onsiteSurvey.earlyStartMinutes`
(system setting, default `OnSiteSurvey:EarlyStartMinutes` = 120) before the start — from the earliest on-site survey start onwards `409 INVALID_BOOKING_STATE`, and always
`409 ONSITE_SURVEY_IN_PROGRESS` once the on-site survey is `IN_PROGRESS`, checked again under the technician lock that starting
the survey also takes; after that cut-off only staff can reassign, and only before the survey started): the appointment
opens again with a fresh acceptance deadline
`acceptance_deadline_at = LEAST(now + scheduling.acceptanceWindowMinutes, starts_at − scheduling.acceptanceMinimumLeadMinutes)` — a NEW
timing event, so it uses the settings current at the release and stores their snapshot; the acceptance reminder marker is reset — and the hold moved
to another eligible technician. It is `PENDING_ACCEPTANCE` while that deadline is in the future and `ESCALATED` (with
`escalated_at`) when it is already ≤ now — i.e. inside the minimum lead before the start — even if a new holder was found;
with no eligible holder it escalates without one. The survey request returns to `Pending`; event `RELEASED` (plus
`ESCALATED`), Sales owner notified.

### Customer changes

Reschedule and cancel are allowed in PENDING_ACCEPTANCE, ESCALATED, ACCEPTED and ASSIGNED until
`scheduling.customerChangeLeadMinutes` (system setting, default `Scheduling:CustomerChangeLeadMinutes` = 120) before the start (`409 INVALID_BOOKING_STATE` after). A reschedule
cancels the row and inserts a replacement (`replaced_by_appointment_id`): a confirmed technician who is still eligible at the
new time keeps the job (same status and source); otherwise the new row is `PENDING_ACCEPTANCE` with a new holder and needs
re-acceptance. "Still eligible" uses the same plan as acceptance: the technician's own pending holds are ignored, and those
conflicting with the new time (or pushing the day over the limit) are handed to other eligible technicians in the same
transaction (`HOLD_MOVED`); only when that is impossible does the appointment fall back to `PENDING_ACCEPTANCE`.
Sales may reschedule owned requests with `PUT /api/survey-requests/{id}/reschedule { startsAt, reason, technicianUserId? }`
(with a technician: `ASSIGNED`, reason required, event `ASSIGNED` — or `REASSIGNED` with old/new technician when it replaces
another confirmed technician — besides `RESCHEDULED`). Cancellation of a confirmed appointment returns the request to `Pending`.
If the appointment changed only in the background while the customer's cancel/reschedule was running (a hold moved, a
technician accepted, it escalated) and it is still changeable at the same start, the change is re-read and retried
(bounded) instead of failing with `APPOINTMENT_CONCURRENCY_CONFLICT`. A Sales reschedule is retried only for hold moves or
escalation: a concurrent (re)assignment or acceptance wins and the reschedule gets `409 APPOINTMENT_CONCURRENCY_CONFLICT`.

Technicians only learn about appointments in their areas: accepting (or opening) an appointment whose site is not covered by
one of the caller's active areas returns `404 APPOINTMENT_NOT_FOUND` before any other check, whatever its state. Once the
on-site survey is `IN_PROGRESS` the appointment is frozen for everyone (`409 ONSITE_SURVEY_IN_PROGRESS`): staff cannot hand it
to another technician (assign-technician / Manager override), Sales cannot reschedule it (with or without a technician), the
customer cannot reschedule or cancel it (independently of `CustomerChangeLeadMinutes`), and its technician cannot release it.
Every path re-checks the report status inside its transaction under the confirmed technician's profile lock, which starting the
survey also takes, so a survey start and a change never both succeed. Re-confirming the same technician and a same-start
reschedule without a technician stay idempotent no-ops. Completing the on-site survey only completes the appointment while the
caller is still its confirmed technician.

### Commercial ownership

`assigned_sale_id` is the commercial owner, independent of scheduling. `POST /api/survey-requests/{id}/claim` is a conditional
first-claim-wins update while the request is Pending, Scheduled or Completed and unowned; it never changes the status.
`GET /api/survey-requests/pending` = unclaimed non-cancelled requests. Managers/Admins move ownership with
`PUT /api/admin/survey-requests/{id}/sales-owner { saleUserId (active SALES), reason }` (audited in `survey_request_event`). The
change locks the request row (PostgreSQL `FOR UPDATE`) and is a conditional update on the owner it read, so the audit event
names the owner really replaced; a concurrent claim is re-read and retried, then `409 SURVEY_REQUEST_UNAVAILABLE`.
Quotations and contracts keep requiring the owner.

### History and notifications

Events (`technician_appointment_event`): `BOOKED`, `ACCEPTED`, `ASSIGNED`, `REASSIGNED`, `HOLD_MOVED`, `RELEASED`,
`ESCALATED`, `RESCHEDULED`, `CANCELLED`. Notifications are recorded as `NOT_DELIVERED` (no delivery channel yet).

---

## 5. Dashboard (`GET /api/technicians/{id}/schedule?date=` and `/api/technicians/me/schedule?date=`)

The technician-day in time order with `potentialConflict` when `previous.end + buffer > start`. The technician view shows
confirmed jobs only; the MANAGER/ADMIN view also shows capacity holds (`isCapacityHold`). No travel values exist.

## 6. Slot capacity

`knownAvailableTechnicians` counts eligible holders of the covering areas, capped at `Scheduling:MaxReportedSlotCapacity`
(`capacityStatus` `EXACT` below the cap, `AT_LEAST` at it). Booking always re-validates under the technician lock.

## 7. Error codes (scheduling)

| Code | HTTP |
|---|---|
| `SCHEDULING_VALIDATION_FAILED`, `INVALID_BOOKING_TIME`, `ASSIGNMENT_REASON_REQUIRED` | 400 |
| `SURVEY_REQUEST_NOT_OWNED`, `SURVEY_REQUEST_NOT_ASSIGNED` | 403 |
| `SURVEY_REQUEST_NOT_FOUND`, `PROPERTY_SITE_NOT_FOUND`, `SURVEY_BOOKING_NOT_FOUND`, `APPOINTMENT_NOT_FOUND`, `TECHNICIAN_NOT_FOUND`, `ZONE_NOT_FOUND`, `DISPATCH_REQUEST_NOT_FOUND`, `DISPATCH_PROPOSAL_NOT_FOUND` | 404 |
| `SITE_LOCATION_REQUIRED`, `ADMINISTRATIVE_LOCATION_UNKNOWN`, `ADMINISTRATIVE_LOCATION_SUPERSEDED`, `ADMINISTRATIVE_LOCATION_OUT_OF_SERVICE_AREA`, `RESPONSIBLE_MANAGER_INVALID`, `SALES_USER_INVALID` | 422 |
| `NO_ZONE_COVERAGE`, `ZONE_NOT_SERVICEABLE` (scheduling: temporarily uncovered, dispatchable) | 409 |
| `ZONE_MANAGEMENT_FORBIDDEN` (zone structure is Admin-only), `ZONE_SCOPE_FORBIDDEN` (Manager outside their zones) | 403 |
| `NO_ZONE_TECHNICIAN`, `NO_AVAILABLE_TECHNICIAN`, `SCHEDULE_CONFLICT` (customer booking/reschedule without capacity; `details = { coverageStatus, dispatchAvailable }`), `DISPATCH_REQUEST_OPEN`, `DISPATCH_*` / `PROPOSAL_*` / `CROSS_ZONE_AUTHORIZATION_REQUIRED` (see the dispatch guide), `SURVEY_SLOT_UNAVAILABLE` (staff reschedule paths), `SURVEY_BOOKING_ALREADY_EXISTS`, `INVALID_BOOKING_STATE`, `APPOINTMENT_ALREADY_TAKEN`, `ONSITE_SURVEY_IN_PROGRESS`, `APPOINTMENT_CONCURRENCY_CONFLICT`, `TECHNICIAN_HAS_CONFLICTING_APPOINTMENTS`, `TECHNICIAN_PROFILE_REQUIRED`, `SITE_HAS_ACTIVE_BOOKING`, `SITE_HAS_ACTIVE_INSTALLATION`, `ZONE_CODE_EXISTS`, `ZONE_STALE_REVISION`, `ZONE_IN_USE`, `COMMUNE_ALREADY_MAPPED`, `COMMUNE_IN_USE`, eligibility codes (`TECHNICIAN_*`, `OUTSIDE_WORKING_HOURS`, `DAILY_LIMIT_REACHED`) | 409 |

---

## 8. Configuration

| Key | Default |
|---|---|
| `Scheduling:TimeZoneId` | `Asia/Ho_Chi_Minh` |
| `Scheduling:WorkingHours` / `WorkingDays` | `08:00-12:00,13:00-17:00` / `MON,…,SUN` |
| `Scheduling:AppointmentDurationMinutes`, `SlotIntervalMinutes` | 90, 30 |
| `Scheduling:BufferBetweenAppointmentsMinutes` | 30 (0–240) — **bootstrap default** of `scheduling.bufferBetweenAppointmentsMinutes` |
| `Scheduling:MaxAppointmentsPerTechnicianPerDay` | 3 |
| `Scheduling:BookingHorizonDays`, `MinimumBookingLeadMinutes` (bootstrap defaults of the system settings), `MaxReportedSlotCapacity`, `MaxReservationAttempts` | 30, 0, 3, 3 |
| `Scheduling:AcceptanceWindowMinutes`, `AcceptanceMinimumLeadMinutes`, `CustomerChangeLeadMinutes` (bootstrap defaults of the system settings) | 240, 120, 120 |
| `OnSiteSurvey:EarlyStartMinutes` (bootstrap default of `onsiteSurvey.earlyStartMinutes`: earliest on-site survey start; also the technician's release cut-off) | 120 |
| `Dispatch:WorkerEnabled`, `Dispatch:PollSeconds` (uncovered-zone dispatch sweep; infrastructure only) | true, 60 (5–3600) |
| `Scheduling:EscalationWorkerEnabled`, `EscalationPollSeconds` | true, 60 (5–3600) |
| `Auth:AccountSetup:LifetimeMinutes` | 4320 |
| `Frontend:AccountSetupUrl` | empty → `Frontend:PasswordResetUrl` |

All values are validated at startup (the API refuses to start with an out-of-range value). The business durations marked
"bootstrap default" are admin-editable at runtime (`/api/admin/system-settings/scheduling`); handlers and workers read the effective
values from `ISchedulingPolicyProvider`, never from these keys (see `guides/system-settings.md`). Removed keys:
`Scheduling:EarliestBaseDeparture`, `SafetyBufferMinutes`, `MaxRouteRequestsPerOperation`, `MaxTechniciansEvaluatedPerSlot`,
`Recalculation*`, and `Providers:Vietmap:*` / `Providers__Vietmap__*`.

ASP.NET Core does not read `.env` by itself: for `dotnet run`, run `set -a && . ./.env && set +a` first.

---

## 9. Database

The Flow 1/Flow 2 redesign is captured in the migration `20261016161547_BusinessRulesFlow1And2Redesign` (present in the
branch, not yet applied to any shared database; apply it only with explicit approval — see
[database-migration-business-rules-redesign.md](database-migration-business-rules-redesign.md)). For Flow 1 it adds
new tables `service_area`, `technician_service_area`, `survey_request_event`; new `technician_appointment` columns
(`service_area_id`, `acceptance_deadline_at`, `escalated_at`, `accepted_at`, `assignment_source`, `assignment_reason`,
`completed_at`) with `reserved_technician_user_id` made nullable and the status/assignment check constraints replaced;
`property_site.province_code`, `district_code`, `ward_code`. Existing rows are backfilled (BOOKED → PENDING_ACCEPTANCE,
ASSIGNED/ACKNOWLEDGED → ASSIGNED with `SALES_ASSIGNED`; survey requests Assigned/Reviewing → Pending). The tables
`technician_travel_estimate` and `technician_schedule_recalculation` and the columns `technician_profile.base_latitude`,
`base_longitude`, `base_label`, `earliest_base_departure` are no longer in the model but stay in the database until an
approved cleanup. The exclusion constraint `ex_technician_appointment_no_overlap` on `reserved_technician_user_id` is
unchanged (NULL holders never conflict).

**Administrative catalog + operational zones (pending migration).** The model now adds `administrative_province`,
`administrative_commune`, `administrative_legacy_commune_map`, `administrative_catalog_import`, `operational_zone`,
`operational_zone_commune`, `technician_zone_membership`, `property_site.commune_code` (composite FK
`(province_code, commune_code)` → `administrative_commune (province_code, code)`, check `ck_property_site_commune_code`),
`technician_appointment.operational_zone_id` and `installation_project.operational_zone_id`. `service_area`,
`technician_service_area`, the two `service_area_id` columns and `property_site.district_code` / `ward_code` left the EF model
but stay in the database (empty / NULL) until an approved cleanup; only the checks `ck_property_site_district_code` and
`ck_property_site_ward_code` are dropped. The migration is created by the integrator — see
[administrative-locations-and-zones.md](administrative-locations-and-zones.md#database).

---

## 10. Tests

- **Default run:** `dotnet test` uses SQLite; geography uses the REAL official catalog (seeded from the packaged
  `communes.csv` / `legacy-commune-map.csv`); zone codes are test data. No external service is called.
- **PostgreSQL concurrency:** only when `SMARTSOLAR_TEST_POSTGRES` points at a disposable local server (non-local hosts are
  refused; each test creates and drops its own database). Coverage: exclusion constraint with NULL holders, lifecycle
  check constraints, two bookings for the last technician, accept vs accept, accept vs Sales assignment, accept vs
  cancellation, accept vs reschedule, a hold re-homed while a booking targets the freed technician, Sales reschedule vs
  reassignment (exactly one wins), concurrent escalation sweeps auto-cancelling each started open appointment exactly once, disabling a technician / adding leave / removing a zone racing a booking or an
  acceptance (the technician is never left committed while ineligible), two zones racing for one commune, a commune removal or
  a zone deactivation racing a booking, and the catalog import (dry run, apply, idempotent re-apply).

```bash
SMARTSOLAR_TEST_POSTGRES="Host=127.0.0.1;Port=55433;Username=…;Password=…;Database=postgres" \
  dotnet test tests/SmartSolar.Tests --filter "FullyQualifiedName~Postgres"
```

---

## Interaction with Installation Flow assignments

Only active when `Features__InstallationWorkflowEnabled=true`; otherwise Flow 1 never queries the Installation Flow tables.

- Current (PENDING or ACCEPTED) installation assignments (`installation_assignment`, possibly multi-day) are busy intervals in
  every technician day they touch (same buffer rule) but do **not** count toward `MaxAppointmentsPerTechnicianPerDay`.
- Survey booking/acceptance/assignment and installation assignment take the same `technician_profile` row lock;
  PostgreSQL additionally rejects overlapping current installation assignments of one technician (`ex_installation_assignment_no_overlap`).
- Installation assignment uses the same eligibility rules (zone membership included, every touched day must be a working day).
  Confirmed (ACCEPTED/ASSIGNED) survey appointments of the candidate block the interval; the candidate's own pending capacity
  holds (`PENDING_ACCEPTANCE`/`ESCALATED`) overlapping the interval plus buffer are re-homed to other eligible technicians in the
  same transaction (`HOLD_MOVED`, same planner as survey acceptance), or the assignment is refused with
  `409 TECHNICIAN_SCHEDULE_CONFLICT` when one cannot be handed over.
- Disabling a technician's scheduling, deactivating / removing the TECHNICIAN role, leave over an assignment, removing the site's
  zone or a weekly schedule that no longer covers it is refused while the technician has upcoming installation assignments
  (reported with status `INSTALLATION_ASSIGNMENT`). Disabling scheduling, removing the site's zone and deactivation are also
  refused while the technician is responsible for a started, unfinished project (IN_PROGRESS, REWORK_REQUIRED,
  PENDING_CUSTOMER_ACCEPTANCE), even after its scheduled interval ended.

Details: [installation-workflow-business-flow.md](installation-workflow-business-flow.md).
