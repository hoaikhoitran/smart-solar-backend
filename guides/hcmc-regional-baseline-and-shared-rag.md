# Ước tính khu vực TP.HCM 2025 và kho tri thức RAG dùng chung

Ngày: 2026-11-23. Hai quyết định nghiệp vụ đã chốt:

1. Sản lượng điện mặt trời (PVGIS) và khí hậu (NASA POWER) dùng **một** ước tính trung bình có trọng số diện tích cho toàn bộ
   TP.HCM sau sáp nhập 2025 (TP.HCM, Bình Dương, Bà Rịa – Vũng Tàu cũ, gồm Côn Đảo). Thông số riêng của từng mái (diện tích, vật cản,
   tấm pin, số tấm, góc nghiêng, hướng, kiểu lắp, tổn hao, bóng che) vẫn được tính riêng.
2. Tài liệu RAG có **một** phạm vi công bố: khi Admin công bố (đã duyệt, đã nạp), cả 5 vai trò đã đăng nhập hỏi được trên cùng phiên bản.

Nhãn hiển thị duy nhất cho người dùng: **"Ước tính trung bình khu vực TP.HCM (sau sáp nhập)"** — không bao giờ "dự báo chính xác tại
mái nhà". Đây là ước tính do SmartSolar tự tính, **không** phải sản phẩm "trung bình TP.HCM" chính thức của PVGIS hay NASA POWER.

---

## Phần A — Ước tính khu vực TP.HCM 2025

### A1. Nguồn ranh giới (đã kiểm chứng)

| Mục | Giá trị |
|---|---|
| Bộ dữ liệu | OCHA HDX COD-AB *Viet Nam – Subnational Administrative Boundaries* (nguồn: Tổng cục Thống kê / GSO) |
| Trang | https://data.humdata.org/dataset/cod-ab-vnm |
| File tải | `vnm_admin_boundaries.geojson.zip`, sha256 `0a74e8ee18eb84b21f980fa4bd2a3135d2bc42fbd6e31450472368ff48abe41b` |
| File dùng | `vnm_admin1.geojson`, sha256 `d606ad96b97bf1686709738b4ab7b228442f8e5666e05c7c0e9693329ab44fad` |
| Feature | `adm1_pcode = VN79` "TP. Hồ Chí Minh", `valid_on = 2025-09-25`, `version = v02`; 34 tỉnh sau sắp xếp (Quyết định 19/2025/QĐ-TTg) |
| Giấy phép | CC BY-IGO (ghi nguồn) |
| CRS | OGC:CRS84 (WGS84 lon/lat) |
| Hình học | MultiPolygon 40 đa giác (đất liền + đảo), 39 939 đỉnh, không có lỗ |

Kiểm chứng: diện tích tính lại trên phép chiếu đẳng diện là 6 692.76 km²; `area_sqkm` khai báo 6 780.43 km² (lệch 1.29%). Với các
tỉnh nội địa cùng file, phép tính khớp khai báo trong 0.1% → chênh lệch của VN79 là do cách số hoá bờ biển (Cần Giờ, cửa sông, đảo)
của nguồn, không phải lỗi phép chiếu. Diện tích chính thức sau sáp nhập công bố là 6 772.59 km².

### A2. Lấy mẫu và trọng số (tái lập được)

Công cụ: `tools/regional-baseline/hcm_regional_samples.py` (Python thuần, kiểm tra sha256 đầu vào).

1. Chiếu WGS84 → **Lambert Azimuthal Equal-Area ellipsoidal** (EPSG method 9820), tâm 10.75°N 106.75°E. Kiểm chứng: sai số diện
   tích so với công thức ellipsoid chính xác 9×10⁻¹¹; round-trip chính xác tới 10⁻¹⁰ độ.
2. Lưới ô vuông 20 km neo tại gốc mặt phẳng chiếu; cắt từng đa giác theo ô (Sutherland–Hodgman, lỗ trừ đi).
3. Trọng số thô = diện tích phần giao. Ô có phần giao < 10% ô đầy (mảnh vụn ven biên, đảo nhỏ) không lấy mẫu riêng; diện tích của nó
   cộng vào ô giữ lại gần nhất (danh sách trong `manifest.json → merges`) → tổng diện tích được bảo toàn.
4. Điểm mẫu của ô = trọng tâm diện tích phần giao nếu nằm trong ranh giới, nếu không thì điểm trong lưới con 64×64 gần trọng tâm nhất
   nằm trong ranh giới (mọi điểm được kiểm tra point-in-polygon với ranh giới đầy đủ).
5. `w_i = diện tích_i / tổng`, làm tròn 9 chữ số, phần dư dồn vào trọng số lớn nhất → Σw = 1 chính xác.

Kết quả (`src/SmartSolar.Infrastructure/Persistence/ReferenceData/RegionalBaseline/hcm-79-2025-equal-area-20km-v1/`):
26 điểm, w ∈ [0.00719, 0.06077], Côn Đảo 1 điểm (w = 0.01195 ≈ 80 km²), khoảng cách lớn nhất từ một phần ô tới điểm mẫu của nó
20.2 km. Hash: mẫu canonical `a60ae400…2514d6`, ranh giới đơn giản hoá `eb006030…fe6f57` (Douglas–Peucker 100 m, 1 857 đỉnh,
lệch diện tích 0.0106%). Điểm tham chiếu `10.86549, 106.84559` = trọng tâm diện tích, **chỉ để hiển thị**.

Không dùng: trung bình 3 tỉnh lỵ cũ (+1.05% so với giá trị vùng), một điểm trung tâm (Bến Thành −2.5%), trọng số dân số hay chia đều
theo tỉnh cũ.

### A3. Kiểm tra hội tụ với nhà cung cấp thật (PVGIS 5.3, PVGIS-ERA5 2005–2023)

`tools/regional-baseline/convergence_check.py`, kết quả trong `tools/regional-baseline/evidence/`.

| Cấu hình 1 kWp | Lưới 5 km (327 mẫu) | 10 km (89) | 15 km (45) | **20 km (26)** |
|---|---|---|---|---|
| 10°, Nam, 14%, free | 1444.33 kWh/kWp | lệch 0.057% (tháng ≤0.19%) | 0.044% (≤0.14%) | **0.124% (≤0.22%)** |
| 25°, Đông, 14%, building | 1330.09 | — | — | **0.035% (≤0.16%)** |

**Tái lập độc lập 2026-11-24** (tải lại nguồn HDX → sha256 khớp; công cụ sinh lại 3 file dataset **giống từng byte**; đo lại với PVGIS/NASA
thật, cache trống, 705 lời gọi): 5 km 1444.33, 20 km 1446.126 → lệch 0.1243%/năm, 0.2223%/tháng — khớp bảng trên
(`tools/regional-baseline/evidence/reconvergence-2026-11-24-fresh-cache-tilt10-south-free.json`). Lưu ý: lưới tham chiếu 5 km phải sinh với
`--simplify-m 10` (ở 100 m, 5 điểm sát bờ rơi ra ngoài ranh giới đơn giản hoá dùng cho kiểm tra vùng phủ; vị trí/trọng số mẫu không đổi).

Biên độ giữa các điểm: 1 367–1 589 kWh/kWp (~16%). Lưới 20 km được chọn vì sai số lưới < 0.25% (nhỏ hơn nhiều bất định của PVGIS)
với chi phí gọi nhà cung cấp thấp nhất. NASA POWER (lưới 0.5°×0.625°): 26 mẫu rơi vào 4 ô lưới khác nhau của NASA.

### A4. Công thức

- `Y_HCM[m] = Σ_i w̃_i · E_PVGIS[m,i] / 1 kWp` (hệ tham chiếu chung 1 kWp, cùng góc nghiêng, aspect, tổn hao, kiểu lắp, công nghệ
  `crystSi`, horizon). `w̃_i = w_i / Σ_thành công w` (bằng `w_i` khi đủ mẫu).
- `E_site[m] = round(Y_HCM[m] × kWp_lắp, 2)`; **năm = Σ tháng** (tháng và năm luôn khớp). Không bao giờ lấy trung bình sản lượng thô
  của các hệ có công suất khác nhau.
- Kiểu lắp giữ nguyên luật cứ: FLUSH = `building` (tham chiếu thận trọng) + `free` (so sánh); RACK = `free`. Đổi phương vị la bàn →
  aspect PVGIS như cũ.
- Độ lệch chuẩn liên năm của trung bình vùng không phải trung bình có trọng số của độ lệch từng điểm → trả `null`, không bịa.
- Khí hậu: với mỗi đại lượng theo ngày (kWh/m²/ngày, mm/ngày) lấy trung bình có trọng số **tốc độ theo ngày**, rồi nhân số ngày trung
  bình của tháng trong giai đoạn cấu hình (2001–2020: tháng 2 = 28.25 ngày); nhiệt độ lấy trung bình có trọng số °C; năm = Σ tháng;
  nhiệt độ năm = Σ w̃·ANN_i. `requestedLatitude/Longitude = null` (số liệu vùng không gắn với một điểm). Khí hậu chỉ là bối cảnh,
  không cộng/nhân vào sản lượng.
- **Bóng che cục bộ** (phương án (a)): chuỗi giờ PVGIS `seriescalc` vẫn lấy tại toạ độ công trình nhưng **chỉ để tính tỉ lệ tổn thất
  theo tháng** từ hình học mái/vật cản; tỉ lệ này nhân **một lần** vào baseline vùng theo tháng. `shading.correctionBasis =
  LOCAL_SHADING_LOSS_FRACTION_ON_REGIONAL_BASELINE`. Không có toạ độ → bóng che `UNAVAILABLE / LOCATION_MISSING`, sản lượng vùng chưa trừ
  bóng che (có cảnh báo), không thay bằng điểm khác.

### A5. Phạm vi áp dụng

Thứ tự: `property_site.province_code` (mã chính thức 2025) → nếu không có, toạ độ công trình trong ranh giới đơn giản hoá
(sai số ~100 m sát biên) → nếu không có cả hai: `REGION_UNKNOWN`.
- Trong TP.HCM: energy/climate theo vùng (`regional.membershipBasis` = `PROVINCE_CODE` | `SITE_COORDINATES`).
- Ngoài: `UNAVAILABLE / REGION_NOT_SUPPORTED`, không gọi nhà cung cấp, không có số 0.
- Toạ độ và `communeCode` của công trình **không bao giờ** bị thay; bản đồ, vùng phục vụ, điều phối kỹ thuật viên, địa chỉ pháp lý vẫn
  dùng chúng như trước.

### A6. Hiệu năng, cache, chi phí vận hành

- Một cấu hình (góc, aspect, tổn hao, kiểu lắp) = 26 lời gọi PVGIS 1 kWp; FLUSH = 52. Kết quả **COMPLETE** cache dùng chung theo cấu hình
  (`ICacheStore`/Redis, 168 h, khoá gồm phiên bản thuật toán, datasetKey, hash manifest, phiên bản PVGIS, công nghệ, horizon, tổn hao,
  góc, aspect, kiểu lắp, ngưỡng phủ); từng điểm còn được `PvgisV53Client` cache riêng → thử lại chỉ gọi điểm thiếu.
- Song song có giới hạn **toàn tiến trình**: PVGIS 4, NASA 3 (PVGIS reset kết nối khi 6 luồng trong lúc đo). Retry/backoff có sẵn
  (2 lần, 429/5xx/timeout). Phép tính dùng chung chạy trên thread pool, trong scope DI riêng, ngân sách riêng 25 s; người gọi dừng chờ
  không huỷ phép tính, lần sau lấy từ cache.
- Thiếu mẫu: phủ ≥ 95% diện tích → `DEGRADED` (chuẩn hoá lại, cảnh báo `REGIONAL_BASELINE_DEGRADED`, **không cache, không dùng lại**);
  < 95% → `FAILED / REGIONAL_COVERAGE_INSUFFICIENT` (hoặc `PROVIDER_TIMEOUT`). Không bao giờ cache lỗi hay trả 0.
- Khởi động: dataset được nạp và kiểm hash **khi khởi tạo DI**; bật mà dataset thiếu/sửa → host không khởi động (không fallback về một
  điểm). `RegionalClimateWarmupService` tính trước khí hậu vùng (best effort, không chặn readiness).
- Đo NFR (máy dev Apple M5 10 core, 16 GB, mạng thật, 30 lần, mỗi lần góc nghiêng mới → luôn lạnh, 52 lời gọi PVGIS): p50 6.89 s,
  **p95 8.52 s**, max 17.77 s; 30/30 COMPLETE 26/26. **Chưa đo trên VPS 3 vCPU**; thời gian chủ yếu là mạng tới PVGIS.

Cấu hình `SolarSimulation:RegionalBaseline`: `Enabled` (true), `DatasetDirectory`, `MaxConcurrentPvgisRequests` (4),
`MaxConcurrentNasaRequests` (3), `MinCoveredWeightFraction` (0.95), `CacheHours` (168), `WarmClimateOnStartup` (true),
`ComputationBudgetSeconds` (25).

### A7. Hợp đồng API (bổ sung, nullable — tương thích ngược)

`SimulationDetail.estimateBasis`, `energy.estimateBasis`, `energy.regional`, `climate.estimateBasis`, `climate.regional`,
`shading.correctionBasis`, `SimulationListItem.estimateBasis`, `SelectedSimulationView.estimateBasis`;
`climate.context.metadata.requestedLatitude/Longitude` trở thành nullable.
`estimateBasis` = `HCMC_2025_REGIONAL_AREA_WEIGHTED` | `SITE_POINT_PVGIS_LEGACY` (kết quả/snapshot/summary cũ — số liệu lưu **không bị
viết lại**, chỉ nhãn được làm rõ). `regional` gồm: `label`, `regionCode`, `datasetKey`, `sourceDatasetVersion`, `manifestSha256`,
`samplesSha256`, `algorithmVersion`, `weightMethod`, `cellKm`, `sampleCount`, `succeededSampleCount`, `coveredWeightFraction`,
`quality`, `missingLocationIds`, `referenceKwp`, `providerDatabases`, `providerYearMin/Max`, `providerRetrievedFrom/To`,
`computedAt`, `membershipBasis`, `referencePoint{latitude, longitude, displayOnly:true}`, `boundary{…}`, `formula`, `limitations`.
Lý do mới: `REGION_NOT_SUPPORTED`, `REGION_UNKNOWN`, `REGIONAL_BASELINE_DISABLED`, `REGIONAL_COVERAGE_INSUFFICIENT`.
Cảnh báo mới: `REGIONAL_ESTIMATE`, `REGIONAL_BASELINE_DEGRADED`, `REGION_NOT_SUPPORTED`.

Fingerprint thêm `estimateBasis`, `regionalBaseline` (phiên bản thuật toán, dataset, hash manifest/mẫu, số mẫu, kWp tham chiếu,
ngưỡng phủ, cài đặt PVGIS/NASA) và `regionMembership`; `AlgorithmVersions.Current = layout-v2+shading-v1+hcm2025-regional-v1` → kết quả
điểm cũ trong cache không bao giờ được dùng lại. Mọi luật cũ giữ nguyên: chủ sở hữu, chỉ DRAFT, `expectedGeometryVersion`, 409 lệch
phiên bản, 410 hết hạn, 503 cache, chọn mô phỏng thủ công, snapshot bất biến, GET không ghi và không gọi nhà cung cấp.

Solar Advisor (`/api/chat/advisor`, chat) dùng **cùng** provider vùng (building, 1 kWp); vị trí chỉ để xác định có thuộc TP.HCM;
mô phỏng đã chọn chỉ được dùng nếu thuộc thuật toán vùng (mô phỏng điểm cũ bị bỏ qua).

### A8. Cập nhật dataset

Tải lại bộ HDX, chạy công cụ với `--expected-sha256` mới, tạo **thư mục/datasetKey mới**, chạy `convergence_check.py`, cập nhật
`DatasetDirectory`, bump `AlgorithmVersions.RegionalBaseline` nếu phương pháp đổi, sinh lại `hcm2025Boundary.ts` của FE (test FE so sha).

---

## Phần B — Kho tri thức RAG dùng chung

### B1. Quy tắc

- `POST /api/chat/messages`, đọc phiên của chính mình: Customer, Technician, Sales, Manager, Admin (JWT bắt buộc; ẩn danh 401).
- Allow-list **một truy vấn cho mọi vai trò**: `knowledge_document.status = PUBLISHED` ∧ `published_version_id = v.id` ∧
  `v.status = PUBLISHED` ∧ `v.shared_scope_approved_at IS NOT NULL`, sắp theo `v.id`. RAG service giữ nguyên lọc
  `version_id = ANY(...)` + lọc lại; API kiểm tra lại sau khi sinh.
- Quản lý tài liệu vẫn chỉ Admin. `POST …/{id}/publish` **bắt buộc** `confirmSharedVisibility: true` (thiếu → 400
  `RAG_PUBLISH_CONFIRMATION_REQUIRED` kèm câu xác nhận); `note` tuỳ chọn. Mỗi lần duyệt ghi `knowledge_publication_approval`.
- `PUT …/{id}/audience` → **410 `RAG_AUDIENCE_RETIRED`** (vẫn 401/403 với ẩn danh/không phải Admin), không đổi gì.
- `GET /api/admin/knowledge-documents/shared-visibility-review` (Admin, chỉ đọc): tài liệu PUBLISHED chưa được duyệt cho mọi vai trò.
- Câu xác nhận hiển thị cho Admin:
  > Khi xuất bản, nội dung phiên bản này và các trích đoạn mà trợ lý AI trích dẫn sẽ được MỌI người dùng đã đăng nhập (Khách hàng,
  > Kỹ thuật viên, Kinh doanh, Quản lý, Quản trị) đọc được qua chatbot. Không xuất bản tài liệu nội bộ hoặc có dữ liệu nhạy cảm.

### B2. Chẩn đoán "Admin trả lời được, vai trò khác không"

Nguyên nhân cũ: `ChatController.Audience()` xếp Technician/Sales/Manager/Admin vào Staff (đọc mọi tài liệu PUBLISHED), Customer chỉ
đọc `audience = CUSTOMER`; mặc định `INTERNAL` → Customer nhận `INSUFFICIENT_CONTEXT` dù Admin có câu trả lời. Đã bỏ bộ lọc theo vai trò.
Phân biệt lý do (`ChatAnswerView.insufficientReason`, giống nhau cho mọi vai trò): `NO_PUBLISHED_DOCUMENTS`, `NO_INDEXED_CHUNKS`
(tài liệu rỗng / cần nạp lại / lệch mô hình embedding), `BELOW_MIN_SCORE` (câu hỏi không liên quan), `MODEL_INSUFFICIENT`,
`CITATIONS_REVOKED`; lỗi hạ tầng vẫn là mã riêng (`RAG_TIMEOUT` 504, `RAG_BUSY`/`RAG_SERVICE_UNAVAILABLE` 503,
`AI_GENERATION_DISABLED`, quota/ngân sách 429, 401/403). Không hạ `minScore`, không bỏ kiểm tra grounded.

### B3. Lịch sử trò chuyện

- Câu trả lời mới: `retrieval_audience = ALL_SIGNED_IN_ROLES`, chủ phiên luôn đọc được; trích dẫn chỉ hiện khi version còn được truy
  xuất (gỡ công bố → mất trích dẫn).
- Câu trả lời cũ `INTERNAL` hoặc `NULL`: vẫn **ẩn với khách hàng**, không bao giờ đưa lại vào lịch sử gửi model; câu trả lời cũ
  `CUSTOMER` ẩn với khách hàng nếu trích tài liệu không còn truy xuất được. Phiên vẫn riêng từng người.

### B3b. Định danh mỗi lời gọi API → RAG (2026-11-24)

Chạy thử với RAG service thật lộ lỗi có sẵn: hai lời gọi hợp lệ cùng nội dung trong cùng một giây (hai người hỏi cùng câu — nay mọi vai trò dùng chung
allow-list nên rất dễ xảy ra) có cùng chữ ký HMAC → lời gọi thứ hai bị chặn `replayed` → người dùng nhận 503. Sửa: API thêm `requestId` (GUID)
vào **body đã ký** của mọi lời gọi; RAG service chấp nhận trường tuỳ chọn này (`^[A-Za-z0-9-]{1,64}$`). HMAC, cửa sổ thời gian và chống phát lại
giữ nguyên (gửi lại đúng byte vẫn bị chặn). **Thứ tự triển khai bắt buộc: RAG service trước, API sau** — RAG service cũ từ chối trường lạ (422).

### B4. Runbook cutover (cổng an toàn cho nội dung cũ) — CHƯA chạy trên môi trường dùng chung

Migration `20261123140208_SharedKnowledgePublicationScope` (bổ sung, tương thích build cũ đang chạy trên Supabase: cột nullable, bảng mới,
check nới rộng). Dữ liệu: version PUBLISHED của tài liệu `CUSTOMER` (vốn đã cho cả 5 vai trò đọc) được duyệt + ghi audit
`MIGRATED_FROM_CUSTOMER_AUDIENCE`; tài liệu `INTERNAL` **không** được mở — không ai truy xuất được cho tới khi Admin duyệt lại hoặc gỡ.

1. **Trước** (chỉ đọc) — kiểm kê:
   ```sql
   SET SESSION CHARACTERISTICS AS TRANSACTION READ ONLY;
   SELECT d.id, d.title, d.status, d.audience, d.published_version_id, v.version_number, v.chunk_count
   FROM knowledge_document d LEFT JOIN knowledge_document_version v ON v.id = d.published_version_id
   WHERE d.status = 'PUBLISHED' ORDER BY d.audience, d.title;
   ```
   Lưu kết quả làm biên bản. Sao lưu DB.
2. Diễn tập trên bản sao lưu phục hồi (`pgvector/pgvector:pg17`): apply migration, chạy build cũ + mới song song.
3. Được phê duyệt → triển khai RAG service mới (nhận `requestId`), apply migration (thao tác riêng, cần phê duyệt riêng), rồi API mới.
4. Admin mở trang Kho tri thức: khối "tài liệu cũ chờ duyệt" (hoặc `GET …/shared-visibility-review`). Với **từng** tài liệu: đọc lại
   nội dung → "Xác nhận cho mọi vai trò" (publish cùng `publishedVersionId`, `confirmSharedVisibility: true`, ghi chú) hoặc "Gỡ công bố".
   Không xoá gì; gỡ công bố có thể công bố lại sau.
5. Kiểm tra: `pendingCount = 0`; mỗi vai trò hỏi cùng câu → cùng allow-list (log `Chat knowledge outcome … allowedVersions=`).
6. **Rollback**: sao lưu `knowledge_publication_approval` (Down xoá bảng) → `dotnet ef database update 20261118202100_PaymentPlan3070PaperSignatureCollection`
   → triển khai lại build cũ. Down đổi `ALL_SIGNED_IN_ROLES` → `INTERNAL` (ẩn với khách hàng, an toàn). Đã kiểm thử Up/Down/Up trên
   PostgreSQL dùng một lần (`PostgresSharedPublicationCutoverTests`).

FE: tách worktree `origin/main` tại `../smart-solar-hcmc-rag-fe` (xem `FE_HANDOFF_HCMC_REGIONAL_RAG_SHARED.md`).
