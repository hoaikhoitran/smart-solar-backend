# Financial rules — 30/70 installation payment plan (2026-11-19)

Applies to installation contracts created at the on-site agreement from 2026-11-19 on (`customer_contract.payment_plan = 'DEPOSIT_30_BALANCE_70'`).
Legacy contracts (`payment_plan IS NULL`) keep the 100% rule unchanged. After-sales (packages, repair / maintenance quotations) stays 100% upfront.

Code: `src/SmartSolar.Modules/CustomerContracts/PaymentPlanCalculator.cs` (pure, unit-tested), `PaymentPlanLedger.cs` (state changes),
`FinanceOverview.cs` + `Infrastructure/.../FinanceOverviewQueries.cs` (reporting).

## 1. Amounts
| Item | Rule |
|---|---|
| Money type | `decimal` in code, `numeric(18,2)` in the database; whole VND only (no float, no `bool isPaid`) |
| Total `T` | the agreed quotation total (frozen in the contract snapshot) |
| Deposit (installment 1) | `round(T × 30%, 0, MidpointRounding.AwayFromZero)` — e.g. 1,000,005 → 300,002 |
| Balance (installment 2) | `T − deposit` (so deposit + balance = T exactly, never a 1-VND gap) |
| Minimum | `T ≥ 2` and both amounts > 0, otherwise the agreement is refused (`CONTRACT_PAYMENT_PLAN_AMOUNT_NOT_SUPPORTED`, 400) |
| Paid | Σ amounts of installments in status `PAID` (applied money only) |
| Outstanding | `max(0, T − paid)`; includes a balance that is not due yet (`PENDING`) |

Property test: 20,000 random totals up to 50 bn VND keep `deposit + balance = T`, both positive, deposit within ±0.5 VND of 30%.

## 2. Installment lifecycle (separate from contract / installation / collection)
| Status | Meaning | Set by |
|---|---|---|
| `DUE` | payable now | deposit: at the agreement; balance: at the customer's ACCEPT |
| `PENDING` | not payable yet | balance until acceptance |
| `PAID` | settled by exactly one verified payment line of its exact amount | payment settlement (webhook, reconciliation, Manager cash) |
| `CANCELLED` | contract withdrawn before any payment | technician DECLINED / REVISION_NEEDED before the deposit |

Overdue is **derived**, never stored: `status = DUE AND now > due_at` (strictly after; exactly at `due_at` is still on time).

## 3. Deadline of the balance
* `due_at = accepted_at (UTC) + PaymentPlan:BalanceDueHours` — default **72 h** (= at most 3 days), values above 72 are capped, startup refuses
  values outside 1–72.
* Stored in UTC (`timestamptz`); shown in the business time zone **Asia/Ho_Chi_Minh** (UTC+7, no DST). Example: accepted 19/11 23:30 local →
  due 22/11 23:30 local = 22/11 16:30 UTC (unit-tested).
* The deadline is set once (a repeated acceptance never resets it) and is never changed by a collection agreement.

## 4. What counts as payment
* Only: a verified PayOS webhook / server-to-server status query whose amount equals the attempt amount, or a Manager/Admin cash confirmation
  (exact amount, unique receipt reference, VERIFIED `PAYMENT_EVIDENCE` file registered by the confirming staff member).
* Technician and Sales never mark money as received; a browser return URL never marks anything paid.
* A cash choice (`PENDING` CASH attempt) is not money.
* Each installment payment is its own PayOS attempt with one `CONTRACT_INSTALLMENT` line (`payment_attempt.contract_id` stays NULL, so legacy
  "contract paid" logic never sees it). One live attempt per installment (contract row lock + check in the creating transaction).

## 5. Money that cannot be applied
| Case | Result |
|---|---|
| Wrong amount (webhook amount ≠ attempt amount) | attempt `REVIEW_REQUIRED`; installment unchanged |
| Second payment of an already paid installment (e.g. QR paid after cash was confirmed) | line `REVIEW_REQUIRED` ("already paid"); unapplied-money record (after-sales refund ledger) |
| Payment of a cancelled installment / discarded contract | line `REVIEW_REQUIRED`; unapplied record |
| Payment after the contract moved on (e.g. deposit when the contract is already signed) | line `REVIEW_REQUIRED` |
| Late balance (after `due_at`) | **applied** and flagged `paid_late = true` (judged on the provider transaction time / cash confirmation time) |

No refund is executed by the system and there is no refund button; the existing manual refund ledger (`REFUND_REQUIRED`, refund records)
is the only path. Database guard: unique settled line per installment (`ux_payment_attempt_item_settled_contract_installment`).

## 6. Contract state vs money
| Contract | Meaning | Money |
|---|---|---|
| `DRAFT` (30/70) | created at agreement, previewable | deposit due |
| `AWAITING_SIGNATURE` | deposit verified, paper may be signed | 30% applied |
| `SIGNED` | paper pages finalized: **ready for installation** | 30% (+ balance due after acceptance) |
| `PAID` | **both** installments paid (= fully paid) | 100% |
| `DISCARDED` | withdrawn before the deposit | none |

`SIGNED ≠ FULLY_PAID`. `ContractView.paymentCompleted` is true only for a fully paid contract (never for the deposit alone).
Database triggers enforce the order (`DRAFT → AWAITING_SIGNATURE` only with the deposit PAID, `→ SIGNED` only with a finalized page set,
`→ PAID` only with every installment PAID, no 100% contract attempt for a 30/70 contract, installment PAID only with its settled line).

## 7. Finance overview (`GET /api/admin/finance/overview?from&to&zoneId`, Admin)
* Snapshot (now): contract value (30/70 vs legacy), deposits paid / due, balances pending / due / overdue / paid (+ paid late), outstanding,
  outstanding due now, awaiting acceptance, accepted-unpaid, fully paid, contracts by stage, open collection cases by status, negotiations,
  escalations (incl. legal review).
* Period `[from, to]` (local dates → UTC range, ≤ 366 days, default this month): deposits collected, balances collected, installments
  collected by method (ONLINE vs CASH), legacy contract payments collected, unapplied, refunded.
* No double counting: a combined after-sales checkout is split by its lines (existing rule); each installment has at most one settled line;
  `PENDING`, `REVIEW_REQUIRED` and `REFUND_REQUIRED` money is never "collected". The existing after-sales report counts settled
  `CONTRACT_INSTALLMENT` lines as contract revenue (once) and follows them to the installation for the zone filter.
* Zone = the installation project's operational zone (contracts without a project are excluded when a zone is given).

## 8. Collection (overdue)
Overdue sweep (every 60 s with the installation schedule worker) opens one case per overdue installment (`SALES_CONTACT_REQUIRED`). Sales
records contacts / negotiations / agreements (promised date) / notes; Sales, Manager or Admin escalate; Manager / Admin request legal review
or resolve. None of these changes money, the due date or ownership; paying the installment resolves the case automatically (`PAID`).
