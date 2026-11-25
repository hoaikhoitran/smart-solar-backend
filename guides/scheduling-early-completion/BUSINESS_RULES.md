# Technician scheduling after early completion and overtime — business rules (backend, 2026-11-25)

Legend: **[Implemented]** verified by automated tests (see TEST_REPORT.md) · **[Decision required]** business approval needed · **[Unresolved]** known
scenario not handled automatically.

## 1. Three separate concepts (BR-01) [Implemented]

| Concept | Survey appointment | Installation assignment |
| --- | --- | --- |
| Reserved schedule (history) | `StartsAt` / `EndsAt` | `StartsAt` / `EndsAt` |
| Actual execution | report `StartedAt`, appointment `CompletedAt` (server time) | project `StartedAt`, assignment `WorkReportedAt` (server time of the completion report) |
| Effective busy interval (future scheduling) | see §2 | see §2 |

`StartsAt` / `EndsAt` are never modified to release capacity. No appointment, assignment, event, notification or audit row is deleted.

## 2. Effective busy interval [Implemented]

One rule (`EffectiveBusy`, `src/SmartSolar.Modules/Scheduling/Domain/Eligibility.cs`) used by every scheduling decision:

| State | Busy interval |
| --- | --- |
| Survey PENDING_ACCEPTANCE / ESCALATED / ACCEPTED / ASSIGNED, not started on site | `[StartsAt, EndsAt)` — after `EndsAt` it is **not** treated as in progress |
| Survey started on site (report IN_PROGRESS), within its reservation | `[StartsAt, EndsAt)` |
| Survey started on site and still IN_PROGRESS after `EndsAt` (**overtime**) | held from `StartsAt` until the **end of that local day** (re-evaluated at every decision) |
| Survey COMPLETED at C | `[StartsAt, C)` (early or late); if C ≤ StartsAt: the point `[C, C)` |
| Survey CANCELLED | not busy |
| Installation assignment PENDING / ACCEPTED, project not in execution | `[StartsAt, EndsAt)` |
| Installation IN_PROGRESS (or REWORK_REQUIRED with evidence of the current round) within its reservation | `[StartsAt, EndsAt)` |
| Same, still not reported after `EndsAt` (**overtime**) | held until the end of that local day |
| Assignment ACCEPTED with completion reported at R (PENDING_CUSTOMER_ACCEPTANCE, or REWORK_REQUIRED on that assignment) | `[StartsAt, R)` (early or late); `[R, R)` if R ≤ StartsAt |
| Installation assignment COMPLETED / REPLACED / CANCELLED / DECLINED | not busy |

"In execution" needs authoritative evidence: the on-site survey report IN_PROGRESS (started by the technician), the project status IN_PROGRESS,
or rework evidence of the current round. An ACCEPTED appointment whose start passed is never assumed to be in progress.

The configured buffer `scheduling.bufferBetweenAppointmentsMinutes` (Admin setting; never hard-coded) is applied **once**, around the effective
interval, by the existing conflict rule: a new job [S, E) conflicts when `busy.Start < E + buffer` and `S < busy.End + buffer`.
Example (buffer 30 min): survey reserved 10:00–11:30, started 08:00 (allowed by `onsiteSurvey.earlyStartMinutes`), completed 09:00 → busy point 09:00,
next start allowed from 09:30; completed 09:15 → a 09:30 booking is refused (`TECHNICIAN_SCHEDULE_CONFLICT` / no available technician), 10:00 is allowed.
New starts must also be in the future (existing booking validation), so the current time is always respected.

The database exclusion constraints use the same end (and an empty range for the zero-length case), so the database and the application agree.

## 3. Survey completed early (BR-02) [Implemented]

Completion is unchanged (transaction, technician row lock, server `CompletedAt`, early-start validation preserved). From the completion on, every
availability and booking path sees the released time because they all go through the shared eligibility service: customer slots and booking,
rescheduling, technician self-acceptance, Sales/Manager assignment and override, holder re-homing, cross-zone dispatch, installation planning.

## 4. Installation reported complete (BR-03, BR-05) [Implemented]

- The completion report sets `WorkReportedAt` on the current assignment; the assignment **stays ACCEPTED**, the project stays
  `PENDING_CUSTOMER_ACCEPTANCE`, the customer still ACCEPTs or REQUEST_REWORKs. The report never completes the project, never hands over, never
  opens the 70 % balance.
- The rest of the reserved interval is released for future scheduling (surveys, other installations, leave and working-schedule changes).

| Installation situation | Technician capacity |
| --- | --- |
| Assigned, future work (PENDING / ACCEPTED, not started) | busy for the whole interval |
| Actively working (IN_PROGRESS) | busy for the whole interval |
| Execution reported, customer acceptance pending | released from the report time |
| Customer requested rework (REWORK_REQUIRED) | released; a **new assignment** is required before work resumes |
| Completed and handed over | not busy (assignment COMPLETED) |

## 5. Customer decision (BR-04) [Implemented, unchanged]

- ACCEPT: checklist and substitution gates, project COMPLETED, handover, assignment COMPLETED, the 70 % balance opens (30/70 contracts), warranty and
  bundled per-panel package provisioning conditions unchanged.
- REQUEST_REWORK (and the Manager's return to rework): project REWORK_REQUIRED, no handover, balance stays closed.
- **New:** on-site rework actions (evidence, asset registration, completion report) on an assignment whose time was released are refused with
  `409 INSTALLATION_REWORK_SCHEDULE_REQUIRED`. A Manager/Admin assigns the rework interval (existing `PUT /api/installation-projects/{id}/assignment`,
  reason required, full eligibility and locks) and the technician accepts it; the old assignment becomes REPLACED (history kept).

## 6. Installation plans (BR-08) [Implemented]

A CONFIRMED plan is the project's accepted assignment; it no longer blocks another plan of the technician by itself (its capacity is the assignment's
effective interval). PLANNED and RESCHEDULE_REQUIRED plans (which hold no assignment yet) still block overlapping plans. Historical plans stay readable.

## 7. Daily survey limit (BR-07) [Implemented — approved rule]

Approved (2026-11-25): `MaxAppointmentsPerTechnicianPerDay` (and the per-technician override) caps the **total surveys per technician per local day**.
Completed surveys still count; early completion releases occupied time, not the number of surveys performed. The behaviour is unchanged and applied
identically in availability, automatic assignment, self-acceptance, staff assignment, rescheduling and dispatch. The limit is configuration (never
hard-coded); `DAILY_LIMIT_REACHED` can therefore be returned for a day whose time is free.

## 7a. Overtime protection (A1–A3) [Implemented]

- Work in execution past its reserved end keeps occupying the technician until the end of that local day (business time zone) or until it is
  completed / reported, whichever comes first. No new commitment can be taken in that time by any path (customer booking, self-acceptance, staff
  assignment, rescheduling, dispatch, installation planning, the installation-schedule worker).
- Commitments that already exist in that time are **never** cancelled, moved or reassigned automatically. The scheduling worker records each collision
  once as an OPEN schedule conflict (`technician_schedule_conflict`), adds an `OVERTIME_CONFLICT` history event to an affected survey appointment
  (notifying its technician) and notifies every active Manager/Admin. Managers list them at `GET /api/manager/scheduling/schedule-conflicts` and act
  with the existing override / reassign / reschedule actions.
- A conflict closes by itself (`RESOLVED`): `OVERTIME_ENDED` when the overrunning work is completed / reported, `COMMITMENT_CLEARED` when the commitment
  no longer collides (moved, reassigned, cancelled, started, finished). It re-opens if the same pair collides again.
- `StartsAt` / `EndsAt` are never rewritten to show overtime.

**Decision recorded in code (review recommended):** the overtime hold lasts until the end of the local day in which the overrun is observed. A
shorter hold (e.g. a fixed extension) would need a policy value; none was invented.

## 8. Concurrency (BR-09) [Implemented]

Survey completion, installation completion reporting, booking, acceptance, staff assignment, installation planning, the installation-schedule worker
(which assigns technicians through the same assignment path) and the overtime sweep all take the technician row lock, so a decision either sees the
released / held time or not — never half. Database backstops: the effective-interval exclusion constraints (survey appointments, installation
assignments) and the unique (overrun, affected) index of schedule conflicts. A worker run that loses a race logs the id and retries on its next run.

## 9. Unresolved / out of scope

| # | Scenario | Current behaviour |
| --- | --- | --- |
| U1 | After-sales visits (shared assignment table) overrunning | not detected as overtime (they have their own visit lifecycle); unchanged |
| U2 | Overtime hold length | until the end of the local day (§7a); a configurable hold is a policy decision |
| U3 | Technician-proposed rework dates | rework is scheduled by Manager/Admin through the assignment endpoint; the technician proposal flow stays pre-installation only |
| U4 | Conflict notifications | recorded in-app (no push / e-mail channel exists for these notifications) |


## 10. FE Flow 1–4 fixes (2026-11-25) [Implemented]

- **Early completion on another day (BUG-01):** a survey completed before its scheduled day frees its scheduled slot; its real completion point keeps
  the buffer on the day it happened; the approved daily limit charges the survey to its **scheduled** day only (`ScheduledStop.ReservedStartsAtUtc`).
- **Plans of finished projects (BUG-02):** a plan whose project is COMPLETED or CANCELLED never blocks another plan, whatever its stored status; CONFIRMED
  plans of active projects block through their assignment; PLANNED / RESCHEDULE_REQUIRED plans of active projects still block (`PLANNED_CONFLICT`).
- Whole-unit equipment quantities (BUG-03), quotation `contractId` (BUG-04) and the `dispatch-read` rate-limit bucket (BUG-05): see
  `guides/fe-flow-1-4-fixes/FE_HANDOFF.md`.
