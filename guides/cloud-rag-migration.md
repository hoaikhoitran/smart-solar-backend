# Chuyển RAG từ Ollama local sang OpenAI — runbook

Ngày: 2026-11-17. Trạng thái: **migration DB đã áp dụng lên Supabase; image chưa build/deploy; re-index chưa chạy** (key OpenAI
hiện trả `429 insufficient_quota` — cần nạp credit trước).

## 0. Hiện trạng đã kiểm tra (read-only, 2026-11-17)

| Hạng mục | Giá trị |
|---|---|
| Migration | 29 / head `20261117131913_CloudRagCostControl` (áp dụng 2026-11-17, sau backup + restore + rehearsal) |
| `knowledge_chunk` | `vector(1024)`, 146 chunk `qwen3-embedding:0.6b`, 14 version; 56 271 ký tự |
| `knowledge_chunk_openai_1536` | rỗng (bảng 1536 cũ được đổi tên khi chuyển sang local 1024) |
| Tài liệu | 11 PUBLISHED (7 CUSTOMER, 4 INTERNAL) |
| `rag_configuration` | v2 ACTIVE `qwen3:1.7b`, prompt v1, context 3000, output 700 |
| `.env` | `Features__KnowledgeBaseEnabled=false` |
| OpenAI | `GET /v1/models/gpt-6-luna` = 200; mọi request trả phí = 429 `insufficient_quota` |

## 1. Cổng migration DB (đã thực hiện)

1. Đích: DB trong `.env` (Supabase, PostgreSQL 17.6, pgvector 0.8.2 schema `extensions`).
2. Client đang chạy: API cũ qua Supavisor + dịch vụ Supabase. Không dừng gì.
3. Preflight: 28 migration, không trùng `(version_id, embedding_model, chunk_index)`, bảng ledger chưa tồn tại.
4. Backup: `~/SmartSolarBackups/smartsolar-supabase-before-CloudRagCostControl-20261117T133140Z.dump` (+ `.sha256`, chmod 600).
5. Restore vào `pgvector/pgvector:pg17` dùng một lần: số dòng 104 bảng **trùng khớp**.
6. Áp dụng trên bản restore → kiểm tra: số dòng mọi bảng cũ không đổi; chỉ thêm 1 history + 2 bảng rỗng; constraint đổi đúng;
   cấu hình cũ nhận default hợp lệ.
7. Tương thích rolling (trong transaction rồi ROLLBACK): API cũ insert `rag_configuration` / `rag_usage_reservation` không có cột mới
   → OK (default); RAG service cũ xóa-ghi chunk theo version → OK; hai model cùng version → OK; trùng (version, model, index) → bị chặn.
8. Down rồi Up trên dữ liệu thật → số dòng giống hệt.
9. Áp dụng lên Supabase → post-check read-only: số dòng = bản rehearsal; API build mới khởi động với DB thật: `/health/ready` 200.

Rollback DB: `dotnet ef database update 20261117101818_SurveyRequestSiteCompatibility` — **bị từ chối có chủ đích** khi ledger có
dòng (lịch sử tài chính) hoặc khi một version còn chunk của hai model. Khi đó: xuất ledger, hoàn tất/hủy re-index trước.

## 2. Các bước còn lại (theo thứ tự; chưa thực hiện)

### Bước 1 — OpenAI
- Nạp credit; tạo **project key riêng** cho SmartSolar, đặt hard monthly limit ở OpenAI (lớp bảo vệ thứ hai ngoài ngân sách của API).
- Smoke test có giới hạn chi phí (≤ 0.002 USD):
  `RAG_LIVE_OPENAI_SMOKE=1 RAG_EMBEDDING_API_KEY=… RAG_LLM_API_KEY=… .venv/bin/python -m pytest tests/test_cloud_provider.py -k live`

### Bước 2 — Build image (không push cho đến khi được duyệt)
API và RAG service từ cây mã này; gắn tag theo commit.

### Bước 3 — Deploy RAG service ở chế độ chuyển tiếp (Ollama vẫn chạy)
```
RAG_ENV=production
RAG_VECTOR_STORE=pgvector
RAG_EMBEDDING_PROVIDER=openai
RAG_EMBEDDING_MODEL=text-embedding-3-small
RAG_EMBEDDING_DIMENSION=1024
RAG_EMBEDDING_BASE_URL=https://api.openai.com/v1
RAG_EMBEDDING_API_KEY=<project key>
RAG_QUERY_EMBEDDING_PROVIDER=ollama              # câu hỏi vẫn dùng model cũ trong lúc re-index
RAG_QUERY_EMBEDDING_MODEL=qwen3-embedding:0.6b
RAG_QUERY_EMBEDDING_BASE_URL=http://ollama:11434
RAG_LLM_PROVIDER=openai
RAG_LLM_MODEL=gpt-6-luna
RAG_LLM_BASE_URL=https://api.openai.com/v1
RAG_LLM_API_KEY=<project key>
RAG_LLM_ALLOWED_MODELS=gpt-6-luna
RAG_MAX_CONTEXT_TOKENS=2000
RAG_MAX_OUTPUT_TOKENS=300
RAG_MAX_CONCURRENT_GENERATIONS=3
```
Vẫn dùng `--profile rag` (Ollama còn cần cho embedding câu hỏi). `/health/ready` kiểm tra cả hai embedder.

### Bước 4 — Deploy API
`Rag__EmbeddingModel=text-embedding-3-small`; giữ mặc định chi phí (xem rag-security-and-cost-control.md). Thứ tự an toàn: API mới
tương thích với RAG service cũ và mới (chỉ gửi `reasoningEffort` khi được cấu hình).

### Bước 5 — Cấu hình hiệu lực mới
Admin tạo draft: `generationModel=gpt-6-luna`, `reasoningEffort=none`, `maxContextTokens=2000`, `maxOutputTokens=300`, prompt v2 → validate → activate.
Cấu hình v2 hiện tại (`qwen3:1.7b`) không phải model khả dụng trong registry cloud → dashboard báo `MODEL_NOT_USABLE` cho đến bước này.

### Bước 6 — Re-index (trả phí, đã kiểm ngân sách)
`POST /api/admin/rag-configuration/reindex-stale` → phản hồi có `estimatedCostUsd` và `remainingIngestionBudgetUsd`; API **từ chối**
(`AI_BUDGET_EXCEEDED`) nếu ước lượng vượt ngân sách. Ước lượng cho dữ liệu hiện tại:

| | Ký tự | Token | USD |
|---|---|---|---|
| Cận trên mà API đặt trước (kích thước file × 4) | 604 952 | ~201 700 | **~0.0040** |
| Kỳ vọng thực tế (nội dung chunk) | 56 271 | ~19–23 nghìn | ~0.0005 |
| Ngân sách ingestion tháng | | | 0.50 |

Theo dõi: `GET /api/admin/rag-usage/by-operation`, job ingestion READY cho cả 14 version. Có thể chạy lại bất kỳ lúc nào (idempotent).

### Bước 7 — Chuyển câu hỏi sang model mới
Kiểm tra mọi version PUBLISHED có chunk `text-embedding-3-small`:
```sql
SELECT v.id FROM knowledge_document_version v JOIN knowledge_document d ON d.id = v.document_id
WHERE d.status = 'PUBLISHED' AND NOT EXISTS (SELECT 1 FROM knowledge_chunk c WHERE c.version_id = v.id AND c.embedding_model = 'text-embedding-3-small');
```
Kết quả rỗng → bỏ trống 4 biến `RAG_QUERY_EMBEDDING_*` → khởi động lại RAG service (`--profile rag-cloud`). Chạy eval:
`EVAL_MODE=live … bench/eval_rag.py` (giới hạn 0.05 USD, xem rag-performance-report.md).

### Bước 8 — Dọn dẹp (chỉ sau khi đã xác minh, có duyệt riêng)
- Dừng Ollama (`--profile rag-cloud` không khởi động nó). **Không xóa volume `ollama_models`** (rollback nhanh).
- Chunk `qwen3-embedding:0.6b` cũ: giữ ít nhất một chu kỳ phát hành làm đường lui; xóa sau bằng
  `DELETE FROM knowledge_chunk WHERE embedding_model = 'qwen3-embedding:0.6b'` chỉ khi có duyệt và đã backup.

## 3. Rollback ứng dụng

| Tình huống | Hành động |
|---|---|
| Chất lượng/độ trễ kém sau bước 7 | Đặt lại `RAG_QUERY_EMBEDDING_*` = Ollama (chunk cũ còn nguyên) + `RAG_LLM_*` = Ollama; rollback cấu hình về v2. |
| Chi phí bất thường | `PUT /api/admin/rag-budget` `generationEnabled=false` (hiệu lực trong ≤ `ConfigurationCacheSeconds`=10 s). |
| API mới lỗi | Deploy lại image API cũ: schema tương thích ngược (đã kiểm tra ở cổng 7). |
