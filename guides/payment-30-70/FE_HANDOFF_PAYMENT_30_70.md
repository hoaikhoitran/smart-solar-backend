# FE handoff — 30/70 payment, paper contract, customer acceptance (2026-11-19)

Envelope unchanged: `{ isSuccess, data, error: { code, message, details, fieldErrors }, traceId }`. Status mapping unchanged (`*_NOT_FOUND` → 404,
validation → 400, state / duplicates / stale → 409, retired → 410 `ENDPOINT_RETIRED`). Money = numbers in whole VND. Times = ISO-8601 with offset
(UTC from contract / payment APIs; installation views keep their local +07:00 times).

## 1. Technician app
### 1.1 Agreement → contract
`POST /api/technician/quotations/{quotationId}/on-site-decision` `{ decision: "AGREED" | "DECLINED" | "REVISION_NEEDED", revision, note? }`
* AGREED response (QuotationView) now has **`contractId`** (the 30/70 contract, DRAFT). A retried AGREED returns the same `contractId`.
* DECLINED / REVISION_NEEDED before the deposit discards that contract. While a deposit payment is open: 409 `QUOTATION_PAYMENT_STARTED`
  (cancel the payment first); after the deposit was verified: 409 too.
* 409 `CONTRACT_WARRANTY_REQUIRED` (400) when no default warranty option exists (Admin setup).
* `POST /api/technician/quotations/{id}/payment` → **410** (retired). GET / cancel stay for payments opened before the release.

### 1.2 Installment payment (sequence 1 = deposit 30%, 2 = balance 70%)
| Method | Route | Body |
|---|---|---|
| open / reopen | `POST /api/technician/contracts/{contractId}/installments/{sequence}/payment` | `{ method: "ONLINE" \| "CASH" }` |
| state (reconciles an open QR with PayOS) | `GET  /api/technician/contracts/{contractId}/installments/{sequence}/payment` | — |
| cancel | `POST /api/technician/contracts/{contractId}/installments/{sequence}/payment/cancel` | — |

Response `InstallmentPaymentView`:
```json
{ "contractId": "…", "contractNumber": "HD-20261119-ABC123", "installmentId": "…", "sequence": 1, "kind": "DEPOSIT", "amount": 5550000,
  "currency": "VND", "installmentStatus": "DUE", "dueAt": null, "overdue": false, "paidAt": null, "paidLate": false,
  "paymentState": "ONLINE_IN_PROGRESS",
  "current": { "paymentAttemptId": "…", "orderCode": 1763540000123, "amount": 5550000, "status": "PENDING", "checkoutUrl": "https://…",
               "qrCode": "000201…", "expiresAt": "…", "method": "ONLINE", … },
  "attempts": [ … ] }
```
`paymentState`: `NOT_PAYABLE` | `AWAITING_PAYMENT` | `ONLINE_IN_PROGRESS` | `AWAITING_CASH_CONFIRMATION` | `PAID`.
Errors: 409 `CONTRACT_INSTALLMENT_NOT_PAYABLE` (e.g. balance before acceptance, deposit after signature), 409 `CONTRACT_INSTALLMENT_ALREADY_PAID`,
409 `PAYMENT_CASH_SELECTED` / `PAYMENT_CHECKOUT_IN_PROGRESS` (other method open), 404 `CONTRACT_INSTALLMENT_NOT_FOUND`, 404 `CONTRACT_NOT_FOUND`
(not the assigned technician), 409 `CONTRACT_PAYMENT_PLAN_NOT_APPLICABLE` (legacy contract), 400 `VALIDATION_FAILED`.
Poll GET every 3–5 s while `ONLINE_IN_PROGRESS`. Cash: show "waiting for Manager confirmation"; the technician never confirms.

### 1.3 Signed paper pages (after the deposit is PAID)
1. Upload each photo with the existing file flow (`POST /api/files/upload-url` → PUT to S3 → `POST /api/files`), purpose **`SIGNED_CONTRACT_PAGE`**
   (JPEG / PNG / WebP, PDF if enabled; ≤ 20 MB).
2. `POST /api/technician/contracts/{contractId}/signature-set` `{ expectedPageCount: 1..50, signedOn?: "2026-11-19", note? }`
   (same count again = same set; a different count restarts the unfinished upload).
3. `PUT /api/technician/contracts/{contractId}/signature-set/pages/{pageNumber}` `{ fileId }` (replace allowed until finalized).
4. `POST /api/technician/contracts/{contractId}/signature-set/finalize` `{ expectedRevision? }`.

Response `SignatureSetView`: `{ signatureSetId, contractId, version, status: UPLOADING|FINALIZED|SUPERSEDED, expectedPageCount, uploadedPageCount,
missingPages: [2], signedOn, note, createdByUserId, createdAt, finalizedByUserId, finalizedAt, revision, pages: [{ pageNumber, fileId, sha256,
contentType, uploadedByUserId, uploadedAt, replacedAt }] }`.
Errors: 409 `CONTRACT_DEPOSIT_REQUIRED`, 409 `CONTRACT_SIGNATURE_NOT_STARTED`, 400 `CONTRACT_SIGNED_PAGE_OUT_OF_RANGE`,
409 `CONTRACT_SIGNED_PAGES_INCOMPLETE` (`details.missingPages`, `expected`, `actual`), 409 `CONTRACT_SIGNATURE_ALREADY_FINALIZED`,
409 `FILE_ALREADY_LINKED`, 403 `FILE_NOT_OWNED`, 400 `FILE_WRONG_PURPOSE`, 409 `FILE_NOT_VERIFIED`, 410 `FILE_OBJECT_MISSING`.

### 1.4 Schedule and installation
Recording the agreed date (`POST /api/technicians/me/contracts/{contractId}/installation-schedule`) is allowed from the verified deposit on;
`awaitingContractSignature = true` until the pages are finalized, then the schedule is confirmed automatically. Completion report unchanged.

## 2. Customer app
| Feature | Route |
|---|---|
| My contracts (all sites / projects / versions; 30/70 DRAFT visible for preview) | `GET /api/customers/me/contracts`, `GET …/{contractId}` |
| Pay an installment (QR or cash choice) | `POST /api/customers/me/contracts/{contractId}/installments/{sequence}/payment` `{ method }` |
| Payment state / cancel | `GET …/installments/{sequence}/payment`, `POST …/payment/cancel` |
| Signed pages | `GET /api/customers/me/contracts/{contractId}/signature-sets`, `GET …/signed-pages/{fileId}/url?disposition=inline\|attachment` |
| **Acceptance (restored)** | `POST /api/customers/me/installation-projects/{projectId}/acceptance` `{ decision: "ACCEPT" \| "REQUEST_REWORK" \| "REJECT", reason?, evidenceFileIds?: [], expectedRevision? }` |

* Rework / reject needs `reason`; evidence = files of purpose **`ACCEPTANCE_EVIDENCE`** uploaded by the customer (≤ 10).
* ACCEPT errors: 409 `INSTALLATION_INVALID_STATE` (not pending / already decided), 409 `INSTALLATION_CHECKLIST_INCOMPLETE`,
  409 `INSTALLATION_SUBSTITUTION_APPROVAL_REQUIRED` (SmartSolar must finish something first), 409 `INSTALLATION_STALE_REVISION`.
* Retired: `POST /api/customers/me/contracts/{id}/signed-document` → 410.
* Legacy contract payment routes answer 409 `PAYMENT_CONTRACT_HAS_INSTALLMENTS` for 30/70 contracts.
* New notifications: `CONTRACT_READY_FOR_PREVIEW`, `CONTRACT_DEPOSIT_RECEIVED`, `CONTRACT_PAPER_SIGNED`, `INSTALLATION_ACCEPTANCE_REQUESTED`,
  `CONTRACT_BALANCE_DUE`, `CONTRACT_BALANCE_OVERDUE`, `CONTRACT_FULLY_PAID`.

## 3. ContractView additions
`ContractView.paymentPlan` (null for legacy contracts):
```json
{ "plan": "DEPOSIT_30_BALANCE_70", "termsVersion": "PAYMENT_30_70_DRAFT_PENDING_LEGAL_REVIEW",
  "stage": "DRAFT_CREATED | DEPOSIT_VERIFIED | READY_FOR_INSTALLATION | BALANCE_DUE | BALANCE_OVERDUE | FULLY_PAID | DISCARDED",
  "total": 18500000, "paid": 5550000, "outstanding": 12950000, "depositPaid": true, "paperSigned": false, "readyForInstallation": false,
  "fullyPaid": false, "overdue": false, "nextDueAt": null, "ownershipStatus": "OWNERSHIP_RETAINED | OWNERSHIP_TRANSFER_ELIGIBLE | NOT_APPLICABLE",
  "termsPendingLegalReview": true, "legalReviewRequired": false,
  "installments": [ { "installmentId": "…", "sequence": 1, "kind": "DEPOSIT", "percent": 30, "amount": 5550000, "status": "PAID", "openedAt": "…",
                      "dueAt": null, "overdue": false, "paidAt": "…", "paidAmount": 5550000, "paidLate": false, "settledPaymentAttemptId": "…" }, … ],
  "signature": { …SignatureSetView… }, "collection": [ … staff only … ] }
```
`paymentCompleted` = fully paid only (never true after the deposit alone). Show "Signed" and "Fully paid" separately.

## 4. Staff web (Sales / Technician / Manager / Admin)
* `GET /api/contracts?page&pageSize&customerId&status&paymentPlan=DEPOSIT_30_BALANCE_70|LEGACY&surveyRequestId&propertySiteId&quotationId&projectId&search&overdue`
  → `PagedResult<ContractDirectoryItem>` (`contractNumber, status, paymentPlan, stage, customerName, customerPhone, propertyName, projectId,
  projectStatus, totalAmount, paid, outstanding, depositPaid, fullyPaid, overdue, nextDueAt, ownershipStatus, createdAt`).
* `GET /api/contracts/{id}`, `GET /api/contracts/{id}/signature-sets`, `GET /api/contracts/{id}/signed-pages/{fileId}/url`,
  `GET /api/contracts/{id}/installments/{n}/payment` (read-only).
* Manager/Admin support upload: `POST /api/contracts/{id}/signature-set`, `PUT …/pages/{n}`, `POST …/finalize`.
* Cash confirmation unchanged: `POST /api/manager/payments/{attemptId}/cash/confirm` `{ amount, receiptReference, evidenceFileId, note? }`
  (amount = the installment amount). Payment list `GET /api/manager/payments` shows `lines[].itemType = CONTRACT_INSTALLMENT`, `contractInstallmentId`.
* Manager internal acceptance `POST /api/installation-projects/{id}/acceptance` → 410; keep "Return to rework".

### Collection (Sales / Manager / Admin)
* `GET /api/collections?page&pageSize&status&openOnly=true&customerId&contractId` → `PagedResult<CollectionCaseView>`
  (`caseId, contractId, contractNumber, installmentSequence, customerName, customerPhone, status, openedAt, dueAt, daysOverdue, amountDue,
  installmentStatus, promisedPaymentDate, resolvedAt, resolution, revision`).
* `GET /api/collections/{caseId}` (+ `activities[]`).
* `POST /api/collections/{caseId}/activities` `{ kind: CONTACT|NEGOTIATION|AGREEMENT|NOTE, channel?: PHONE|VISIT|ZALO|EMAIL|SMS|OTHER, summary,
  promisedPaymentDate? (AGREEMENT), evidenceFileIds? (COLLECTION_EVIDENCE), expectedRevision? }`
* `POST …/escalate` `{ reason, expectedRevision? }` (Sales, Manager, Admin); `POST …/legal-review`, `POST …/resolve` (Manager, Admin; Sales → 403).
* No refund / repossession buttons exist.

### Admin finance
`GET /api/admin/finance/overview?from=2026-11-01&to=2026-11-30&zoneId=` → `FinanceOverviewView` (buckets `{ key, count, amount }`; field list in
FINANCIAL_RULES.md §7).
