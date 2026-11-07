# SmartSolar — full business remediation report (2026-10-30, updated 2026-10-31) — authoritative state on top, history below

## Authoritative current state (2026-10-31, final)

* **Per decision:** see the table in [approved-business-implementation-status.md](approved-business-implementation-status.md).
* **Release:** [production-readiness-report.md](production-readiness-report.md) (BLOCKED for production; code ready for review).
* **Migrations:** seven pending (two earlier, M-1..M-4 and M-7, all authorised). Applied and verified only on disposable
  PostgreSQL databases. M-5 and M-6 are not generated.
* **Tests (after M-7):** full suite with disposable PostgreSQL **1,539 total / 1,537 passed / 0 failed / 2 skipped** (Redis tests; Redis suite 9/9 separately); without infrastructure 1,539 / 1,463 / 0 / 76; the combined PostgreSQL + Redis run is NOT VERIFIED in this environment.

**M-7 (2026-10-31):**
* DRAFT contract discard and the explicit closure afterwards.
* A preventive constraint fix: never-issued DISCARDED rows are exempt from the issue and document checks.
* Details: [m7-contract-draft-discard-implementation.md](m7-contract-draft-discard-implementation.md).

**Final phase fixes:**
* **P0 closure vs contract race:** FIXED_AND_VERIFIED_POSTGRESQL.
* **DRAFT-cancellation constraint (M-1 correction):** FIXED_AND_VERIFIED_POSTGRESQL.
* **B-01:** FIXED_AND_VERIFIED_SQLITE_ONLY (a pure flow, no concurrency).
* **B-04 verified activation:** FIXED_AND_VERIFIED_SQLITE_ONLY.
* **Three PostgreSQL test-suite issues** (scenario gap, stale migration-guard setup, the defect above): resolved.

SELF-01 and every earlier "PostgreSQL NOT EXECUTED" note in the history below were executed in the final phase and pass.
See [final-postgresql-concurrency-verification.md](final-postgresql-concurrency-verification.md).

---

**Historical record below.** Earlier sections describe each phase as it was written; where they differ from the section above, the section above applies.

Audience: backend reviewers and the release owner.

The first part (2026-10-30) is the audit remediation. **§ Approved directions (2026-10-31)** at the end covers the
implementation of the Product Owner's approved decisions D-01..D-10. Rows changed by that phase say so.

## Baseline and constraints

* **Baseline:**
  * Branch `feat/fe-backend-gap-fixes`, HEAD `2810286`, 81 modified + 24 untracked entries before this phase.
  * Docker unavailable.
  * Tests: 1,456 total, 1,393 passed, 0 failed, 63 skipped.
* **Constraints honoured:** no commit/push/reset/stash/deploy, no shared-database access, and **no new EF migration**. The
  standing rule is that migration generation needs explicit approval. Every fix below is schema-free, and
  `has-pending-model-changes` reports no pending model changes. Schema-dependent decisions are listed as such.
* **Source audit:** [full-business-logic-audit-20261030.md](full-business-logic-audit-20261030.md) was used as the starting backlog
  and each finding was re-verified.

## Status legend

* **FIXED_AND_VERIFIED**: fixed, with a passing automated test that would fail on the old code.
* **FIXED_NOT_VERIFIED**: fixed, but the verifying test could not run here.
* **CONFIRMED_NOT_FIXED**: confirmed and still open.
* **NEEDS_PRODUCT_DECISION**: depends on a policy only the Product Owner can set.
* **NOT_REPRODUCED**: the reported problem did not occur.
* **NOT_VERIFIED**: no runtime evidence either way.

## Issues

| ID | Module | Severity | Evidence | Root cause | Fix | Verification | Status |
|---|---|---|---|---|---|---|---|
| BL-01 | Deployment / schema | P1 | New mapped columns (`administrative_commune.reference_*`, `quotation.origin/pricing_basis`, `onsite_survey_report.bill_of_materials`) are read by ungated queries; `FeatureOptions` claimed early deployment was safe. | Feature flags gate features, not schema presence. | `SchemaReadiness` + `SchemaReadinessMiddleware` (`src/SmartSolar.Api/Middlewares/SchemaReadinessMiddleware.cs`, wired in `Program.cs:93-94,109`): on PostgreSQL, `GetPendingMigrationsAsync` ≠ ∅ → every request answers `503 SCHEMA_NOT_READY` with `pendingMigrations`; re-check throttled to 30 s; ready is final. `FeatureOptions.cs` comment corrected. | `SchemaReadinessTests` (3): throttled state machine, provider without migrations, real pipeline answers 503 with the pending list. **The EF probe against real PostgreSQL was not executed (Docker down).** | FIXED_AND_VERIFIED (pipeline) / NOT_VERIFIED (PG probe) |
| BL-02 | Survey ↔ quotation ↔ contract | P1 | No step compared `quotation.onsite_survey_version` with the report version. | Missing version gate. | `QuotationHandlers.IssueAsync` and `AcceptAsync` (atomic `TryAcceptAsync` conditioned on the report version, `QuotationUnitOfWork.cs`) → `QUOTATION_SURVEY_CHANGED`. `ContractHandlers.CreateAsync` → `CONTRACT_SURVEY_CHANGED` (`ContractUnitOfWork.GetQuotationAsync` now carries both versions). `canAccept` false when stale. Signed/paid records never changed. | `QuotationIntegrityTests.An_amended_survey_blocks_acceptance_and_signals_sales_once_per_version`, `A_contract_cannot_be_created_from_a_quotation_whose_survey_was_amended_after_acceptance`, `An_accepted_quotation_with_a_contract_is_never_revised_or_superseded` | FIXED_AND_VERIFIED (cut-off policy: D-02) |
| BL-03 | Quotation | P2 | An AUTOMATIC draft with PRICE_MISSING could be issued unchanged with labor 0. | `IssueAsync` ignored pricing issues. | `ReviewOfAsync` recomputes outstanding issues from the current lines and costs. Missing price/panel/inverter/labor/invalid total block the issue (`QUOTATION_PRICING_REVIEW_REQUIRED`). Unavailable/wrong-kind/currency/conditions need a `reviewNote`, recorded in the ISSUED event. | `QuotationIntegrityTests.An_automatic_draft_with_a_missing_price_cannot_be_issued_until_the_price_is_entered`, `Conditions_to_review_need_a_review_note_which_is_recorded_on_the_issue` | FIXED_AND_VERIFIED |
| BL-04 | Quotation | P2 | Customer views included `pricingBasis` (internal issues, product ids); `RequestRevisionAsync` returned the staff view with events. | One view for all roles. | `ViewAsync(forCustomer)`: no `pricingBasis`, `pricingReview` or events for customers (list, get, accept, revision request). Staff get a live `pricingReview`. | `QuotationIntegrityTests.Customers_never_receive_pricing_diagnostics_or_the_event_history`; updated `AutomaticQuotationTests`, E2E-10 | FIXED_AND_VERIFIED |
| BL-05 | Survey ↔ quotation | P2 | An amendment produced no signal. | `AmendAsync` never notified quotations. | After a committed amendment, `RecordSurveyAmendedAsync` adds one `SURVEY_AMENDED` event per affected quotation and version (idempotent). `pending-review` lists every quotation with `surveyChanged` (derived from data, so no duplicates under retries). | Same test as BL-02 (repeat call adds nothing; queue lists the quotation once) | FIXED_AND_VERIFIED |
| BL-06 | Quotation | P2 | A revision stamped the current survey version on copied lines. | `CreateRevisionAsync` used `source.OnSiteSurveyVersion`. | The copy keeps `original.OnSiteSurveyVersion`. New `ConfirmSurveyVersionAsync` (+ `POST /api/quotations/{id}/confirm-survey-version`) re-bases a draft with a reason (`SURVEY_VERSION_CONFIRMED`). A stale ACCEPTED quotation without a contract may be revised and superseded. | `QuotationIntegrityTests.A_revision_keeps_the_survey_version_of_its_copied_lines_until_sales_confirms_the_current_one` (incl. 24,500,000 VND contract from the confirmed v2) | FIXED_AND_VERIFIED |
| BL-07 | Configuration | P2 | The guide named `Quotations:`; the section is `Quotation`; tax unvalidated (DB allows 0–20). | Documentation error; no options validation. | `QuotationOptions.Validate()` (tax 0–20, MaxLines 1–1000, MaxValidityDays 1–365, 1 ≤ AutomaticValidityDays ≤ MaxValidityDays, terms ≤ 4000) enforced at startup (`InstallationWorkflowModuleExtensions.cs`). Guide and `.env.example` corrected. | `QuotationOptionsTests` (7), incl. API refusing to start with tax 25 | FIXED_AND_VERIFIED |
| BL-08 | Installation | P2 | `product_id` has a database FK (`InstallationConfigurations.cs:223`, Restrict), so an unknown product failed at the database instead of with a 400. Kind and warranty were unchecked. | No catalog validation. | `RegisterAssetsAsync`: product must exist (not deleted) and match the asset kind (`INSTALLATION_ASSET_PRODUCT_VALIDATION_FAILED`). An omitted `warrantyMonths` is taken from the catalog `WarrantyMonth`. Installed-vs-quoted comparison not implemented (D-05). | `AssetIntegrityTests` | FIXED_AND_VERIFIED (substitution: NEEDS_PRODUCT_DECISION) |
| BL-09 | Scheduling | P2 | **Reproduced:** a customer reschedule of a cross-zone dispatch failed with `NO_ZONE_COVERAGE`. The authorization was copied to a different technician on Sales reassignment. | Reschedule ignored the recorded authorization. | `AppointmentMover.AuthorizedCrossZoneTechnicianAsync` checks that the authorization belongs to the kept technician. `SlotGrid.ResolveAsync` and `ReserveForTechnicianAsync(crossZone: true)` skip only the zone rule. On failure, no silent in-zone hand-off: refused, original unchanged. The authorization is copied only when the technician is unchanged. `AppointmentSnapshot` carries `CrossZoneAuthorizationId`. | `CrossZoneRescheduleTests` (3): customer reschedule keeps technician and authorization with history; leave → `TECHNICIAN_ON_LEAVE`, unchanged; Sales to another technician refused / kept technician succeeds, no new authorization | FIXED_AND_VERIFIED (PG race: NOT_VERIFIED) |
| BL-12 | Disaster protection | P3 | `Release` clamped at 0 silently. Double consumption was **not** reachable (acceptance only from `PENDING_CUSTOMER_ACCEPTANCE`). | Masking clamp. | `AfterSalesWorkflow.Release/Consume` record `DISASTER_SUPPORT_LEDGER_MISMATCH` on the claim when releasing more than is reserved (the balance still never goes negative, as the check constraints require). | `ServicePackageTests.Releasing_more_support_than_is_reserved_is_recorded_instead_of_hidden`; consistent flow records none | FIXED_AND_VERIFIED |
| BL-13 | Quotation | P3 | `catalogUnitPrice` visible to customers. | No requirement either way (until D-09). | **2026-10-31:** customer views return `catalogUnitPrice = null` on every path (list, detail, accept, revision request); staff keep it. | `ApprovedQuotationPolicyTests.Every_customer_quotation_response_…`, `QuotationTests` | FIXED_AND_VERIFIED (D-09) |
| BL-14 | Contracts | P3 | A contract could be issued with null terms. | No check at issue. | `ContractHandlers.IssueAsync` → `400 CONTRACT_TERMS_REQUIRED`. Already issued contracts untouched; no schema change. | `QuotationIntegrityTests.A_contract_without_terms_cannot_be_issued_until_terms_are_entered` | FIXED_AND_VERIFIED |
| BL-15 | Quotation audit | P3 | Same-timestamp events in nondeterministic order. | Ordering by `CreatedAt` only. | Tie-breaker `QuotationEventTypes.Rank` (lifecycle order) in `QuotationUnitOfWork.GetEventsAsync`. Stored data unchanged. Other modules' event lists were not changed. | `QuotationIntegrityTests.Events_written_in_the_same_instant_are_listed_in_lifecycle_order` | FIXED_AND_VERIFIED (quotation only) |
| BL-17 | Installation | P3 | `currentRound` one too high after completion **and while waiting for acceptance**. | `CurrentRound = AcceptanceRound + 1` used for display. | `InstallationProject.DisplayRound` used by all three views. Evidence grouping still uses `CurrentRound`. | E2E-08 asserts 2 after one rework; `InstallationTests` rework test asserts 1 while pending | FIXED_AND_VERIFIED |
| BL-18 | Pre-survey | P3 | `business-rules-redesign.md:42`: simulation is "(optional)"; selection is frozen after submission. | — | None needed. | Documentation | NOT_REPRODUCED (not a defect) |
| D-04 | Installation | P1 (scope) | Registered scope requires EVN/PCCC/structural; items started not required. | Default `IsRequired = false`. | `Installation:PrerequisitesRequiredByDefault = true` for new projects. A waiver (NOT_APPLICABLE or not required) needs a note (`INSTALLATION_CHECKLIST_WAIVER_REASON_REQUIRED`), audited. | `InstallationTests.Start_is_gated_…` (waiver refused without reason, recorded with one), `Projects_are_created_…` (all required); all start paths use `SatisfyPrerequisitesAsync` | FIXED_AND_VERIFIED (legal applicability: decision) |
| D-08 | Monitoring | Scope | No monitoring existed. | Not implemented. | Read-only `EnergyMonitoringHandlers` plus Customer and Manager endpoints: SIMULATED expected production from the selected simulation, `NO_MEASURED_DATA`, alerts `NOT_CONFIGURED`. | E2E-01 (values from the selected simulation, technician 404, staff allowed); `AutomaticQuotationEndpointsTests.Energy_monitoring_is_customer_or_manager_only` | FIXED_AND_VERIFIED (measured data/alerts: decision) |
| NEW-01 | After-sales assets | P3 | A replacement asset recorded `RegisteredByUserId = old.RegisteredByUserId` (the original installer), and the audit event had no actor. | Copy of the old row's value. | `TechnicianServiceHandlers.AddReplacementsAsync` uses the reporting technician for both. | `FaultRequestTests.A_replacement_is_registered_by_the_technician_who_installed_it_not_by_the_original_installer` (different technicians) | FIXED_AND_VERIFIED |
| NEW-02 | Payments | P2 | A second payment of a paid contract (`REFUND_REQUIRED`) or a wrong amount (`REVIEW_REQUIRED`) produced only an attempt status and a log line; no refund request. | Only after-sales lines created refund records. | `IPaymentItemSettlement.RecordUnappliedPaymentAsync`: `PaymentService.ConfirmAsync` opens a `PENDING_APPROVAL` `RefundRecord` (attempt id, amount received) in the confirmation transaction. Repeated webhooks add nothing. | `UnappliedPaymentRefundTests` (2) | FIXED_AND_VERIFIED |
| NEW-03 | Notifications | P3 | Customer and technician notification texts embed UTC ISO timestamps (`AfterSalesSweepService.cs`, `ServiceManagerHandlers.cs`). | Formatting. | **2026-10-31:** `DisplayTime.Format` (`src/SmartSolar.Modules/Common/Time/DisplayTime.cs`) in the configured business zone with the offset, e.g. "10:00 20/10/2026 (GMT+07:00)". | `ApprovedInstallationPolicyTests.Message_times_…` (4 boundary cases), `FaultRequestTests` (visit notification text) | FIXED_AND_VERIFIED (D-10) |
| D-01, D-03, D-05, D-06, D-07 | Several | — | Decided on 2026-10-31; see below. | — | — | — | see § Approved directions |

## Areas re-audited this phase without new findings

* **Payments:** single 100 % payment, signed-contract gate, idempotent webhooks, reconciliation, expiry/retry, and unique
  payable/paid/order-code/provider-reference/payload indexes.
* **Contracts:** created only from accepted quotations, once.
* **Projects:** created only for PAID contracts, unique per contract.
* **Maintenance:** booking and entitlement locking with a "one live booking per visit" index, consumption only on acceptance,
  cancellation returns the visit.
* **Sweeps:** status-guarded, re-read in their transaction, idempotent notices and follow-ups.
* **Refunds:** Manager decision bounded by the original amount; Admin record.
* **RBAC:** after-sales controllers separate Customer, Technician, Sales, Manager/Admin and Admin.
* **Identity:** login status gate; reset revokes refresh tokens; auth endpoints rate-limited.
* **File URLs:** default TTL 300 s, clamped to 30–3,600 s.

## Approved directions (2026-10-31)

* **Baseline re-verified:** 143 modified/untracked entries; tests 1,484 / 1,421 passed / 0 failed / 63 skipped.
* **Schema:** schema-free again (no migration generated). Proposed migrations M-1..M-6 are in
  [approved-business-implementation-status.md](approved-business-implementation-status.md) §3.
* Per-decision status: same document, §1.

| ID | Module | Change | Main files | Verification | Status |
|---|---|---|---|---|---|
| D-01 | Quotation | **Manager approval workflow** for discounts, prices below the catalog reference and zero prices:<br>• events `PRICING_APPROVAL_REQUESTED`/`PRICING_APPROVED`/`PRICING_REJECTED` with `fp:{fingerprint}:{sequence}; reason`;<br>• `IssueAsync` gate `QUOTATION_PRICING_APPROVAL_REQUIRED`;<br>• missing catalog price refused (`QUOTATION_PRICE_MISSING`);<br>• Manager-only controller; self-approval refused;<br>• `pricingApproval` on staff views. | `Quotations/QuotationPricingApproval.cs` (new), `QuotationHandlers.cs`, `QuotationEntities.cs`, `QuotationUnitOfWork.cs`, `QuotationControllers.cs` | `ApprovedQuotationPolicyTests` (approval, rejection + re-request, stale after edit, note edit keeps it, reduced/zero/missing price, concurrent edit vs decision, new version), E2E-04, `ApprovedPolicyEndpointsTests`. Mutation check: removing the gate fails 6 tests. | FULLY_IMPLEMENTED_AND_VERIFIED (PostgreSQL lock NOT EXECUTED) |
| D-02 / R-01 | Survey ↔ quotation | **BOM consistency:**<br>• `confirm-survey-version` and issue refuse `QUOTATION_BOM_MISMATCH` (every BOM material quoted at its survey quantity; no extra panel/inverter);<br>• survey amendment refused with `ONSITE_SURVEY_COMMERCIALLY_LOCKED` once a non-superseded contract exists. | `QuotationPricingApproval.cs` (`BomMismatchesAsync`), `OnSiteSurveyHandlers.cs`, `OnSiteSurveyUnitOfWork.cs` | 2 → 20 panel test (relabel refused, extra inverter refused, tamper after confirm refused at issue, history kept); `QuotationIntegrityTests` (amendment lock). Mutation check: removing the confirm gate fails the test. | PARTIALLY_IMPLEMENTED (post-contract change process undefined) |
| D-03 | Quotation | Manual quotation for a NOT_FEASIBLE survey refused (`QUOTATION_SURVEY_NOT_FEASIBLE`); expiry refusal tested. | `QuotationHandlers.cs` | `ApprovedQuotationPolicyTests` | SCHEMA_APPROVAL_REQUIRED (reject/cancel/close: M-1) |
| D-04 / R-05 | Installation | **Mandatory prerequisites:**<br>• EVN/PCCC/structural cannot be made optional (`INSTALLATION_PREREQUISITE_MANDATORY`);<br>• NOT_APPLICABLE needs an approved rule (`Installation:ExemptiblePrerequisiteCodes`, empty) → `INSTALLATION_PREREQUISITE_EXEMPTION_NOT_AUTHORIZED`, plus a reason and a verified document (`INSTALLATION_EXEMPTION_EVIDENCE_REQUIRED`);<br>• the start gate ignores legacy flags and note-only waivers;<br>• the checklist is frozen after start. | `InstallationHandlers.cs`, `InstallationEntities.cs` | `InstallationTests.Start_is_gated_…` (rewritten), `ApprovedInstallationPolicyTests` (legacy, frozen) | FULLY_IMPLEMENTED_AND_VERIFIED |
| D-05 | Installation | **Equipment check:**<br>• PANEL/INVERTER need a catalog product;<br>• comparison with the accepted quotation from installation-time assets;<br>• `INSTALLATION_SUBSTITUTION_APPROVAL_REQUIRED` at report, `INSTALLATION_SUBSTITUTION_CUSTOMER_APPROVAL_REQUIRED` at handover;<br>• Manager-only and customer approval endpoints, events bound to a fingerprint;<br>• `equipment` on staff, technician and customer views (no staff id for the customer);<br>• legacy `NOT_CHECKED_AT_HANDOVER`. | `Installations/InstallationEquipmentHandlers.cs` (new), `InstallationHandlers.cs`, `InstallationViews.cs`, `InstallationUnitOfWork.cs`, `InstallationControllers.cs` | `ApprovedInstallationPolicyTests` (substitution flow, matching flow, legacy in-flight/completed), `InstallationTests` (13-for-12 panels), `FaultRequestTests` (stable after replacement), endpoints | PARTIALLY_IMPLEMENTED (structured record M-3, equivalence rule) |
| D-06 | Assets / coverage | **Warranty sources:**<br>• asset warranty only from the catalog (a differing value refused; none without a product);<br>• `COVERAGE_DECIDED` records the category and the item's equipment-warranty facts. | `InstallationHandlers.RegisterAssetsAsync`, `ServiceManagerHandlers.DecideCoverageAsync` | `AssetIntegrityTests`, `FaultRequestTests` | PARTIALLY_IMPLEMENTED (M-4; start rule and commercial handling undefined) |
| D-07 | Knowledge base | **Audience boundary** (`KnowledgeAudience`, Customer default):<br>• customer retrieval limited to documents tagged `audience:customer`;<br>• post-generation re-check and stored citations filtered by audience;<br>• staff roles see all published documents. | `KnowledgeBase/ChatService.cs`, `KnowledgeContracts.cs`, `KnowledgeUnitOfWork.cs`, `KnowledgeControllers.cs` | `KnowledgeBaseEndpointsTests` (allowed-version filter, staff vs customer, malicious RAG citation, history after reclassification) | PARTIALLY_IMPLEMENTED (audited column M-2; real RAG NOT EXECUTED) |
| D-08 | Monitoring | No change; simulated view kept. | — | Existing tests | EXTERNAL_DEPENDENCY_REQUIRED |
| D-09 | Quotation | See BL-13. | `QuotationHandlers.ViewAsync` | — | FULLY_IMPLEMENTED_AND_VERIFIED |
| D-10 | Notifications | See NEW-03. | `Common/Time/DisplayTime.cs`, `ServiceManagerHandlers.cs`, `AfterSalesSweepService.cs` | — | FULLY_IMPLEMENTED_AND_VERIFIED |
| R-02 | Deployment | `/health/live` (always 200) and `/health/ready` (503 `SCHEMA_NOT_READY` until migrated) served by the schema guard. The PayOS webhook stays 503 while not ready (retried by PayOS; reconciliation). | `Middlewares/SchemaReadinessMiddleware.cs` | `SchemaReadinessTests` (new case) | FIXED_AND_VERIFIED (real PostgreSQL probe NOT EXECUTED) |
| R-03 | Payments | Reviewed: no duplicate refund, verified amount only, Manager approval kept, evidence kept. | — | `UnappliedPaymentRefundTests` | NOT_REPRODUCED (no defect) |
| R-04 | Scheduling | Reviewed: success and refusal both preserve history and authorization. | — | `CrossZoneRescheduleTests` | NOT_REPRODUCED (PostgreSQL race NOT EXECUTED) |
| R-06 | Legacy data | Legacy rules documented in the status document §2. One fix: completed or in-flight projects are never stuck or mislabelled by D-05. | `InstallationEquipmentHandlers.cs` | `ApprovedInstallationPolicyTests` legacy case | FIXED_AND_VERIFIED |
| SELF-01 | Tests (PostgreSQL only) | `PostgresPendingSchemaConstraintTests` accepted a quotation after amending its survey. That has been refused since BL-02, so the test was latently broken and never executed. It is reordered (revise → confirm → issue → accept), its knowledge document is classified customer-visible and its extra panel approved. `PostgresKnowledgeBaseConcurrencyTests` now states the staff audience. | tests | **NOT EXECUTED** (Docker) | FIXED_NOT_VERIFIED |

**Behaviour change to note:** an AUTOMATIC draft whose BOM material could not be priced (unavailable, wrong kind,
non-VND) can no longer be issued with a review note. The unpriced material is missing from the lines, so it is a BOM
mismatch. The technician must amend the survey to an available product. CONDITIONS_REQUIRE_REVIEW still accepts a note.

## M-1 to M-4 (2026-10-31, migrations authorised)

| ID | Change | Verification | Status |
|---|---|---|---|
| M-1 / D-03 | **Migration `20261030201506_SalesTerminalStates`.** Customer reject (ISSUED → REJECTED, atomic vs accept, idempotent); customer withdraw (request → Cancelled); Sales close (→ Closed with NOT_FEASIBLE / CUSTOMER_DECLINED / NO_RESPONSE / OTHER). Open quotations CANCELLED with the request. Refused with a contract (`SURVEY_REQUEST_CONTRACT_EXISTS`) or an open appointment (`SURVEY_REQUEST_APPOINTMENT_OPEN`). Terminal: no new quotation, revision, generation or amendment. Removed from the unclaimed and review queues. Revision after a rejection gives a new version; the rejected one keeps its outcome. | `SalesTerminalStateTests` (7 incl. DB constraints), endpoint RBAC. Mutation: removing the contract guard fails the test. | FIXED_AND_VERIFIED (SQLite); PostgreSQL race NOT_VERIFIED; reopening NEEDS_PRODUCT_DECISION |
| M-2 / D-07 | **Migration `20261030201618_KnowledgeDocumentAudience`.** Persisted audience (default INTERNAL) with an audited Admin change; the tag no longer grants anything. `chat_message.retrieval_audience`. Customers never read answer text not retrieved for them: legacy answers, answers citing documents reclassified INTERNAL, idempotent replays, and model history. | `KnowledgeAudienceTests` (2), `KnowledgeBaseEndpointsTests`. Mutation: removing the withholding rule fails the test. | FIXED_AND_VERIFIED (SQLite/HTTP); real RAG NOT_VERIFIED |
| M-3 / D-05 | **Migration `20261030201748_InstallationEquipmentSubstitution`.** Approvals stored as records with per-product items. Superseded on equipment change, declined on a customer rework request, one active per project (partial unique), Manager cannot act for the customer, history in the staff view. | `ApprovedInstallationPolicyTests` (records, decline, supersede, duplicate, DB unique) | FIXED_AND_VERIFIED (SQLite); PostgreSQL concurrency NOT_VERIFIED |
| M-4 / D-06 | **Migration `20261030201824_ManufacturerWarrantyCoverage`.** MANUFACTURER_WARRANTY category (refused by the handler, and by the database without an end date); asset `warranty_source` (CATALOG_PRODUCT; legacy backfilled LEGACY_UNVERIFIED); per-item warranty snapshot at decision. | `ManufacturerWarrantyTests` (2), `FaultRequestTests`. Mutation: removing the handler block fails the test. | FIXED_AND_VERIFIED (safe part); decisions NEEDS_PRODUCT_DECISION |
| SELF-02 | A scripted edit converted `AppDbContext.cs` and `PreSurveyUnitOfWork.cs` from CRLF to LF. | Restored. Each changed file compared with its HEAD line endings; `diff --check` clean. | FIXED_AND_VERIFIED |
