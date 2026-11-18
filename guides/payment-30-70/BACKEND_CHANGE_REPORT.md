# Backend change report — 30/70 payment, paper contract, customer acceptance, collection (2026-11-19)

Branch `feat/technician-led-onsite-workflow`, HEAD `8437dcc` (tree identical to baseline `462733c`). All changes are uncommitted in the working
tree: **nothing committed, pushed or deployed; no FE code changed.** Production database: migration applied and verified (MIGRATION_REPORT.md).
Planning skill: project skill `writing-plans` (`.agent-skills/superpowers/skills/writing-plans`) → IMPLEMENTATION_PLAN.md.

## 1. Business change (before → after)
| | Before (2026-11-18) | After |
|---|---|---|
| Agreement | technician records AGREED; no contract | AGREED creates ONE 30/70 contract (DRAFT, frozen snapshot, payment + ownership clauses, canonical hash) + 2 installments; customer previews it |
| Payment 1 | 100% of the quotation before the contract | deposit 30% of the contract (QR / cash), never "paid" |
| Signature | Manager publishes official PDF, scan, Manager review | paper signed on site after the deposit; technician uploads every page and finalizes; no review |
| Installation | needs 100% (PAID) | needs deposit + finalized paper (SIGNED) + existing prerequisites |
| Acceptance | Manager internal acceptance | customer ACCEPT / REQUEST_REWORK (rounds kept); Manager only returns to rework |
| Payment 2 | — | balance 70% opens at ACCEPT, due ≤ 72 h; late accepted + flagged |
| Overdue | — | collection case (Sales contact / negotiation / agreement, escalation, legal review, resolve); nothing automatic |
| Contracts read | Sales: own requests; Technician: none | Sales / Technician / Manager / Admin read all (paged, filters, search, audited); customers own only |
| Finance | after-sales report | + Admin finance overview of installments; installment lines counted once in the existing report |
| After-sales | 100% upfront | unchanged (no bundling with a 30/70 contract) |

## 2. Schema (migration `20261118202100_PaymentPlan3070PaperSignatureCollection`, additive)
* `customer_contract.payment_plan` (NULL = legacy), `terms_version`; relaxed `ck_customer_contract_published_document` / `_signed_review` for 30/70;
  `ck_customer_contract_payment_plan`.
* New tables: `contract_installment`, `contract_signature_set`, `contract_signed_page`, `contract_collection_case`, `contract_collection_activity`,
  `contract_access_audit` (checks, unique / partial indexes, FKs RESTRICT).
* `payment_attempt_item.contract_installment_id` + type `CONTRACT_INSTALLMENT`, widened target / type checks, unique settled line per installment.
* `installation_acceptance.decided_by_role`, `evidence_file_ids`; file purposes `SIGNED_CONTRACT_PAGE`, `ACCEPTANCE_EVIDENCE`, `COLLECTION_EVIDENCE`.
* PostgreSQL guards (rolling-deploy safety): no 100% contract attempt for a 30/70 contract; deferred state machine of 30/70 contracts; installment
  PAID only with exactly one settled verified line of its amount; frozen amounts. Down refuses while 30/70 data exists.

## 3. API
New: `POST|GET /api/technician/contracts/{id}/installments/{n}/payment`, `…/cancel`; `POST /api/technician/contracts/{id}/signature-set`,
`PUT …/signature-set/pages/{p}`, `POST …/signature-set/finalize`; `GET /api/contracts` (directory), `GET /api/contracts/{id}/signature-sets`,
`GET /api/contracts/{id}/signed-pages/{fileId}/url`, `GET /api/contracts/{id}/installments/{n}/payment`, Manager/Admin support upload routes;
customer `POST|GET /api/customers/me/contracts/{id}/installments/{n}/payment`, `…/cancel`, `GET …/signature-sets`, `GET …/signed-pages/{fileId}/url`;
`GET|POST /api/collections…` (list, detail, activities, escalate, legal-review, resolve); `GET /api/admin/finance/overview`.
Restored: `POST /api/customers/me/installation-projects/{id}/acceptance`.
Changed: on-site decision returns `contractId`; staff contract reads open to Technician and read-all; `ContractView.paymentPlan`; payment lines
show `contractInstallmentId`; legacy contract payment routes answer 409 `PAYMENT_CONTRACT_HAS_INSTALLMENTS` for 30/70 contracts; legacy contract
steps answer 409 `CONTRACT_PAYMENT_PLAN_NOT_APPLICABLE` for 30/70 contracts; package bundling refused for 30/70 contracts.
Retired (410): `POST /api/technician/quotations/{id}/payment`, `POST /api/customers/me/contracts/{id}/signed-document`,
`POST /api/installation-projects/{id}/acceptance` (Manager). Removed code: `InstallationHandlers.AcceptInternallyAsync`.
Details and payloads: FE_HANDOFF_PAYMENT_30_70.md.

## 4. Design / quality
* Module code: `CustomerContracts/PaymentPlan*.cs` (entities, pure calculator, ledger, views), `InstallmentPaymentHandlers`, `PaperSignatureHandlers`,
  `CollectionHandlers`, `ContractDirectoryHandlers`, `FinanceOverview`; `Installations/InstallationCustomerAcceptance.cs`. Cross-module calls through
  interfaces (`IAgreedQuotationContracts`, `IInstallationAcceptanceHook`) run inside the caller's transaction (one scoped DbContext).
* Controllers stay thin (`PaymentPlanControllers.cs`); validation → 400 with field errors; ownership → 404; state → 409; retired → 410.
* Idempotency: retried AGREED / same payment method / same page file / same page count return the existing result; races are settled by row locks
  (survey request, contract) plus unique indexes and concurrency tokens.
* Explicit readiness (`ContractStatuses.IsReadyForInstallation`) replaces every `status == PAID` installation gate (C# and SQL).
* Every write is audited in `contract_event` / `payment_event` / `installation_event` / `contract_collection_activity`; reads in `contract_access_audit`.

## 5. Tests
Final run `TestResults/qa-2026-11-19/full-regression-final.trx` (PostgreSQL 17 + pgvector, Redis 7, S3-compatible, 5 m 39 s):
**1,958 tests: 1,955 passed, 1 failed, 2 not executed** — Unit 1,449 passed · Integration 475 passed / 1 failed / 2 not executed · E2E 31 passed.
* Failed: `SimulationEndpointsTests.Sales_cannot_select_and_bad_input_is_a_validation_error` — HTTP client aborted under full-suite load (unrelated
  solar-simulation code; also seen in the first run). Rerun of the class: 20/20 passed (`simulation-rerun.trx`).
* Not executed: 2 opt-in NFR tests (`NFR_SIM_P95…` real providers, `NFR_SIGNED_URL…` 300 s expiry) by design.
* First full run (`full-regression-1.trx`): 6 failures → 4 real interactions fixed (Down guard too strict on `decided_by_role`; upgrade snapshot
  compared new columns), 2 load flakes (Redis replica timing, client abort) green on rerun (49/49).

New tests (all passing): `PaymentPlanCalculatorTests` (split / rounding / property test / 72 h boundary / summary / clauses), `PaymentPlanWorkflowTests`
(18: agreement once, retry, cutover, warranty, withdrawal, deposit cash / online / wrong amount / duplicate / late after discard, RBAC, paper pages,
ownership of files, tampered object, readiness, acceptance, rework, balance), `PaymentPlanCollectionTests` (5: overdue boundary, collection workflow +
RBAC, late payment, directory filters / search / read-all, finance no double counting), `PaymentPlanE2ETests` (7 workflows), `PostgresPaymentPlanTests`
(6: full flow through the triggers, guards vs manual SQL / older API, concurrent agreements and deposit starts, webhook vs cash race, concurrent sweeps
and finalizations, Down refusal + round trip), `PostgresPaymentPlanVolumeTests` (NFR: 2,000 contracts — directory 2 pages 23 ms, finance overview
56 ms; budgets 3 s / 5 s). Converted (same coverage, new rules): 13 tests of the retired 100%-before-contract flow and Manager acceptance
(API journeys now cash / QR deposit + paper signature; role matrix extended to every new route; customer acceptance restored; staff read-all).

## 6. Production migration
Applied 2026-11-18 20:50:54–20:51:02 UTC after: target identification, history comparison (no unknown migration), two backups each restore-verified,
rehearsal (up, older-API compatibility, down, re-up, idempotent re-run) on the restored copy. Script SHA-256
`251f9b1325d76b5512a1d87edb7f46093033716b56847fa05948156115362cbe`. Read-only verification: row counts unchanged, new objects present, 0 unvalidated
constraints, no 30/70 rows. See MIGRATION_REPORT.md.

## 7. Open items / decisions to confirm
* Legal (LEGAL_REVIEW_CHECKLIST.md): nature of the 30% (Art. 328), ownership retention (Art. 331–332), 72 h deadline fairness, late-payment
  consequences, in-app acceptance as handover proof, retention of signed page photos. Clauses carry `PAYMENT_30_70_DRAFT_PENDING_LEGAL_REVIEW`.
* The legacy staff scan upload + review routes remain for legacy contracts still in flight (30/70 contracts refuse them); retire them once no
  legacy contract waits for a signature.
* Deploy the new API image (not done here). Until then the migration is inert and the older API works unchanged (verified).
* FE work per FE_HANDOFF_PAYMENT_30_70.md.
