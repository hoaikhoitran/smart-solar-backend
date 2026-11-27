# FE handoff — Unified technician cash receipts (installation + after-sales, backend 2026-11-27)

The technician cash receipt flow now works the same way for:

- **installation 30/70 installments** (released in `147c66e`, see `guides/technician-cash-receipt/FE_HANDOFF_TECHNICIAN_CASH_RECEIPT.md`);
- **every chargeable after-sales quotation**: paid repairs, warranty repairs with a customer-payable part, one-time maintenance, the
  customer-payable part of disaster-protection repairs, repairs after a rejected disaster claim, supplementary quotations, and fault work found
  during a package maintenance visit (the separate FAULT request opened from that visit).

There is one Manager/Admin review queue for both, and the payment attempt is the common identity. Amounts always come from the backend:

| Context | Amount due |
| --- | --- |
| Installation | 30 % deposit, then 70 % balance. Unchanged. |
| After-sales | 100 % of the quotation's customer amount. Unchanged. |
| Fully covered work (customer amount 0) | Never a payment, never a receipt. |

Standalone package purchases are **out of scope**: unchanged, and receipts are not accepted for them.

Every response keeps the envelope `{ isSuccess, traceId, data, error: { code, message, details } }`.

## 1. Business workflows

### After-sales quotation
1. The technician prices the quotation and shows it on the device. The customer agrees, and the technician records it:
   `POST /api/technicians/me/after-sales/quotations/{quotationId}/on-site-decision`. Unchanged.
   - Customer amount 0 → the work is authorized at once. No payment, no receipt.
2. The customer chooses how to pay: `POST /api/technicians/me/after-sales/quotations/{quotationId}/payment` with `{ "method": "ONLINE" | "CASH" }`.
3. **ONLINE**: PayOS QR / checkout. Unchanged.
   - Only the verified webhook or the server-to-server reconciliation marks it paid. The browser return URL never does.
4. **CASH**:
   1. The technician takes the cash, writes a paper receipt and photographs it.
   2. Upload the photo as `PAYMENT_EVIDENCE` (§4).
   3. `POST …/payment/cash-receipt` (§2.1). The receipt becomes `SUBMITTED`; the payment stays `PENDING` and the quotation stays `ACCEPTED`.
   4. A Manager/Admin reviews it in the shared queue (§3):
      - **Accept**: the payment becomes PAID exactly once. The existing after-sales settlement marks the quotation `PAID`, and the request moves
        on as after a verified PayOS payment (usually `READY_FOR_WORK`).
      - **Reject** (reason required): same payment, still unpaid. The technician sends a corrected receipt. No new payment is created, and the
        customer is never asked to pay again.

### Installation installment
Unchanged (see the previous handoff). The installment payment view and the queue rows now also carry `kind` / `context` (§5).

## 2. Technician endpoints (role `TECHNICIAN`)

Only the technician with a live (not declined / cancelled) visit on the request can use these. Anyone else gets 404 `SERVICE_QUOTATION_NOT_FOUND`.

### 2.1 Send a receipt — NEW
`POST /api/technicians/me/after-sales/quotations/{quotationId}/payment/cash-receipt`

```json
{ "paymentAttemptId": "8d1f…", "receiptNumber": "PT-000321", "evidenceFileId": "a3c9…",
  "collectedAt": "2026-11-27T10:15:00+07:00", "note": "Khách trả đủ" }
```

| Field | Rule |
| --- | --- |
| `paymentAttemptId` | Required. The quotation's open CASH payment: `data.current.paymentAttemptId` of the payment view. |
| `receiptNumber` | Required. Trimmed, 1–100 characters. Unique across all payments (installation and after-sales). The same number may be resent for the same payment after a rejection. |
| `evidenceFileId` | Required. A VERIFIED `PAYMENT_EVIDENCE` file registered by this technician, not the evidence of another payment. |
| `collectedAt` | Optional; defaults to now. At most 5 minutes in the future, and not before cash was chosen. It is the time used for the expiry check (§6). |
| `note` | Optional, at most 500 characters. |

Response 200 — `ServiceQuotationPaymentView`:

```json
{ "isSuccess": true, "traceId": "…", "data": {
  "quotationId": "…", "version": 1, "quotationStatus": "ACCEPTED", "customerAmount": 450000, "nothingToPay": false,
  "current": { "paymentAttemptId": "8d1f…", "method": "CASH", "status": "PENDING", "amount": 450000, "providerReference": null, "paidAt": null, … },
  "currentCashReceipt": { "cashReceiptId": "…", "paymentAttemptId": "8d1f…", "kind": "SERVICE_QUOTATION", "serviceQuotationId": "…",
    "serviceRequestId": "…", "contractId": null, "contractInstallmentId": null, "amount": 450000, "receiptNumber": "PT-000321",
    "collectedByUserId": "…", "collectedAt": "2026-11-27T03:15:00+00:00", "evidenceFileId": "a3c9…", "note": "Khách trả đủ",
    "status": "SUBMITTED", "submittedAt": "…", "reviewedByUserId": null, "reviewedAt": null, "reviewReason": null, "revision": 1 },
  "cashReceipts": [ { …same shape, oldest first… } ] } }
```

### 2.2 Photo of a sent receipt — NEW
`GET /api/technicians/me/after-sales/quotations/{quotationId}/payment/cash-receipts/{cashReceiptId}/evidence/url?disposition=inline|attachment`

Returns 200 `AttachedFileUrlView` `{ id, url, expiresAt, disposition, contentType, fileName }`, sent with `Cache-Control: no-store`. The URL is
short-lived: do not store it.

### 2.3 Read / cancel (existing, extended)
- `GET …/payment` now includes `currentCashReceipt` and `cashReceipts`. `currentCashReceipt` is the `SUBMITTED` one, otherwise the `ACCEPTED` one,
  otherwise the latest (for example `REJECTED` with its `reviewReason`).
- `POST …/payment/cancel`, and switching to ONLINE, are refused while a receipt is `SUBMITTED` (§7).

Installation equivalents (unchanged routes):
- `POST /api/technician/contracts/{contractId}/installments/{sequence}/payment/cash-receipt`
- `GET /api/technician/contracts/{contractId}/installments/{sequence}/payment/cash-receipts/{cashReceiptId}/evidence/url`

## 3. Manager / Admin review queue (roles `MANAGER`, `ADMIN`) — shared

| Method & route | Purpose |
| --- | --- |
| `GET /api/manager/payments/cash-receipts?status=SUBMITTED&kind=SERVICE_QUOTATION\|INSTALLMENT&page=1&pageSize=20` | Queue, newest first. `kind` is NEW and optional. |
| `GET /api/manager/payments/cash-receipts/{cashReceiptId}` | Detail: `current`, `history` (every receipt of the same payment, oldest first) and `lines` (NEW) — the payment's lines with their status. |
| `GET /api/manager/payments/cash-receipts/{cashReceiptId}/evidence/url?disposition=inline` | The technician's photo, for both kinds. No re-upload. |
| `POST /api/manager/payments/cash-receipts/{cashReceiptId}/accept` with `{ "revision": 1, "note": "…" }` | Accept. Returns `PaymentAttemptView` (staff fields). |
| `POST /api/manager/payments/cash-receipts/{cashReceiptId}/reject` with `{ "revision": 1, "reason": "…" }` | Reject. `reason` is required. Returns `CashReceiptView`. |

Queue row (`StaffCashReceiptView`), after-sales example:

```json
{ "receipt": { …CashReceiptView… }, "context": "AFTER_SALES_QUOTATION", "contractNumber": "", "installmentSequence": null, "installmentKind": null,
  "serviceRequestId": "…", "serviceRequestNumber": "SR-2026-000123", "serviceRequestKind": "FAULT", "serviceQuotationId": "…", "quotationVersion": 1,
  "customerUserId": "…", "customerName": "Nguyễn Văn An", "collectedByName": "KTV Bình", "paymentStatus": "PENDING" }
```

- `context` is one of `INSTALLATION_DEPOSIT`, `INSTALLATION_BALANCE` or `AFTER_SALES_QUOTATION`.
- `serviceRequestKind` is one of `FAULT`, `ONE_TIME_MAINTENANCE` or `MAINTENANCE`.
- The amount is `receipt.amount`; the collector is `collectedByName`, the technician who took the cash.
- Detail `lines[]` is `{ itemType: "REPAIR_QUOTATION" | "MAINTENANCE_QUOTATION" | "CONTRACT_INSTALLMENT", amount, status, quotationId, contractInstallmentId, … }`.
  After an acceptance, a line with `status = "REVIEW_REQUIRED"` means the money was recorded but could not be applied (§6). Show a warning and
  link to the unapplied-money / refund queue.

## 4. Camera and upload

1. `POST /api/files/presigned-upload` with `{ purpose: "PAYMENT_EVIDENCE", fileName, contentType, sizeBytes }`.
   - Allowed types: `image/jpeg`, `image/png`, `image/webp` and `application/pdf`.
   - Maximum size: **15 MB**. Unchanged.
   - Technician is allowed.
2. `PUT` the bytes to the returned URL, without a Bearer token.
3. `POST /api/files` to register the file; the response is `status = "VERIFIED"`. Use `fileId` as `evidenceFileId`.

On the device:
- **Chụp phiếu thu** opens `<input type="file" accept="image/*" capture="environment">`. `getUserMedia` is optional.
- Show a preview and a **Chụp lại** (retake) button. Check the type and size on the client before uploading.
- A **Số phiếu thu** field (required) and an optional **Thời điểm thu**.
- **Gửi phiếu thu cho quản lý** uploads the photo and then calls §2.1.

## 5. DTO changes

All changes are additive, except that some fields of the new installment receipt DTOs became nullable. Those DTOs were never released.

| DTO | Change |
| --- | --- |
| `CashReceiptView` | + `kind` (`INSTALLMENT` / `SERVICE_QUOTATION`), + `serviceQuotationId`, + `serviceRequestId`. `contractId` / `contractInstallmentId` are now nullable (null for after-sales). |
| `StaffCashReceiptView` | + `context`, `serviceRequestId`, `serviceRequestNumber`, `serviceRequestKind`, `serviceQuotationId`, `quotationVersion`. `installmentSequence` / `installmentKind` are now nullable. |
| `StaffCashReceiptDetail` | + `lines`. |
| `ServiceQuotationPaymentView` (technician after-sales payment) | + `currentCashReceipt`, + `cashReceipts`. |
| `InstallmentPaymentView` | Unchanged since `147c66e`. |

The customer views are unchanged:
- `GET /api/customers/me/after-sales/payments/{attemptId}` shows `status` / `paidAt` / `amountPaid` / `providerReference`.
- `evidenceFileId`, `confirmedByUserId` and receipt review data are never returned to the customer.

## 6. Expiry before / after the reported collection

A quotation's validity is not extended by a cash choice. What changed is what happens once the technician has reported the cash as taken.

| Situation | Behaviour |
| --- | --- |
| No receipt sent, quotation expires | Unchanged: the sweep withdraws the cash choice and expires the quotation. |
| Receipt `SUBMITTED`, quotation expires before the review | The sweep **holds** the quotation. Accepting later pays it when `collectedAt` ≤ the expiry (judged at the collection time, like a PayOS transaction time). |
| Receipt `REJECTED` and `collectedAt` ≤ the expiry | Held. The technician sends a corrected receipt for the same payment. |
| `collectedAt` > the expiry, or the quotation can no longer take the money | Accepting records the money: the payment is `PAID` with the receipt. The quotation is **not** paid. The line becomes `REVIEW_REQUIRED`, with a refund request `PENDING_APPROVAL` in the existing unapplied-money ledger (`GET /api/manager/after-sales/unapplied-money`) and the event `CASH_RECEIPT_UNAPPLIED`. Nothing is lost; a Manager decides. |

The installment rules are unchanged. An installment that can no longer take the money still refuses the acceptance (409 `PAYMENT_CASH_ITEM_NOT_PAYABLE`).

## 7. Guards while a receipt is `SUBMITTED`

All of the following answer **409 `PAYMENT_CASH_RECEIPT_UNDER_REVIEW`**:
- the technician cancels the cash payment;
- the customer cancels the cash choice;
- the technician records DECLINED;
- the customer or a Manager cancels or closes the request (cancel, stop work, duplicate, cannot fulfil);
- a Manager confirms the cash directly;
- a background expiry runs (it simply skips).

These answer **409 `PAYMENT_CASH_SELECTED`**:
- switching to ONLINE;
- a customer checkout.

Replacing the quotation answers **409 `PAYMENT_CHECKOUT_IN_PROGRESS`** (open cash payment), backed by the receipt guard.

After a rejection, these explicit human actions work again. The rejected receipt stays in the history.

## 8. RBAC

| Role | Can |
| --- | --- |
| Technician | Send and resend receipts for quotations of requests they have a live visit on. Read their history and photos. Never accept, reject or mark paid (403 on the manager routes). |
| Manager / Admin | Queue, detail, photo, accept, reject (revision-checked). Existing direct cash confirmation, with their own evidence, when no receipt is under review. |
| Customer | Payment status only. 403 on technician and manager routes. |
| Sales | No cash authority: 403 on the queue, accept and reject. |

Legacy / customer-initiated cash keeps the Manager's direct confirmation: the customer chooses cash in the app and pays at the office, or a
legacy full-payment contract. Making a technician receipt mandatory for every cash payment would break those paths, which have no technician. That
was **not** done; see the open decisions.

## 9. Error codes

| HTTP | Code | When |
| --- | --- | --- |
| 400 | `VALIDATION_FAILED` | `details.paymentAttemptId` / `receiptNumber` / `evidenceFileId` / `collectedAt` / `note` / `reason` / `revision` / `kind` / `status`. |
| 400 | `FILE_TYPE_NOT_ALLOWED`, `FILE_TOO_LARGE`, `FILE_WRONG_PURPOSE` | Upload or photo refused. |
| 403 | `FILE_NOT_OWNED` | The photo was registered by another user. |
| 404 | `SERVICE_QUOTATION_NOT_FOUND` | Unknown quotation, or not the technician's. |
| 404 | `PAYMENT_CASH_RECEIPT_NOT_FOUND` | Unknown receipt, or it belongs to another quotation. |
| 404 | `FILE_NOT_FOUND` | Unknown file. |
| 409 | `FILE_NOT_VERIFIED` | The upload failed or is not verified yet. |
| 409 | `PAYMENT_CASH_NOT_SELECTED` | No open CASH payment (ONLINE, cancelled, or none). |
| 409 | `AFTER_SALES_INVALID_STATE` | Nothing is due (customer amount 0). |
| 409 | `PAYMENT_ALREADY_PAID` | Already paid. |
| 409 | `PAYMENT_CASH_RECEIPT_ATTEMPT_MISMATCH` | `paymentAttemptId` is not the open payment: reload. |
| 409 | `PAYMENT_CASH_RECEIPT_CONTEXT_MISMATCH` | The payment's lines do not pay this quotation / installment. |
| 409 | `PAYMENT_CASH_RECEIPT_ALREADY_SUBMITTED` | A receipt is already under review. |
| 409 | `PAYMENT_CASH_RECEIPT_DUPLICATE` | The receipt number is used by another payment. |
| 409 | `PAYMENT_CASH_RECEIPT_EVIDENCE_REUSED` | The photo is already the evidence of another payment. |
| 409 | `PAYMENT_CASH_RECEIPT_UNDER_REVIEW` | See §7. |
| 409 | `PAYMENT_CASH_RECEIPT_NOT_REVIEWABLE` | The receipt was already accepted or rejected. |
| 409 | `PAYMENT_CASH_RECEIPT_STALE` | Revision changed: reload. |
| 409 | `PAYMENT_CASH_SELECTED`, `PAYMENT_CHECKOUT_IN_PROGRESS` | Method switch / replacement while cash is open. |
| 401 / 403 / 429 | — | Not signed in / wrong role / rate limit. The send uses `payment-create`. |

## 10. Screens

- **Technician — after-sales payment**: the same component pattern as the installment block.
  - Show **Chụp phiếu thu** when `current.method = CASH`, `current.status = PENDING` and there is no `SUBMITTED` receipt.
  - While `SUBMITTED`, show the "Chờ quản lý kiểm tra" badge. Hide **Hủy tiền mặt** and **Đổi sang PayOS**. Poll `GET …/payment` every 10–20 s.
  - When `REJECTED`, show `reviewReason` and a **Gửi lại phiếu thu** button.
  - When `nothingToPay` is true, show no payment UI at all.
- **Manager — "Phiếu thu tiền mặt" queue**:
  - Tabs or a filter on `kind`. Each row shows its `context` label: "Cọc 30%" / "Thanh toán 70%" / "Hậu mãi — {serviceRequestNumber} v{quotationVersion}". Also show the amount, the customer, the collector, `collectedAt` and the receipt number.
  - The detail screen shows the photo, the history and the lines (REVIEW_REQUIRED warning), with **Xác nhận** / **Yêu cầu bổ sung** buttons (reason required).
- **Customer**: the payment status only — "Đang chờ xác nhận tiền mặt" / "Đã thanh toán". No receipt details.

UI locks (FE `ui-locks.txt`, owner `huufuoc`):
- `src/features/contracts/CashConfirmDialog.tsx` and `src/features/payment-plan/*` are locked.
- The after-sales files (`RepairQuotationDialog.tsx`, `technicianAfterSalesService.ts`, `technicianVisit*.ts`) are not locked, as checked on the
  local FE worktree at `2fb3221`.
- Build the receipt capture and the queue as new components. Do not regenerate the whole API client.

## 11. Migration and deployment

| Migration | Change | Data impact |
| --- | --- | --- |
| `20261127021802_TechnicianCashReceipts` (from `147c66e`) | Creates `cash_receipt_submission` | None |
| `20261127124918_UnifiedCashReceipts` (NEW) | `contract_id` / `contract_installment_id` become nullable. Adds `kind` (default `INSTALLMENT`), `service_quotation_id`, `service_request_id`, two indexes, two FKs and `ck_cash_receipt_submission_context` | Existing rows become `INSTALLMENT` automatically. No row is rewritten. |

Deployment:
1. Check `__EFMigrationsHistory` on the target. Supabase was at `20261125104043_SchedulingOvertimeConflicts` on 2026-11-25, so both migrations above
   are expected to be pending.
2. Back up and test the restore.
3. Apply in order with the idempotent script (`dotnet ef migrations script <last applied> --idempotent`). This takes short locks on
   `cash_receipt_submission` only.
4. Deploy the API image.
5. Check `/health/ready`.

An older API image keeps working after the migration: the new columns are nullable or defaulted, and the schema gate only checks migrations the
image knows. `Down` of the new migration is refused once after-sales receipts exist; restore from backup instead. Not applied automatically
anywhere.

## 12. E2E acceptance checklist

- [ ] Paid repair, CASH: receipt → SUBMITTED; the quotation stays ACCEPTED; Manager accepts → quotation PAID, request READY_FOR_WORK; repair → completed.
- [ ] Paid repair, PayOS: QR, webhook, PAID. No receipt UI.
- [ ] Fully covered warranty / disaster: no payment UI, no receipt.
- [ ] Partial warranty / partial disaster: receipt amount = customer part only.
- [ ] One-time maintenance, CASH: receipt → accepted → visit → customer acceptance → completed.
- [ ] Fault found during package maintenance: the new FAULT request is paid by receipt; the package visit has no payment.
- [ ] Rejected receipt: reason shown; resend for the same payment; accepted; the customer is not charged twice.
- [ ] Missing photo / number → field errors. Wrong attempt → reload message. Duplicate number or photo → messages.
- [ ] While SUBMITTED: cancel / switch / decline / cancel request are refused with the under-review message.
- [ ] Quotation expiry while SUBMITTED: still acceptable. Collected after the expiry: REVIEW_REQUIRED warning plus the unapplied-money entry.
- [ ] Manager queue filters by kind. The photo opens. Admin can review. Sales, technician and customer cannot.
- [ ] Customer sees only the payment status.
- [ ] Installation 30/70 receipts unchanged (deposit, signature after deposit, balance after acceptance).
