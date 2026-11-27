# FE handoff — technician scheduling after early completion and overtime

Backend behaviour only; no FE change is prescribed beyond using the fields below. All responses keep the envelope
`{ isSuccess, traceId, data, error: { code, message, details } }`. **No route, request DTO or role changed.** The FE must keep using backend
availability; it must never compute availability locally or override `TECHNICIAN_SCHEDULE_CONFLICT` / `DAILY_LIMIT_REACHED`.

## 1. What changed for the user

- A survey completed before the end of its reserved slot frees the rest of that slot (plus the configured buffer after the real completion time) for
  new bookings, acceptances and assignments.
- An installation whose completion the technician reported frees the technician's remaining reserved time **while the customer still has to accept**.
  The project stays "waiting for customer acceptance"; the 70 % balance still opens only when the customer accepts.
- If the customer (or a Manager) sends the installation back to rework after such a report, the technician cannot resume on-site work until a
  Manager/Admin schedules the rework and the technician accepts it.

## 2. Schedules are never rewritten

`startsAt` / `endsAt` of survey appointments, installation assignments and installation schedules stay the **original reserved interval** (history).
Do not use them to infer availability. Show actual times from: survey `startedAt` / `completedAt` of the on-site survey view
(`GET /api/survey-requests/{surveyRequestId}/onsite-survey`, technician `…/appointments/{id}/onsite-survey`), installation `startedAt`,
`acceptanceRequestedAt`, assignment `workReportedAt`.

## 3. How availability is determined (backend only)

Effective busy interval = reserved interval, cut at the real finish time once the work is finished (survey COMPLETED; installation completion
reported), plus the configured buffer around it; cancelled / completed / replaced work is not busy; working hours, leave, zones and the daily survey
limit apply as before; work still running after its reserved end holds the technician for the rest of that day (§3a). The daily limit counts **surveys
per day including completed ones** (approved policy), so `DAILY_LIMIT_REACHED` can appear on a day whose time looks free — display it as returned.

Authoritative endpoints (unchanged): `GET /api/survey-slots`, `POST|PUT /api/customers/me/survey-requests/{id}/booking`, technician
`GET /api/technicians/me/open-appointments`, `POST /api/technicians/me/appointments/{id}/accept`, staff availability `GET /api/technicians/availability…`,
`GET /api/technicians/{id}/schedule`, installation `GET /api/installation-projects/{id}/technician-candidates`,
`PUT /api/installation-projects/{id}/assignment`, technician `GET /api/technicians/me/contracts/{contractId}/installation-availability`,
`POST /api/technicians/me/contracts/{contractId}/installation-schedule`.

## 3a. Overtime (work still running after its reserved end)

- Detected by the backend only from authoritative evidence (survey started on site; installation IN_PROGRESS or rework evidence). An ACCEPTED appointment
  whose time passed is not "in progress".
- While it lasts, the technician gets no new commitment for the rest of that local day: bookings / acceptances / assignments for that time fail with the
  usual codes (`TECHNICIAN_SCHEDULE_CONFLICT`, or "no available technician" for customer booking). Show them as returned.
- Existing appointments are never changed automatically. Managers/Admins see the collisions in the dispatch queue (below) and receive an in-app
  notification of type `OVERTIME_CONFLICT`; an affected survey appointment gets an `OVERTIME_CONFLICT` history event (its technician is notified).
- `startsAt` / `endsAt` stay the reserved times; do not extend them in the UI. Show "Running over time" from the job status (survey report
  IN_PROGRESS / project IN_PROGRESS) when now > `endsAt`.

### `GET /api/manager/scheduling/schedule-conflicts?status=OPEN|RESOLVED&technicianUserId=` (Manager, Admin)

`status` defaults to `OPEN`; any other value → 400 `VALIDATION_FAILED`. 401 anonymous, 403 other roles. Response `data`: array of

| Field | Meaning |
| --- | --- |
| `conflictId`, `technicianUserId` | |
| `overrunKind`, `overrunId`, `overrunReservedEndsAt` | the work still running (`SURVEY_APPOINTMENT` → appointment id; `INSTALLATION_ASSIGNMENT` → assignment id) and its reserved end |
| `affectedKind`, `affectedId`, `affectedStartsAt`, `affectedEndsAt` | the existing commitment it collides with |
| `status` | `OPEN` / `RESOLVED` |
| `detectedAt`, `resolvedAt`, `resolution` | `OVERTIME_ENDED` (the work finished) or `COMMITMENT_CLEARED` (the commitment was moved / reassigned / cancelled / started / finished) |

Resolve with the existing actions (survey override / reassign / reschedule; installation assignment); the queue updates on the next worker run.

## 4. New response fields (additive, nullable / default false)

| View | Field | Meaning |
| --- | --- | --- |
| Staff project (`GET /api/installation-projects/{id}`) `assignments[]` | `workReportedAt` | when the technician reported completion on this assignment (null otherwise) |
| same | `holdsSchedule` | true while the assignment still reserves technician time (current and not reported); false for released or historical rows |
| Technician project (`GET /api/technicians/me/installation-projects/{id}`) | `workReportedAt` | same, for the caller's assignment |
| same | `reworkScheduleRequired` | true when the project is REWORK_REQUIRED and the caller's assignment was released: show "waiting for a rework schedule" and disable evidence / assets / completion actions |

## 5. Error codes

| HTTP | Code | When | FE behaviour |
| --- | --- | --- | --- |
| 409 | `INSTALLATION_REWORK_SCHEDULE_REQUIRED` (**new**) | technician evidence / assets / completion report during rework on a released assignment | show that a Manager must schedule the rework; refresh the project |
| 409 | `INSTALLATION_ASSIGNMENT_NOT_ACCEPTED` | after the Manager scheduled the rework, before the technician accepts | prompt the technician to accept (`POST /api/technicians/me/installation-projects/{id}/accept`) |
| 409 | `INSTALLATION_STALE_REVISION` | completion report / assignment with an outdated `expectedRevision` | reload the project and retry |
| 409 | `INSTALLATION_INVALID_STATE` | e.g. a second completion report while the customer decides | refresh; show the current status |
| 404 | `INSTALLATION_PROJECT_NOT_FOUND` | another technician's project | — |
| 409 | `TECHNICIAN_SCHEDULE_CONFLICT`, `DAILY_LIMIT_REACHED`, `OUTSIDE_WORKING_HOURS`, `TECHNICIAN_ON_LEAVE`, `TECHNICIAN_OUTSIDE_ZONE` | unchanged (also during an overtime hold) | show as returned; never override |
| 400 | `VALIDATION_FAILED` | schedule-conflict list with an unknown `status` | — |

## 6. Status display rules

| Situation | Suggested display |
| --- | --- |
| Survey COMPLETED (any time) | "Completed at {completedAt}"; keep the reserved slot as the appointment time |
| Installation IN_PROGRESS | "In progress" (technician busy for the planned interval) |
| Installation PENDING_CUSTOMER_ACCEPTANCE | "Waiting for customer acceptance — work reported at {workReportedAt}"; customer sees Accept / Request rework as today |
| Installation REWORK_REQUIRED and `reworkScheduleRequired` | staff: "Rework needs a schedule" (assign action); technician: "Waiting for rework schedule" |
| Installation REWORK_REQUIRED, new assignment PENDING | technician: accept the rework assignment |
| Installation COMPLETED | "Handed over" |

The daily survey limit counts completed surveys (approved rule): early completion frees time, not the day's survey count.

## 7. Refresh guidance

Re-query availability (slots / candidates / technician availability) after: a booking or reschedule, a technician accept/release, a survey completion,
an installation completion report, a customer acceptance or rework request, an assignment change, and on any 409 from a booking or assignment
(the slot may have been taken meanwhile). Do not cache availability across these actions.


## 8. FE Flow 1–4 fixes (2026-11-25)

See `guides/fe-flow-1-4-fixes/FE_HANDOFF.md`. Scheduling-relevant points: a survey completed before its scheduled day frees the scheduled slot (daily
limit still counted on the scheduled day); plans of completed / cancelled installations never cause `PLANNED_CONFLICT`; dispatch-request detail reads use
their own `dispatch-read` rate-limit bucket, so polling them does not exhaust slot / availability reads.
