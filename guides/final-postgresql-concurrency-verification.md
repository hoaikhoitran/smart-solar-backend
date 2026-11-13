# SmartSolar V2 — final PostgreSQL, concurrency and migration verification (2026-10-31)

Audience: backend reviewers, the database owner, QA and the release owner.

Scope: the local working tree of `feat/fe-backend-gap-fixes` (HEAD `2810286` plus uncommitted changes). Nothing was committed, pushed or deployed.
No shared, staging or production database was touched.

## 1. Test infrastructure used

* **Runtime:** Docker Desktop (local), started for this run with the owner's consent. It was stopped again afterwards, and no
  pre-existing container was started.
* **Disposable containers:** `ss-verify-pg` (`postgres:17-alpine`, `127.0.0.1:55432`, random throwaway password) and `ss-verify-redis`
  (`redis:7-alpine`, `127.0.0.1:56379`). Both were bound to localhost only and **removed after the run**. The password file was deleted.
* **Isolation:** every PostgreSQL test creates and drops its own database `smartsolar_test_<guid>`. The test harness refuses
  non-local hosts.
* The migrations were applied **only** to these throwaway databases.

## 2. P0: closure vs contract creation race

**Root cause:** `QuotationHandlers.CloseAsync` (withdrawal / Sales closure) and `ContractHandlers.CreateAsync` checked their
preconditions **before** opening their transactions, and took no common lock:
* the closure checked "no live contract";
* contract creation checked "quotation ACCEPTED".

Under READ COMMITTED both could pass. The result could be a Closed request, a CANCELLED quotation and a new DRAFT contract.
A quotation revision or automatic generation racing a closure could likewise leave an open quotation on a closed request.

**Fix (lock and re-check):**
* Every path that creates or ends commercial records of a survey request first takes
  `SELECT 1 FROM survey_request WHERE id = … FOR UPDATE`, then re-checks its rules under the lock. The paths are:
  * withdrawal and Sales closure;
  * contract creation;
  * quotation creation and revision;
  * automatic quotation generation.
* **Lock order** is always survey_request → quotation → contract, so there is no cycle and no deadlock. Accept and reject
  touch only the quotation row, with conditional single-row updates.
* **Closure** re-reads status, live contract and open appointment after the lock, then makes conditional updates on the
  request and on each open quotation. Any change aborts the whole transaction.
* **Contract creation** re-checks after the lock: request not closed (`CONTRACT_REQUEST_CLOSED`), quotation still ACCEPTED,
  survey version unchanged, no live contract. Nothing is written if a check fails.
* **Quotation inserts** refuse a closed request (`QUOTATION_REQUEST_CLOSED`, or the automatic outcome `REQUEST_CLOSED`).
* **Idempotency** is unchanged: repeating the same closure returns the stored outcome.
* **No** SERIALIZABLE isolation and no distributed lock were introduced.

**Files changed:**
* `src/SmartSolar.Modules/Quotations/QuotationSalesOutcomes.cs` (locked re-check, `Refusal`)
* `QuotationHandlers.cs` (`RequestClosedUnderLockAsync` in `PersistNewAsync`)
* `QuotationAutomaticGeneration.cs`
* `QuotationContracts.cs`
* `src/SmartSolar.Modules/CustomerContracts/ContractHandlers.cs` (precondition under lock)
* `ContractContracts.cs`
* `ContractEntities.cs` (`CONTRACT_REQUEST_CLOSED`)
* `src/SmartSolar.Infrastructure/Persistence/InstallationWorkflow/QuotationUnitOfWork.cs`
* `ContractUnitOfWork.cs` (`LockSurveyRequestAsync`, `IsSurveyRequestClosedAsync`, the live-contract status)

**A second defect found by the real PostgreSQL run:**
* Closing a request whose open quotation is a never-issued DRAFT violated `ck_quotation_issued_fields`, which demanded issue
  fields on every non-DRAFT row. No SQLite test had cancelled a draft, so it was not caught there.
* **Fixed** in the unapplied, authorised M-1 migration and its model, without a new migration:
  `status IN ('DRAFT','CANCELLED') OR (issue fields present)`. `Down` restores the old definition. The model snapshot and the
  M-1..M-4 designer snapshots were updated, and `has-pending-model-changes` reports none.
* Covered by a new SQLite test as well.

## 3. Concurrency tests (`Integration/Persistence/PostgresSalesClosureConcurrencyTests.cs`)

**Method:**
* Two real PostgreSQL connections.
* A `DispatchProxy` holds the first transaction right after it acquired the survey-request lock.
* The second transaction is started and released only when `pg_stat_activity` shows a session of the test database waiting
  on a `Lock`. That is a condition wait, not a fixed delay.
* The stored state is asserted after both finish.

| Test | Result |
|---|---|
| Closure first, then contract creation | Closure succeeds; contract `CONTRACT_REQUEST_CLOSED`; state: request Closed, quotation CANCELLED, **0 contracts**. **PASS** |
| Contract creation first, then closure | Contract created; closure `SURVEY_REQUEST_CONTRACT_EXISTS` (`details.contractStatus = DRAFT`); state: request Completed, quotation ACCEPTED, 1 DRAFT contract. **PASS** |
| Withdrawal vs contract creation, both orders | The same exclusion, both orders. **PASS** |
| Reject vs accept (simultaneous) | Exactly one wins; the stored status matches the winner. **PASS** |
| 4 duplicate closures (simultaneous) | All return the same outcome; exactly one `REQUEST_CLOSED` event and one quotation `CANCELLED` event. **PASS** |
| Revision vs closure (simultaneous) | The closure succeeds; no non-terminal quotation remains on the closed request. **PASS** (it first exposed the DRAFT constraint defect) |

The tests ran 3 times in a row, all passing.

**Mutation check:** I disabled the closure's re-check under the lock. Three PostgreSQL tests then failed (contract-first,
withdrawal both orders, duplicates). The check was restored afterwards.

## 4. Migration verification

**Empty database:**
* `MigrateAsync()` applies the full chain, including all pending migrations (six at that time; seven with M-7, see §8), in every PostgreSQL test (about 70 fresh
  databases).

**Representative data, Down and Up** (`Integration/Persistence/PostgresM1M4MigrationTests.cs`, **PASS**):
1. Full schema with a real handed-over project: contract, payment, project, 13 assets.
2. Down to `20261030122441_AutomatedInstallationQuotation`. The pending list is exactly M-1..M-4, and `EfSchemaMigrationProbe`
   (readiness) reports the same four.
3. Legacy-shaped rows written with the old schema: a typed panel warranty, an old document and an old chat answer.
4. Up. The pending list is empty and readiness is ready.
5. Results:
   * every asset duration is backfilled `LEGACY_UNVERIFIED`; assets without a duration have no source; no start dates;
   * the old document is `INTERNAL`;
   * the old answer has `retrieval_audience` NULL, so it is withheld from customers;
   * row counts of seven business tables are unchanged.
6. Constraints and indexes on PostgreSQL:
   * a legacy `Cancelled` row without metadata is valid;
   * `Closed` without metadata or with a bad reason → 23514; `Closed` with a valid reason is accepted;
   * `REJECTED` without closure data → 23514; a bad `closed_by_user_id` → 23503;
   * an invalid audience → 23514;
   * a second active substitution → **23505** (partial unique index);
   * a warranty source removed, or an end date without a start → 23514;
   * `ux_installation_equipment_substitution_active`, `ix_survey_request_assigned_sale_status` and
     `ix_knowledge_document_status_audience` are present; `IX_survey_request_assigned_sale_id` is gone, replaced by the composite.

**Rollback limits:**
* `PostgresM1M4MigrationTests.M1_down_refuses_while_terminal_rows_exist_…` (**PASS**): with a REJECTED quotation, M-1's `Down`
  fails on the restored status check. The schema stays at M-1 and the row is untouched. Terminal rows must be remapped before
  any rollback.
* A `Down` of M-4 loses `warranty_source`. After a re-`Up`, catalog-sourced durations become `LEGACY_UNVERIFIED`. That is safe
  (never upgraded to verified), but it is real data loss on rollback.

**Existing migration tests:**
* **Foreign-key catalogue test:** passes on PostgreSQL. Every Installation-Flow foreign key populated by the scenario is rejected when broken (114 before the terminal-state branch was added; the passing run does not print its count), and delete restrictions and invalid checks are enforced.
* **`PostgresAfterSalesMigrationTests.Down_refuses_while_service_only_payments_exist_…` failed on the first run.** The test
  predated the four newer migrations: migrating to the pre-AfterSales migration first ran their `Down` (each committed
  separately) before reaching the guard.
  * The guard itself was correct. The test now stands on AfterSalesServices first and then asserts that the refused step
    changes nothing.
  * Assertion kept: "Down refused, schema and payment data unchanged". Setup changed: an added `MigrateToAsync(db, AfterSales)`.
* **`PostgresPendingSchemaConstraintTests` failed** because `quotation.closed_by_user_id` was not exercised by the scenario.
  The scenario now includes a rejected quotation, a revision and a withdrawn request (which also exercises the DRAFT
  cancellation).

## 5. PostgreSQL / Redis suite counts

| Class | Cases | Result |
|---|---|---|
| PostgresSchedulingConcurrencyTests | 18 | PASS |
| PostgresInstallationWorkflowConcurrencyTests | 14 | PASS |
| PostgresDispatchConcurrencyTests | 7 | PASS |
| PostgresKnowledgeBaseConcurrencyTests | 6 | PASS |
| PostgresSalesClosureConcurrencyTests (new) | 6 | PASS |
| PostgresAfterSalesConcurrencyTests | 4 | PASS |
| PostgresPendingSchemaConstraintTests | 3 | PASS (after the scenario addition) |
| PostgresPaymentIntegrityTests | 3 | PASS |
| PostgresM1M4MigrationTests (new) | 2 | PASS |
| PostgresSystemSettingsTests | 2 | PASS |
| PostgresAfterSalesMigrationTests | 2 | PASS (after the setup correction) |
| PostgresAdministrativeCatalogImportTests | 2 | PASS |
| DistributedRateLimitingTests (Redis) | 2 | PASS |
| **Total infrastructure tests** | **71** | **71 passed, 0 failed** |

**Full Release run:**
* With PostgreSQL and Redis: **1,528 total, 1,528 passed, 0 failed, 0 skipped.**
* One HTTP authorization test (no infrastructure) was added afterwards. Final run without infrastructure: **1,529 / 1,458 / 0 / 71 skipped**.

## 6. Status of each item

| Item | Status |
|---|---|
| Closure / withdrawal vs contract creation | FIXED_AND_VERIFIED_POSTGRESQL |
| Reject vs accept, duplicate closure, revision vs closure | FIXED_AND_VERIFIED_POSTGRESQL |
| DRAFT quotation cancellation constraint (M-1 correction) | FIXED_AND_VERIFIED_POSTGRESQL |
| Six-migration chain, M-1..M-4 Down/Up with legacy data, backfill, constraints, readiness probe | FIXED_AND_VERIFIED_POSTGRESQL |
| Concurrent substitution approvals | FIXED_AND_VERIFIED_POSTGRESQL at constraint level (23505). An end-to-end handler race was not added. |
| Concurrent audience changes | FIXED_AND_VERIFIED_SQLITE_ONLY (revision token) |
| B-02 discard of a DRAFT contract | BLOCKED_BY_PRODUCT_DECISION / SCHEMA_APPROVAL_REQUIRED (§7) |
| Manufacturer claim handling and payer | BLOCKED_BY_PRODUCT_DECISION |
| Real PayOS, S3, PVGIS/NASA, SMTP, RAG providers | NOT_EXECUTED |
| NFRs (load p95, simulation p95, PayOS timing, signed-URL expiry, RAG accuracy) | NOT_EXECUTED |

## 7. Remaining gaps and blockers

1. **B-02, discarding a DRAFT contract:**
   * The contract state machine (`DRAFT, ISSUED, AWAITING_SIGNATURE, UNDER_REVIEW, SIGNATURE_REJECTED, SIGNED, PAID, SUPERSEDED`)
     has no state that means "discarded". SUPERSEDED means "replaced by a newer version" and would be incompatible.
   * Proposal **M-7 `ContractDraftDiscard`** (not generated):
     * status `DISCARDED`;
     * `discarded_at`, `discarded_by_user_id` (FK), `discard_reason`, with a check that DISCARDED has all three;
     * the partial unique "one contract per quotation" index keeps excluding DISCARDED rows, as it does SUPERSEDED.
   * Handler design: Sales owner only; DRAFT only (conditional update); the same survey-request lock first, so it serialises
     with issue and payment; no payment or refund touched.
   * Until then, a request with a DRAFT contract is refused (`SURVEY_REQUEST_CONTRACT_EXISTS`, `details.contractStatus = "DRAFT"`).
2. **Before production:**
   * apply the seven pending migrations (incl. M-7) on a staging copy;
   * time the M-4 backfill on production-sized data;
   * the external providers and NFRs above.
3. **Product decisions still open:** manufacturer-claim handling and payer; M-7 approval.

## 8. M-7 verification (2026-10-31, later run)

**Infrastructure:**
* Docker Desktop's VM would not start ("no route to host" to the VM; two restarts). I did not reset the VM, because that
  could delete the owner's Docker data.
* With the owner's consent, `postgresql@17` (17.11) and `redis` were installed through Homebrew. They ran **only temporarily**
  from a data directory in the session scratchpad, on 127.0.0.1:55432 and :56379, with a random password, and durability off
  (`fsync=off`, which is acceptable for a throwaway cluster).
* Both were stopped and their data deleted afterwards. No Homebrew service was registered; the two packages remain installed.

| Run | Total | Passed | Failed | Skipped |
|---|---|---|---|---|
| Full Release suite, PostgreSQL only (latest two runs) | 1,539 | **1,537** | **0** | 2 (the Redis tests) |
| Full Release suite, PostgreSQL only (one earlier run) | 1,539 | 1,536 | 1 | 2. The pre-existing `Acceptance_and_revision_request_race_to_exactly_one_outcome` failed once and did **not** reproduce in 2 full runs and 11 isolated runs; its message was not captured. **Open: intermittent, NOT_REPRODUCED.** |
| Redis suite (`DistributedRateLimitingTests`), 4 runs | 9 | 9 | 0 | 0 |
| Full Release suite, no infrastructure | 1,539 | 1,463 | 0 | 76 |
| **Full suite with PostgreSQL *and* Redis together** (native setup) | 1,539 | 1,526–1,528 | **11–13** | 0 |

**The PostgreSQL + Redis combined run (NOT VERIFIED):**
* Each such run took about 35 minutes, and 11–13 HTTP-pipeline tests failed, a different set each time: mostly unexpected
  401s, plus rate-limit counts. None are contract or M-7 tests.
* The same tests pass when run alone with both variables set (87/87), in the PostgreSQL-only full runs, and in the previous
  phase's Docker-based combined run (1,528/1,528).
* This is an unresolved interaction of the native Redis setup with the full parallel suite. A candidate worth examining is
  the blocking `ConnectionMultiplexer.Connect` used when Redis is enabled.
* The combined run must be repeated in a container environment before release.

**M-7-specific PostgreSQL results (all pass, deterministic, repeated 5 times):**

| Test | Result |
|---|---|
| Discard vs issue (simultaneous) | Exactly one wins; DISCARDED rows have no issue data, ISSUED rows no discard data |
| 4 concurrent discards | One discard and one event; the others return it unchanged |
| Discard vs terms edit | Exactly one wins; a discarded row keeps its original terms |
| Discard holding the lock, then closure | The closure refuses at once while the DRAFT is live; after the commit the explicit closure closes |
| Closure while the DRAFT is live | Refused before taking the lock; the discard then succeeds, and the closure afterwards |
| Stale discard, then retry | Stale; the retry succeeds; a repeat writes nothing; one event |
| M-7 Down/Up over DRAFT, ISSUED, SIGNED and PAID contracts | Rows unchanged, no discard data, constraints enforced (23514 / 23503), readiness reports only M-7 while missing, `__EFMigrationsHistory` ends with all seven in order, `Down` refused with a DISCARDED row |
| FK catalogue scenario | Now includes a discarded draft and closure (exercises `discarded_by_user_id`) |

**Test corrections in this run** (old → new assertions documented):
* `PostgresM1M4MigrationTests` (M-1..M-4): pending after Down is now M-1..M-4 **and M-7**.
* `PostgresInstallationWorkflowConcurrencyTests.Concurrent_assignments_…` (pre-existing): a loser may answer
  `TECHNICIAN_SCHEDULE_CONFLICT` or `INSTALLATION_STALE_REVISION`, depending on lock timing. Both refuse without writing. The
  invariants (one success, one assignment, revision +1) are unchanged. It previously demanded STALE only.
* My first M-7 race test waited for a lock that a closure never takes while a DRAFT is live, and it **hung** the earlier
  runs. It now asserts the immediate refusal. Every lock wait is bounded (30 s) so a test design error fails instead of hanging.

**Mutation check:** treating DISCARDED as live again for closure fails the E2E discard → close test (restored afterwards).

## 9. Flaky-test investigation (2026-10-31, latest; supersedes the "NOT VERIFIED" combined run in §8)

Fresh disposable PostgreSQL 17.11 and Redis instances were used, again running only from the session scratchpad. Their data was
deleted afterwards.

| Run (Release, clean build) | Total | Passed | Failed | Skipped |
|---|---|---|---|---|
| PostgreSQL only | 1,546 | 1,544 | 0 | 2 (Redis tests) |
| Redis only | 1,546 | 1,469 | 0 | 77 |
| **PostgreSQL and Redis together** | **1,546** | **1,546** | **0** | **0** |
| No infrastructure | 1,546 | 1,467 | 0 | 79 |

**Findings** (full evidence in [flaky-test-root-cause-analysis.md](flaky-test-root-cause-analysis.md)):

* **The combined-run failures of §8:**
  * Every HTTP test host started the dispatch worker, which shares the host's single in-memory SQLite connection with the
    test's requests.
  * A logged `SQLite Error 5: 'unable to delete/modify user-function due to active statements'` and a deterministic
    reproduction show this collision: the request answers 500 (`data: null`).
  * **Fixed in the test harness:** `Dispatch:WorkerEnabled=false`, plus a regression test.
  * The 401s themselves were not reproduced.
* **The 35-minute durations:** environmental. Run 1 overlapped the M-7 race test that hung before its fix; during run 3 a Debug
  build overwrote the assemblies of the running `--no-build` test run.
* **`Acceptance_and_revision_request_race_to_exactly_one_outcome`: NOT_REPRODUCED** (60 repeated runs, 20 of them with 1 CPU).
  * Both handlers lock the quotation row (`FOR UPDATE`) before reading, so the loser re-reads the winner's state.
  * Two new deterministic tests cover accept-first and revision-first. They hold the first transaction on the lock until
    PostgreSQL reports the second one waiting.
  * Mutation-checked: without the under-lock re-check both tests fail and the race test captures the lost update.
* **New: `PostgresPaymentIntegrityTests` intermittent failure: FIXED_AND_VERIFIED.**
  * The PostgreSQL log showed `ux_payment_attempt_order_code` violated by `7924652005645`: the same second and the same 4 random
    digits.
  * `PaymentService` now draws a new code for that specific violation, at most 3 tries.
  * A regression test replays the exact code.
* **`Two_replicas_backed_by_real_redis_share_one_limit`: CONFIRMED_NOT_FIXED.**
  * Failed 2 times under load, 4 OK instead of 3.
  * Cause unproven: either a window boundary or the 250 ms Redis budget falling back to per-instance limits.
  * The test now reports the counters and timings when it fails.

