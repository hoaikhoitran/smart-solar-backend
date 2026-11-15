# Giám sát điện năng IoT (ESP32 + ACS712) — Hướng dẫn tích hợp FE và firmware

Phiên bản V1 (15/11/2026; sửa 15/11/2026: giới hạn một thiết bị áp dụng **theo công trình**, không theo khách hàng). Backend nhận **dòng điện (A)** từ thiết bị ESP32 + cảm biến ACS712 qua HTTP POST mỗi 10 giây, lưu vào
PostgreSQL và hiển thị cho khách hàng bên cạnh sản lượng **mô phỏng** đã có. Mọi API dùng envelope quen thuộc
`{ "isSuccess", "traceId", "data", "error": { "code", "message", "details", "fieldErrors" } }`.

**Giới hạn V1 (quyết định business, không phải lỗi):** firmware chỉ gửi một giá trị `current`. Backend **không suy ra** công suất
(W), điện năng (kWh), sản lượng solar, điện mua / bán với lưới hay chiều dòng điện. Các đại lượng đó trả về `status: "NOT_AVAILABLE"`
và `value: null` cho tới khi nhóm IoT bổ sung cảm biến và firmware; không có số liệu giả, không dùng số mô phỏng thay số đo.

## 1. Luồng nghiệp vụ

```
Admin ── POST /api/admin/iot/devices ────────────► thiết bị + secret (hiện đúng một lần)
Manager/Admin ── POST /api/installation-projects/{projectId}/iot-device ─► binding ACTIVE (1 thiết bị / công trình, 1 binding / thiết bị)
ESP32 ── POST /api/iot/devices/{deviceId}/readings  (header X-Device-Key) ─► 201, bản ghi có receivedAt do server cấp
Customer ── GET /api/customers/me/installation-projects/{projectId}/energy-monitoring ─► simulated + measured
          ── GET …/energy-monitoring/readings/latest · GET …/energy-monitoring/readings?limit=
```

Quy tắc ràng buộc (được bảo đảm bằng unique index một phần trong PostgreSQL, nên request đồng thời cũng không tạo trùng):

* Mỗi **thiết bị** có `deviceId` duy nhất (3–64 ký tự chữ, số, `.`, `_`, `-`; lưu dạng chữ hoa, FE và firmware gửi kiểu nào cũng được).
* **Một khách hàng có thể sở hữu nhiều công trình** (`InstallationProject`), mỗi công trình có thiết bị riêng. Ví dụ: khách A có
  công trình 1 (thiết bị 1), công trình 2 (thiết bị 2), công trình 3 (chưa có thiết bị); A chọn từng công trình để xem số đo riêng.
* Mỗi **công trình** tối đa **một** binding ACTIVE; mỗi **thiết bị** tối đa **một** binding ACTIVE (unique index một phần
  `ux_iot_device_binding_active_project` và `ux_iot_device_binding_active_device`). Không còn giới hạn theo khách hàng.
* Binding tham chiếu một `InstallationProject` (mọi trạng thái trừ `CANCELLED`); `customerUserId` là chủ công trình, được chốt lúc
  gắn để lưu lịch sử. **Quyền xem luôn kiểm theo `InstallationProject.CustomerUserId` của `projectId` trong URL**; không tin
  `customerId` từ ESP32 (thiết bị không gửi và không thể gửi trường này).
* Tháo (`release`) hoặc thay thiết bị (`replaceCurrent: true`) **không xoá**: binding cũ thành `RELEASED` kèm lý do, người thao tác,
  thời điểm. `replaceCurrent` chỉ thay thiết bị của **công trình được chọn**; các công trình khác của cùng khách không bị đụng tới.
* Mọi bản ghi đo giữ nguyên `bindingId` (tức công trình) lúc nhận. Khi thiết bị chuyển từ công trình A sang công trình B (cùng
  khách hay khác khách), lịch sử **không** đi theo thiết bị: B không bao giờ thấy số đo của A, A vẫn xem được lịch sử của mình.
* Thiết bị `DISABLED` (Admin khoá) hoặc chưa gắn: từ chối số đo và **không** cung cấp số đo hiện tại cho khách.
* Khách không tự gắn thiết bị, không xem thiết bị / số đo của khách khác (404 `INSTALLATION_PROJECT_NOT_FOUND`, không lộ thông tin).

## 2. Endpoint và RBAC

| Vai trò | Method | Path | Rate limit |
|---|---|---|---|
| Admin | POST | `/api/admin/iot/devices` — đăng ký, trả secret một lần | `iot-write` 20/phút |
| Admin | GET | `/api/admin/iot/devices?status=ACTIVE\|DISABLED&search=&page=1&pageSize=20` | `iot-read` 60/phút |
| Admin | GET | `/api/admin/iot/devices/{id}` | `iot-read` |
| Admin | POST | `/api/admin/iot/devices/{id}/disable` `{ "reason", "expectedRevision"? }` | `iot-write` |
| Admin | POST | `/api/admin/iot/devices/{id}/enable?expectedRevision=` | `iot-write` |
| Admin | POST | `/api/admin/iot/devices/{id}/rotate-credential?expectedRevision=` — secret mới, secret cũ mất hiệu lực ngay | `iot-write` |
| Manager, Admin | POST | `/api/installation-projects/{projectId}/iot-device` `{ "deviceId", "note"?, "replaceCurrent"? }` | `iot-write` |
| Manager, Admin | GET | `/api/iot/bindings?projectId=&deviceId=&status=ACTIVE\|RELEASED` — lịch sử binding | `iot-read` |
| Manager, Admin | POST | `/api/iot/bindings/{bindingId}/release` `{ "reason" }` | `iot-write` |
| Manager, Admin | GET | `/api/installation-projects/{projectId}/energy-monitoring` (đã có, mở rộng) · `…/device` · `…/readings/latest` · `…/readings` | `installation-read` / `iot-read` |
| **ESP32** (không JWT) | POST | `/api/iot/devices/{deviceId}/readings` — header `X-Device-Key` | `iot-telemetry-ingest` 12/phút/thiết bị |
| Customer | GET | `/api/customers/me/installation-projects/{projectId}/energy-monitoring` (đã có, mở rộng) | `installation-read` |
| Customer | GET | `…/energy-monitoring/device` · `…/energy-monitoring/readings/latest` · `…/energy-monitoring/readings?from=&to=&before=&limit=` | `iot-read` |

Mọi endpoint (trừ ESP32) cần Bearer JWT và feature `Features__InstallationWorkflowEnabled=true` (503 `FEATURE_PENDING_MIGRATION`
khi tắt). Sai vai trò → 403 `AUTH_FORBIDDEN`. Khi database chưa áp dụng migration `IotEnergyMonitoring`, **mọi** request trả 503
`SCHEMA_NOT_READY` (kiểm tra sẵn có của API).

## 3. Request / response

### 3.1 Admin đăng ký thiết bị

`POST /api/admin/iot/devices`

```json
{ "deviceId": "esp32-001", "displayName": "Kẹp dòng inverter nhà anh A", "sensorModel": "ACS712-30A", "maxCurrentAmps": 30 }
```

201 (`Cache-Control: no-store`; `secret` chỉ xuất hiện ở đây và ở rotate):

```json
{
  "isSuccess": true, "traceId": "…",
  "data": {
    "device": {
      "id": "0b1e…", "deviceId": "ESP32-001", "displayName": "Kẹp dòng inverter nhà anh A", "status": "ACTIVE",
      "sensorModel": "ACS712-30A", "maxCurrentAmps": 30, "secretVersion": 1, "secretRotatedAt": "2026-11-15T07:00:00+00:00",
      "lastSeenAt": null, "connectionStatus": "UNASSIGNED", "currentBinding": null,
      "disabledAt": null, "disabledReason": null, "createdAt": "…", "updatedAt": "…", "revision": 1
    },
    "secret": "ssdk_…43 ký tự…",
    "secretVersion": 1
  },
  "error": null
}
```

* `maxCurrentAmps` (0 < x ≤ 1000) là dải đo của cảm biến; bỏ trống = `IotMonitoring__DefaultMaxCurrentAmps` (30, ACS712-30A).
  Số đo có |current| vượt dải bị từ chối 400.
* Secret: 256 bit từ CSPRNG, tiền tố `ssdk_`; server chỉ lưu SHA-256. Mất secret thì **rotate**, không có API đọc lại.
* `GET /api/admin/iot/devices` trả `{ items: [device…], page, pageSize, totalItems, totalPages }`; `search` khớp `deviceId` / tên.

### 3.2 Manager gắn thiết bị

`POST /api/installation-projects/{projectId}/iot-device`

```json
{ "deviceId": "ESP32-001", "note": "Kẹp dòng đầu ra AC của inverter", "replaceCurrent": false }
```

201:

```json
{
  "bindingId": "…", "deviceRecordId": "0b1e…", "deviceId": "ESP32-001", "deviceDisplayName": "…",
  "projectId": "…", "customerUserId": "…", "status": "ACTIVE", "boundAt": "…", "boundByUserId": "…", "note": "…",
  "releasedAt": null, "releasedByUserId": null, "releaseReason": null
}
```

* Công trình đã có thiết bị khác → 409 `IOT_PROJECT_ALREADY_BOUND` (`details.bindingId`, `details.projectId`); gửi lại với
  `replaceCurrent: true` để tháo thiết bị cũ **của công trình này** (lý do tự ghi "Replaced by device …") và gắn thiết bị mới trong
  cùng một giao dịch. Khách có thiết bị ở công trình khác không ảnh hưởng gì.
* Thiết bị đang gắn cho công trình khác → 409 `IOT_DEVICE_ALREADY_BOUND`: phải `release` ở đó trước (không "cướp" thiết bị).
* Gắn lại đúng thiết bị cho đúng công trình → 201 với binding hiện có (idempotent).
* `POST /api/iot/bindings/{bindingId}/release` `{ "reason": "Khách huỷ dịch vụ" }` → 200 binding `RELEASED`; thiết bị về `UNASSIGNED`.

### 3.3 ESP32 gửi số đo

```
POST /api/iot/devices/ESP32-001/readings
Content-Type: application/json
X-Device-Key: ssdk_…

{ "current": 0.2145 }
```

201:

```json
{ "isSuccess": true, "traceId": "…", "data": { "readingId": "…", "receivedAt": "2026-11-15T07:00:10.1234567+00:00", "currentAmps": 0.2145, "connectionStatus": "ONLINE" }, "error": null }
```

* `current`: số JSON hữu hạn (A), dấu giữ nguyên như đo được, |current| ≤ `maxCurrentAmps` của thiết bị. Server không đổi dấu,
  không làm tròn, không suy ra chiều dòng điện.
* `receivedAt` là giờ UTC server lúc nhận; firmware không cần gửi thời gian.
* Payload giữ nguyên với bản Node.js đã thử nghiệm; khác biệt chỉ ở **đường dẫn** và **header**.

### 3.4 Khách xem thiết bị, số đo mới nhất, lịch sử

`GET /api/customers/me/installation-projects/{projectId}/energy-monitoring/device` → 200:

```json
{ "bindingId": "…", "deviceId": "ESP32-001", "displayName": "…", "sensorModel": "ACS712-30A", "maxCurrentAmps": 30,
  "deviceStatus": "ACTIVE", "connectionStatus": "ONLINE", "boundAt": "…", "lastReadingAt": "…",
  "offlineAfterSeconds": 30, "expectedReportIntervalSeconds": 10 }
```

`GET …/energy-monitoring/readings/latest` → 200 `{ "connectionStatus": "ONLINE", "reading": { "readingId", "deviceId": "ESP32-001", "receivedAt", "currentAmps": 0.2145 } }`.
`reading` là `null` khi chưa có số đo (`NO_DATA`) **và luôn `null` khi thiết bị `DISABLED`**.

`GET …/energy-monitoring/readings?limit=100` (tối đa 500; `from`, `to` ISO-8601; `before` = con trỏ `nextBefore` của trang trước) → 200:

```json
{ "items": [ { "readingId": "…", "deviceId": "ESP32-001", "receivedAt": "…", "currentAmps": 0.2145 } ],
  "limit": 100, "hasMore": true, "nextBefore": "2026-11-15T06:43:20+00:00", "from": null, "to": null }
```

Mới nhất trước; gồm mọi thiết bị từng gắn với công trình này (và chỉ công trình này). Chưa từng có thiết bị → `items: []`.
`…/device` và `…/readings/latest` trả 404 `IOT_BINDING_NOT_FOUND` khi công trình hiện không có thiết bị.

### 3.5 Energy monitoring (API cũ, mở rộng tương thích ngược)

`GET /api/customers/me/installation-projects/{projectId}/energy-monitoring` — mọi trường cũ giữ nguyên tên và ý nghĩa; thêm
`measuredDataSource` và `measured`:

```json
{
  "projectId": "…", "projectStatus": "COMPLETED", "handoverAt": "…",
  "dataSource": "SIMULATED", "measuredDataAvailable": true, "status": "MEASURED_DATA_AVAILABLE",
  "simulated": { "simulationId": "…", "selectedAt": "…", "algorithmVersion": "…", "energyStatus": "…", "panelCount": 12,
                 "installedCapacityKwp": 6.6, "annualEnergyKwh": 9100, "specificYieldKwhPerKwpYear": 1380 },
  "alerts": [], "alertsStatus": "NOT_CONFIGURED",
  "notice": "Expected production (simulated) … 'measured' carries the readings of the bound IoT device …",
  "measuredDataSource": "IOT_DEVICE",
  "measured": {
    "source": "IOT_DEVICE",
    "device": { "bindingId": "…", "deviceId": "ESP32-001", "connectionStatus": "ONLINE", "lastReadingAt": "…", "offlineAfterSeconds": 30, "expectedReportIntervalSeconds": 10, "…": "…" },
    "latest": { "readingId": "…", "deviceId": "ESP32-001", "receivedAt": "…", "currentAmps": 0.2145 },
    "currentAmps":       { "status": "MEASURED",      "value": 0.2145, "unit": "A" },
    "powerWatts":        { "status": "NOT_AVAILABLE", "value": null,   "unit": "W" },
    "energyProducedKwh": { "status": "NOT_AVAILABLE", "value": null,   "unit": "kWh" },
    "energyImportedKwh": { "status": "NOT_AVAILABLE", "value": null,   "unit": "kWh" },
    "energyExportedKwh": { "status": "NOT_AVAILABLE", "value": null,   "unit": "kWh" },
    "notice": "Current (A) measured by the bound ACS712 sensor … Power, energy and grid import/export are not measured …"
  }
}
```

| Trường | Ý nghĩa |
|---|---|
| `dataSource` | Luôn `SIMULATED`: nguồn của khối `simulated` (không đổi để không phá FE cũ). |
| `measuredDataSource` | `IOT_DEVICE` khi công trình có thiết bị đang gắn, `null` khi không. |
| `measured` | `null` khi không có thiết bị. Khi thiết bị `DISABLED`: `latest` = `null`, `currentAmps.status` = `NOT_AVAILABLE`, `device.connectionStatus` = `DISABLED`. |
| `measuredDataAvailable` | `true` chỉ khi thiết bị đang gắn đã có ít nhất một số đo và không bị khoá. |
| `status` | `MEASURED_DATA_AVAILABLE` khi `measuredDataAvailable`; nếu không, như cũ: `NO_MEASURED_DATA` (có mô phỏng) hoặc `SIMULATION_UNAVAILABLE` (`simulated` = `null`). FE kiểm `simulated == null` riêng. |

**Nhãn trên FE:** số của `simulated` luôn là "dự kiến (mô phỏng)"; số của `measured.currentAmps` là "đo được"; đại lượng
`NOT_AVAILABLE` hiện "chưa đo được" (không hiện 0, không tự tính W từ A).

## 4. Trạng thái thiết bị (`connectionStatus`)

Tính từ dấu thời gian lúc đọc, không có worker:

| Trạng thái | Khi nào |
|---|---|
| `DISABLED` | Admin đã khoá (ưu tiên cao nhất). |
| `UNASSIGNED` | Đang hoạt động nhưng chưa gắn công trình nào; số đo bị từ chối 409. |
| `NO_DATA` | Đã gắn, chưa có số đo nào **trong binding hiện tại**. |
| `ONLINE` | Số đo mới nhất cách hiện tại ≤ `IotMonitoring__OfflineAfterSeconds` (mặc định 30 s = 3 chu kỳ). |
| `OFFLINE` | Có số đo nhưng quá hạn trên. |

## 5. Mã lỗi

| HTTP | `error.code` | Khi nào |
|---|---|---|
| 400 | `IOT_VALIDATION_FAILED` | Trường không hợp lệ (`details` / `fieldErrors` theo tên trường), `limit` ngoài 1–500, `from` > `to`, thiếu `reason` |
| 400 | `IOT_READING_VALIDATION_FAILED` | `current` thiếu, không hữu hạn (NaN / Infinity) hoặc vượt dải cảm biến |
| 400 | `AUTH_VALIDATION_FAILED` | Body JSON sai kiểu (vd. `"current": "abc"`) — kiểm tra của model binding |
| 401 | `IOT_DEVICE_UNAUTHORIZED` | Thiếu header `X-Device-Key`, `deviceId` không tồn tại hoặc key sai / đã rotate (không phân biệt để không dò được id) |
| 403 | `IOT_DEVICE_DISABLED` | Thiết bị bị khoá (gửi số đo, hoặc Manager gắn) |
| 404 | `IOT_DEVICE_NOT_FOUND` · `IOT_BINDING_NOT_FOUND` · `INSTALLATION_PROJECT_NOT_FOUND` | Sai id, hoặc khách xem công trình không phải của mình |
| 409 | `IOT_DEVICE_NOT_BOUND` | Thiết bị hợp lệ nhưng chưa được Manager gắn |
| 409 | `IOT_DEVICE_ID_TAKEN` · `IOT_DEVICE_ALREADY_BOUND` · `IOT_PROJECT_ALREADY_BOUND` · `IOT_PROJECT_NOT_BINDABLE` · `IOT_BINDING_NOT_ACTIVE` · `IOT_DEVICE_NOT_DISABLED` · `IOT_STALE_REVISION` | Xung đột trạng thái |
| 429 | `AUTH_TOO_MANY_REQUESTS` (+ `Retry-After`) | Quá giới hạn: thiết bị gửi nhanh hơn 12 lần/phút |
| 503 | `IOT_STORAGE_UNAVAILABLE` | Database lỗi / hết thời gian lúc lưu số đo; thiết bị gửi tiếp ở chu kỳ sau |
| 503 | `FEATURE_PENDING_MIGRATION` · `SCHEMA_NOT_READY` | Feature tắt / migration chưa áp dụng |

## 6. Rate limit

* `iot-telemetry-ingest`: 12 request/phút cho mỗi cặp (`deviceId`, IP nguồn) — 6 lần theo chu kỳ 10 s, phần còn lại là biên cho
  thử lại và lệch giờ. Cấu hình `RateLimiting__IotTelemetryIngest__PermitLimit` / `WindowMinutes`. Firmware gặp 429 thì **chờ 60 s**
  rồi tiếp tục chu kỳ 10 s, không dồn request.
* `iot-read` 60/phút, `iot-write` 20/phút theo người dùng; đọc `energy-monitoring` giữ `installation-read`.

## 7. Firmware ESP32: chuyển từ Node.js thử nghiệm sang ASP.NET Core

| | Node.js thử nghiệm | SmartSolar API |
|---|---|---|
| Đường dẫn | `POST /api/readings` | `POST /api/iot/devices/{deviceId}/readings` |
| Xác thực | không | header `X-Device-Key: <secret>` (deviceId trong path) |
| Body | `{ "current": 0.2145 }` | **giữ nguyên** |
| Thành công | 200 | **201** + envelope JSON |
| Giao thức | HTTP | **HTTPS** trên production (secret đi trong header) |
| Chu kỳ | `SEND_INTERVAL = 2000` + `delay(3000)` | **đúng 10 s** theo mốc `millis()`, không cộng dồn thời gian đọc cảm biến |

Nguyên tắc: **không hard-code secret trong mã nguồn** (nạp từ build flag `-DDEVICE_KEY=…`, file cấu hình ngoài git, hoặc NVS
`Preferences` ghi một lần lúc nạp máy). Mã mẫu chỉ là hướng dẫn, nhóm IoT giữ repository firmware.

```cpp
#include <WiFi.h>
#include <WiFiClientSecure.h>
#include <HTTPClient.h>

// Nạp từ build flag / NVS, KHÔNG ghi giá trị thật vào mã nguồn.
#ifndef DEVICE_ID
#error "Build with -DDEVICE_ID=\"ESP32-001\""
#endif
#ifndef DEVICE_KEY
#error "Build with -DDEVICE_KEY=\"ssdk_...\" (or load it from NVS at boot)"
#endif
static const char* API_BASE = "https://api.smartsolar.example";          // origin của backend, không có dấu / cuối
static const char* ROOT_CA  = "-----BEGIN CERTIFICATE-----\n...";        // CA của API; không dùng setInsecure() ở production
static const uint32_t SEND_INTERVAL_MS = 10000;                         // yêu cầu đã chốt: 1 request / 10 s
static uint32_t nextSendAt = 0;
static uint32_t pausedUntil = 0;                                        // sau 429: tạm dừng 60 s

float readCurrentAmps();                                                 // đọc ACS712 (có thể mất vài trăm ms), trả về ampere có dấu

int sendReading(float amps) {
  WiFiClientSecure client;
  client.setCACert(ROOT_CA);
  HTTPClient http;
  String url = String(API_BASE) + "/api/iot/devices/" + DEVICE_ID + "/readings";
  if (!http.begin(client, url)) return -1;
  http.addHeader("Content-Type", "application/json");
  http.addHeader("X-Device-Key", DEVICE_KEY);
  http.setTimeout(5000);
  int code = http.POST("{\"current\":" + String(amps, 4) + "}");
  http.end();
  return code;
}

void setup() {
  Serial.begin(115200);
  WiFi.begin(WIFI_SSID, WIFI_PASS);                                     // cũng nạp từ cấu hình
  while (WiFi.status() != WL_CONNECTED) delay(250);
  nextSendAt = millis();
}

void loop() {
  uint32_t now = millis();
  if ((int32_t)(now - nextSendAt) < 0) { delay(20); return; }           // chưa tới mốc
  nextSendAt += SEND_INTERVAL_MS;                                        // mốc cố định: chu kỳ không phụ thuộc thời gian đọc/gửi
  if ((int32_t)(now - nextSendAt) >= 0) nextSendAt = now + SEND_INTERVAL_MS; // bị trễ quá một chu kỳ: bỏ qua, không gửi dồn
  if ((int32_t)(now - pausedUntil) < 0) return;                          // đang tạm dừng vì 429

  float amps = readCurrentAmps();
  if (isnan(amps) || isinf(amps)) return;                                // không gửi số không hữu hạn (server cũng từ chối)
  int code = sendReading(amps);
  switch (code) {
    case 201: break;                                                     // đã lưu
    case 401: Serial.println("key sai hoặc đã rotate: cần nạp key mới"); break;
    case 403: Serial.println("thiết bị bị khoá"); break;
    case 409: Serial.println("chưa được gắn công trình"); break;
    case 429: pausedUntil = millis() + 60000; break;                     // chậm lại một phút
    case 503: break;                                                     // server lưu lỗi: chu kỳ sau gửi tiếp
    default:  Serial.printf("HTTP %d\n", code); break;                   // -1: mạng; 400: giá trị không hợp lệ
  }
}
```

Thay đổi so với firmware hiện có: bỏ `delay(3000)` ở cuối `loop()`, đổi `SEND_INTERVAL` 2000 → 10000, chuyển sang mốc
`nextSendAt += SEND_INTERVAL_MS` (không dùng `nextSendAt = now + …` vì sẽ cộng dồn thời gian đọc cảm biến và thời gian HTTP vào chu kỳ).
Thiết bị không gửi `customerId`, `projectId` hay thời gian: server tự gán theo binding và cấp `receivedAt`.

## 8. Cấu hình môi trường và migration

| Biến | Mặc định | Ghi chú |
|---|---|---|
| `Features__InstallationWorkflowEnabled` | — | Phải `true` (IoT thuộc Installation Flow). |
| `IotMonitoring__OfflineAfterSeconds` | 30 | 5–86400. |
| `IotMonitoring__ExpectedReportIntervalSeconds` | 10 | Thông tin cho client; không ép. |
| `IotMonitoring__DefaultMaxCurrentAmps` | 30 | Dải đo mặc định khi đăng ký không ghi. |
| `IotMonitoring__DefaultHistoryLimit` / `MaxHistoryLimit` | 100 / 500 | |
| `RateLimiting__IotTelemetryIngest__PermitLimit` / `WindowMinutes` | 12 / 1 | Theo thiết bị + IP. |
| `RateLimiting__IotRead__PermitLimit`, `RateLimiting__IotWrite__PermitLimit` | 60, 20 | Theo người dùng. |

* Migration **`20261115153613_IotEnergyMonitoring`** (ba bảng mới `iot_device`, `iot_device_binding`, `iot_telemetry_reading`, không sửa
  bảng cũ; Down chỉ xoá ba bảng này). Trạng thái: **ĐÃ TẠO, CHƯA ÁP DỤNG** trên bất kỳ môi trường nào; áp dụng sau khi được duyệt
  (`dotnet ef database update` hoặc script đã sinh từ `dotnet ef migrations script`). Tới lúc đó API trả 503 `SCHEMA_NOT_READY`.
* HTTPS bắt buộc ở production (API đã `UseHttpsRedirection`; reverse proxy phải chuyển tiếp HTTPS). Secret không bao giờ được ghi
  log (handler chỉ log `deviceId`).
* Cơ sở dữ liệu: index `(binding_id, received_at DESC)` và `(device_id, received_at DESC)` cho latest / history. Chưa có chính sách
  xoá dữ liệu cũ (8 640 bản ghi/thiết bị/ngày): cần quyết định thời gian lưu trước khi số thiết bị lớn.

## 9. Checklist tích hợp FE

* [ ] Màn Admin "Thiết bị IoT": danh sách (lọc `status`, `search`, phân trang), đăng ký (hiện secret **một lần** với nút sao chép,
      cảnh báo không xem lại được), khoá / mở khoá (lý do bắt buộc), rotate (hiện secret mới một lần). Không lưu secret vào
      localStorage, log, URL hay cache react-query.
* [ ] Màn Manager/Admin trên trang công trình: khối "Thiết bị đo": gắn theo `deviceId` (gợi ý từ danh sách thiết bị `UNASSIGNED`),
      xử lý 409 `IOT_PROJECT_ALREADY_BOUND` bằng hộp thoại "Thay thiết bị" (`replaceCurrent: true`), 409 `IOT_DEVICE_ALREADY_BOUND`
      bằng câu "đang gắn ở công trình khác, tháo ở đó trước"; nút tháo (lý do bắt buộc); lịch sử binding từ `GET /api/iot/bindings?projectId=`.
* [ ] Màn khách, **chọn công trình trước**: `GET /api/customers/me/installation-projects` → khách chọn một công trình (một công trình
      thì chọn sẵn) → mọi lệnh gọi `energy-monitoring`, `…/device`, `…/readings/latest`, `…/readings` dùng `projectId` đó. Khi đổi
      công trình: huỷ polling cũ, xoá dữ liệu đang hiện, dùng query key có `projectId` (react-query) để không hiện số của công
      trình trước. Công trình không có thiết bị: `measured = null`, `…/device` 404 `IOT_BINDING_NOT_FOUND` → hiện "Chưa gắn thiết bị đo".
* [ ] Màn khách: khối "Điện năng" đọc `energy-monitoring` một lần; nếu `measured != null` hỏi lại `readings/latest` mỗi 10–15 s
      (giới hạn 60/phút) khi tab đang hiện; hiện `connectionStatus` bằng nhãn tiếng Việt (`ONLINE` "Đang kết nối", `OFFLINE`
      "Mất kết nối từ {lastReadingAt}", `NO_DATA` "Chưa có số đo", `DISABLED` "Thiết bị bị khoá", `UNASSIGNED` không xảy ra ở khách).
* [ ] Biểu đồ lịch sử: `readings?from=&to=&limit=500`, phân trang bằng `before = nextBefore`; trục giá trị "Dòng điện (A)".
* [ ] Các ô công suất / điện năng / mua / bán: hiện "Chưa đo được" khi `status = NOT_AVAILABLE`; **không** tính từ dòng điện.
* [ ] Dịch mã lỗi mục 5 trong `services/api/errors.ts`; 429 hiện số giây theo `Retry-After`.
* [ ] Sinh lại kiểu request từ swagger (`AdminIotDevicesController`, `StaffIotMonitoringController`, `CustomerIotMonitoringController`);
      response viết tay theo record C# trong `src/SmartSolar.Modules/IotMonitoring/IotViews.cs`.

## 10. Checklist kiểm thử phần cứng

* [ ] Admin đăng ký thiết bị với đúng `deviceId` nạp vào firmware; Manager gắn vào công trình của tài khoản khách thử nghiệm.
* [ ] Firmware gửi 10 s/lần; sau 1 phút, `GET …/readings?limit=10` có 6 bản ghi (±1), `receivedAt` cách nhau ≈ 10 s.
* [ ] Ngắt Wi-Fi 40 s → `connectionStatus` = `OFFLINE`; nối lại → `ONLINE` sau bản ghi đầu tiên.
* [ ] Key sai → 401 và thiết bị không được lưu gì; rotate trên Admin → firmware cũ nhận 401 cho tới khi nạp key mới.
* [ ] Khoá thiết bị → 403; khách thấy `DISABLED` và `reading: null`; mở khoá → trở lại bình thường.
* [ ] Gửi 15 request trong 1 phút (thử bằng curl) → từ request 13 nhận 429 kèm `Retry-After`; firmware thật không bị ảnh hưởng.
* [ ] Dòng âm (đảo chiều kẹp) được lưu đúng dấu; giá trị > `maxCurrentAmps` → 400 (kiểm tra dải cảm biến đã khai đúng).
* [ ] Tháo thiết bị và gắn cho công trình khác (cùng khách hoặc khách thứ hai) → công trình mới không thấy bản ghi cũ; công trình cũ
      vẫn xem được lịch sử.
* [ ] Một khách có hai công trình, mỗi công trình một thiết bị → mỗi công trình chỉ hiện số của thiết bị mình.
* [ ] `curl -v https://…/api/iot/devices/ESP32-001/readings` qua HTTPS với CA đã nạp vào firmware (không `setInsecure()`).

## 11. Chưa quyết định / ngoài phạm vi V1

* Đo công suất, điện năng, điện mua / bán với lưới: cần cảm biến điện áp hoặc công tơ hai chiều và quyết định của nhóm IoT về
  payload mới (các cột nullable sẽ được thêm bằng migration cộng thêm; API đã có chỗ `NOT_AVAILABLE`).
* Thời gian lưu lịch sử số đo và tổng hợp theo giờ / ngày.
* Cảnh báo sản lượng bất thường: vẫn `alertsStatus = NOT_CONFIGURED` (D-08, chưa có ngưỡng được duyệt).
* Giới hạn bao nhiêu thiết bị một Admin được đăng ký, và quy trình giao thiết bị vật lý cho kỹ thuật viên lắp đặt.
