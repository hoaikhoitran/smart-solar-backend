# System settings — admin timing policy

Admins can change the business durations of scheduling, uncovered-zone dispatch, on-site survey, installation and after-sales without a
redeploy. Managers can read them. Module: `src/SmartSolar.Modules/SystemSettings/`. API: `api/admin/system-settings/scheduling`.

**Settings apply to new timing events only; existing deadlines are snapshots.**

## Keys, units, defaults, ranges

Every setting is an integer (`type: "INT"`) with a fixed unit. Ranges are inclusive. While no Admin has saved a key, its value is
the bootstrap default: the code value overridden by appsettings (`Scheduling`, `OnSiteSurvey`, `Installation` sections). Dispatch
defaults are fixed in code. The defaults are checked at startup, and an invalid configured default stops the API from starting.

| Key | Unit | Default | Range | Meaning |
|---|---|---|---|---|
| `dispatch.reviewSlaMinutes` | MINUTES | 240 | 5–10080 | Time staff have to review a new dispatch request (`review_due_at = created_at + value`). |
| `dispatch.escalationDelayMinutes` | MINUTES | 0 | 0–10080 | Delay after `review_due_at` before an overdue request escalates to Admin (0 = at the deadline). |
| `dispatch.reminderIntervalMinutes` | MINUTES | 0 | 0, or 15–10080 | Staff reminders while a request is overdue. **0 = disabled.** |
| `dispatch.proposalResponseWindowMinutes` | MINUTES | 1440 | 15–10080 | Customer answer window for a proposal (`expires_at = LEAST(created_at + value, starts_at)`). |
| `scheduling.acceptanceWindowMinutes` | MINUTES | 240 (`Scheduling:AcceptanceWindowMinutes`) | 5–10080 | Technician self-accept window after booking. |
| `scheduling.acceptanceMinimumLeadMinutes` | MINUTES | 120 (`Scheduling:AcceptanceMinimumLeadMinutes`) | 0–10080 | Acceptance must happen at least this long before the start (deadline = `LEAST(booked_at + window, starts_at − lead)`). |
| `scheduling.acceptanceReminderLeadMinutes` | MINUTES | 0 | 0–10080, and below the acceptance window | Technician reminder before the acceptance deadline. **0 = disabled.** |
| `scheduling.customerChangeLeadMinutes` | MINUTES | 120 (`Scheduling:CustomerChangeLeadMinutes`) | 0–10080 | Customers may reschedule or cancel until this long before the start. |
| `scheduling.minimumBookingLeadMinutes` | MINUTES | 0 (`Scheduling:MinimumBookingLeadMinutes`) | 0–10080 | Minimum time between now and a bookable start. |
| `scheduling.bookingHorizonDays` | DAYS | 30 (`Scheduling:BookingHorizonDays`) | 1–365 | How many days ahead (including today) customers may book. |
| `scheduling.bufferBetweenAppointmentsMinutes` | MINUTES | 30 (`Scheduling:BufferBetweenAppointmentsMinutes`) | 0–240 | Fixed gap between two commitments of one technician (not a travel-time estimate). |
| `onsiteSurvey.earlyStartMinutes` | MINUTES | 120 (`OnSiteSurvey:EarlyStartMinutes`) | 0–1440 | How early the technician may start the on-site survey. This is also the release cut-off. |
| `installation.maxAssignmentDays` | DAYS | 14 (`Installation:MaxAssignmentDays`) | 1–365 | Longest installation assignment, in local calendar days. |
| `afterSales.inspectionDurationMinutes` | MINUTES | 60 | 15–480 | Default length of an after-sales inspection visit (other visits default to 120 minutes). |
| `afterSales.quotationValidityDays` | DAYS | 7 | 1–60 | A repair / one-time maintenance quotation expires this long after it is issued (snapshot `expires_at`). |
| `afterSales.checkoutExpiryMinutes` | MINUTES | 30 | 5–1440 | Lifetime of an after-sales PayOS checkout (never past the quotation expiry). |
| `afterSales.maintenanceBookingLeadDays` | DAYS | 7 | 0–60 | A maintenance booking must start at least this far ahead. |
| `afterSales.maintenanceNoticeLeadDays` | DAYS | 14 | 1–90 | Customer notice before a maintenance period opens, and the package-expiry reminder lead. |
| `afterSales.customerResponseHours` | HOURS | 24 | 1–336 | Wait after a notice before Sales follows up; also the completion-acceptance reminder interval. |
| `afterSales.followUpMaxDays` | DAYS | 7 | 1–60 | An unanswered maintenance follow-up closes as UNREACHABLE after this long. |
| `afterSales.delayedMaintenanceExtensionDays` | DAYS | 30 | 0–180 | Extension after the term end when SmartSolar delayed a valid booking. |
| `afterSales.appointmentChangeLeadHours` | HOURS | 24 | 0–336 | Customers may change or cancel a maintenance appointment until this long before it starts. |
| `afterSales.disasterWaitingDays` | DAYS | 7 | 0–60 | Waiting period before a new disaster-protection term covers incidents (none for a continuous renewal). |

Cross-field rules are checked on the merged values (stored values plus the submitted changes):

* `scheduling.minimumBookingLeadMinutes` < `scheduling.bookingHorizonDays` × 1440
* `scheduling.acceptanceMinimumLeadMinutes` < `scheduling.bookingHorizonDays` × 1440
* `scheduling.customerChangeLeadMinutes` < `scheduling.bookingHorizonDays` × 1440
* `scheduling.acceptanceReminderLeadMinutes` = 0, or < `scheduling.acceptanceWindowMinutes`
* `dispatch.reminderIntervalMinutes` = 0, or ≥ 15

Infrastructure settings are not exposed here and stay in configuration: poll intervals, timeouts, retry counts, slot interval,
appointment duration, daily capacity, and working hours/days (working calendars).

## Non-retroactivity (as implemented)

**Settings apply to new timing events only; existing deadlines are snapshots.** Each timing event stores the deadline it derived,
plus the value(s) and the `settings_version` it used:

| Event | Stored snapshot | Derived from |
|---|---|---|
| Dispatch request created | `review_sla_minutes`, `review_due_at`, `escalation_due_at`, `reminder_interval_minutes`, `settings_version` | `dispatch.reviewSlaMinutes`, `dispatch.escalationDelayMinutes`, `dispatch.reminderIntervalMinutes` |
| Dispatch proposal created | `response_window_minutes`, `expires_at = LEAST(created_at + window, starts_at)`, `settings_version` | `dispatch.proposalResponseWindowMinutes` |
| Survey booking (and a reschedule that needs re-acceptance) | `acceptance_deadline_at`, `acceptance_window_minutes`, `settings_version` (new nullable columns of `technician_appointment`) | `scheduling.acceptanceWindowMinutes`, `scheduling.acceptanceMinimumLeadMinutes` |
| Technician release | a fresh `acceptance_deadline_at` with its snapshot; `acceptance_reminder_sent_at` reset | same |
| Reschedule keeping the confirmed technician | the old row's deadline and snapshot are copied (no new acceptance) | — |
| Direct dispatch assignment / accepted proposal | no acceptance deadline (the appointment is `ASSIGNED`) | — |

* Background workers act only on persisted deadlines: the dispatch worker uses `review_due_at`, `escalation_due_at`, the stored
  reminder interval and each proposal's `expires_at`; the escalation worker uses `acceptance_deadline_at`. They never recompute an
  existing deadline from the current settings.
* The acceptance reminder is the one worker step that reads a current value: `scheduling.acceptanceReminderLeadMinutes` decides
  how long before the PERSISTED acceptance deadline the (single) reminder is recorded. The deadline itself never moves.
* A **new** event uses the settings that are current when it happens: a new dispatch request, a new proposal on an old request,
  a booking, a reschedule creating a new appointment that needs acceptance, or a technician release that recomputes the deadline.
* Rules checked at the moment of an action (not stored) use the current values: booking horizon and minimum lead (slots,
  booking, dispatch windows/starts), the buffer between commitments (every eligibility check), the customer change lead, the
  on-site early start and the release cut-off, and the installation maximum length.
* Dispatch requests never expire. No setting introduces an expiry; only explicit staff or customer actions resolve them.

## Wiring

Handlers and workers read the effective values from `ISchedulingPolicyProvider` (one snapshot per scope: `TechnicianEligibility`
reads it on its first load and every rule of that request uses it). `SchedulingOptions`, `OnSiteSurveyOptions` and
`InstallationOptions` keep the business-duration properties only as bootstrap defaults (the former `SchedulingOptions.
OnSiteSurveyEarlyStartMinutes` mirror was removed).

| Key | Read by |
|---|---|
| `dispatch.reviewSlaMinutes`, `dispatch.escalationDelayMinutes`, `dispatch.reminderIntervalMinutes` | `DispatchService.CreateAsync` (snapshotted) |
| `dispatch.proposalResponseWindowMinutes` | `DispatchService.ProposeAsync` (snapshotted) |
| `scheduling.acceptanceWindowMinutes`, `scheduling.acceptanceMinimumLeadMinutes` | booking (`Appointments.New`), reschedule (`AppointmentMover`), release (`AppointmentAssignmentService.ReleaseAsync`) |
| `scheduling.acceptanceReminderLeadMinutes` | `AppointmentEscalationService.SendAcceptanceRemindersAsync` (worker, every tick; 0 = disabled) |
| `scheduling.customerChangeLeadMinutes` | customer reschedule / cancel guard and `canCustomerChange` in customer views |
| `scheduling.minimumBookingLeadMinutes`, `scheduling.bookingHorizonDays` | `WorkingCalendar.BookingRange/Bookable/TryResolveBookable/IsBookableStart` (passed as `BookingLimits`): slots, booking, reschedule, coverage horizon, dispatch windows and dispatch starts |
| `scheduling.bufferBetweenAppointmentsMinutes` | `TechnicianEligibility.Buffer` (every eligibility check, hold re-homing, installation planning) and the technician schedule dashboard |
| `onsiteSurvey.earlyStartMinutes` | on-site survey start (`OnSiteSurveyHandlers.StartAsync`) and the technician release cut-off |
| `installation.maxAssignmentDays` | `InstallationSchedulePlanner.ValidateIntervalAsync` |
| `afterSales.*` | Flow 3 handlers and `AfterSalesSweepService`; deadlines (quotation `expires_at`, checkout `expires_at`, term `coverage_starts_at`, entitlement `extended_until`) are stored when created. See [after-sales-services.md](after-sales-services.md). |

## Freshness

`ISchedulingPolicyProvider.GetAsync()` returns an immutable `SchedulingPolicySnapshot { Version, Values }`, which has typed accessors
such as `DispatchReviewSlaMinutes`. Handlers read it per request, and workers read it on every tick. Each process caches the
snapshot for `SystemSettings:CacheSeconds`: the default is 15, the allowed range is 0–15, and 0 disables the cache.

* A successful PUT invalidates the cache on the API instance that handled it, so that instance sees the change at once.
* Other instances see the change within the TTL (≤ 15 s).
* Version 0 means the bootstrap defaults. Each saved update increments the global version by one.

## Storage

The EF configuration is in `Persistence/Configurations/SystemSettings`. The tables come from the integrator's migration.

* `system_setting`: one row per admin-saved key. Columns:
  * `key` (PK, ≤ 100), `value_int`, `unit` (check MINUTES|HOURS|DAYS), `version` (≥ 1)
  * `updated_at`, `updated_by_user_id` (nullable, FK `user_account`)
  * `revision`: the concurrency token
* `system_setting_revision`: append-only audit with one row per changed key and version. Columns:
  * `id`, `key`, `version`, `old_value`, `new_value`, `unit`
  * `changed_by` (FK `user_account`), `changed_at`, `reason` (≤ 500)
  * Unique `(key, version)`.
* `system_settings_version`: a single row (check `id = 1`) holding the global `version`, `updated_at`, `updated_by_user_id`, and
  `revision` (concurrency token). The row is absent until the first save.

Value ranges and cross-field rules are validated in code; the database checks only the unit. If a stored value is outside the
current range (for example after a later release narrows a range), or its unit differs, the default is used instead.

## API

All responses use the standard `ApiResponse` envelope. Every action has a per-user fixed-window rate limit:

* `RateLimiting:SystemSettingsRead`: 60/min
* `RateLimiting:SystemSettingsWrite`: 10/min

| Method & path | Roles |
|---|---|
| `GET /api/admin/system-settings/scheduling` | Admin, Manager |
| `PUT /api/admin/system-settings/scheduling` | Admin only (Manager → 403) |
| `GET /api/admin/system-settings/scheduling/history?page=1&pageSize=20&key=` | Admin, Manager |

Anonymous calls get 401. Customer, Sales and Technician get 403.

### GET

```json
{
  "isSuccess": true,
  "data": {
    "version": 0,
    "bootstrap": true,
    "updatedAt": null,
    "updatedByUserId": null,
    "cacheTtlSeconds": 15,
    "settings": [
      {
        "key": "dispatch.reviewSlaMinutes", "type": "INT", "unit": "MINUTES",
        "value": 240, "defaultValue": 240, "min": 5, "max": 10080,
        "source": "DEFAULT", "adminEditable": true,
        "description": "Time staff have to review a new uncovered-zone dispatch request (...)",
        "version": 0, "updatedAt": null, "updatedByUserId": null
      }
    ],
    "rules": ["scheduling.minimumBookingLeadMinutes < scheduling.bookingHorizonDays * 1440", "..."]
  },
  "error": null,
  "traceId": "..."
}
```

`source` is `DEFAULT` (bootstrap value) or `ADMIN` (saved). The `version` of a setting is the global version that last changed it.

### PUT

```json
{
  "settings": [
    { "key": "dispatch.reviewSlaMinutes", "value": 180, "unit": "MINUTES" },
    { "key": "scheduling.bookingHorizonDays", "value": 21 }
  ],
  "expectedVersion": 0,
  "reason": "Pilot week"
}
```

* The update is all-or-nothing, and only the listed keys change. `value` must be a JSON integer.
* `unit` is optional. When sent, it must equal the setting's unit, compared case-insensitively. No conversion is applied.
* `expectedVersion` is required: send the `version` returned by GET.
* `reason` is **required** (non-blank, up to 500 characters) and is stored on every audit row.
* The response is 200 with the same body as GET, at the new version.
* A request that changes no value is a no-op: it returns 200 at the same version and writes no audit row.

Concurrency: the global version row is locked `FOR UPDATE` on PostgreSQL and its revision is checked. If two Admins save from the
same version, exactly one wins. The other gets 409 `SETTINGS_VERSION_CONFLICT`, with `details.currentVersion` when known; it should
reload and retry.

### History

```json
{
  "data": {
    "items": [
      {
        "id": "…", "key": "dispatch.reviewSlaMinutes", "version": 2,
        "oldValue": 180, "newValue": 120, "unit": "MINUTES",
        "changedByUserId": "…", "changedAt": "2026-10-19T03:05:00+00:00", "reason": null
      }
    ],
    "page": 1, "pageSize": 20, "totalItems": 3, "totalPages": 1
  }
}
```

Items are sorted newest version first, then by key. `pageSize` is clamped to 1–100. `oldValue` is the effective value before the
change, which is the default when the key had never been saved.

### Error codes

| Code | HTTP | When |
|---|---|---|
| `SYSTEM_SETTING_VALIDATION_FAILED` | 400 | Missing/negative `expectedVersion`, empty or oversized `settings`, missing or duplicate key, missing/blank `reason` or `reason` > 500 |
| `SYSTEM_SETTING_UNKNOWN_KEY` | 400 | A key is not in the registry |
| `SYSTEM_SETTING_NOT_EDITABLE` | 400 | The key is not admin-editable (none today) |
| `SYSTEM_SETTING_INVALID_UNIT` | 400 | `unit` differs from the setting's unit |
| `SYSTEM_SETTING_INVALID_VALUE` | 400 | Not an integer, or outside the range |
| `SYSTEM_SETTING_RULE_VIOLATION` | 400 | A cross-field rule fails on the merged values |
| `SETTINGS_VERSION_CONFLICT` | 409 | `expectedVersion` is stale, or a concurrent update won |

`details` maps each offending key (or request field) to its messages. When a request has several kinds of error, the most
fundamental kind is reported: unknown key, then not editable, then unit, then value, then rules.
