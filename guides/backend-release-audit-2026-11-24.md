# Backend release audit — HCMC 2025 regional baseline + shared RAG publication

Date: 2026-11-24. Scope: backend repository only (working tree on `feat/payment-30-70-paper-contract`, base `770379b` = `origin/main@7d27054`
tree). Nothing committed, pushed, deployed or applied to Supabase. Companion documents:
[hcmc-regional-baseline-and-shared-rag.md](hcmc-regional-baseline-and-shared-rag.md) (method, contract, cutover/rollback runbook),
[FE_HANDOFF_HCMC_REGIONAL_RAG_SHARED.md](FE_HANDOFF_HCMC_REGIONAL_RAG_SHARED.md) (API contract for the FE).

**Status: BLOCKED for production** (code and test evidence complete on the development machine; blockers in §6). No test fails.

## 1. Changes made during this audit (on top of the reported implementation)

| Change | Why (evidence) |
| --- | --- |
| `RagServiceClient` adds a unique `requestId` to every signed body; RAG `StrictBody.requestId` (optional, `^[A-Za-z0-9-]{1,64}$`) | Live run: two identical questions in the same second had the same HMAC signature → second refused `replayed` → 503 to the user. Pre-existing, now frequent (shared allow-list). Replay of identical bytes still refused (pytest). |
| `RagServiceClient.Str/Int/Long` treat a missing JSON block as absent | A response without `usage` threw `InvalidOperationException` instead of "no usage" (pre-existing latent bug; unit test). |
| Solar Advisor cache key includes regional algorithm + manifest hash; request uses the same 1° rounding as the key | A cached yield could otherwise belong to another dataset/tilt within the process lifetime. |
| Tests: IoT test uses a controlled server clock (+ explicit 30 s ONLINE / 31 s OFFLINE assertions); rate-limit replicas use a 60-min window with `Retry-After ≤ window`; time-sensitive / CPU-heavy classes in a non-parallel collection; test-host thread-pool minimum | Root cause measured: the **unchanged HEAD** also failed 3 tests under the same full run (requests took 2.5–10 s; a simulation request hit the 100 s HttpClient timeout; fixed windows rolled over). Assertions unchanged or stricter; nothing skipped. |
| New tests: live .NET→Python RAG E2E (2), advisor/simulation consistency, client requestId/signature, requestId replay (pytest), in-process read p95 | Coverage requested by the audit matrix. |

## 2. Traceability (requirement → code → test)

| # | Requirement | Code | Test evidence | Status |
| --- | --- | --- | --- | --- |
| A1 | Verified boundary VN79, provenance, sha256, licence | `ReferenceData/RegionalBaseline/…/manifest.json`, `RegionalDataset.Load` | Re-download 2026-11-24: zip `0a74e8ee…`, file `d606ad96…` match; `Shipped_dataset_is_verified…`, `An_altered_or_incomplete_dataset_is_refused…` | PASS |
| A2 | Whole merged city incl. Côn Đảo, islands, multipolygon | sampler (40 polygons), `Contains` | Theory `The_boundary_is_the_merged_2025_city…` (9 points), Côn Đảo weight 0.0119 | PASS |
| A3 | Equal-area CRS, weights normalised, deterministic, reproducible | `hcm_regional_samples.py` (LAEA ellipsoidal) | area error 9e-11; regenerated files **byte-identical**; Σw = 1 | PASS |
| A4 | 20 km grid, 26 samples, ≤0.124 %/0.22 % vs 5 km | evidence JSON | Fresh-cache live re-measurement: 0.1243 % / 0.2223 % | PASS |
| A5 | E_site[m] = Y[m]·kWp; annual = Σ months; shading once | `RegionalAggregationCalculator`, `CreateSimulationHandler` | `Site_energy_is_the_regional_yield…`, `Flush_simulation…` (annual = Σ monthly, before − loss = after) | PASS |
| A6 | FLUSH building+free / RACK free kept; metadata, warnings, failures | handler | `Flush_simulation…`, `Rack_simulation…`, `Provider_failure…` | PASS |
| A7 | Partial failures explicit; no renormalised insufficient set; no zero | coverage ≥ 0.95 → DEGRADED, else FAILED | `A_missing_sample…`, `A_degraded_or_failed_baseline…`, `A_degraded_regional_baseline…` | PASS |
| A8 | `REGION_NOT_SUPPORTED`; no fallback to a site point | `ResolveMembership` | `Outside_or_unknown_sites…`, `A_disabled_model_never_falls_back…` | PASS |
| A9 | Legacy results unchanged, `SITE_POINT_PVGIS_LEGACY` | `EstimateBases.ForAlgorithm`, `WithBasis` | `Results_stored_before_the_regional_model…` | PASS |
| A10 | Fingerprint/version invalidation | fingerprint keys, `AlgorithmVersions.Current` | `…a_model_change_invalidates_reuse` | PASS |
| A11 | Three former provinces → same baseline; site inputs still matter | — | `Sites_in_the_three_former_provinces…`, `Different_roof_inputs…` | PASS |
| A12 | Advisor uses the same baseline | `SolarAdvisorService` | `Advisor_and_flush_simulation_report_the_same…`, `SolarAdvisorTests` | PASS |
| A13 | Timeout/retry/cache/concurrency | gates 4/3, shared flight, 25 s budget | `A_complete_baseline_is_shared…` (≤3 in flight, 26 calls for 2 callers), cancellation test | PASS |
| B1 | Admin-only management; JWT for chat; anonymous 401 | controllers | `Every_signed_in_role_retrieves…`, live E2E | PASS |
| B2 | Five roles retrieve the same approved published versions | `GetRetrievableVersionsAsync` | HTTP matrix + **live** E2E (same answer/citation for 5 roles) | PASS |
| B3 | Publish requires `confirmSharedVisibility`, audited | `PublishAsync` | unit + HTTP + live | PASS |
| B4 | Unpublished/archived/withdrawn/unapproved never exposed | allow-list (.NET) + `ANY()` (pgvector SQL) + re-filter (Python) | live E2E (no leakage, archive), pytest pgvector suite (executed) | PASS |
| B5 | Post-generation revalidation | `ChatService` | `A_version_unpublished_during_generation…` (CITATIONS_REVOKED) | PASS |
| B6 | Legacy INTERNAL blocked until review; review endpoint; approval activates | migration + review | `A_legacy_internal_document…`, Postgres cutover test, live E2E | PASS |
| B7 | Audience endpoint 410 | controller | `ApprovedPolicyEndpointsTests` | PASS |
| B8 | Historical restricted answers protected | `WithheldFor` | `A_legacy_answer_is_withheld…` | PASS |
| B9 | Reason codes reflect real conditions | `answering.py`, `ChatService` | pytest (all five incl. MODEL_INSUFFICIENT), live: NO_PUBLISHED_DOCUMENTS, BELOW_MIN_SCORE, NO_INDEXED_CHUNKS | PASS |
| B10 | HMAC, quotas, rate limits, budget, deadlines, prompt-injection | unchanged + requestId | existing suites (all pass), requestId tests | PASS |
| C1 | Migration additive; CUSTOMER carried, INTERNAL not; Up→Down→Up | `20261123140208_SharedKnowledgePublicationScope` | `PostgresSharedPublicationCutoverTests`, migration-list tests (disposable PG 17 + pgvector) | PASS |
| C2 | Migration applied to Supabase / production | — | not performed (no authorisation) | BLOCKED |
| N1 | Simulation p95 ≤ 25 s (30 runs, real providers) | — | Apple M5: p95 8.52 s / 7.99 s / 7.51 s (three runs) | PASS (dev machine only) |
| N2 | Same on VPS 3 vCPU | — | no authorised environment | NOT TESTED |
| N3 | RAG p95 ≤ 10 s, ≥ 50 questions, real LLM | — | needs real provider credentials | BLOCKED |
| N4 | Read p95 ≤ 1.5 s | — | in-process only: p95 ≤ 5.1 ms (not a VPS measurement) | NOT TESTED on VPS |

## 3. Test report (development machine: Apple M5, 10 cores, 16 GB, macOS, .NET 8.0.31 SDK, Python 3.12)

| Run | Dependencies | Total | Passed | Failed | Skipped |
| --- | --- | --- | --- | --- | --- |
| Baseline HEAD (unchanged code) | PG 17 + pgvector, Redis | 1958 | 1950 | **3** | 5 |
| This branch before the audit fixes | PG, Redis | 1989 | 1982 | 2 | 5 |
| This branch, 3 consecutive runs | PG, Redis | 1991 | 1984 | 0 | 7 (S3, live RAG, NFR opt-in) |
| **This branch, widest run** | PG, Redis, S3 (MinIO-compatible), live RAG service, NFR | **1994** | **1994** | **0** | **0** |
| Python RAG `pytest -q` (pgvector enabled) | PG + pgvector | 191 | 188 | 0 | 3 (paid OpenAI ×2, local Ollama) |

Command (widest): `env -u ConnectionStrings__DefaultConnection -u RabbitMq__Enabled -u Redis__Enabled -u Email__Enabled SMARTSOLAR_TEST_POSTGRES=… SMARTSOLAR_TEST_REDIS=… SMARTSOLAR_TEST_S3=… SMARTSOLAR_TEST_RAG=… SMARTSOLAR_NFR=1 dotnet test SmartSolar.sln`.
Not executed anywhere: paid OpenAI/Ollama smoke tests; frontend (out of scope).

## 4. NFR evidence

- Simulation (cold: new tilt each run → 52 PVGIS + NASA calls): raw lines in `tools/regional-baseline/evidence/nfr-sim-p95-2026-11-24-apple-m5.txt`.
- Read p95 (in-process TestServer + SQLite): simulation detail 5.1 ms, list 3.1 ms, chat sessions 1.1 ms, review 1.1 ms.
- Signed URL 300 s expiry at the storage server: PASS. PayOS reconciliation ≤ 2 min: PASS. Rate-limit burst 20/80: PASS.

## 5. Migration risk

Additive (2 nullable columns, 1 audit table, widened check). Up re-validates `ck_chat_message_retrieval_audience` (table scan under lock) and
back-fills only CUSTOMER published versions (idempotent guard `shared_scope_approved_at IS NULL`). Irreversible part: **Down drops
`knowledge_publication_approval`** — back it up first. The older API build keeps working; publications it makes stay unretrievable until
re-confirmed (fail-safe).

## 6. Release blockers / required actions

1. Explicit approval to apply the migration to Supabase (rehearse on a restored backup first) and to deploy.
2. Deploy order: **RAG service → migration → API → FE** (the old RAG service rejects `requestId` with 422).
3. Admin review of every document in `shared-visibility-review` after the cutover.
4. VPS 3-vCPU simulation benchmark and RAG p95 (≥ 50 questions, real provider) — not measured.
5. FE must send `confirmSharedVisibility` (otherwise Admins cannot publish).
