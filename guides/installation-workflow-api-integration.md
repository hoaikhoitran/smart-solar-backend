# Installation Flow — API integration (frontend)

Envelope (unchanged): `{ "isSuccess", "traceId", "data", "error": { "code", "message", "details" } }`.
All endpoints require a Bearer JWT except the PayOS webhook. While `Features__InstallationWorkflowEnabled=false`
every Installation Flow endpoint below (chatbot endpoints: see [knowledge-base-api-integration.md](knowledge-base-api-integration.md)) returns **503 `FEATURE_PENDING_MIGRATION`**.

Mutations of versioned aggregates send the last seen `revision`; a stale value → **409** `*_STALE_REVISION`
(reload and retry). Error → HTTP mapping: `*_NOT_FOUND` 404 · validation 400 · ownership
(`FILE_NOT_OWNED`, `INSTALLATION_NOT_LEADER`) 403 · quotas (`RAG_QUOTA_EXCEEDED`,
`RAG_TOO_MANY_CONCURRENT_REQUESTS`, `PAYMENT_TOO_MANY_ATTEMPTS`) and HTTP rate limits 429 ·
`PAYMENT_PROVIDER_ERROR` 502 · provider not configured/unavailable 503 · other state conflicts 409.

## Files

The FE uploads to S3 itself (pre-signed upload is outside this API), then registers the reference.

| Purpose | Key prefix | Types | Max | SHA-256 verified |
|---|---|---|---|---|
| `ONSITE_SURVEY_EVIDENCE` | `onsite-surveys/{userId}/` | jpeg, png, webp, pdf | 15 MB | no |
| `QUOTATION_DOCUMENT` | `quotations/{userId}/` | pdf | 20 MB | yes |
| `CONTRACT_DOCUMENT` | `contracts/official/{userId}/` | pdf | 20 MB | yes |
| `SIGNED_CONTRACT` | `contracts/signed/{userId}/` | pdf | 20 MB | yes |
| `INSTALLATION_EVIDENCE` | `installations/{userId}/` | jpeg, png, webp | 15 MB | no |
| `KNOWLEDGE_DOCUMENT` | `knowledge/{userId}/` | pdf, docx, txt, md | 50 MB | yes |
| `PROJECT_DOCUMENT` | `project-documents/{userId}/` | pdf, jpeg, png | 20 MB | yes |

| Method | Path | Body / notes |
|---|---|---|
| POST | `/api/files` | `{ purpose, bucket?, objectKey, fileName, contentType, sizeBytes, checksumSha256? }` → 201 `{ fileId, status: VERIFIED \| PENDING_VERIFICATION \| REJECTED, … }` |
| POST | `/api/files/{fileId}/verify` | retry verification of a pending file |
| GET | `/api/files/{fileId}` | owner or staff |

Use `fileId` everywhere below; only `VERIFIED` files are accepted.

**Integrity.** The bucket must have versioning enabled (`Storage__S3__RequireObjectVersioning=true`): a file
is pinned to the S3 version that was verified (`objectVersionId` in the file view) and its backend-computed
SHA-256 (`checksumSha256`, for every purpose). Download a file by key **and** `objectVersionId`; uploading
again to the same key creates a new version and never changes a registered file. Before a signed contract
is approved, the signed scan and the official PDF are re-checked against storage; a changed or missing
object is rejected (`409 FILE_CONTENT_CHANGED`), and an unreachable store answers `503 FILE_STORAGE_UNAVAILABLE`.

## On-site survey

Technician (`/api/technicians/me/appointments/{appointmentId}/onsite-survey`):

| Method | Path | Body |
|---|---|---|
| GET | `` | — |
| POST | `/start` | — |
| PUT | `` | `{ revision?, roofType, roofMaterial, roofLengthM, roofWidthM, usableAreaM2, tiltDegree, azimuthDegree, roofCondition, electricalCabinetNotes, electricalConnectionNotes, installationFeasibility, proposedInverterLocation, proposedPanelLayoutNotes, measuredLatitude, measuredLongitude, notes, obstacles: [{ kind, description, heightM, areaM2 }] }` |
| POST | `/attachments` | `{ fileId, category, caption }` |
| DELETE | `/attachments/{attachmentId}` | — |
| POST | `/complete` | `{ revision }` |
| POST | `/amendments` | `{ revision, reason, changes: <same as PUT>, additionalFileIds }` |

Staff: `GET /api/survey-requests/{surveyRequestId}/onsite-survey` (Sales/Manager/Admin).

## Quotations

Sales (owner):

| Method | Path | Body |
|---|---|---|
| POST | `/api/survey-requests/{surveyRequestId}/quotations` | `{ laborCost, transportCost, otherServiceCost, discountAmount, taxRatePercent?, validUntil?, terms?, notes? }` |
| GET | `/api/survey-requests/{surveyRequestId}/quotations` | all versions |
| GET | `/api/quotations/{id}` | |
| PUT | `/api/quotations/{id}` | header + `revision` |
| POST | `/api/quotations/{id}/lines` | `{ revision, productId?, name?, unit?, quantity, unitPrice?, description? }` |
| PUT / DELETE | `/api/quotations/{id}/lines/{lineId}` | `{ revision, quantity, unitPrice?, description? }` / `?revision=` |
| POST | `/api/quotations/{id}/issue` | `{ revision }` |
| POST | `/api/quotations/{id}/revisions` | new DRAFT version |
| PUT | `/api/quotations/{id}/document` | `{ fileId }` (QUOTATION_DOCUMENT) |

Customer: `GET /api/customers/me/quotations[/{id}]`, `POST …/{id}/accept`, `POST …/{id}/revision-request` `{ reason }`.

## Contracts

Sales: `POST /api/quotations/{quotationId}/contracts` `{ terms }` · `GET /api/survey-requests/{id}/contracts` ·
`GET /api/contracts/{id}` · `PUT /api/contracts/{id}` `{ revision, terms }` · `POST …/issue` `{ revision }` →
response has `canonicalHash` · `PUT …/official-document` `{ revision, fileId, canonicalHash }` ·
`POST …/publish` `{ revision }` · `POST …/amendments` `{ terms }`.
Sales owner or Manager: `POST /api/contracts/{id}/review` `{ revision, decision: APPROVE|REJECT, reason }`.

Customer: `GET /api/customers/me/contracts[/{id}]` (includes the official document file id and its
SHA-256) · `POST …/{id}/signed-document` `{ fileId, officialDocumentSha256 }`.

## Payments (PayOS)

| Role | Method | Path |
|---|---|---|
| Customer | POST | `/api/customers/me/contracts/{contractId}/payments` → `{ paymentAttemptId, orderCode, amount, checkoutUrl, qrCode, expiresAt, status }` (idempotent) |
| Customer | GET | `/api/customers/me/contracts/{contractId}/payments` → contract payment status (reconciles with PayOS, throttled) |
| Customer | POST | `/api/customers/me/contracts/{contractId}/payments/cancel` |
| Staff | GET | `/api/contracts/{contractId}/payments` |
| PayOS | POST | `/api/payments/payos/webhook` (anonymous, signature-verified, ≤ 64 KB, not user-rate-limited) |

FE flow: create → redirect to `checkoutUrl` (or show `qrCode`) → on return **poll GET** until the
contract is `PAID`. Never treat the return URL query string as proof of payment.

## Installation

Manager/Admin: `GET /api/installations/ready-contracts` · `POST /api/contracts/{contractId}/installation-project` ·
`GET /api/installation-projects[?status=]` · `GET /api/installation-projects/{id}` ·
`POST …/schedule/preview` and `PUT …/schedule` `{ revision, members: [userId], leaderUserId, shifts: [{ date: "YYYY-MM-DD", start: "HH:mm", end: "HH:mm" }] }` ·
`PUT …/leader` `{ revision, leaderUserId }` · `PUT …/milestones` `{ revision, milestones: [{ milestoneId?, name, description, weightPercent }], reason }` ·
`PUT …/checklist/{itemId}` `{ revision, isRequired, status: PENDING|SATISFIED|NOT_APPLICABLE, evidenceFileId?, note }` ·
`GET …/handover`.

Technician (leader for writes) under `/api/technicians/me/installation-projects`: `GET`, `GET /{id}`,
`POST /{id}/start`, `POST /{id}/milestones/{mid}/start`, `PUT …/progress` `{ revision, progressPercent, notes }`,
`POST …/evidence` `{ fileId, caption }`, `POST …/complete`, `POST …/reopen`,
`POST /{id}/assets` `{ assets: [{ assetType, productId?, manufacturer, model, serialNumber, positionCode, warrantyMonths }] }`,
`POST /{id}/acceptance-request` `{ completionReport }`.

Customer under `/api/customers/me/installation-projects`: `GET`, `GET /{id}`,
`POST /{id}/acceptance` `{ decision: ACCEPT|REQUEST_REWORK, reason }`, `GET /{id}/handover`.

Schedule conflicts return 409 `INSTALLATION_SCHEDULE_CONFLICT` with per technician × shift details.

## Rate limits (per user; per IP when anonymous)

Chatbot limits and quotas are listed in [knowledge-base-api-integration.md](knowledge-base-api-integration.md); scopes (cluster-wide vs per instance) and the Redis failure policy in [rate-limiting.md](rate-limiting.md). Policies marked ★ are cluster-wide when Redis is enabled.

| Policy | Default | Applies to |
|---|---|---|
| files-read / files-write ★ | 60 / 20 per min | files (registration and verification) |
| onsite-survey-read / write | 60 / 20 per min | on-site survey |
| quotation-read / write | 60 / 20 per min | quotations |
| contract-read / write | 60 / 10 per min | contracts |
| payment-create ★ | 5 per 10 min **per user + contract** | create payment link (plus DB cap 5 attempts / 10 min / contract) |
| payment-status | 30 per min | payment status |
| installation-read / write | 90 / 20 per min | installation |
| installation-preview ★ / scheduling ★ | 15 / 10 per min | schedule preview / confirm |

All values are configurable under `RateLimiting:<Section>`. The PayOS webhook has no user limit, only a 600/min per-IP guard (`RateLimiting:PayOsWebhook`).
