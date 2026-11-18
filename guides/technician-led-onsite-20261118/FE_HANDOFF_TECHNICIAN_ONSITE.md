# FE handoff: Technician báo giá tại chỗ, thanh toán QR, hợp đồng sau thanh toán, chốt lịch (2026-11-18)

Tài liệu cho đội Frontend. Backend đã thay đổi; **FE chưa được sửa trong task này**. Mọi response dùng envelope chuẩn
`{ isSuccess, traceId, data, error: { code, message, details, fieldErrors } }`.

## 1. Thay đổi nghiệp vụ (trước → sau)

| Bước | Trước (2026-11-17) | Sau (2026-11-18) |
|---|---|---|
| Báo giá lắp đặt | Technician tạo cart → `issue` = **gửi** báo giá vào app Customer (notification) | Technician tạo cart → `issue` = **chốt giá để trình bày** trên thiết bị Technician; **không** gửi gì vào app Customer |
| Quyết định báo giá | Customer bấm Accept / Reject / Request revision trong app | Customer trả lời **trực tiếp**; Technician ghi nhận `AGREED` / `DECLINED` / `REVISION_NEEDED` bằng tài khoản của mình |
| Hợp đồng | Tạo DRAFT ngay khi Customer accept | Chỉ tạo **sau khi thanh toán được xác minh**, từ snapshot báo giá đã thanh toán |
| Thanh toán | Customer trả sau khi hợp đồng SIGNED | Technician mở thanh toán ngay sau khi Customer đồng ý: **QR PayOS** trên máy Technician (Customer quét bằng app ngân hàng, không cần đăng nhập) hoặc **tiền mặt** (Manager/Admin xác nhận) |
| Ký hợp đồng | Customer upload scan trong app | Manager/Admin chuẩn bị hợp đồng; scan bản giấy ký tại chỗ được **nhân sự** upload; Manager duyệt. Hợp đồng đã trả tiền → duyệt chữ ký là **PAID** ngay, không thu lại |
| Lịch lắp đặt | Technician gửi khung giờ, Customer chọn trong app | Technician thống nhất trực tiếp rồi ghi lịch; hệ thống tự xác nhận khi hợp đồng hoàn tất (đã trả + đã ký) |
| Nghiệm thu lắp đặt | Customer bấm Accept / yêu cầu rework | **Manager/Admin nghiệm thu nội bộ** (có ghi chú căn cứ, tùy chọn biên bản) hoặc trả về rework |
| Thiết bị thay thế | Manager duyệt + Customer đồng ý trong app | Chỉ Manager duyệt (có lý do); Customer **được thông báo** |
| Báo giá hậu mãi (bảo hành/sửa chữa/bảo trì một lần) do Technician lập | Customer Accept/Reject trong app rồi tự thanh toán | Technician ghi nhận quyết định tại chỗ + mở QR / tiền mặt; công việc có phí chỉ làm sau khi tiền được xác minh |
| Kết thúc công việc hậu mãi | Customer bấm chấp nhận | Technician ghi nhận Customer đã xác nhận tại chỗ (vẫn giữ endpoint Customer cho trường hợp họ tự xác nhận) |

**App Customer chỉ để theo dõi**: hợp đồng, lịch, tiến độ, ảnh/bằng chứng, bàn giao, bảo hành. Không còn bước duyệt nào chặn tiến độ.

## 2. Màn hình Technician: báo giá tại chỗ

### 2.1 Cart (không đổi)
- `POST /api/technician/survey-requests/{surveyRequestId}/quotation` → DRAFT (201)
- `POST /api/technician/quotations/{id}/lines` `{ revision, productId, quantity }` — giá luôn lấy từ catalog
- `PUT /api/technician/quotations/{id}/lines/{lineId}` `{ revision, quantity }`, `DELETE …/lines/{lineId}?revision=`
- `POST /api/technician/quotations/{id}/issue` `{ revision }` → `status: ISSUED` (giá đã chốt, `validUntil` có hạn)
- `GET /api/technician/quotations/{id}` (có `events`), `GET /api/technician/survey-requests/{id}/quotations`

UI: sau `issue`, hiển thị báo giá full màn hình cho Customer xem (dòng, đơn giá, VAT, tổng, hạn hiệu lực).

### 2.2 Ghi nhận quyết định tại chỗ (MỚI)
`POST /api/technician/quotations/{quotationId}/on-site-decision`

```json
{ "decision": "AGREED", "revision": 7, "note": "Khách đồng ý tại nhà" }
```

| decision | Kết quả | Ghi chú |
|---|---|---|
| `AGREED` | `status: ACCEPTED` (giá bất biến) | Báo giá phải `ISSUED`, còn hạn, survey chưa đổi |
| `DECLINED` | `status: REJECTED`, `closeReason: "DECLINED_ON_SITE: …"` | `note` bắt buộc; không phát sinh thanh toán |
| `REVISION_NEEDED` | `status: REVISION_REQUESTED` | `note` bắt buộc; sau đó gọi `POST …/revisions` để tạo version mới |

- `revision` = revision đang hiển thị trên màn hình (sai → 409 `QUOTATION_STALE_REVISION`, reload).
- Khách đổi ý **sau khi AGREED nhưng chưa trả tiền**: gửi `DECLINED`/`REVISION_NEEDED` lên báo giá ACCEPTED. Nếu đã mở thanh toán → 409
  `QUOTATION_PAYMENT_STARTED` (hủy thanh toán đang mở trước).
- Lỗi: 404 `QUOTATION_NOT_FOUND` (không phải Technician khảo sát), 409 `QUOTATION_EXPIRED`, `QUOTATION_SURVEY_CHANGED`, `QUOTATION_ALREADY_ACCEPTED`,
  `QUOTATION_NOT_ISSUED`, `QUOTATION_REQUEST_CLOSED`, 400 `VALIDATION_FAILED`.
- Lịch sử: event `AGREED_ON_SITE` / `DECLINED_ON_SITE` / `REVISION_REQUESTED_ON_SITE`, `actorUserId` = Technician.

### 2.3 Thanh toán tại chỗ (MỚI)
`POST /api/technician/quotations/{quotationId}/payment` `{ "method": "ONLINE" | "CASH" }` → `QuotationPaymentView`

```json
{
  "quotationId": "…", "quoteNumber": "QT-20261118-ABCD23", "quotationVersion": 1, "quotationStatus": "ACCEPTED",
  "amount": 19440000, "currency": "VND", "paymentState": "ONLINE_IN_PROGRESS",
  "current": { "paymentAttemptId": "…", "contractId": null, "orderCode": 1794968155123, "amount": 19440000, "status": "PENDING",
               "checkoutUrl": "https://pay.payos.vn/…", "qrCode": "000201…", "expiresAt": "…", "method": "ONLINE" },
  "attempts": [ … ], "contractId": null
}
```

- **ONLINE**: render `current.qrCode` thành QR (chuỗi VietQR) hoặc mở `checkoutUrl`. Customer quét bằng app ngân hàng.
- **CASH**: `paymentState: AWAITING_CASH_CONFIRMATION`. Technician **không** xác nhận được; Manager/Admin xác nhận ở màn Payment desk:
  `POST /api/manager/payments/{paymentAttemptId}/cash/confirm` `{ amount, receiptReference, evidenceFileId, note }` (đúng số tiền, mã phiếu duy nhất,
  file `PAYMENT_EVIDENCE` đã verify).
- Gọi lại cùng method → trả về thanh toán đang mở (idempotent). Đổi method → 409 `PAYMENT_CASH_SELECTED` / `PAYMENT_CHECKOUT_IN_PROGRESS`: gọi
  `POST /api/technician/quotations/{id}/payment/cancel` trước.
- **Poll** trạng thái: `GET /api/technician/quotations/{id}/payment` (server hỏi PayOS server-to-server; **không** dùng return URL làm bằng chứng PAID).
  Gợi ý poll 3–5 s khi đang hiển thị QR; webhook/reconciliation xác nhận trong ≤ 2 phút.

`paymentState`:

| Giá trị | Ý nghĩa | UI |
|---|---|---|
| `NOT_PAYABLE` | chưa đồng ý / hết hạn / đã đóng | ẩn nút thanh toán |
| `AWAITING_PAYMENT` | đã đồng ý, chưa mở thanh toán | nút QR / Tiền mặt |
| `ONLINE_IN_PROGRESS` | QR đang mở | hiển thị QR + poll |
| `AWAITING_CASH_CONFIRMATION` | đã chọn tiền mặt | "Chờ quản lý xác nhận thu tiền" |
| `PAID` | tiền đã xác minh, **hợp đồng đã tạo** (`contractId`) | chuyển sang bước chốt lịch |
| `REVIEW_REQUIRED` | tiền về nhưng không áp dụng được (sai số tiền…) | báo Manager; không tạo hợp đồng |

Lỗi: 409 `QUOTATION_NOT_AGREED`, `QUOTATION_EXPIRED`, `PAYMENT_ALREADY_PAID`, 400 `VALIDATION_FAILED`, 404 `QUOTATION_NOT_FOUND`.

## 3. Hợp đồng (Manager/Admin) — tạo sau thanh toán

- Hợp đồng được tạo **tự động** khi thanh toán xác minh: `status: DRAFT`, `paymentCompleted: true`, `paymentAttemptId`, `paymentConfirmedAt`.
- `ContractView` có thêm: `paymentCompleted`, `paymentAttemptId`, `paymentConfirmedAt`. **Trạng thái hợp đồng ≠ trạng thái thanh toán**: hiển thị cả hai.
- Quy trình giữ nguyên: `PUT /api/contracts/{id}` (terms, warranty) → `POST …/issue` → `PUT …/official-document` → `POST …/publish` →
  nhân sự upload scan giấy ký tại chỗ `POST /api/contracts/{id}/signed-document` `{ fileId, officialDocumentSha256, signedOn?, note? }` →
  `POST …/review` `{ revision, decision: "APPROVE" }`. Hợp đồng đã trả tiền → **PAID ngay** khi duyệt (không thu lại).
- `POST /api/quotations/{id}/contracts` (tạo thủ công) chỉ còn là đường khôi phục: không có thanh toán đã xác minh → 409 `CONTRACT_PAYMENT_REQUIRED`.
- Hợp đồng **cũ** (SIGNED, chưa trả): vẫn thanh toán qua các endpoint cũ `…/customers/me/contracts/{id}/payments…` và `…/contracts/{id}/payments/cash/confirm`.
  Hợp đồng đã trả qua báo giá gọi các endpoint này → 409 `PAYMENT_ALREADY_PAID`.

## 4. Chốt lịch lắp đặt (Technician)

- Danh sách: `GET /api/technicians/me/installation-schedules` (gồm cả hợp đồng đã trả nhưng chưa ký).
- Lịch trống: `GET /api/technicians/me/contracts/{contractId}/installation-availability?from=yyyy-MM-dd&to=yyyy-MM-dd&durationDays=2`.
- Ghi lịch đã thống nhất: `POST /api/technicians/me/contracts/{contractId}/installation-schedule`

```json
{ "startsAt": "2026-11-25T08:00:00+07:00", "endsAt": "2026-11-26T17:00:00+07:00",
  "customerAgreementNote": "Thống nhất trực tiếp tại nhà khách 18/11", "reason": null, "expectedRevision": null }
```

- Backend kiểm tra: tương lai, khung giờ làm việc, horizon, zone, nghỉ phép, cam kết khác, **lịch đã thống nhất khác của Technician**
  (409 `INSTALLATION_SCHEDULE_TECHNICIAN_INELIGIBLE`, `details.reason = "PLANNED_CONFLICT"`).
- Kết quả `status: PLANNED`; cờ mới `awaitingContractSignature: true` (đã trả, chờ ký) hoặc `awaitingPayment: true` (hợp đồng cũ chưa trả).
  Khi hợp đồng thành PAID (đã trả + đã ký), hệ thống tạo project, giao việc và chuyển lịch `CONFIRMED` — không cần Customer.
- Đổi lịch: gửi lại với `reason` (bắt buộc). Hủy: `POST /api/technicians/me/installation-schedules/{id}/cancel` `{ reason }`.
- **Retired**: `POST /api/technicians/me/contracts/{id}/installation-slot-offers` (410). `GET` danh sách offer cũ vẫn đọc được (lịch sử).

## 5. Thi công & nghiệm thu nội bộ (Manager/Admin)

- Technician: start → evidence → assets → `POST /api/technicians/me/installation-projects/{id}/completion-report` (không đổi).
- Trạng thái `PENDING_CUSTOMER_ACCEPTANCE` **giữ tên** (tương thích dữ liệu) nhưng nghĩa mới: **chờ nghiệm thu nội bộ**. FE nên hiển thị
  "Chờ nghiệm thu".
- Nghiệm thu (MỚI): `POST /api/installation-projects/{projectId}/acceptance` (Manager/Admin)

```json
{ "expectedRevision": 12, "note": "Đã bàn giao trực tiếp với khách, biên bản ký tại chỗ", "evidenceFileId": "…(PROJECT_DOCUMENT, tùy chọn)" }
```

  Bị từ chối nếu checklist bắt buộc chưa xong (409 `INSTALLATION_CHECKLIST_INCOMPLETE`) hoặc thiết bị khác hợp đồng chưa được duyệt
  (409 `INSTALLATION_SUBSTITUTION_APPROVAL_REQUIRED`). Trả về rework: `POST /api/installation-projects/{id}/return-to-rework` (không đổi).
- Thiết bị thay thế: `POST /api/installation-projects/{id}/equipment-substitution/approve` (Manager) → `equipment.status: SUBSTITUTION_APPROVED` ngay
  (bản ghi `INTERNALLY_APPROVED`); trạng thái `CUSTOMER_APPROVAL_REQUIRED` không còn.

## 6. Hậu mãi tại hiện trường (Technician)

1. Kiểm tra, ghi hạng mục (không đổi); Manager quyết định coverage (Technician **không** đổi coverage).
2. Lập báo giá: `POST /api/technicians/me/after-sales/requests/{requestId}/quotations` (không đổi; không còn notification cho Customer).
3. Ghi nhận quyết định (MỚI): `POST /api/technicians/me/after-sales/quotations/{quotationId}/on-site-decision` `{ "decision": "AGREED" | "DECLINED", "note": "…" }`
   (`note` bắt buộc khi `DECLINED`). Báo giá được bảo hiểm toàn bộ (`customerAmount = 0`) → AGREED là được phép làm ngay, **không** tạo checkout 0 VND.
4. Thanh toán (MỚI): `POST …/quotations/{id}/payment` `{ "method": "ONLINE" | "CASH" }` → `PaymentAttemptView` (QR/`checkoutUrl` hoặc tiền mặt chờ
   Manager xác nhận ở `POST /api/manager/payments/{attemptId}/cash/confirm`); `GET …/payment` → `{ quotationId, version, quotationStatus, customerAmount,
   nothingToPay, current }`; `POST …/payment/cancel`.
5. Sửa ngay trong cùng visit: `continue-repair` vẫn yêu cầu đã thanh toán (409 `AFTER_SALES_PAYMENT_REQUIRED` nếu chưa).
6. Kết thúc (MỚI): `POST /api/technicians/me/after-sales/requests/{requestId}/completion-confirmation` `{ "note": "Khách kiểm tra cùng KTV", "evidenceFileIds": [] }`
   → `COMPLETED`.

Báo giá do **Sales** lập (bảo trì một lần không qua kiểm tra) vẫn dùng accept/thanh toán phía Customer như cũ. Với báo giá do Technician lập, Customer
gọi accept/reject → 409 `SERVICE_QUOTATION_DECIDED_ON_SITE`.

## 7. App Customer — chỉ theo dõi

Giữ nguyên (GET): `/api/customers/me/contracts`, `/contracts/{id}`, `/installation-schedules`, `/contracts/{id}/installation-schedule`,
`/installation-projects`, `/installation-projects/{id}`, `/evidence`, `/handover`, after-sales requests & notifications.
Đổi lịch (tùy chọn, không chặn): `POST /api/customers/me/installation-schedules/{id}/change-request`.

**Retired — 410 `ENDPOINT_RETIRED`** (FE xóa các màn hình/nút tương ứng):

| Endpoint |
|---|
| `GET /api/customers/me/quotations`, `GET …/quotations/{id}` |
| `POST /api/customers/me/quotations/{id}/accept`, `/reject`, `/revision-request` |
| `GET /api/customers/me/contracts/{id}/installation-slot-offers`, `POST /api/customers/me/installation-slot-offers/{id}/select` |
| `POST /api/customers/me/installation-projects/{id}/acceptance`, `/equipment-substitution/approve` |
| `POST /api/technicians/me/contracts/{id}/installation-slot-offers` (Technician) |

Notification mới (chỉ thông tin): `CONTRACT_CREATED_AFTER_PAYMENT`, `INSTALLATION_COMPLETION_REPORTED`, `INSTALLATION_COMPLETED`,
`EQUIPMENT_SUBSTITUTION_RECORDED`. Không còn: `INSTALLATION_QUOTATION_ISSUED`, `INSTALLATION_SLOTS_OFFERED`, `INSTALLATION_ACCEPTANCE_REQUIRED`,
`EQUIPMENT_SUBSTITUTION_APPROVAL_REQUIRED`.

## 8. Khác
- Survey request đang có thanh toán mở/đã xác minh không thể withdraw/close: 409 `SURVEY_REQUEST_PAYMENT_IN_PROGRESS`.
- Rate limit: decision = `quotation-write`, mở/hủy thanh toán = `payment-create`, xem thanh toán = `payment-status`, nghiệm thu = `installation-write`.
- Dữ liệu cũ (báo giá đã ACCEPTED trong app trước 18/11 mà chưa có hợp đồng) đi theo luồng mới: Technician mở thanh toán → hợp đồng được tạo.
