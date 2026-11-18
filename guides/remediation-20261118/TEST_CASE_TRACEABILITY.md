# Test case traceability (2026-11-18)

Status = outcome recorded in `TestResults/qa-2026-11-18/pass2/pass2-final.trx` (final run, 1 877/1 877 PASS; pass-1 rows were re-executed there) (git-ignored; .NET 8, all infrastructure-gated tests executed).
"Existing" tests predate this change and passed in the same run. Counting: one row = one xUnit execution (a `[Theory]` row counts once); test classes,
files and assertions are not counted as cases. The reference workbook `SP26SE002_GSP72_HCM_Report5_Unit_Test_REF.xlsx` was **not found** in the
workspace; no LOC per function was measured, so no test-case/KLOC figure is claimed.

## Requirement → scenario → test (new in this change: 76 executions, all PASS)

| Group | Function under test (source) | Case | Type | Test method | Status |
|---|---|---|---|---|---|
| PKG-IMG | `AdminAfterSalesController.ToInputAsync`, `ServicePackageHandlers.Create/UpdatePackageAsync` | PKG-IMG-001/002 attach + visible in admin and customer lists | Normal | `PackageImageAcceptanceTests.PKG_IMG_001_002_…` | PASS |
| PKG-IMG | same | PKG-IMG-003/004 replace with another upload, then remove; stale revision 409 | Normal / Abnormal | `PKG_IMG_003_004_…` | PASS |
| PKG-IMG | `FileUploadUrlService`, `FilePolicies` | PKG-IMG-005 PDF type, 10 MB + 1 size, unknown id, wrong purpose | Abnormal / Boundary | `PKG_IMG_005_…` | PASS |
| PKG-IMG | API role policy | PKG-IMG-006 Manager/Sales/Technician/Customer 403, anonymous 401 | Abnormal (auth) | `PKG_IMG_006_…` | PASS |
| PKG-IMG | `AfterSalesViews.Package` | PKG-IMG-007 package without image readable | Boundary (legacy) | `PKG_IMG_007_…` | PASS |
| PKG-IMG | `ServicePackageHandlers.UpdatePackageAsync` | PKG-IMG-008 subscription snapshot unchanged after replace/remove | Normal | `PackageImageHistoryTests.PKG_IMG_008_…` | PASS |
| INSTALL-CONTRACT | `ContractHandlers.SubmitPaperSignedDocumentAsync` | Sales owner uploads scan → UNDER_REVIEW; only Manager review → SIGNED | Normal + transition | `PaperContractUploadTests.INSTALL_CONTRACT_001_…` | PASS |
| INSTALL-CONTRACT | same | wrong official hash, future date, note > 300, wrong purpose, other uploader's file | Abnormal / Boundary | `…_002_…` | PASS |
| INSTALL-CONTRACT | same | Sales of another request 404; DRAFT cannot receive a scan | Abnormal (auth / invalid transition) | `…_003_…` | PASS |
| INSTALL-CONTRACT | same | no replacement while UNDER_REVIEW; rejected scan kept as history; duplicate file refused | Abnormal (invalid transition) + history | `…_004_…` | PASS |
| INSTALL-CONTRACT | same | Manager upload; customer path unchanged | Normal | `…_005_…` | PASS |
| INSTALL-CONTRACT | `SalesContractsController.SubmitPaperSigned`, `FilePolicies.UploaderRoles` | role matrix (401/403/404), Sales may presign SIGNED_CONTRACT, Technician 403 | Abnormal (auth) | `PaperContractEndpointsTests` (2) | PASS |
| INSTALL-SCHEDULE | `InstallationScheduleHandlers.AvailabilityAsync / PublishOffersAsync / SelectOfferAsync` | availability → offers → customer selects → PLANNED → cash → CONFIRMED; idempotent double click | Normal + transition | `InstallationSlotOfferTests.INSTALL_SCHEDULE_OFFER_001_…` | PASS |
| INSTALL-SCHEDULE | same | 0/6/duplicate slots, note > 500, past, reversed, range > 31, durationDays 0, contract not signed | Abnormal / Boundary / invalid transition | `…_002_…` | PASS |
| INSTALL-SCHEDULE | same | other technician / customer 404 | Abnormal (auth) | `…_003_…` | PASS |
| INSTALL-SCHEDULE | same | replaced offer NOT_OPEN; history kept | Abnormal (stale) | `…_004_…` | PASS |
| INSTALL-SCHEDULE | same | leave after offer → unavailable (generic customer message); start passed → refused | Abnormal (capacity / past) | `…_005_…` | PASS |
| INSTALL-SCHEDULE | same | two customers, one technician interval: second refused | Abnormal (conflict) | `…_006_…` | PASS |
| INSTALL-SCHEDULE | same | selection on a PAID contract replaces the plan with history and confirms at once | Normal (reschedule) | `…_007_…` | PASS |
| INSTALL-BUNDLE / INSTALL-CASH | `PaymentService.CreateCashCheckoutAsync / ConfirmCashAsync`, `AfterSalesPaymentSettlement` | one cash payment, lines, contract-only amount refused, exact total settles all, second confirm refused | Normal + Abnormal | `BundledCashCheckoutTests.INSTALL_BUNDLE_CASH_001_…` | PASS |
| INSTALL-BUNDLE | `ServicePackageHandlers.PurchaseAsync` | cash without contract, unknown method, no package, inactive package, other customer | Abnormal | `…_002_…` | PASS |
| INSTALL-BUNDLE | same | contract-only cash choice blocks; online link blocks cash; nothing created twice | Abnormal (duplicate) | `…_003_…` | PASS |
| INSTALL-BUNDLE | settlement + sweep | pending cash not expired after 2 days; withdrawn → CHECKOUT_EXPIRED; rebuy settles | Boundary / transition | `…_004_…` | PASS |
| PAGINATION | `PageRequest.Parse` | 10 rows: absent, defaults, limits 1/100/10 000, 0, −1, 10 001, 101 | Boundary / Abnormal | `OptInPagingTests.PAGINATION_Page_parameters_…` (×10) | PASS |
| PAGINATION | `ServiceCustomerHandlers.PageNotificationsAsync` | first/middle/last/beyond, ties stable, total, no other user's rows, empty | Normal / Boundary / Auth | `PAGINATION_Notifications_pages_…` | PASS |
| PAGINATION | same | not capped at 100 | Boundary | `PAGINATION_Paged_notifications_are_not_capped_…` | PASS |
| PAGINATION | `ServiceManagerHandlers.PageAsync`, `ServiceCustomerHandlers.PageAsync` | status filter in SQL, totals, customer isolation | Normal / Auth | `PAGINATION_Request_pages_…` | PASS |
| PAGINATION | 5 controllers | legacy array, paged shape, 400 on 6 invalid queries | Normal / Abnormal | `OptInPagingEndpointsTests.PAGINATION_API_Legacy_…` (×5) | PASS |
| PAGINATION | 5 controllers | paging does not open the list to other roles | Abnormal (auth) | `PAGINATION_API_Paging_does_not_open_…` (×5) | PASS |
| RATE-LIMIT | runtime routing table | every action has a policy; none disabled | Normal (guard) | `EndpointRateLimitCoverageTests.Every_… / No_…` | PASS |
| RATE-LIMIT | policy mapping | 10 sensitive routes on the expected policy (incl. PayOS webhook, payment creation) | Normal | `Abuse_sensitive_actions_use_the_expected_policy` (×10) | PASS |
| RATE-LIMIT | `appsettings.json` vs code | 3 sections | Normal | `Shipped_configuration_matches_the_code_defaults` (×3) | PASS |
| RATE-LIMIT | limiter | under limit 200, over limit 429 + envelope + Retry-After, per-user partition | Normal / Abnormal | `Portal_read_limit_is_per_user_…` | PASS |
| RATE-LIMIT | limiter | exhausted policy does not block another policy | Abnormal | `An_exhausted_portal_budget_…` | PASS |
| RATE-LIMIT | retired routes | still 410 below the limit | Normal | `Retired_routes_keep_answering_410_…` | PASS |
| DASHBOARD | `AfterSalesReportQueries.CollectedAsync` | cash bundle + duplicate online payment + pending cash; reconciled with detail rows | Normal / Abnormal | `DashboardReconciliationTests.DASHBOARD_001_…` | PASS |
| DASHBOARD | same | zero data; date boundaries | Boundary | `DASHBOARD_002_…` | PASS |
| MIGRATION | `20261117171006_InstallationSlotOffers` | checks, FK, partial unique, RLS, Down refused with rows | Abnormal / Boundary | `PostgresInstallationSlotOfferTests.MIGRATION_PostgreSQL_…` | PASS |
| MIGRATION | same | Down/Up round trip on empty table | Normal | `MIGRATION_Down_and_Up_round_trip_…` | PASS |
| E2E | full flow | E2E-01 cash + paper scan by Sales + customer-chosen date + prerequisites gate | E2E | `CommercialRemediationE2ETests.E2E_R01_…` | PASS |
| E2E | full flow | E2E-02 PayOS: browser return does not pay; forged signature rejected; webhook pays | E2E | `E2E_R02_…` | PASS |
| E2E | full flow | E2E-03 bundle: one PayOS charge, item lines settled, duplicate webhook ignored | E2E | `E2E_R03_…` | PASS |
| E2E | concurrency (PostgreSQL) | E2E-04 three customers race for one technician interval: one wins | E2E | `PostgresInstallationSlotOfferTests.E2E_04_…` | PASS |
| E2E | concurrency (PostgreSQL) | one contract, 6 concurrent selections: one live schedule | E2E | `Concurrent_selections_of_one_contract_…` | PASS |
| E2E | full flow | E2E-07 wrong amount → REVIEW_REQUIRED, unknown order ignored, contract not paid | E2E | `E2E_R07_…` | PASS |
| E2E | concurrency (PostgreSQL) | E2E-09 5 concurrent cash bundle purchases + 6 concurrent confirmations | E2E | `PostgresBundledCashTests.E2E_09_…` | PASS |
| E2E | dashboard | E2E-10 = DASHBOARD_001 | E2E | see above | PASS |

## Groups covered by existing tests (not re-implemented)

| Group / scenario | Existing tests (all PASS in the final run) |
|---|---|
| INSTALL-QUOTE (cart, catalog prices, issue, revisions, customer access, duplicates) | `TechnicianCartQuotationTests`, `QuotationIntegrityTests`, `SimplifiedCommercialFlowEndpointsTests` |
| INSTALL-CASH contract-only (authorized confirm, amount, duplicate receipt, concurrency) | `SimplifiedContractAndCashPaymentTests`, `PostgresSimplifiedCommercialFlowTests` |
| INSTALL-PAYOS (signature, dedup, reconciliation, expiry) | `PaymentTests`, `PostgresInstallationWorkflowConcurrencyTests`, `PostgresPaymentIntegrityTests`, `InstallationLifecycleE2ETests.E2E_06/07` |
| AFTER-COVERAGE / AFTER-QUOTE / AFTER-PAYMENT (PayOS) / AFTER-COMPLETE | `FaultRequestTests`, `AfterSalesOperationsTests`, `ServicePackageTests`, `AfterSalesEndpointsTests`, Postgres after-sales tests |
| E2E-05 covered warranty, E2E-06 paid after-sales (PayOS) | after-sales unit suites above (handler level, multi-step) |
| E2E-08 cross-role authorization | role matrices in `SimplifiedCommercialFlowEndpointsTests`, `CustomerToInstallationEndpointsTests`, `AfterSalesEndpointsTests`, plus the new API tests |

## Second pass (backend, 46 new executions + 1 renamed, all PASS in `pass2-final.trx`)

| Group | Function under test (source) | Case | Type | Test method | Status |
|---|---|---|---|---|---|
| AFTER-QUOTE | `ServiceQuotationHandlers.IssueByTechnicianAsync` (`AfterSales/ServiceQuotationTechnicianPricing.cs`) | catalog pricing parts + labor, snapshot, issuer, Sales task closed, customer accepts, PayOS → READY_FOR_WORK | Normal | `TechnicianPricedQuotationTests.AFTER_QUOTE_001_…` | PASS |
| AFTER-QUOTE | same | price 0, inactive, unknown product, quantity 0, duplicate product, empty, foreign item — nothing written | Abnormal / Boundary | `AFTER_QUOTE_002_…` | PASS |
| AFTER-QUOTE | same | Sales / unknown user / unknown request → 404; Sales cannot supersede a technician version | Abnormal (auth) | `AFTER_QUOTE_003_…` | PASS |
| AFTER-QUOTE | same + disaster support | partial coverage (free item not priced), disaster 70 % support reserved | Normal / Abnormal | `AFTER_QUOTE_004_…` | PASS |
| AFTER-QUOTE | same | new version supersedes, catalog price change keeps old snapshot | Normal (versioning) | `AFTER_QUOTE_005_…` | PASS |
| AFTER-QUOTE | `AcceptAsync`, `RejectAsync` | expired cannot be accepted; rejected declines items; accept after reject refused | Abnormal (invalid transition) | `AFTER_QUOTE_006_…` | PASS |
| AFTER-QUOTE | coverage | fully covered: READY_FOR_WORK, quotation refused, no payment | Boundary | `AFTER_QUOTE_007_…` | PASS |
| AFTER-PAYMENT | `ServiceQuotationHandlers.SelectCashAsync`, `PaymentService.ConfirmCashAttemptAsync` | pending ≠ paid; exact confirmation → PAID, READY_FOR_WORK, audit events | Normal | `AfterSalesCashPaymentTests.AFTER_CASH_001_…` | PASS |
| AFTER-PAYMENT | `ConfirmCashAttemptAsync` | wrong amount, missing fields, foreign evidence, duplicate confirm, unknown attempt | Abnormal | `AFTER_CASH_002_…` | PASS |
| AFTER-PAYMENT | `CashSettlementProblemAsync`, `AfterSalesSweepService.ExpireQuotationsAsync` | late confirm refused, sweep withdraws cash and expires quotation | Abnormal / Boundary | `AFTER_CASH_003_…` | PASS |
| AFTER-PAYMENT | `CheckoutAsync`, `SelectCashAsync`, `CancelCashAsync` | PayOS first blocks / pays; cash first blocks online until withdrawn; PayOS regression | Abnormal (duplicate) | `AFTER_CASH_004_…` | PASS |
| AFTER-PAYMENT | `SelectCashAsync`, `RejectAsync`, `CancelAttemptAsync` | not accepted, other customer, reject withdraws cash | Abnormal (auth / transition) | `AFTER_CASH_005_…` | PASS |
| AFTER-PAYMENT | `ServicePackageHandlers.PurchaseAsync` | packages alone in cash on a PAID contract; second order blocked | Normal / Abnormal | `AFTER_CASH_006_…` | PASS |
| API | 5 new routes | role matrix (5 roles + anonymous), validation, 404 without disclosure, overview Admin-only | Abnormal (auth) | `AfterSalesRemediationEndpointsTests` (2) | PASS |
| PostgreSQL | technician issue | 4 concurrent issues → one open version, jsonb snapshot, task CANCELLED | Concurrency / persistence | `PostgresAfterSalesRemediationTests.Concurrent_technician_issues_…` | PASS |
| PostgreSQL | cash confirm | 6 concurrent confirmations → one PAID, one SETTLED line, no refund | Concurrency | `…Concurrent_cash_confirmations_…` | PASS (8 isolated reruns + final run) |
| PostgreSQL | cash vs online | 6 concurrent cash / checkout calls → one live payment | Concurrency | `…Concurrent_cash_choices_and_online_checkouts_…` | PASS |
| MIGRATION | `20261117185657_AfterSalesTechnicianQuotation` | check violation, default SALES, Down refused with technician rows | Abnormal / Boundary | `…MIGRATION_The_issuer_check_…` | PASS |
| MIGRATION | same | Down/Up round trip | Normal | `…MIGRATION_Down_and_Up_round_trip_without_technician_quotations` | PASS |
| DASHBOARD | `AdminOverviewQueries` on PostgreSQL | translation + confirmed after-sales cash counted | Normal | `…DASHBOARD_The_overview_translates_on_PostgreSQL_…` | PASS |
| DASHBOARD | `AdminOverview` / `AdminOverviewQueries` | known dataset: contracts, PDFs/scans, collected by method, pending cash, combined checkout, package channel, offers, refunds, attempts | Normal / Abnormal | `AdminOverviewTests.DASHBOARD_OVERVIEW_001_…` | PASS |
| DASHBOARD | same | technician quotation by issuer, offers ≠ collected | Normal | `DASHBOARD_OVERVIEW_002_…` | PASS |
| DASHBOARD | same | empty database, reversed / > 366-day period | Boundary / Abnormal | `DASHBOARD_OVERVIEW_003_…` | PASS |
| PAGINATION | 7 new opt-in routes | legacy array, paged shape, 6 invalid queries → 400; other roles 403 | Normal / Abnormal | `OptInPagingEndpointsTests` (+14 rows) | PASS |
| PAGINATION | `SalesTaskHandlers.Page*` | queue order, totals, owner isolation, status filter | Normal / Auth | `OptInPagingTests.PAGINATION_Sales_task_pages_…` | PASS |
| PAGINATION | `PreSurveyUnitOfWork.Page*` | first/last page, total = legacy list, claim moves a row, owner isolation | Boundary / Auth | `PAGINATION_Sales_survey_request_queues_…` | PASS |
| E2E | full after-sales | technician-priced + cash → repair → customer acceptance → COMPLETED | E2E | `AfterSalesRemediationE2ETests.E2E_AS_01_…` | PASS |
| E2E | same | + PayOS, duplicate webhook | E2E | `E2E_AS_02_…` | PASS |
| E2E | same | fully covered, no quotation / payment | E2E | `E2E_AS_03_…` | PASS |
| E2E | same | partially covered, only chargeable item paid | E2E | `E2E_AS_04_…` | PASS |
| E2E | same | expired quotation never leads to work | E2E | `E2E_AS_05_…` | PASS |
| E2E | installation | installation + 2 packages in one cash payment → schedule confirmed → prerequisites gate | E2E | `CommercialRemediationE2ETests.E2E_R04_…` | PASS |

LOC per function was not measured; no test-case/KLOC figure is claimed. Report 5 workbook not present in the workspace.

## Not covered

| Scenario | Reason |
|---|---|
| Frontend screens / FE E2E | out of scope (backend only) |
| NFR performance (p95 latency, load) | measured in pass 3, see NFR_VERIFICATION_REPORT.md |
| ~~Technician pricing of one-time maintenance~~ | superseded in pass 3: implemented (inspection first), see below |

## Final acceptance pass (pass 3): one-time maintenance MAINT-001..015

Classes: `Unit/AfterSales/OneTimeMaintenanceTests` (U), `EndToEnd/AfterSalesRemediationE2ETests` (E2E). Test catalog data only
(SVC-CLEAN 50 000 / panel, MC4-PAIR 40 000 / pair, SVC-HOUR 200 000 / hour → 880 000). These are **test values, not company prices.**

| ID | Scenario | Type | Test | Result |
|---|---|---|---|---|
| MAINT-001 | valid request → `SUBMITTED`, no Sales task, history records the mode; too-soon date and foreign project refused; `inspectionRequired: false` keeps the Sales task | U | `MAINT_001_A_valid_request_waits_for_an_inspection_without_any_Sales_task` | PASS |
| MAINT-002 | Manager assigns a technician, INSPECTION visit before any payment | U | `MAINT_002_A_technician_is_assigned_for_the_inspection_before_any_payment` | PASS |
| MAINT-003 | inspecting technician issues a catalog-priced quotation (exact total, `TECHNICIAN` issuer, snapshot lines) | U | `MAINT_003_…_issues_a_catalog_priced_quotation` | PASS |
| MAINT-004 | another technician → `REQUEST_NOT_FOUND`; the Sales issue path → `FORBIDDEN`; nothing written | U / Auth | `MAINT_004_Another_technician_or_Sales_cannot_issue_the_quotation` | PASS |
| MAINT-005 | unpriced catalog product → `SERVICE_QUOTATION_PRICE_MISSING`, nothing written | U | `MAINT_005_A_missing_catalog_price_prevents_the_quotation` | PASS |
| MAINT-006 / 007 | customer accepts / rejects (rejection never leads to work) | U | `MAINT_006_007_The_customer_accepts_or_rejects_the_quotation` | PASS |
| MAINT-008 / 010 | cash confirmed once; second cash / PayOS refused (no double payment) | U | `MAINT_008_010_Cash_pays_once_and_every_second_payment_is_refused` | PASS |
| MAINT-009 | PayOS payment; duplicate webhook → `DUPLICATE`, no change | U | `MAINT_009_PayOS_pays_the_quotation_and_a_duplicate_webhook_changes_nothing` | PASS |
| MAINT-011 | package (prepaid) maintenance visit: no quotation, no payment (regression) | U | `MAINT_011_A_prepaid_package_maintenance_visit_needs_no_quotation_and_no_payment` | PASS |
| MAINT-012 | extra work found on site → supplementary quotation for that item only | U | `MAINT_012_Additional_work_found_on_site_is_charged_alone_on_a_supplementary_quotation` | PASS |
| MAINT-013 | expired quotation cannot be paid; sweep reopens pricing; technician prices again | U | `MAINT_013_An_expired_quotation_cannot_be_paid_and_the_technician_can_price_it_again` | PASS |
| MAINT-014 | invalid transitions refused: quotation before inspection, completion before work, maintenance visit before payment, checkout / cash before acceptance (package-request refusal: MAINT-011) | U | `MAINT_014_Invalid_transitions_are_refused` | PASS |
| MAINT-015 | full journey: request → inspection → quotation → cash → maintenance visit → acceptance → `COMPLETED` / `MAINTENANCE_DONE` | E2E | `E2E_MAINT_015_Technician_priced_one_time_maintenance_runs_from_request_to_closure` | PASS |
| legacy | `inspectionRequired: false` keeps the Sales-priced path | U | `FaultRequestTests` (one-time maintenance test, now passes `inspectionRequired: false`) | PASS |

### Other pass-3 tests

| Requirement | Test | Type | Result |
|---|---|---|---|
| After-sales pricing configuration (Admin creates labor / part products; validation; technician search / filter / page; technician 403 on writes; inactive hidden) | `Integration/AfterSales/AfterSalesPricingConfigurationTests.Admin_configures_labor_and_parts_…` | Integration (API) | PASS |
| Unapplied-money ledger = dashboard `collected_unapplied` | `DashboardReconciliationTests.DASHBOARD_001_…` (ledger assertions added) | U | PASS |
| Ledger roles and status validation | `AfterSalesRemediationEndpointsTests.New_routes_admit_only_their_roles`, `Requests_are_validated_and_unknown_resources_are_404` | Integration | PASS |
| Schema readiness under concurrent cold start (defect found by the NFR burst) | `SchemaReadinessTests.Concurrent_callers_during_the_first_check_get_its_answer_and_the_probe_runs_once` | Integration | PASS |
| NFR measurements | `Integration/Nfr/NfrMeasurementTests` (4 tests): see NFR_VERIFICATION_REPORT.md | NFR | PASS |

Report 5: the workbook is not in the workspace; this table is its function-level input. LOC per function was **not measured**, so no test-case/KLOC
figure is claimed.
