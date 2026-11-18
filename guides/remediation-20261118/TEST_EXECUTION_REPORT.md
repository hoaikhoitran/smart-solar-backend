# Test execution report (2026-11-18, backend)

* Backend: branch `feat/cloud-rag-security-cost-control`, HEAD `4fb5b95` + uncommitted working tree (two remediation passes).
* Runtime: .NET SDK 8.0.425 host / ASP.NET Core 8.0.34 (`~/.dotnet8`), macOS (Darwin 25.2).
* Shell real-service variables unset for every run (`ConnectionStrings__DefaultConnection`, `RabbitMq__Enabled`, `Redis__Enabled`, `Email__Enabled`):
  no test touched Supabase, RabbitMQ or SMTP.
* Disposable infrastructure (local, random credentials, removed afterwards): `pgvector/pgvector:pg17` (127.0.0.1:55433), `redis:7-alpine`
  (127.0.0.1:56379), S3-compatible `pgsty/silo` (127.0.0.1:59000).
* Frontend: **not modified and not run in the second pass** (backend-only scope).

## Command (final evidence run)

```sh
SMARTSOLAR_TEST_POSTGRES="Host=127.0.0.1;Port=55433;Username=postgres;Password=…;Database=postgres" \
SMARTSOLAR_TEST_REDIS=127.0.0.1:56379 SMARTSOLAR_TEST_S3="http://127.0.0.1:59000|…|…|http://localhost:3000" \
DOTNET_ROOT=~/.dotnet8 env -u ConnectionStrings__DefaultConnection -u RabbitMq__Enabled -u Redis__Enabled -u Email__Enabled \
  ~/.dotnet8/dotnet test tests/SmartSolar.Tests --logger "trx;LogFileName=pass2-final.trx" --results-directory TestResults/qa-2026-11-18/pass2
```

## Results

| Suite | Session baseline (before any change) | End of pass 1 | **Final (pass 2)** | Failed | Skipped | Evidence |
|---|---|---|---|---|---|---|
| Unit | 1 341 passed | 1 373 passed | **1 391 passed** | 0 | 0 | `TestResults/qa-2026-11-18/pass2/pass2-final.trx` |
| Integration | 291 passed, 112 skipped | 442 passed, 1 failed | **465 passed** | 0 | 0 | same |
| End-to-end | 11 passed | 15 passed | **21 passed** | 0 | 0 | same |
| **Total .NET** | 1 755 (1 643 passed) | 1 831 (1 830 passed) | **1 877 passed / 1 877** | **0** | **0** | same; earlier: `…/baseline/baseline-all.trx`, `…/final/final2-all.trx` |
| Python RAG | – | – | not run | – | – | no RAG code or contract changed |

* Pass 2 added **46 new test executions** (unit 18, integration 22, E2E 6) and renamed one (`INSTALL_BUNDLE_CASH_002_…`, see below); 0 removed otherwise.
* "Not executed": 0. Every infrastructure-gated test ran.

### Failures found and fixed during pass 2 (not hidden)

1. `PostgresAfterSalesRemediationTests.Concurrent_cash_confirmations_…` failed in the first pass-2 full run (`pass2-all.trx`): a confirmation that lost the
   race answered `PAYMENT_CASH_ITEM_NOT_PAYABLE` instead of `PAYMENT_ALREADY_PAID` (money integrity held: nothing written). Fixed in
   `PaymentService.ConfirmCashAttemptAsync` (re-reads the committed attempt status). Re-run 8× in isolation (`race-rerun-1..8.trx`, all 7/7) and in the
   final full run.
2. Technician issue failed on `ck_sales_task_owner` (closing an unclaimed Sales task as DONE). Fixed: an unclaimed task is CANCELLED.
3. `S3CompatibleUploadChainTests.Storage_answers_403_for_…_an_expired_link…` (failed once at the end of pass 1): the test now waits on the storage
   server's own clock (see BUSINESS_REMEDIATION_REPORT.md §4.5). PASS in every pass-2 run; root cause (clock drift) is likely, not proven.

### Existing tests modified (with old → new expectation)

* `BundledCashCheckoutTests.INSTALL_BUNDLE_CASH_002_…` renamed `…_Cash_needs_a_known_method_active_packages_and_follows_the_contract_rule`: old
  `AFTER_SALES_VALIDATION_FAILED` ("cash only with the contract"), new `AFTER_SALES_INVALID_STATE` (the restriction was removed; the unchanged
  "pay the installation first" rule applies).
* Migration-chain tests (`PostgresM1M4MigrationTests`, `PostgresIotMonitoringTests`, `PostgresCustomerToInstallationTests`, `PostgresAiCostControlTests`):
  expected migration lists now end with `…_InstallationSlotOffers`, `…_AfterSalesTechnicianQuotation`.
* `S3CompatibleUploadChainTests`: fixed sleep replaced by a bounded wait on the server clock; assertion (403) unchanged.
* Support: `InterleavingPreSurveyUnitOfWork` (delegates the two new paging methods), `AfterSalesTestContext.PaymentsWithSchedule`, `AuthApiFactory`,
  `PublicFileEndpointsTests` helper visibility (pass 1).

## Acceptance status

PASS with evidence: technician-priced after-sales quotation, after-sales cash (+ PayOS regression), coverage / expiry rules, installation flows
(paper contract, slot choice, cash / PayOS / combined cash), pagination (12 families), rate-limit coverage (every controller action), dashboard overview
and money reconciliation, both migrations, required E2E journeys. Coverage percentage: not measured. NFR performance: not measured.

# Final acceptance pass (pass 3, 2026-11-18)

Same runtime, branch and disposable infrastructure as above. Real-service variables unset; `SMARTSOLAR_NFR` unset in the regression runs.

## Command (final evidence run)

```sh
SMARTSOLAR_TEST_POSTGRES="Host=127.0.0.1;Port=55433;Username=postgres;Password=…;Database=postgres" \
SMARTSOLAR_TEST_REDIS=127.0.0.1:56379 SMARTSOLAR_TEST_S3="http://127.0.0.1:59000|…|…|http://localhost:3000" \
DOTNET_ROOT=~/.dotnet8 env -u ConnectionStrings__DefaultConnection -u RabbitMq__Enabled -u Redis__Enabled -u Email__Enabled -u SMARTSOLAR_NFR \
  caffeinate -dims ~/.dotnet8/dotnet test tests/SmartSolar.Tests --no-build --blame-hang-timeout 15m \
  --logger "trx;LogFileName=pass3-final-3.trx" --results-directory TestResults/qa-2026-11-18/pass3
```

## Results (`TestResults/qa-2026-11-18/pass3/pass3-final-3.trx`, 11:16:36–11:21:42 +07, 5 m 5 s)

| Suite | Pass 2 final | **Pass 3 final** | Failed | Not executed | Evidence |
|---|---|---|---|---|---|
| Unit | 1 391 | **1 403 passed** | 0 | 0 | `pass3-final-3.trx` |
| Integration | 465 | **469 passed** | 0 | 2 (opt-in NFR, run separately below) | same |
| End-to-end | 21 | **22 passed** | 0 | 0 | same |
| **Total .NET** | 1 877 | **1 894 passed / 1 896** | **0** | 2 | same |
| Opt-in NFR (`SMARTSOLAR_NFR=1`) | – | **4 passed / 4** (`NFR_SIM_P95`, `NFR_SIGNED_URL`, plus the 2 always-run ones) | 0 | 0 | `TestResults/qa-2026-11-18/nfr/nfr-opt-in-2.trx` |
| API read load (ab, 10 min) | – | PASS, p95 36 ms | 0 failed requests | – | `TestResults/qa-2026-11-18/nfr/api-read-load/` |
| Python RAG | not run | not run | – | – | no RAG code or contract changed |

Pass 3 added 19 test executions: unit +12 (MAINT_001..014 = 12), integration +6 (pricing configuration, schema-readiness concurrency,
NFR_PAYOS_RECON, NFR_RL_CONCURRENT, plus 2 opt-in NFR), E2E +1 (E2E_MAINT_015). Assertions were added to `DASHBOARD_001` and
`AfterSalesRemediationEndpointsTests`.

## Earlier pass-3 runs (kept, not hidden)

| Run | Result | Cause | Action |
|---|---|---|---|
| `pass3-final.trx` (09:51–11:01) | 1 891 passed, **3 failed** | macOS **clamshell sleep** 09:54–11:00 (pmset log). The failures happened exactly at dark-wake moments: `PostgresInstallationSlotOfferTests.MIGRATION_Down_and_Up_round_trip…` Npgsql connect timeout at 10:10:54 (wake 10:10:53); `SimulationEndpointsTests.Technicians_read_simulations…` **401** at 10:43:48 after a wall-clock jump longer than the token lifetime; `SimulationEndpointsTests.Expired_result_returns_410…` at 11:00:42 (wake 11:00:40) | re-run of both classes with the machine awake: 24/24 (`pass3-sleep-failures-rerun.trx`). Environmental; no code change |
| `pass3-final-2.trx` (11:11–11:15, awake) | 1 893 passed, **1 failed** | `DistributedRateLimitingTests.Two_replicas_backed_by_real_redis_share_one_limit`: Redis keys `…:29916252=1`, `…:29916253=5`. The six asks crossed the fixed 1-minute window boundary (aligned on the Redis clock), so 4 were admitted. The limiter behaved correctly; the **test** was flaky (committed in `deddde8`, before this work) | **test fixed**: it starts the asks with at least 20 s left in the current Redis window. The assertions (exactly 3 × 200, 3 × 429) are unchanged. 5/5 re-runs pass (`redis-window-rerun-1..5.trx`) |
| `pass3-final-3.trx` | **1 894 passed, 0 failed** | – | final evidence |

## Existing tests modified in pass 3 (old → new)

* `FaultRequestTests` (one-time maintenance via Sales): now passes `inspectionRequired: false`. The default changed to inspection first;
  the old behavior is still asserted.
* `DistributedRateLimitingTests.Two_replicas_…`: waits for a fresh Redis window when fewer than 20 s remain. Expectation unchanged.
* `DashboardReconciliationTests.DASHBOARD_001`, `AfterSalesRemediationEndpointsTests`, `SchemaReadinessTests`: assertions / tests added; nothing removed.
* `S3CompatibleUploadChainTests`: a pass-3 signer experiment was **reverted**; the assertion is back to `InRange(lifetime, 1, 2)` and the signer
  source has no diff.

## Other gates

| Gate | Result |
|---|---|
| Build (`dotnet build tests/SmartSolar.Tests`) | succeeded, 0 warnings |
| EF model ↔ snapshot (`has-pending-model-changes`) | no pending changes |
| `dotnet list package --vulnerable --include-transitive` | Api / Modules / Infrastructure: none. Test project: `System.Net.Http` 4.3.0, `System.Text.RegularExpressions` 4.3.0 (High, transitive, **not shipped**; known OPEN P3) |
| Coverage percentage | not measured |
