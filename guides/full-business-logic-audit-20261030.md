# SmartSolar backend — full business logic audit (2026-10-30)

Audience: the backend team, QA and product owners deciding on release. This is an **audit only**: no production code was
changed for it.

Evidence labels:
* **[Đã xác định]**: established directly by code, documentation or an executed test.
* **[Trung lập]**: direct evidence exists but is ambiguous.
* **[Chưa xác định]**: not enough direct evidence.

Severity (independent of evidence):
* **P0**: blocker.
* **P1**: high.
* **P2**: medium.
* **P3**: low.

---

## 1. Executive summary

### Scope actually audited
* **Source revision:** local working tree of branch `feat/fe-backend-gap-fixes` on top of commit `2810286`. That is 81
  modified tracked files (+1,642 / −170) and 28 untracked files, including the new automatic quotation workflow and both
  unapplied migrations (`20261021164225_AddCommuneReferencePoints`, `20261030122441_AutomatedInstallationQuotation`).
* **Read in full or near full:**
  * `QuotationAutomaticGeneration.cs`, `QuotationHandlers.cs`, `QuotationPricing.cs`, `QuotationConfigurations.cs`;
  * `OnSiteSurveyHandlers.cs`, from completion and amendment through BOM validation;
  * the new migration;
  * `ContractHandlers.CreateAsync`, `PaymentService` (attempt creation, confirmation, refund/review branches) and the payment indexes;
  * installation project creation, assets, acceptance and checklist;
  * after-sales coverage decisions and disaster-support reservation;
  * reschedule and cross-zone paths in scheduling;
  * login and password reset gates;
  * knowledge base authorization;
  * administrative unit mapping;
  * `FeatureOptions`.
* **Read selectively:** dispatch internals, maintenance packages, service visits, system settings, the RAG pipeline and
  file storage. Those areas are marked partial in the matrix.

### Findings
| Severity | Confirmed [Đã xác định] | Ambiguous [Trung lập] | Unverified [Chưa xác định] |
|---|---|---|---|
| P0 | 0 | 0 | 0 |
| P1 | 2 | 0 | 0 |
| P2 | 6 | 1 | 0 |
| P3 | 5 | 0 | 1 |

### Release blockers
1. **BL-01: migration order.** The working tree maps columns that exist only after the two unapplied migrations. One of
   them, `administrative_commune.reference_*`, is read by **Flow 1 queries that are not behind any feature flag**.
   Deploying this code before both migrations are applied will break commune lookups. With
   `Features__InstallationWorkflowEnabled=true`, every quotation and on-site survey read also breaks.
2. **BL-02: stale survey versions.** A completed on-site survey can be amended at any time, including after quotation
   acceptance, contract signing, payment and installation. No quotation, contract or project step checks the survey version.

### Could not be verified
* The 63 PostgreSQL/Redis integration tests (Docker daemon unavailable, re-checked during this audit).
* Behaviour of the unapplied migrations on a real PostgreSQL database.
* Real HTTP/PayOS/S3/PVGIS integrations.
* Load/latency NFRs.

The full SQLite suite was executed in this session: **1,456 total, 1,393 passed, 0 failed, 63 skipped** (Release build,
0 warnings). That is evidence for SQLite-level behaviour only.

---

## 2. Business coverage matrix

These labels describe review depth, not quality:
* **Reviewed**: code read for this audit.
* **Partial**: sampled only.
* **Tests exist**: tests were found and ran green in SQLite.
* **PG skipped**: PostgreSQL tests exist but did not run.

None of these means "verified correct".

| Domain | Main flow | Exception flows | Authorization | State transitions | Tests | Findings |
|---|---|---|---|---|---|---|
| A. Identity, RBAC, Customer | Partial: login status gate, reset revokes refresh tokens, rate limits on auth endpoints | Partial | Partial: role attributes per controller; ownership via 404 | Partial: `UserStatus` incl. `PendingVerification` | Tests exist | none new |
| B. Pre-survey & simulation | Partial: selection checks geometry version | Partial | Partial | Partial | Tests exist | BL-18 |
| C. Scheduling & dispatch | Partial: reschedule, overdue recovery, cross-zone | Partial | Reviewed for reschedule/recovery | Partial | Tests exist; PG skipped (25) | BL-09 |
| D. On-site survey & BOM | Reviewed | Reviewed | Reviewed: technician must hold the active appointment | Reviewed | Tests exist | BL-02, BL-05 |
| E. Automatic & manual quotation | Reviewed | Reviewed | Reviewed | Reviewed | Tests exist (11 unit, 10 E2E, 3 HTTP) | BL-02..BL-07, BL-13..BL-15 |
| F. Contracts & payment | Reviewed (creation, payment) | Reviewed (amount mismatch, late payment, expiry) | Reviewed | Reviewed | Tests exist; PG skipped | BL-02, BL-14 |
| G. Installation & handover | Reviewed | Reviewed (rework) | Reviewed | Reviewed | Tests exist; PG skipped (14) | BL-08, BL-17, D-04 |
| H. After-sales & warranty | Partial: coverage decision | Partial | Partial | Partial | Tests exist; PG skipped | BL-08 |
| I. Disaster protection | Partial: reservation formula | Partial | Partial | Partial | Tests exist; PG skipped | BL-12 |
| J. Maintenance & paid services | Not reviewed in depth | — | — | — | Tests exist | none (not reviewed) |
| K. Monitoring & alerts | **Not implemented** (no module, entity or controller) | — | — | — | none | D-08 |
| L. Admin, catalog, settings, dashboards | Partial: CSV export only writes server-defined metric names | Partial | Partial | Partial | Tests exist | BL-01 |
| M. RAG, KB, files | Partial: authorization only | Partial | Reviewed | Not reviewed | Tests exist; PG skipped (6) | D-07 |

---

## 3. Confirmed business bugs

### BL-01: unapplied migrations break Flow 1 and the Installation Flow when the code is deployed first
* **Severity:** P1 (release blocker). **Evidence:** [Đã xác định] for the mappings and query shapes;
  [Chưa xác định] for runtime errors on PostgreSQL (not executed).
* **Module:** administrative units / quotation / on-site survey / deployment.
* **Expected:** `FeatureOptions.cs:3-8` states "deploying this code before the migration is safe".
* **Actual:**
  * `AdministrativeUnitConfigurations.cs:56-58` maps `reference_latitude`, `reference_longitude` and
    `reference_point_source` on `AdministrativeCommune`.
  * `AdministrativeCatalogStore.cs:10` queries the full entity, and `AdministrativeUnitQueries.cs:81` reads those properties.
  * `AdministrativeUnitsController` has no `RequireFeature`.
  * `QuotationConfigurations.cs:55-56` maps `origin` and `pricing_basis`; `OnSiteSurveyConfigurations.cs:51` maps
    `bill_of_materials`.
  * `.env.example:68` sets `Features__InstallationWorkflowEnabled=true`, and `deployment-readiness.md:31` reports flags
    already `true` in DEV.
* **Root cause:** new mapped columns plus a feature flag that tracks configuration, not the schema actually present.
* **Reproduce:** apply migrations only up to the commit `2810286` schema, deploy the working tree, then call
  `GET /api/administrative-units/communes`. A missing-column error is expected (not executed).
* **Impact:** site creation, commune listing and scheduling coverage break. With the flag on, all quotation and survey
  endpoints break.
* **Fix:** make "apply both migrations, then deploy" an explicit gate in the deployment guide. Optionally add a startup
  check of `__EFMigrationsHistory` against the assembly's migrations. Correct the `FeatureOptions` comment.
* **Tests:** a PostgreSQL test that runs the API against a schema missing the newest migration and asserts a clear startup
  failure (or 503) instead of 500s.

### BL-02: survey amendments are unbounded and invisible to quotation, contract and installation
* **Severity:** P1. **Evidence:** [Đã xác định]. **Module:** on-site survey ↔ quotation ↔ contract ↔ installation.
* **Expected:** a quotation, contract and installation are built from the survey version they claim, and a later
  amendment either is blocked or forces review.
* **Actual:**
  * `OnSiteSurveyHandlers.cs:301-311` allows a `COMPLETED` appointment to act indefinitely.
  * `AmendAsync` (`OnSiteSurveyHandlers.cs:236-298`) has no check on quotation, contract or project state. It can change
    the BOM, feasibility (even to `NOT_FEASIBLE`) and geometry, and increments `Version`.
  * `QuotationHandlers.IssueAsync` (153-190) and `AcceptAsync` (282-297), and `ContractHandlers.CreateAsync` (41-63),
    never compare `quotation.OnSiteSurveyVersion` with the report's current version. The only readers of that column are
    the creation paths and the automatic generator (`grep OnSiteSurveyVersion`).
* **Reproduce:** E2E-01 path → after `AcceptAsync`, call `AmendAsync` changing the BOM to 20 panels →
  `ContractHandlers.CreateAsync` still succeeds with the 2-panel total.
* **Impact:** the customer pays for and is installed against a design that no longer matches the recorded survey, and
  the audit trail contradicts itself.
* **Fix (needs a decision, see D-02):** either block amendments once a quotation is ACCEPTED, or refuse
  issue/accept/contract when the quotation's survey version is older than the current one.
* **Tests:**
  * `Amending_the_survey_after_acceptance_is_refused_or_blocks_the_contract`
  * `Accepting_a_quotation_of_an_older_survey_version_is_refused`

### BL-03: an automatic draft with PRICE_MISSING can be issued unchanged, with the missing price at 0
* **Severity:** P2. **Evidence:** [Đã xác định]. **Module:** quotation.
* **Expected:** the handoff guide says "a missing price is never treated as free".
* **Actual:**
  * The generator leaves the unpriced service out of `LaborCost`: `ServiceTotal` sums `LineTotal ?? 0m`
    (`QuotationAutomaticGeneration.cs:224-225`).
  * `IssueAsync` (`QuotationHandlers.cs:153-190`) checks only lines > 0, total > 0 and validity. It ignores
    `PricingBasis.Issues`.
* **Reproduce:** E2E-03 setup → Sales calls `IssueAsync` immediately with the draft's revision. The quotation issues with
  labor 0.
* **Impact:** under-priced offers. The invariant holds only on the automatic path.
* **Fix:** when issuing an AUTOMATIC draft, require that each `PRICE_MISSING` / `LABOR_NOT_SPECIFIED` issue was resolved
  (e.g. labor > 0, or an explicit Sales confirmation recorded in an event).
* **Tests:** `Issuing_an_automatic_draft_with_an_unresolved_missing_price_is_refused`.

### BL-04: customers see the internal pricing basis, which goes stale after Sales edits
* **Severity:** P2. **Evidence:** [Đã xác định]. **Module:** quotation.
* **Actual:**
  * `ViewAsync` (`QuotationHandlers.cs:448-461`) always deserializes `PricingBasisJson`.
  * `GetForCustomerAsync` (274-280) and `ListForCustomerAsync` return that view.
  * The basis is written once at generation (`QuotationAutomaticGeneration.cs:146-147`) and never updated by
    `UpdateHeaderAsync` or the line edits.
* **Impact:** an issued quotation can show the customer `issues: [PRICE_MISSING …]` and service items with
  `lineTotal: null`, while the header shows Sales' labor price. Internal product ids and messages leak.
* **Fix:** use a customer view without `pricingBasis` (or only kWp and simulation id), and refresh or clear `issues` when
  Sales resolves them.
* **Tests:** `Customer_quotation_view_has_no_internal_pricing_issues`.

### BL-05: an amended survey produces no review signal for Sales
* **Severity:** P2. **Evidence:** [Đã xác định]. **Module:** survey ↔ quotation.
* **Actual:**
  * `AmendAsync` never calls the generator.
  * When generation is retried, `SURVEY_CHANGED_REVIEW_REQUIRED` is returned (`QuotationAutomaticGeneration.cs:94`) with
    nothing persisted: no draft, no task, no event.
  * The review queue (`QuotationUnitOfWork.ListAutomaticDraftsAsync`) lists only AUTOMATIC drafts.
* **Impact:** Sales is not told that the quotation in front of the customer no longer matches the survey.
* **Fix:** record a quotation event or a Sales task on amendment of a quoted survey, depending on D-02.
* **Tests:** `Amending_a_quoted_survey_creates_a_sales_review_signal`.

### BL-06: a revision stamps the current survey version but copies the old lines
* **Severity:** P2. **Evidence:** [Đã xác định]. **Module:** quotation.
* **Actual:**
  * `CreateRevisionAsync` (`QuotationHandlers.cs:192-224`) sets `OnSiteSurveyVersion = source.OnSiteSurveyVersion` (the
    current version).
  * It copies the original's lines and service costs, and does not copy `Origin` or `PricingBasisJson`.
* **Impact:** a v2 quotation can claim survey v2 while carrying v1 prices, so the automatic idempotency check (`sameVersion`)
  then treats v2 as "already generated" for the new BOM.
* **Fix:** keep the original's survey version on a copy, or re-price from the new BOM, depending on D-02.
* **Tests:** `Revision_after_survey_amendment_keeps_a_truthful_survey_version`.

### BL-07: handoff guide names the wrong configuration section, and the default tax is unvalidated
* **Severity:** P2. **Evidence:**
  * [Đã xác định] for the documentation mismatch;
  * [Trung lập] for the failure mode, which was not executed.
* **Module:** quotation configuration / documentation.
* **Actual:**
  * `QuotationOptions.SectionName = "Quotation"` (`QuotationHandlers.cs:29`), bound in `InstallationWorkflowModuleExtensions.cs:20`.
  * `guides/automated-quotation-business-handoff.md:80-81` says `Quotations:DefaultTaxRatePercent` and
    `Quotations:AutomaticValidityDays`, so an operator following the guide changes nothing.
  * `DefaultTaxRatePercent` is not range-checked. The header path checks 0–20 (`QuotationHandlers.cs:442`) and the
    database enforces `ck_quotation_tax_rate` 0–20 (`QuotationConfigurations.cs:24`). A configured 25 would make every
    automatic insert fail, which the survey reports as `FAILED`.
  * Neither key is in `.env.example`.
* **Fix:** correct the guide to `Quotation__…`, validate the options at startup, and list them in `.env.example`.
* **Tests:** `Invalid_default_tax_rate_fails_options_validation`.

### BL-08: installed assets are not tied to the catalog, BOM or quotation, and equipment warranty is free text
* **Severity:** P2. **Evidence:** [Đã xác định]. **Module:** installation ↔ after-sales.
* **Actual:**
  * `RegisterAssetsAsync` (`InstallationHandlers.cs:494-527`) stores the technician's `ProductId` unchecked, along with
    the manufacturer, model and `WarrantyMonths` (0–600).
  * Handover computes `WarrantyEndsOn` from those months (`:636-637`).
  * After-sales coverage uses only the contract-level installation warranty (`ServiceCustomerHandlers.cs:95`,
    `ServiceManagerHandlers.cs:221-224`). Per-asset equipment warranty never enters a coverage decision.
* **Impact:** installed equipment can silently differ from what was quoted and paid. The equipment warranty shown to the
  customer is technician-typed.
* **Fix:** validate `ProductId` against the active catalog, and compare panel/inverter counts and types with the accepted
  quotation (warn or block, see D-05). Derive equipment warranty from the catalog spec.
* **Tests:** `Registering_an_asset_with_an_unknown_product_is_refused`,
  `Completion_flags_assets_that_differ_from_the_accepted_quotation`.

### BL-09: rescheduling a cross-zone-dispatched appointment drops back to in-zone auto-assignment
* **Severity:** P2. **Evidence:** [Trung lập]. The code path is clear, but no test reproduces it.
* **Module:** scheduling.
* **Actual:**
  * `MoveOnceAsync` (`CustomerBookingHandlers.cs:357-376`) calls `ReserveForTechnicianAsync(...)` with the default
    `crossZone: false`, which enforces zone membership (`AppointmentAssignmentService.cs:223-233`).
  * For a technician authorized cross-zone this fails with an eligibility code. Because `AssignTechnicianUserId` is null,
    the code then falls back to `reservations.ReserveAsync(Spec(spec, null), …)`, which picks a different in-zone
    technician or fails for a site with no zone coverage.
  * The release path handles cross-zone explicitly by returning to dispatch (`AppointmentAssignmentService.cs:362-363`);
    the reschedule path does not.
  * Separately, `CustomerBookingHandlers.cs:430` copies `CrossZoneAuthorizationId` onto the new appointment even when
    Sales assigned another technician. That technician passes the zone check, so the authorization id is just misattributed.
* **Impact:** the manually authorized technician loses the job on a simple reschedule, without a dispatch event.
* **Fix:** route reschedules of `ManagerDispatch` / cross-zone appointments through dispatch, as release does. Copy the
  authorization only when the technician is unchanged.
* **Tests:** `Rescheduling_a_cross_zone_appointment_keeps_the_authorized_technician_or_returns_to_dispatch`.

### BL-12: disaster-support release clamps negatives silently
* **Severity:** P3. **Evidence:** [Đã xác định]. **Module:** disaster protection.
* **Actual:** `AfterSalesWorkflow.Release` (`AfterSalesWorkflow.cs:90-96`) uses `Math.Max(0, reserved − amount)`. A double
  release or a mismatch is hidden instead of reported. Reservation itself is protected by revision concurrency tokens
  (`AfterSalesConfigurations.cs`), but the PG tests for it were skipped.
* **Fix:** treat a negative result as an invariant violation (log and refuse).
* **Tests:** `Releasing_more_support_than_reserved_is_rejected`.

### BL-13: customers see each line's catalog price
* **Severity:** P3. **Evidence:** [Đã xác định]. Existing behaviour, not new.
* **Actual:** `QuotationLineView` includes `CatalogUnitPrice`, and the customer view uses it. Per-line Sales price
  overrides are therefore visible to the customer. This may be intended; it is a product decision.

### BL-14: automatic quotations and their contracts can carry no terms
* **Severity:** P3. **Evidence:** [Đã xác định].
* **Actual:** `AutomaticTerms` defaults to null (`QuotationHandlers.cs:38`), and `ContractHandlers.CreateAsync` uses
  `Trim(terms) ?? quotation.Terms` (`ContractHandlers.cs:60`). A contract can therefore be created with null terms when
  Sales omits them. Whether issue or publish later requires terms was not verified.

### BL-15: generated and issued events share one timestamp
* **Severity:** P3. **Evidence:** [Đã xác định]. Observed as a flaky ordering in E2E-01 during this session.
* **Actual:** both events use the same `now` (`QuotationAutomaticGeneration.cs:163-165`), and the event list orders by
  `CreatedAt` only (`QuotationUnitOfWork.cs:76-77`). The audit order shown to staff is nondeterministic.
* **Fix:** add a sequence column or a tie-breaker.

### BL-17: currentRound is off by one after completion
* **Severity:** P3. **Evidence:** [Đã xác định]. Observed in E2E-08.
* **Actual:** `CurrentRound => AcceptanceRound + 1` (`InstallationEntities.cs:53`). A project completed after one rework
  reports round 3. The views expose this to the FE.

### BL-18: submission does not require a selected, non-stale simulation
* **Severity:** P3. **Evidence:** [Chưa xác định].
* **Actual:** `SubmitPreSurveyHandler.IsComplete` checks geometry fields only. Whether a stale selection can reach the
  quotation's `pricingBasis.selectedSimulationId` was not traced end to end. It is a reference value only, and price is
  unaffected.

---

## 4. Business rules requiring decisions

| ID | Topic | Current behaviour | Decision needed | Options and trade-offs |
|---|---|---|---|---|
| D-01 | Manager approval | None. Sales may set any discount ≤ subtotal and override any line price, including to 0 (`AddLineAsync`, `UpdateLineAsync`). Automatic quotations have no discount. | Whether approval is required, above what, and by whom | (a) none, current; (b) discount % threshold → Manager approval state before issue; (c) any deviation from catalog price → approval. (b)/(c) need a new quotation state and a queue. No threshold is proposed here. |
| D-02 | Survey amendment after quoting | Unbounded (BL-02) | Until when amendments are allowed, and what they do to quotations | (a) block after ACCEPTED; (b) allow, and mark live quotations stale so they cannot be accepted or contracted; (c) allow, and auto-create a revision draft. (a) is simplest; (c) needs re-pricing rules. |
| D-03 | Quotation terminal states | No REJECTED, CANCELLED or EXPIRED status; expiry is computed. A NOT_FEASIBLE request has no closing state. | How a request ends without a sale | Add explicit terminal states, or keep the computed expiry plus a request-level closure. |
| D-04 | Installation prerequisites | EVN, PCCC and structural items start not required, and a Manager may toggle `IsRequired` at any time (`InstallationHandlers.cs:103, 376`). This is documented behaviour. | Whether any item is mandatory by policy (e.g. EVN above X kWp) | Policy-driven defaults vs Manager discretion with audit. |
| D-05 | Installed vs quoted equipment | Not compared (BL-08) | Whether substitution is allowed | Block, warn and require approval, or allow freely. |
| D-06 | Equipment warranty source | Technician-typed months; coverage uses the contract warranty only | Whether equipment warranty participates in coverage | Catalog-derived per-asset warranty vs contract-only. |
| D-07 | Knowledge base audience | Every signed-in role (including Customer) can query all published documents (`KnowledgeControllers.cs:147`); there is no audience field | Whether internal documents exist | Add a per-document audience, or a strict publishing policy. |
| D-08 | Monitoring & alerts | Not implemented; not described in `guides/` | Whether it is in scope for this release | Descope explicitly, or plan a module. |

---

## 5. Cross-module inconsistencies

| Invariant | Enforced in code | Enforced in database | Gap |
|---|---|---|---|
| No customer-visible DRAFT quotation | `GetForCustomerAsync`, `AcceptAsync`, `ListForCustomerAsync` filter drafts | none | ok in code [Đã xác định] |
| One draft and one live quotation per request | `CreateAsync` / `CreateRevisionAsync` checks | `ux_quotation_one_draft`, `ux_quotation_one_live` | PG race not executed |
| Contract only from an ACCEPTED quotation, once | `ContractHandlers.CreateAsync` | `ContractConfigurations.cs:65-67` unique indexes | ignores the survey version (BL-02) |
| Payment only for a SIGNED contract, full amount | `CreateOrGetAttemptAsync`; confirmation compares the paid amount and sends mismatches to `REVIEW_REQUIRED` | `ux_payment_attempt_one_payable`, `ux_payment_attempt_one_paid`, unique order code and provider reference, webhook payload hash | none found |
| No duplicate settlement | conditional `TryMarkContractPaidAsync`; a late second payment goes to `REFUND_REQUIRED` | `ux_payment_attempt_one_paid` | refunds are a manual follow-up (status only) |
| No project without full payment, one per contract | `CreateProjectAsync` (`InstallationHandlers.cs:72`) | `ux_installation_project_contract` | none found |
| Installation matches what was sold | not enforced | none | BL-08 |
| Survey ↔ quotation ↔ contract consistency | partly (generation only) | none | BL-02, BL-05, BL-06 |
| Cross-zone needs explicit authorization | dispatch and installation require a reason and flag | authorization rows | reschedule fallback (BL-09) |
| Warranty / protection claim within coverage | frozen at submission; claim must be APPROVED; limits by formula | revision tokens | PG concurrency tests skipped |

---

## 6. Security, concurrency and financial integrity

* **RBAC:** quotation edit, issue and revision endpoints are Sales-only. Generation and the review queue are
  Sales/Manager/Admin, with 401/403/404 verified by `AutomaticQuotationEndpointsTests` in this session. Ownership failures
  return 404.
* **Price authority:** on the automatic path no client price is accepted [Đã xác định]. On the manual path Sales can set
  any price (D-01).
* **Idempotency:**
  * automatic generation is idempotent per survey version, verified in SQLite;
  * a concurrent duplicate relies on PG partial unique indexes [Chưa xác định], since the PG test was not run;
  * payment webhooks are de-duplicated by payload hash and conditional transitions.
* **Transactions:** survey completion and quotation generation are separate transactions by design. A failure leaves
  `FAILED`, which can be retried. There is no outbox, so if the process stops between the two, recovery depends on
  someone retrying (repeat completion or the generate endpoint).
* **Data exposure:** BL-04 (pricing basis), BL-13 (catalog price), D-07 (knowledge base).

---

## 7. Database and migration review

* **Order:** `20261021164225_AddCommuneReferencePoints`, then `20261030122441_AutomatedInstallationQuotation`. Both are
  additive and **not applied**. `has-pending-model-changes` reported no pending changes in this session.
* **Existing data:**
  * `quotation.origin` NOT NULL DEFAULT `'SALES'` backfills existing rows, and the check constraint is compatible;
  * `pricing_basis` and `bill_of_materials` are nullable;
  * `Down` drops all three columns and the check, which loses BOM and pricing history if rolled back after use.
* **Constraints present:** quotation amounts are non-negative, total consistent, discount bounded, tax 0–20, and issued
  quotations require issued fields. There is no database constraint tying `onsite_survey_version` to the report (BL-02).
* **Deployment dependency:** BL-01.

---

## 8. Test coverage gaps (specific)

| Area | Missing test |
|---|---|
| Survey/quotation versions | `Amending_the_survey_after_acceptance_*`, `Accepting_a_quotation_of_an_older_survey_version_is_refused`, `Revision_after_survey_amendment_keeps_a_truthful_survey_version` |
| Automatic exceptions | `Issuing_an_automatic_draft_with_an_unresolved_missing_price_is_refused`, `Customer_quotation_view_has_no_internal_pricing_issues`, `Amending_a_quoted_survey_creates_a_sales_review_signal` |
| Configuration | `Invalid_default_tax_rate_fails_options_validation` |
| PostgreSQL | `Concurrent_automatic_generation_creates_one_quotation` (PG), API startup against a schema missing the newest migration |
| Scheduling | `Rescheduling_a_cross_zone_appointment_keeps_the_authorized_technician_or_returns_to_dispatch` |
| Installation | `Registering_an_asset_with_an_unknown_product_is_refused`, asset vs quotation comparison |
| E2E | HTTP-level E2E. The current E2E-01..10 call handlers directly on SQLite and seed the profile/site/draft for E2E-01. |
| NFR | No latency, throughput or load tests exist. Only bounded-time unit checks exist (shading, ray-cast budget). |

---

## 9. Prioritized fix backlog

| Priority | Issue | Module | Required change | Risk | Verification | Dependency |
|---|---|---|---|---|---|---|
| 1 | BL-01 | Deployment | Apply both migrations before deploy; startup schema check; fix the `FeatureOptions` comment | Low | PG startup test | none |
| 2 | BL-02 | Survey/Quotation/Contract | Version gate per D-02 | Medium | new version tests + E2E | D-02 |
| 3 | BL-05 | Survey/Quotation | Review signal on amendment | Low | unit | D-02 |
| 4 | BL-06 | Quotation | Truthful survey version on revisions | Low | unit | D-02 |
| 5 | BL-03 | Quotation | Unresolved-issue gate on issuing automatic drafts | Low | unit + E2E-03 variant | none |
| 6 | BL-04 | Quotation | Customer view without internal basis | Low (FE contract change) | unit + HTTP | FE |
| 7 | BL-07 | Config/Docs | Fix section name, validate options, add to `.env.example` | Low | options test | none |
| 8 | BL-09 | Scheduling | Cross-zone reschedule via dispatch | Medium | unit + PG | none |
| 9 | BL-08 | Installation | Asset product validation; quotation comparison | Medium | unit | D-05, D-06 |
| 10 | BL-15 / BL-17 / BL-12 | Audit/Installation/Disaster | Event ordering, round counter, release invariant | Low | unit | none |

---

## 10. Final release readiness assessment

**Must fix before deployment:**
* BL-01 (migration order) as a deployment procedure at minimum;
* BL-02, or explicitly accept the risk with D-02 recorded.

**Should fix before enabling the automatic quotation for real customers:** BL-03, BL-04, BL-05, BL-06, BL-07.

**Can be deferred, with owners:** BL-08, BL-09 (after reproduction), BL-12..BL-18, D-03..D-08.

**Overall:** not ready for production with the automatic quotation enabled until BL-01 and BL-02 are resolved.
Payment, contract and project-creation invariants look sound in code and in database constraints. Their PostgreSQL
concurrency tests have not been run.

### NOT VERIFIED
* 63 skipped PostgreSQL/Redis tests, including scheduling (25), installation (14), knowledge base (6), after-sales and
  payment (7), schema (5), settings (2), catalog import (2) and Redis rate limiting (2). Docker daemon unavailable.
* Applying either migration to PostgreSQL, and the BL-01 runtime failure.
* Real PayOS, S3, PVGIS/NASA, SMTP and RAG services.
* HTTP-level end-to-end flows.
* Load/latency NFRs.
* Domains J and K beyond what is stated in the matrix, and the parts of A, C, H, I and M marked partial.
* That the earlier handoff summaries are complete: they were treated as claims, and BL-03 and BL-07 contradict
  `automated-quotation-business-handoff.md`.
