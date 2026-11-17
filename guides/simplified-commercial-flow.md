# Simplified quotation, contract and payment flow (2026-11-17)

Backend change for the frontend team, reviewers and operations. Code only: **nothing was committed, deployed or applied to a database**.
The schema change is the EF migration `20261117074228_SimplifiedCommercialFlow` (additive; see [Migration](#migration-and-existing-data)).

## AS-IS → TO-BE

| Step | AS-IS (until 2026-11-16) | TO-BE |
|---|---|---|
| Survey request → technician | Customer requests, Manager/dispatch assigns, Sales claims the request | unchanged (Flow 1 not touched) |
| Quotation | Completing the survey **generated** an AUTOMATIC quotation from the bill of materials; issues went to a Sales **PENDING_SALES_REVIEW** queue; Sales could also write a manual quotation with custom lines, custom prices and discounts, which needed a **Manager pricing approval** | The surveying **technician builds a cart** of catalog products; prices are catalog snapshots, totals by the backend; no price/discount/tax input; no Sales owner |
| Customer decision | accept / reject / request revision | unchanged; a revision is answered by the technician |
| Contract | **Sales** created the draft from the accepted quotation, issued, published, reviewed the signature (Sales or Manager) | the **draft is created automatically** on acceptance (same transaction); **Manager/Admin** issue, publish and confirm the signature |
| Payment | PayOS only, by the customer | **ONLINE (PayOS)** or **CASH**; cash is confirmed by Manager/Admin only |
| Installation project | Manager/Admin, only when PAID | unchanged (verified by tests) |
| Sales | owned quotation, contract and part of payment | **consult-only**: reads quotations/contracts of the requests it owns; no commercial step |

## Actors and roles

| Action | Customer | Technician | Sales | Manager | Admin |
|---|---|---|---|---|---|
| Build / edit / issue / revise the quotation cart | – | ✔ (only the technician who completed that survey) | – | – | – |
| Read quotations (staff) | – | own surveys | own requests | all | all |
| Accept / reject / request revision | ✔ (own) | – | – | – | – |
| Contract terms, issue, official PDF, publish, discard, amend | – | – | – | ✔ | ✔ |
| Read contracts (staff) | – | – | own requests | all | all |
| Submit the signed scan | ✔ (own) | – | – | – | – |
| Confirm the signature (review) | – | – | – | ✔ | ✔ |
| Pay online / choose cash / cancel | ✔ (own) | – | – | – | – |
| Confirm cash, read payment history | – | – | – | ✔ | ✔ |
| Create the installation project (PAID only) | – | – | – | ✔ | ✔ |

Every cell marked "–" answers 403 (role) or 404 (not the caller's resource). Covered by `SimplifiedCommercialFlowEndpointsTests`.

## State machines

**Quotation** (`origin` = `TECHNICIAN` for new ones; `SALES` / `AUTOMATIC` are history)

```
DRAFT ──issue──▶ ISSUED ──accept──▶ ACCEPTED (final; contract DRAFT created in the same transaction)
                   │  ├──reject──▶ REJECTED (final for this version)
                   │  └──revision-request──▶ REVISION_REQUESTED
                   └── a new version issued ──▶ SUPERSEDED   (from ISSUED or REVISION_REQUESTED)
DRAFT / ISSUED ── request withdrawn or closed ──▶ CANCELLED
legacy SALES/AUTOMATIC DRAFT ── technician starts the cart ──▶ CANCELLED (reason LEGACY_DRAFT_REPLACED)
```

**Contract**

```
DRAFT ──issue──▶ ISSUED ──official PDF + publish──▶ AWAITING_SIGNATURE ──signed scan──▶ UNDER_REVIEW
UNDER_REVIEW ──approve──▶ SIGNED ──payment confirmed (online or cash)──▶ PAID
UNDER_REVIEW ──reject──▶ SIGNATURE_REJECTED ──new scan──▶ UNDER_REVIEW
DRAFT ──discard──▶ DISCARDED        issued, unpaid version ──amendment issued──▶ SUPERSEDED
```

**Payment attempt** (`method` ONLINE or CASH; at most one PENDING/PROCESSING and one PAID per contract — unique indexes)

```
ONLINE: PENDING ─▶ PROCESSING ─▶ PAID | CANCELLED | EXPIRED | FAILED
        money with a wrong amount ─▶ REVIEW_REQUIRED      money for an already-paid contract ─▶ REFUND_REQUIRED
CASH:   PENDING (customer chose cash) ──Manager/Admin confirms──▶ PAID
        PENDING ──customer withdraws──▶ CANCELLED
```

Derived `paymentState` of the contract: `NOT_PAYABLE` → `AWAITING_PAYMENT` → `ONLINE_IN_PROGRESS` | `AWAITING_CASH_CONFIRMATION` → `PAID`.
The contract status stays `SIGNED` while cash is awaited (choosing cash is not a payment).

**Installation project**: created by Manager/Admin only from a `PAID` contract (409 `INSTALLATION_CONTRACT_NOT_PAID` otherwise); the
rest of its lifecycle is unchanged.

## Rules that make it safe

* **Cart pricing:** each line is `catalog unit price × quantity`, rounded to whole VND; the issue re-reads every price (snapshot);
  later catalog changes never alter an issued quotation. An inactive, non-VND or unpriced product is refused (`QUOTATION_PRODUCT_UNAVAILABLE`,
  `QUOTATION_PRICE_MISSING`). Tax = configured `Quotation:DefaultTaxRatePercent` (unchanged setting, 8 % by default); discount is always 0.
  For TECHNICIAN quotations `equipmentSubtotal` = sum of all cart lines and labor/transport/other = 0, so no amount is counted twice.
* **Feasibility:** a NOT_FEASIBLE survey gets no cart (`QUOTATION_SURVEY_NOT_FEASIBLE`); the check runs again at issue.
* **Contract exactly once:** the draft is inserted in the acceptance transaction (request lock → quotation lock → contract), the
  one-draft / one-live contract indexes back it up; a double-click acceptance is idempotent (PostgreSQL race test).
* **Cash:** choosing cash takes the same "one payable attempt" slot as an online link, so PayOS and cash can never run together. Before
  cash is chosen, an open online link is checked with PayOS (paid → contract paid; still open → refused until cancelled). Confirmation
  requires the exact contract amount, a receipt reference unique per provider, and a VERIFIED `PAYMENT_EVIDENCE` file uploaded by the
  confirming Manager/Admin; it marks the attempt PAID and the contract SIGNED → PAID in one transaction (conditional update). A repeated or
  concurrent confirmation is refused (`PAYMENT_ALREADY_PAID`). Database checks: a PAID cash attempt must have `confirmed_by_user_id`,
  `provider_reference`, `evidence_file_id`; those columns are only allowed on CASH attempts.
* **Late or duplicate online money:** a verified webhook for a contract already paid (e.g. in cash) makes that attempt `REFUND_REQUIRED`
  and opens one refund record for a Manager; nothing is refunded automatically and nothing is settled twice. Duplicate webhooks are
  deduplicated by payload hash. Reconciliation never queries PayOS for CASH attempts.

## Legacy classification

| Legacy item | Decision | What happened |
|---|---|---|
| Sales manual quotation create / edit / issue / revision / document | **Retired (API)**, code kept | Routes answer 410 `ENDPOINT_RETIRED`; handler code kept (unreachable) for legacy data and its tests; removal in a later clean-up |
| Sales ownership requirement on quotations / contracts | **Replaced** | Technician-of-the-survey for quotations, Manager/Admin role for contracts; Sales reads its own requests only |
| Automatic quotation after survey completion | **Retired** | No longer called; `automaticQuotationOutcome` always null (deprecated field); generator code kept for history |
| `PENDING_SALES_REVIEW` queue, `GET /api/quotations/pending-review` | **Retired** | 410 |
| Pricing review / Manager pricing approval | **Retired** | 410; there is no discount or price exception any more |
| `POST …/quotations/generate` | **Retired** | 410 |
| Confirm-survey-version | **Replaced** | The technician's cart is re-based on the current survey at issue (recorded event) |
| Sales contract create / issue / publish / review / discard / amend | **Replaced** | Same routes, Manager/Admin only; draft created automatically |
| BOM vs cart duplication | **Replaced** | The cart is the only price source; the survey bill of materials stays technical survey data and is not checked against the cart |
| Old jobs (payment reconciliation worker) | **Still used** | Unchanged for online attempts; CASH attempts are excluded |
| Sales closing a request with a reason | **Still used** | Not a quotation/contract/payment step; unchanged |
| Historical rows (SALES/AUTOMATIC quotations, Sales contracts, events) | **Kept** | Never deleted or rewritten; still readable; legacy ISSUED quotations can still be accepted (contract draft created) |

## API mapping (old → new)

| Old | New |
|---|---|
| `POST /api/survey-requests/{id}/quotations` (Sales) | `POST /api/technician/survey-requests/{id}/quotation` |
| `POST /api/quotations/{id}/lines` `{ productId?, name?, unit?, quantity, unitPrice?, description? }` | `POST /api/technician/quotations/{id}/lines` `{ revision, productId, quantity }` |
| `PUT /api/quotations/{id}/lines/{lineId}` `{ quantity, unitPrice?, description? }` | `PUT /api/technician/quotations/{id}/lines/{lineId}` `{ revision, quantity }` |
| `DELETE /api/quotations/{id}/lines/{lineId}?revision=` | `DELETE /api/technician/quotations/{id}/lines/{lineId}?revision=` |
| `PUT /api/quotations/{id}` (labor, transport, discount, tax, validity, terms) | none — computed by the backend |
| `POST /api/quotations/{id}/issue` `{ revision, reviewNote? }` | `POST /api/technician/quotations/{id}/issue` `{ revision }` |
| `POST /api/quotations/{id}/revisions` | `POST /api/technician/quotations/{id}/revisions` |
| `POST …/quotations/generate`, `GET /api/quotations/pending-review`, `POST …/confirm-survey-version` | none (410) |
| `POST …/pricing-approval-request`, `GET /api/manager/quotations/pricing-approvals`, `POST /api/manager/quotations/{id}/pricing-approval` | none (410) |
| `PUT /api/quotations/{id}/document` | none (410) — render from the quotation view |
| `GET /api/survey-requests/{id}/quotations`, `GET /api/quotations/{id}` | unchanged (read; plus `GET /api/technician/…` for the technician) |
| `POST /api/quotations/{id}/contracts` (Sales) | automatic on accept; route kept for Manager/Admin for legacy accepted quotations |
| `PUT /api/contracts/{id}`, `…/issue`, `…/official-document`, `…/publish`, `…/discard`, `…/amendments` (Sales) | same routes, **Manager/Admin** |
| `POST /api/contracts/{id}/review` (Sales or Manager) | same route, **Manager/Admin** |
| `GET /api/contracts/{id}/payments` (Sales, Manager, Admin) | same route, **Manager/Admin** |
| — | `POST /api/customers/me/contracts/{id}/payments/cash` (customer chooses cash) |
| — | `POST /api/contracts/{id}/payments/cash/confirm` (Manager/Admin) |
| `POST /api/customers/me/contracts/{id}/payments/cancel` | also withdraws a cash choice |

Response additions: quotation `origin: "TECHNICIAN"`; payment attempt `method`, `amountPaid`, staff-only `confirmedByUserId`,
`evidenceFileId`, `confirmationNote`, and `expiresAt: null` for CASH; contract payment view `paymentState`.
New file purpose `PAYMENT_EVIDENCE` (Manager/Admin; pdf/jpeg/png/webp, 15 MB). `CONTRACT_DOCUMENT` uploads are now Manager/Admin.

## Frontend checklist

1. **Technician app:** after `POST …/onsite-survey/complete`, open the cart: `POST /api/technician/survey-requests/{id}/quotation`,
   add catalog products (`GET` catalog as today), change quantities, remove lines, `issue`. Show prices read-only. Handle
   `QUOTATION_PRICE_MISSING` (ask the Admin to price the product), `QUOTATION_SURVEY_NOT_FEASIBLE`, `*_STALE_REVISION` (reload).
2. **Technician app:** list revision requests / rejections (`GET /api/technician/survey-requests/{id}/quotations`, status
   `REVISION_REQUESTED` / `REJECTED`) and create a new version with `…/revisions`.
3. **Sales screens:** remove quotation editing, generation, review queue, pricing approval, contract actions and payment history. Keep
   read-only quotation/contract views for consultation.
4. **Manager screens:** remove the pricing-approval queue. Add the contract work queue (`GET /api/survey-requests/{id}/contracts`,
   status DRAFT) with terms + warranty, issue, PDF generation/upload (`CONTRACT_DOCUMENT`) + attach, publish, signature review.
5. **Customer app:** after accepting, show "contract being prepared". When SIGNED, offer **Pay online** (existing flow) or **Pay in cash**
   (`…/payments/cash`); show `paymentState`; allow cancelling either before it is paid. Poll `GET …/payments` until `PAID`.
6. **Manager/Admin cash screen:** for contracts in `AWAITING_CASH_CONFIRMATION`, upload the receipt (`PAYMENT_EVIDENCE`), enter the
   amount (prefill the contract total, but the user must confirm it), receipt number and note, then `…/payments/cash/confirm`. Handle
   `PAYMENT_CASH_AMOUNT_MISMATCH`, `PAYMENT_CASH_RECEIPT_DUPLICATE`, `PAYMENT_ALREADY_PAID`.
7. Treat **410 `ENDPOINT_RETIRED`** as "this feature moved": `error.details.replacement` names the new route.
8. Stop reading `automaticQuotationOutcome` (always null) and the quotation `pricingApproval` / `pricingReview` blocks (null for new quotations).

Frontend files that call the changed routes (read-only scan of the `smart-solar` repository at commit `9bbb45b`; not edited):
`src/services/api/endpoints.ts` (quotation routes, `generate`, `pendingReview`, `confirmSurveyVersion`, `pricingApprovalRequest`,
`pricingApprovals`, `pricingApproval`), `src/hooks/useSalesQuotations.ts`, `src/hooks/useManagerQuotations.ts`,
`src/pages/pricing-approvals-page` and its route in `src/routes/protectedRoutes.tsx` / `src/routes/paths.ts`, `src/pages/ops/survey-request-page.tsx`,
`src/features/contracts/SalesContractPanel.tsx` and the other staff contract panels, `src/features/contracts/ContractPaymentPanel.tsx`,
`src/features/contracts/CustomerContractPayment.tsx`, `src/types/res/quotationsRes.ts`, `src/types/res/staffPaymentsRes.ts`.

## Migration and existing data

`20261117074228_SimplifiedCommercialFlow` — generated, **not applied anywhere** except throwaway PostgreSQL test databases.

Up (additive, no row changed or deleted):
* `quotation.sales_user_id`, `customer_contract.sales_user_id`: `NOT NULL` → nullable (legacy rows keep their owner).
* `ck_quotation_origin` accepts `TECHNICIAN`; `ck_file_reference_purpose` accepts `PAYMENT_EVIDENCE`.
* `payment_attempt`: `confirmed_by_user_id` (FK user_account), `evidence_file_id` (FK file_reference), `confirmation_note` (varchar 500),
  two indexes, checks `ck_payment_attempt_cash_confirmation` and `ck_payment_attempt_cash_fields_only_for_cash` (all existing rows
  satisfy them: the new columns are NULL and the provider is PAYOS).

Existing data: SALES / AUTOMATIC quotations, their events, Sales contracts and payments stay as they are and remain readable. Open legacy
items continue as follows: an ISSUED legacy quotation can still be accepted (contract draft created automatically); a REVISION_REQUESTED or
REJECTED one is answered by the technician's new version; a legacy DRAFT (never visible to the customer) is closed as CANCELLED with
reason `LEGACY_DRAFT_REPLACED` when the technician starts the cart; an ACCEPTED quotation without contract gets one through
`POST /api/quotations/{id}/contracts` (Manager/Admin); contracts in any state continue with Manager/Admin.

Checks before applying (read-only SQL, run by the operator):

```sql
SELECT count(*) FROM payment_attempt WHERE provider <> 'PAYOS';          -- expected 0
SELECT status, origin, count(*) FROM quotation GROUP BY 1, 2;            -- legacy drafts that will be replaced
SELECT q.id FROM quotation q WHERE q.status = 'ACCEPTED'
  AND NOT EXISTS (SELECT 1 FROM customer_contract c WHERE c.quotation_id = q.id);   -- need a manual contract
```

### Rollback

* Code rollback: redeploy the previous build. The previous build cannot read rows with a NULL `sales_user_id` or CASH attempts correctly,
  so roll back the code **only while no technician quotation, automatic contract draft or cash payment exists**, or first decide how those
  rows are handled.
* Schema rollback (`Down`): starts with a guard that **raises and changes nothing** while data the old schema cannot hold exists:
  TECHNICIAN quotations, CASH attempts (or cash confirmation columns filled), PAYMENT_EVIDENCE files, or quotations / contracts without a
  Sales owner whose survey request has no `assigned_sale_id` either (tested on PostgreSQL). Otherwise it fills the missing
  `sales_user_id` of quotations and contracts from `survey_request.assigned_sale_id` (the value the previous build expects; contracts
  created after this release by Manager/Admin for Sales-assigned requests), then restores the previous schema (tested: catalog
  identical, Up runs again, the older migration down/up suites pass over data created by the new code). Export blocking rows before
  deciding anything; the migration never deletes rows.

## Open decisions (not assumed)

* Discount policy, tax policy and refund policy: none was invented. Tax uses the existing configured default; discounts do not exist;
  refunds stay a manual Manager process on the refund record.
* Partial or over-payment in cash: refused (exact total only), because no partial-payment policy exists.
* Whether Sales should keep closing survey requests and whether Flow 1 assignment should move entirely to Managers: Flow 1 was not changed.
* Removal of the retired handler code and the legacy tests that exercise it: proposed for a later clean-up once the FE no longer calls
  the retired routes.
* Validity of a quotation and its terms: reuse `Quotation:AutomaticValidityDays` and `Quotation:AutomaticTerms`; the business may want
  dedicated settings.
* Whether a technician quotation must contain at least a panel and an inverter (the automatic generator required it): not enforced now.
