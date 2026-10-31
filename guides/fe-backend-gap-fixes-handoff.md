# FE ↔ Backend gap fixes — integration handoff

Branch `feat/fe-backend-gap-fixes`. Covers FE issues #1 and #3–#11 (#2, presigned uploads, is documented in
[file-upload-api-integration.md](file-upload-api-integration.md) and is unchanged).

Envelope (unchanged): `{ "isSuccess", "traceId", "data", "error": { "code", "message", "details", "fieldErrors" } }`.

## Breaking changes (read first)

| # | Change | What the FE must do |
|---|---|---|
| 9 | C# enums are now **written as UPPER_SNAKE strings** (`"PENDING"`, `"SCHEDULED"`, `"ROOFTOP"`, `"INDIVIDUAL"`) instead of numbers. Requests still accept the old numbers **and** the strings. | Replace numeric comparisons (`status === 4`) with string ones (`status === "SCHEDULED"`). Affects e.g. `survey-requests/my` / `pending` / detail `status`, `installationSurfaceType`, `customerType`. |
| 9 | `installationSurfaceType` in technician job / open-appointment views changes from `"Rooftop"` to `"ROOFTOP"`. | Compare upper case. |
| 1 | `energy.annualEnergyKwh` and `energy.monthly[].energyKwh` are **after shading** when `shading.status = "SUCCEEDED"` (unchanged otherwise). The unshaded value is `energy.annualEnergyBeforeShadingKwh`. `algorithmVersion` is now `layout-v2+shading-v1`. | Show "before / after shading" from the new fields. Results created before this version have no `shading`. |

Everything else is additive (new fields or endpoints).

## #3 Overdue appointments

**Overdue** = the scheduled end (`endsAt`) has passed but the survey was not completed (the appointment is still
`PENDING_ACCEPTANCE`, `ESCALATED`, `ACCEPTED` or `ASSIGNED`; completing the on-site survey makes it `COMPLETED`).

* `GET /api/technicians/me/appointments?includePast=false` (default) now returns upcoming **and overdue** confirmed jobs;
  `includePast=true` also returns completed past jobs. New fields per item: `surveyRequestId`, `ward`, `communeName`,
  `isOverdue`. `GET …/appointments/{id}` has `isOverdue`.
* Recovery = reschedule to a future time (optionally with another technician). A **reason is required** for an overdue
  appointment (`400 APPOINTMENT_RECOVERY_REASON_REQUIRED`); it is stored in the `RESCHEDULED` event as `OVERDUE_RECOVERY: <reason>`.
  The old appointment is cancelled (`RESCHEDULED_BY_SALES`) and linked to the replacement (`replacedByAppointmentId`); history is kept.
  * Sales owner: `PUT /api/survey-requests/{surveyRequestId}/reschedule` `{ startsAt, reason, technicianUserId? }` (existing endpoint).
  * Manager/Admin (no ownership needed): **new** `PUT /api/admin/survey-requests/{surveyRequestId}/reschedule`, same body.
* Still refused: an appointment that is running (started, not ended) → `409 INVALID_BOOKING_STATE`; completed →
  `409 INVALID_BOOKING_STATE`; on-site survey started → `409 ONSITE_SURVEY_IN_PROGRESS`; another Sales user → `403 SURVEY_REQUEST_NOT_OWNED`.
  The new time and technician are re-checked (availability, conflicts, zone) with the existing locks; on a conflict the
  original appointment is unchanged.

## #5 Survey request lists

`GET /api/survey-requests/my` and `GET /api/survey-requests/pending` items gain:

```json
{ "ward": "Ben Nghe", "communeName": "Phường Sài Gòn",
  "appointment": { "appointmentId": "…", "status": "ACCEPTED", "startsAt": "…", "endsAt": "…", "acceptanceDeadlineAt": null,
                   "technicianUserId": "…", "technicianName": "…", "isOverdue": false } }
```

`appointment` is `null` when the request has no active (non-cancelled) appointment. In `/pending` (unclaimed queue)
`technicianUserId` / `technicianName` are always `null`. `communeName` is the official name of the site's commune code
(null without a code). Times are ISO 8601 with offset (UTC).

## #7 Customer sites and drafts

* `GET /api/customers/me/sites?page=1&pageSize=20` → `PagedResult` of `{ siteId, name, province, district, ward, streetLine,
  provinceCode, communeCode, communeName, latitude, longitude, installationSurfaceType, surfaceMaterial, preSurveyCount }` (by name).
* `GET /api/customers/me/pre-surveys?status=DRAFT&sortBy=updatedAt&sortDirection=desc&page=1&pageSize=20` → `PagedResult` of
  `{ preSurveyId, propertySiteId, siteName, status, revision, geometryVersion, totalAreaM2, usableAreaM2, selectedSimulationId,
  surveyRequestId, createdAt, updatedAt }`. `status` = `DRAFT` | `SUBMITTED` (omit for all); `sortBy` = `updatedAt` | `createdAt`.
* CUSTOMER role only, own data only (another user gets an empty page). `pageSize` 1–100; invalid parameters → `400` with `fieldErrors`.

## #1 Time-dependent shading (authoritative backend estimate)

**Method `RAY_CAST_3D_PVGIS_HOURLY_LINEAR`** — a *linear irradiance-based shading estimate*, not an electrical model.

1. **Sun position** for every 15-minute sub-sample: NOAA solar-position equations (Meeus), checked against the NREL SPA
   reference case (within 0.05°). Inputs: latitude, longitude, UTC instant. Elevation above the horizon; azimuth clockwise
   from geographic north (same convention as roof/panel azimuth; PVGIS `aspect = azimuth − 180`).
2. **3D scene** in the existing world frame (X = East, Y = North, Z = Up, metres, origin = surface origin — the same
   `worldCenter` / `worldRotation` the FE already renders): the surface rectangle, every obstacle as a box on the surface
   (footprint `xM, yM, widthM, lengthM`, `heightM` along the surface normal) and, for RACK, every panel's front face.
3. **Ray casting**: each panel face is sampled on a 4 × 4 grid; a sample is shaded when the ray toward the sun hits the
   surface, an obstacle or another panel. `shadedFraction = blockedSamples / samples` (0..1; overlapping shadows count once).
   Sun directions are grouped in 1° bins (elevation × azimuth) and each bin is ray-cast once.
4. **PVGIS hourly** `seriescalc` with `pvcalculation=1&components=1` for the **same system** as the primary energy scenario
   (location, kWp, `loss`, `angle`, `aspect`, `mountingplace`, `pvtechchoice`, `usehorizon`), years `StartYear`–`EndYear`.
   Fields used: `P` (W), `Gb(i)`, `Gd(i)`, `Gr(i)` (W/m²), `H_sun` (°, consistency check only).
5. **Energy** (per hour, four sub-samples at +7.5/+22.5/+37.5/+52.5 min, PVGIS hourly values used unchanged within the hour):

   ```
   S_system(t)       = Σ(P_rated,i · f_i(t)) / Σ P_rated,i
   irradianceLoss(t) = clamp(S_system(t) · Gb / (Gb + Gd + Gr), 0, 1)      (0 when Gb + Gd + Gr = 0, Gb = 0 or sun below horizon)
   E_base,hour       = P · 1 h / 1000                                     [kWh]
   E_loss,hour       = Σ_sub P · irradianceLoss · 0.25 h / 1000           [kWh]
   ```

   Sums per (year, UTC month), then averaged over the years. The monthly loss fraction is applied to the existing PVGIS
   typical-year monthly baseline (PVcalc, same configuration), so PVcalc stays the reference baseline.
   `annualDirectShadedFraction` per panel = Σ(f · Gb · Δt) / Σ(Gb · Δt) (irradiance weighted). A panel is counted in
   `shadedPanelCount` when that fraction ≥ `shadedPanelThresholdPercent` (1 %).

New response block (`GET/POST …/simulations`, also in snapshots):

```json
"shading": {
  "status": "SUCCEEDED", "reason": null, "failure": null,
  "method": "RAY_CAST_3D_PVGIS_HOURLY_LINEAR", "sunPositionAlgorithm": "…", "timeStepMinutes": 15,
  "startYear": 2021, "endYear": 2023, "yearCount": 3,
  "annualLossPercent": 0.0, "annualEnergyLossKwh": 0.0,
  "annualEnergyBeforeShadingKwh": 0.0, "annualEnergyAfterShadingKwh": 0.0,
  "shadedPanelCount": 0, "shadedPanelThresholdPercent": 1,
  "panels": [ { "index": 0, "annualDirectShadedFraction": 0.0 } ],
  "monthly": [ { "month": 1, "energyBeforeShadingKwh": 0.0, "energyAfterShadingKwh": 0.0, "energyLossKwh": 0.0, "lossPercent": 0.0 } ],
  "hourlyReferenceAnnualEnergyKwh": 0.0, "sunHeightCheckDeviationDegree": 0.0,
  "samplesPerPanel": 16, "directionBinDegree": 1, "limitations": [ "…" ]
},
"energy": { "annualEnergyBeforeShadingKwh": 0.0, "annualEnergyAfterShadingKwh": 0.0, "annualEnergyKwh": 0.0, "monthly": [] }
```

(Values are placeholders.) `panels[].index` = `layout.details.placements[].index`.

| `shading.status` / `reason` | Meaning; energy |
|---|---|
| `SUCCEEDED`, `reason: null` | Ray cast done; energy is after shading. |
| `SUCCEEDED`, `NO_OCCLUDERS` | Nothing can shade the panels (no obstacles; flush or a single rack panel): zero loss, no hourly call. |
| `UNAVAILABLE`, `OBSTACLE_HEIGHT_MISSING` | An obstacle has no `heightM`: no loss values (never a fake zero); energy unshaded, warning `SHADING_NOT_APPLIED`. |
| `UNAVAILABLE`, `LOCATION_MISSING` / `ENERGY_UNAVAILABLE` / `COMPUTATION_LIMIT` | No coordinates / no PVGIS baseline / scene too large for the request budget. Energy unshaded. |
| `FAILED`, `HOURLY_DATA_FAILED` | PVGIS seriescalc failed (`failure.code`, e.g. `PROVIDER_REJECTED_REQUEST` for years outside the database). Not reused; retry recalculates. |
| `NOT_APPLICABLE`, `NO_PANELS` | No panel fits. |

**Visualisation**: the FE may animate shadows with Three.js shadow maps using the same world frame (Z-up; rotate the scene
root −90° about X for a Y-up camera) and the sun direction `(cos e · sin a, cos e · cos a, sin e)`; this is visual only — the
numbers come from the backend. A heat map can colour each panel by `annualDirectShadedFraction`.

**Measured choices** (test `ShadingResolutionAndMonthTests`, one synthetic year near HCMC, three 1 m panels between a
2.5 m wall and a 1 m parapet; numbers vary with the scene):

| Sample grid per panel | Annual loss | Time |
|---|---|---|
| 4 × 4 (default) | 28.403 % | 173 ms |
| 8 × 8 | 28.437 % | 607 ms |
| 16 × 16 | 28.460 % | 2,326 ms |

The default 4 × 4 is 0.057 percentage points from 16 × 16 at about 1/13 of the cost, so it stays the default.
**Month grouping**: hours are grouped by **UTC** month, the same convention as the PVGIS baseline the loss fraction is
applied to. Compared with Vietnam local months (UTC+7), only the first 7 local hours of each month's 1st day move. In the
measurement that is 0.08 % of annual energy, and no month differs by more than 0.031 %. This is not a defect and was left
unchanged. The FE shows `month` 1–12 as-is.

**Limitations** (also in `shading.limitations`): direct beam only; no electrical mismatch, bypass diodes, strings,
inverter clipping or temperature effect of partial shading; diffuse/reflected not reduced; objects outside the drawn surface
are not modelled (PVGIS handles terrain horizon); PVGIS hourly values are constant within each hour. PVGIS `loss` (14 %
default) does not include nearby-object shading, so the estimate is not double-counted.

## #4 Commune reference coordinates (PARTIAL — data BLOCKED)

`GET /api/administrative-units/communes` items gain `referencePoint: { latitude, longitude, source, kind: "COMMUNE_REFERENCE_POINT" } | null`.
It is an **approximate administrative point**, never a rooftop location; it is not used for energy estimates. All values are
`null` today: the official dataset (danhmuchanhchinh.nso.gov.vn) has no coordinates and no verified source was available.
To add them, package `commune-reference-points.csv` (`commune_code,latitude,longitude,source`, matched by official code only,
inside Viet Nam, ≤ 6 decimals, one row per code, source required) with its sha256 in the dataset manifest and run the
existing catalog import. Migration `20261021164225_AddCommuneReferencePoints` (3 nullable columns + check) is **generated, not applied**.

## #6 Layout after 24 hours

The code already persists the selected simulation as an S3 snapshot (surfaces, obstacles, placements, shading, energy) and
serves it with `source: "SNAPSHOT"` after the cache expires; it is **disabled by default**. In every FE environment set:

```
SolarSimulation__Snapshots__Enabled=true
SolarSimulation__Snapshots__Bucket=<bucket>
SolarSimulation__Snapshots__Region=ap-southeast-1
# MinIO / S3-compatible only:
SolarSimulation__Snapshots__ServiceUrl=
SolarSimulation__Snapshots__ForcePathStyle=false
```

Credential: IAM role or `Storage__S3__AccessKeyId/SecretAccessKey`, with `s3:PutObject` and `s3:GetObject` on `simulations/*`.
Without it, after `ResultTtlMinutes` the FE only gets `source: "SUMMARY"` (no layout).

## #8 OpenAPI

Priority endpoints (scheduling, survey requests, customer sites/pre-surveys, simulations, administrative units, files,
installation/after-sales evidence URLs, admin products) declare their success schema `ApiResponse<…>`. Every operation
documents its error statuses (400/401/403/404/409/429/503 as applicable) with the envelope schema, including `fieldErrors`.
Enums appear as string enums. Schema ids are full type names (no collisions).

## #10 Validation errors

`error.details` is unchanged (field → messages, same keys as before). New `error.fieldErrors`:

```json
[ { "field": "unitPrice", "code": "TOO_SMALL", "message": "'Unit Price' must be greater than or equal to '0'." } ]
```

`field` is the camelCase JSON path (`images[0].url`); `code` ∈ `REQUIRED, TOO_SHORT, TOO_LONG, INVALID_LENGTH, TOO_SMALL,
TOO_LARGE, OUT_OF_RANGE, INVALID_EMAIL, INVALID_FORMAT, INVALID_PRECISION, INVALID_VALUE`. Unreadable bodies / wrong types
→ `INVALID_FORMAT`. `fieldErrors` is `null` for non-validation errors. Statuses: 400 validation, 401 no/invalid token,
403 role or ownership, 404 not found / not visible, 405 wrong method (envelope), 409 state conflicts, 422 location data.

## #11 Admin product list

`GET /api/admin/products?status=ACTIVE|INACTIVE|ALL` (default `ALL`) with the public filters and paging
(`search, productType, category, brand, minPower, maxPower, minPrice, maxPrice, page, pageSize, sortBy, sortDirection`).
ADMIN/MANAGER only; soft-deleted products are never listed. Reactivate with the existing `PATCH /api/admin/products/{id}/status`.
`GET /api/products` stays ACTIVE only.

## New error codes

`APPOINTMENT_RECOVERY_REASON_REQUIRED` (400), `FILE_UPLOAD_NOT_ALLOWED` (403, #2), `ONSITE_SURVEY_ATTACHMENT_NOT_FOUND` /
`SERVICE_EVIDENCE_NOT_FOUND` (404, #2), warnings `SHADING_NOT_APPLIED`, `SHADING_BASELINE_DIFFERS`, `SUN_POSITION_CHECK_DEVIATION`.

## Database and configuration

* Migration `20261021164225_AddCommuneReferencePoints` — additive, nullable, **not applied**; apply only through the
  approved process.
* Migration `20261030122441_AutomatedInstallationQuotation` (automatic quotation: `quotation.origin`, `quotation.pricing_basis`,
  `onsite_survey_report.bill_of_materials`) — additive, **not applied**. See
  [automated-quotation-business-handoff.md](automated-quotation-business-handoff.md).
* New optional settings: `SolarSimulation__ShadingAnalysis__Enabled` (true), `__StartYear` (2021), `__EndYear` (2023)
  (must lie in the site's PVGIS radiation database), plus advanced `SamplesAlongWidth/Length`, `DirectionBinDegree`,
  `MaxWorkUnits`, `ShadedPanelThresholdPercent`. See `.env.example`.
