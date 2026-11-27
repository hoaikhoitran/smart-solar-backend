# Technician scheduling after early completion and overtime — technical changes

Baseline: `763cb0b` (= local `d64e2ca` tree). Nothing committed, pushed, deployed or applied to Supabase.

## 1. Root causes (baseline line numbers)

| # | Cause | Location |
| --- | --- | --- |
| R1 | Every non-cancelled survey appointment, COMPLETED included, is a busy stop over its full reserved `[StartsAt, EndsAt)` | `SchedulingUnitOfWork.cs:59` (`Active` = status ≠ CANCELLED), `:199` (stop built from `StartsAt/EndsAt`) |
| R2 | Survey completion only sets `Status = COMPLETED` and `CompletedAt`; nothing uses `CompletedAt` for capacity | `OnSiteSurveyUnitOfWork.cs:64-70` |
| R3 | The completion report leaves the assignment ACCEPTED (correct for the workflow) and nothing marks that its execution is finished | `InstallationHandlers.cs:737` |
| R4 | Current installation assignments (PENDING/ACCEPTED) are busy over the full interval until the customer's ACCEPT sets COMPLETED | `SchedulingUnitOfWork.cs:55-57, :201`; `InstallationCustomerAcceptance.cs:90` |
| R5 | Leave / working-schedule checks treat a reported assignment as upcoming work | `SchedulingUnitOfWork.cs:684` |
| R6 | The database exclusions forbid any overlap with a COMPLETED survey or a reported assignment over the full interval, so the database would refuse what the application should allow | `20261012040148_AddTechnicianScheduling.cs:261-263`; `20261016161547_BusinessRulesFlow1And2Redesign.cs:1320-1323` |
| R7 | A CONFIRMED installation plan blocks other plans of the technician for its whole original interval, forever | `InstallationUnitOfWork.cs:94` |
| R8 | The daily limit counts COMPLETED surveys — by its documented definition (surveys per day), not a defect; see BUSINESS_RULES §7 | `SchedulingOptions.cs:28-29`, `Eligibility.cs` (`CountsTowardDailyLimit`) |
| R9 | Rework after a completion report continued on the same assignment without any scheduling check | `InstallationHandlers.cs:839` (only "ACCEPTED" was required) |

## 2. Changes

| File | Change |
| --- | --- |
| `src/SmartSolar.Modules/Scheduling/Domain/Eligibility.cs` | `EffectiveBusy.Of(startsAt, endsAt, finishedAt)` — the single effective-interval rule |
| `src/SmartSolar.Infrastructure/Persistence/SchedulingUnitOfWork.cs` | day states use `EffectiveBusy` (survey `CompletedAt` when COMPLETED; assignment `WorkReportedAt`); upcoming installation work excludes reported assignments. Daily-limit counting unchanged |
| `src/SmartSolar.Modules/Installations/InstallationEntities.cs` | `InstallationAssignment.WorkReportedAt`; error code `INSTALLATION_REWORK_SCHEDULE_REQUIRED` |
| `src/SmartSolar.Modules/Installations/InstallationHandlers.cs` | completion report sets `WorkReportedAt` (+ event detail); execution actions refused in REWORK_REQUIRED on a released assignment; re-assigning the same interval to a released assignment is not a no-op; views expose `workReportedAt`, `holdsSchedule`, `reworkScheduleRequired` |
| `src/SmartSolar.Modules/Installations/InstallationViews.cs` | the new optional view fields |
| `src/SmartSolar.Infrastructure/Persistence/InstallationWorkflow/InstallationUnitOfWork.cs` | live-plan overlap counts PLANNED / RESCHEDULE_REQUIRED only |
| `src/SmartSolar.Infrastructure/Persistence/Configurations/InstallationWorkflow/InstallationConfigurations.cs` | `work_reported_at` mapping |
| `src/SmartSolar.Infrastructure/Persistence/Migrations/20261125094516_SchedulingEffectiveBusyAfterCompletion*.cs`, `AppDbContextModelSnapshot.cs` | migration (below) |

No controller route, request DTO, RBAC rule, lock, revision check, audit event or policy value changed. 30/70 payment and per-panel package rules
are untouched (verified by their suites).

## 3. Migration `20261125094516_SchedulingEffectiveBusyAfterCompletion` (unavoidable)

Why: the PostgreSQL exclusion constraints (R6) are the double-booking backstop of every write path. With them unchanged the application would offer
released time that the database then refuses (exclusion violation → conflict). Releasing capacity without changing `StartsAt/EndsAt` requires the
constraints to use the effective end; installations additionally need a persisted "execution reported" instant (`work_reported_at`).

- Additive column `installation_assignment.work_reported_at timestamptz NULL` (NULL = previous behaviour).
- `ex_technician_appointment_no_overlap`: `tstzrange(starts_at, CASE WHEN status='COMPLETED' AND completed_at < ends_at THEN GREATEST(starts_at, completed_at) ELSE ends_at END, '[)')`, same `WHERE status <> 'CANCELLED'`.
- `ex_installation_assignment_no_overlap`: same with `work_reported_at`, same `WHERE status IN ('PENDING','ACCEPTED')`.
- Existing rows satisfy the new constraints (they are weaker); the older API build keeps working (never sets the column, checks full intervals itself).
- One transaction; dropping/re-creating each exclusion rebuilds a GiST index under an exclusive lock on `technician_appointment` and
  `installation_assignment` — run in a low-traffic window.
- Down restores the full-interval constraints and drops the column; it is **refused** (`RAISE EXCEPTION`) when rows booked in released time would
  violate them — rollback after real use then means restoring a backup.
- Rehearsed Up → Down → Up on disposable PostgreSQL 17.12; not applied to Supabase.


## 4. Hardening (overtime, workers, HTTP) — 2026-11-25

### Verified root causes

| # | Cause | Location (before this hardening) |
| --- | --- | --- |
| H1 | Every non-completed survey / installation commitment stopped being busy at `EndsAt`, even while the technician was still on site (no notion of "in execution") | `SchedulingUnitOfWork.GetTechnicianDayStatesAsync` (window filter `EndsAt > windowStart`, stops built from the reserved interval) |
| H2 | A survey completed after its end kept only `[StartsAt, EndsAt)`: the buffer was measured from the reserved end, not the real end | `EffectiveBusy.Of` (late completion returned the reserved interval) |
| H3 | No detection of existing commitments collided by an overrun; nothing reached Manager/Admin | — |
| H4 | The installation-schedule worker assigns through `InstallationHandlers.AssignAsync` (locks + eligibility): correct once eligibility sees overtime | `InstallationScheduleHandlers.ConfirmForContractAsync` |

### Changes

| File | Change |
| --- | --- |
| `Scheduling/Domain/Eligibility.cs` | `ScheduledStop.InExecution` / `IsInstallation`; `EffectiveBusy.Of` uses the confirmed finish time also when late; `EffectiveBusy.OvertimeHoldUntil` |
| `Infrastructure/Persistence/SchedulingUnitOfWork.cs` | in-execution evidence in the day-state query (survey report IN_PROGRESS; project IN_PROGRESS or rework with current-round evidence), overtime stops end at now; queries for the overtime sweep (`ListTechniciansToCheckForOvertimeAsync`, conflicts, dispatch recipients, assignment end) |
| `Scheduling/Services/TechnicianDayLoader.cs` | extends overtime stops to the end of the local day |
| `Scheduling/Services/OvertimeConflictService.cs` (new) | per-technician sweep under the technician lock: open / re-open / resolve conflicts, `OVERTIME_CONFLICT` appointment event, Manager/Admin notifications; list for dispatch |
| `Scheduling/Entities/SchedulingEntities.cs` | `TechnicianScheduleConflict`, kinds / statuses / resolutions, `AppointmentEventTypes.OvertimeConflict` |
| `Scheduling/Contracts/Persistence/ISchedulingUnitOfWork.cs` | the new persistence methods |
| `Api/Background/AppointmentEscalationWorker.cs` | runs the overtime sweep each tick in its own scope / failure handling |
| `Api/Controllers/TechnicianSchedulingControllers.cs` | `GET /api/manager/scheduling/schedule-conflicts` (Manager/Admin) |
| `Api/Extensions/SchedulingModuleExtensions.cs` | DI registration |
| `Infrastructure/.../SchedulingConfigurations.cs`, `AppDbContext.cs` | mapping of `technician_schedule_conflict` |

### Migration `20261125104043_SchedulingOvertimeConflicts`

Why: the collision is time-dependent ("still running now"), so it cannot be an index or exclusion condition (`now()` is not immutable); it must be
recorded once per (overrunning work, affected commitment) even when several worker instances run concurrently, and kept as dispatch history.
Additive: one new table (`technician_schedule_conflict`, RLS enabled, unique `(overrun_id, affected_id)`, FK to the technician, check constraints).
No existing table, row or constraint changes. Down drops the table (conflict history lost — back it up first). Not applied to Supabase.

The overtime hold itself needs no schema change: it only concerns time after "now", which the exclusion constraints never have to cover (a new
commitment always starts in the future); the application rule is strictly stronger than the database backstop, so the database never refuses what
the application allows.


## 5. FE Flow 1–4 fixes (2026-11-25)

| Bug | Verified root cause (newest local code) | Change |
| --- | --- | --- |
| BUG-01 | `origin/main`: completed surveys blocked their full slot (fixed locally by `EffectiveBusy`). Remaining local defect: a survey completed on an earlier **day** was loaded only for its scheduled day (its completion buffer was lost on the completion day) and range loads would charge the daily limit to the completion day | `SchedulingUnitOfWork` also loads completed surveys whose `CompletedAt` falls in the window; `ScheduledStop` carries the reserved interval; `TechnicianDayLoader` buckets stops by effective or reserved interval and counts the daily limit on the reserved start's day only. Exclusion constraint unchanged (already agrees: empty range for a completion before the start) |
| BUG-02 | `origin/main`: CONFIRMED plans always counted in `ListOverlappingLiveProposalsAsync` (removed locally earlier). Remaining: PLANNED / RESCHEDULE_REQUIRED rows of COMPLETED / CANCELLED projects still counted | `InstallationUnitOfWork.ListOverlappingLiveProposalsAsync` ignores plans of terminal projects (shared by technician / Manager plans and slot offers). No data repair |
| BUG-03 | `ValidQuantity` allowed 3 decimals for every product | `ProductTypes.WholeUnitTypes` (`SOLAR_PANEL`, `INVERTER`); `QuotationTechnicianCart` validates add, update (400 `VALIDATION_FAILED`, `details.quantity`) and issue (legacy drafts) |
| BUG-04 | `contractId` was only set on the AGREED decision response (`QuotationOnSiteDecisions`), never by `ViewAsync` | `IQuotationUnitOfWork.GetLiveContractIdsAsync` (one query, live = not DISCARDED / SUPERSEDED, latest version); `ViewAsync` resolves it; lists use `ViewsAsync` (one lookup per list) |
| BUG-05 | One `scheduling-read` bucket (20 / min / user) shared by dispatch detail polling and availability computations | New `dispatch-read` policy (same 20 / min, own bucket) on the three lightweight dispatch reads; candidates stay on `scheduling-read` |

No new migration for these fixes. Pending migrations of the local model (in order): `20261124173442_PerPanelServicePackages`,
`20261125094516_SchedulingEffectiveBusyAfterCompletion`, `20261125104043_SchedulingOvertimeConflicts` (the first is in `origin/main`; production has none of
the two scheduling ones yet). `dotnet ef migrations has-pending-model-changes`: none.
