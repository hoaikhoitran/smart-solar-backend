# SmartSolar — Full Project Production Audit (2026-11-16)

Phạm vi: Backend `smart-solar-backend` nhánh `main` @ `5476dba` (working tree sạch lúc bắt đầu), Frontend web `smart-solar` @ `9bbb45b`
(working tree có 43 thay đổi chưa commit từ phiên trước, audit trên working tree), RAG service (Python, cùng repo). Mobile: **NOT AUDITED**
(không có source). Production / VPS / Supabase: **không truy cập**; mọi kết luận về môi trường thật dựa trên cấu hình trong repo và tài liệu
đã ghi, nên được đánh dấu `[Chưa xác định]` khi cần dữ liệu thật.

Không sửa source, không tạo migration, không ghi vào database ứng dụng. Kiểm thử chạy trên container PostgreSQL 17, Redis 7 và MinIO-fork
dùng một lần, đã xoá sau audit. File thêm vào repo trong audit: tài liệu này và `guides/sql/supabase-acl-rls-audit-readonly.sql`
(SQL chỉ đọc). Hai test tái hiện tạm thời được chạy rồi xoá (không còn trong repo).

Ký hiệu trạng thái: `[Đã xác định]` có bằng chứng source/test; `[Trung lập]` đúng như thiết kế nhưng có đánh đổi; `[Chưa xác định]` cần dữ
liệu môi trường thật hoặc quyết định chưa có.

---

## 0. Remediation 2026-11-16 (sau audit) — trạng thái hiện tại

Làm trên working tree `main` @ `5476dba`, **chưa commit**. Không migration mới, không đổi shape request/response. Chi tiết các mục
bên dưới thay thế trạng thái trong §4–§10 khi khác nhau.

| ID | Trước | Sau | Thay đổi (file:line) | Regression test |
|---|---|---|---|---|
| SEC-01 | P1 OPEN | **FIXED** (repo) | `docker-compose.pull.yml`: `ASPNETCORE_ENVIRONMENT: ${ASPNETCORE_ENVIRONMENT:-Production}`; `Program.cs:117-128` Swagger theo `Swagger:Enabled` (mặc định chỉ Development), `UseHsts()` ngoài Development; `appsettings.json` `Swagger:Enabled=false`, `appsettings.Development.json` `true` | `ProductionHardeningTests.Production_hides_swagger_sends_hsts_on_https_and_still_answers_the_health_probes`, `Swagger_follows_the_explicit_switch_and_development_keeps_it_on`; container thật: Swagger 404 + HSTS qua proxy tin cậy |
| SEC-02 | P1 OPEN | **FIXED trong repo / BLOCKED trên VPS** | `docker-compose.pull.yml`: API publish `${API_BIND_ADDRESS:-127.0.0.1}:8080`; service `edge` (Caddy 2.8.4, profile `tls`, HTTPS tự động cho `PUBLIC_API_HOST`) trên network `edge` subnet cố định; `HttpsRedirection__HttpsPort` (HTTP trực tiếp → 307 HTTPS) | Container thật: cổng chỉ `127.0.0.1:8080`; HTTP trực tiếp 307; qua proxy tin cậy 200 + HSTS. **Chưa kiểm được proxy/TLS thật trên VPS** (không có quyền truy cập) |
| SEC-07 (mới, phát hiện khi viết test) | — | **FIXED** | ASP.NET Core bỏ qua kiểm tra nguồn khi `KnownProxies` và `KnownNetworks` đều rỗng → **mọi client giả mạo được `X-Forwarded-For`** với cấu hình mặc định (`KnownProxies: []`), lấy bucket rate limit mới mỗi request (login, forgot-password, ingest). Kết luận "không giả mạo được" ở §4 của audit là **sai**. Sửa: `ForwardedHeadersExtensions.cs:34-38` tắt forwarding khi không có proxy tin cậy; `:42` `ForwardLimit = 1`; `KnownNetworks` (CIDR, kiểm tra hợp lệ lúc khởi động); `Program.cs:111` `UseForwardedHeaders()` là middleware đầu tiên (trước đây chạy sau HTTPS redirection) | `A_client_cannot_escape_the_login_limit_by_spoofing_x_forwarded_for` (FAIL trước khi sửa, PASS sau), `Behind_a_trusted_proxy_each_client_gets_its_own_login_budget` (×2), `Invalid_trusted_proxy_settings_stop_the_api_at_startup`; container thật: X-Forwarded-Proto giả từ container không tin cậy bị bỏ qua |
| RL-03 | P2 | **FIXED** (giả mạo IP) / **OPEN** (giới hạn theo tài khoản: cần quyết định ngưỡng) | như SEC-07 + `.env.example` hướng dẫn `KnownProxies` khi proxy chạy trên host | như SEC-07 |
| RL-01 | P2 | **FIXED** | `IotMonitoringModuleExtensions.cs:56` global limiter chỉ cho endpoint ingest, theo IP nguồn, mặc định 60/phút (`RateLimiting:IotTelemetryIngestPerIp`, cấu hình được — giá trị là giả định 5 thiết bị/NAT, chưa đo) cộng với 12/phút/thiết bị | `Rotating_device_ids_hits_the_per_ip_ceiling` (5 × 401 rồi 429), `A_real_device_sending_every_10_seconds_is_never_throttled_by_the_production_limits` (12/12 × 201, lần 13 → 429) |
| RL-02 | P2 | **FIXED** | `IotMonitoringControllers.cs:164-165` `[RequestSizeLimit(1024)]` + `[BoundedRequestBody(1024)]` (`BoundedRequestBodyAttribute.cs`: 411 khi không có `Content-Length`, 413 khi > 1 KiB, trước model binding) | `The_ingest_body_is_refused_before_it_is_read_when_too_large_or_of_unknown_length` (8 MB → 413, 1 KiB+ → 413, payload thật 201, "abc" vẫn 400, key sai 401), `The_bounded_body_filter_refuses_unknown_or_oversized_lengths` (×4) |
| INT-01 | P2 | **FIXED** | `AuthLinkEmailDelivery.cs` (mới): lỗi SMTP → trả dòng outbox về PENDING kèm URL, backoff, ack message (không vào error queue, URL không nằm lại broker); `ParseException` ghi nhận, không retry; `PasswordResetDispatchStore.ReturnForRetryAsync:27`; worker phát lại tới khi link hết hạn | `An_smtp_outage_longer_than_the_broker_retry_hands_the_row_back_and_ends_in_exactly_one_email` (6 lần lỗi trong ~10 phút → đúng 1 email, bản sao cũ bị bỏ), `An_outage_longer_than_the_link_lifetime_expires_the_row_and_erases_the_url`, `The_consumer_records_sent_skips_redeliveries_and_superseded_rows_and_never_retries_an_invalid_recipient` |
| INT-02 | P2 | **FIXED** | Đăng ký (`RegisterHandler.cs:111`), gửi lại xác thực (`ResendVerificationHandler.cs:88`), link Admin (`AdminUserHandlers.cs:445`) dùng chung outbox `password_reset_dispatch`; loại email lấy từ `auth_action_token.type` (`PasswordResetDispatchStore.cs:22`) → **không cần migration**; consumer xác thực / thiết lập tài khoản dùng `AuthLinkEmailDelivery` | `Verification_emails_of_register_and_resend_survive_a_broker_outage_and_an_smtp_outage`, `An_admin_setup_link_created_while_the_broker_is_down_is_queued_and_published_later` |
| SEC-03 | P2 | **FIXED** (project ship ra production) | `SmartSolar.Infrastructure.csproj:26` pin `System.Security.Cryptography.Pkcs` 10.0.15 (advisory: CVE-2026-84008 High, CVE-2026-70162 Moderate; vá ở 10.0.13; dòng 10.0.x tương thích net8.0, MimeKit cần ≥ 10.0.0) | `dotnet list package --vulnerable`: Api/Infrastructure/Modules sạch. Còn `System.Net.Http 4.3.0`, `System.Text.RegularExpressions 4.3.0` trong **project test** (không ship) — OPEN P3 |
| DEP-01 | P2 | **FIXED** | `restart: unless-stopped` cho api, rabbitmq, redis, edge, rag-service, ollama | Container thật: `RestartPolicy=unless-stopped`; `docker restart` → healthy lại sau ~9 s |
| DEP-02 | P2 | **FALSE POSITIVE (sửa báo cáo)** + healthcheck **FIXED** | `/health/live`, `/health/ready` **đã tồn tại** (`SchemaReadinessMiddleware.cs:85-104`; audit grep `MapHealthChecks` nên bỏ sót). Thêm healthcheck compose cho API gọi `/health/ready` bằng bash `/dev/tcp` (image không có curl) | Container thật: `healthy` sau ~9 s; `/health/ready` 200 |
| DEP-08 | P3 | **Không cần** | Không có code dùng DataProtection (không cookie auth, antiforgery hay `IDataProtector`) → không cần lưu key | grep |
| DB-01, DB-03, DB-04, INT-03, RL-04, SEC-04, SEC-05, SEC-06, DEP-03..07, FE-01 | — | **OPEN** (ngoài phạm vi ưu tiên của lần sửa này) | — | — |

**Hành vi thay đổi (tương thích ngược):** không đổi shape API. Admin `activation.emailDelivery` nay luôn `QUEUED` khi link được lưu
(`PUBLISH_FAILED` giữ trong enum nhưng không còn trả về). Email liên kết chỉ được publish khi môi trường có RabbitMQ + SMTP
(`IEmailDeliveryStatus.CanDeliver`); trước đây vẫn publish vào publisher rỗng. IoT ingest có thêm 411/413 và 429 theo IP. Thiết bị gọi
thẳng HTTP vào cổng API nhận 307 → HTTPS.

**Rủi ro còn lại:** gửi trùng email vẫn có thể xảy ra (at-least-once) nếu SMTP nhận email nhưng ghi SENT lỗi; lỗi database trong lúc
consumer xử lý vẫn đi theo retry MassTransit → error queue (message chứa URL); ngưỡng 60/phút/IP cho ingest là giả định chưa đo; plain
`docker compose` không tự restart container `unhealthy` (chỉ restart khi crash); `/health/ready` phản ánh schema, không phản ánh DB đang
sống sau khi đã ready.

**Test sau sửa:** `dotnet restore` OK, `dotnet build` 0 warning, toàn bộ **1 655 test: 1 655 PASS, 0 FAIL, 0 SKIP** (PostgreSQL 17 +
Redis 7 + S3-compatible dùng một lần; 223 s; Unit 1 271, Integration 373, E2E 11; 89 test PostgreSQL, 12 Redis/S3). Trước sửa: 1 638
tổng. Container thật (image build local, không push): compose `docker-compose.pull.yml` với `.env` giả lập → healthy, Production,
loopback-only, restart. **NFR vẫn NOT VERIFIED.** Không xem là production-ready chỉ vì test PASS.

**Checklist cấu hình VPS (cần làm trên VPS — BLOCKED cho tới khi có quyền truy cập):**
1. Kiểm tra VPS đang có reverse proxy hay không: `sudo ss -ltnp | grep -E ':(80|443|8080)\b'`, `systemctl status nginx caddy`.
2. **Không có proxy:** đặt `PUBLIC_API_HOST=<tên miền API>` trong `.env`, trỏ DNS A/AAAA về VPS, mở 80/443 (UFW), chạy
   `docker compose -f docker-compose.pull.yml --profile tls up -d`; giữ mặc định `EDGE_SUBNET`/`EDGE_PROXY_IP` (hoặc đổi cả hai nếu trùng mạng).
3. **Có Nginx/Caddy trên host:** proxy tới `http://127.0.0.1:8080`, gửi `X-Forwarded-For` (ghi đè, không nối) và `X-Forwarded-Proto`,
   đặt `ForwardedHeaders__KnownProxies__0=<IP gateway của network compose mà API thấy>` (xem `docker network inspect`), bật HSTS và TLS ở proxy.
4. `.env`: không đặt `ASPNETCORE_ENVIRONMENT=Development`; `Swagger__Enabled` để trống / false; kiểm tra `Frontend__*`, `Cors__AllowedOrigins__*`
   là URL HTTPS thật; `API_IMAGE_TAG` ghim tag (không `latest`).
5. Kiểm chứng từ máy ngoài VPS: `curl -sI http://<IP>:8080/health/live` → không kết nối được; `curl -sI https://<domain>/health/ready` → 200
   và có `Strict-Transport-Security`; `curl -s -o /dev/null -w '%{http_code}' https://<domain>/swagger/index.html` → 404.
6. Gửi 3 lần `POST /api/auth/login` sai với `X-Forwarded-For` khác nhau từ cùng máy: sau `RateLimiting__Login__PermitLimit` lần phải 429.
7. ESP32: đổi URL sang `https://<domain>/api/iot/devices/<id>/readings` (HTTP sẽ nhận 307), nạp CA của chứng chỉ.
8. Theo dõi `docker inspect -f '{{.State.Health.Status}}'`; cân nhắc giám sát ngoài (uptime check `/health/ready`) vì compose không tự restart khi unhealthy.

---

## 1. Executive Summary

**Kết luận: CONDITIONAL GO** — không có P0 được chứng minh; có 2 P1 về cấu hình triển khai phải sửa trước khi mở công khai, cùng các P2 về
độ bền tích hợp và giới hạn tần suất.

Căn cứ:

* **Toàn bộ test tự động PASS**: 1 638 test (1 633 PASS, 0 FAIL, 5 SKIP) trên PostgreSQL 17 dùng một lần; 5 test SKIP (Redis, S3) được chạy
  riêng trên Redis 7 và S3-compatible thật dùng một lần: 13/13 PASS. Không còn test nào chưa chạy.
* Kiểm soát cốt lõi có bằng chứng: phân quyền theo chủ sở hữu ở mọi luồng đã kiểm (khảo sát, mô phỏng, thanh toán, after-sales, file, chat,
  IoT theo công trình), JWT có kiểm `AuthVersion` mỗi request, refresh token xoay vòng + phát hiện replay, webhook PayOS HMAC so sánh hằng
  thời gian + chống trùng bằng hash, toàn vẹn thanh toán bằng unique/partial index trong PostgreSQL, outbox email đặt lại mật khẩu.
* **P1 chặn mở công khai** (cấu hình, sửa nhanh):
  * `SEC-01` Compose triển khai chạy `ASPNETCORE_ENVIRONMENT=Development` → Swagger UI và cấu hình Development được bật trên production.
  * `SEC-02` API publish `0.0.0.0:8080` bằng HTTP, không HSTS; TLS/reverse proxy không có trong repo → không chứng minh được HTTPS.
* **NFR: tất cả NOT VERIFIED** (không có k6/đo tải trên môi trường tương đương).
* Quyết định business còn mở (không tự quyết): xử lý tiền đã thu không áp dụng được (ledger `refund_record PENDING_APPROVAL`), ngưỡng cảnh
  báo sản lượng (D-08), thời gian lưu telemetry IoT, nguyên nhân thật của lỗi upload ảnh khảo sát trên production.

**Phạm vi đã đạt / chưa đạt (trung thực):** inventory 100% endpoint (301) và 100% rate-limit được tạo tự động từ source; bảo mật, rate limit,
database, tích hợp, triển khai được review tĩnh có mục tiêu + test. Business logic của 7 luồng được đối chiếu qua tài liệu đã duyệt, các
handler rủi ro cao và bộ test sẵn có; **không** đọc từng dòng mọi handler trong phiên này — vì vậy báo cáo không tự gọi là review line-by-line
toàn bộ; các vùng chỉ dựa vào test được ghi rõ ở §2.

---

## 2. Audit Coverage Matrix

| Module / nhóm endpoint | Endpoint | Cách audit | Test liên quan (đã chạy) | Kết quả |
|---|---|---|---|---|
| Identity & Auth (`AuthController`, `AdminUsersController`) | 20 | Review sâu handler login/refresh/reset/change/forgot/outbox, JWT, rate limit | Unit Identity, Integration Auth (+ revocation, outbox) | PASS; INT-01, INT-02, RL-03 |
| Pre-survey, Sites, Customers | 20 | Review controller + rate limit; test ownership | PreSurvey unit/endpoint | PASS; RL-04 |
| Solar Simulation (PVGIS, NASA POWER) | 5 | Review `SimulationAccess`, controller | SimulationEndpoints, CreateSimulationHandler, SimulationAccess | PASS; provider thật NOT VERIFIED |
| Scheduling, Zones, Dispatch | ~60 | Dựa test + review rate limit | Scheduling unit/endpoint, Postgres concurrency | PASS (test); review tĩnh một phần |
| On-site survey, Quotation, Contract, Documents | ~45 | Review ownership contract URL, quotation policy qua test | Quotation/Contract unit, ContractDocumentEndpoints, Postgres | PASS |
| Payments / PayOS | 7 | Review sâu webhook, idempotency, DB constraints | PaymentTests, PostgresPaymentIntegrity | PASS |
| Installation (projects, evidence, assets, substitution, handover) | ~40 | Dựa test + review lô thiết bị (phiên trước) | Installation*, AssetIntegrity, E2E lifecycle, Postgres | PASS |
| After-sales | ~45 | Review ownership customer; luật no-refund | AfterSales unit/endpoint, Postgres concurrency | PASS; BUS-01 mở |
| Files / S3 | 6 | Review owner/staff, presign; S3 chain thật | Files unit/endpoint, S3CompatibleUploadChain (MinIO-fork) | PASS; SEC-04 |
| Knowledge base / RAG chat | ~20 | Review lọc audience + kiểm citation sau sinh | KnowledgeBase unit/endpoint | PASS; INT-03 |
| Energy monitoring (simulated + measured) | 2 (+7 IoT read) | Review mapping NOT_AVAILABLE | IoT unit/endpoint/E2E | PASS |
| IoT (device, binding, ingest) | 16 | Review sâu + 2 test tái hiện | IoT unit/endpoint/E2E/Postgres race | PASS; RL-01, RL-02 |
| System settings, Admin products, Admin users | ~20 | Review RBAC + rate limit | Admin/Catalog/SystemSettings | PASS |
| Rate limiting (toàn hệ thống) | 301 | Inventory tự động 100% (§5, Phụ lục A) | RateLimitPolicyContract, DistributedRateLimiting (Redis thật) | RL-01..RL-04 |
| Database / migrations / Supabase ACL | 25 migration | Review + tái hiện local Supabase-like | Migration unit, M1–M10 Up/Down Postgres | DB-01..DB-04 |
| Deployment (Dockerfile, Compose) | — | Review tĩnh | — | SEC-01, SEC-02, DEP-01..DEP-05 |
| Frontend web | — | Review có mục tiêu: lưu token, refresh 401, guard role, đổi mật khẩu | FE test không chạy trong audit này | FE-01, SEC-06; còn lại NOT AUDITED |
| Mobile | — | — | — | **NOT AUDITED** (không có source) |

---

## 3. Business Workflow Matrix

| ID | Luồng | Business rule (nguồn) | Expected | Actual / bằng chứng | Trạng thái |
|---|---|---|---|---|---|
| BW-01 | F1 | Chỉ chủ khảo sát, Sales được giao, kỹ thuật viên được giao đọc mô phỏng | Người khác bị từ chối | `SimulationAccess.CanRead` (`Modules/SolarSimulation/Access/SimulationAccess.cs:14`); `SimulationAccessTests` PASS | [Đã xác định] PASS |
| BW-02 | F1 | Sales chỉ xem yêu cầu khảo sát được giao | 404/403 khi khác Sales | `GetSurveyRequestDetailHandler.cs:30` so `AssignedSaleId` | [Đã xác định] PASS |
| BW-03 | F1 | PVGIS / NASA POWER thật, p95 ≤ 25 s | Đo trên provider thật | Chỉ fake trong test | [Chưa xác định] NOT VERIFIED |
| BW-04 | F2 | Mỗi hợp đồng một attempt payable và một attempt PAID; amount_paid = amount | DB chặn | `PaymentConfigurations.cs:44-50` partial unique; `ck_payment_attempt_paid_fields`; Postgres integrity PASS | [Đã xác định] PASS |
| BW-05 | F2 | Webhook PayOS: chữ ký, chống trùng, order code lạ vẫn ack | Không ghi nhận hai lần | `PayOsGateway.VerifyWebhook:77-102` (FixedTimeEquals), `PaymentService.HandleWebhookAsync:294` (hash + unique) | [Đã xác định] PASS |
| BW-06 | F2 | PayOS reconciliation ≤ 2 phút | Worker 60 s, re-check 2 phút | `PaymentReconciliationWorker` tick 60 s, cửa sổ 2 phút | [Trung lập] code đạt; NFR NOT VERIFIED |
| BW-07 | F3 | Bàn giao theo số lượng (lô) + serial tuỳ chọn, đối chiếu hợp đồng | Lệch cần duyệt | `InstallationLotRegistrationTests`, `PostgresInstalledAssetLotTests` PASS | [Đã xác định] PASS |
| BW-08 | F4 | Không hoàn tiền; tiền đã thu không áp dụng được vẫn được ghi sổ | Không mất ghi nhận | `refund_record PENDING_APPROVAL` vẫn được ghi; không có luồng xử lý tiếp | [Chưa xác định] **BLOCKED — quyết định business** (BUS-01) |
| BW-09 | F5 | Mô phỏng ≠ đo đạc; không tạo số liệu giả | Hai nguồn tách biệt | `EnergyMonitoringView` `dataSource`/`measuredDataSource`; NOT_AVAILABLE cho W/kWh | [Đã xác định] PASS |
| BW-10 | F5 | RAG: khách chỉ thấy tài liệu audience customer, citation kiểm lại | Không rò tài liệu nội bộ | `ChatService.cs:51-118` lọc trước + kiểm sau | [Đã xác định] PASS |
| BW-11 | F5 | Cảnh báo sản lượng bất thường | Có ngưỡng được duyệt | `alertsStatus = NOT_CONFIGURED` (D-08 chưa quyết) | [Chưa xác định] decision pending |
| BW-12 | F6 | Một thiết bị ACTIVE / công trình; nhiều công trình / khách; một binding / thiết bị | DB chặn race | `ux_iot_device_binding_active_project`, `…_active_device`; race test PostgreSQL PASS | [Đã xác định] PASS |
| BW-13 | F6 | Firmware V1 chỉ đo dòng ACS712; không suy ra W/kWh | NOT_AVAILABLE | `IotTelemetryHandlers.MeasuredAsync`; bảng `iot_telemetry_reading` chỉ có `current_amps` | [Đã xác định] PASS |
| BW-14 | F6 | Lịch sử không đi theo thiết bị khi chuyển công trình | Đọc theo binding | `IotBindingTests.Release_keeps_history…`, `Replacing_the_device…` PASS | [Đã xác định] PASS |
| BW-15 | F6 | Thời gian lưu telemetry | Có chính sách | Không có job xoá / tổng hợp | [Chưa xác định] decision pending (DB-03) |
| BW-16 | F7 | Đổi/đặt lại mật khẩu thu hồi mọi JWT cũ | 401 ngay | `AuthVersion++` (Reset/ChangePasswordHandler); `PasswordChangeTokenRevocationTests` PASS | [Đã xác định] PASS |
| BW-17 | F7 | Forgot password không chống liệt kê tài khoản, không mất yêu cầu | Outbox | Outbox PASS cho broker down; vẫn mất khi SMTP lỗi > ~15 s | [Đã xác định] INT-01 |
| BW-18 | F7 | Email xác thực / gửi lại xác thực không mất | Bền vững | Publish sau commit, lỗi bị nuốt | [Đã xác định] INT-02 |
| BW-19 | F2 | Upload ảnh khảo sát thất bại trên production | Root cause | Chuỗi code PASS trên S3-compatible; production chưa có request/response | [Chưa xác định] (phiên trước) |

---

## 4. Security Findings

Không có finding P0. Mỗi finding: severity · trạng thái · vị trí · invariant · bằng chứng · actual/expected · impact · fix tối thiểu · test cần
có · migration / API contract.

### SEC-01 · P1 · [Đã xác định] — Môi trường Development trên compose triển khai
* **Vị trí:** `docker-compose.pull.yml:45`, `docker-compose.yml:47` (`ASPNETCORE_ENVIRONMENT: Development`); `src/SmartSolar.Api/Program.cs:114`
  (`if (app.Environment.IsDevelopment()) { UseSwagger(); UseSwaggerUI(); }`).
* **Invariant:** production không lộ tài liệu API tương tác, không nạp cấu hình Development.
* **Actual:** compose dùng để kéo image triển khai (`API_IMAGE_TAG`) đặt Development → Swagger UI/JSON công khai; `appsettings.Development.json`
  được nạp (CORS `http://localhost:3000`, link email `localhost`) trừ khi `.env` ghi đè. **Expected:** `Production`, Swagger tắt hoặc sau auth.
* **Impact:** lộ toàn bộ bề mặt API (301 route, schema) cho người lạ; link email có thể trỏ localhost nếu `.env` thiếu. Mọi vai trò.
* **Fix:** `ASPNETCORE_ENVIRONMENT: Production` trong compose triển khai (hoặc biến từ `.env`), kiểm tra `.env` có đủ `Frontend__*`, `Cors__*`.
* **Test:** smoke test production: `GET /swagger/index.html` → 404. **Migration/API:** không.

### SEC-02 · P1 · [Đã xác định] cấu hình / [Chưa xác định] mức phơi bày thật — HTTP trực tiếp trên mọi interface
* **Vị trí:** `docker-compose.pull.yml:58` (`"8080:8080"`), `Dockerfile` (`ASPNETCORE_URLS=http://+:8080`), `Program.cs:120`
  (`UseHttpsRedirection` không có cổng HTTPS trong container), không có `UseHsts`.
* **Invariant:** mật khẩu, JWT, refresh token, khoá thiết bị IoT (`X-Device-Key`) chỉ đi qua TLS.
* **Actual:** API nghe HTTP thường, publish ra `0.0.0.0`; Docker-published port bỏ qua UFW (đúng như ghi chú chính repo cho RabbitMQ). Repo không
  có reverse proxy/TLS. Nếu VPS có Nginx/Caddy với TLS thì client vẫn có thể gọi thẳng `http://<ip>:8080`.
* **Impact:** nghe lén thông tin đăng nhập / khoá thiết bị khi gọi trực tiếp; vượt proxy (mất forwarded headers → rate limit theo IP proxy).
* **Fix:** publish `127.0.0.1:8080:8080` sau reverse proxy TLS; cấu hình `ForwardedHeaders__KnownProxies`; bật HSTS ở proxy.
* **Test:** từ ngoài VPS `curl http://<ip>:8080` phải thất bại; `curl https://<domain>/api/products` 200. **Migration/API:** không.

### SEC-03 · P2 · [Đã xác định] package / [Chưa xác định] khả năng khai thác — Gói có lỗ hổng đã công bố
* `dotnet list package --vulnerable --include-transitive`: `System.Security.Cryptography.Pkcs 10.0.0` (High GHSA-846f-f764-76mx,
  Moderate GHSA-2fmv-rv2c-59x7) — transitive qua MimeKit 4.18.0 trong `SmartSolar.Api`/`Infrastructure` (vào image production). Test project
  còn `System.Net.Http 4.3.0`, `System.Text.RegularExpressions 4.3.0` (High, chỉ test).
* **Fix:** nâng MimeKit/MailKit hoặc pin `System.Security.Cryptography.Pkcs` bản đã vá; chạy lại `--vulnerable` trong CI. Python RAG:
  **NOT AUDITED** (không có `pip-audit`).

### SEC-04 · P3 · [Đã xác định] — Sales đọc metadata mọi file
* `FilesController.cs:59` `IsStaff() => Admin || Manager || Sales`; `FileService.GetAsync:94-97` bỏ kiểm người upload khi staff.
* Sales bất kỳ đọc được `GET /api/files/{id}` (tên file gốc, object key, checksum, người upload) của file thuộc khách / Sales khác nếu biết id
  (GUID, khó đoán). Không cấp URL đọc nội dung. **Fix:** giới hạn Sales theo nghiệp vụ hoặc chỉ uploader + Manager/Admin. API: không đổi shape.

### SEC-05 · P3 · [Trung lập] — Oracle thời gian tồn tại deviceId
* `IotTelemetryHandlers.cs:18-20`: deviceId không tồn tại trả 401 trước khi băm secret; deviceId tồn tại + key sai thì băm SHA-256. Chênh lệch
  rất nhỏ (một SHA-256), cùng mã lỗi. Kết hợp RL-01 có thể dò deviceId. **Fix (tuỳ chọn):** băm giả khi không thấy thiết bị.

### SEC-06 · P2 · [Trung lập] — Refresh token trong Web Storage (FE)
* `smart-solar/src/services/api/tokens.ts:4,55`: refresh token trong `localStorage` (ghi nhớ) / `sessionStorage`; access token trong bộ nhớ.
  XSS bất kỳ đọc được refresh token (sống 14 ngày). Đánh đổi đã chọn; giảm thiểu bằng CSP và phát hiện replay (đã có).

### Không phát hiện vấn đề (đã kiểm)
* JWT: issuer/audience/chữ ký/hạn dùng, ClockSkew 30 s, khoá ≥ 32 byte (`JwtOptions.MinimumSigningKeyBytes`), `AuthVersion` mỗi request
  (`AccessTokenRevocationCheck`). Refresh: xoay vòng có claim điều kiện, replay → thu hồi mọi phiên (`RefreshTokenHandler.RotateAsync`).
* Login: chuẩn hoá email, băm giả chống timing, cùng một lỗi cho mọi trường hợp (`LoginHandler`).
* ~~Forwarded headers chỉ tin `KnownProxies` cấu hình → không giả mạo X-Forwarded-For được.~~ **Sai** — với danh sách rỗng ASP.NET Core
  không kiểm tra nguồn; xem SEC-07 ở §0 (đã sửa).
* Log: không template nào chứa email/URL/token/body; outbox xoá URL sau publish. Exception middleware không trả nội dung lỗi.
* PayOS, S3 presign (Content-Type ký, TTL, key do server tạo, verify magic bytes, chỉ VERIFIED gắn vào nghiệp vụ), IoT secret (CSPRNG 256 bit,
  chỉ lưu SHA-256, FixedTimeEquals, rotate có hiệu lực ngay).
* RAG: lọc audience phía API + kiểm citation; prompt injection chỉ ảnh hưởng nội dung câu trả lời, không vượt quyền tài liệu.

---

## 5. Rate Limit Matrix

Toàn bộ 301 endpoint ở **Phụ lục A** (tạo tự động từ attribute trong controller). Tóm tắt:

| Nhóm | Số endpoint | Partition | Cluster-wide (Redis) | Ghi chú |
|---|---|---|---|---|
| Auth anonymous (register, verify, resend, login, refresh, logout, forgot, reset) + change-password | 10 | **IP** | không | RL-03 |
| Catalog read (public) | 5 | user, IP nếu anonymous | không | |
| Scheduling | 45 | user | không | |
| Installation workflow (installation/quotation/contract/onsite/files) | ~140 | user (+contractId với payment-create) | files-write, payment-create, installation-scheduling, installation-preview | |
| RAG | 21 | user | rag-chat, register, reindex, config | |
| IoT read/write | 15 | user | không | |
| IoT ingest (anonymous) | 1 | **IP + deviceId** | không | RL-01, RL-02 |
| PayOS webhook (anonymous) | 1 | IP, 600/phút | không | không chặn retry hợp lệ |
| Public file redirect (anonymous) | 1 | IP, 120/phút | không | |
| **Không có limit** | **21** | — | — | RL-04 (tất cả cần JWT) |

Redis lỗi/chậm > 250 ms → **fail to local** (giới hạn theo instance), không fail-open (`guides/rate-limiting.md` §2; test Redis thật 13/13 PASS).
Fixed window cho phép tối đa 2× limit quanh ranh giới cửa sổ (tính chất thuật toán, [Trung lập]).

### RL-01 · P2 · [Đã xác định, tái hiện] — Vượt giới hạn IoT ingest bằng cách đổi deviceId
* `IotMonitoringModuleExtensions.cs:35` (`RouteValuePartition: "deviceId"`), `RateLimitPolicyRegistration.PartitionKey:66-71` → khoá =
  `iot-telemetry-ingest:ip:<ip>:deviceId:<giá trị route>`.
* **Tái hiện** (test tạm, đã xoá): limit 2/phút; 30 request với 30 deviceId khác nhau từ một client → `401×30`, **0 lần 429**; cùng một
  deviceId → `401,401,429,429`.
* **Impact:** không có trần theo IP cho endpoint anonymous; mỗi request tốn một truy vấn DB (+ SHA-256 nếu id tồn tại), dò deviceId không giới hạn.
  Thiết bị hợp lệ 10 s/lần không bị ảnh hưởng (6/12 mỗi phút).
* **Fix tối thiểu:** thêm một policy chồng theo IP (ví dụ trần tổng theo IP, giá trị cần đo theo số thiết bị sau NAT) hoặc limiter theo IP cho
  phản hồi 401. Không đổi API. **Test:** rotating ids → 429 sau ngưỡng IP; thiết bị thật vẫn 201.

### RL-02 · P2 · [Đã xác định, tái hiện] — Ingest anonymous đọc body lớn trước khi xác thực
* `IotMonitoringControllers.cs:160-161` không có `[RequestSizeLimit]`; Kestrel mặc định 30 MB; model binding đọc và parse JSON trước kiểm khoá.
* **Tái hiện:** body 8 MB → `401` (đọc hết, không có 413).
* **Fix:** `[RequestSizeLimit(1024)]` (payload thật ~20 byte) như webhook PayOS (`PaymentControllers.cs:66`). **Test:** 2 KB body → 413.

### RL-03 · P2 · [Đã xác định] code / [Chưa xác định] triển khai — Giới hạn auth chỉ theo IP
* `RateLimitingExtensions.ClientKey:224` khoá = `<section>|<ip>`. Đằng sau reverse proxy mà `ForwardedHeaders:KnownProxies` rỗng
  (`appsettings.json`), mọi người dùng chung IP proxy → 10 lần login / 10 phút cho **toàn hệ thống**. Ngược lại, credential stuffing phân tán
  nhiều IP không bị giới hạn theo tài khoản (không có lockout theo account).
* **Fix:** cấu hình KnownProxies đúng; cân nhắc thêm giới hạn theo email chuẩn hoá cho login/forgot (cần quyết định ngưỡng — không tự đặt).

### RL-04 · P3 · [Đã xác định] — 21 endpoint không có rate limit
* Customer: survey-requests (2), sites (2), pre-surveys (3), surface (2), `POST /api/customers/me`; Sales: survey-requests pending/my/detail/
  claim/assign/reassign/escalated/reschedule (8); simulations list/get (2). Tất cả cần JWT; chi phí DB thấp; ghi (create site, submit, claim,
  assign) không có trần. **Fix:** gắn `scheduling-*`/`catalog-*` sẵn có.

---

## 6. Database Audit

| Hạng mục | Kết quả | Bằng chứng |
|---|---|---|
| Migration history / snapshot | 25 migration; `has-pending-model-changes` sạch (phiên trước); M1–M10 Up/Down trên PostgreSQL PASS | `PostgresM1M4MigrationTests` |
| Toàn vẹn thanh toán | PASS | §3 BW-04/05 |
| IoT uniqueness / ownership | PASS | BW-12/14 |
| Outbox atomic | Token + dispatch cùng transaction; claim bằng UPDATE điều kiện | `IdentityUnitOfWork.ExecuteInTransactionAsync`, `PasswordResetDispatchStore.TryClaimAsync` |
| Concurrency | 89 test PostgreSQL PASS (race, exclusion, FOR UPDATE, partial unique) | test log |

### DB-01 · P2 · [Đã xác định, tái hiện local] — 7 bảng mới không bật RLS
* Quy ước từ `20261020051352` và `20261021023128`: mỗi migration tự `ENABLE ROW LEVEL SECURITY` cho bảng nó tạo (16/16, 15/15). Các migration
  sau **không**: `20261030201618_KnowledgeDocumentAudience` (1 bảng), `20261030201748_InstallationEquipmentSubstitution` (2),
  `20261115153613_IotEnergyMonitoring` (3), `20261115153617_PasswordResetDispatchOutbox` (1).
* **Tái hiện local** (PostgreSQL 17 dùng một lần, role/default privileges kiểu Supabase): migrate đến `20261021164225` → chạy
  `supabase-security-hardening-draft.sql` → migrate đến cuối → `supabase-acl-rls-audit-readonly.sql`: 101 bảng, RLS 94/101, 7 bảng trên RLS
  off, anon/authenticated 0 quyền (vì default ACL đã bị thu). Khớp quan sát Supabase của bạn (RLS false, anon SELECT false).
* **Impact:** hiện **không lộ** nếu không còn grant nào cho anon/authenticated (cần xác minh INSERT/UPDATE/DELETE, column ACL, default ACL bằng
  script §6.1); mất lớp phòng thủ thứ hai cho `password_reset_dispatch` (chứa URL reset khi PENDING), `iot_device` (hash khoá), telemetry.
* **Fix:** migration mới chỉ `ALTER TABLE … ENABLE ROW LEVEL SECURITY` cho 7 bảng (không đổi API); thêm test migration kiểm "mọi bảng tạo
  sau 2026-10-20 có RLS". Cần duyệt migration trước khi áp dụng.

### DB-02 · [Chưa xác định] — Quyền Supabase ngoài SELECT, Data API
* Quan sát hiện có chỉ phủ SELECT/RLS của 4 bảng. Chưa biết: INSERT/UPDATE/DELETE/TRUNCATE, column ACL, default ACL hiện tại, schema được Data
  API expose (cấu hình Dashboard), quyền role RAG. **Script chỉ đọc:** `guides/sql/supabase-acl-rls-audit-readonly.sql` (đã chạy thử trên DB
  local mô phỏng Supabase: chạy hết, exit 0). Kết quả mong đợi: mọi dòng `table_priv` trống, `table_priv_summary` anon/authenticated/public = 0,
  không `column_acl`, `default_acl` không có anon/authenticated, `security_definer` không có hàm public executable bởi anon.
* Đã biết từ tài liệu trước (`deployment-readiness.md` §1a): kết nối Npgsql dùng `Trust Server Certificate=true` (không xác thực chứng chỉ, rủi
  ro MITM) — `.env` không được đọc trong audit nên trạng thái hiện tại [Chưa xác định]; API và RAG cùng dùng role `postgres` (owner, BYPASSRLS).

### DB-03 · P2 · [Đã xác định] — Telemetry tăng không giới hạn
* Không có job xoá/tổng hợp `iot_telemetry_reading`. 8 640 bản ghi/thiết bị/ngày (10 s). Index `(binding_id, received_at)` đủ cho đọc; tăng
  trưởng lưu trữ cần chính sách. **Quyết định business:** thời gian lưu, tổng hợp theo giờ/ngày.

### DB-04 · P3 · [Đã xác định] — Danh sách không phân trang
* Ví dụ `InstallationUnitOfWork.ListProjectsAsync:33` (tải mọi dự án, sắp xếp trong bộ nhớ), `GET /api/iot/bindings`. Chấp nhận ở quy mô hiện
  tại; cần phân trang khi dữ liệu lớn.

### 6.1 SQL chỉ đọc
`guides/sql/supabase-acl-rls-audit-readonly.sql`: chạy trong `BEGIN TRANSACTION READ ONLY … ROLLBACK`, gồm: role/attribute; quyền
SELECT/INSERT/UPDATE/DELETE/TRUNCATE/REFERENCES/TRIGGER của anon, authenticated, PUBLIC cho từng bảng; tổng hợp; 7 bảng sau quy ước; column ACL;
ACL thô; sequence; default privileges; RLS + policy; SECURITY DEFINER; quyền schema; cấu hình/phiên PostgREST; publication/trigger/view; role
LOGIN có quyền ghi; cột nhạy cảm (chỉ tên); lịch sử migration, constraint/index invalid. Không tự chạy hardening.

---

## 7. Integration & Background Jobs Audit

| Tích hợp | Timeout / retry | Idempotency | Khi lỗi | Trạng thái |
|---|---|---|---|---|
| PayOS | HttpClient timeout 3–60 s | webhook hash unique; reconciliation 60 s | link vẫn PENDING, reconcile | [Đã xác định] PASS (fake server) |
| PVGIS / NASA POWER | qua provider client + cache | cache theo fingerprint | lỗi provider → lỗi có mã | test fake; thật NOT VERIFIED |
| S3 | presign + verify | file key duy nhất, version id | PENDING_VERIFICATION → verify lại | S3-compatible thật PASS |
| RabbitMQ / MassTransit | retry 3 × 5 s mỗi consumer | outbox reset password; consumer dedupe | xem INT-01/02 | |
| SMTP | trong consumer | — | ném lỗi → MassTransit retry | |
| Redis | 250 ms | — | fail to local | Redis thật PASS |
| RAG / Ollama | HMAC, timeout cấu hình | — | 503 RAG_SERVICE_UNAVAILABLE | INT-03 |
| IoT ESP32 | thiết bị tự gửi lại chu kỳ sau | mỗi reading một bản ghi (không dedupe) | 503 IOT_STORAGE_UNAVAILABLE | [Trung lập] |
| Background workers (6) | tick 15–60 s | dựa vào revision / UPDATE điều kiện | — | nhiều instance [Chưa xác định] (DEP-06) |

### INT-01 · P2 · [Đã xác định] — Đặt lại mật khẩu: các trường hợp "accepted" nhưng không có email
Điều kiện (từ code `PasswordResetDispatch.cs`, `PasswordResetRequestedConsumer.cs`):
1. RabbitMQ hoặc Email tắt (`IEmailDeliveryStatus.CanDeliver = false`, `PasswordResetDispatch.cs:95`): không publish; dòng hết hạn theo token.
2. Broker không dùng được lâu hơn thời hạn token (`Auth:PasswordReset:LifetimeMinutes`, 60 phút): dòng thành `EXPIRED`.
3. **SMTP lỗi lâu hơn ~15 s**: consumer retry `RetryLimit = 3`, `RetryInterval = 5 s` (`PasswordResetRequestedConsumer.cs:73-81`) → message vào
   error queue; dòng ở `PUBLISHED` với `last_error`; **không có cơ chế đẩy lại** → mất email. Khách phải yêu cầu lại.
4. Yêu cầu mới thay token cũ → dòng cũ bị xoá (cascade) → email cũ bị bỏ (đúng thiết kế).
* Rủi ro phụ: URL reset (raw token) nằm trong message RabbitMQ và **ở lại error queue** sau khi retry cạn ([Đã xác định] theo cấu hình
  MassTransit mặc định); gửi trùng có thể xảy ra (at-least-once) khi SMTP nhận nhưng ghi SENT lỗi.
* **Fix tối thiểu:** redelivery trễ (`UseDelayedRedelivery`) trong thời hạn token hoặc worker đẩy lại dòng PUBLISHED quá hạn chưa SENT; chính
  sách TTL/purge cho error queue. **Test:** SMTP lỗi 1 phút rồi hồi → email được gửi đúng một lần.

### INT-02 · P2 · [Đã xác định] — Email xác thực và gửi lại xác thực có thể mất im lặng
* `RegisterHandler.cs:118-126`, `ResendVerificationHandler.cs:93-101`: publish sau commit, `catch (Exception)` chỉ log. Khi broker lỗi, tài khoản
  được tạo nhưng không có email; người dùng phải tự bấm gửi lại (giới hạn 3/15 phút/IP). Luồng admin (`AdminUserHandlers.DeliverAsync:440-480`)
  **không** im lặng: trả `PublishFailed` cho Admin ([Trung lập]).
* **Fix:** tái sử dụng outbox như reset password. Không đổi API.

### INT-03 · P2 · [Đã xác định] code / [Chưa xác định] chế độ kết nối — RAG psycopg với transaction pooler
* `src/SmartSolar.RagService/app/vectorstore.py:116` `ConnectionPool(...)` không đặt `prepare_threshold=None`. Tài liệu
  `deployment-readiness.md` §1a ghi đã tái hiện `DuplicatePreparedStatement` qua transaction pooler (cổng 6543). Nếu production dùng 6543 thì
  truy vấn vector lỗi khi đồng thời. **Fix:** `kwargs={"prepare_threshold": None}` khi dùng transaction pooler.

---

## 8. Production & Deployment

| ID | Sev | Trạng thái | Vấn đề | Bằng chứng | Fix tối thiểu |
|---|---|---|---|---|---|
| SEC-01 | P1 | Đã xác định | Development env, Swagger | §4 | `Production` |
| SEC-02 | P1 | Đã xác định / Chưa xác định | HTTP 0.0.0.0:8080, không TLS trong repo | §4 | loopback + proxy TLS |
| DEP-01 | P2 | Đã xác định | Không `restart:` cho api, rabbitmq, redis | `grep restart docker-compose*.yml` chỉ có `ollama-models: "no"` | `restart: unless-stopped` |
| DEP-02 | P2 | **False positive một phần** (xem §0) | ~~Không health endpoint~~ (`/health/live`, `/health/ready` có sẵn trong `SchemaReadinessMiddleware`); không healthcheck container API | `Program.cs` chỉ `MapControllers`; `SchemaReadinessMiddleware` trả 503 nhưng không có `/health` | `MapHealthChecks("/health/live")`, `/health/ready` (DB + schema) + healthcheck compose |
| DEP-03 | P2 | Đã xác định | Không CI/CD, không quality gate | không có `.github/` | build + test + `--vulnerable` trên PR |
| DEP-04 | P3 | Đã xác định | Container chạy root | `Dockerfile` không có `USER` | `USER $APP_UID` (ảnh .NET 8) |
| DEP-05 | P3 | Đã xác định | Tag mặc định `latest` (api, rag, ollama) | `docker-compose.pull.yml` | ghim tag; rollback = tag trước (đã ghi chú) |
| DEP-06 | P3 | Chưa xác định | Worker chạy nhiều instance | 6 worker không khoá riêng; dựa vào revision/claim | xác nhận chỉ 1 instance hoặc thêm advisory lock |
| DEP-07 | P3 | Đã xác định | Không giới hạn tài nguyên cho api/rabbitmq/redis (rag/ollama có) | compose | `deploy.resources.limits` |
| DEP-08 | P3 | Trung lập | DataProtection không dùng tường minh | không có `AddDataProtection` | không cần nếu không dùng cookie/antiforgery |
| — | — | Đã xác định | API mới + schema cũ | `SchemaReadinessMiddleware` trả 503 `SCHEMA_NOT_READY` mọi request đến khi đủ migration; migration mới đều additive (API cũ chạy được trên schema mới) | — |
| — | — | Chưa xác định | Monitoring, metrics, alert, backup verification | không có trong repo | — |

**Frontend (audit có mục tiêu):** access token trong bộ nhớ; refresh single-flight + Web Locks giữa tab (`client.ts:12-69`); `RequireRole.tsx`
cho route. IoT UI chưa có. **FE-01 · P3 · [Đã xác định]:** sau đổi mật khẩu thành công FE chỉ toast (`ChangePasswordDialog.tsx:78`), không đăng
xuất; backend đã thu hồi mọi JWT nên request kế tiếp 401 → refresh lỗi → "phiên hết hạn". Fix: xoá token và chuyển tới đăng nhập ngay.

---

## 9. Test Evidence

| Lệnh | Môi trường | Kết quả |
|---|---|---|
| `dotnet test tests/SmartSolar.Tests` (toàn bộ, `SMARTSOLAR_TEST_POSTGRES`) | .NET 8.0.425; PostgreSQL 17 Docker dùng một lần (127.0.0.1) | **1 638 tổng: 1 633 PASS, 0 FAIL, 5 SKIP** (229 s). Unit 1 268, Integration 354, E2E 11 PASS; 89 test PostgreSQL PASS |
| `--filter DistributedRateLimiting\|S3CompatibleUploadChain\|Redis` (`SMARTSOLAR_TEST_REDIS`, `SMARTSOLAR_TEST_S3`) | Redis 7 + MinIO-fork `pgsty/silo` dùng một lần | **13/13 PASS** (bao phủ 5 SKIP ở trên) |
| Test tái hiện tạm `AuditRepro` (2 test, đã xoá) | WebApplicationFactory + SQLite | 2/2 PASS: RL-01, RL-02 |
| `dotnet ef database update` lên DB `sbx` dùng một lần + hardening draft + audit SQL | PostgreSQL 17 local, role Supabase mô phỏng | DB-01 tái hiện; audit SQL exit 0 |
| `dotnet list package --vulnerable --include-transitive` | NuGet | SEC-03 |
| FE `npm test`/`typecheck` | — | **không chạy trong audit này** (đã PASS ở phiên trước trên cùng working tree: 202/202) |
| Python RAG tests, `pip-audit` | — | **NOT RUN / NOT AUDITED** |
| Third-party thật: PayOS, PVGIS, NASA POWER, SMTP, RabbitMQ, AWS S3, Ollama | — | **NOT VERIFIED** (chỉ fake/mock, trừ Redis + S3-compatible) |

**NFR (ngưỡng đã chốt trong repo):**

| NFR | Ngưỡng (nguồn) | Trạng thái |
|---|---|---|
| API read | p95 ≤ 1.5 s, 50 VUs, 10 phút (`full-regression-test-report.md:152`) | **NOT VERIFIED** |
| Simulation | p95 ≤ 25 s, ≥ 30 lần (`:153`) | **NOT VERIFIED** |
| PayOS reconciliation | ≤ 2 phút (`production-readiness-report.md:124`) | **NOT VERIFIED** (code: tick 60 s) |
| JWT 401/403 | — | PASS chức năng (test), không đo tải |
| Presigned URL expiry | TTL cấu hình | PASS chức năng (S3-compatible: link hết hạn → 403), production NOT VERIFIED |
| RAG | p95 ≤ 10 s trên VPS (`rag-local-ollama.md:399`); accuracy/citation | **NOT VERIFIED** trên VPS. Theo `rag-local-ollama.md:138-153` (máy dev, nhanh hơn VPS): cấu hình mặc định qwen3:1.7b p95 14.74 s **FAIL**; ngân sách giảm (1 500/300 token) p95 5.24 s đạt trên máy dev |
| IoT throughput | không có ngưỡng chốt | **NOT VERIFIED** |

---

## 10. Remediation Backlog

| Ưu tiên | ID | Fix tối thiểu | Ảnh hưởng business / API / FE / DB | Test cần thêm |
|---|---|---|---|---|
| P1 | SEC-01 | `ASPNETCORE_ENVIRONMENT=Production` trong compose triển khai | không / không / không / không | smoke `/swagger` 404 |
| P1 | SEC-02 | API sau proxy TLS, publish loopback, KnownProxies, HSTS | không | curl ngoài VPS |
| P2 | RL-01 | Thêm giới hạn theo IP cho ingest | không | rotating ids → 429 |
| P2 | RL-02 | `[RequestSizeLimit]` ingest | không | body 2 KB → 413 |
| P2 | RL-03 | KnownProxies; quyết định giới hạn theo tài khoản | có thể thêm 429 cho login | login qua proxy |
| P2 | DB-01 | Migration bật RLS 7 bảng | DB (cần duyệt) | test "mọi bảng có RLS" |
| P2 | INT-01 | Redelivery trễ / đẩy lại PUBLISHED chưa SENT; TTL error queue | không | SMTP down 1 phút |
| P2 | INT-02 | Outbox cho email xác thực | DB (bảng mới hoặc mở rộng) | broker down khi register |
| P2 | INT-03 | `prepare_threshold=None` | không | pgvector qua pooler 6543 |
| P2 | SEC-03 | Nâng gói Pkcs/MimeKit | không | `--vulnerable` sạch |
| P2 | DEP-01/02/03 | restart, health endpoints, CI | không | healthcheck |
| P2 | DB-03 | Chính sách lưu telemetry (sau quyết định) | DB | job purge |
| P3 | RL-04, SEC-04, SEC-05, DEP-04..08, DB-04, FE-01 | như bảng trên | nhỏ | tương ứng |
| Decision | BUS-01 | Quy trình xử lý tiền đã thu không áp dụng được; huỷ việc/gói đã trả tiền | business | — |
| Decision | D-08 | Ngưỡng cảnh báo sản lượng | business | — |

---

## 11. Production Release Gates

Phải đạt **trước** khi mở công khai:

1. SEC-01 và SEC-02 đã sửa và kiểm chứng từ ngoài VPS (Swagger 404; chỉ HTTPS qua proxy; cổng 8080 không truy cập được từ Internet).
2. `supabase-acl-rls-audit-readonly.sql` chạy trên Supabase production: anon/authenticated/PUBLIC = 0 quyền mọi bảng, không column ACL, default
   ACL không có anon/authenticated, không SECURITY DEFINER executable bởi anon; Data API cho `public` tắt hoặc đã xác minh không expose.
3. Migration `IotEnergyMonitoring` và `PasswordResetDispatchOutbox` được duyệt và áp dụng (đã có thứ tự hardening → migration → hardening);
   sau đó RLS bật cho 7 bảng (DB-01).
4. Health endpoint + healthcheck + restart policy (DEP-01/02) hoạt động; backup và restore được diễn tập trên bản sao.
5. Đo NFR trên môi trường tương đương production bằng k6 (API read p95 ≤ 1.5 s/50 VUs; simulation p95 ≤ 25 s; PayOS ≤ 2 phút; RAG p95 ≤ 10 s
   nếu bật RAG) — hoặc ghi rõ NFR nào được chấp nhận là ngoại lệ.
6. Smoke test third-party thật: PayOS sandbox, SMTP, RabbitMQ, AWS S3 (upload ảnh khảo sát từ thiết bị kỹ thuật viên), PVGIS/NASA POWER.
7. `dotnet list package --vulnerable` không còn High trong project ship ra production (SEC-03).
8. Quyết định business cho BUS-01 (tiền đã thu không áp dụng được) trước khi nhận thanh toán thật ở quy mô lớn.

Nên có (không chặn): RL-01/02/03, INT-01/02/03, DB-03, CI (DEP-03).

---

## Phụ lục A — Rate limit toàn bộ 301 endpoint

Tạo tự động từ `[Http*]`, `[Authorize]`/`[AllowAnonymous]`, `[EnableRateLimiting]` (cấp action, nếu không có thì cấp class) trong
`src/SmartSolar.Api/Controllers/*.cs`. "user" = JWT subject, IP khi anonymous. Tất cả endpoint có `[RequireFeature]` trả 503 khi feature tắt.

| # | Method | Route | Auth | Policy | Limit | Partition | Cluster-wide | Bypass / note |
|---|---|---|---|---|---|---|---|---|
| 1 | GET | `/api/admin/products` | Admin,Manager | catalog-write | 30/1m | user | no |  |
| 2 | GET | `/api/admin/products/{id:guid}` | Admin,Manager | catalog-write | 30/1m | user | no |  |
| 3 | POST | `/api/admin/products` | Admin,Manager | catalog-write | 30/1m | user | no |  |
| 4 | PUT | `/api/admin/products/{id:guid}` | Admin,Manager | catalog-write | 30/1m | user | no |  |
| 5 | PATCH | `/api/admin/products/{id:guid}/status` | Admin,Manager | catalog-write | 30/1m | user | no |  |
| 6 | DELETE | `/api/admin/products/{id:guid}` | Admin,Manager | catalog-write | 30/1m | user | no |  |
| 7 | POST | `/api/admin/products/{id:guid}/images` | Admin,Manager | catalog-write | 30/1m | user | no |  |
| 8 | DELETE | `/api/admin/products/{id:guid}/images/{imageId:guid}` | Admin,Manager | catalog-write | 30/1m | user | no |  |
| 9 | PUT | `/api/admin/products/{id:guid}/images/order` | Admin,Manager | catalog-write | 30/1m | user | no |  |
| 10 | PUT | `/api/admin/products/{id:guid}/images/{imageId:guid}/primary` | Admin,Manager | catalog-write | 30/1m | user | no |  |
| 11 | GET | `/api/admin/users` | Admin | admin-users-read | 60/1m | user | no |  |
| 12 | GET | `/api/admin/users/{userId:guid}` | Admin | admin-users-read | 60/1m | user | no |  |
| 13 | POST | `/api/admin/users` | Admin | admin-users-write | 20/1m | user | no |  |
| 14 | PUT | `/api/admin/users/{userId:guid}` | Admin | admin-users-write | 20/1m | user | no |  |
| 15 | PATCH | `/api/admin/users/{userId:guid}/status` | Admin | admin-users-write | 20/1m | user | no |  |
| 16 | PUT | `/api/admin/users/{userId:guid}/roles` | Admin | admin-users-write | 20/1m | user | no |  |
| 17 | DELETE | `/api/admin/users/{userId:guid}` | Admin | admin-users-write | 20/1m | user | no |  |
| 18 | POST | `/api/admin/users/{userId:guid}/send-password-reset` | Admin | admin-users-recovery | 5/10m | user | no |  |
| 19 | GET | `/api/administrative-units/provinces` | anonymous | catalog-read | 60/1m | user, IP if anonymous | no | IP when anonymous |
| 20 | GET | `/api/administrative-units/communes` | anonymous | catalog-read | 60/1m | user, IP if anonymous | no | IP when anonymous |
| 21 | GET | `/api/administrative-units/legacy-lookup` | anonymous | catalog-read | 60/1m | user, IP if anonymous | no | IP when anonymous |
| 22 | GET | `/api/customers/me/after-sales/installations/{projectId:guid}/warranty` | Customer | installation-read | 90/1m | user | no |  |
| 23 | POST | `/api/customers/me/after-sales/fault-requests` | Customer | installation-write | 20/1m | user | no |  |
| 24 | POST | `/api/customers/me/after-sales/one-time-maintenance` | Customer | installation-write | 20/1m | user | no |  |
| 25 | GET | `/api/customers/me/after-sales/requests` | Customer | installation-read | 90/1m | user | no |  |
| 26 | GET | `/api/customers/me/after-sales/requests/{requestId:guid}` | Customer | installation-read | 90/1m | user | no |  |
| 27 | GET | `/api/customers/me/after-sales/requests/{requestId:guid}/evidence/{evidenceId:guid}/url` | Customer | installation-read | 90/1m | user | no |  |
| 28 | POST | `/api/customers/me/after-sales/requests/{requestId:guid}/cancel` | Customer | installation-write | 20/1m | user | no |  |
| 29 | POST | `/api/customers/me/after-sales/requests/{requestId:guid}/completion` | Customer | installation-write | 20/1m | user | no |  |
| 30 | POST | `/api/customers/me/after-sales/requests/{requestId:guid}/reconsideration` | Customer | installation-write | 20/1m | user | no |  |
| 31 | POST | `/api/customers/me/after-sales/requests/{requestId:guid}/confirm-appointment` | Customer | installation-write | 20/1m | user | no |  |
| 32 | POST | `/api/customers/me/after-sales/requests/{requestId:guid}/reschedule` | Customer | installation-write | 20/1m | user | no |  |
| 33 | POST | `/api/customers/me/after-sales/requests/{requestId:guid}/report-fault` | Customer | installation-write | 20/1m | user | no |  |
| 34 | POST | `/api/customers/me/after-sales/quotations/{quotationId:guid}/accept` | Customer | installation-write | 20/1m | user | no |  |
| 35 | POST | `/api/customers/me/after-sales/quotations/{quotationId:guid}/reject` | Customer | installation-write | 20/1m | user | no |  |
| 36 | POST | `/api/customers/me/after-sales/quotations/{quotationId:guid}/checkout` | Customer | payment-create | 5/10m | user + contractId | yes |  |
| 37 | GET | `/api/customers/me/after-sales/payments/{attemptId:guid}` | Customer | payment-status | 30/1m | user | no |  |
| 38 | GET | `/api/customers/me/after-sales/packages` | Customer | installation-read | 90/1m | user | no |  |
| 39 | POST | `/api/customers/me/after-sales/purchases` | Customer | payment-create | 5/10m | user + contractId | yes |  |
| 40 | GET | `/api/customers/me/after-sales/subscriptions` | Customer | installation-read | 90/1m | user | no |  |
| 41 | POST | `/api/customers/me/after-sales/subscriptions/{subscriptionId:guid}/cancel` | Customer | installation-write | 20/1m | user | no |  |
| 42 | POST | `/api/customers/me/after-sales/maintenance/{entitlementId:guid}/book` | Customer | installation-write | 20/1m | user | no |  |
| 43 | GET | `/api/customers/me/after-sales/notifications` | Customer | installation-read | 90/1m | user | no |  |
| 44 | POST | `/api/customers/me/after-sales/notifications/{notificationId:guid}/read` | Customer | installation-write | 20/1m | user | no |  |
| 45 | GET | `/api/technicians/me/after-sales/visits` | Technician | installation-read | 90/1m | user | no |  |
| 46 | GET | `/api/technicians/me/after-sales/visits/{visitId:guid}` | Technician | installation-read | 90/1m | user | no |  |
| 47 | GET | `/api/technicians/me/after-sales/visits/{visitId:guid}/evidence/{evidenceId:guid}/url` | Technician | installation-read | 90/1m | user | no |  |
| 48 | POST | `/api/technicians/me/after-sales/visits/{visitId:guid}/respond` | Technician | installation-write | 20/1m | user | no |  |
| 49 | POST | `/api/technicians/me/after-sales/visits/{visitId:guid}/start` | Technician | installation-write | 20/1m | user | no |  |
| 50 | POST | `/api/technicians/me/after-sales/visits/{visitId:guid}/evidence` | Technician | installation-write | 20/1m | user | no |  |
| 51 | POST | `/api/technicians/me/after-sales/visits/{visitId:guid}/inspection` | Technician | installation-write | 20/1m | user | no |  |
| 52 | POST | `/api/technicians/me/after-sales/visits/{visitId:guid}/continue-repair` | Technician | installation-write | 20/1m | user | no |  |
| 53 | POST | `/api/technicians/me/after-sales/visits/{visitId:guid}/finish` | Technician | installation-write | 20/1m | user | no |  |
| 54 | POST | `/api/technicians/me/after-sales/visits/{visitId:guid}/supplementary-items` | Technician | installation-write | 20/1m | user | no |  |
| 55 | POST | `/api/technicians/me/after-sales/visits/{visitId:guid}/work-report` | Technician | installation-write | 20/1m | user | no |  |
| 56 | POST | `/api/technicians/me/after-sales/visits/{visitId:guid}/absence` | Technician | installation-write | 20/1m | user | no |  |
| 57 | GET | `/api/sales/after-sales/warranty-options` | Sales | installation-read | 90/1m | user | no |  |
| 58 | GET | `/api/sales/after-sales/tasks/open` | Sales | installation-read | 90/1m | user | no |  |
| 59 | GET | `/api/sales/after-sales/tasks/mine` | Sales | installation-read | 90/1m | user | no |  |
| 60 | POST | `/api/sales/after-sales/tasks/{taskId:guid}/claim` | Sales | installation-write | 20/1m | user | no |  |
| 61 | POST | `/api/sales/after-sales/tasks/{taskId:guid}/start` | Sales | installation-write | 20/1m | user | no |  |
| 62 | POST | `/api/sales/after-sales/tasks/{taskId:guid}/follow-up` | Sales | installation-write | 20/1m | user | no |  |
| 63 | GET | `/api/sales/after-sales/requests/{requestId:guid}` | Sales | installation-read | 90/1m | user | no |  |
| 64 | POST | `/api/sales/after-sales/requests/{requestId:guid}/quotations` | Sales | installation-write | 20/1m | user | no |  |
| 65 | GET | `/api/manager/after-sales/requests` | Manager,Admin | installation-read | 90/1m | user | no |  |
| 66 | GET | `/api/manager/after-sales/requests/{requestId:guid}` | Manager,Admin | installation-read | 90/1m | user | no |  |
| 67 | GET | `/api/manager/after-sales/requests/{requestId:guid}/evidence/{evidenceId:guid}/url` | Manager,Admin | installation-read | 90/1m | user | no |  |
| 68 | POST | `/api/manager/after-sales/requests/{requestId:guid}/visits` | Manager,Admin | installation-scheduling | 10/1m | user | yes |  |
| 69 | POST | `/api/manager/after-sales/requests/{requestId:guid}/coverage` | Manager,Admin | installation-write | 20/1m | user | no |  |
| 70 | POST | `/api/manager/after-sales/requests/{requestId:guid}/claim-decision` | Manager,Admin | installation-write | 20/1m | user | no |  |
| 71 | POST | `/api/manager/after-sales/requests/{requestId:guid}/duplicate` | Manager,Admin | installation-write | 20/1m | user | no |  |
| 72 | POST | `/api/manager/after-sales/requests/{requestId:guid}/dispute-review` | Manager,Admin | installation-write | 20/1m | user | no |  |
| 73 | POST | `/api/manager/after-sales/requests/{requestId:guid}/cancellation-decision` | Manager,Admin | installation-write | 20/1m | user | no |  |
| 74 | POST | `/api/manager/after-sales/requests/{requestId:guid}/stop` | Manager,Admin | installation-write | 20/1m | user | no |  |
| 75 | POST | `/api/manager/after-sales/requests/{requestId:guid}/not-fulfilled` | Manager,Admin | installation-write | 20/1m | user | no |  |
| 76 | POST | `/api/manager/after-sales/assets/{assetId:guid}/warranty-activation` | Manager,Admin | installation-write | 20/1m | user | no |  |
| 77 | PUT | `/api/manager/after-sales/assets/{assetId:guid}` | Manager,Admin | installation-write | 20/1m | user | no |  |
| 78 | POST | `/api/manager/after-sales/subscriptions/{subscriptionId:guid}/cancellation-decision` | Manager,Admin | installation-write | 20/1m | user | no |  |
| 79 | GET | `/api/manager/after-sales/tasks` | Manager,Admin | installation-read | 90/1m | user | no |  |
| 80 | POST | `/api/manager/after-sales/tasks/{taskId:guid}/assign` | Manager,Admin | installation-write | 20/1m | user | no |  |
| 81 | GET | `/api/admin/after-sales/packages` | Admin | installation-read | 90/1m | user | no |  |
| 82 | POST | `/api/admin/after-sales/packages` | Admin | installation-write | 20/1m | user | no |  |
| 83 | PUT | `/api/admin/after-sales/packages/{packageId:guid}` | Admin | installation-write | 20/1m | user | no |  |
| 84 | GET | `/api/admin/after-sales/warranty-options` | Admin | installation-read | 90/1m | user | no |  |
| 85 | PUT | `/api/admin/after-sales/warranty-options` | Admin | installation-write | 20/1m | user | no |  |
| 86 | GET | `/api/admin/after-sales/dashboard` | Admin | installation-read | 90/1m | user | no |  |
| 87 | GET | `/api/admin/after-sales/dashboard/export` | Admin | installation-read | 90/1m | user | no |  |
| 88 | POST | `/api/auth/register` | anonymous (no attribute) | auth-register | 5/10m | IP | no | IP-only |
| 89 | POST | `/api/auth/verify-email` | anonymous (no attribute) | auth-verify-email | 20/10m | IP | no |  |
| 90 | POST | `/api/auth/resend-verification` | anonymous (no attribute) | auth-resend-verification | 3/15m | IP | no |  |
| 91 | POST | `/api/auth/login` | anonymous (no attribute) | auth-login | 10/10m | IP | no | IP-only: shared NAT/proxy IP throttles all users; distributed IPs bypass (no per-account limit) |
| 92 | POST | `/api/auth/refresh` | anonymous (no attribute) | auth-refresh | 30/10m | IP | no |  |
| 93 | POST | `/api/auth/logout` | anonymous (no attribute) | auth-refresh | 30/10m | IP | no |  |
| 94 | POST | `/api/auth/forgot-password` | anonymous (no attribute) | auth-forgot-password | 3/15m | IP | no | IP-only; no per-email limit |
| 95 | POST | `/api/auth/reset-password` | anonymous (no attribute) | auth-reset-password | 10/15m | IP | no | IP-only; token entropy 256 bit |
| 96 | POST | `/api/auth/change-password` | any authenticated | auth-change-password | 10/15m | IP | no |  |
| 97 | POST | `/api/quotations/{quotationId:guid}/contracts` | Sales | contract-write | 10/1m | user | no |  |
| 98 | GET | `/api/survey-requests/{surveyRequestId:guid}/contracts` | Sales,Manager,Admin | contract-read | 60/1m | user | no |  |
| 99 | GET | `/api/contracts/{contractId:guid}` | Sales,Manager,Admin | contract-read | 60/1m | user | no |  |
| 100 | PUT | `/api/contracts/{contractId:guid}` | Sales | contract-write | 10/1m | user | no |  |
| 101 | POST | `/api/contracts/{contractId:guid}/issue` | Sales | contract-write | 10/1m | user | no |  |
| 102 | POST | `/api/contracts/{contractId:guid}/discard` | Sales | contract-write | 10/1m | user | no |  |
| 103 | PUT | `/api/contracts/{contractId:guid}/official-document` | Sales | contract-write | 10/1m | user | no |  |
| 104 | GET | `/api/contracts/{contractId:guid}/official-document/url` | Sales,Manager,Admin | contract-read | 60/1m | user | no |  |
| 105 | GET | `/api/contracts/{contractId:guid}/submissions/{submissionId:guid}/url` | Sales,Manager,Admin | contract-read | 60/1m | user | no |  |
| 106 | POST | `/api/contracts/{contractId:guid}/publish` | Sales | contract-write | 10/1m | user | no |  |
| 107 | POST | `/api/contracts/{contractId:guid}/review` | Sales,Manager | contract-write | 10/1m | user | no |  |
| 108 | POST | `/api/contracts/{contractId:guid}/amendments` | Sales | contract-write | 10/1m | user | no |  |
| 109 | GET | `/api/customers/me/contracts` | Customer | contract-read | 60/1m | user | no |  |
| 110 | GET | `/api/customers/me/contracts/{contractId:guid}` | Customer | contract-read | 60/1m | user | no |  |
| 111 | POST | `/api/customers/me/contracts/{contractId:guid}/signed-document` | Customer | contract-write | 10/1m | user | no |  |
| 112 | GET | `/api/customers/me/contracts/{contractId:guid}/official-document/url` | Customer | contract-read | 60/1m | user | no |  |
| 113 | GET | `/api/customers/me/contracts/{contractId:guid}/submissions/{submissionId:guid}/url` | Customer | contract-read | 60/1m | user | no |  |
| 114 | GET | `/api/survey-slots` | Customer | scheduling-read | 20/1m | user | no |  |
| 115 | GET | `/api/customers/me/survey-requests` | Customer | **none** | — | — | — | no HTTP limit (authenticated) |
| 116 | GET | `/api/customers/me/survey-requests/{surveyRequestId:guid}` | Customer | **none** | — | — | — | no HTTP limit (authenticated) |
| 117 | POST | `/api/customers/me/survey-requests/{surveyRequestId:guid}/booking` | Customer | scheduling-write | 10/1m | user | no |  |
| 118 | PUT | `/api/customers/me/survey-requests/{surveyRequestId:guid}/booking` | Customer | scheduling-write | 10/1m | user | no |  |
| 119 | POST | `/api/customers/me/survey-requests/{surveyRequestId:guid}/booking/cancel` | Customer | scheduling-write | 10/1m | user | no |  |
| 120 | GET | `/api/customers/me/sites` | Customer | **none** | — | — | — | no HTTP limit (authenticated) |
| 121 | GET | `/api/customers/me/pre-surveys` | Customer | **none** | — | — | — | no HTTP limit (authenticated) |
| 122 | POST | `/api/customers/me` | Customer | **none** | — | — | — | no HTTP limit (authenticated) |
| 123 | POST | `/api/customers/me/sites` | Customer | **none** | — | — | — | no HTTP limit (authenticated) |
| 124 | POST | `/api/customers/me/survey-requests/{surveyRequestId:guid}/dispatch-request` | Customer | scheduling-write | 10/1m | user | no |  |
| 125 | GET | `/api/customers/me/survey-requests/{surveyRequestId:guid}/dispatch-request` | Customer | scheduling-read | 20/1m | user | no |  |
| 126 | POST | `/api/customers/me/survey-requests/{surveyRequestId:guid}/dispatch-request/proposals/{proposalId:guid}/accept` | Customer | scheduling-write | 10/1m | user | no |  |
| 127 | POST | `/api/customers/me/survey-requests/{surveyRequestId:guid}/dispatch-request/proposals/{proposalId:guid}/decline` | Customer | scheduling-write | 10/1m | user | no |  |
| 128 | POST | `/api/customers/me/survey-requests/{surveyRequestId:guid}/dispatch-request/cancel` | Customer | scheduling-write | 10/1m | user | no |  |
| 129 | GET | `/api/admin/dispatch-requests` | Manager,Admin | scheduling-read | 20/1m | user | no |  |
| 130 | GET | `/api/admin/dispatch-requests/{dispatchRequestId:guid}` | Manager,Admin | scheduling-read | 20/1m | user | no |  |
| 131 | GET | `/api/admin/dispatch-requests/{dispatchRequestId:guid}/candidates` | Manager,Admin | scheduling-read | 20/1m | user | no |  |
| 132 | POST | `/api/admin/dispatch-requests/{dispatchRequestId:guid}/assign` | Manager,Admin | scheduling-write | 10/1m | user | no |  |
| 133 | POST | `/api/admin/dispatch-requests/{dispatchRequestId:guid}/proposals` | Manager,Admin | scheduling-write | 10/1m | user | no |  |
| 134 | POST | `/api/admin/dispatch-requests/{dispatchRequestId:guid}/proposals/{proposalId:guid}/withdraw` | Manager,Admin | scheduling-write | 10/1m | user | no |  |
| 135 | POST | `/api/admin/dispatch-requests/{dispatchRequestId:guid}/unserviceable` | Manager,Admin | scheduling-write | 10/1m | user | no |  |
| 136 | POST | `/api/admin/dispatch-requests/{dispatchRequestId:guid}/close` | Manager,Admin | scheduling-write | 10/1m | user | no |  |
| 137 | POST | `/api/files/presigned-upload` | any authenticated | files-write | 20/1m | user | yes |  |
| 138 | POST | `/api/files` | any authenticated | files-write | 20/1m | user | yes |  |
| 139 | POST | `/api/files/{fileId:guid}/verify` | any authenticated | files-write | 20/1m | user | yes |  |
| 140 | GET | `/api/files/{fileId:guid}` | any authenticated | files-read | 60/1m | user | no |  |
| 141 | GET | `/api/files/public/{fileId:guid}` | anonymous | files-public-read | 120/1m | IP | no | IP; anonymous redirect |
| 142 | GET | `/api/installation-projects/{projectId:guid}/energy-monitoring` | Manager,Admin | installation-read | 90/1m | user | no |  |
| 143 | GET | `/api/installations/ready-contracts` | Manager,Admin | installation-read | 90/1m | user | no |  |
| 144 | POST | `/api/contracts/{contractId:guid}/installation-project` | Manager,Admin | installation-write | 20/1m | user | no |  |
| 145 | GET | `/api/installation-projects` | Manager,Admin | installation-read | 90/1m | user | no |  |
| 146 | GET | `/api/installation-projects/{projectId:guid}` | Manager,Admin | installation-read | 90/1m | user | no |  |
| 147 | GET | `/api/installation-projects/{projectId:guid}/technician-candidates` | Manager,Admin | installation-preview | 15/1m | user | yes |  |
| 148 | PUT | `/api/installation-projects/{projectId:guid}/assignment` | Manager,Admin | installation-scheduling | 10/1m | user | yes |  |
| 149 | POST | `/api/installation-projects/{projectId:guid}/cancel` | Manager,Admin | installation-write | 20/1m | user | no |  |
| 150 | POST | `/api/installation-projects/{projectId:guid}/return-to-rework` | Manager,Admin | installation-write | 20/1m | user | no |  |
| 151 | POST | `/api/installation-projects/{projectId:guid}/equipment-substitution/approve` | Manager | installation-write | 20/1m | user | no |  |
| 152 | PUT | `/api/installation-projects/{projectId:guid}/checklist/{itemId:guid}` | Manager,Admin | installation-write | 20/1m | user | no |  |
| 153 | GET | `/api/installation-projects/{projectId:guid}/evidence` | Manager,Admin | installation-read | 90/1m | user | no |  |
| 154 | GET | `/api/installation-projects/{projectId:guid}/evidence/{evidenceId:guid}/url` | Manager,Admin | installation-read | 90/1m | user | no |  |
| 155 | GET | `/api/installation-projects/{projectId:guid}/handover` | Manager,Admin | installation-read | 90/1m | user | no |  |
| 156 | GET | `/api/technicians/me/installation-projects` | Technician | installation-read | 90/1m | user | no |  |
| 157 | GET | `/api/technicians/me/installation-projects/{projectId:guid}` | Technician | installation-read | 90/1m | user | no |  |
| 158 | POST | `/api/technicians/me/installation-projects/{projectId:guid}/accept` | Technician | installation-write | 20/1m | user | no |  |
| 159 | POST | `/api/technicians/me/installation-projects/{projectId:guid}/decline` | Technician | installation-write | 20/1m | user | no |  |
| 160 | POST | `/api/technicians/me/installation-projects/{projectId:guid}/start` | Technician | installation-write | 20/1m | user | no |  |
| 161 | POST | `/api/technicians/me/installation-projects/{projectId:guid}/evidence` | Technician | installation-write | 20/1m | user | no |  |
| 162 | GET | `/api/technicians/me/installation-projects/{projectId:guid}/evidence` | Technician | installation-read | 90/1m | user | no |  |
| 163 | GET | `/api/technicians/me/installation-projects/{projectId:guid}/evidence/{evidenceId:guid}/url` | Technician | installation-read | 90/1m | user | no |  |
| 164 | POST | `/api/technicians/me/installation-projects/{projectId:guid}/assets` | Technician | installation-write | 20/1m | user | no |  |
| 165 | POST | `/api/technicians/me/installation-projects/{projectId:guid}/completion-report` | Technician | installation-write | 20/1m | user | no |  |
| 166 | GET | `/api/customers/me/installation-projects/{projectId:guid}/energy-monitoring` | Customer | installation-read | 90/1m | user | no |  |
| 167 | GET | `/api/customers/me/installation-projects` | Customer | installation-read | 90/1m | user | no |  |
| 168 | GET | `/api/customers/me/installation-projects/{projectId:guid}` | Customer | installation-read | 90/1m | user | no |  |
| 169 | GET | `/api/customers/me/installation-projects/{projectId:guid}/evidence` | Customer | installation-read | 90/1m | user | no |  |
| 170 | GET | `/api/customers/me/installation-projects/{projectId:guid}/evidence/{evidenceId:guid}/url` | Customer | installation-read | 90/1m | user | no |  |
| 171 | POST | `/api/customers/me/installation-projects/{projectId:guid}/acceptance` | Customer | installation-write | 20/1m | user | no |  |
| 172 | POST | `/api/customers/me/installation-projects/{projectId:guid}/equipment-substitution/approve` | Customer | installation-write | 20/1m | user | no |  |
| 173 | GET | `/api/customers/me/installation-projects/{projectId:guid}/handover` | Customer | installation-read | 90/1m | user | no |  |
| 174 | POST | `/api/admin/iot/devices` | Admin | iot-write | 20/1m | user | no |  |
| 175 | GET | `/api/admin/iot/devices` | Admin | iot-read | 60/1m | user | no |  |
| 176 | GET | `/api/admin/iot/devices/{deviceId:guid}` | Admin | iot-read | 60/1m | user | no |  |
| 177 | POST | `/api/admin/iot/devices/{deviceId:guid}/disable` | Admin | iot-write | 20/1m | user | no |  |
| 178 | POST | `/api/admin/iot/devices/{deviceId:guid}/enable` | Admin | iot-write | 20/1m | user | no |  |
| 179 | POST | `/api/admin/iot/devices/{deviceId:guid}/rotate-credential` | Admin | iot-write | 20/1m | user | no |  |
| 180 | POST | `/api/installation-projects/{projectId:guid}/iot-device` | Manager,Admin | iot-write | 20/1m | user | no |  |
| 181 | GET | `/api/iot/bindings` | Manager,Admin | iot-read | 60/1m | user | no |  |
| 182 | POST | `/api/iot/bindings/{bindingId:guid}/release` | Manager,Admin | iot-write | 20/1m | user | no |  |
| 183 | GET | `/api/installation-projects/{projectId:guid}/energy-monitoring/device` | Manager,Admin | iot-read | 60/1m | user | no |  |
| 184 | GET | `/api/installation-projects/{projectId:guid}/energy-monitoring/readings/latest` | Manager,Admin | iot-read | 60/1m | user | no |  |
| 185 | GET | `/api/installation-projects/{projectId:guid}/energy-monitoring/readings` | Manager,Admin | iot-read | 60/1m | user | no |  |
| 186 | GET | `/api/customers/me/installation-projects/{projectId:guid}/energy-monitoring/device` | Customer | iot-read | 60/1m | user | no |  |
| 187 | GET | `/api/customers/me/installation-projects/{projectId:guid}/energy-monitoring/readings/latest` | Customer | iot-read | 60/1m | user | no |  |
| 188 | GET | `/api/customers/me/installation-projects/{projectId:guid}/energy-monitoring/readings` | Customer | iot-read | 60/1m | user | no |  |
| 189 | POST | `/api/iot/devices/{deviceId}/readings` | anonymous | iot-telemetry-ingest | 12/1m | IP + deviceId | no | **bypass**: new bucket per deviceId (reproduced) |
| 190 | GET | `/api/admin/knowledge-documents` | Admin | rag-admin-read | 60/1m | user | no |  |
| 191 | GET | `/api/admin/knowledge-documents/{documentId:guid}` | Admin | rag-admin-read | 60/1m | user | no |  |
| 192 | POST | `/api/admin/knowledge-documents` | Admin | rag-admin-register | 10/1m | user | yes |  |
| 193 | POST | `/api/admin/knowledge-documents/{documentId:guid}/versions` | Admin | rag-admin-register | 10/1m | user | yes |  |
| 194 | PUT | `/api/admin/knowledge-documents/{documentId:guid}` | Admin | rag-admin-register | 10/1m | user | yes |  |
| 195 | PUT | `/api/admin/knowledge-documents/{documentId:guid}/audience` | Admin | rag-admin-publish | 10/1m | user | no |  |
| 196 | POST | `/api/admin/knowledge-documents/{documentId:guid}/publish` | Admin | rag-admin-publish | 10/1m | user | no |  |
| 197 | POST | `/api/admin/knowledge-documents/{documentId:guid}/unpublish` | Admin | rag-admin-publish | 10/1m | user | no |  |
| 198 | POST | `/api/admin/knowledge-documents/{documentId:guid}/reindex` | Admin | rag-admin-reindex | 3/10m | user | yes |  |
| 199 | POST | `/api/admin/knowledge-documents/{documentId:guid}/jobs/{jobId:guid}/retry` | Admin | rag-admin-reindex | 3/10m | user | yes |  |
| 200 | POST | `/api/admin/knowledge-documents/{documentId:guid}/archive` | Admin | rag-admin-publish | 10/1m | user | no |  |
| 201 | GET | `/api/admin/rag-configuration` | Admin | rag-admin-read | 60/1m | user | no |  |
| 202 | GET | `/api/admin/rag-configuration/models` | Admin | rag-admin-read | 60/1m | user | no |  |
| 203 | GET | `/api/admin/rag-configuration/versions` | Admin | rag-admin-read | 60/1m | user | no |  |
| 204 | PUT | `/api/admin/rag-configuration/draft` | Admin | rag-admin-config | 10/1m | user | yes |  |
| 205 | POST | `/api/admin/rag-configuration/draft/validate` | Admin | rag-admin-config | 10/1m | user | yes |  |
| 206 | POST | `/api/admin/rag-configuration/draft/activate` | Admin | rag-admin-config | 10/1m | user | yes |  |
| 207 | POST | `/api/admin/rag-configuration/rollback` | Admin | rag-admin-config | 10/1m | user | yes |  |
| 208 | DELETE | `/api/admin/rag-configuration/draft` | Admin | rag-admin-config | 10/1m | user | yes |  |
| 209 | POST | `/api/admin/rag-configuration/reindex-stale` | Admin | rag-admin-reindex | 3/10m | user | yes |  |
| 210 | POST | `/api/chat/messages` | any authenticated | rag-chat | 10/1m | user | yes |  |
| 211 | GET | `/api/chat/sessions` | any authenticated | rag-history | 60/1m | user | no |  |
| 212 | GET | `/api/chat/sessions/{sessionId:guid}` | any authenticated | rag-history | 60/1m | user | no |  |
| 213 | GET | `/api/technicians/me/appointments/{appointmentId:guid}/onsite-survey` | Technician | onsite-survey-read | 60/1m | user | no |  |
| 214 | POST | `/api/technicians/me/appointments/{appointmentId:guid}/onsite-survey/start` | Technician | onsite-survey-write | 20/1m | user | no |  |
| 215 | PUT | `/api/technicians/me/appointments/{appointmentId:guid}/onsite-survey` | Technician | onsite-survey-write | 20/1m | user | no |  |
| 216 | POST | `/api/technicians/me/appointments/{appointmentId:guid}/onsite-survey/attachments` | Technician | onsite-survey-write | 20/1m | user | no |  |
| 217 | GET | `/api/technicians/me/appointments/{appointmentId:guid}/onsite-survey/attachments/{attachmentId:guid}/url` | Technician | onsite-survey-read | 60/1m | user | no |  |
| 218 | DELETE | `/api/technicians/me/appointments/{appointmentId:guid}/onsite-survey/attachments/{attachmentId:guid}` | Technician | onsite-survey-write | 20/1m | user | no |  |
| 219 | POST | `/api/technicians/me/appointments/{appointmentId:guid}/onsite-survey/complete` | Technician | onsite-survey-write | 20/1m | user | no |  |
| 220 | POST | `/api/technicians/me/appointments/{appointmentId:guid}/onsite-survey/amendments` | Technician | onsite-survey-write | 20/1m | user | no |  |
| 221 | GET | `/api/survey-requests/{surveyRequestId:guid}/onsite-survey` | Sales,Manager,Admin | onsite-survey-read | 60/1m | user | no |  |
| 222 | GET | `/api/survey-requests/{surveyRequestId:guid}/onsite-survey/attachments/{attachmentId:guid}/url` | Sales,Manager,Admin | onsite-survey-read | 60/1m | user | no |  |
| 223 | GET | `/api/admin/operational-zones` | Admin,Manager | scheduling-read | 20/1m | user | no |  |
| 224 | GET | `/api/admin/operational-zones/coverage` | Admin,Manager | scheduling-read | 20/1m | user | no |  |
| 225 | GET | `/api/admin/operational-zones/{zoneId:guid}` | Admin,Manager | scheduling-read | 20/1m | user | no |  |
| 226 | POST | `/api/admin/operational-zones` | Admin | scheduling-write | 10/1m | user | no |  |
| 227 | PUT | `/api/admin/operational-zones/{zoneId:guid}` | Admin | scheduling-write | 10/1m | user | no |  |
| 228 | PUT | `/api/admin/operational-zones/{zoneId:guid}/communes` | Admin | scheduling-write | 10/1m | user | no |  |
| 229 | GET | `/api/admin/operational-zones/{zoneId:guid}/technicians` | Admin,Manager | scheduling-read | 20/1m | user | no |  |
| 230 | POST | `/api/admin/survey-appointments/{appointmentId:guid}/assign-technician` | Admin,Manager | scheduling-write | 10/1m | user | no |  |
| 231 | PUT | `/api/admin/survey-requests/{surveyRequestId:guid}/sales-owner` | Admin,Manager | scheduling-write | 10/1m | user | no |  |
| 232 | PUT | `/api/admin/survey-requests/{surveyRequestId:guid}/reschedule` | Admin,Manager | scheduling-write | 10/1m | user | no |  |
| 233 | PUT | `/api/customers/me/sites/{siteId:guid}/administrative-location` | Customer | scheduling-write | 10/1m | user | no |  |
| 234 | POST | `/api/customers/me/contracts/{contractId:guid}/payments` | Customer | payment-create | 5/10m | user + contractId | yes |  |
| 235 | GET | `/api/customers/me/contracts/{contractId:guid}/payments` | Customer | payment-status | 30/1m | user | no |  |
| 236 | POST | `/api/customers/me/contracts/{contractId:guid}/payments/cancel` | Customer | payment-create | 5/10m | user + contractId | yes |  |
| 237 | GET | `/api/contracts/{contractId:guid}/payments` | Sales,Manager,Admin | payment-status | 30/1m | user | no |  |
| 238 | POST | `/api/payments/payos/webhook` | anonymous | payos-webhook | 600/1m | IP | no | IP; HMAC verified before work |
| 239 | GET | `/api/pre-surveys/{preSurveyId:guid}/surface` | Customer | **none** | — | — | — | no HTTP limit (authenticated) |
| 240 | PUT | `/api/pre-surveys/{preSurveyId:guid}/surface` | Customer | **none** | — | — | — | no HTTP limit (authenticated) |
| 241 | POST | `/api/pre-surveys` | Customer | **none** | — | — | — | no HTTP limit (authenticated) |
| 242 | POST | `/api/pre-surveys/{preSurveyId:guid}/submit` | Customer | **none** | — | — | — | no HTTP limit (authenticated) |
| 243 | PUT | `/api/pre-surveys/{preSurveyId:guid}` | Customer | **none** | — | — | — | no HTTP limit (authenticated) |
| 244 | GET | `/api/products` | anonymous (no attribute) | catalog-read | 60/1m | user, IP if anonymous | no | IP when anonymous |
| 245 | GET | `/api/products/{id:guid}` | anonymous (no attribute) | catalog-read | 60/1m | user, IP if anonymous | no | IP when anonymous |
| 246 | POST | `/api/survey-requests/{surveyRequestId:guid}/quotations` | Sales | quotation-write | 20/1m | user | no |  |
| 247 | POST | `/api/survey-requests/{surveyRequestId:guid}/quotations/generate` | Sales,Manager,Admin | quotation-write | 20/1m | user | no |  |
| 248 | GET | `/api/quotations/pending-review` | Sales,Manager,Admin | quotation-read | 60/1m | user | no |  |
| 249 | GET | `/api/survey-requests/{surveyRequestId:guid}/quotations` | Sales,Manager,Admin | quotation-read | 60/1m | user | no |  |
| 250 | GET | `/api/quotations/{quotationId:guid}` | Sales,Manager,Admin | quotation-read | 60/1m | user | no |  |
| 251 | PUT | `/api/quotations/{quotationId:guid}` | Sales | quotation-write | 20/1m | user | no |  |
| 252 | POST | `/api/quotations/{quotationId:guid}/lines` | Sales | quotation-write | 20/1m | user | no |  |
| 253 | PUT | `/api/quotations/{quotationId:guid}/lines/{lineId:guid}` | Sales | quotation-write | 20/1m | user | no |  |
| 254 | DELETE | `/api/quotations/{quotationId:guid}/lines/{lineId:guid}` | Sales | quotation-write | 20/1m | user | no |  |
| 255 | POST | `/api/quotations/{quotationId:guid}/issue` | Sales | quotation-write | 20/1m | user | no |  |
| 256 | POST | `/api/quotations/{quotationId:guid}/confirm-survey-version` | Sales | quotation-write | 20/1m | user | no |  |
| 257 | POST | `/api/quotations/{quotationId:guid}/pricing-approval-request` | Sales | quotation-write | 20/1m | user | no |  |
| 258 | POST | `/api/quotations/{quotationId:guid}/revisions` | Sales | quotation-write | 20/1m | user | no |  |
| 259 | PUT | `/api/quotations/{quotationId:guid}/document` | Sales | quotation-write | 20/1m | user | no |  |
| 260 | GET | `/api/manager/quotations/pricing-approvals` | Manager | quotation-read | 60/1m | user | no |  |
| 261 | POST | `/api/manager/quotations/{quotationId:guid}/pricing-approval` | Manager | quotation-write | 20/1m | user | no |  |
| 262 | GET | `/api/customers/me/quotations` | Customer | quotation-read | 60/1m | user | no |  |
| 263 | GET | `/api/customers/me/quotations/{quotationId:guid}` | Customer | quotation-read | 60/1m | user | no |  |
| 264 | POST | `/api/customers/me/quotations/{quotationId:guid}/accept` | Customer | quotation-write | 20/1m | user | no |  |
| 265 | POST | `/api/customers/me/quotations/{quotationId:guid}/reject` | Customer | quotation-write | 20/1m | user | no |  |
| 266 | POST | `/api/customers/me/quotations/{quotationId:guid}/revision-request` | Customer | quotation-write | 20/1m | user | no |  |
| 267 | POST | `/api/customers/me/survey-requests/{surveyRequestId:guid}/withdraw` | Customer | quotation-write | 20/1m | user | no |  |
| 268 | POST | `/api/survey-requests/{surveyRequestId:guid}/close` | Sales | quotation-write | 20/1m | user | no |  |
| 269 | POST | `/api/pre-surveys/{preSurveyId:guid}/simulations` | Customer | simulation-write | 10/1m | user | no |  |
| 270 | GET | `/api/pre-surveys/{preSurveyId:guid}/simulations` | Customer,Sales,Technician | **none** | — | — | — | no HTTP limit (authenticated) |
| 271 | GET | `/api/pre-surveys/{preSurveyId:guid}/simulations/{simulationId:guid}` | Customer,Sales,Technician | **none** | — | — | — | no HTTP limit (authenticated) |
| 272 | POST | `/api/pre-surveys/{preSurveyId:guid}/simulations/{simulationId:guid}/select` | Customer | simulation-write | 10/1m | user | no |  |
| 273 | DELETE | `/api/pre-surveys/{preSurveyId:guid}/simulations/selection` | Customer | simulation-write | 10/1m | user | no |  |
| 274 | GET | `/api/survey-requests/pending` | Sales | **none** | — | — | — | no HTTP limit (authenticated) |
| 275 | POST | `/api/survey-requests/{surveyRequestId:guid}/claim` | Sales | **none** | — | — | — | no HTTP limit (authenticated) |
| 276 | GET | `/api/survey-requests/my` | Sales | **none** | — | — | — | no HTTP limit (authenticated) |
| 277 | GET | `/api/survey-requests/{surveyRequestId:guid}` | Sales | **none** | — | — | — | no HTTP limit (authenticated) |
| 278 | POST | `/api/survey-requests/{surveyRequestId:guid}/assign-technician` | Sales | **none** | — | — | — | no HTTP limit (authenticated) |
| 279 | PUT | `/api/survey-requests/{surveyRequestId:guid}/reassign-technician` | Sales | **none** | — | — | — | no HTTP limit (authenticated) |
| 280 | GET | `/api/survey-requests/escalated` | Sales | **none** | — | — | — | no HTTP limit (authenticated) |
| 281 | PUT | `/api/survey-requests/{surveyRequestId:guid}/reschedule` | Sales | **none** | — | — | — | no HTTP limit (authenticated) |
| 282 | GET | `/api/admin/system-settings/scheduling` | Admin,Manager | system-settings-read | 60/1m | user | no |  |
| 283 | PUT | `/api/admin/system-settings/scheduling` | Admin | system-settings-write | 10/1m | user | no |  |
| 284 | GET | `/api/admin/system-settings/scheduling/history` | Admin,Manager | system-settings-read | 60/1m | user | no |  |
| 285 | GET | `/api/technicians/availability` | Sales,Manager,Admin | scheduling-read | 20/1m | user | no |  |
| 286 | GET | `/api/technicians/me/appointments` | Technician | scheduling-read | 20/1m | user | no |  |
| 287 | GET | `/api/technicians/me/appointments/{appointmentId:guid}` | Technician | scheduling-read | 20/1m | user | no |  |
| 288 | GET | `/api/technicians/me/open-appointments` | Technician | scheduling-read | 20/1m | user | no |  |
| 289 | GET | `/api/technicians/me/open-appointments/{appointmentId:guid}` | Technician | scheduling-read | 20/1m | user | no |  |
| 290 | POST | `/api/technicians/me/appointments/{appointmentId:guid}/accept` | Technician | scheduling-write | 10/1m | user | no |  |
| 291 | POST | `/api/technicians/me/appointments/{appointmentId:guid}/release` | Technician | scheduling-write | 10/1m | user | no |  |
| 292 | GET | `/api/technicians/me/schedule` | Technician | scheduling-read | 20/1m | user | no |  |
| 293 | GET | `/api/technicians/{technicianUserId:guid}/schedule` | Manager,Admin | scheduling-read | 20/1m | user | no |  |
| 294 | GET | `/api/admin/technicians` | Admin,Manager | scheduling-read | 20/1m | user | no |  |
| 295 | PUT | `/api/admin/technicians/{technicianUserId:guid}/profile` | Admin,Manager | scheduling-write | 10/1m | user | no |  |
| 296 | PUT | `/api/admin/technicians/{technicianUserId:guid}/zones` | Admin,Manager | scheduling-write | 10/1m | user | no |  |
| 297 | POST | `/api/admin/technicians/{technicianUserId:guid}/unavailable-periods` | Admin,Manager | scheduling-write | 10/1m | user | no |  |
| 298 | GET | `/api/admin/technicians/{technicianUserId:guid}/unavailable-periods` | Admin,Manager | scheduling-read | 20/1m | user | no |  |
| 299 | GET | `/api/admin/technicians/{technicianUserId:guid}/working-schedule` | Admin,Manager | scheduling-read | 20/1m | user | no |  |
| 300 | PUT | `/api/admin/technicians/{technicianUserId:guid}/working-schedule` | Admin,Manager | scheduling-write | 10/1m | user | no |  |
| 301 | DELETE | `/api/admin/technicians/{technicianUserId:guid}/unavailable-periods/{periodId:guid}` | Admin,Manager | scheduling-write | 10/1m | user | no |  |
