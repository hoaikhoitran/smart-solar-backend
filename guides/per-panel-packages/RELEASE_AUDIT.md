# Phase 3 pre-release audit — per-panel service packages

Date: 2026-11-25. Scope: local working tree of `feat/hcmc-regional-rag-shared` (HEAD `9b86e33`, uncommitted Phase 3 changes), migration
`20261124173442_PerPanelServicePackages`. No production connection, no Supabase migration, no commit / push / PR / deploy, no FE change.

## Decision: `READY_FOR_STAGING` (technical) — production release NOT approved

Technically ready to be reviewed, committed and deployed to **staging**. Production remains gated by the non-technical approvals in §5 and by the
release conditions in §6. Nothing below claims production readiness.

## 1. Business verification (evidence = tests on the production handlers; see TEST_REPORT.md)

| Requirement | Verified by | Status |
| --- | --- | --- |
| Technician lists active PER_PANEL packages; selects / replaces / removes on own DRAFT | `TC27_C_…`, `TC07_TC28_…`, HTTP role matrix | OK |
| Quantity = Σ SOLAR_PANEL units (multi-SKU; fractional/zero refused; atomic on line change) | `TC03_TC04_…`, `TC05_…`, `TC06_…` | OK |
| Snapshot of price and benefits at issue; frozen afterwards | `TC08_TC09_TC10_…` | OK |
| Quotation and contract include installation + packages; `contract.TotalAmount` = grand total | `TC11_TC12_…`, `TC09` | OK |
| 30/70 on the exact total; 57,000,000 → 17,100,000 + 39,900,000 | `TC11_TC12_…` | OK |
| No second charge for bundled packages (single installment line; verified money = contract total) | `TC11_TC12_…`, `TC13_…`, PostgreSQL race test | OK |
| No activation before full payment and handover; reconciliation on mismatch | `TC13_…`, `TC15_TC29_…`, `TC19_…`, `TC20_…` | OK |
| Retail, renewal, overlap, claims | `TC21_TC22_…`, renewal test, `TC23_…`, `TC24_…`, PostgreSQL concurrency tests | OK |
| Legacy contracts, subscriptions and settled payments unchanged | `TC01_TC26_…`, `TC26_…`, migration Up–Down–Up, all pre-existing suites green | OK |

Code review checks done in this audit: every path that ends a 30/70 contract (`PaymentPlanLedger.ReleaseOnWithdrawalAsync` is the only one; staff
discard refuses 30/70 contracts) discards the commitments; legacy contract creation paths refuse package quotations; provisioning is idempotent
(unique index) and runs in the payment confirmation transaction.

### Defect found and fixed in this audit

| Defect | Evidence | Fix |
| --- | --- | --- |
| An installation-wide (legacy) package purchase was checked against per-panel coverage only **before** the checkout transaction and not at settlement; because the database exclusion now applies to INSTALLATION scope only, a concurrent per-panel purchase of the same kind could produce double coverage of the same panels | code path `ServicePackageHandlers.PurchaseAsync` (pre-check only), `AfterSalesPaymentSettlement.SettleSubscriptionAsync` (no check for INSTALLATION scope) | cross-scope re-check under the contract row lock inside the checkout transaction (`ServicePackageHandlers.cs`, reserve callback) and at settlement (`AfterSalesPaymentSettlement.cs`); new test `Concurrent_installation_wide_and_per_panel_purchases_of_one_kind_never_both_cover_the_panels` |

FE handoff correction: `includeContract` with a per-panel package on a 30/70 contract returns 409 `AFTER_SALES_INVALID_STATE` (existing check), not the
pricing-unit code; `FE_HANDOFF.md` corrected. All other documented routes, fields and codes were checked against `QuotationControllers.cs`,
`AfterSalesControllers.cs`, `PaymentPlanControllers.cs`, `ContractControllers.cs` and the view records.

## 2. Financial verification

- Mandatory example asserted exactly (contract 57,000,000; installments 17,100,000 / 39,900,000; allocations 15,000,000 + 2,100,000 and
  35,000,000 + 4,900,000; Σ allocations = installments = total; rounding remainder test).
- PayOS: fake server only (signed webhooks, link status). Online deposit start, online balance, duplicate webhook (`DUPLICATE`), webhook ×2 +
  reconciliation ×2 race on PostgreSQL → one settlement, one subscription, verified money = 57,000,000. **No live / sandbox PayOS call was made.**
- Cash: Manager/Admin confirmation with evidence and exact amount; a wrong amount is refused; technician gets 403 on the confirmation route.
- Finance overview: `collectedByComponent` splits installation / bundled packages / retail without double counting (`TC30_…`).
- Collections: overdue balance case for the full 70 % (`TC15_TC29_…`).

## 3. Migration verification

- Source and snapshot inspected; `dotnet ef migrations has-pending-model-changes` → "No changes have been made to the model since the last migration".
- Offline scripts generated (`dotnet ef migrations script`, no database): Up and Down each run in **one transaction** (`START TRANSACTION … COMMIT`).
- Additive: 3 tables (RLS enabled), nullable columns or NOT NULL with constant defaults carrying the old meaning (PostgreSQL ≥ 11: metadata-only add);
  check constraints validate existing rows (`ck_customer_contract_package_breakdown`, subscription scope/units); the exclusion
  `ex_service_subscription_no_overlap` is dropped and re-created with `AND coverage_scope = 'INSTALLATION'` (re-built over `service_subscription` under an
  exclusive lock — small table, but run it in a low-traffic window).
- Up–Down–Up on disposable PostgreSQL 17.12 with a legacy row: OK. Down refused while per-panel data exists: OK.
- **Production rollback after any per-panel sale requires restoring a backup** (the Down guard refuses by design). Before any sale, Down is clean.
- Compatible with the previous API build (defaults); see the release conditions for the one unsafe interleaving.

## 4. Testing (final code)

`dotnet test SmartSolar.sln` with disposable PostgreSQL 17.12 (pgvector) + Redis 7: **2025 total, 2018 passed, 0 failed, 7 skipped**. Skipped (opt-in,
dependencies not started): S3-compatible upload chain ×3, live RAG ×2, NFR ×2. No assertion was weakened; the three migration tests changed in Phase 3
use column projections / exclude new columns from the row comparison and assert those columns separately (TEST_REPORT §1).

Not covered: live PayOS; an HTTP-level E2E of the whole bundle (the bundle E2E runs on the production handlers, the HTTP layer is covered by the
role / validation tests); successful reconciliation retry after a real equipment correction; FE; VPS / production load.

## 5. Approvals that are NOT technical (not invented, still open)

| Item | Owner | State |
| --- | --- | --- |
| Tax / VAT treatment of package prices (catalog final price, no quotation tax added) | Finance / Legal | **Pending** |
| Legal wording of the generated package clauses 7–8 (terms version still marked pending legal review) | Legal | **Pending** |
| Coverage transfer on panel replacement (currently never transferred) | Product | **Pending** |
| Policy for installed ≠ contracted panels (refund / partial activation / subset) | Product + Finance | **Pending** |
| Approved per-panel catalog prices | Admin / Business | Not configured (no price ships in code) |

## 6. Release conditions (blockers for production, not for staging)

1. **Do not create any PER_PANEL package while an older API build can still serve requests.** An older instance would agree a package quotation
   without its packages (contract total = installation only). Per-panel quotations cannot exist until such a package exists, so the deployment order
   below prevents it.
2. Backup + rehearsal on a restored copy before the migration; migration applied only with explicit approval.
3. Approvals in §5 for production sale (tax at minimum, and legal clauses).

## 7. Release checklist (in order)

1. **Review and commit** the Phase 3 working tree (owner review of `git diff`; this audit made no commit).
2. **PR / CI**: full .NET suite with PostgreSQL + Redis (expect 2025 / 0 failed; opt-in suites as configured).
3. **Database backup** of Supabase; restore a copy and rehearse Up (+ Down guard) there.
4. **Approved migration** `20261124173442_PerPanelServicePackages` (low-traffic window; exclusive lock while the exclusion is rebuilt).
5. **Backend deployment**; confirm no older instance remains (condition 6.1).
6. **Admin catalog configuration**: create PER_PANEL packages with approved prices (legacy packages remain PER_INSTALLATION_LEGACY).
7. **FE integration** per `FE_HANDOFF.md` (30/70 on `grandTotal`).
8. **Staging E2E** (bundle 30/70 with PayOS sandbox and cash, rework, handover, provisioning, retail, claim) then **production smoke tests**
   (read-only checks + one supervised sale), with §5 approvals recorded.
