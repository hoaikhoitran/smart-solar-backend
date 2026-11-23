# FE handoff — 2025 HCMC regional baseline and shared RAG publication (API contract only)

Date: 2026-11-24. Owner: backend. This document describes the **backend API behaviour** the frontend must integrate. It prescribes no
frontend source change. All additions are backward compatible (new nullable fields) except the two endpoint changes marked **BREAKING**.

## 1. Simulations (`/api/pre-surveys/{id}/simulations…`)

Unchanged: routes, roles, ownership, DRAFT-only writes, `expectedGeometryVersion`, 409 stale, 410 expired, 503 cache, explicit selection.

New fields (null on results stored before the change):

| Field | Type | Meaning |
| --- | --- | --- |
| `estimateBasis` (detail, list item, selected summary) | string | `HCMC_2025_REGIONAL_AREA_WEIGHTED` or `SITE_POINT_PVGIS_LEGACY` (old stored results; values unchanged) |
| `energy.estimateBasis`, `climate.estimateBasis` | string \| null | same values |
| `energy.regional`, `climate.regional` | object \| null | regional model metadata (below); null when no regional estimate was computed |
| `shading.correctionBasis` | string \| null | `LOCAL_SHADING_LOSS_FRACTION_ON_REGIONAL_BASELINE` when local shading was applied |
| `climate.context.metadata.requestedLatitude/Longitude` | number \| null | **now nullable**: null for regional climate |

`regional` object: `estimateBasis`, `label` (display exactly: "Ước tính trung bình khu vực TP.HCM (sau sáp nhập)"), `regionCode` ("79"),
`regionName`, `algorithmVersion`, `datasetKey`, `sourceDatasetVersion`, `manifestSha256`, `samplesSha256`, `weightMethod`, `cellKm`,
`sampleCount`, `succeededSampleCount`, `coveredWeightFraction` (0–1), `quality` (`COMPLETE` | `DEGRADED`), `missingLocationIds[]`,
`referenceKwp` (1 for energy, null for climate), `providerDatabases[]`, `providerYearMin/Max`, `providerRetrievedFrom/To`, `computedAt`,
`membershipBasis` (`PROVINCE_CODE` | `SITE_COORDINATES`), `referencePoint {latitude, longitude, displayOnly: true, method}` (a display-only
area centroid — **never** the site location), `boundary {publisher, datasetPage, downloadUrl, fileSha256, featurePcode, validOn, license, crs}`,
`formula`, `limitations[]`.

New `energy.reason` / `climate.reason` values: `REGION_NOT_SUPPORTED` (site outside the 2025 territory), `REGION_UNKNOWN` (no province code and no
coordinates), `REGIONAL_BASELINE_DISABLED`, `REGIONAL_COVERAGE_INSUFFICIENT` (too few samples answered; energy null — never 0), plus the existing
`PROVIDER_TIMEOUT` etc. New warning codes: `REGIONAL_ESTIMATE`, `REGIONAL_BASELINE_DEGRADED`, `REGION_NOT_SUPPORTED`.
`LOCATION_MISSING` (new results): only local shading is missing; the regional energy is present and unshaded.

UX rules the contract implies: one city-wide figure (never split by former province); never present it as a forecast for the roof; the site
coordinates stay the site's; show `null` energy as "no data".

Coverage check for map pickers: the verified simplified boundary is shipped by the backend at
`src/SmartSolar.Infrastructure/Persistence/ReferenceData/RegionalBaseline/hcm-79-2025-equal-area-20km-v1/boundary-simplified.geojson`
(sha256 `eb006030090c309523bc75653df3450d04b1d9f69ce37d512b3a655131fe6f57`, GeoJSON MultiPolygon, lon/lat, ~100 m tolerance). The backend decides
by the official province code first.

## 2. Knowledge administration (Admin only, `/api/admin/knowledge-documents`)

- `POST /{documentId}/publish` — **BREAKING**: body `{ versionId, revision, confirmSharedVisibility: true, note?: string(≤1000) }`.
  Without `confirmSharedVisibility: true` → **400 `RAG_PUBLISH_CONFIRMATION_REQUIRED`**, `error.details.confirmation` = the text to show:
  "Khi xuất bản, nội dung phiên bản này và các trích đoạn mà trợ lý AI trích dẫn sẽ được MỌI người dùng đã đăng nhập (Khách hàng, Kỹ thuật viên,
  Kinh doanh, Quản lý, Quản trị) đọc được qua chatbot. Không xuất bản tài liệu nội bộ hoặc có dữ liệu nhạy cảm."
  Sending the **currently published** version of a `PENDING_REVIEW` document re-confirms it (status unchanged, audit row added).
- `PUT /{documentId}/audience` — **BREAKING**: **410 `RAG_AUDIENCE_RETIRED`** (401/403 unchanged for anonymous / non-Admin); nothing changes.
- `GET /shared-visibility-review` (new, read only): `{ pendingCount, pendingInternalCount, pendingChunkCount, items[{documentId, title, category,
  status, legacyAudience, publishedVersionId, versionNumber, chunkCount, publishedAt, revision}], guidance }`.
- Document view additions: `sharedVisibility {scope: "ALL_SIGNED_IN_ROLES", status: APPROVED | PENDING_REVIEW | NOT_PUBLISHED, approvedAt,
  approvedByUserId}`, `publicationApprovals[]` (detail only: `versionId, action, legacyAudience, actorUserId, note, createdAt`),
  `versions[].sharedScopeApprovedAt`. `audience` and `legacyAudienceTag` remain in the payload as **legacy, informational** fields: they no longer
  decide access and must not be shown as a permission.

## 3. Chat (`/api/chat/*`, every signed-in role; anonymous 401)

- Every role retrieves the same published + approved versions; identical questions give identical evidence and citations (subject to model
  variability). Sessions remain private to their owner.
- `POST /messages` response adds `insufficientReason` (when `grounded = false`): `NO_PUBLISHED_DOCUMENTS`, `NO_INDEXED_CHUNKS`, `BELOW_MIN_SCORE`,
  `MODEL_INSUFFICIENT`, `CITATIONS_REVOKED`, `UNSPECIFIED`. Content-free; safe to display as a hint.
- History: new answers stay readable by their owner; a citation disappears when its version is unpublished/superseded. Legacy INTERNAL / pre-audience
  answers remain withheld from customers (`withheld: true`, placeholder text).

## 4. Rollout order that affects the FE

RAG service → API (migration first, separately approved) → FE. Until the FE sends `confirmSharedVisibility`, Admins cannot publish (400);
the audience toggle returns 410. After the cutover, Admins must review the documents listed by `shared-visibility-review`.

Note: an FE prototype of these changes was prepared earlier in a separate worktree (`/Users/khoiyami/smart-solar-hcmc-rag-fe`, not committed).
It is outside this backend release and is not covered by the backend test evidence.
