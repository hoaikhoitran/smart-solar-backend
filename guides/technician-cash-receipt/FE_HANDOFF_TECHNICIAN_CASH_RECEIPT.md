# FE handoff — Technician physical cash receipt (Flow 2, backend 2026-11-27)

The technician takes the cash for a 30/70 installment at the customer's site. They then photograph the paper receipt and send it with its number.
**Sending a receipt never pays anything.** The installment stays `DUE` until a Manager or Admin accepts the receipt. Accepting it marks the payment
`PAID`, using the technician's photo, so nobody uploads it again. The Manager can instead reject the receipt with a reason, and the technician sends a
new one.

What is unchanged:
- PayOS.
- The 30 / 70 amounts.
- Bundled per-panel packages.
- The Manager's existing direct cash confirmation.
- Legacy contracts.

Every response keeps the envelope `{ isSuccess, traceId, data, error: { code, message, details } }`.

> Not deployed. The feature needs migration `20261127021802_TechnicianCashReceipts`, which only adds a table. Until it is applied, the API answers
> `503 SCHEMA_NOT_READY`.

## 1. Flow and states

```
Technician: CASH chosen ──► photo uploaded ──► receipt sent ───────────────► Manager accepts ──► payment PAID, installment PAID
            (AWAITING_CASH_CONFIRMATION)       currentCashReceipt.status      (or rejects with a reason ──► REJECTED ──► technician sends again)
                                               = SUBMITTED
```

| `paymentState` | `currentCashReceipt` | Technician UI | Manager UI |
| --- | --- | --- | --- |
| `AWAITING_CASH_CONFIRMATION` | `null` | **Chụp phiếu thu** button | — |
| `AWAITING_CASH_CONFIRMATION` | `status = SUBMITTED` | "Đã gửi phiếu thu, chờ quản lý kiểm tra". Hide "Hủy tiền mặt" (the API answers 409) | Queue item: view photo, Accept / Reject |
| `AWAITING_CASH_CONFIRMATION` | `status = REJECTED` | Show `reviewReason` and a **Gửi lại phiếu thu** button | Not in the SUBMITTED queue |
| `PAID` | `status = ACCEPTED` | "Quản lý đã xác nhận phiếu thu" | — |

Receipt statuses: `SUBMITTED`, `ACCEPTED`, `REJECTED`, `WITHDRAWN`. `WITHDRAWN` is reserved for history and is never set by the current flows.

## 2. Technician endpoints (role `TECHNICIAN`, only the technician assigned to the contract; anyone else gets 404)

### 2.1 Choose cash (existing, unchanged)
`POST /api/technician/contracts/{contractId}/installments/{sequence}/payment` with body `{ "method": "CASH" }`. Sequence 1 is the 30 % deposit and
sequence 2 is the 70 % balance. Keep `data.current.paymentAttemptId`.

### 2.2 Upload the photo (existing file flow)

1. Request an upload URL with `POST /api/files/presigned-upload`:
   `{ "purpose": "PAYMENT_EVIDENCE", "fileName": "phieu-thu.jpg", "contentType": "image/jpeg", "sizeBytes": 812345 }`.
   - Allowed types: `image/jpeg`, `image/png`, `image/webp` and `application/pdf`.
   - Maximum size: **15 MB**.
   - The `TECHNICIAN` role can now request this purpose.
2. Send the bytes with a `PUT` to the returned `uploadUrl`, using the returned headers and **no** Bearer token.
3. Register the file with `POST /api/files`, passing `{ purpose, objectKey, fileName, contentType, sizeBytes }`. The response is 201 with
   `status = "VERIFIED"`. Keep `fileId`.

### 2.3 Send the receipt — NEW
`POST /api/technician/contracts/{contractId}/installments/{sequence}/payment/cash-receipt`

```json
{ "paymentAttemptId": "4c1e…", "receiptNumber": "PT-000123", "evidenceFileId": "9b0a…",
  "collectedAt": "2026-11-27T09:40:00+07:00", "note": "Khách trả đủ" }
```

| Field | Rule |
| --- | --- |
| `paymentAttemptId` | Required. Must be the open CASH payment of this installment (`data.current.paymentAttemptId`). |
| `receiptNumber` | Required. Trimmed, 1–100 characters. Unique across payments. The same number may be resent for the same payment after a rejection. |
| `evidenceFileId` | Required. A VERIFIED `PAYMENT_EVIDENCE` file registered by this technician, not used as the evidence of another payment. |
| `collectedAt` | Optional; defaults to now. At most 5 minutes in the future (device clock skew) and not before cash was chosen. |
| `note` | Optional, at most 500 characters. |

The response is 200 with the `InstallmentPaymentView` (shape in §4). It still shows `paymentState = AWAITING_CASH_CONFIRMATION`,
`installmentStatus = DUE` and `currentCashReceipt.status = SUBMITTED`.

### 2.4 Photo of a sent receipt — NEW
`GET /api/technician/contracts/{contractId}/installments/{sequence}/payment/cash-receipts/{cashReceiptId}/evidence/url?disposition=inline`

The response is 200 with `AttachedFileUrlView`: `{ id, url, expiresAt, disposition, contentType, fileName }`. The URL is short-lived: do not store it.
The response is sent with `Cache-Control: no-store`. Each read is audited.

### 2.5 Read / cancel (existing)
- Read: `GET …/installments/{sequence}/payment`. The response now also includes `currentCashReceipt` and `cashReceipts`.
- Cancel: `POST …/payment/cancel`. While a receipt is `SUBMITTED`, the cancel answers **409 `PAYMENT_CASH_RECEIPT_UNDER_REVIEW`**.

## 3. Manager / Admin endpoints (roles `MANAGER`, `ADMIN`) — NEW

| Method & route | Purpose |
| --- | --- |
| `GET /api/manager/payments/cash-receipts?status=SUBMITTED&page=1&pageSize=20` | Review queue, newest first. `status` is optional (any status when omitted). |
| `GET /api/manager/payments/cash-receipts/{cashReceiptId}` | Detail: `current` (same shape as a queue row) and `history`, which lists every receipt sent for the same payment, oldest first. |
| `GET /api/manager/payments/cash-receipts/{cashReceiptId}/evidence/url?disposition=inline\|attachment` | The technician's photo. No re-upload. Audited, `no-store`. |
| `POST /api/manager/payments/cash-receipts/{cashReceiptId}/accept` with body `{ "revision": 1, "note": "Đã đối chiếu" }` | Accept: the payment becomes `PAID` and the installment is settled. Returns `PaymentAttemptView` (staff fields included). |
| `POST /api/manager/payments/cash-receipts/{cashReceiptId}/reject` with body `{ "revision": 1, "reason": "Ảnh mờ, chụp lại số phiếu" }` | Reject. `reason` is required (at most 500 characters) and is shown to the technician. Returns `CashReceiptView`. |

`revision` is the `revision` of the receipt you displayed. A newer state answers 409 `PAYMENT_CASH_RECEIPT_STALE`: reload it.

Queue row (`StaffCashReceiptView`):

```json
{ "receipt": { "cashReceiptId": "…", "paymentAttemptId": "…", "contractId": "…", "contractInstallmentId": "…", "amount": 17100000,
               "receiptNumber": "PT-000123", "collectedByUserId": "…", "collectedAt": "2026-11-27T02:40:00+00:00", "evidenceFileId": "…",
               "note": "Khách trả đủ", "status": "SUBMITTED", "submittedAt": "…", "reviewedByUserId": null, "reviewedAt": null,
               "reviewReason": null, "revision": 1 },
  "contractNumber": "HD-…", "installmentSequence": 1, "installmentKind": "DEPOSIT", "customerUserId": "…",
  "customerName": "Nguyễn Văn An", "collectedByName": "KTV Bình", "paymentStatus": "PENDING" }
```

The existing direct confirmation `POST /api/manager/payments/{attemptId}/cash/confirm` still works, with the Manager's own evidence. While a
technician receipt is `SUBMITTED` it answers **409 `PAYMENT_CASH_RECEIPT_UNDER_REVIEW`**: review the receipt instead.

## 4. DTO changes

`InstallmentPaymentView` gains two additive fields. Technician and staff reads fill them; for the customer both are always `null`.

```ts
currentCashReceipt: CashReceiptView | null   // the receipt of the current payment: SUBMITTED first, else ACCEPTED, else the latest (e.g. REJECTED)
cashReceipts: CashReceiptView[] | null       // every receipt of this installment's payments, oldest first (review history)
```

```ts
type CashReceiptView = {
  cashReceiptId: string; paymentAttemptId: string; contractId: string; contractInstallmentId: string; amount: number
  receiptNumber: string; collectedByUserId: string; collectedAt: string; evidenceFileId: string; note: string | null
  status: 'SUBMITTED' | 'ACCEPTED' | 'REJECTED' | 'WITHDRAWN'; submittedAt: string
  reviewedByUserId: string | null; reviewedAt: string | null; reviewReason: string | null; revision: number
}
type StaffCashReceiptView = { receipt: CashReceiptView; contractNumber: string; installmentSequence: number; installmentKind: 'DEPOSIT' | 'BALANCE'
  customerUserId: string; customerName: string; collectedByName: string; paymentStatus: string }
type StaffCashReceiptDetail = { current: StaffCashReceiptView; history: CashReceiptView[] }
```

When a receipt is accepted, the `PAID` attempt carries:
- `providerReference`: the receipt number;
- `paidAt`: the time of the confirmation;
- `amountPaid`.

Staff views also see `confirmedByUserId` (the Manager) and `evidenceFileId` (the technician's photo). The time the cash was taken is kept on the
receipt (`collectedAt`) and is used for late-payment checks.

## 5. Error codes

| HTTP | Code | When / FE action |
| --- | --- | --- |
| 400 | `VALIDATION_FAILED`, `FILE_TYPE_NOT_ALLOWED`, `FILE_TOO_LARGE` | The upload type or size is refused at the presigned step; for `VALIDATION_FAILED`, `details` contains one or more of `paymentAttemptId`, `receiptNumber`, `evidenceFileId`, `collectedAt`, `note`, `reason`, `revision`, `status`. Show each message next to its field. |
| 404 | `CONTRACT_NOT_FOUND` | The contract does not exist, or it belongs to another technician. |
| 404 | `CONTRACT_INSTALLMENT_NOT_FOUND` | No installment with this sequence. |
| 404 | `PAYMENT_CASH_RECEIPT_NOT_FOUND` | No receipt with this id, or it belongs to another installment. |
| 404 | `FILE_NOT_FOUND` | Unknown `evidenceFileId`: upload again. |
| 409 | `FILE_NOT_VERIFIED` | The upload is not verified (it failed or is incomplete): upload again. |
| 403 | `FILE_NOT_OWNED` | The file was registered by another user. |
| 400 | `FILE_WRONG_PURPOSE` | The file was not registered as `PAYMENT_EVIDENCE`. |
| 409 | `PAYMENT_CASH_NOT_SELECTED` | No open CASH payment (cash not chosen, cancelled, or the payment is ONLINE). |
| 409 | `PAYMENT_CASH_RECEIPT_ATTEMPT_MISMATCH` | `paymentAttemptId` is not the open payment (stale screen): reload the payment. |
| 409 | `PAYMENT_CASH_RECEIPT_ALREADY_SUBMITTED` | A receipt is already waiting for review. |
| 409 | `PAYMENT_CASH_RECEIPT_DUPLICATE` | The receipt number is used by another payment. |
| 409 | `PAYMENT_CASH_RECEIPT_EVIDENCE_REUSED` | The photo is already the evidence of another payment. |
| 409 | `PAYMENT_CASH_RECEIPT_UNDER_REVIEW` | Cancel or direct confirmation attempted while a receipt is `SUBMITTED`. |
| 409 | `PAYMENT_CASH_RECEIPT_NOT_REVIEWABLE` | The receipt was already accepted or rejected. |
| 409 | `PAYMENT_CASH_RECEIPT_STALE` | The `revision` is outdated: reload and review again. |
| 409 | `PAYMENT_ALREADY_PAID`, `CONTRACT_INSTALLMENT_ALREADY_PAID` | Already paid. Never show it as paid twice. |
| 409 | `PAYMENT_CASH_AMOUNT_MISMATCH` | The amount due changed since the receipt was sent. This should not happen; contact support. |
| 401 / 403 | `AUTH_UNAUTHORIZED` / forbidden | Not signed in / wrong role. A technician calling the Manager routes gets 403. |
| 429 | — | Rate limit. The send uses the `payment-create` limit, reads use `contract-read`, and Manager reviews use `contract-write`. |

## 6. FE integration guide

**Technician — on the installment block, when `paymentState === 'AWAITING_CASH_CONFIRMATION'`:**

1. Show the **Chụp phiếu thu** button when `currentCashReceipt` is `null` or `REJECTED`.
2. Open the camera with `<input type="file" accept="image/*" capture="environment">`. Most mobile browsers open the rear camera; desktop browsers
   open a file picker. Optionally use `getUserMedia` for an in-page camera where it is supported.
3. Show a preview with `URL.createObjectURL(file)` and offer **Chụp lại** (retake). Check the type and the 15 MB limit on the client before
   uploading. Optionally downscale large photos before upload.
4. Add a **Số phiếu thu** input (required, at most 100 characters) and an optional **Thời điểm thu** field (defaults to now).
5. **Gửi phiếu thu cho quản lý**:
   - upload the photo (§2.2);
   - send the receipt with `POST …/cash-receipt` (§2.3), using the current `paymentAttemptId`;
   - render the returned view.
6. While the receipt is `SUBMITTED`:
   - show a "chờ quản lý kiểm tra" badge;
   - hide the cancel button;
   - poll `GET …/payment` every 10–20 seconds, or refresh on focus.
7. After a rejection (`REJECTED`), show `currentCashReceipt.reviewReason` and a **Gửi lại** button that repeats steps 2–5. A new photo is usually needed.
8. Show `cashReceipts` as a small history, using the photo URL from §2.4 on demand.

**Manager — new "Phiếu thu tiền mặt" queue:**
- List the receipts with `status=SUBMITTED`. Each row shows the contract number, the installment ("Đợt 1 – 30%" / "Đợt 2 – 70%"), the amount, the
  customer, who collected the cash, when, and the receipt number.
- The detail screen shows the photo (§3 URL, `inline`), the history, and two actions:
  - **Xác nhận** sends `{ revision }`;
  - **Yêu cầu bổ sung** opens a dialog with a required reason and sends `{ revision, reason }`.
- Disable both buttons while a request is in flight. On 409 `PAYMENT_CASH_RECEIPT_STALE` or `NOT_REVIEWABLE`, reload the receipt.

**Customer:** no change. `cashReceipts` / `currentCashReceipt` are `null`.

**UI locks:**
- In the FE repo (`ui-locks.txt`, owner `huufuoc`), `src/features/payment-plan/InstallmentPaymentBlock.tsx` and
  `src/features/contracts/CashConfirmDialog.tsx` are locked.
- Build the receipt capture and the Manager queue as **new components**, or ask the owner to wire them into the locked files.
- Add the new endpoints to `src/services/api/endpoints.ts` and the types to `src/types/res/paymentPlanRes.ts`. Do not regenerate the whole API
  client, because the FE swagger copy lags behind.

## 7. Not verified / open points

- Real S3 / camera devices: tests use synthetic storage and presigners. No physical device testing was done.
- The backend does not OCR the photo or compare it with the number; the Manager checks it visually.
- No push or e-mail notification to the Manager when a receipt arrives, and none to the technician on rejection. The FE should poll. A notification
  would be a follow-up decision.
- Scope: 30/70 installment payments only. Legacy full-payment contracts and after-sales cash keep the Manager's direct confirmation.
