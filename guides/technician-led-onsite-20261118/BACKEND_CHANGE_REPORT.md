# Backend: Technician-led on-site sales & after-sales (2026-11-18)

Baseline `f1dde1e` (origin/main) = HEAD `8f70cb2` (same tree). All changes are uncommitted in the working tree; nothing committed, pushed or deployed;
Supabase untouched. FE contract: `FE_HANDOFF_TECHNICIAN_ONSITE.md`.

## 1. Flow before → after

| | Before | After |
|---|---|---|
| Installation | Technician issues → **Customer accepts in app** → contract DRAFT → sign → **customer pays** → schedule confirmed | Technician issues (prices frozen) → **Technician records on-site decision** → Technician opens **QR / cash** → **verified payment creates the contract** → Technician records the agreed date → Manager prepares + confirms paper signature → contract PAID (no second charge), schedule confirmed by the system → install → **Manager accepts internally** |
| After-sales (technician quotation) | Customer accepts/rejects + pays in app; customer accepts completion | Technician records AGREED/DECLINED, opens QR/cash; paid work only after verification; technician records the customer's on-site confirmation |

## 2. Design decisions
- Payment line `INSTALLATION_QUOTATION` (+ `installation_quotation_id`); settlement (`InstallationQuotationPaymentSettlement`) creates the contract in the
  payment confirmation transaction, under the survey-request lock; links the attempt to the contract. Unique settled index → exactly one contract.
- Contract status ≠ payment: `paymentCompleted / paymentAttemptId / paymentConfirmedAt` on the view; prepaid contract moves SIGNED → PAID at the
  signature approval (signature evidence still required; never auto-signed).
- One live payment per quotation: quotation + request row locks in the creating transaction; closure / withdrawal refused while a payment is open.
- `PENDING_CUSTOMER_ACCEPTANCE` value kept (data + live older API compatibility) = awaiting internal acceptance.
- Substitution: new status `INTERNALLY_APPROVED` (older API ignores it rather than crashing on a manager-only `APPROVED` row).
- Legacy data kept working: SIGNED-unpaid contracts are still paid through the old contract payment endpoints; legacy ACCEPTED quotations without a
  contract are paid on site like new ones; Sales-issued after-sales quotations keep the customer accept/pay path.

## 3. Endpoints
New: `POST /api/technician/quotations/{id}/on-site-decision`, `POST|GET /api/technician/quotations/{id}/payment`, `POST …/payment/cancel`,
`POST /api/installation-projects/{id}/acceptance`, `POST /api/technicians/me/after-sales/quotations/{id}/on-site-decision`,
`POST|GET …/after-sales/quotations/{id}/payment`, `POST …/payment/cancel`, `POST /api/technicians/me/after-sales/requests/{id}/completion-confirmation`.
Changed: `…/issue` (no customer notification), contract view (payment fields), schedule view (`awaitingContractSignature`), schedule recording
(prepaid contracts; planned-conflict check), substitution approval (internal, final), manual contract creation (needs a settled payment).
Retired (410): customer quotation list/get/accept/reject/revision-request; customer slot offers + select; technician slot-offer publishing; customer
installation acceptance and substitution approval.

## 4. Migration `20261118102011_TechnicianLedOnSiteSales` (additive)
Adds `payment_attempt_item.installation_quotation_id` (FK quotation, RESTRICT) + index + unique (attempt, quotation) + unique settled; widens
`ck_payment_attempt_item_type` / `_target`; adds `ck_payment_attempt_item_installation_quotation`; adds `INTERNALLY_APPROVED` to the substitution status
check and active index. Down refuses while new-flow rows exist. Scripts: `TestResults/qa-2026-11-18/onsite/migration/*.sql` (git-ignored). Applied only to
disposable local PostgreSQL. **Not applied to Supabase** (needs approval, backup, rehearsal). Deploy the API before enabling technician payments: an
older instance confirming such a webhook would flag the line for manual handling (money kept, no contract).

## 5. Tests (`TestResults/qa-2026-11-18/onsite/onsite-final-3.trx`, PostgreSQL 17 + Redis 7 + S3-compatible, 5 m 20 s)
Unit 1 412 passed · Integration 469 passed (+2 opt-in NFR not executed by design) · E2E 24 passed · **0 failed**.
Earlier run `onsite-final-2.trx`: 2 failures under machine load (Redis replica fallback, HTTP client abort), unrelated code; both classes 29/29 on rerun
(`onsite-load-failures-rerun.trx`) and 0 failures in the final run.
New test files: `OnSiteQuotationPaymentTests`, `TechnicianScheduleOnSiteTests`, `OnSiteServiceQuotationTests`, `TechnicianLedSalesE2ETests`,
`TechnicianLedOnSiteEndpointsTests`, `PostgresTechnicianLedSalesMigrationTests`; legacy-flow tests converted (retired customer steps → technician /
internal steps; legacy data via `LegacyFlowData`).

## 6. Limitations / open
- Prepaid contracts confirm the schedule only when the signature is approved (legal paper signature kept); the installation cannot start before.
- After-sales completion: a customer who disagrees on site is not recorded by the technician (request stays waiting; customer may dispute; Manager follows up).
- Refunds remain manual (unapplied-money ledger); manufacturer warranty still blocked by the pending business decision.
- FE not changed; migration not applied to any shared database.
