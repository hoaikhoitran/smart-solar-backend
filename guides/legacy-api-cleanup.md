# Dọn dẹp API và thành phần cũ — kiểm kê (2026-11-17)

Nguyên tắc: không xóa API chỉ vì cũ; chỉ xóa khi chứng minh không còn bên gọi. Không xóa schema/dữ liệu khi chưa chứng minh mọi dữ liệu
và consumer đã lỗi thời.

## 1. 14 route đã ngừng (trả 410 `ENDPOINT_RETIRED` kèm `details.replacement`) — **GIỮ LẠI**

Nguồn: `src/SmartSolar.Api/Controllers/QuotationControllers.cs` (ngừng bởi luồng báo giá/hợp đồng/thanh toán đơn giản hóa).

| Route | FE còn tham chiếu (origin/main d8cc550) |
|---|---|
| `POST /api/survey-requests/{id}/quotations` | `services/salesQuotationService.ts` |
| `POST /api/survey-requests/{id}/quotations/generate` | `endpoints.ts` `salesQuotations.generate`, `salesQuotationService.ts` |
| `GET /api/quotations/pending-review` | `endpoints.ts`, `hooks/useSalesQuotations.ts`, `salesQuotationService.ts` |
| `PUT /api/quotations/{id}` | `salesQuotationService.ts` |
| `POST /api/quotations/{id}/lines` | `endpoints.ts` `lines`, `salesQuotationService.ts` |
| `PUT /api/quotations/{id}/lines/{lineId}` | `endpoints.ts` `line` |
| `DELETE /api/quotations/{id}/lines/{lineId}` | `endpoints.ts` `line` |
| `POST /api/quotations/{id}/issue` | `endpoints.ts` `issue` |
| `POST /api/quotations/{id}/confirm-survey-version` | `endpoints.ts`, `useSalesQuotations.ts`, `salesQuotationService.ts` |
| `POST /api/quotations/{id}/pricing-approval-request` | `endpoints.ts`, `salesQuotationService.ts` |
| `POST /api/quotations/{id}/revisions` | `salesQuotationService.ts` |
| `PUT /api/quotations/{id}/document` | `endpoints.ts` `document` |
| `GET /api/manager/quotations/pricing-approvals` | `config/portals.ts`, `manage-layout.tsx`, `protectedRoutes.tsx`, `paths.ts`, `useManagerQuotations.ts`, `managerQuotationService.ts` (trang `/manage/pricing-approvals`) |
| `POST /api/manager/quotations/{id}/pricing-approval` | `managerQuotationService.ts` |

**Quyết định: giữ cả 14 route ở dạng 410.** FE vẫn gọi chúng; xóa route sẽ biến 410 có hướng dẫn thay thế thành 404 khó hiểu trên một
luồng FE đang tồn tại. **Phụ thuộc chặn:** FE gỡ các tham chiếu trên (đặc biệt menu/trang `/manage/pricing-approvals`). Sau khi FE phát
hành bản đã gỡ và log truy cập 30 ngày không còn request tới các route này → xóa route + test tương ứng trong một thay đổi riêng.

## 2. Migration tương thích `20261117101818_SurveyRequestSiteCompatibility` — **GIỮ LẠI**

Trigger cho API cũ (rolling deploy trên Supabase). Không xóa chỉ vì đã có image API mới; chỉ gỡ (bằng migration mới) khi mọi instance API
cũ đã dừng và được xác nhận.

## 3. Thành phần RAG cũ

| Thành phần | Trạng thái | Quyết định |
|---|---|---|
| Provider Ollama trong RAG service (`RAG_*_PROVIDER=ollama`) | còn dùng cho query embedding trong giai đoạn chuyển tiếp và làm đường lui | GIỮ |
| Service `ollama`, `ollama-models`, volume `ollama_models` (compose `--profile rag`) | production đang chạy | GIỮ; không dừng, không xóa volume. Cloud dùng `--profile rag-cloud` |
| Chunk `qwen3-embedding:0.6b` (146) | đang phục vụ | GIỮ đến sau bước 7 của cloud-rag-migration.md + một chu kỳ phát hành |
| Bảng `knowledge_chunk_openai_1536` (rỗng) | được `knowledge_chunk_local_1024_rollback.sql` dùng | GIỮ (rollback script phụ thuộc) |
| `rag_usage_reservation` / `rag_usage_daily` | vẫn là cơ chế quota/concurrency; ledger mới bổ sung, không thay thế | GIỮ |
| Mô hình `gpt-4.1-mini`, `gpt-4.1`, `gpt-4o-mini` trong registry | `Enabled=false`, không giá | GIỮ trong danh sách (cấu hình lịch sử tham chiếu chúng) |
| `bench/bench_local_rag.py` (Ollama) | công cụ đo | GIỮ |

## 4. Không có gì bị xóa trong đợt này

Không route, bảng, cột, migration hay volume nào bị xóa. Thay đổi schema duy nhất là migration cộng thêm `CloudRagCostControl`
(và đổi khóa duy nhất của `knowledge_chunk` sang khóa rộng hơn, không đổi dữ liệu).
