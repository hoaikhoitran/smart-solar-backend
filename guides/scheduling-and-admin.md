# SmartSolar backend — Flow 1 scheduling, admin user management and operations

This guide describes the implemented behaviour of:

- Flow 1 (pre-survey booking, Sales assignment, technician acknowledgment);
- administrative account management and technician provisioning;
- the scheduling hardening work (per-technician schedules, earliest departure, recalculation,
  Sales rescheduling and reassignment, leave conflicts, dashboard snapshots, capacity semantics);
- Vietmap Route v4 integration, configuration, migrations and tests.

Flow 1 ends when the assigned technician acknowledges the job. It never marks a survey request
Completed and implements no survey execution, quotation, construction, warranty or maintenance work.

All responses use the envelope `{ "isSuccess", "traceId", "data", "error": { "code", "message", "details" } }`.
Timestamps with an offset are local `Asia/Ho_Chi_Minh` (`+07:00`); `…Utc` fields and storage are UTC.

---

## 1. Frontend call sequence

| # | Role | Call |
|---|---|---|
| 0 | Admin | `POST /api/admin/users` (create TECHNICIAN/SALES/…); user opens the emailed setup link → `POST /api/auth/reset-password` |
| 0 | Admin/Manager | `PUT /api/admin/technicians/{id}/profile`, `PUT …/working-schedule`, `POST …/unavailable-periods` |
| 1 | Customer | Profile, site with GPS, `POST /api/pre-surveys`, optional `PUT …/surface`, optional simulation |
| 2 | Customer | `POST /api/pre-surveys/{id}/submit` → `surveyRequestId` |
| 3 | Customer | `GET /api/survey-slots?propertySiteId=…&date=YYYY-MM-DD` |
| 4 | Customer | `POST /api/customers/me/survey-requests/{id}/booking` |
| 5 | Customer | View: `GET /api/customers/me/survey-requests[/{id}]`; before assignment only: `PUT …/booking` (reschedule), `POST …/booking/cancel` |
| 6 | Sales | `GET /api/survey-requests/pending` → `POST /api/survey-requests/{id}/claim` → `GET /api/survey-requests/{id}` |
| 7 | Sales | `GET /api/technicians/availability?surveyRequestId=…[&startsAt=…&endsAt=…]` |
| 8 | Sales | `POST /api/survey-requests/{id}/assign-technician` |
| 9 | Sales | Later changes: `PUT /api/survey-requests/{id}/reschedule`, `PUT /api/survey-requests/{id}/reassign-technician` |
| 10 | Technician | `GET /api/technicians/me/appointments[/{appointmentId}]` |
| 11 | Technician | `POST /api/technicians/me/appointments/{appointmentId}/acknowledge` — **end of Flow 1** |
| – | Dashboards | `GET /api/technicians/{id}/schedule?date=` (MANAGER, ADMIN), `GET /api/technicians/me/schedule?date=` (TECHNICIAN) |

Survey request status: booking keeps `Pending`; claim → `Assigned`; official assignment → `Scheduled`
(`scheduledAt` = appointment start); acknowledgment keeps `Scheduled`; a Sales reschedule of an assigned
appointment moves `scheduledAt`.

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
              "technician": { "hasProfile": false, "isSchedulingActive": false, "hasBaseLocation": false, "baseLabel": null,
                              "maxAppointmentsPerDay": null, "hasCustomWorkingSchedule": false, "earliestBaseDeparture": null } },
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
3. ADMIN/MANAGER configures `PUT /api/admin/technicians/{id}/profile` (real base coordinates, `isSchedulingActive`,
   optional `maxAppointmentsPerDay`, optional `earliestBaseDeparture`).
4. Optionally `PUT /api/admin/technicians/{id}/working-schedule`.
5. The technician is bookable only when the account is ACTIVE and not deleted, holds TECHNICIAN, the profile is active
   with a real base location, and the schedule has working time that day. Every scheduling query enforces all of this.

### Status lifecycle

| From → To | Rule |
|---|---|
| any → `PENDING_VERIFICATION` | refused (`ADMIN_INVALID_STATUS_TRANSITION`) |
| any → `ACTIVE` | only when the email is verified (otherwise send the setup/verification link) |
| `ACTIVE` → `SUSPENDED` | allowed (only ACTIVE can be suspended) |
| any → `DISABLED` | allowed |
| same status | idempotent no-op |

Leaving `ACTIVE` (and soft delete) is refused with `409 ACCOUNT_HAS_ACTIVE_WORK` when the account still holds
upcoming technician appointments (reserved or assigned) or open survey requests it owns as Sales
(Assigned/Reviewing/Scheduled). `details` lists them, and nothing is cancelled automatically. Removing the last
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

## 3. Technician configuration (`/api/admin/technicians`, ADMIN and MANAGER)

| Method | Route |
|---|---|
| GET | `/api/admin/technicians` |
| PUT | `/api/admin/technicians/{id}/profile` `{ baseLatitude, baseLongitude, baseLabel, isSchedulingActive, maxAppointmentsPerDay, earliestBaseDeparture: "06:30" }` |
| GET / PUT | `/api/admin/technicians/{id}/working-schedule` |
| GET / POST | `/api/admin/technicians/{id}/unavailable-periods` |
| DELETE | `/api/admin/technicians/{id}/unavailable-periods/{periodId}` |

There is no vehicle selector: every technician uses Vietmap `vehicle=motorcycle` (`vehicleType: "MOTORCYCLE"`).

### Weekly working schedule

```http
PUT /api/admin/technicians/{id}/working-schedule
{ "useDefault": false,
  "periods": [ { "day": "MONDAY", "start": "08:00", "end": "12:00" }, { "day": "MONDAY", "start": "13:00", "end": "17:00" },
               { "day": "SATURDAY", "start": "08:00", "end": "12:00" } ] }
```

- Local time (`Scheduling:TimeZoneId`). The list is the full week; days without periods are days off. Several periods
  per day are allowed and must not overlap (400). `useDefault: true` returns to the default schedule.
- Default schedule: `Scheduling:WorkingHours` on `Scheduling:WorkingDays` (default every day, as in V1).
- Upcoming appointments that would fall outside the new week → `409 TECHNICIAN_HAS_CONFLICTING_APPOINTMENTS` with
  `details.conflictingAppointments` (`problem: OUTSIDE_WORKING_HOURS`).
- Slots are the union of all eligible technicians' own grids; each appointment must fit inside one working period of the
  technician who takes it.

### Earliest base departure

The first trip of a day must satisfy `RequiredDeparture >= EarliestAllowedBaseDeparture`, with
`RequiredDeparture = FirstStart − SafetyBuffer − Travel(Base → First)`. Example: 08:00 start, 90 min travel, 30 min
buffer → 06:00 required. With the default `Scheduling:EarliestBaseDeparture = 07:00` the slot is rejected
(`BEFORE_EARLIEST_BASE_DEPARTURE`); with a technician override of `06:00` it is allowed. This rule is separate from on-site
working hours. Raising the limit when a booked first trip would violate it → 409 with the affected appointments.

### Leave and disabling scheduling

- Leave that overlaps an upcoming appointment → `409 TECHNICIAN_HAS_CONFLICTING_APPOINTMENTS` (problem
  `TECHNICIAN_UNAVAILABLE_PERIOD`); resolve by Sales reassignment or reschedule first. Appointments are never cancelled
  automatically. The check uses the appointment interval `[start, end)`.
- `isSchedulingActive: false` with upcoming appointments → same 409.
- A base location change is accepted and queues a recalculation of every upcoming day (first legs start elsewhere);
  the dashboard flags any resulting conflict.

---

## 4. Booking and recalculation behaviour

### Feasibility rules (unchanged core, extended)

```
PreviousEnd + Travel(Previous → C) + SafetyBuffer <= C.Start   (from the base: departure >= earliest base departure)
C.End       + Travel(C → Next)     + SafetyBuffer <= Next.Start
```

Plus: inside a working period of that technician, no overlap, below the daily limit, no leave between departure and
end, real coordinates, every needed route verified (an unverified route is never treated as zero or as "available").

### Cancellation → durable route recalculation

Example: A (Thu Duc) → B (Binh Tan) → C (District 7); B is cancelled → A → C.

1. The cancellation commits in one short transaction regardless of Vietmap. The same transaction upserts
   `technician_schedule_recalculation(technician, local_date)` as PENDING and supersedes B's own snapshots.
2. Right after the commit (no transaction open) the service leases the request, reads the chain and asks Vietmap only for
   legs whose current snapshot no longer matches the real predecessor (here A → C). Unchanged legs are kept.
3. In a short transaction it locks the technician, re-reads the chain (if it changed, it starts over), supersedes the old
   B → C snapshot and inserts the new A → C snapshot (`context = RECALCULATION`): distance, duration, departure (A's end),
   estimated arrival, buffer, provider, congestion.
4. The request row is deleted on success. If a leg cannot be verified, the failed leg is stored with its failure status
   (so the old ETA is no longer current), the request becomes `FAILED` with exponential backoff
   (`2^attempts` minutes, capped by `RecalculationMaxBackoffMinutes`), and `ScheduleRecalculationWorker` retries it.
   The row is in the database, so it survives restarts; leases (`locked_until`) prevent two instances from processing
   the same technician-day.

The same queue is used after customer reschedule, Sales reschedule, Sales reassignment (previous technician's day),
base location change and working-schedule change. New bookings never rely on stored snapshots: they always estimate
their own legs live and re-check them under the technician lock.

### Sales reschedule

```http
PUT /api/survey-requests/{surveyRequestId}/reschedule
{ "startsAt": "2026-10-22T13:30:00+07:00", "reason": "Customer requested another time", "technicianUserId": null }
```
```json
{ "isSuccess": true, "data": { "appointmentId": "new…", "previousAppointmentId": "old…", "surveyRequestId": "…",
  "technicianUserId": "…", "technicianName": "Tech A", "startsAt": "2026-10-22T13:30:00+07:00", "endsAt": "2026-10-22T15:00:00+07:00",
  "appointmentStatus": "ASSIGNED", "surveyRequestStatus": "SCHEDULED", "scheduledAt": "2026-10-22T13:30:00+07:00",
  "reassigned": false, "acknowledgementRequired": true } }
```

- SALES only, owner of the request (`403 SURVEY_REQUEST_NOT_ASSIGNED` otherwise); survey request Assigned/Scheduled;
  appointment not started.
- The new time is fully re-validated for the assigned technician (schedule, leave, daily limit, previous/next appointments,
  live Vietmap legs). If that technician cannot serve it → `409 TECHNICIAN_NOT_AVAILABLE` and the original appointment is
  unchanged; pass `technicianUserId` to reschedule and reassign in one operation.
- The old appointment is cancelled (`RESCHEDULED_BY_SALES`) and linked to the replacement in the same transaction;
  `scheduledAt` moves; a previous acknowledgment is invalidated (`ACKNOWLEDGEMENT_INVALIDATED`) and the technician must
  acknowledge again.
- Customers keep changing only BOOKED (not yet assigned) appointments; after assignment they get `409 INVALID_BOOKING_STATE`.

### Reassignment (also after acknowledgment)

`POST /api/survey-requests/{id}/assign-technician` and `PUT /api/survey-requests/{id}/reassign-technician` run the same
handler: `{ "technicianUserId": "…", "reason": "Tech A is sick" }`.

- The replacement must be an ACTIVE, non-deleted TECHNICIAN with an active profile and base, and pass every rule.
- One UPDATE moves `reserved_technician_user_id` and `assigned_technician_user_id`; the exclusion constraint guards the new
  calendar. Any failure rolls back and keeps the original technician.
- Reassigning after acknowledgment sets the status back to ASSIGNED and clears `acknowledged_at`; the old technician's
  acknowledgment never counts for the new one. Assigning the same technician again is a no-op that keeps the acknowledgment.
- Both technicians' schedules are refreshed: the new one gets fresh legs from the reservation; the old one's day is
  recalculated.
- Past or ongoing appointments cannot be reassigned or rescheduled.

### History and notifications

`technician_appointment_event` records BOOKED, ASSIGNED, REASSIGNED, RESCHEDULED, CANCELLED, ACKNOWLEDGED and
ACKNOWLEDGEMENT_INVALIDATED with actor, previous/new technician, previous/new start and reason.
`scheduling_notification` records who must be told (technicians, customer, Sales). **No delivery channel exists yet**:
rows are stored with `delivery_status = NOT_DELIVERED` and are not claimed as sent.

---

## 5. Dashboard (`GET /api/technicians/{id}/schedule?date=` and `/api/technicians/me/schedule?date=`)

Per appointment: start/end, status, property, `expectedOriginKind` (BASE or APPOINTMENT) and
`expectedOriginAppointmentId`, `inboundTravel` (current snapshot only), `nonCurrentEstimate`,
`routingStatus`, `snapshotAgeMinutes`, `potentialConflict`.
Per day: `appointmentCount`, `estimatedTravelDistanceMeters`, `estimatedTravelSeconds`, `safetyBufferMinutesTotal`,
`conflictCount`, `routesNeedingReview`, `earliestBaseDeparture` and `recalculation` (status, reason, attempts,
nextAttemptAt, lastError), or null.

| routingStatus | Meaning |
|---|---|
| `OK` | Current snapshot, verified, origin = real predecessor (base coordinates or previous appointment) |
| `PENDING_RECALCULATION` | The chain changed and a recalculation is queued; the old value is only in `nonCurrentEstimate` |
| `STALE` | The origin no longer matches and nothing is queued (should not occur after this release) |
| `FAILED` | The latest attempt for this leg failed (provider error, no route, missing coordinates) |
| `MISSING` | No snapshot |

Totals include only `OK` legs, one current snapshot per appointment, so superseded and new legs are never double counted.
Snapshots are minimal (no raw response, no geometry) and are historical estimates, not live traffic.

---

## 6. Slot capacity

Each slot returns `canBook: true`, `knownAvailableTechnicians` and `capacityStatus`:

- `EXACT`: every pre-qualified technician was route-checked with a verified answer, so the count is exact.
- `AT_LEAST`: the search stopped at `MaxReportedSlotCapacity`, was capped by `MaxTechniciansEvaluatedPerSlot`, or a route
  could not be verified. The count is a lower bound.

A technician whose route could not be verified is never counted. Booking always re-validates under the database lock.
(`availableCapacity` and `capacityIsLowerBound` were removed.)

---

## 7. Vietmap Route v4

Request: `GET {BaseUrl}/api/route/v4?apikey=…&point=lat,lng&point=lat,lng&vehicle=motorcycle&points_encoded=true&time={UTC, 15-min bucket}&annotations=congestion,congestion_distance`.

- `paths[0].distance` is in meters; `paths[0].time` is in milliseconds and converted with `TimeSpan.FromMilliseconds`.
- Codes:
  - `OK`;
  - `ZERO_RESULTS` means route not found;
  - `OVER_DAILY_LIMIT` means the quota is exhausted (never retried);
  - `INVALID_REQUEST` and `MAX_POINTS_EXCEED` mean the request was rejected;
  - `ERROR_UNKNOWN`, 5xx and transport errors mean the provider is unavailable;
  - 429 means rate-limited (never retried).
- Retry: one retry, and only for timeouts, transport errors and 5xx.
- Limits and cache: 4 concurrent requests; only successful results are cached, for 30 minutes.
- Logging: the URL holds the key, so request logging is removed from that client.
- **Congestion, documented shape:** per the official docs, congestion lives in `paths[].annotations`:
  - `congestion` is one level per segment: `low`, `moderate`, `heavy`, `severe` or `unknown`;
  - `congestion_distance` is described as the meters of heavy-or-worse segments.
- **Congestion, live shape (one manual request on 2026-10-12, not part of the test suite):** the response did contain
  `annotations.congestion`, `annotations.congestion_distance` and `annotations.history_speed`. It differs from the docs:
  - each item is an object `{ "value", "first", "last" }`, where `first` and `last` are point indexes;
  - the two arrays have the same length and the same ranges, so `congestion_distance` gives meters for every segment,
    not only heavy ones.
- **What the previous adapter got wrong:** it read `path.congestion` instead of `path.annotations.congestion`, so it
  never found any data. That was the cause of the earlier "no congestion returned" observation.
- **What the adapter does now:** it accepts both shapes and:
  - counts segments per level (`segmentsByLevel`);
  - computes `metersByLevel` when the arrays are aligned, with heavy-or-worse = heavy + severe;
  - otherwise treats the distances as the documented heavy-or-worse list;
  - ignores malformed annotations without failing the route.
- **When congestion is absent:** the dashboard shows `congestion.status = NOT_RETURNED_BY_PROVIDER`. Nothing is ever
  derived from the duration, and `history_speed` is not stored.

---

## 8. Configuration

| Key | Default |
|---|---|
| `Scheduling:TimeZoneId` | `Asia/Ho_Chi_Minh` |
| `Scheduling:WorkingHours` / `WorkingDays` | `08:00-12:00,13:00-17:00` / `MON,…,SUN` |
| `Scheduling:EarliestBaseDeparture` | `07:00` |
| `Scheduling:AppointmentDurationMinutes`, `SlotIntervalMinutes`, `SafetyBufferMinutes` | 90, 30, 30 |
| `Scheduling:MaxAppointmentsPerTechnicianPerDay` | 3 |
| `Scheduling:MaxReportedSlotCapacity`, `MaxTechniciansEvaluatedPerSlot`, `MaxRouteRequestsPerOperation` | 3, 6, 60 |
| `Scheduling:RecalculationWorkerEnabled`, `RecalculationPollSeconds`, `RecalculationLeaseSeconds`, `RecalculationMaxBackoffMinutes` | true, 30, 120, 60 |
| `Auth:AccountSetup:LifetimeMinutes` | 4320 |
| `Frontend:AccountSetupUrl` | empty → `Frontend:PasswordResetUrl` |
| `Providers__Vietmap__Enabled`, `__ApiKey`, `__BaseUrl`, `__Vehicle` | unchanged; key only from the environment |

ASP.NET Core does not read `.env` by itself: for `dotnet run`, run `set -a && . ./.env && set +a` first.

---

## 9. Database and migrations

| Migration | Content |
|---|---|
| `20261012040148_AddTechnicianScheduling` | profile, leave, appointment, travel estimate, `btree_gist`, exclusion constraint |
| `20261012054514_AddAdminUserManagementAndSchedulingHardening` | additive, see below |

The second migration adds:

- **Columns:**
  - `user_account.auth_version` (int, default 0);
  - `technician_profile.has_custom_working_schedule` (bool, default false) and `earliest_base_departure` (time, nullable);
  - `technician_travel_estimate.superseded_at` (timestamptz, nullable).
- **New tables:**
  - `technician_working_period`: day 0–6 and end after start (both checked), unique on technician, day and start;
  - `technician_schedule_recalculation`: primary key on technician and local date, with status and attempts checked;
  - `technician_appointment_event`;
  - `scheduling_notification`;
  - `user_account_audit`.
- **Down:** drops the five tables and four columns, so recalculation queues, history, notifications, audit rows and
  custom schedules are lost.

Existing rows need no backfill:

- `auth_version` 0 matches tokens issued before this release, which carry no `av` claim;
- every existing profile uses the default schedule;
- existing estimates count as current.

```bash
# Review SQL (no connection):
dotnet ef migrations script 20261012040148_AddTechnicianScheduling 20261012054514_AddAdminUserManagementAndSchedulingHardening \
  --project src/SmartSolar.Infrastructure --startup-project src/SmartSolar.Api --output admin-and-scheduling-hardening.sql

# Apply only in an approved environment (direct/session connection, port 5432), migrations before the new code:
ConnectionStrings__DefaultConnection="<approved connection string>" \
  dotnet ef database update 20261012054514_AddAdminUserManagementAndSchedulingHardening \
  --project src/SmartSolar.Infrastructure --startup-project src/SmartSolar.Api
```

If `AddTechnicianScheduling` is not applied yet, `database update` applies both in order (requires `btree_gist`).

---

## 10. Tests

- **Default run:** `dotnet test` uses SQLite and synthetic travel. Vietmap is never called.
- **PostgreSQL concurrency:** these tests run only when `SMARTSOLAR_TEST_POSTGRES` points at a disposable local server.
  They refuse non-local hosts, and each test creates and drops its own database.
  - The existing suite covers the exclusion constraint, the partial unique index, races and atomic reassignment.
  - The new suite covers:
    - a booking racing a neighbouring cancellation;
    - a Sales reschedule racing a reassignment;
    - an account change racing an in-flight reservation, both under a held lock and with a disable before commit;
    - reassignment rollback;
    - the recalculation lease;
    - invariants: no dangling reservations and no stale snapshot marked current.

```bash
SMARTSOLAR_TEST_POSTGRES="Host=127.0.0.1;Port=55433;Username=…;Password=…;Database=postgres" \
  dotnet test tests/SmartSolar.Tests --filter "FullyQualifiedName~Postgres"
```

---

## Interaction with Installation Flow shifts

Only active when `Features__InstallationWorkflowEnabled=true` (after the Installation Flow migration); otherwise Flow 1 behaves
exactly as described above and never queries the Installation Flow tables.

- ACTIVE installation shift assignments are loaded as stops in the technician's day: survey slots
  respect them with the same Vietmap travel time and safety buffer, but they do **not** count toward
  `MaxAppointmentsPerTechnicianPerDay`.
- Survey booking/reassignment and installation scheduling take the same `technician_profile` row lock;
  PostgreSQL additionally rejects overlapping installation assignments
  (`ex_installation_shift_assignment_no_overlap`, part of the pending Installation Flow migration).
- Travel estimates are stored only between survey appointments; legs to/from an installation shift are
  validated but not persisted.
- Disabling a technician's scheduling, or deactivating / removing the TECHNICIAN role from the account,
  is refused while the technician has upcoming installation shifts (reported with status
  `INSTALLATION_SHIFT`). Change the installation team first.

Details: [installation-workflow-business-flow.md](installation-workflow-business-flow.md).
