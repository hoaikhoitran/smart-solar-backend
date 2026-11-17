# RAG — bảo mật, chống spam và kiểm soát chi phí token

Ngày: 2026-11-17. Mọi giới hạn được thực thi **phía server**; FE chỉ hiển thị.

## 1. Các lớp bảo vệ (theo thứ tự request đi qua)

| # | Lớp | Ở đâu | Khi vượt |
|---|---|---|---|
| 1 | JWT + feature flag `KnowledgeBase` | API | 401 / 404 |
| 2 | Rate limit `RagChat`: 5 câu/phút/người (Redis khi bật, phân tán giữa replica; không có Redis → đếm theo instance) | API | 429 + `Retry-After` |
| 3 | Độ dài câu hỏi ≤ min(`Rag:MaxQuestionCharacters`, cấu hình) = 1000 | ChatService | 400 |
| 4 | Solar Advisor được định tuyến trước LLM: deterministic, **không tính phí**, không bị khóa bởi kill switch | ChatService | — |
| 5 | Kill switch `generationEnabled=false` | ChatService | 503 `AI_GENERATION_DISABLED` |
| 6 | Admission nguyên tử trong PostgreSQL (mục 2) | KnowledgeUnitOfWork | 429 (mã bên dưới) |
| 7 | Deadline: API timeout = min(`GenerationTimeoutSeconds` 30, `RequestTimeoutSeconds`), RAG service không bắt đầu generation khi còn < 2 s | API + RAG | 503 |
| 8 | Trần cứng của RAG service: `RAG_MAX_CONTEXT_TOKENS` 2000, `RAG_MAX_OUTPUT_TOKENS` 300, `RAG_MAX_CONCURRENT_GENERATIONS` 3 | RAG | request chỉ có thể xin ít hơn |
| 9 | Hard limit tháng trên project key OpenAI | OpenAI | provider 429 → `none` billing |

## 2. Admission (một transaction, khóa hàng theo thứ tự cố định → không deadlock, không vượt ngân sách giữa các replica)

1. Dọn reservation hết hạn (crash/bỏ dở) → `UNCERTAIN` (giữ nguyên số tiền đã đặt trước).
2. Trùng `clientMessageId` đang xử lý → `AI_REQUEST_IN_PROGRESS` (unique index lọc chặn cả hai replica nhận cùng lúc).
3. Concurrency người dùng (mặc định 1) → `RAG_TOO_MANY_CONCURRENT_REQUESTS`.
4. Số câu/ngày/người (30) → `RAG_QUOTA_EXCEEDED`.
5. Token/ngày/người (`DailyTokensPerUser` 200 000) và toàn hệ thống (`GlobalDailyTokens` 5 000 000) → `RAG_QUOTA_EXCEEDED`.
6. Concurrency toàn hệ thống cho request trả phí (3) → `AI_GLOBAL_CONCURRENCY_LIMIT`.
7. Ngân sách USD ngày (1.00) và tháng (4.00): `reserved + settled + uncertain + chi phí tối đa của câu này ≤ hạn mức` → `AI_BUDGET_EXCEEDED`.

Thứ tự khóa: `rag_usage_daily` (toàn cục, rồi người dùng) → `ai_budget_period` sắp theo (kind, start). Được kiểm chứng trên
PostgreSQL với caller đua nhau trên kết nối riêng: 20 câu × 0.10 USD vào ngân sách 1.00 → **đúng 10** được nhận; trần toàn cục 3 → đúng 3;
6 lần cùng `clientMessageId` → đúng 1; 4 replica settle cùng reservation → áp dụng đúng 1 lần.

Mọi lỗi 429 có `error.details.retryAfterSeconds`.

## 3. Đặt trước và quyết toán

- **Chi phí tối đa** (`AiCostCalculator.MaxChat`): embedding câu hỏi + input (câu hỏi + toàn bộ context + 1334 token lịch sử + 1200 overhead
  prompt) + toàn bộ output, không giả định cache. Ví dụ `gpt-6-luna`, câu 1000 ký tự: ≈ **0.00064 USD**. Không có model được gửi
  (cấu hình bootstrap / model không khả dụng) → ước lượng theo model **đắt nhất** khả dụng.
- **Quyết toán** trong `finally` (`SettleChatAsync`), idempotent theo `idempotency_key`:
  - provider báo usage → `SETTLED` theo token thực tế (8 chữ số thập phân);
  - không có bằng chứng → không gọi generation → chỉ tính embedding câu hỏi;
  - lỗi chắc chắn không tính phí (HTTP lỗi, 429 provider, không kết nối được) → `RELEASED` (embedding đã trả thì vẫn tính);
  - timeout / mất kết nối / phản hồi hỏng / provider không báo usage → `UNCERTAIN` = số tiền đã đặt trước, chờ admin review
    (`POST /api/admin/rag-usage/{id}/review` với chi phí thật từ billing OpenAI).
- Ingestion: đặt trước `MaxIngestion` (kích thước file × 4 ký tự, ≤ `MaxDocumentCharacters` 3 000 000) vào `INGEST_MONTH` (0.50);
  vượt → job thất bại với `AI_BUDGET_EXCEEDED: …`, không gọi provider. `reindex-stale` từ chối khi ước lượng > phần ngân sách còn lại.

## 4. Giá trị mặc định và trần cứng

| Thiết lập | Mặc định | Trần (admin không thể vượt) |
|---|---|---|
| Câu/ngày/người | 30 | 1–200 |
| Concurrency/người | 1 | 1–2 |
| Concurrency toàn hệ thống | 3 | 1–10 |
| Độ dài câu hỏi | 1000 | 100–2000 |
| Timeout generation | 30 s | 5–60 s |
| Ngân sách chat ngày / tháng | 1.00 / 4.00 USD | ≤ 10 / ≤ 100, ngày ≤ tháng, 2 chữ số thập phân |
| Ngân sách ingestion tháng | 0.50 USD | ≤ 10 |
| Context / output | 2000 / 300 token | trần của RAG service |
| `reasoning_effort` | `none` | chỉ giá trị model hỗ trợ |

Thay đổi qua `PUT /api/admin/rag-budget` → tạo **phiên bản cấu hình mới được kích hoạt**, có audit (admin, thời điểm, ghi chú),
rollback được.

## 5. Bảo mật

- **Bí mật**: key OpenAI chỉ trong `.env` của RAG service (`RAG_*_API_KEY`), không bao giờ tới API/FE; không log, không echo
  (kiểm thử: response admin không chứa shared secret hay `sk-`). API ↔ RAG ký HMAC-SHA256 có timestamp + chống replay.
- **RBAC tài liệu**: API chỉ gửi danh sách version được phép theo audience; RAG service lọc trong câu SQL **và** lọc lại kết quả
  (defense in depth). Eval offline: 0/8 câu hỏi nội bộ từ khách hàng trích tài liệu INTERNAL.
- **Prompt injection**: nội dung nguồn bị trung hòa thẻ `<source>/<system>/…`, system prompt yêu cầu coi nguồn là dữ liệu; câu trả lời
  chỉ được coi là grounded khi trích dẫn nguồn có thật. Eval offline: 6/6 câu injection không lộ system prompt/bí mật.
- **Ledger** không lưu câu hỏi, câu trả lời hay khóa; dashboard chỉ Admin.
- **Dữ liệu gửi OpenAI**: câu hỏi, lịch sử ngắn và đoạn tài liệu được phép. Tài liệu INTERNAL chỉ được gửi khi người hỏi là nhân viên.

## 6. Vận hành

| Tình huống | Hành động |
|---|---|
| Chi phí tăng bất thường | `PUT /api/admin/rag-budget {expectedActiveVersion, generationEnabled:false}` |
| Một người dùng spam | Rate limit + quota/ngày tự chặn; xem `top-users` |
| Có dòng `UNCERTAIN` | Đối chiếu billing OpenAI → review (0 nếu không bị tính) |
| Cảnh báo 80% / 100% | `GET /api/admin/rag-budget` → `alerts` |
