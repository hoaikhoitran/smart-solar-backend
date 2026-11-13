# SmartSolar — full regression test report (authoritative, 2026-10-31, after the flaky-test investigation)

Audience: QA lead and the release owner.

Every number below comes from commands executed on 2026-10-31 on the local working tree: branch `fix/business-logic-v2`, HEAD
`deddde8` (committed outside this session) plus the uncommitted changes of the flaky-test phase. Skipped tests are never counted
as passed. This report replaces all earlier versions; the history of each phase is summarised in §5.

## 00. Current state after the flaky-test investigation (authoritative)

| Run (Release, clean build: 0 warnings, 0 errors) | Total | Passed | Failed | Skipped |
|---|---|---|---|---|
| A: no infrastructure | 1,546 | 1,467 | 0 | 79 |
| B: disposable PostgreSQL 17 | 1,546 | 1,544 | 0 | 2 (the `[RedisFact]` tests) |
| C: disposable Redis | 1,546 | 1,469 | 0 | 77 (the `[PostgresFact]` tests) |
| **D: PostgreSQL and Redis together** | **1,546** | **1,546** | **0** | **0** |

* **D is now verified:** the combined run completed with 0 failures and 0 skips in 2.5 minutes.
* **Before the fixes:** 4 more combined runs were also green — Release, Debug, and two suites run at once.
* **New in this phase:** +7 cases (1,539 → 1,546).
  * `TestHostIsolationTests` 2;
  * `PaymentTests` 2;
  * `PostgresPaymentIntegrityTests` 1;
  * deterministic acceptance/revision orderings on PostgreSQL 2.
* **Fixed:**
  * HTTP test hosts no longer run the dispatch worker on the shared SQLite connection (test harness);
  * a payment order-code collision no longer fails a checkout (production).
* **Still open:**
  * the original 401s: NOT_REPRODUCED;
  * the acceptance race: NOT_REPRODUCED in 60 runs;
  * the real-Redis two-replica test: CONFIRMED_NOT_FIXED, intermittent under CPU starvation.
* Details: [flaky-test-root-cause-analysis.md](flaky-test-root-cause-analysis.md).

The sections below record earlier phases.

## 0. State after M-7 (history)

| Run (Release, clean build: 0 warnings, 0 errors) | Total | Passed | Failed | Skipped |
|---|---|---|---|---|
| Full suite, disposable PostgreSQL 17 (latest two runs) | **1,539** | **1,537** | **0** | 2 (Redis tests) |
| Redis suite alone (4 runs) | 9 | 9 | 0 | 0 |
| Full suite, no infrastructure | 1,539 | 1,463 | 0 | 76 |
| Full suite with PostgreSQL and Redis together | **NOT VERIFIED** in this environment (11–13 unrelated HTTP failures that do not reproduce otherwise; see the verification report §8) | | | |

* **New in M-7:** +10 cases (1,529 → 1,539).
  * `ContractDraftDiscardTests` 4;
  * the discard endpoint authorization test 1;
  * PostgreSQL discard races 4;
  * the M-7 migration test 1.
* **Infrastructure tests:** 76 (71 + 5).
* **Open:** one intermittent, not-reproduced failure of the pre-existing quotation acceptance race test (verification report §8).

The sections below describe the M-1..M-4 phase and are kept as history.

## 1. Commands

```
dotnet build -c Release --no-incremental                                → 0 Warning(s), 0 Error(s)
dotnet test -c Release --no-build   (with SMARTSOLAR_TEST_POSTGRES / _REDIS → disposable local containers)
dotnet test -c Release --no-build   (without infrastructure)
dotnet ef migrations has-pending-model-changes                         → No changes have been made to the model since the last migration
git -c core.whitespace=cr-at-eol diff --check                          → clean
```

* **Infrastructure:** `postgres:17-alpine` and `redis:7-alpine` containers on 127.0.0.1, created for this run and removed
  afterwards. Every PostgreSQL test uses its own throwaway database.
* **Real-service variables:** unset for every run.

## 2. Totals (final)

| Run | Total | Passed | Failed | Skipped |
|---|---|---|---|---|
| **Release, with disposable PostgreSQL + Redis** | **1,528** | **1,528** | **0** | **0** |
| Release, without infrastructure (final, after one more HTTP test) | 1,529 | 1,458 | 0 | 71 (all need PostgreSQL or Redis) |

## 3. Infrastructure suites (executed)

| Class | Cases | Result |
|---|---|---|
| PostgresSchedulingConcurrencyTests | 18 | pass |
| PostgresInstallationWorkflowConcurrencyTests | 14 | pass |
| PostgresDispatchConcurrencyTests | 7 | pass |
| PostgresKnowledgeBaseConcurrencyTests | 6 | pass |
| **PostgresSalesClosureConcurrencyTests** (new) | 6 | pass |
| PostgresAfterSalesConcurrencyTests | 4 | pass |
| PostgresPendingSchemaConstraintTests | 3 | pass |
| PostgresPaymentIntegrityTests | 3 | pass |
| **PostgresM1M4MigrationTests** (new) | 2 | pass |
| PostgresSystemSettingsTests | 2 | pass |
| PostgresAfterSalesMigrationTests | 2 | pass |
| PostgresAdministrativeCatalogImportTests | 2 | pass |
| DistributedRateLimitingTests (Redis) | 2 | pass |
| **Total** | **71** | **71 passed** |

**First run:** 3 of the 77 filtered tests failed. Each was investigated before any change:

| Failure | Cause | Resolution |
|---|---|---|
| `PostgresSalesClosureConcurrencyTests.A_revision_racing_a_closure…` | **Product defect:** `ck_quotation_issued_fields` rejected cancelling a never-issued DRAFT. | Constraint corrected in M-1 (model, migration and snapshots); a SQLite regression test was added. |
| `PostgresPendingSchemaConstraintTests.PostgreSQL_enforces_every_foreign_key…` | The scenario did not populate the new `quotation.closed_by_user_id`. | Scenario extended (rejected quotation, revision, withdrawal). The assertion is unchanged. |
| `PostgresAfterSalesMigrationTests.Down_refuses_while_service_only_payments_exist…` | The test predated the four newer migrations, whose `Down` ran (each committed separately) before the guarded step. | Setup now migrates to AfterSalesServices first. The assertion "refused, nothing changed" is unchanged. |

**Determinism and mutation:**
* The new race and migration tests passed 3 times in a row.
* Disabling the closure's re-check under the lock fails 3 PostgreSQL race tests. It was restored afterwards.

## 4. Tests added or changed in the final phase (+12 cases: 1,517 → 1,529)

| File | Cases | Covers |
|---|---|---|
| `Integration/Persistence/PostgresSalesClosureConcurrencyTests.cs` | 6 | Closure vs contract (both orders), withdrawal vs contract (both orders), reject vs accept, duplicate closures, revision vs closure. Two real connections; lock held via proxy; release on `pg_stat_activity` lock wait. |
| `Integration/Persistence/PostgresM1M4MigrationTests.cs` | 2 | M-1..M-4 Down/Up over real data with legacy rows, backfill, defaults, every new constraint and index, readiness probe, row counts; M-1 Down refused with terminal rows. |
| `Unit/InstallationWorkflow/SalesTerminalStateTests.cs` | +2 | B-01 new request after a withdrawal (nothing copied); a never-issued DRAFT is cancelled with its request. |
| `Integration/InstallationWorkflow/ApprovedPolicyEndpointsTests.cs` | +1 | Warranty-activation endpoint: 401, 403 (Customer, Sales, Technician), 400 (date, reason). Added after the infrastructure run; it needs none. |
| `Unit/AfterSales/ManufacturerWarrantyTests.cs` | +1 | B-04 verified activation: reason, future date and document validation; unverified or no catalog duration refused; once only; end = start + catalog months; snapshot carries it; MANUFACTURER_WARRANTY still refused. |

**Changed:**
* `PostgresAfterSalesMigrationTests` (setup);
* `PostgresPendingSchemaConstraintTests` (terminal branch);
* `AfterSalesTestContext` (`FileService` for the Manager handlers).

## 5. History of totals

| Phase | Total | Passed | Failed | Skipped | New cases |
|---|---|---|---|---|---|
| Start of the 2026-10-30 remediation | 1,456 | 1,393 | 0 | 63 | — |
| End of the 2026-10-30 remediation | 1,484 | 1,421 | 0 | 63 | +28 |
| Approved directions (2026-10-31) | 1,504 | 1,441 | 0 | 63 | +20 |
| M-1..M-4 (2026-10-31) | 1,517 | 1,454 | 0 | 63 | +13 |
| Final phase, with infrastructure | 1,528 | 1,528 | 0 | 0 | +11 |
| **Final, without infrastructure (after one more HTTP test)** | **1,529** | **1,458** | **0** | **71** | **+1** |

Earlier versions of this report listed the 63 infrastructure tests as NOT EXECUTED because Docker was unavailable then. They
have now been executed (§3).

## 6. Coverage by test type

| Type | Status |
|---|---|
| Unit / handler (SQLite) | Executed, green |
| HTTP pipeline (401/403/404/400/409/503, new endpoints, probes) | Executed, green |
| Cross-module E2E (E2E-01..E2E-10, SQLite, handler level) | Executed, green (not HTTP-level) |
| PostgreSQL concurrency, constraints, row locks | **Executed, green** (disposable PostgreSQL 17) |
| Migration chain on PostgreSQL (empty and with representative data, Down/Up M-1..M-4) | **Executed, green** |
| Redis distributed rate limiting | **Executed, green** |
| Real PayOS / S3 / PVGIS / NASA / SMTP / RAG | **NOT EXECUTED** (fakes only) |

## 7. NFR measurements

| NFR | Result |
|---|---|
| API read p95 ≤ 1.5 s, 50 VUs, 10 min | **NOT EXECUTED** (no load-test tooling or deployed environment) |
| Simulation p95 ≤ 25 s over ≥ 30 runs | **NOT EXECUTED** against real PVGIS |
| PayOS PAID confirmed ≤ 2 min | Logic verified with the fake; **timing NOT EXECUTED** |
| 401 / 403 | **Executed**, every endpoint added on 2026-10-30/31 |
| Signed URLs expire after 300 s | Unit-tested; **real S3 NOT EXECUTED** |
| RAG accuracy / citation / p95 | **NOT EXECUTED** |
