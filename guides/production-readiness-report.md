# SmartSolar — production readiness report (authoritative, 2026-10-31, after M-7)

Audience: the release owner and operations.

Scope: the local working tree of `feat/fe-backend-gap-fixes` (HEAD `2810286` plus uncommitted changes). Nothing was committed,
pushed or deployed, and no shared or persistent database was touched.

This report replaces every earlier version. Evidence: [final-postgresql-concurrency-verification.md](final-postgresql-concurrency-verification.md),
[m1-m4-migration-implementation-report.md](m1-m4-migration-implementation-report.md) and [full-regression-test-report.md](full-regression-test-report.md).

## Recommendation: **NO-GO** for production. Status: **BLOCKED** (code **ready for review**)

Production is blocked by:
* provider and NFR verification that has not been executed;
* applying the migrations to a staging copy;
* the combined PostgreSQL + Redis suite, which is not green in this environment (§7);
* one undecided policy (manufacturer-claim handling and payer).

No known P0/P1 defect remains. The closure-vs-contract race is fixed and verified on PostgreSQL.

## 1. Schema and migrations (7 pending, none applied anywhere persistent)

| Migration | State |
|---|---|
| `20261021164225_AddCommuneReferencePoints` | Generated, additive. Applied only to disposable test databases. |
| `20261030122441_AutomatedInstallationQuotation` | Generated, additive. Same. |
| `20261030201506_SalesTerminalStates` (M-1) | Generated (authorised); `ck_quotation_issued_fields` corrected on 2026-10-31 so that a never-issued DRAFT can be CANCELLED. Same. |
| `20261030201618_KnowledgeDocumentAudience` (M-2) | Generated (authorised). Same. |
| `20261030201748_InstallationEquipmentSubstitution` (M-3) | Generated (authorised). Same. |
| `20261030201824_ManufacturerWarrantyCoverage` (M-4) | Generated (authorised), with the legacy `warranty_source` backfill. Same. |
| `20261031043834_ContractDraftDiscard` (M-7) | Generated (approved): status DISCARDED with discard metadata and constraints. Applied only to disposable test databases. |
| Not generated | M-5 (energy readings/alerts), M-6 (optional pricing-approval table) |

* **Verified on disposable PostgreSQL 17:**
  * the full chain on empty databases;
  * M-1..M-4 Down and Up with representative data (backfill, INTERNAL default, legacy rows valid, row counts unchanged);
  * every new constraint and index;
  * readiness reporting exactly the missing migrations.
* **Deployment order:** apply all seven, in timestamp order, on a **staging copy** first; time the M-4 backfill. While any is
  missing, the API answers `503 SCHEMA_NOT_READY` (`/health/ready` is 503, `/health/live` stays 200).
* **Rollback:** code rollback is safe before the migrations are applied.
  * M-1 `Down` is **refused** while REJECTED, CANCELLED or `Closed` rows exist (verified): remap them first.
  * M-4 `Down` loses `warranty_source`; after a re-`Up`, durations read `LEGACY_UNVERIFIED`.
  * M-7 `Down` is **refused** while DISCARDED contracts exist (verified).
  * Prefer forward fixes.

## 2. Configuration

* `Installation:ExemptiblePrerequisiteCodes` (default `[]`).
* `Quotation:*` (validated at startup).
* `Scheduling:TimeZoneId` (`Asia/Ho_Chi_Minh`), which is also the notification display zone.
* `Storage:S3:PresignedUrlTtlSeconds` (default 300).

## 3. Behaviour changes operations must know

* **Quotations:** discounts, reduced prices and zero prices need a Manager approval (Admin cannot approve). The customer can reject.
* **Requests:**
  * withdrawn and closed requests are final; a customer who wants to try again creates a new request;
  * closure is refused while a live contract exists or a survey appointment is open;
  * a DRAFT contract is first discarded by its Sales owner with a reason (M-7), then the request is closed;
  * re-contracting a quotation whose draft was discarded is refused.
* **Installations:**
  * EVN, PCCC and structural must be satisfied (no exemption rule is configured);
  * any panel/inverter difference needs a Manager approval and the customer's agreement before handover.
* **Knowledge base:**
  * after M-2 every document is INTERNAL; Admins reclassify customer-safe documents through `PUT …/audience`;
  * all existing customer chat answers are withheld from customers (kept for review).
* **Manufacturer warranty:**
  * dates exist only when a Manager records a verified activation date with a document;
  * the MANUFACTURER_WARRANTY coverage decision is refused (payer undecided).

## 4. External integrations

* **NOT EXECUTED:** real PayOS, S3, PVGIS/NASA, SMTP, RabbitMQ and the RAG service.
* **Logic verified with fakes:** webhook signature, idempotency, reconciliation, expiry, unapplied-money refunds, the RAG
  citation re-check.

## 5. Security

* All new endpoints are tested for 401/403/404/400. Manager-only decisions refuse Admin.
* Customers never receive catalog reference prices, pricing or approval diagnostics, quotation events, internal knowledge or
  withheld answers. This covers retrieval, citations, session history, idempotent replay and model history.

## 6. Business invariants

| Invariant | Code | Database / PostgreSQL evidence |
|---|---|---|
| No contract for a closed request; no closure past a contract | Lock and re-check on survey_request | **Verified** with two connections, both orders |
| No open quotation left on a closed request | Same lock on every quotation insert | **Verified** (revision vs closure race) |
| One winner for reject vs accept | Conditional updates | **Verified** |
| Terminal rows carry actor, time, reason | Handlers | Check constraints **verified** |
| One active substitution per project | Handlers | Partial unique index **verified** (23505) |
| Manufacturer coverage only with a computed end date | Handler refuses | Check constraint **verified** |
| Payment only for SIGNED contracts; single settlement; no duplicate project | Existing | Existing PostgreSQL payment/installation suites **pass** |

## 7. Test evidence

* Clean Release build: 0 warnings, 0 errors.
* Full suite with disposable PostgreSQL 17: **1,539 total, 1,537 passed, 0 failed, 2 skipped** (the Redis tests), in the latest
  two runs.
* Redis suite alone: 9/9.
* Without infrastructure: 1,539 / 1,463 / 0 / 76 skipped.
* **Update after the flaky-test investigation** (1,546 tests):
  * **PostgreSQL + Redis combined:** 1,546 / 1,546 / 0 failed / 0 skipped (Release, clean build).
  * **PostgreSQL only:** 1,544 / 0 failed / 2 skipped.
  * **Redis only:** 1,469 / 0 failed / 77 skipped.
  * **No infrastructure:** 1,467 / 0 failed / 79 skipped.
* **Root causes fixed:**
  * HTTP test hosts ran the dispatch worker on their shared SQLite connection (test harness);
  * a 1-in-10,000 payment order-code collision made a checkout answer `PAYMENT_CHECKOUT_IN_PROGRESS` (production
    `PaymentService` fix).
* **Still open:**
  * the original 401s and the acceptance race: NOT_REPRODUCED;
  * the real-Redis two-replica test: CONFIRMED_NOT_FIXED, intermittent under CPU starvation.
* Details: [flaky-test-root-cause-analysis.md](flaky-test-root-cause-analysis.md).
* *Superseded:* the combined run was previously NOT VERIFIED (11–13 HTTP failures in this native setup). See
  [final-postgresql-concurrency-verification.md](final-postgresql-concurrency-verification.md) §8–§9.

## 8. Release blockers

1. **Staging:** apply the seven migrations to a staging copy through the approved process, time the M-4 backfill, and repeat
   the readiness and smoke checks there.
2. **Providers:** real PayOS, S3, PVGIS/NASA, SMTP and RAG are not verified.
3. **NFRs not measured:** load p95, simulation p95, PayOS ≤ 2 min, signed-URL expiry, RAG accuracy.
4. **Test stability:** the combined suite now passes locally; it should be repeated in CI.
   * Also open: the unexplained earlier 401s (NOT_REPRODUCED) and the intermittent real-Redis two-replica test
     (CONFIRMED_NOT_FIXED).
5. **Policy:** manufacturer-claim handling and payer (D-06/B-04).
6. **FE adoption:** [frontend-api-change-handoff.md](frontend-api-change-handoff.md) §0, §7, §8 and §9.
7. **New decisions (not blockers):** re-contracting after a discard; discarding amendment drafts.
