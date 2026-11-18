# Final backend acceptance (2026-11-18)

Branch `feat/cloud-rag-security-cost-control`, HEAD `4fb5b95` plus an **uncommitted** working tree (three remediation passes). Backend only:
no frontend code was changed in passes 2 and 3. Nothing was committed, pushed, merged or deployed. Supabase and production were not touched.

## Status

| Area | Status | Why |
|---|---|---|
| **Backend implementation** | **COMPLETE for every decided requirement** | Every flow in scope is implemented and tested. Two items are **blocked by business decisions**, not by code: manufacturer warranty and refund execution (see "Outstanding decisions") |
| **Backend QA** | **PASS** | full regression 1 894 / 1 896 passed, **0 failed**; the 2 opt-in NFR tests ran separately and passed (4/4); API read load p95 36 ms. RAG NFR **not measured** |
| **Production readiness** | **NOT READY** | the work is uncommitted; the 2 migrations are not applied to Supabase (approval needed); real labor / part prices are not entered; NFRs not measured on the VPS (API, RAG); the FE has not implemented the handoff |

## 1. Business completion matrix

| Flow / requirement | Status | Evidence |
|---|---|---|
| Installation: pre-survey → quotation → contract (paper scan by Sales) → payment (cash / PayOS / combined with packages) → customer-chosen date → installation → handover | DONE | `InstallationLifecycleE2ETests` E2E_01..10, `CommercialRemediationE2ETests` E2E_R01..R04, R07 |
| Warranty / repair: fault → inspection → coverage → technician-priced quotation → cash / PayOS → repair → acceptance | DONE | `AfterSalesRemediationE2ETests` E2E_AS_01..05, `TechnicianPricedQuotationTests` |
| Maintenance (package, prepaid visits) | DONE | `ServicePackageTests`, MAINT_011 |
| **One-time maintenance, technician-priced (pass 3)** | DONE | MAINT_001..014, `E2E_MAINT_015` |
| After-sales pricing configuration (catalog labor / parts, missing price refused, search / filter / page) | DONE (mechanism); **real prices: business input** | `AfterSalesPricingConfigurationTests`, MAINT_005 |
| Disaster protection (waiting period, reconsideration, capped support) | DONE | `Disaster_claims_…`, `Disaster_support_is_capped_…`, `Parallel_quotations_…`, AFTER_QUOTE_004 |
| After-sales cash payment | DONE | `AfterSalesCashPaymentTests`, `PostgresAfterSalesRemediationTests` (race) |
| Payment reconciliation ≤ 2 min | DONE (**defect fixed in pass 3**) | NFR_PAYOS_RECON 89 s |
| Unapplied money | DONE as a **read-only ledger**; execution is a business decision | DASHBOARD_001, endpoint tests |
| Refund execution | **BLOCKED (decision)**: refunds are outside the application (team decision of 2026-11-13) | – |
| Manufacturer warranty | **BLOCKED (decision)**: refused with `AFTER_SALES_MANUFACTURER_WARRANTY_POLICY_PENDING` | `Manufacturer_warranty_is_a_separate_category_…` |
| Admin dashboard KPIs (money, counts, overview) | DONE | DASHBOARD_001/002, DASHBOARD_OVERVIEW_001..003, PostgreSQL translation test |
| Administration (catalog, users, settings) | DONE | existing admin endpoint tests + pricing configuration |

## 2. API completeness matrix

| Check | Result | Evidence |
|---|---|---|
| Endpoints | 348 controller actions in 60 controllers (142 GET, 160 POST, 35 PUT, 2 PATCH, 9 DELETE) | source count |
| Rate limiting | **348 / 348** actions carry a named policy (no exemption); 429 keeps the envelope and `Retry-After` | `EndpointRateLimitCoverageTests` (live route table), `NFR_RL_CONCURRENT` (exactly 20 admitted of 100), `DistributedRateLimitingTests` (2 replicas share one Redis window) |
| Authorization | 401 / 403 tested for every new route; ownership filters before COUNT | endpoint tests, `New_routes_admit_only_their_roles` |
| Pagination | 23 list families paged (13 added in passes 1–2, 9 already paged, plus the new `unapplied-money`). Opt-in: legacy array without `page`/`pageSize`; `page` 1–10 000, `pageSize` 1–100; the rest are bounded or per-object lists (handoff §10 lists each with its reason) | `Integration/Paging`, `Unit/Paging` |
| Validation | 400 `VALIDATION_FAILED` envelope; catalog price / unit / decimals validated | pricing configuration test, endpoint tests |
| Schema readiness | 503 `SCHEMA_NOT_READY` only while the schema is really behind (**cold-start race fixed in pass 3**) | `SchemaReadinessTests` |
| FE contract | API_FE_INTEGRATION_HANDOFF.md §1–15; `cashPurchaseAllowed` declared **obsolete** (§15) | – |

## 3. Automated test results

| Suite | Passed | Failed | Not executed | Evidence |
|---|---|---|---|---|
| Unit | 1 403 | 0 | 0 | `TestResults/qa-2026-11-18/pass3/pass3-final-3.trx` |
| Integration | 469 | 0 | 2 (opt-in NFR) | same |
| End-to-end | 22 | 0 | 0 | same |
| **Total** | **1 894** | **0** | 2 | same (5 m 5 s, PostgreSQL 17 + Redis 7 + S3-compatible) |
| Opt-in NFR | 4 | 0 | 0 | `TestResults/qa-2026-11-18/nfr/nfr-opt-in-2.trx` |

Two earlier pass-3 runs had failures; both are kept and explained in TEST_EXECUTION_REPORT.md. Run 1 hit 3 failures caused by laptop sleep;
they pass when re-run awake. Run 2 hit 1 failure in an existing flaky Redis window test; the test was fixed and its assertions are unchanged.
No test was disabled or skipped to hide a failure.

## 4. NFR measurements (details: NFR_VERIFICATION_REPORT.md)

| NFR | Approved target | Actual | Status |
|---|---|---|---|
| API read | p95 ≤ 1.5 s, 50 VUs, 10 min | 1 605 370 requests, 0 errors, **p95 36 ms**, max 240 ms (local, same host) | PASS (local) |
| Simulation | p95 ≤ 25 s, ≥ 30 runs | n = 30 with real PVGIS / NASA, **p95 0.55 s** | PASS (local) |
| PayOS PAID | ≤ 2 min | worst case **89 s** (was about 180 s by design: defect fixed) | PASS |
| Signed URL | TTL 300 s | `X-Amz-Expires` 300; 200 before, 403 after | PASS (S3-compatible) |
| 401 / 403 | – | all pass | PASS |
| RAG | p95 ≤ 10 s on the VPS + accuracy | service and models not available here | **NOT MEASURED** |

## 5. Database

| Item | Result |
|---|---|
| New migrations | `20261117171006_InstallationSlotOffers`, `20261117185657_AfterSalesTechnicianQuotation` (additive). None in pass 3 |
| Model ↔ snapshot | no pending model changes |
| Applied | disposable local PostgreSQL only (every `[PostgresFact]`, plus a local up / down / idempotency rehearsal) |
| Supabase | **not applied**. Read-only preflight `TestResults/qa-2026-11-18/migration/supabase-readiness.preflight.sql` validated locally |
| Compatibility with the older live API | data-compatible. Operational caveat: during a mixed-version window, plan one-time maintenance inspection visits from the new build (MIGRATION_VERIFICATION.md §7) |

## 6. Outstanding decisions (minimal, owner: business)

| # | Decision | Needed for | Options |
|---|---|---|---|
| 1 | Manufacturer warranty **start date** | enabling manufacturer-warranty coverage | installation handover date, or the manufacturer's registration / production date |
| 2 | Manufacturer **claim handling**: who files and who pays parts / labor while a claim is pending | same | SmartSolar files and advances the cost, or the customer is charged and reimbursed |
| 3 | **Refund execution** for unapplied money: approver and channel | moving beyond the read-only ledger | e.g. Manager / Admin approval; PayOS refund or bank transfer |
| 4 | **Real labor and part prices** | technician quotations in production | entered by the Admin in the catalog (no price was invented) |
| 5 | **Supabase migration approval** + backup / rehearsal window | production deploy | MIGRATION_VERIFICATION.md §6 checklist |
| 6 | **VPS NFR run** (API read, RAG) | production sign-off | NFR_VERIFICATION_REPORT.md "Before production" |

## 7. Git status (actual, `git status --short`, 2026-11-18 11:2x +07)

71 tracked files modified (2 141 insertions, 640 deletions), plus these untracked paths: `guides/remediation-20261118/`, 10 new source files
(2 migrations with designers, `PortalRateLimitingExtensions`, `AdminOverviewQueries`, `AdminOverview`, `ServiceQuotationTechnicianPricing`, `PageRequest`,
`InstallationSlotOfferHandlers`), and 22 new test files / folders. `TestResults/` is git-ignored. **Nothing committed or pushed.** HEAD is still `4fb5b95`.
The full list is in the final report of this session.

## Documents

BUSINESS_REMEDIATION_REPORT.md · API_FE_INTEGRATION_HANDOFF.md · ADMIN_DASHBOARD_METRICS.md · MIGRATION_VERIFICATION.md ·
TEST_CASE_TRACEABILITY.md (Report 5 input; LOC not measured) · TEST_EXECUTION_REPORT.md · NFR_VERIFICATION_REPORT.md · this file.
