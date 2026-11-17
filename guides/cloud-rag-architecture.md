# Kiến trúc Cloud RAG (OpenAI)

Ngày: 2026-11-17 · Áp dụng cho: SmartSolar.Api (.NET 8), SmartSolar.RagService (FastAPI), PostgreSQL + pgvector (Supabase)

Tài liệu liên quan: [cloud-rag-migration.md](cloud-rag-migration.md) · [rag-security-and-cost-control.md](rag-security-and-cost-control.md) ·
[admin-rag-dashboard-api.md](admin-rag-dashboard-api.md) · [rag-fe-integration.md](rag-fe-integration.md) · [rag-performance-report.md](rag-performance-report.md)

## 1. Tổng quan

```
FE ──JWT──► API (.NET)                                   RAG service (FastAPI, mạng nội bộ, HMAC)        OpenAI
            ├─ ChatService                               ├─ /internal/v1/answer  ── embed câu hỏi ───────► text-embedding-3-small
            │   1. cấu hình hiệu lực (versioned)         │                        ── pgvector search (lọc version + model)
            │   2. giới hạn độ dài, retry, lịch sử        │                        ── generation (chỉ khi có bằng chứng) ► gpt-6-luna
            │   3. định tuyến Solar Advisor (miễn phí)   ├─ /internal/v1/ingest  ── trích xuất, chunk, embed ─► text-embedding-3-small
            │   4. danh sách version được phép (audience)└─ /internal/v1/delete
            │   5. TryAdmitAsync  ── PostgreSQL (khóa hàng, nguyên tử, fail closed)
            │   6. gọi RAG service (deadline, ký HMAC)
            │   7. SettleChatAsync (finally) ── ai_usage_ledger + ai_budget_period
            └─ Admin: rag-configuration, rag-usage, rag-budget
```

Những thành phần **giữ nguyên**: ChatService, RAG service, pgvector, pipeline ingestion, RBAC theo audience
(CUSTOMER / INTERNAL), citations, Solar Advisor deterministic (không gọi LLM, không tính phí), versioning cấu hình
(draft → activate → rollback, có audit).

## 2. Mô hình

| Vai trò | Mô hình | Giá (USD / 1M token, nguồn: trang model OpenAI, kiểm tra 2026-11-17) | Ghi chú |
|---|---|---|---|
| Generation | `gpt-6-luna` | input 0.10 · cached input 0.01 · output 0.50 | `max_completion_tokens`; không gửi `temperature` khi `reasoning_effort` ≠ `none`; mặc định effort `none` |
| Embedding | `text-embedding-3-small` | 0.02 | tham số `dimensions` = 1024 (khớp cột `vector(1024)` hiện có) hoặc 1536 |

- Registry model của API (`RagConfigurationRules.DefaultModels`): `gpt-6-luna` là model **có giá và được bật** duy nhất →
  là model rẻ nhất khả dụng → mặc định bootstrap. `gpt-4.1-mini`, `gpt-4.1`, `gpt-4o-mini` vẫn có trong danh sách nhưng
  `Enabled=false` (không chọn được cho đến khi admin cấu hình giá qua `Rag:GenerationModels`).
- Model trả phí **không có giá** thì không khả dụng (không thể ước lượng chi phí → fail closed).
- Model local (`id` chứa `:`, ví dụ `qwen3:1.7b`) được nhận là provider `ollama`, chi phí 0.

## 3. Embedding có version, không trộn model

- Mỗi chunk lưu `embedding_model`. Tìm kiếm luôn lọc `embedding_model = <model của câu hỏi>` → không bao giờ so vector của hai model.
- Khóa duy nhất mới: `ux_knowledge_chunk_version_model_index UNIQUE (version_id, embedding_model, chunk_index)`
  (migration `20261117131913_CloudRagCostControl`). Chunk của model mới được ghi **bên cạnh** chunk model cũ của cùng version.
- `chunk_id = {version_hex}:{model_tag8}:{index}`; `replace_version` chỉ xóa chunk **cùng model** (idempotent, resumable).
  Trên schema cũ (khóa `version_id, chunk_index`) RAG service tự fallback về xóa toàn bộ chunk của version.
- Giai đoạn chuyển tiếp: `RAG_QUERY_EMBEDDING_*` = model cũ (câu hỏi vẫn embed bằng model cũ và chỉ tìm chunk model cũ) trong khi
  ingestion ghi model mới. Khi mọi version đã có chunk model mới → bỏ trống `RAG_QUERY_EMBEDDING_*` → chuyển một lần, không có
  thời điểm nào phục vụ index đang dở (không "partial exposure").

## 4. Kiểm soát chi phí (tóm tắt — chi tiết trong rag-security-and-cost-control.md)

1. Giới hạn server-side: độ dài câu hỏi, context, output, số lịch sử.
2. Rate limit HTTP (`RagChat` 5/phút/người, Redis khi bật).
3. Admission trong PostgreSQL (một transaction, khóa hàng theo thứ tự cố định): trùng `clientMessageId` → concurrency người dùng →
   số request/ngày → token/ngày (người, toàn hệ thống) → concurrency toàn cục → ngân sách USD ngày + tháng.
4. Đặt trước (reserve) chi phí tối đa trước khi gọi provider; quyết toán (settle) đúng một lần theo usage thực tế;
   không rõ có bị tính phí hay không → `UNCERTAIN` (giữ nguyên số tiền đã đặt trước cho đến khi admin review).
5. Kill switch `GenerationEnabled`.

## 5. Dữ liệu mới

| Bảng | Mục đích |
|---|---|
| `ai_usage_ledger` | Một dòng cho mỗi thao tác AI (CHAT / INGESTION): token, model, chi phí (8 chữ số thập phân), trạng thái RESERVED → SETTLED / UNCERTAIN / RELEASED, `idempotency_key` duy nhất. Không lưu câu hỏi, câu trả lời, khóa. |
| `ai_budget_period` | Bộ đếm nguyên tử theo kỳ UTC: `DAY`, `MONTH` (chat), `INGEST_MONTH` (embedding tài liệu). |
| `rag_configuration` (+cột) | `reasoning_effort`, `generation_enabled`, quota, concurrency, timeout, 3 ngân sách USD (có check constraint). |
| `rag_usage_reservation` (+cột) | `reserved_usd`, `client_message_id` (unique có lọc → chống double-click giữa các replica). |
