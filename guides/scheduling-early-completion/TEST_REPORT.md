# Technician scheduling after early completion and overtime — test report (2026-11-25)

Environment: Apple M5, macOS, .NET SDK 8.0.31; PostgreSQL **17.12** (`pgvector/pgvector:pg17`, disposable container, a fresh database per test);
Redis 7 (disposable); SQLite for unit / E2E tests. Real Supabase variables unset; `PostgresTestDatabase` refuses non-local hosts. PayOS is the
in-process fake server.

## 1. Results

| Run | Total | Passed | Failed | Skipped |
| --- | --- | --- | --- | --- |
| Unit + E2E after the change, before updating rework tests | 1553 | 1546 | **7** | 3 |
| **Full suite, final** (`dotnet test SmartSolar.sln`, PostgreSQL + Redis) | **2043** | **2036** | **0** | 7 |
| New early-completion tests (13 SQLite + 5 PostgreSQL), 3 extra consecutive runs | 18 | 18 | 0 | 0 |

Skipped (7, opt-in dependencies not started; never counted as passed): `S3CompatibleUploadChainTests` ×3 (`SMARTSOLAR_TEST_S3`),
`LiveRagSharedPublicationTests` ×2 (`SMARTSOLAR_TEST_RAG`), `NfrMeasurementTests` ×2 (`SMARTSOLAR_NFR`).

Mutation check: with `EffectiveBusy` forced back to the full reserved interval, 4 of the 13 new SQLite tests fail (TC01/TC05, TC06/TC07, early-start
collapse, TC09/TC10); the other 9 are regression guards that must hold either way.

### Existing tests changed (business rule change, not weakening)

The 7 failures were tests that resumed rework on the reported assignment without any schedule — exactly what BR-04 / TC13 now forbids. Each now inserts
the real scheduling step (`InstallationTests.ReworkScheduledAsync`: Manager `AssignAsync` with a reason + technician acceptance) and keeps every
original assertion: `PaymentPlanWorkflowTests.REWORK_requested_by_the_customer…`, `PaymentPlanE2ETests.E2E_3070_3…`,
`InstallationReworkAndZoneLockTests.A_manager_returns…`, `ApprovedInstallationPolicyTests.A_rework_or_an_equipment_change…`,
`PerPanelServicePackageTests.TC15_TC29…`, `InstallationTests.Manager_rework_opens…` (its "single assignment" check now asserts the stronger history:
REPLACED round-1 assignment + COMPLETED rework assignment), `InstallationLifecycleE2ETests.E2E_08…`.
Migration-list tests updated for the new last migration: `PostgresM1M4MigrationTests` (M21), `PostgresIotMonitoringTests`,
`PostgresCustomerToInstallationTests`, `PostgresPaymentPlanTests` (`Latest`), `PostgresPerPanelPackageTests` (pending list / applied check).

## 2. Traceability

Files: `tests/SmartSolar.Tests/Unit/InstallationWorkflow/EarlyCompletionSchedulingTests.cs` (13),
`tests/SmartSolar.Tests/Integration/InstallationWorkflow/PostgresEarlyCompletionSchedulingTests.cs` (5).

| TC | Scenario | Test | Result |
| --- | --- | --- | --- |
| TC01 | Survey 10:00–11:30 completed 09:00 → new booking at 10:00 goes to the same technician | `TC01_TC05_…` (SQLite + PostgreSQL) | PASS |
| TC02 | Completed at/after the end keeps its interval | `TC02_…` | PASS |
| TC03 | Started, not completed → still blocks; booking refused | `TC03_…` | PASS |
| TC04 | Cancelled → not busy | `TC04_…` | PASS |
| TC05 | `StartsAt` / `EndsAt` unchanged after completion | `TC01_TC05_…` | PASS |
| TC06 | Buffer (configured policy) follows the real end | `TC06_TC07_…`, early-start collapse test | PASS |
| TC07 | Booking 15 min after the real end refused, 10:00 allowed | `TC06_TC07_…` | PASS |
| TC08 | Daily limit keeps counting completed surveys (documented policy) → `DAILY_LIMIT_REACHED`; next day free | `TC08_…` | PASS |
| TC09 | Report keeps PENDING_CUSTOMER_ACCEPTANCE, assignment ACCEPTED, balance closed, no handover | `TC09_TC10_…` | PASS |
| TC10 | Rest of Wednesday and Thursday released; buffer after report; leave checks skip it; CONFIRMED plan no longer blocks plans | `TC09_TC10_…` | PASS |
| TC11 | Customer ACCEPT → completed, handover, assignment COMPLETED, 70 % DUE | `TC11_TC15_…` | PASS |
| TC12 | REQUEST_REWORK does not complete | `TC12_TC13_TC18_…` | PASS |
| TC13 | Rework refused (`INSTALLATION_REWORK_SCHEDULE_REQUIRED`) until Manager schedules and technician accepts | `TC12_TC13_TC18_…` | PASS |
| TC14 | Active installation still blocks | `TC14_…` | PASS |
| TC15 | Completed assignment never blocks | `TC11_TC15_…` | PASS |
| TC16 | Survey completion vs booking race (3 rounds); installation report vs booking race | `PostgresEarlyCompletionSchedulingTests` ×2 | PASS |
| TC17 | 4 concurrent bookings of the released slot → exactly one | `TC17_…` (PostgreSQL) | PASS |
| TC18 | Reassignment keeps the REPLACED row with its schedule and report time | `TC12_TC13_TC18_…` | PASS |
| TC19 | Released time still obeys working hours, leave, zones | `TC19_…` | PASS |
| TC20 | 30/70, per-panel packages, after-sales, dispatch suites | full suite | PASS |
| — | Invalid transitions after a report (second report, restart, reassignment while pending, stranger) | `Invalid_transitions_…` | PASS |
| — | Migration Up–Down–Up; exclusion still refuses a real overlap; Down refused with released-time bookings | PostgreSQL tests | PASS |

## 3. Not covered

- "Web request vs background scheduling worker" races (escalation / acceptance reminder / installation-schedule sweep) have no dedicated test in this
  change; the release itself is a read-side rule plus the completion writes already covered above.
- No HTTP-level test of the new 409 code (mapped by the generic rule: not `*_NOT_FOUND`, not validation → 409).
- No VPS / production measurement; Supabase not touched.


## 4. Hardening run (overtime, workers, HTTP rework)

| Run | Total | Passed | Failed | Skipped |
| --- | --- | --- | --- | --- |
| Full suite after the hardening, first run | 2059 | 2049 | **3** | 7 |
| **Full suite, final** (PostgreSQL 17.12 + Redis) | **2059** | **2052** | **0** | 7 |
| New hardening tests (8 overtime SQLite + 6 worker PostgreSQL + 2 HTTP) + early-completion PostgreSQL (5), 3 consecutive stress runs | 21 | 21 | 0 | 0 (each run) |
| All scheduling tests incl. PostgreSQL (`FullyQualifiedName~Scheduling`) | 249 | 249 | 0 | 0 |

First-run failures (real, fixed): `AppointmentSweepAndEdgeCaseTests.The_worker_cancels…`, `…One_failing_appointment_does_not_abort_the_sweep…`,
`PostgresSchedulingConcurrencyTests.Concurrent_sweeps_cancel_each_started_open_appointment_exactly_once` — their hand-built worker container lacked
the new `OvertimeConflictService`. Fix: the worker runs the overtime sweep in its own scope with its own failure handling (it can never stop the
escalation steps), and the test worker factory now registers the production dependencies (no assertion changed).

Skipped (7, opt-in, not counted as passed): S3 ×3, live RAG ×2, NFR ×2 (see §1).

Rule change reflected in existing tests: `EarlyCompletionSchedulingTests.TC02` and the `EffectiveBusy` unit assertion now expect a late completion to
be busy until the real completion (A1: "use the confirmed completion timestamp … retaining the buffer").

Mutation check: with the overtime hold disabled in `SchedulingUnitOfWork`, OT01, OT04, OT06 and OT07 fail.

### Traceability (hardening)

| Requirement | Test | Result |
| --- | --- | --- |
| A1 survey in progress past its end stays occupied (rest of day); next day free; schedule unchanged | `OvertimeSchedulingTests.OT01_…` | PASS |
| A1 ACCEPTED but never started is not "in progress" | `OT02_…` | PASS |
| A1 release at confirmed (late) completion + buffer | `OT03_…`, `EarlyCompletionSchedulingTests.TC02_…` | PASS |
| A2 installation IN_PROGRESS past its end stays occupied until the report; report keeps PENDING_CUSTOMER_ACCEPTANCE and the 70 % closed | `OT04_…` | PASS |
| A2 scheduled but not started is not in execution | `OT05_…` | PASS |
| A2 rework occupies beyond its end only once rework evidence exists | `OT06_…` | PASS |
| A3 collision → one OPEN conflict, event, Manager notification, booking untouched, further bookings refused, auto-resolve after completion | `OT07_…` | PASS |
| A3 no conflict within the reservation | `OT08_…` | PASS |
| B1 completion vs worker | `PostgresSchedulingWorkerConcurrencyTests.W2_…` | PASS |
| B2 overtime vs worker assignment (installation-schedule worker vs survey completion) | `W4_…` | PASS |
| B3 customer rework vs schedule-confirmation and overdue workers | `W6_…` | PASS |
| B4 two scheduling operations for one technician (customer booking vs worker assignment) | `W5_…` (+ `PostgresEarlyCompletionSchedulingTests.TC17_…`) | PASS |
| B5 worker retry after a transaction conflict (blocked on an uncommitted unique row, fails, next run clean) | `W3_…` | PASS |
| B6 duplicate worker execution (4 concurrent sweeps) | `W1_…` | PASS |
| C rework over HTTP: 409 `INSTALLATION_REWORK_SCHEDULE_REQUIRED` → Manager schedule → accept → work succeeds; 401/403/404, stale revisions, missing reason, invalid transition | `ReworkScheduleEndpointsTests.Rework_after_a_reported_completion_needs_a_new_schedule_through_the_api` | PASS |
| Dispatch queue RBAC / validation | `ReworkScheduleEndpointsTests.The_schedule_conflict_queue_is_manager_only…` | PASS |
| D daily limit unchanged (completed surveys count) | `EarlyCompletionSchedulingTests.TC08_…` | PASS |

### Not covered

- No live PayOS, no VPS / production measurement, Supabase untouched.
- After-sales visit overrun is not detected (documented as U1).
- The HTTP rework test builds the installation up to IN_PROGRESS with the production handlers on the API's database; the steps under test are HTTP.


## 5. FE Flow 1–4 fixes run (2026-11-25)

| Run | Total | Passed | Failed | Skipped |
| --- | --- | --- | --- | --- |
| Unit + E2E after the fixes, before updating per-panel TC05/TC06 | 1567 | 1565 | **2** | 2 |
| **Full suite, final** (PostgreSQL 17.12 + Redis) | **2070** | **2063** | **0** | 7 |
| PostgreSQL scheduling / quotation / installation / per-panel regression (`Postgres` & those areas) | 94 | 94 | 0 | 0 |

The 2 failures were `PerPanelServicePackageTests.TC05` and `TC06`: they added / updated 2.5 and 7.5 panels, which BUG-03 now forbids. Updated to the new
rule (400 `VALIDATION_FAILED` on add / update) while still asserting `QUOTATION_INVALID_PANEL_QUANTITY` for a draft stored with fractional panels before
the rule (synthetic legacy row). `SchedulingRateLimitCoverageTests` now expects `dispatch-read` on the three lightweight dispatch reads.

New tests (11): `FeFlowFixesTests` (6, SQLite) — BUG01 ×2, BUG02, BUG03 ×2, BUG04; `FeFlowFixesEndpointsTests` (2, HTTP) — BUG03/BUG04 envelope and
`contractId`, BUG05 buckets / 429 / RBAC; `PostgresFeFlowFixesTests` (3) — BUG01 with the exclusion constraint, BUG02 on stored rows of a completed project
(CONFIRMED / PLANNED / RESCHEDULE_REQUIRED), BUG04 batch lookup.

Skipped (7, opt-in, not counted as passed): S3 ×3, live RAG ×2, NFR ×2.


## 6. FE feedback after the Flow 1–4 audit (2026-11-25)

| Run | Total | Passed | Failed | Skipped |
| --- | --- | --- | --- | --- |
| New + affected (FeFeedback, FeFlowFixes, PerPanel, PaymentPlan, InstallationTests, OptInPaging), PostgreSQL + Redis | 175 | 175 | 0 | 0 |
| Full suite, first run | 2081 | 2072 | **2** | 7 |
| Rate-limit classes re-run in isolation (3 runs) | 24 | 24 | 0 | 0 (each run) |
| **Full suite, final** (PostgreSQL 17 + Redis) | **2081** | **2074** | **0** | 7 |

First-run failures: `EndpointRateLimitCoverageTests.An_exhausted_portal_budget_does_not_block_other_policies` and
`CatalogRateLimitTests.Read_and_write_budgets_are_separate`. Under full-suite load each test ran for more than a minute, so the 1-minute fixed
window reset before the 429 was asserted. No rate-limit code changed in this round. In isolation they pass 3 runs out of 3, and both pass in the final full run.
Skipped (7, opt-in, not counted as passed): S3 ×3, live RAG ×2, NFR ×2.

New tests (12):
- `FeFeedbackFixesTests` (6, SQLite):
  - ISSUE01: bundled per-panel contract and plan; SUPERSEDED.
  - ISSUE02: names, paging and fields; empty list; soft-deleted and missing customer; one lookup per list (proxy-counted).
- `FeFeedbackFixesEndpointsTests` (3, HTTP):
  - ISSUE01: contract and plan through the API.
  - ISSUE02: array, paged, RBAC, envelope.
  - ISSUE03: removing a cash selection; signature waits for the deposit; receipt fields.
- `PostgresFeFeedbackFixesTests` (2): ISSUE01 read after a "restart" (new contexts and handlers); ISSUE02 batch names on PostgreSQL.

Mutation check: with the customer-name lookup disabled, the 4 ISSUE02 SQLite/HTTP tests fail. `has-pending-model-changes`: none (DTO-only change).
