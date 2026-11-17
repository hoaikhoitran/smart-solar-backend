# Tích hợp FE — Cloud RAG, chống spam, dashboard chi phí

Trạng thái FE: **NOT IMPLEMENTED** (2026-11-17). Repo FE `~/smart-solar` có 43 thay đổi chưa commit của người khác và chậm hơn
`origin/main` (9bbb45b → d8cc550); sửa ở đó sẽ trộn vào công việc đang dở. Tài liệu này là đặc tả để đội FE làm trên nhánh của họ.
Đối chiếu dưới đây dựa trên `origin/main` d8cc550 (đọc qua GitHub API, không sửa).

## 1. Chat (`POST /api/chat/messages`) — thay đổi bắt buộc

| # | Việc | Vì sao | File FE |
|---|---|---|---|
| 1 | `CHAT_LIMITS.messageMax`: **8000 → 1000** | Backend từ chối > 1000 ký tự (400 `RAG_MESSAGE_TOO_LONG`, admin có thể hạ thấp hơn: dùng `details.maxCharacters`). FE hiện cho gõ 8000. | `src/common/chat.ts` |
| 2 | Xử lý mã lỗi mới (bảng 2) trong `chatSendError` | Hiện chỉ xử lý `AUTH_TOO_MANY_REQUESTS` | `src/features/chat/chatDisplay.ts` |
| 3 | Thử lại `AI_REQUEST_IN_PROGRESS` với **cùng** `clientMessageId` sau `retryAfterSeconds` | Đây là "câu này đang được trả lời" (double-click, hai tab); thử lại cùng id trả về câu trả lời, không tính thêm | `useChat.ts` / `chatRules.ts` (đã có cơ chế reuse id) |
| 4 | Khóa nút gửi trong lúc chờ (đã có) và đếm ngược theo `retryAfterSeconds` | Concurrency/người = 1 | `ChatComposer.tsx` |

FE đã gửi `clientMessageId` và dùng lại khi retry (`chatRules.ts`) — giữ nguyên.

## 2. Mã lỗi chat

| HTTP | `error.code` | Ý nghĩa | Hiển thị gợi ý | Retry tự động |
|---|---|---|---|---|
| 400 | `RAG_MESSAGE_TOO_LONG` (`details.maxCharacters`) | Câu hỏi rỗng / quá dài | Lỗi tại ô nhập, dùng `maxCharacters` | Không |
| 429 | `AUTH_TOO_MANY_REQUESTS` (+ header `Retry-After`) | > 5 câu/phút | "Bạn hỏi nhanh quá…" (đã có) | Không |
| 429 | `RAG_TOO_MANY_CONCURRENT_REQUESTS` | Câu trước chưa xong | "Chờ câu trả lời trước" | Không |
| 429 | `RAG_QUOTA_EXCEEDED` | Hết lượt hôm nay (người dùng hoặc toàn hệ thống) | "Bạn đã dùng hết lượt hỏi hôm nay" | Không |
| 429 | `AI_REQUEST_IN_PROGRESS` | Cùng `clientMessageId` đang xử lý | (im lặng) | **Có**, cùng id, sau `details.retryAfterSeconds` |
| 429 | `AI_GLOBAL_CONCURRENCY_LIMIT` | Hệ thống đang bận | "Trợ lý đang bận, thử lại sau N giây" | Tùy chọn 1 lần |
| 429 | `AI_BUDGET_EXCEEDED` | Hết ngân sách ngày/tháng | "Trợ lý tạm ngưng trả lời, thử lại sau" | Không |
| 503 | `AI_GENERATION_DISABLED` | Admin tắt generation | "Trợ lý tạm ngưng" (Solar Advisor vẫn dùng được) | Không |
| 503 | (khác) | RAG service lỗi / quá hạn | thông báo chung | Không (đã có) |

Không hiển thị con số hạn mức (admin cấu hình được). `retryAfterSeconds` nằm trong `error.details`.

## 3. Trang Admin "Chi phí AI" (mới) — đặc tả

Route đề xuất: `/admin/ai-usage` (chỉ Admin), cạnh `knowledge-documents` và tab cấu hình RAG trong `admin-settings`.

Endpoint (thêm vào `ENDPOINTS`):
```ts
adminRagUsage: {
  summary: '/admin/rag-usage/summary', timeseries: '/admin/rag-usage/timeseries', byModel: '/admin/rag-usage/by-model',
  byOperation: '/admin/rag-usage/by-operation', byRole: '/admin/rag-usage/by-role', topUsers: '/admin/rag-usage/top-users',
  requests: '/admin/rag-usage/requests', review: (id: string) => `/admin/rag-usage/${encode(id)}/review`,
},
adminRagBudget: '/admin/rag-budget',
```
Kiểu dữ liệu: xem [admin-rag-dashboard-api.md](admin-rag-dashboard-api.md).

Bố cục:
1. **Thẻ ngân sách** (từ `GET /admin/rag-budget`): Hôm nay / Tháng / Ingestion tháng — `usedPercent` (thanh tiến độ), `remainingUsd`,
   `blocked`; badge cảnh báo từ `alerts` (critical đỏ, warning vàng, info xám).
2. **Bộ chọn khoảng ngày** (mặc định tháng hiện tại; chặn > 92 ngày phía client cho UX — server vẫn kiểm).
3. **KPI** từ `summary`: chi phí khoảng (settled + uncertain + reserved), số request, tỉ lệ answered / insufficient / failed,
   token in/out/embedding, latency trung bình & p95, model đang dùng.
4. **Biểu đồ theo ngày** (`timeseries`): cột chồng generation / embedding / ingestion USD; đường requests.
5. **Phân rã**: by-operation, by-model, by-role (bảng hoặc donut).
6. **Top người dùng** (`top-users?limit=10`).
7. **Bảng request** (`requests`, phân trang server, lọc operation / status / outcome); dòng `UNCERTAIN` có nút "Review" → dialog nhập
   `costUsd` (≥ 0, ≤ 6 số lẻ) + `note` (bắt buộc) → `POST review`; 404 = đã được review (tải lại).
8. **Form ngân sách & giới hạn** (`PUT /admin/rag-budget`): chỉ gửi trường đã đổi + `expectedActiveVersion` = `activeVersion` vừa tải;
   giới hạn input theo `ceilings`; 409 → báo "cấu hình vừa được người khác đổi", tải lại; công tắc **Tắt generation** có xác nhận.

Định dạng tiền: USD tối đa 6 chữ số lẻ trên UI (dữ liệu có 8); không làm tròn thành 0 khi > 0 (hiện "< 0.000001").

## 4. Tab cấu hình RAG (đã có) — bổ sung

`GET /admin/rag-configuration/models` nay có `provider`, `enabled`, `usable`, giá (`inputUsdPerMillion`, `cachedInputUsdPerMillion`,
`outputUsdPerMillion`), `reasoningEfforts`, `defaultReasoningEffort`, `isDefault`. Draft nhận thêm `reasoningEffort` và các trường chi phí.
- Chỉ cho chọn model `usable`; hiển thị giá; đánh dấu `isDefault`.
- Có `reasoningEfforts` → select; ẩn `temperature` khi model không hỗ trợ.

## 5. Kiểm thử chấp nhận đề xuất

- Câu 1001 ký tự bị chặn ở client; dán 1000 ký tự gửi được.
- Double-click gửi → một câu trả lời, không có lỗi hiện ra.
- Admin tắt generation → khách nhận thông báo tạm ngưng; Solar Advisor vẫn chạy.
- Role khác Admin không thấy menu và nhận 403 nếu gọi trực tiếp.
