# SmartSolar V2 — flaky test root-cause analysis (2026-10-31)

Audience: backend reviewers, the QA lead and the release owner.

Scope:
* the local working tree (branch `fix/business-logic-v2` at `deddde8`, plus the uncommitted changes listed in §6);
* disposable PostgreSQL 17.11 (127.0.0.1:55432) and Redis (127.0.0.1:56379) instances, both running from the session
  scratchpad and deleted afterwards.

No shared, staging or production database was touched. No migration was generated or applied outside throwaway test databases.

## 1. Summary

| Test | Reproduction | Root cause evidence | Code fix | Regression test | Final status |
|---|---|---|---|---|---|
| 11–13 HTTP tests failing only in "combined" full runs (unexpected `401 AUTH_UNAUTHORIZED`, `data: null`, a 429 that should have arrived first) | Not reproduced in 12 later runs: 4 full combined runs (2 sequential, 2 concurrent) and 8 runs under 1 CPU with the fix reverted. A deterministic test reproduces the mechanism. | Every HTTP test host started the **dispatch worker**, which shares the host's single in-memory SQLite connection with the test's requests. Logged in a host: `SqliteException: SQLite Error 5: 'unable to delete/modify user-function due to active statements'` from `DispatchWorker` → `DispatchSweepService`. A request that hits the same collision returns **500 INTERNAL_ERROR** (`data: null`). | `AuthApiFactory` sets `Dispatch:WorkerEnabled=false`, like the four other workers. Test harness only. | `TestHostIsolationTests` (2 tests). Mutation: without the fix the test lists `["DispatchWorker"]`. | **FIXED_AND_VERIFIED** for the 500/`data: null` failures. The **401s: NOT_REPRODUCED** (the mechanism shown yields 500, not 401; §3.3). |
| The ~35-minute duration of those combined runs | Not reproduced: the same combination now takes 2.3–2.8 minutes. | Run 1 overlapped the M-7 race test that hung before its 06:23 fix. During run 3 a Debug `dotnet build` was executed at 08:02:40, 16.7 minutes in, into the same `bin/Debug` that the `--no-build` run was using. | None needed. Environmental. | — | **NOT_REPRODUCED**. The cause of run 3's duration was not proven. |
| `PostgresPaymentIntegrityTests.A_contract_is_paid_once_…`: `NullReferenceException` at line 47 (found in this phase) | Seen once in 9 constrained runs. The PostgreSQL server log identifies the cause exactly. | Server log: `duplicate key value violates unique constraint "ux_payment_attempt_order_code"`, `Key (order_code)=(7924652005645)`. Order codes are the clock in seconds × 10,000 plus 4 random digits. Under the frozen test clock both checkouts drew the same digits (1 in 10,000). `CreateCheckoutAsync` mapped that to `PAYMENT_CHECKOUT_IN_PROGRESS`, so `.Value` was null. | **Production:** `PaymentService` draws a new code when the insert violates **only** `ux_payment_attempt_order_code`, at most 3 tries. Every other conflict keeps its existing answer. | `PaymentTests` +2 (SQLite), `PostgresPaymentIntegrityTests` +1 (replays code 7924652005645). Mutation: without the redraw, PostgreSQL returns `PAYMENT_CHECKOUT_IN_PROGRESS`. | **FIXED_AND_VERIFIED** |
| `Acceptance_and_revision_request_race_to_exactly_one_outcome` (one unexplained failure in an earlier phase) | Not reproduced: 60 runs (40 normal, 20 with 1 CPU), plus every full run of this phase. | Code review: both handlers run through `RunAsync`, which takes `SELECT … FROM quotation … FOR UPDATE` **before** reading. Accept uses a status-guarded `ExecuteUpdate` that bumps `revision`. The loser waits and re-reads the winner's committed state, and the only lock taken is the quotation row (no cycle). | None (no defect found). The test now reports the stored status, every outcome event and each caller's result when it fails. | Two deterministic orderings (accept first, revision first), held on the quotation lock and released only once PostgreSQL reports the other session waiting. Mutation: without the under-lock re-check, both fail (lost update captured: `stored=REVISION_REQUESTED; events=[ACCEPTED, REVISION_REQUESTED×3]`). | **NOT_REPRODUCED** |
| `Two_replicas_backed_by_real_redis_share_one_limit`: 4 OK instead of 3 | 2 failures, both under load: the infrastructure-only run, and repro run 1 under 1 CPU. | The monitored passing run shows all six asks in one window. In the failing runs the cause was not captured: either a window boundary (`floor(now/window)`) or the 250 ms Redis budget expiring, which hands a replica to its per-instance window **by design**. The fallback warning is throttled by a process-wide static (`_lastFallbackLog`, once per minute), so it could not tell the two apart. | None. Changing the limiter's budget or fallback would change approved behaviour. | The test now reports each ask's status and latency and every Redis counter key with its value. | **CONFIRMED_NOT_FIXED** (intermittent under CPU starvation; cause not proven). See §3.5 for the latest attempts. |

## 2. Configurations (this phase, Release, clean build: 0 warnings, 0 errors)

The command for each (real-service variables unset by the scratchpad wrapper):

```
dotnet test tests/SmartSolar.Tests -c Release --no-build \
  --logger "trx;LogFileName=<name>.trx" --logger "console;verbosity=normal"
# B: SMARTSOLAR_TEST_POSTGRES="Host=127.0.0.1;Port=55432;Username=postgres;Password=<random>;Database=postgres"
# C: SMARTSOLAR_TEST_REDIS="127.0.0.1:56379"
# D: both
```

* **Parallelisation:** xUnit defaults (test collections in parallel). The only change was `DOTNET_PROCESSOR_COUNT=1`, used
  only in the diagnostic runs (§3).
* **PostgreSQL:** each test creates and drops its own `smartsolar_test_<guid>` database.
* **Redis:** each test uses its own key prefix.

| Config | Total | Passed | Failed | Skipped | Duration |
|---|---|---|---|---|---|
| A: no infrastructure | 1,546 | 1,467 | 0 | 79 | 1.28 min |
| B: PostgreSQL only | 1,546 | 1,544 | 0 | 2 (the two `[RedisFact]` tests) | 2.26 min |
| C: Redis only | 1,546 | 1,469 | 0 | 77 (the `[PostgresFact]` tests) | 1.33 min |
| **D: PostgreSQL and Redis** | **1,546** | **1,546** | **0** | **0** | 2.50 min |

**Earlier D runs in this phase, before the fixes:**

| Run | Build | Result | Duration |
|---|---|---|---|
| Release | before the fixes | 1,539 / 1,539 / 0 / 0 | 2.32 min |
| Debug | before the fixes | 1,539 / 1,539 / 0 / 0 | 2.79 min |
| Two Release suites at once | before the fixes | each 1,539 / 1,539 / 0 / 0 | about 2.7 min |

**Reading the "Redis suite" figures:**
* `DistributedRateLimitingTests` has 9 tests. 7 use fake counters and always run; 2 are `[RedisFact]` and run only with
  `SMARTSOLAR_TEST_REDIS`.
* So "2 Redis tests skipped" (configuration B) and "9 tests passing under the Redis filter" describe the same 2 tests, not a
  discrepancy.

## 3. Investigation details

### 3.1 What differed between failing and passing combined runs

* **Redis does not trigger the failures:**
  * the Redis-only configuration passes;
  * only one test builds Redis-enabled hosts;
  * a failing test (`Two_replicas_share_one_limit_per_user`) uses a fake counter.
* **Not the build configuration:** the failing runs were Debug, but Debug D passes.
* **When the failures happened:** in the 35-minute run 1, the HTTP failures were already reported at `00:00:43`, inside the
  first minute. The 35-minute duration was a separate effect.
* **The slowdown is process-wide:** even passing runs have a window of thread-pool starvation in the first ~60 s.
  * HTTP tests that normally take under 1 s took 50–56 s.
  * Pure unit tests such as `PanelLayoutCalculatorTests` and `PvgisV53ClientTests` took 14–16 s.
  * Unrelated tests finished in tight clusters (≈42.8–44.5 s and ≈57–59 s), the pattern of thread injection releasing queued work.
* **Ruled out:**
  * shared clocks (each context has its own `FixedTimeProvider`);
  * JWT expiry from a static clock (`Generate` uses the real `UtcNow`; lifetime 15 min, skew 30 s);
  * shared Redis state (unique key prefixes);
  * a second `WebApplicationFactory` type (`AuthApiFactory` is the only one).

### 3.2 The shared-connection collision (confirmed)

* **The setup:** `AuthApiFactory` gives every host one open `SqliteConnection("Filename=:memory:")`. Every `AppDbContext`
  constructs `SqliteRelationalConnection`, whose `InitializeDbConnection` calls `SqliteConnection.CreateFunction(...)` on that
  same connection.
* **What SQLite does:** it refuses that with `SQLITE_BUSY` while another context has a statement running.
* **What was running beside the tests:**
  * `DispatchWorker` defaults to `WorkerEnabled = true`;
  * it runs its first sweep **immediately** at host start (`do { RunOnceAsync } while (tick)`), then every 60 s;
  * `AuthApiFactory` disabled four other workers but not this one.
* **Evidence:**
  * In a passing run, a host logged `Dispatch sweep failed … SQLite Error 5: 'unable to delete/modify user-function due to
    active statements'`, stack `DispatchUnitOfWork.ListDueProposalExpiriesAsync` → `DispatchSweepService.RunOnceAsync` →
    `DispatchWorker.ExecuteAsync`.
  * A deterministic diagnostic held a statement open on the shared connection and sent an authenticated request.
    * JwtBearer logged `Exception occurred while processing message` (same SQLite error, from `AccessTokenRevocationCheck` in
      `OnTokenValidated`) and **rethrew**.
    * `ExceptionHandlingMiddleware` answered `500 INTERNAL_ERROR` with `data: null`.
  * The diagnostic was temporary and has been deleted. The permanent test asserts the SQLite error code 5.
* **Other effects of the same collision:**
  * If the collision hits the schema-readiness probe of the first request, `SchemaReadiness` answers `503 SCHEMA_NOT_READY`
    for 30 s.
  * If it hits the worker, the error is swallowed.
* **Not a production defect:** in production each `DbContext` takes its own pooled Npgsql connection.

### 3.3 What remains unexplained

* **No 401 from the collision:** the mechanism above produces 500s (and possibly 503s), not `401 AUTH_UNAUTHORIZED`. A 401 needs
  token validation to fail or `IsCurrentAsync` to return false (account missing, inactive or on a different auth version).
* **No reproduction:** none of the 12 later runs produced a 401, including 8 under 1 CPU with the fix reverted. So the earlier
  401s stay **NOT_REPRODUCED**.
* **Consistent but not proof:** SQLite's busy retry (`CommandTimeout='30'`) matches the ~37 s durations of the failing tests,
  but that alone doesn't prove the cause.
* **What to keep:** run combined suites with `console;verbosity=normal` and `Logging__LogLevel__Microsoft.AspNetCore.Authentication=Information`,
  so any future 401 logs its JwtBearer failure reason.

### 3.4 Rate-limit counts

* **The test assumption:** `CatalogRateLimitTests.Read_and_write_budgets_are_separate` (expected 429, got OK) assumes two
  consecutive requests fall into one per-instance `FixedWindowRateLimiter` window.
* **Under starvation:** a request can take tens of seconds, so the two can land in different windows. That is timing, not a
  limiter defect.
* **No longer seen:** it did not recur after the worker fix, in any run of this phase.

### 3.5 Real-Redis two-replica test

**Passing run (Redis MONITOR capture):**
* six `INCR`s, all in window `…:29890650` (60 s windows);
* the asks took 0.2–0.7 s each under 1 CPU, close to the limiter's 250 ms Redis budget.

**Two candidate causes for a fourth OK:**
* the asks crossing a window boundary;
* one Redis call exceeding the budget, which makes that replica decide locally (approved fail-to-local policy).

**Result:** the test now reports which of the two happened, but neither later attempt reproduced a failure:
* **repro runs 2–8:** passed.
* **Targeted attempt:** 25 isolated runs of the test with 1 CPU. All 25 passed.
  * A concurrent PostgreSQL suite loaded the machine, but it finished in 43 s (78/78 passed), so it overlapped only the first
    runs.
  * Not reproduced in this attempt; the status stays CONFIRMED_NOT_FIXED on the strength of the two earlier failures.

## 4. Production code changed

| File | Change |
|---|---|
| `src/SmartSolar.Modules/Payments/PaymentService.cs` | `CreateOrGetAttemptAsync` and `CreateCheckoutAsync` draw a new order code when the insert violates `ux_payment_attempt_order_code` (PostgreSQL), or `payment_attempt.order_code` (SQLite text), with at most 3 tries. Every other conflict keeps its existing answer. New optional constructor parameter `orderCodes` (not registered in DI, so production keeps the random generator). Order-code format, description format, the one-payable rule and every error code are unchanged. |

## 5. Test code changed

* **`TestSupport/AuthApiFactory.cs`:** `Dispatch:WorkerEnabled=false`.
* **New: `Integration/TestHostIsolationTests.cs`:**
  * test hosts start no `SmartSolar.Api.Background` worker;
  * a second context on the shared connection fails with SQLite error 5 while a statement is active.
* **`Unit/InstallationWorkflow/PaymentTests.cs`** (+2): order-code collision redrawn; redraw bounded to 3.
* **`Integration/AfterSales/PostgresPaymentIntegrityTests.cs`** (+1): a combined checkout repeating an issued order code gets a fresh code.
* **`TestSupport/InstallationWorkflowTestContext.cs` and `AfterSalesTestContext.cs`:** an optional `OrderCodes` hook.
* **`Integration/Persistence/PostgresSalesClosureConcurrencyTests.cs`:**
  * `LockHoldProxy.Wrap(inner, holdOn)`; the default is unchanged;
  * +2 deterministic accept/revision orderings.
* **`Integration/InstallationWorkflow/PostgresInstallationWorkflowConcurrencyTests.cs`:** the acceptance race asserts with
  evidence. Same checks as before.
* **`Integration/RateLimiting/DistributedRateLimitingTests.cs`:** the real-Redis two-replica test asserts with evidence. Same
  checks as before.

No assertion was weakened, no test was skipped or deleted, and no authorization, rate-limiting or concurrency protection was
changed.

## 6. Other checks

* `dotnet ef migrations has-pending-model-changes`: no changes. No new migration; the latest is still `20261031043834_ContractDraftDiscard`.
* `git diff --check`: clean. Line endings and BOM match HEAD for every changed file (all LF, no BOM).
* **Git:** I made no commit, push, deploy, reset, stash or clean. The commit `deddde8` and the switch to the branch
  `fix/business-logic-v2` (both at 16:41 +07) happened outside this session. They hold the earlier uncommitted work and the
  seven migrations. The changes in §4–§5 made after that are uncommitted.

## 7. Remaining gaps

1. The original 401s are not reproduced or explained (§3.3).
2. `Two_replicas_backed_by_real_redis_share_one_limit` is intermittent under CPU starvation; its cause is unproven (§3.5).
3. Order codes still depend on the clock second plus 4 random digits. The collision is now handled, but a sequence-based code
   would remove it entirely. That needs a schema change, which was not done.
4. A database error during the per-request account check answers 500, not 401. That is existing JwtBearer behaviour and was not
   changed (authorization behaviour is out of scope without approval).
