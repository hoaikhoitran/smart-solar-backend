# NFR verification report (2026-11-18, backend)

The thresholds are the ones already approved in the repository: `guides/full-regression-test-report.md` §7 (lines 150–157) and
`guides/full-project-production-audit.md` (lines 373–384). No threshold was invented or relaxed.
Evidence: `TestResults/qa-2026-11-18/nfr/` (TRX files `nfr-opt-in-2.trx` and `api-read-load/`).

**Scope of every result.** All measurements ran on the development machine: macOS Darwin 25.2 on Apple silicon, running on battery, with the load
generator and the API on the same host. They ran against local Docker infrastructure (PostgreSQL 17 + pgvector, Redis 7, S3-compatible `pgsty/silo`).
None was taken on the VPS or the production stack, and none used Supabase. Each PASS means "meets the threshold in this environment".
Production performance still needs one run on the VPS (see the last section).

## Results

| NFR | Approved target | Test environment | Test method | Actual result | Status |
|---|---|---|---|---|---|
| API read latency | p95 ≤ 1.5 s, 50 VUs, 10 min | Release API (Kestrel, `127.0.0.1:5099`), local PostgreSQL 17, background workers off, rate-limit permits raised for the single load host | ApacheBench 2.3, 4 processes in parallel, keep-alive, closed loop, **zero think time**, concurrency 13+13+12+12 = **50**, `-t 600` (**10 min**); sleep prevented with `caffeinate`. Dataset: **5 000 products** (10 % inactive), **2 000 user accounts**. Endpoints: `GET /api/products` (paged), `GET /api/products?search&productType` (search + filter), `GET /api/admin/products` (ADMIN JWT), `GET /api/admin/users` (ADMIN JWT), all `pageSize=20` | **1 605 370 requests, 0 failed, 0 non-2xx.** Combined p50 17 ms, **p95 36 ms**, p99 53 ms, max 240 ms (2 676 req/s). Per endpoint p95: 33 / 37 / 41 / 35 ms. 0 requests > 1.5 s | **PASS** (local) |
| Simulation latency | p95 ≤ 25 s over ≥ 30 runs | Test host (`AuthApiFactory`, SQLite), **real PVGIS v5.3 and NASA POWER** over the internet (production `ExternalProvidersRegistration`, production `appsettings.json` base URLs) | `NFR_SIM_P95_…`: 30 simulations through `POST /api/pre-surveys/{id}/simulations`, each with a different tilt, so every PVGIS call misses the cache. Every run must return 201; provider failures are never turned into energy. The test asserts the synthetic providers were never called | n = 30: min 0.46 s, p50 0.52 s, **p95 0.55 s**, max 1.44 s (first run: p95 0.73 s, max 1.73 s) | **PASS** (local, real providers) |
| PayOS PAID confirmed | ≤ 2 min | Test host with the production `PaymentService.ReconcileDueAsync` and the production worker timing (`ReconciliationIntervalSeconds` 30, `ReconcileAfterSeconds` 60, batch 25 × 20) | `NFR_PAYOS_RECON_…`: one attempt with a lost webhook; the PayOS fake reports PAID; the worker's first tick after payment is placed at five phases (0.5, 1, 15, 29, 29.9 s); the virtual time until the attempt is PAID is measured. Capacity per tick by design is 25 × 20 = 500 due attempts; a backlog larger than that was not measured | worst case **89.0 s**. Before the pass-3 fix the design allowed about 180 s, a **defect** that is now fixed | **PASS** |
| Rate limiting under concurrency | limit admitted exactly; excess → 429 | Test host, catalog-read policy with a limit of 20 | `NFR_RL_CONCURRENT_…`: 100 concurrent anonymous `GET /api/products` | **200: 20, 429: 80** (exact). The first run found 93 × 503 `SCHEMA_NOT_READY`, a **defect** (cold-start race) that is now fixed | **PASS** |
| Signed URL expiry | TTL 300 s | Real S3-compatible server (`pgsty/silo`, local Docker), production `S3StorageUrlSigner` | `NFR_SIGNED_URL_…`: read URL signed with TTL 300; GET before expiry, then wait until the **storage server's** clock passes `X-Amz-Date` + 300 s, GET again | `X-Amz-Expires` = **300**; before expiry **200**, after expiry **403** | **PASS** (S3-compatible; AWS / production bucket not measured) |
| 401 / 403 | every protected endpoint | Test host | existing endpoint auth tests plus pass-2/3 role tests | all pass in the final regression | **PASS** (functional) |
| RAG latency and accuracy | p95 ≤ 10 s on the VPS; accuracy / citation | – | – | The RAG service (`rag-service:8000`, Python) is not in this repository. The configured models are not installed locally (Ollama has only `qwen2.5-coder:7b`). The target is defined on the VPS | **NOT MEASURED** |
| IoT throughput | no approved threshold | – | – | – | **NOT APPLICABLE** (no threshold) |

## Notes on method

* **API read.** A closed loop with zero think time is harsher than 50 k6 VUs with think time: each connection sends its next request as soon as the
  previous one returns. Percentiles were computed from the per-request `ttime` of every request in the `ab -g` output (the raw files, 75 MB, were not
  kept; `summary.txt` and the four `ab` reports were kept).
* **Discarded first API run.** The first attempt (`api-read-load/invalid-run-1-system-sleep/`) was invalidated by macOS idle sleep: the `pmset` log
  shows sleep at 09:17:55 for 971 s, which froze client and server and produced a 969 s "longest request". It is not counted. It was repeated with
  sleep prevented, and the repeat showed no stall.
* **Rate-limit permits.** Permits were raised only for the load run because all 50 connections come from one IP and would otherwise be throttled by
  design. The rate limiter itself is measured separately (row 4).
* **Simulation.** Response time depends on the public PVGIS / NASA services at the time of the run. Each run's climatology request for the same site
  may be served from the application cache after the first, as in production.

## Re-running

```sh
# opt-in NFR tests (≈ 4 min; NFR_SIGNED_URL needs SMARTSOLAR_TEST_S3, NFR_SIM_P95 needs internet)
SMARTSOLAR_NFR=1 SMARTSOLAR_TEST_S3="http://127.0.0.1:59000|<key>|<secret>|http://localhost:3000" \
  dotnet test tests/SmartSolar.Tests --filter "FullyQualifiedName~NfrMeasurementTests"
```

The PayOS and rate-limit NFR tests run in every regression; the other two are opt-in because they need the network or take several minutes.

## Before production

1. Repeat the API read run on the VPS against a production-sized copy, from a separate load host (k6 or ab), with the real rate-limit settings.
2. Measure RAG p95 and accuracy on the VPS with the configured models (the earlier dev-machine figures in `rag-local-ollama.md:138-153` are not a VPS
   measurement).
3. Check the signed-URL expiry once on the production bucket.
