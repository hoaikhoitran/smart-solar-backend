# SmartSolar V2 — M-1 to M-4: migrations and implementation (2026-10-31, final; M-7 follows in its own report)

Audience: backend reviewers, the database owner, QA and the release owner.

Scope: the local working tree of `feat/fe-backend-gap-fixes` (HEAD `2810286` plus uncommitted changes). The Product Owner
authorised **generating** M-1 to M-4. Nothing was applied to any database, committed, pushed or deployed. M-5 and M-6
were not generated.

**PostgreSQL verification (final):** M-1..M-4 and the two earlier pending migrations were applied to **disposable** PostgreSQL 17
databases only. They were verified there on empty databases and with representative data, Down and Up. Details are in
[final-postgresql-concurrency-verification.md](final-postgresql-concurrency-verification.md).

The first draft of this report (written before Docker was available) marked these checks NOT_EXECUTED. Every status below is
the final one.

## 1. Summary

| Migration | Schema changes | Business implementation | Test evidence | Applied to DB? | Status |
|---|---|---|---|---|---|
| **M-1** `20261030201506_SalesTerminalStates` | `quotation`: statuses REJECTED/CANCELLED, `closed_at`, `closed_by_user_id` (FK), `close_reason`. `survey_request`: status `Closed`, `closed_at`, `closed_by_user_id` (FK), `closure_reason_code`, `closure_note`, index `(assigned_sale_id, status)`. `survey_request_event`: types REQUEST_WITHDRAWN, REQUEST_CLOSED. | **Flows:**<br>• customer rejects an ISSUED quotation (atomic vs accept);<br>• customer withdraws a request;<br>• Sales closes one (NOT_FEASIBLE / CUSTOMER_DECLINED / NO_RESPONSE / OTHER).<br>**Effects:**<br>• open quotations are CANCELLED with the request;<br>• refused with a contract or an open appointment;<br>• terminal: never reopened, quotation work and survey amendment refused;<br>• out of the unclaimed and quotation review queues;<br>• history readable. | `SalesTerminalStateTests` (7), `ApprovedPolicyEndpointsTests` (401/403/404/400) | **No persistent DB** (disposable test DBs only) | MIGRATION_GENERATED, BUSINESS_IMPLEMENTED, TESTED_SQLITE; TESTED_POSTGRESQL (constraints, Down/Up, closure/contract/revision/reject races); reopening decided (B-01: new request) |
| **M-2** `20261030201618_KnowledgeDocumentAudience` | `knowledge_document.audience` (NOT NULL DEFAULT 'INTERNAL', check) and index `(status, audience)`. New `knowledge_document_audience_change` (audit). `chat_message.retrieval_audience` (check). | **Audience:** the column is the only source; the interim tag grants nothing and is only flagged for review. Admin `PUT …/audience` with reason, revision-checked, audited. Customer retrieval, the citation re-check and stored citations all use it.<br>**Answer containment (customer):** legacy answers, staff-retrieved answers and answers citing a now-internal document are withheld. Their text and citations are hidden in sessions and idempotent replays, and never sent back as model history. The stored text is kept. | `KnowledgeAudienceTests` (2), `KnowledgeBaseEndpointsTests` (reclassification, replay, malicious RAG), endpoint RBAC | **No persistent DB** (disposable test DBs only) | MIGRATION_GENERATED, BUSINESS_IMPLEMENTED, TESTED_SQLITE; TESTED_POSTGRESQL (default INTERNAL on legacy rows, audience check); withheld answers stay withheld (B-03) |
| **M-3** `20261030201748_InstallationEquipmentSubstitution` | New `installation_equipment_substitution` (project, contract, quotation, fingerprint varchar(64), status, Manager decision, customer decision, superseded, revision; 5 checks; partial unique "one active per project"). New `installation_equipment_substitution_item` (one row per affected product). | **Records:** approvals are rows instead of events (events stay as the audit trail). A new approval supersedes a stale one; registering panels/inverters supersedes the active record; a customer rework request declines it.<br>**Gates:** report and handover read only the active record for exactly the current comparison. A duplicate approval is refused. A Manager cannot agree for the customer.<br>**Views:** staff see the history. | `ApprovedInstallationPolicyTests` (substitution with DB rows; decline/supersede/duplicate/DB-unique; matching; legacy), `InstallationTests`, `FaultRequestTests` (after-sales replacement), endpoints | **No persistent DB** (disposable test DBs only) | MIGRATION_GENERATED, BUSINESS_IMPLEMENTED, TESTED_SQLITE; TESTED_POSTGRESQL (one-active partial unique index 23505) |
| **M-4** `20261030201824_ManufacturerWarrantyCoverage` | `service_item`: coverage MANUFACTURER_WARRANTY; `equipment_warranty_months/_source/_ends_at`; check "MANUFACTURER_WARRANTY needs `equipment_warranty_ends_at`". `installed_asset`: `warranty_source`, `warranty_starts_at`, `warranty_ends_at`, plus 2 checks. **Backfill:** `warranty_source = 'LEGACY_UNVERIFIED'` where a duration exists. | **Sources:** asset registration records `warranty_source = CATALOG_PRODUCT`; start and end stay unset (no invented date).<br>**Coverage:** every decision snapshots the item's equipment-warranty facts. MANUFACTURER_WARRANTY is refused (`AFTER_SALES_MANUFACTURER_WARRANTY_POLICY_PENDING`), and the database also refuses it without an end date. Other categories are unchanged. | `ManufacturerWarrantyTests` (2), `FaultRequestTests` (snapshot), `AssetIntegrityTests` | **No persistent DB** (disposable test DBs only) | MIGRATION_GENERATED, BUSINESS_IMPLEMENTED (safe part), TESTED_SQLITE; TESTED_POSTGRESQL (backfill, constraints); verified activation date implemented (B-04); coverage decisions **BLOCKED_BY_PRODUCT_DECISION** (payer) |

**Migration order:**
1. `20261021164225_AddCommuneReferencePoints` (existing, unchanged)
2. `20261030122441_AutomatedInstallationQuotation` (existing, unchanged)
3. `20261030201506_SalesTerminalStates`
4. `20261030201618_KnowledgeDocumentAudience`
5. `20261030201748_InstallationEquipmentSubstitution`
6. `20261030201824_ManufacturerWarrantyCoverage`

Each migration has a `.Designer.cs`, and `AppDbContextModelSnapshot.cs` was updated by the tools. Only M-4's
`Up` was hand-edited, to add the documented backfill statement.

## 2. Affected entities and tables

| Migration | Entities | Tables | Main code |
|---|---|---|---|
| M-1 | `Quotation`, `SurveyRequest`, `SurveyRequestEvent`, `SurveyRequestStatus` (`Closed = 7`), `SurveyRequestClosureReasons` | `quotation`, `survey_request`, `survey_request_event` | `Quotations/QuotationSalesOutcomes.cs` (new), `QuotationHandlers.cs`, `QuotationAutomaticGeneration.cs`, `QuotationUnitOfWork.cs`, `OnSiteSurveyHandlers.cs`, `PreSurveyUnitOfWork.cs`, `SalesOutcomeControllers.cs` (new), `QuotationControllers.cs` |
| M-2 | `KnowledgeDocument.Audience`, `KnowledgeDocumentAudienceChange` (new), `ChatMessage.RetrievalAudience` | `knowledge_document`, `knowledge_document_audience_change`, `chat_message` | `KnowledgeDocumentHandlers.SetAudienceAsync`, `ChatService.cs`, `KnowledgeUnitOfWork.cs`, `KnowledgeControllers.cs` |
| M-3 | `EquipmentSubstitution`, `EquipmentSubstitutionItem` (new) | `installation_equipment_substitution`, `installation_equipment_substitution_item` | `InstallationEquipmentHandlers.cs`, `InstallationHandlers.cs`, `InstallationUnitOfWork.cs` |
| M-4 | `ServiceItem` (snapshot), `InstalledAsset` (source and period), `ItemCoverages.ManufacturerWarranty`, `EquipmentWarrantySources` | `service_item`, `installed_asset` | `ServiceManagerHandlers.DecideCoverageAsync`, `InstallationHandlers.RegisterAssetsAsync` |

## 3. New constraints and indexes

**M-1**
* `ck_quotation_status`: extended with REJECTED, CANCELLED.
* `ck_quotation_closed_fields`: `(status IN ('REJECTED','CANCELLED')) = (closed_at IS NOT NULL AND closed_by_user_id IS NOT NULL)`.
* `ck_survey_request_status`: extended with `Closed`.
* `ck_survey_request_closure_terminal`: closure columns only on Cancelled or Closed.
* `ck_survey_request_closed_fields`: a Closed request always has `closed_at`, `closed_by_user_id` and `closure_reason_code`.
* `ck_survey_request_closure_reason`: `closed_at` and the reason code come together, and the code is one of the five
  approved outcomes. It covers every terminal outcome, not only cancellation, and a **legacy Cancelled row without
  metadata stays valid**.
* `ck_survey_request_event_type`: extended.
* FKs to `user_account` for both `closed_by_user_id` columns.
* `ix_survey_request_assigned_sale_status`. EF replaced the plain FK index `IX_survey_request_assigned_sale_id`, which the
  composite index covers.
* The partial unique indexes `ux_quotation_one_live` / `ux_quotation_one_draft` are unchanged; REJECTED and CANCELLED are
  outside them.

**M-2**
* `ck_knowledge_document_audience`.
* `ck_chat_message_retrieval_audience` (NULL allowed: legacy answers).
* `ck_knowledge_document_audience_change_values` (both values valid and different).
* FKs: document, actor. Indexes `ix_knowledge_document_status_audience` and `ix_knowledge_document_audience_change_document_created_at`.

**M-3**
* Status check.
* APPROVED ⇒ `customer_approved_at`; CUSTOMER_DECLINED ⇒ `customer_declined_at`.
* Customer fields consistent (never both approved and declined; customer id iff a customer decision).
* SUPERSEDED ⇔ `superseded_at`.
* Item quantities ≥ 0 and different.
* `ux_installation_equipment_substitution_active`: **unique `project_id` WHERE status IN (CUSTOMER_APPROVAL_PENDING,
  APPROVED)**.
* FKs: project, contract, quotation, Manager, customer, item → substitution, item → product (all RESTRICT).
* **Fingerprint:** the algorithm (unchanged) emits the first 128 bits of SHA-256 as 32 lowercase hex characters. The column
  is `varchar(64)`, so a later move to a full SHA-256 hex digest fits without another migration.

**M-4**
* `ck_service_item_coverage`: extended.
* `ck_service_item_manufacturer_warranty`: MANUFACTURER_WARRANTY ⇒ `equipment_warranty_ends_at IS NOT NULL`.
* `ck_service_item_equipment_warranty_source`.
* `ck_installed_asset_warranty_source`: a duration always has a source; source values limited.
* `ck_installed_asset_warranty_period`: an end needs a start, a duration and end > start.

## 4. Backfill and existing data

| Migration | Existing rows after `Up` |
|---|---|
| M-1 | No status changes. Closure columns NULL. A legacy `Cancelled` survey request stays valid. Every existing quotation keeps its status. |
| M-2 | **Every document becomes INTERNAL**: nothing is exposed by the migration. Documents carrying the interim `audience:customer` tag are only flagged (`legacyAudienceTag`) for Admin review, never reclassified automatically. Every existing chat answer has `retrieval_audience` NULL, so it is **withheld from customers** (still readable by staff and kept in the database). |
| M-3 | No rows. The event-based approvals of the previous phase existed only on this undeployed branch. They are **not migrated** (no approval is synthesised) and remain as installation events. Completed projects without a record read `NOT_CHECKED_AT_HANDOVER`, never "non-compliant". |
| M-4 | `UPDATE installed_asset SET warranty_source = 'LEGACY_UNVERIFIED' WHERE warranty_months IS NOT NULL AND warranty_source IS NULL`. Those durations were typed or defaulted and cannot be verified, so they are not labelled as catalog data. No start or end dates are set. Existing coverage decisions are unchanged; their snapshot columns stay NULL, which is accurate because no snapshot was taken. |

## 5. Rollback implications

| Migration | `Down` | Risk after use |
|---|---|---|
| M-1 | Drops the columns and restores the old checks and the FK index. | Fails while rows are REJECTED, CANCELLED or `Closed`: remap them first. Closure actor, reason and time are lost. Prefer a forward fix. |
| M-2 | Drops the column, the audit table and `retrieval_audience`. | Loses classification and audit. The code would no longer compile against the old model. A code rollback to the interim tag version re-exposes nothing (that version also defaults to internal). |
| M-3 | Drops both tables. | Loses the structured approvals. The events remain as evidence, but the gates of this code need the table. |
| M-4 | Drops the columns and restores the coverage check. | Fails if any item has MANUFACTURER_WARRANTY (impossible through the API today). Loses snapshots and sources. |

## 6. PostgreSQL concurrency (final)

* **Closure / withdrawal vs contract creation (P0, found by this report's first draft):**
  * Fixed: both paths take the survey-request row lock first and re-check under it.
  * Verified with two PostgreSQL connections in both orders.
* **Reject vs accept, duplicate closures, revision vs closure:** verified on PostgreSQL.
  * The revision race also exposed the DRAFT-cancellation constraint defect, now corrected in M-1.
* **Substitution approvals:** the one-active partial unique index was verified on PostgreSQL (23505); the handler path is
  serialised by the project revision token.
* **Concurrent audience changes:** revision token, verified sequentially on SQLite only.
* **M-4 backfill:** verified on test data; not timed on production-sized data.

## 7. Product Owner decisions (final state)

| Topic | Decision | Status |
|---|---|---|
| Reopening (B-01) | Closed and withdrawn requests are final; the customer creates a new request. Nothing is copied. | Implemented and tested |
| Request with a DRAFT contract (B-02) | Sales must discard the DRAFT with a reason first. | **Implemented with M-7** (`20261031043834_ContractDraftDiscard`, approved and generated): see [m7-contract-draft-discard-implementation.md](m7-contract-draft-discard-implementation.md). |
| Legacy customer answers (B-03) | Keep withheld; no automatic release or deletion; content kept. | Implemented |
| Manufacturer warranty (B-04) | Entitlement needs a verified product-specific activation date; no date from unverified durations. | Implemented: Manager records a verified date with a document. **Claim handling and payer: pending.** |
| Equipment equivalence (B-05) | None: every difference needs Manager and customer approval. | Implemented |
