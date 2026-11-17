# Báo cáo hiệu năng và đánh giá RAG (2026-11-17)

## 1. Tóm tắt

| Hạng mục | Kết quả | Trạng thái |
|---|---|---|
| Eval 50 câu — offline (pipeline, ACL, injection) | hard check 0 lỗi | IMPLEMENTED AND VERIFIED |
| Eval 50 câu — live OpenAI (chất lượng, latency, chi phí thật) | không chạy: key trả `429 insufficient_quota` | **BLOCKED** |
| Chi phí tối đa của eval live | 0.0303 USD ≤ trần 0.05 (kiểm trước khi chạy, fail closed) | VERIFIED (tính toán) |
| Overhead admission / settlement trên PostgreSQL | kiểm tra đúng đắn dưới tải đua; chưa đo latency trên Supabase (cần ghi) | NOT MEASURED |

## 2. Bộ đánh giá `src/SmartSolar.RagService/bench/eval_rag.py`

50 câu tiếng Việt: 30 trong phạm vi (24 khách hàng, 6 nhân viên với tài liệu INTERNAL), 8 câu hỏi nội bộ do khách hàng hỏi (ACL),
6 ngoài phạm vi, 6 prompt injection. Đi qua endpoint ký HMAC `POST /internal/v1/answer` giống API thật.
Báo cáo JSON chỉ có cờ, số đếm, latency, token, chi phí — không có nội dung câu trả lời.

### Offline (đã chạy) — corpus tổng hợp, hashing embedder, extractive model, không mạng

```
cd src/SmartSolar.RagService && .venv/bin/python bench/eval_rag.py
```

| Chỉ số | Kết quả | Loại |
|---|---|---|
| Rò rỉ ACL (khách hàng trích tài liệu INTERNAL) | **0 / 8** | HARD |
| Injection bị chặn (không lộ system prompt / bí mật / tài liệu nội bộ) | **6 / 6** | HARD |
| Trích dẫn ngoài danh sách version được phép | **0 / 50** | HARD |
| HTTP lỗi | 0 | HARD |
| Không có bằng chứng → không gọi generation | 13 câu | — |
| Hit trong phạm vi (grounded + trích đúng tài liệu) | 29 / 30 | chỉ minh họa (hashing ≠ embedding thật) |
| Từ chối ngoài phạm vi | 5 / 6 | chỉ minh họa |

Các chỉ số chất lượng offline **không** đại diện cho chất lượng thật (embedding hashing là từ vựng thuần túy).

### Live (BLOCKED) — lệnh để chạy khi có credit

```
EVAL_MODE=live EVAL_URL=http://127.0.0.1:8000 RAG_SHARED_SECRET=… \
EVAL_VERSIONS='[{"versionId":"…","documentId":"…","title":"…","versionNumber":1,"audience":"CUSTOMER"}, …]' \
EVAL_INPUT_USD_PER_M=0.10 EVAL_OUTPUT_USD_PER_M=0.50 EVAL_EMBEDDING_USD_PER_M=0.02 EVAL_MAX_USD=0.05 \
.venv/bin/python bench/eval_rag.py
```
Thiếu giá → từ chối (exit 2). Trước mỗi câu: `đã chi + chi phí tối đa của câu` > trần → bỏ qua câu đó. Đã kiểm: trần 0.01 → từ chối
trước khi gọi (worst case 0.0303).

## 3. Ước lượng chi phí vận hành (gpt-6-luna, giá 2026-11-17)

| Thao tác | Token (ước lượng) | USD |
|---|---|---|
| Câu hỏi, cận trên (1000 ký tự, đầy context 2000, lịch sử tối đa, output 300) | ~4 870 in + 300 out | ≈ 0.00064 |
| Câu hỏi điển hình (100 ký tự, ~1 200 token context, không lịch sử) | ~2 400 in + ~150 out | ≈ 0.0003 |
| Không có bằng chứng (chỉ embedding câu hỏi) | ~40 | ≈ 0.0000008 |
| Re-index toàn bộ 14 version hiện có | ~19–23 nghìn (cận trên đặt trước ~201 700) | ≈ 0.0005 (cận trên 0.0040) |

Ngân sách mặc định 1.00 USD/ngày ⇒ ≥ ~1 500 câu/ngày ở cận trên; 4.00 USD/tháng ⇒ ~6 000 câu/tháng ở cận trên, ~13 000 điển hình.
Trần thực tế nhỏ hơn là quota 30 câu/người/ngày.

## 4. Latency

- Trước đây (Ollama `qwen3:1.7b` CPU, VPS 3 vCPU — các bench 2026-11-17 trong `guides/rag-local-ollama.md`): generation hàng chục giây,
  hết hạn 503 khi có tải. Cloud loại bỏ giới hạn CPU cục bộ; latency kỳ vọng do mạng + provider, **chưa đo** (BLOCKED cùng eval live).
- Trên đường đi của API, mỗi câu hỏi thêm: 1 transaction admission + 1 transaction settlement (khóa vài hàng nhỏ, quét hết hạn ≤ 100 dòng).
  Bộ test PostgreSQL (20 caller đua) chạy trong vài giây gồm cả khởi tạo; số đo trên Supabase cần ghi nên chưa thực hiện.
- Log mỗi câu: `contextMs`, `retrievalMs`, `generationMs`, chi phí, phiên bản cấu hình → dùng để đo sau khi bật.

## 5. Việc cần làm khi có credit

1. Smoke test cloud (≤ 0.002 USD), 2. re-index (≤ 0.004 USD), 3. eval live (≤ 0.05 USD), 4. ghi p50/p95 từ `summary`/`timeseries`.
