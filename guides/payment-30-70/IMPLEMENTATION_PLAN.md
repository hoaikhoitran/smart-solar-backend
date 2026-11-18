# Payment 30/70, paper contract, customer acceptance — Implementation Plan

> Planning skill: `.agent-skills/superpowers/skills/writing-plans` (project skill). User overrides: plan stored here (not `docs/superpowers/plans`),
> no commits, implementation starts right after the plan (no review stop). Execution: native (one agent, then full regression).

**Goal:** installation contracts are paid in two installments — 30% before the paper signature, 70% within 3 days after the customer accepts the
installation — with a multi-page hand-signed contract stored per page, customer acceptance restored, overdue collection handled by people, and
finance figures on the admin dashboard. After-sales stays 100% upfront.

**Architecture:** modular monolith (.NET 8, EF Core 8, PostgreSQL). New concepts live in `SmartSolar.Modules.CustomerContracts` (installments,
paper signature, collection) with persistence in `SmartSolar.Infrastructure`; payments keep `PaymentService` + `IPaymentItemSettlement`
(new line type `CONTRACT_INSTALLMENT`). Lifecycles are separate: contract status, installment status, installation status, collection case status.

**Tech stack:** ASP.NET Core 8, EF Core 8 / Npgsql, xUnit (SQLite unit host, PostgreSQL/Redis/S3 gated integration), PayOS, S3-compatible storage.

**Baseline:** reference `462733c` = merge of HEAD `8437dcc` (branch `feat/technician-led-onsite-workflow`), identical tree, clean working tree.

## Global constraints
- Backend, schema, tests, backend/FE docs only. No FE code. No commit / push / deploy.
- Money: `decimal` / `numeric(18,2)`, whole VND; deposit = `round(T × 30%, 0, AwayFromZero)`, balance = `T − deposit`.
- Payment success only from a verified PayOS webhook / server-to-server reconciliation or a Manager/Admin cash confirmation (unique receipt,
  VERIFIED `PAYMENT_EVIDENCE`). Technicians and Sales never mark money received.
- No automatic confiscation, repossession, refund or legal decision. No fixed cure period invented.
- History never deleted (payments, files, contracts, audit). Legacy contracts keep their meaning (100% paid stays paid; no 70% receivable added).
- Supabase production migration allowed only after: target verified, history compared, backup + restore check, rehearsal PASS, compatibility check.

## 1. Current architecture (as read)

| Module | Today (HEAD 8437dcc) |
|---|---|
| Quotation | technician cart → `issue` → technician records `AGREED/DECLINED/REVISION_NEEDED` (`QuotationOnSiteDecisions.cs`) |
| Payment | `OnSiteQuotationPaymentHandlers`: technician opens 100% payment of the agreed quotation (`INSTALLATION_QUOTATION` line) |
| Contract | created by the payment settlement (`InstallationQuotationPaymentSettlement` → `ContractHandlers.CreateForPaidQuotationAsync`), DRAFT → ISSUED → official PDF → AWAITING_SIGNATURE → signed scan → UNDER_REVIEW → Manager review → SIGNED → PAID (prepaid) |
| Installation | project only for `PAID` contracts; schedule confirmed when `PAID`; completion → `PENDING_CUSTOMER_ACCEPTANCE` = Manager internal acceptance (`AcceptInternallyAsync`); customer acceptance retired (410) |
| After-sales | technician quotation, 100% before paid work; unchanged |
| Dashboard | `AfterSalesReportQueries` (money split by line type), `AdminOverviewQueries` |

## 2. Lifecycle old → new

| Step | Old | New |
|---|---|---|
| Agreement | quotation ACCEPTED, no contract | quotation ACCEPTED **and contract created at once** (status `ISSUED`, frozen content, canonical hash, payment plan `DEPOSIT_30_BALANCE_70`, 2 installments) — customer can read it |
| Payment 1 | 100% before contract | **deposit 30%** of the contract (installment 1) |
| After payment 1 | contract created | contract `ISSUED → AWAITING_SIGNATURE` |
| Signature | official PDF + scan + Manager review | **paper signed on site; technician uploads every page, then finalizes** → `SIGNED`; no Manager review |
| Installation | needs `PAID` (100%) | needs **ready for installation** = `SIGNED` payment-plan contract (deposit verified) — or legacy `PAID` |
| Acceptance | Manager internal | **Customer ACCEPT / REQUEST_REWORK** (Manager may return to rework, never accept) |
| Payment 2 | — | **balance 70% opened on ACCEPT**, `dueAt = acceptedAt + 72 h` |
| Completion | — | both installments settled → contract `PAID` (= fully paid), ownership `TRANSFER_ELIGIBLE` |
| Overdue | — | collection case: Sales contact / negotiation / agreement, escalation, Manager legal review / manual resolution |

### State models
- **Contract** (`customer_contract.status`, unchanged values): payment-plan path `ISSUED → AWAITING_SIGNATURE → SIGNED → PAID`; `DISCARDED` before
  the deposit; legacy path unchanged.
- **Installment** (`contract_installment.status`): `PENDING` (not yet payable: balance before acceptance) → `DUE` (payable) → `PAID`;
  `CANCELLED` (contract discarded before payment). Overdue = `DUE` and `now > due_at` (derived, never stored as payment state).
- **Installation project**: unchanged values; `PENDING_CUSTOMER_ACCEPTANCE` = customer decision again.
- **Collection case** (`contract_collection_case.status`): `OVERDUE → CONTACTED → NEGOTIATING → AGREEMENT_RECORDED`, `ESCALATED_TO_MANAGER`,
  `LEGAL_REVIEW_REQUIRED`, `RESOLVED`.
- **Ownership** (derived): `RETAINED` until fully paid, then `TRANSFER_ELIGIBLE` (business status only; not a legal transfer).

### Transition matrix (who / when)

| Transition | Actor | Guard |
|---|---|---|
| quotation ISSUED → ACCEPTED + contract ISSUED + 2 installments | surveying technician | quotation current, unexpired, survey unchanged, request open; one contract per quotation (unique index) |
| deposit DUE → PAID; contract ISSUED → AWAITING_SIGNATURE | payment settlement (webhook / reconciliation / Manager cash) | attempt reserved for this installment, exact amount |
| paper set: pages uploaded / finalized → contract SIGNED | surveying technician (or Manager/Admin support) | contract AWAITING_SIGNATURE, pages 1..N complete, files VERIFIED |
| project / schedule confirmation | Manager / system | contract ready (payment plan SIGNED / PAID, or legacy PAID) |
| completion report → PENDING_CUSTOMER_ACCEPTANCE | assigned technician | unchanged checks |
| ACCEPT → COMPLETED + balance PENDING → DUE (due +72 h) | **customer owner only** | pending round, substitution approved |
| REQUEST_REWORK | customer owner (reason) / Manager return-to-rework | pending round |
| balance DUE → PAID; contract SIGNED → PAID | payment settlement | reserved attempt, exact amount; late payment accepted and flagged |
| case OVERDUE created | system sweep | balance DUE, now > due_at, no open case |
| contact / negotiation / agreement / escalate | Sales, Manager, Admin | open case; never changes payment data |
| legal review / resolve | Manager, Admin | open case; resolution recorded, no automatic money or asset action |

### RBAC

| Data / action | Customer | Sales | Technician | Manager | Admin |
|---|---|---|---|---|---|
| Read contracts (list, detail, pages, installments, history) | own only | all | all | all | all |
| Open installment payment (QR / cash choice) | own, DUE installment | — | surveying technician | — | — |
| Confirm cash | — | — | — | yes | yes |
| Upload / finalize signed pages | — | — | surveying technician | support | support |
| Accept / reject installation | own project | — | — | — (return to rework only) | — |
| Collection: contact / agreement / escalate | — | yes | — | yes | yes |
| Collection: legal review / resolve | — | — | — | yes | yes |

### Financial invariants
1. `deposit + balance = total`; each installment amount > 0 and whole VND.
2. An installment is settled by at most one verified payment line (unique settled index); the line amount equals the installment amount.
3. `paid = Σ settled installment amounts`; `outstanding = total − paid ≥ 0`. Unapplied / duplicate money goes to the refund (unapplied) ledger.
4. Contract `PAID` ⇔ every installment `PAID` (payment-plan contracts).
5. Pending, refund-required and review-required money is never counted as applied.

## 3. Database model (additive migration `ContractInstallmentsPaperSignatureCollection`)
- `customer_contract`: `payment_plan varchar(30) NULL` (null = legacy full payment), `terms_version varchar(50) NULL`; relax
  `ck_customer_contract_published_document` and `ck_customer_contract_signed_review` for `payment_plan IS NOT NULL` (paper flow has no official
  PDF / single scan / reviewer).
- `contract_installment` (id, contract_id FK, sequence 1|2, kind DEPOSIT|BALANCE, percent, amount, status, opened_at, due_at, paid_at, paid_amount,
  paid_late, current_payment_attempt_id, settled_payment_attempt_id, created_at, updated_at, revision); unique (contract_id, sequence), checks.
- `contract_signature_set` (id, contract_id, version, expected_page_count, status UPLOADING|FINALIZED|SUPERSEDED, created/finalized by/at, note,
  revision); unique one non-superseded set per contract.
- `contract_signed_page` (id, signature_set_id FK, page_number, file_id FK unique, sha256, uploaded_by, uploaded_at); unique (set, page).
- `contract_collection_case` (id, contract_id, installment_id, status, opened_at, agreed_payment_date, resolved_at/by, resolution, revision);
  one open case per installment.
- `contract_collection_activity` (id, case_id, kind, actor_user_id, actor_role, occurred_at, channel, summary, outcome, agreed_payment_date,
  evidence_file_ids jsonb, created_at).
- `payment_attempt_item.contract_installment_id` + item type `CONTRACT_INSTALLMENT`; target check widened; unique settled per installment.
- File purpose `SIGNED_CONTRACT_PAGE` (JPEG, PNG, WebP, PDF; Technician, Manager, Admin upload).

## 4. API contract (summary; full shapes in FE_HANDOFF_PAYMENT_30_70.md)
- `POST /api/technician/quotations/{id}/on-site-decision` — AGREED now returns `contractId`.
- Installment payment: `POST|GET /api/technician/contracts/{contractId}/installments/{n}/payment`, `…/payment/cancel`; customer:
  `POST|GET /api/customers/me/contracts/{contractId}/installments/{n}/payment`, `…/cancel`. Cash confirmation unchanged
  (`POST /api/manager/payments/{attemptId}/cash/confirm`).
- Paper signature: `POST /api/technician/contracts/{id}/signature-set` (start / restart), `PUT …/signature-set/pages/{pageNumber}`,
  `POST …/signature-set/finalize`; read: `GET /api/contracts/{id}/signature-sets`, page URLs for staff and owner.
- Contracts read: `GET /api/contracts` (paged, filters: customerId, status, search) and `GET /api/contracts/{id}` for Sales, Technician, Manager, Admin;
  customer `GET /api/customers/me/contracts` (all own).
- Customer acceptance: `POST /api/customers/me/installation-projects/{id}/acceptance` (`ACCEPT` | `REQUEST_REWORK`) restored; Manager internal
  acceptance retired (410).
- Collection: `GET /api/collections`, `POST /api/collections/{id}/activities` (CONTACT | NEGOTIATION | AGREEMENT | NOTE), `…/escalate`,
  `…/legal-review`, `…/resolve`.
- Finance: `GET /api/admin/finance/overview?from&to&zoneId`.
- Retired (410): quotation 100% payment (`/api/technician/quotations/{id}/payment*`), manual contract creation for payment-plan quotations.

## 5. Migration, backfill, cutover
Additive only. Existing contracts get `payment_plan = NULL` (legacy full payment, untouched; no 70% receivable is ever created for them).
Cutover rule (no SQL backfill): an ACCEPTED quotation agreed before the release that has no contract and no legacy payment open or verified gets
its 30/70 contract when the technician re-confirms AGREED (same validity checks). A quotation with a legacy 100% payment in flight keeps the old
rules: its `INSTALLATION_QUOTATION` settlement still creates a legacy full-payment contract. PostgreSQL triggers stop the older API from paying a
30/70 contract 100% or moving it out of order during the rolling deploy. Down refuses while 30/70 rows exist.

## 6. Test plan
Unit (calculator, creation once, deposit before signature, duplicate / wrong amount, pages, RBAC, readiness, acceptance, due boundary, collection,
after-sales 100%), integration (PostgreSQL constraints, concurrency, webhook order, migration up/down, dashboard aggregates), E2E 1–7 from the
spec; full regression with TRX.

## 7. Risks / rollback / compatibility
- Old live API vs new schema: additive; legacy rows unchanged; the old API never creates payment-plan rows. A mixed window could let the old API
  offer a legacy "pay contract" on a payment-plan contract — mitigated by deploying the API right after the migration; documented.
- Rollback: code rollback is safe before any payment-plan row exists; Down refuses afterwards (export first).

## Review focus
1. A retried AGREED / concurrent agreement must not create two contracts (unique index + idempotent create) — test.
2. A late PayOS deposit after the contract was discarded must not settle — unapplied ledger — test.
3. Balance webhook racing a cash confirmation settles once — PostgreSQL race test.
4. Due boundary exactly at acceptedAt + 72 h (not overdue) and +72 h + 1 s (overdue) — unit test.
5. Customer B guessing customer A's contract / page id gets 404 — test.
