# Per-panel packages — test report (2026-11-25)

Machine: Apple M5, macOS, .NET SDK 8.0.31 (runtime 8.0.x). PostgreSQL **17.12** (`pgvector/pgvector:pg17`, disposable container, a new database per
test, dropped afterwards). Redis 7 (`redis:7-alpine`, disposable). PayOS is the in-process **fake** PayOS server (signed webhooks, link status):
no live charge was made and none is claimed. SQLite (EnsureCreated) for unit/HTTP tests. No test touched Supabase or production
(`PostgresTestDatabase` refuses non-local hosts; the real connection-string variables were unset).

## 1. Full suite

Command: `env -u ConnectionStrings__DefaultConnection -u RabbitMq__Enabled -u Redis__Enabled -u Email__Enabled SMARTSOLAR_TEST_POSTGRES=… SMARTSOLAR_TEST_REDIS=… dotnet test SmartSolar.sln`

| Run | Total | Passed | Failed | Skipped |
| --- | --- | --- | --- | --- |
| Run 1 (first full run after the change) | 2024 | 2014 | **3** | 7 |
| Run 2 (after fixing the 3 tests) | 2024 | 2017 | 0 | 7 |
| Run 3 (pre-release audit, same code as run 2) | 2024 | 2017 | 0 | 7 |
| **Run 4 (after the audit fix + its new test, final)** | **2025** | **2018** | **0** | 7 |

Run-1 failures (all real, all in migration tests that stand on an older schema, fixed — none weakened):

| Test | Cause | Fix |
| --- | --- | --- |
| `PostgresSimplifiedCommercialFlowTests.Legacy_sales_quotations_and_contracts_survive_the_upgrade_unchanged` | row JSON compared before/after the upgrade now contains the new columns | new columns excluded from the row comparison exactly like the 30/70 columns already were, **and** asserted separately (`installation_total`/`service_packages_total` null, quotation `service_packages_total = 0`, `panel_quantity` null) |
| `PostgresSimplifiedCommercialFlowTests.Down_is_refused_while_data_of_the_new_flow_exists_and_changes_nothing` | after the refused Down the schema is below the new migration; the test materialised a whole `Quotation` entity | same assertion with a projection of the checked column (`Origin`) |
| `PostgresAfterSalesMigrationTests.Down_refuses_while_service_only_payments_exist_and_changes_nothing` | same, whole `ServiceSubscription` entity | projection of `Status` |

Skipped (7, opt-in dependencies not started in this run, unchanged from the previous release report): `S3CompatibleUploadChainTests` ×3
(`SMARTSOLAR_TEST_S3`), `LiveRagSharedPublicationTests` ×2 (`SMARTSOLAR_TEST_RAG`), `NfrMeasurementTests` ×2 (`SMARTSOLAR_NFR`, real providers / S3).

Migration-list tests updated for the new last migration: `PostgresM1M4MigrationTests` (M20), `PostgresIotMonitoringTests`,
`PostgresCustomerToInstallationTests`, `PostgresPaymentPlanTests` (`Latest`), `PostgresSharedPublicationCutoverTests` (asserts its migration is applied and
nothing is pending instead of "is the last one").

Audit fix (2026-11-25): an installation-wide (legacy) purchase is now re-checked against per-panel coverage of the same kind under the contract lock
(checkout transaction) and again at payment settlement; new PostgreSQL test
`PostgresPerPanelPackageTests.Concurrent_installation_wide_and_per_panel_purchases_of_one_kind_never_both_cover_the_panels`. The per-panel / service-package
/ after-sales subset (166 tests) passed 3 consecutive runs after the fix.

## 2. New tests (31) and traceability

Files: `tests/SmartSolar.Tests/Unit/InstallationWorkflow/PerPanelServicePackageTests.cs` (24, SQLite, production handlers),
`Integration/InstallationWorkflow/PerPanelPackageEndpointsTests.cs` (2, HTTP), `Integration/InstallationWorkflow/PostgresPerPanelPackageTests.cs` (5, PostgreSQL).

| TC | Scenario | Test | Result |
| --- | --- | --- | --- |
| TC01 | No package: contract = installation, 30/70 unchanged, no breakdown in snapshot | `TC01_TC26_a_quotation_without_packages…` | PASS |
| TC02 | 10 × 700,000 = 7,000,000; package not taxed again | `TC02_one_disaster_package…` | PASS |
| TC03 | Both kinds, itemised, counted once | `TC03_TC04_both_kinds…` | PASS |
| TC04 | Several SOLAR_PANEL SKUs summed; inverter/labour excluded | `TC03_TC04_…` | PASS |
| TC05 | Zero / fractional panels refused | `TC05_fractional_or_missing_panels…` | PASS |
| TC06 | 10 → 8 panels recalculates atomically; removing panels refused | `TC06_changing_the_panels…` | PASS |
| TC07 | Replace / remove package with revision checks | `TC07_TC28_…` | PASS |
| TC08 | Catalog price change before issue → issue snapshot | `TC08_TC09_TC10_…` | PASS |
| TC09 | Price change after issue → quotation and contract unchanged | `TC08_TC09_TC10_…` | PASS |
| TC10 | Inactive package blocks the issue | `TC08_TC09_TC10_…` | PASS |
| TC11 | 50,000,000 + 7,000,000 = 57,000,000 → 17,100,000 + 39,900,000; allocation 15,000,000/2,100,000 and 35,000,000/4,900,000 | `TC11_TC12_…` | PASS |
| TC12 | One installment line, no package checkout; verified money = contract total | `TC11_TC12_…`, `TC13_…` (60,000,000) | PASS |
| TC13 | Online deposit: not fully paid, nothing active | `TC13_TC14_TC16_TC17_TC25_…` | PASS |
| TC14 | Cash deposit: exact amount by Manager; wrong amount refused; technician 403 on confirm | `TC13_…`, `PerPanelPackageEndpointsTests` | PASS |
| TC15 | Rework keeps installment 2 closed | `TC15_TC29_…` | PASS |
| TC16 | Balance → PAID → provisioned (PANEL scope, real panels, term ≥ max(paid, handover), 7-day waiting) | `TC13_…` | PASS |
| TC17 | Duplicate webhook: no second settlement / subscription; re-provisioning no-op | `TC13_…`, `Postgres…webhook_and_reconciliation_racing…` | PASS |
| TC18 | Webhook ×2 + reconciliation ×2 race; 4 concurrent buyers of one unit (×3 rounds) | `PostgresPerPanelPackageTests` (2 tests) | PASS |
| TC19 | 10 contracted vs 9 installed → RECONCILIATION_REQUIRED, no coverage, amounts unchanged, Managers notified, retry idempotent | `TC19_…` | PASS |
| TC20 | 10-unit lot allocated by count, no serial invented | `TC20_…` | PASS |
| TC21 | Retail 2 uncovered units at 100 % upfront (2 × 300,000), contract unchanged | `TC21_TC22_…` | PASS |
| TC22 | Covered / reserved units cannot be bought again | `TC21_TC22_…`, PostgreSQL concurrency test | PASS |
| TC23 | Replacement panel eligible for purchase, does not inherit coverage; old allocation kept | `TC23_…` | PASS |
| TC24 | Covered panel assessed; other panel / unnamed panel NOT_ELIGIBLE; limits not multiplied | `TC24_…` | PASS |
| TC25 | 10 panels of maintenance = 2 visits, not 20 | `TC13_…` | PASS |
| TC26 | Legacy package / contract / quotation keep amounts and meaning; legacy vs per-panel overlap refused | `TC01_TC26_…`, `TC26_…`, migration Up–Down–Up | PASS |
| TC27 | RBAC: technician read/own draft only, no catalog edit, no cash confirmation; other technician 404; 5 roles + anonymous | `PerPanelPackageEndpointsTests`, `Invalid_package_choices…`, `TC27_C_…` | PASS |
| TC28 | Two edits from the same revision: second refused | `TC07_TC28_…` | PASS |
| TC29 | Overdue balance → collection case for 39,900,000 | `TC15_TC29_…` | PASS |
| TC30 | Finance: INSTALLATION 50,000,000 + bundled disaster 7,000,000 = deposits + balances collected | `TC30_…` | PASS |

Additional: rounding remainders absorbed exactly; withdrawal before deposit discards commitments; legacy contract paths refuse package quotations;
revision copies packages and leaves the issued version untouched; renewal starts at the current term's end without a new waiting period; unknown /
wrong-kind / legacy-unit packages; selection validation (quantity ≠ units, units > row, unknown panel, missing targets, someone else's contract);
DB constraints (one subscription per commitment, allocation sums, breakdown = total, provisioned ⇔ subscription); Down refused with per-panel data.

## 3. Not covered / limitations

- Live PayOS (sandbox or production) — only the fake server; S3 / live RAG / NFR opt-in suites not run in this session.
- Manager correction of an installed-panel record after handover followed by a *successful* reconciliation retry is not exercised end to end
  (the retry path is tested while the record is still wrong; the provisioning rules themselves are tested in the main flow).
- The panel replacement in TC23 is a synthetic data step (old row REPLACED + new ACTIVE row with lineage); the replacement workflow itself is covered
  by the existing after-sales tests.
- Frontend not tested (out of scope). No VPS / production-load measurement.
