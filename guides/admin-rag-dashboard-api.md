# Admin RAG — API dashboard chi phí và ngân sách

Tất cả endpoint: **chỉ Admin** (role khác 403, không đăng nhập 401), yêu cầu feature `KnowledgeBase`, rate limit `RagAdminRead`
(GET) / `RagAdminConfig` (ghi). Phản hồi theo envelope chuẩn `{ isSuccess, data, error, traceId }`. Tiền tệ: USD, số thập phân
tới 8 chữ số. Ngày: UTC `yyyy-MM-dd`.

## Tham số chung

`from`, `to` (DateOnly, tùy chọn): mặc định `to` = hôm nay (UTC), `from` = ngày 1 của tháng chứa `to`; tối đa **92 ngày**; `from > to` → 400.
Tối đa 200 000 dòng ledger trong một khoảng (vượt → 400, thu hẹp khoảng).

## Endpoint

| Method | Path | Trả về |
|---|---|---|
| GET | `/api/admin/rag-usage/summary` | `AiUsageSummaryView` |
| GET | `/api/admin/rag-usage/timeseries` | `AiUsageDayView[]` — một phần tử mỗi ngày (kể cả ngày 0) |
| GET | `/api/admin/rag-usage/by-model` | `AiUsageBreakdownView[]` theo model |
| GET | `/api/admin/rag-usage/by-operation` | 4 dòng: `QUERY_EMBEDDING`, `GENERATION`, `INGESTION`, `UNCERTAIN` |
| GET | `/api/admin/rag-usage/by-role` | theo role người hỏi (chat) |
| GET | `/api/admin/rag-usage/top-users?limit=10` | `AiTopUserView[]` (tên người dùng — chỉ Admin) |
| GET | `/api/admin/rag-usage/requests?operation=&status=&outcome=&userId=&page=1&pageSize=20` | `AiUsagePageView` |
| POST | `/api/admin/rag-usage/{ledgerId}/review` | review một dòng `UNCERTAIN` |
| GET | `/api/admin/rag-budget` | `AiBudgetView` |
| PUT | `/api/admin/rag-budget` | `RagActivationView` (phiên bản cấu hình mới) |
| GET | `/api/admin/rag-configuration/models` | registry model (provider, enabled, usable, giá, `isDefault`) |

Giá trị lọc hợp lệ: `operation` ∈ `CHAT, INGESTION`; `status` ∈ `RESERVED, SETTLED, UNCERTAIN, RELEASED`;
`outcome` ∈ `PENDING, ANSWERED, INSUFFICIENT_CONTEXT, PROVIDER_ERROR, PROVIDER_RATE_LIMITED, TIMEOUT, BUSY, EXPIRED, INDEXED, INGEST_FAILED, REVIEWED`.
Giá trị khác → 400.

## Kiểu dữ liệu (camelCase trong JSON)

```ts
type BudgetLine = { period: string; periodStart: string; limitUsd: number; settledUsd: number; uncertainUsd: number; reservedUsd: number;
                    remainingUsd: number; usedPercent: number; requests: number; blocked: number };
type Summary = { range: {from: string; to: string}; today: BudgetLine; month: BudgetLine; ingestionMonth: BudgetLine;
  rangeCost: { settledUsd: number; uncertainUsd: number; reservedUsd: number; totalUsd: number };
  requests: number; answered: number; insufficientContext: number; failed: number; uncertain: number; blockedToday: number;
  inputTokens: number; cachedInputTokens: number; outputTokens: number; reasoningTokens: number; embeddingTokens: number;
  averageLatencyMs: number | null; p95LatencyMs: number | null; activeGenerationModel: string | null; activeEmbeddingModel: string | null;
  configVersion: number; generationEnabled: boolean };
type Day = { date: string; costUsd: number; generationCostUsd: number; embeddingCostUsd: number; ingestionCostUsd: number; requests: number;
             failed: number; inputTokens: number; outputTokens: number; embeddingTokens: number; p95LatencyMs: number | null };
type Breakdown = { key: string; requests: number; costUsd: number; inputTokens: number; outputTokens: number; embeddingTokens: number };
type TopUser = { userId: string; fullName: string | null; role: string | null; requests: number; costUsd: number; tokens: number };
type UsageRow = { id: string; operation: string; status: string; outcome: string; userId: string | null; userRole: string | null;
  embeddingModel: string | null; embeddingTokens: number; embeddingCostUsd: number; generationModel: string | null; inputTokens: number;
  cachedInputTokens: number; outputTokens: number; reasoningTokens: number; generationCostUsd: number; reservedUsd: number; costUsd: number;
  usageReported: boolean; latencyMs: number | null; configVersion: number | null; createdAt: string; settledAt: string | null;
  reviewedByUserId: string | null; reviewedAt: string | null; reviewNote: string | null };
type Page = { items: UsageRow[]; total: number; page: number; pageSize: number };
type Alert = { code: string; severity: 'info' | 'warning' | 'critical'; message: string };
type Budget = { activeVersion: number; bootstrap: boolean; generationEnabled: boolean; dailyBudgetUsd: number; monthlyBudgetUsd: number;
  monthlyIngestionBudgetUsd: number; userRequestsPerDay: number; userConcurrency: number; globalConcurrency: number;
  maxQuestionCharacters: number; generationTimeoutSeconds: number; ceilings: object /* RagConfigurationLimitsView: *Min / *Max */;
  today: BudgetLine; month: BudgetLine; ingestionMonth: BudgetLine; alerts: Alert[];
  activeGenerationModel: string | null; activeEmbeddingModel: string | null };
```

Mã cảnh báo: `DAILY_BUDGET_80`, `DAILY_BUDGET_EXHAUSTED`, `DAILY_UNCERTAIN`, `MONTHLY_*`, `INGESTION_*`, `GENERATION_DISABLED`, `MODEL_NOT_USABLE`.

## Ghi

**Đổi ngân sách / giới hạn** — chỉ gửi trường muốn đổi; `expectedActiveVersion` bắt buộc (optimistic concurrency):
```json
PUT /api/admin/rag-budget
{ "expectedActiveVersion": 3, "dailyBudgetUsd": 0.5, "monthlyBudgetUsd": 4, "monthlyIngestionBudgetUsd": 0.5,
  "userRequestsPerDay": 30, "userConcurrency": 1, "globalConcurrency": 3, "maxQuestionCharacters": 1000,
  "generationTimeoutSeconds": 30, "generationEnabled": true, "notes": "Giảm ngân sách cuối tháng" }
```
400 khi vượt trần / thiếu `expectedActiveVersion`; 409 khi phiên bản active đã đổi (tải lại rồi thử lại). Thành công: phiên bản mới
được kích hoạt ngay (cache cấu hình ≤ 10 s).

**Review chi phí không chắc chắn**:
```json
POST /api/admin/rag-usage/{ledgerId}/review
{ "costUsd": 0.00031, "note": "Đối chiếu billing OpenAI ngày 2026-11-20" }
```
`costUsd` 0–100 (≤ 6 chữ số thập phân) và `note` 1–500 ký tự, sai → 400. Không có dòng `UNCERTAIN` với id này (không tồn tại hoặc đã
review) → 404 `AI_USAGE_NOT_FOUND`. Kết quả: `status` = `SETTLED` (costUsd > 0) hoặc `RELEASED` (0), `outcome` = `REVIEWED`; bộ đếm kỳ
chuyển từ uncertain sang settled; lưu người review, thời điểm và ghi chú.

## Kiểm thử

`tests/SmartSolar.Tests/Integration/KnowledgeBase/AdminRagUsageEndpointsTests.cs` (qua pipeline HTTP thật): phân quyền 10 endpoint × 5 role +
anonymous; validation khoảng ngày / filter; trần ngân sách; 409 version cũ; thay đổi tạo version mới; không lộ bí mật.
