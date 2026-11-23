# Product gallery & solar simulation storage — API integration

Both features are part of Flow 1 and are **not** behind a feature flag. Responses use the usual
`ApiResponse` envelope (`{ success, data, error }`); errors carry a stable `error.code`.

## 1. Product image gallery

A product has **0..N images** (`Catalog:MaxImagesPerProduct`, default 10), in a stable order, with exactly
one **primary** image whenever it has at least one. The frontend uploads images to S3 itself; the backend
stores only the URL (absolute `https://`, ≤ 1000 characters, no credentials). When
`Catalog:ImageAllowedHosts` is non-empty, the host must match one entry exactly (case-insensitive).

### Read model (public `GET /api/products`, `GET /api/products/{id}`, admin `GET /api/admin/products/{id}`)

```json
{
  "id": "…", "sku": "PNL-550", "name": "Mono 550", "unitPrice": 2950000, "status": "ACTIVE",
  "images": [
    { "id": "…", "url": "https://cdn.example.com/p/550-front.jpg", "altText": "Front", "sortOrder": 0, "isPrimary": true },
    { "id": "…", "url": "https://cdn.example.com/p/550-back.jpg",  "altText": null,    "sortOrder": 1, "isPrimary": false }
  ],
  "imageUrl": "https://cdn.example.com/p/550-front.jpg"
}
```

`imageUrl` is **deprecated**: it is the primary image's URL, kept for old clients. Use `images`.
`GET /api/admin/products/{id}` (Admin, Manager) also returns INACTIVE products; soft-deleted ones are 404.

### Gallery management (Admin, Manager; rate limit `catalog-write`)

| Method | Route | Body | Result |
|---|---|---|---|
| POST | `/api/admin/products/{id}/images` | `{ "url": "https://…", "altText": "…", "makePrimary": false }` | 201 + product. The first image is always primary; new images go last. |
| DELETE | `/api/admin/products/{id}/images/{imageId}` | – | 200 + product. Removing the primary promotes the image with the lowest `sortOrder`. |
| PUT | `/api/admin/products/{id}/images/order` | `{ "imageIds": ["…", "…"] }` (exactly the current set) | 200 + product, renumbered 0..n-1 |
| PUT | `/api/admin/products/{id}/images/{imageId}/primary` | – | 200 + product (idempotent) |

Image operations never change price, specification, status or `updatedAt` of the product.

* `POST /api/admin/products` accepts `images: [{ url, altText }]` (first = primary). The deprecated
  `imageUrl` still works on create (it becomes the single primary image); if both are sent, `imageUrl` must
  equal `images[0].url`.
* `PUT /api/admin/products/{id}` **no longer touches images** (an `imageUrl`/`images` field is ignored).

| Error code | HTTP | When |
|---|---|---|
| `VALIDATION_FAILED` | 400 | bad URL, host not allowed, alt text > 255, too many images on create |
| `PRODUCT_IMAGE_ORDER_MISMATCH` | 400 | reorder list is not exactly the current images |
| `CATALOG_PRODUCT_NOT_FOUND` / `PRODUCT_IMAGE_NOT_FOUND` | 404 | missing or soft-deleted product / image |
| `PRODUCT_IMAGE_DUPLICATE` | 409 | the product already has that URL |
| `PRODUCT_IMAGE_LIMIT_REACHED` | 409 | add beyond `MaxImagesPerProduct` |
| `PRODUCT_IMAGE_CONFLICT` | 409 | concurrent gallery change — reload and retry |

Migration: every product that had an `https` `image_url` got it as its primary image (Dev: 6 products).
A non-https legacy URL stays only in the deprecated column and must be re-added by an admin.

## 2. Solar simulation — temporary results, explicit selection

Simulation results are **advisory and temporary**. They live in the distributed cache (Redis; the in-process
memory cache when `Redis:Enabled=false`, single instance only) for `SolarSimulation:ResultTtlMinutes`
(default 1440 = 24 h). PostgreSQL keeps only a **compact summary of the simulation the customer selects**
(`selected_simulation`). Optionally (`SolarSimulation:Snapshots:Enabled`, default false) the full selected
result is also written to S3 as JSON with a SHA-256 that is verified on every read.

### Customer journey

1. `POST /api/pre-surveys/{preSurveyId}/simulations` (Customer owner, pre-survey DRAFT) — computes and caches,
   returns the full result with `expiresAt` and `source: "CACHE"`. **It no longer selects anything.**
   The same inputs within the TTL return the cached result (200); changed inputs compute a new one (201).
2. Show / compare results: `GET …/simulations` and `GET …/simulations/{simulationId}`.
3. `POST /api/pre-surveys/{preSurveyId}/simulations/{simulationId}/select` with `{ "expectedGeometryVersion": 3 }`
   — stores the compact summary (and the optional S3 snapshot). Re-selecting replaces it while DRAFT.
4. Optional: `DELETE /api/pre-surveys/{preSurveyId}/simulations/selection` (204) clears it while DRAFT.
5. Submit the pre-survey and book the survey. After submission the selection is frozen.

### Responses

`GET …/simulations` (Customer owner, Sales owner, confirmed technician) — **shape changed from an array to an object**:

```json
{ "items": [ { "id": "…", "isSelected": false, "panelCount": 24, "installedCapacityKwp": 13.2, "annualEnergyKwh": 18480, … } ],
  "selected": { "simulationId": "…", "panelCount": 24, "installedCapacityKwp": 13.2, "annualEnergyKwh": 18480, "snapshotStatus": "NONE", … },
  "cacheAvailable": true }
```

`GET …/simulations/{simulationId}` resolves in this order: the cached full result (`source: "CACHE"`), then — only for the
selected simulation — the verified S3 snapshot (`source: "SNAPSHOT"`) or the compact summary (`source: "SUMMARY"`,
with `snapshotStatus` NONE / STORED / VERIFIED / MISSING / UNAVAILABLE / INTEGRITY_FAILED). Anything else expired → 410.

| Error code | HTTP | Frontend action |
|---|---|---|
| `SIMULATION_EXPIRED` | 410 | the temporary result expired — run the simulation again |
| `SIMULATION_CACHE_UNAVAILABLE` | 503 | cache outage — retry later (the stored selection is still readable) |
| `SIMULATION_SOURCE_CHANGED` | 409 | geometry changed — reload and recalculate |
| `SIMULATION_SELECTION_CONFLICT` | 409 | concurrent selection — reload |
| `PRE_SURVEY_NOT_EDITABLE` | 409 | the pre-survey is submitted; selection is frozen |

Technicians can read simulations only once they are the **confirmed** technician (appointment ACCEPTED,
ASSIGNED or COMPLETED); a capacity holder of a pending appointment cannot (403).

Migration: the 11 selected Dev simulations were copied into `selected_simulation`; unselected historical
results are no longer readable through the API (the old `solar_simulation` table is retained, see
`guides/sql/business-rules-cleanup-draft.sql`).


## Surface step optional (2026-11-17)

The separate surface step (`PUT /api/pre-surveys/{id}/surface`: rectangle + obstacles) is **optional**. Submit never required it. A
simulation without a saved surface now runs on a square of the pre-survey's declared **usable area** (`usableAreaM2`), without obstacles,
with the declared tilt and azimuth; when the customer saved a surface, that surface is used as before. `SURFACE_NOT_DEFINED` (422) is now
returned only when there is neither a saved surface nor a usable area, or when the square would exceed `SolarSimulation:Limits:MaxSurfaceSideM`
(200 m). Without a saved surface, changing `usableAreaM2` makes existing simulations stale (geometry version +1). The `/surface` endpoints
are unchanged (deprecated for new clients, kept for compatibility). The installation checklist item `STRUCTURAL_ASSESSMENT` is unrelated
and still mandatory before an installation starts.

## 2026-11-23 — Regional 2025 HCMC baseline (energy and climate)

Energy and climate are no longer computed at the site's own coordinates. Every site in the 2025 territory of Ho Chi Minh City (province 79,
including the former Binh Duong and Ba Ria – Vung Tau) uses one area-weighted regional PVGIS / NASA POWER baseline for a 1 kWp reference
system with the requested tilt, azimuth, losses and mounting, scaled to the installed kWp; local obstacle shading is applied as monthly loss
fractions. New additive fields: `estimateBasis`, `energy.regional`, `climate.regional`, `shading.correctionBasis` (and `estimateBasis` on
list items and the selected summary); `climate.context.metadata.requestedLatitude/Longitude` are null for regional results. Sites outside
the territory get `REGION_NOT_SUPPORTED`; old results keep their values and report `SITE_POINT_PVGIS_LEGACY`.
Full method, provenance, contract and operations: [hcmc-regional-baseline-and-shared-rag.md](hcmc-regional-baseline-and-shared-rag.md).
