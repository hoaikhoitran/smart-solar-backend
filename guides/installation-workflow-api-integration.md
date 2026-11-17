# Installation Flow — API integration (frontend)

Envelope (unchanged): `{ "isSuccess", "traceId", "data", "error": { "code", "message", "details" } }`.
All endpoints require a Bearer JWT except the PayOS webhook. While `Features__InstallationWorkflowEnabled=false`
every Installation Flow endpoint below (chatbot endpoints: see [knowledge-base-api-integration.md](knowledge-base-api-integration.md)) returns **503 `FEATURE_PENDING_MIGRATION`**.

Mutations of versioned aggregates send the last seen `revision`; a stale value → **409** `*_STALE_REVISION`
(reload and retry). Error → HTTP mapping: `*_NOT_FOUND` 404 · validation 400 · ownership
(`FILE_NOT_OWNED`) 403 · missing site location / zone coverage (`SITE_LOCATION_REQUIRED`, `NO_ZONE_COVERAGE`, `ZONE_NOT_SERVICEABLE`) 422 · quotas (`RAG_QUOTA_EXCEEDED`,
`RAG_TOO_MANY_CONCURRENT_REQUESTS`, `PAYMENT_TOO_MANY_ATTEMPTS`) and HTTP rate limits 429 ·
`PAYMENT_PROVIDER_ERROR` 502 · provider or storage not configured/unavailable 503 · a stored object deleted from storage
(`INSTALLATION_EVIDENCE_OBJECT_MISSING`) 410 · other state conflicts 409.

## Files

The FE asks the backend for a presigned PUT URL (`POST /api/files/presigned-upload`), uploads the bytes straight to S3,
then registers the object key. Step-by-step guide with `fetch` examples, S3 CORS and errors:
[file-upload-api-integration.md](file-upload-api-integration.md).

| Purpose | Key prefix | Types | Max | SHA-256 verified |
|---|---|---|---|---|
| `ONSITE_SURVEY_EVIDENCE` | `onsite-surveys/{userId}/` | jpeg, png, webp, pdf | 15 MB | yes |
| `QUOTATION_DOCUMENT` (legacy; its attach endpoint is retired) | `quotations/{userId}/` | pdf | 20 MB | yes |
| `CONTRACT_DOCUMENT` (Manager/Admin since 2026-11-17) | `contracts/official/{userId}/` | pdf | 20 MB | yes |
| `SIGNED_CONTRACT` | `contracts/signed/{userId}/` | pdf | 20 MB | yes |
| `INSTALLATION_EVIDENCE` | `installations/{userId}/` | jpeg, png, webp | 15 MB | yes |
| `KNOWLEDGE_DOCUMENT` | `knowledge/{userId}/` | pdf, docx, txt, md | 50 MB | yes |
| `PROJECT_DOCUMENT` | `project-documents/{userId}/` | pdf, jpeg, png | 20 MB | yes |
| `SERVICE_EVIDENCE` | `after-sales/{userId}/` | jpeg, png, webp | 15 MB | yes |
| `PAYMENT_EVIDENCE` (Manager/Admin: cash receipt) | `payments/cash/{userId}/` | pdf, jpeg, png, webp | 15 MB | yes |

| Method | Path | Body / notes |
|---|---|---|
| POST | `/api/files/presigned-upload` | `{ purpose, fileName, contentType, sizeBytes }` → 200 `{ uploadUrl, method: "PUT", bucket, objectKey, headers, expiresAt }` (role must upload that purpose) |
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

**Viewing installation evidence.** The bucket is private: photos are displayed and downloaded through short-lived
presigned URLs issued by the backend after authorization — see [Evidence photos](#evidence-photos-viewing-and-download).

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

Completing the survey no longer generates a quotation (`automaticQuotationOutcome` is always `null`, deprecated): the technician then builds the cart below.

## Quotations (technician cart — since 2026-11-17)

Full flow, state machines and the old → new mapping: [simplified-commercial-flow.md](simplified-commercial-flow.md).

Technician who completed the survey (anyone else: 404):

| Method | Path | Body |
|---|---|---|
| POST | `/api/technician/survey-requests/{surveyRequestId}/quotation` | `{ notes? }` → 201, the DRAFT cart (returns the existing draft when repeated) |
| GET | `/api/technician/survey-requests/{surveyRequestId}/quotations` | all versions |
| GET | `/api/technician/quotations/{id}` | |
| POST | `/api/technician/quotations/{id}/lines` | `{ revision, productId, quantity }` — the price is the catalog price; the same product again adds to its quantity |
| PUT | `/api/technician/quotations/{id}/lines/{lineId}` | `{ revision, quantity }` |
| DELETE | `/api/technician/quotations/{id}/lines/{lineId}?revision=` | |
| POST | `/api/technician/quotations/{id}/issue` | `{ revision }` — re-prices every line from the catalog, computes totals, sends to the customer |
| POST | `/api/technician/quotations/{id}/revisions` | new DRAFT from an ISSUED / REVISION_REQUESTED / REJECTED version |

There is no price, discount or tax field: unit prices come from the catalog, tax is `Quotation:DefaultTaxRatePercent`, no discount,
validity `Quotation:AutomaticValidityDays` from issue. For TECHNICIAN quotations `equipmentSubtotal` is the sum of **all** cart lines
(service items included) and `laborCost` / `transportCost` / `otherServiceCost` are 0. A product without a catalog price → 400
`QUOTATION_PRICE_MISSING` (at issue: `details.items` names every unpriceable line).

Read only: `GET /api/survey-requests/{surveyRequestId}/quotations`, `GET /api/quotations/{id}` (Manager/Admin: all; Sales: requests it owns).

Customer: `GET /api/customers/me/quotations[/{id}]`, `POST …/{id}/accept` (creates the contract DRAFT in the same transaction),
`POST …/{id}/reject` `{ reason? }`, `POST …/{id}/revision-request` `{ reason }`.

Retired (410 `ENDPOINT_RETIRED`, `details.replacement` names the new route): every Sales quotation write
(`POST /api/survey-requests/{id}/quotations`, `PUT /api/quotations/{id}`, `…/lines`, `…/issue`, `…/revisions`, `…/document`,
`…/confirm-survey-version`, `…/pricing-approval-request`), `POST …/quotations/generate`, `GET /api/quotations/pending-review`,
`GET /api/manager/quotations/pricing-approvals`, `POST /api/manager/quotations/{id}/pricing-approval`.

## Contracts (Manager/Admin)

The DRAFT exists as soon as the customer accepts. Manager/Admin: `GET /api/survey-requests/{id}/contracts` · `GET /api/contracts/{id}` ·
`PUT /api/contracts/{id}` `{ revision, terms, warrantyMonths? }` · `POST …/issue` `{ revision }` → response has `canonicalHash` ·
`PUT …/official-document` `{ revision, fileId, canonicalHash }` (file purpose CONTRACT_DOCUMENT, uploaded by the same Manager/Admin) ·
`POST …/publish` `{ revision }` · `POST …/review` `{ revision, decision: APPROVE|REJECT, reason }` (signature confirmation) ·
`POST …/discard` `{ revision, reason }` · `POST …/amendments` `{ terms, warrantyMonths? }` ·
`POST /api/quotations/{quotationId}/contracts` `{ terms, warrantyMonths? }` only for a quotation accepted before 2026-11-17 that has no
contract (otherwise 409 `CONTRACT_ALREADY_EXISTS`). Sales may only read (`GET`) the contracts of requests it owns.

`warrantyMonths` is one of the Admin's active installation warranty options (`GET /api/sales/after-sales/warranty-options?kind=INSTALLATION`);
omitted = the default option. A draft without months cannot be issued: **400 `CONTRACT_WARRANTY_REQUIRED`**; without terms:
**400 `CONTRACT_TERMS_REQUIRED`**. An unknown duration → 400 `CONTRACT_WARRANTY_VALIDATION_FAILED`.

Customer: `GET /api/customers/me/contracts[/{id}]` (includes the official document file id and its
SHA-256) · `POST …/{id}/signed-document` `{ fileId, officialDocumentSha256 }`.

## Payments (ONLINE via PayOS, or CASH)

| Role | Method | Path |
|---|---|---|
| Customer | POST | `/api/customers/me/contracts/{contractId}/payments` → ONLINE link `{ paymentAttemptId, orderCode, amount, checkoutUrl, qrCode, expiresAt, status, method: "ONLINE" }` (idempotent) |
| Customer | POST | `/api/customers/me/contracts/{contractId}/payments/cash` → CASH choice `{ …, method: "CASH", status: "PENDING", expiresAt: null }` (idempotent; not a payment) |
| Customer | GET | `/api/customers/me/contracts/{contractId}/payments` → `{ contractStatus, paymentState, current, attempts }` (reconciles ONLINE with PayOS, throttled) |
| Customer | POST | `/api/customers/me/contracts/{contractId}/payments/cancel` → cancels the online link (checked with PayOS) or withdraws the cash choice |
| Manager/Admin | GET | `/api/contracts/{contractId}/payments` (history; cash attempts show `confirmedByUserId`, `evidenceFileId`, `confirmationNote`) |
| Manager/Admin | POST | `/api/contracts/{contractId}/payments/cash/confirm` `{ amount, receiptReference, evidenceFileId, note? }` |
| PayOS | POST | `/api/payments/payos/webhook` (anonymous, signature-verified, ≤ 64 KB, not user-rate-limited) |

`paymentState`: `NOT_PAYABLE` (not SIGNED) · `AWAITING_PAYMENT` · `ONLINE_IN_PROGRESS` · `AWAITING_CASH_CONFIRMATION` · `PAID`.
Only one method can be in progress: cash while a link is open → 409 `PAYMENT_CHECKOUT_IN_PROGRESS`; online while cash is chosen → 409
`PAYMENT_CASH_SELECTED`. Cash confirmation: `amount` must equal the contract total (409 `PAYMENT_CASH_AMOUNT_MISMATCH`), the receipt
reference is unique (409 `PAYMENT_CASH_RECEIPT_DUPLICATE`), `evidenceFileId` is a VERIFIED `PAYMENT_EVIDENCE` file uploaded by the same
Manager/Admin; a second confirmation → 409 `PAYMENT_ALREADY_PAID`; no cash chosen → 409 `PAYMENT_CASH_NOT_SELECTED`.

FE flow (online): create → redirect to `checkoutUrl` (or show `qrCode`) → on return **poll GET** until `paymentState` is `PAID`.
Never treat the return URL query string as proof of payment.

## Installation

One responsible technician per project. Times are ISO-8601 instants with offset; responses use `+07:00`.
Statuses: `PENDING_ASSIGNMENT`, `PENDING_TECHNICIAN_ACCEPTANCE`, `SCHEDULED`, `IN_PROGRESS`, `PENDING_CUSTOMER_ACCEPTANCE`,
`REWORK_REQUIRED`, `COMPLETED`, `CANCELLED` (transitions: [business flow §5.1](installation-workflow-business-flow.md#51-state-machine)).

### Role matrix

| Endpoint | Manager/Admin | Responsible technician | Customer owner |
|---|---|---|---|
| `GET /api/installations/ready-contracts` | ✓ | | |
| `POST /api/contracts/{contractId}/installation-project` → 201 | ✓ | | |
| `GET /api/installation-projects[?status=]` (summaries) · `GET /api/installation-projects/{id}` (staff view) | ✓ | | |
| `GET /api/installation-projects/{id}/technician-candidates?startsAt=&endsAt=` | ✓ | | |
| `PUT /api/installation-projects/{id}/assignment` | ✓ | | |
| `POST /api/installation-projects/{id}/cancel` | ✓ | | |
| `POST /api/installation-projects/{id}/return-to-rework` (PENDING_CUSTOMER_ACCEPTANCE → REWORK_REQUIRED) | ✓ | | |
| `PUT /api/installation-projects/{id}/checklist/{itemId}` | ✓ | | |
| `GET /api/installation-projects/{id}/handover` | ✓ | | |
| `GET /api/installation-projects/{id}/evidence` · `GET …/{id}/evidence/{evidenceId}/url` | ✓ (any project) | | |
| `GET /api/technicians/me/installation-projects/{id}/evidence` · `GET …/{id}/evidence/{evidenceId}/url` | | ✓ (current or completed assignment; others 404) | |
| `GET /api/customers/me/installation-projects/{id}/evidence` · `GET …/{id}/evidence/{evidenceId}/url` | | | ✓ (others 404) |
| `GET /api/technicians/me/installation-projects[/{id}]` | | ✓ (current or completed assignment; others 404) | |
| `POST /api/technicians/me/installation-projects/{id}/accept` · `/decline` | | ✓ (PENDING assignment) | |
| `POST …/{id}/start` · `/evidence` · `/assets` · `/completion-report` | | ✓ (ACCEPTED assignment) | |
| `GET /api/customers/me/installation-projects[/{id}]` · `POST …/{id}/acceptance` · `GET …/{id}/handover` | | | ✓ (others 404) |

### Requests

```jsonc
// PUT /api/installation-projects/{id}/assignment  (assign, reassign or reschedule; the technician must accept again)
{ "technicianUserId": "…", "scheduledStartsAt": "2026-10-21T08:00:00+07:00", "scheduledEndsAt": "2026-10-22T17:00:00+07:00",
  "reason": "First installer is sick",   // required when a technician was already assigned or work has started
  "expectedRevision": 3,
  "crossZoneAuthorized": true }          // only for a technician outside the site's zone (candidate crossZone = true): reason then required,
                                         // recorded as a cross_zone_authorization; else 409 CROSS_ZONE_AUTHORIZATION_REQUIRED
// GET …/technician-candidates lists zone members first, then other technicians with "crossZone": true (only the zone rule is skipped
// for them). A site whose commune has no zone can be assigned this way; SITE_LOCATION_REQUIRED / ZONE_NOT_SERVICEABLE still refuse.
// POST /api/installation-projects/{id}/cancel
{ "reason": "Customer moved house", "expectedRevision": 4 }
// POST /api/installation-projects/{id}/return-to-rework  (completion report waiting for the customer is wrong or incomplete)
{ "reason": "Inverter photo missing", "expectedRevision": 10 }   // reason required, ≤ 500; returns the staff view
// PUT /api/installation-projects/{id}/checklist/{itemId}
{ "revision": 4, "isRequired": true, "status": "SATISFIED", "evidenceFileId": "…", "note": "EVN approval letter" }
// POST /api/technicians/me/installation-projects/{id}/accept     body optional: { "expectedRevision": 5 }
// POST /api/technicians/me/installation-projects/{id}/decline
{ "reason": "Truck in repair that week", "expectedRevision": 5 }
// POST …/{id}/evidence   (verified INSTALLATION_EVIDENCE file you uploaded; each file once)
{ "fileId": "…", "caption": "Array row 1" }
// POST …/{id}/assets — a lot confirmed by quantity (no serial; productId required) and a serialised device (quantity omitted or 1)
{ "assets": [ { "assetType": "PANEL", "productId": "…", "manufacturer": "Jinko", "model": "Tiger Neo 550", "serialNumber": null,
                "quantity": 100, "positionCode": "Roof A" },
              { "assetType": "INVERTER", "productId": "…", "manufacturer": "Huawei", "model": "SUN2000-5KTL", "serialNumber": "HW-001",
                "positionCode": null } ] }
// assets[] in every view: { "assetId", "assetType", "productId", "manufacturer", "model", "serialNumber": null | "…", "quantity": 100,
//   "positionCode", "warrantyMonths", "registeredAt" }; handover assets add "warrantyEndsOn" and carry "quantity" too.
// Technician view also carries "requiresPanelAndInverterAssets" (Installation:RequirePanelAndInverterAssets) so the client can show what is missing.
// POST …/{id}/completion-report
{ "completionReport": "All panels installed and commissioned.", "expectedRevision": 9 }
// POST /api/customers/me/installation-projects/{id}/acceptance
{ "decision": "REQUEST_REWORK", "reason": "Cable tray loose on the east side" }   // or { "decision": "ACCEPT" }
```

### Responses (abridged)

```jsonc
// Staff view (Manager/Admin; every Manager write returns it)
{ "projectId": "…", "contractId": "…", "status": "PENDING_TECHNICIAN_ACCEPTANCE", "zone": { "zoneId": "…", "code": "KHU-BINH-TAN", "name": "Khu Bình Tân" },
  "responsibleTechnician": { "technicianUserId": "…", "fullName": "…", "assignmentStatus": "PENDING", "acceptedAt": null },
  "scheduledStartsAt": "2026-10-21T08:00:00+07:00", "scheduledEndsAt": "2026-10-22T17:00:00+07:00", "plannedStartDate": "2026-10-21",
  "plannedEndDate": "2026-10-22", "currentRound": 1, "revision": 4, "site": { "propertyName": "…", "streetLine": "…", "customerName": "…", "customerPhone": "…" },
  "assignments": [ { "assignmentId": "…", "technicianUserId": "…", "status": "REPLACED", "assignmentReason": null, "responseReason": null, "closedAt": "…" } ],
  "evidence": [], "assets": [], "acceptances": [], "checklist": [ … ], "events": [ { "eventType": "TECHNICIAN_ASSIGNED", "fromStatus": "PENDING_ASSIGNMENT",
  "toStatus": "PENDING_TECHNICIAN_ACCEPTANCE", … } ], "cancelledAt": null, "cancellationReason": null }
// Technician view (every technician write returns it) — no checklist, events or assignment history
{ "projectId": "…", "status": "SCHEDULED", "myAssignmentStatus": "ACCEPTED", "assignmentId": "…", "startsAt": "…", "endsAt": "…",
  "zoneName": "Khu Bình Tân", "site": { … }, "currentRound": 1, "revision": 6, "canStart": false, "startBlockedReason": "INSTALLATION_CHECKLIST_INCOMPLETE",
  "evidence": [ { "evidenceId": "…", "fileId": "…", "round": 1, "caption": "…", "addedAt": "…" } ], "assets": [ … ], "acceptances": [ … ] }
// Customer view (every customer write returns it) — technician name only once accepted, no technician ids
{ "projectId": "…", "contractId": "…", "status": "PENDING_CUSTOMER_ACCEPTANCE", "scheduledStartsAt": "…", "scheduledEndsAt": "…",
  "technicianName": "…", "currentRound": 1, "revision": 9, "evidence": [ … ], "assets": [ … ],
  "acceptances": [ { "round": 1, "requestedAt": "…", "completionReport": "…", "decision": "PENDING", "decidedAt": null, "reason": null, "decidedBy": null } ] }
// acceptances[].decidedBy: null while pending, "CUSTOMER", or "STAFF" when a Manager returned the report to rework (decision REWORK_REQUESTED)
// Candidates
[ { "technicianUserId": "…", "fullName": "…", "isEligible": false, "reason": "TECHNICIAN_ON_LEAVE" } ]
// Handover (COMPLETED only)
{ "projectId": "…", "contractId": "…", "handoverAt": "…", "technicianName": "…", "assets": [ { "assetType": "INVERTER", …, "warrantyEndsOn": "2036-10-22" } ],
  "evidence": [ … ], "acceptances": [ … ] }
```

### Error codes

| Code | HTTP | When |
|---|---|---|
| `INSTALLATION_PROJECT_NOT_FOUND` | 404 | unknown project, or a caller who is not staff / the responsible technician / the customer owner |
| `INSTALLATION_CONTRACT_NOT_FOUND` · `INSTALLATION_CHECKLIST_ITEM_NOT_FOUND` | 404 | |
| `INSTALLATION_CONTRACT_NOT_PAID` · `INSTALLATION_PROJECT_EXISTS` | 409 | create before PAID / second project for a contract |
| `INSTALLATION_INVALID_STATE` | 409 | transition not allowed from the current status (incl. a second completion report, rework after completion, cancel after start) |
| `INSTALLATION_STALE_REVISION` | 409 | `expectedRevision` / `revision` out of date, or a concurrent change won (details: `currentRevision`) |
| `INSTALLATION_INVALID_SCHEDULE` | 409 | end ≤ start, start in the past, beyond the horizon, more than `MaxAssignmentDays` days |
| `INSTALLATION_REASON_REQUIRED` | 409 | reassign/reschedule, decline, cancel or return-to-rework without a reason |
| `INSTALLATION_ASSIGNMENT_NOT_PENDING` · `INSTALLATION_ASSIGNMENT_NOT_ACCEPTED` | 409 | decline of an accepted job / execution before accepting |
| `INSTALLATION_ASSIGNMENT_NOT_ACCEPTED` · `INSTALLATION_INVALID_STATE` · `INSTALLATION_CHECKLIST_INCOMPLETE` (details: `blocking`) | 409 | start gates, same order as `canStart` / `startBlockedReason`. `INSTALLATION_TOO_EARLY_TO_START` and `INSTALLATION_SCHEDULE_EXPIRED` are **no longer returned** (2026-11-17: the scheduled dates do not block a start); the codes stay defined for older clients |
| `INSTALLATION_EVIDENCE_REQUIRED` · `INSTALLATION_ASSETS_REQUIRED` (details: `missing`, e.g. `["INVERTER"]`) · `INSTALLATION_DUPLICATE_SERIAL` | 409 | completion report / assets |
| `INSTALLATION_ASSET_QUANTITY_VALIDATION_FAILED` (details per `assets[i].quantity` / `assets[i].productId`) | 400 | a lot without a catalog product or with a quantity outside 1–10 000, or a serialised entry with a quantity other than 1 |
| `INSTALLATION_NOT_HANDED_OVER` | 409 | handover before COMPLETED |
| `TECHNICIAN_OUTSIDE_ZONE` · `TECHNICIAN_SCHEDULE_CONFLICT` · `TECHNICIAN_ON_LEAVE` · `OUTSIDE_WORKING_HOURS` · `TECHNICIAN_SCHEDULING_DISABLED` · `TECHNICIAN_INACTIVE` | 409 | assignment eligibility (details: `technicianUserId`, `technicianName`) |
| `SITE_LOCATION_REQUIRED` · `NO_ZONE_COVERAGE` · `ZONE_NOT_SERVICEABLE` | 422 | the site has no valid commune / its commune is not mapped to an active zone / the zone is not serviceable (see [administrative-locations-and-zones.md](administrative-locations-and-zones.md)) |
| `SITE_HAS_ACTIVE_INSTALLATION` | 409 | the customer changes the site's administrative location while a project of the site is not COMPLETED/CANCELLED |
| `FILE_NOT_FOUND` 404 · `FILE_WRONG_PURPOSE` 400 · `FILE_NOT_OWNED` 403 · `FILE_NOT_VERIFIED` · `FILE_ALREADY_LINKED` 409 | | evidence file checks |
| `VALIDATION_FAILED` | 400 | missing fields, report > 8000 chars, invalid asset, `disposition` other than `inline`/`attachment` |
| `CONTRACT_NOT_FOUND` 404 · `CONTRACT_DOCUMENT_MISSING` 409 · `CONTRACT_SUBMISSION_NOT_FOUND` 404 | | `GET /api/contracts/{id}/official-document/url`, `…/submissions/{submissionId}/url` and the customer twins under `/api/customers/me/contracts/{id}/…` (presigned GET for the official PDF / a signed scan of any version the caller may read, superseded versions included; `disposition=inline\|attachment`; `Cache-Control: no-store`). Downloading never changes the contract: payment still needs SIGNED |
| `INSTALLATION_EVIDENCE_NOT_FOUND` | 404 | evidence id unknown or not on the requested project |
| `INSTALLATION_EVIDENCE_OBJECT_MISSING` | 410 | the photo's verified object (version) was deleted from storage; it cannot be shown any more |
| `FILE_NOT_VERIFIED` · `FILE_NOT_SIGNABLE` | 409 | the evidence file is no longer VERIFIED (e.g. failed a later integrity check) / its stored reference is outside the allowed scope |
| `STORAGE_NOT_CONFIGURED` · `STORAGE_UNAVAILABLE` | 503 | S3 is not configured in this environment / storage or the signing credential could not be reached — retry later |

### Evidence photos (viewing and download)

The evidence bucket is **private** (no public objects, no long-lived links). The project views list evidence ids
only; to show or download a photo the FE asks the backend for a **presigned GET URL** for that one photo. The
backend authorizes the caller for the project first (same rules as the project views), takes the object location
from its own file record (never from the request), checks that the object still exists and signs a URL pinned to
the verified S3 version. The bucket, object key, version id and credentials never appear in any response field.

| Method | Path | Who |
|---|---|---|
| GET | `/api/customers/me/installation-projects/{projectId}/evidence` | the project's customer |
| GET | `/api/customers/me/installation-projects/{projectId}/evidence/{evidenceId}/url?disposition=inline\|attachment` | the project's customer |
| GET | `/api/technicians/me/installation-projects/{projectId}/evidence` · `…/evidence/{evidenceId}/url` | technician with a current or completed assignment |
| GET | `/api/installation-projects/{projectId}/evidence` · `…/evidence/{evidenceId}/url` | Manager, Admin |

`disposition` defaults to `inline`; anything other than `inline` / `attachment` (case-insensitive) → `400 VALIDATION_FAILED`.
Both use the `installation-read` rate limit. URL responses (success and failure) carry `Cache-Control: no-store` and `Pragma: no-cache`.

```jsonc
// GET …/evidence  → 200
[ { "evidenceId": "…", "round": 1, "caption": "Array row 1", "addedAt": "2026-10-21T10:15:00+07:00",
    "contentType": "image/jpeg", "sizeBytes": 2483412, "fileName": "roof-east.jpg",
    "sha256": "9f86d08…", "downloadable": true } ]
// downloadable=false (contentType/sizeBytes/sha256 null): the file is no longer VERIFIED; do not request a URL for it.

// GET …/evidence/{evidenceId}/url?disposition=attachment  → 200 (Cache-Control: no-store)
{ "evidenceId": "…", "url": "https://<bucket>.s3.ap-southeast-1.amazonaws.com/…?versionId=…&response-content-disposition=…&X-Amz-Expires=300&X-Amz-Signature=…",
  "expiresAt": "2026-10-21T03:20:00+00:00", "disposition": "attachment", "contentType": "image/jpeg", "fileName": "roof-east.jpg" }
```

Errors: `INSTALLATION_PROJECT_NOT_FOUND` 404 (unknown project or not yours — no existence disclosure),
`INSTALLATION_EVIDENCE_NOT_FOUND` 404, `INSTALLATION_EVIDENCE_OBJECT_MISSING` 410, `FILE_NOT_VERIFIED` / `FILE_NOT_SIGNABLE` 409,
`STORAGE_NOT_CONFIGURED` / `STORAGE_UNAVAILABLE` 503, `VALIDATION_FAILED` 400, 401 without a token, 403 on another role's route,
`FEATURE_PENDING_MIGRATION` 503 while the feature is off.

**How the FE uses the URL**

* Request a URL **when the photo is about to be displayed** (or the download button is pressed) and use it right away:
  `<img src={url}>` for `inline`, a plain navigation / `<a href={url}>` for `attachment` (the browser saves it as `fileName`;
  the `download` attribute is ignored cross-origin, which is why the server sets the disposition).
* **Do not persist** the URL (local storage, IndexedDB, state that survives a reload, logs, analytics, error reports) and do
  not reuse it after `expiresAt` (default lifetime 300 s, `Storage__S3__PresignedUrlTtlSeconds`). Anyone holding the URL can
  read that one photo until it expires — treat it like a password.
* If S3 answers **403** (expired or signature no longer valid) or the `<img>` fails to load, **request a new URL** once and retry;
  do not loop. A `410` from the backend means the photo is gone for good — show a placeholder.
* Galleries: list with `GET …/evidence`, then request URLs lazily for the thumbnails that are visible (the read limit is
  90 requests/min per user by default).
* Downloads: always use `disposition=attachment`; never build S3 URLs yourself or rewrite the returned one.

### Frontend migration notes (from the team/shift/milestone API)

* **Removed**: `POST …/schedule/preview`, `PUT …/schedule`, `PUT …/leader`, `PUT …/milestones`, technician
  `…/milestones/{mid}/start|progress|evidence|complete|reopen` and `POST …/acceptance-request`; response fields `team`, `shifts`,
  `milestones`, `progressPercent`; statuses `PLANNING`; error codes `INSTALLATION_INVALID_TEAM`, `INSTALLATION_INVALID_SHIFTS`,
  `INSTALLATION_SCHEDULE_CONFLICT`, `INSTALLATION_NOT_LEADER`, `INSTALLATION_MILESTONE_NOT_FOUND`, `INSTALLATION_MILESTONES_INCOMPLETE`,
  `INSTALLATION_MILESTONES_LOCKED`, `INSTALLATION_INVALID_MILESTONES`.
* **Replacements**: preview → `GET …/technician-candidates`; schedule → `PUT …/assignment`; milestone evidence → `POST …/evidence`
  (project level, per round); acceptance request → `POST …/completion-report`. New: technician `accept` / `decline`, Manager `cancel`.
* New statuses `PENDING_ASSIGNMENT` (replaces `PLANNING`), `PENDING_TECHNICIAN_ACCEPTANCE`, `CANCELLED`.
* A missing contract on create now returns `INSTALLATION_CONTRACT_NOT_FOUND` (was `INSTALLATION_PROJECT_NOT_FOUND`); handover before
  completion returns `INSTALLATION_NOT_HANDED_OVER` (was `INSTALLATION_INVALID_STATE`).
* Responses are role-scoped: customer and technician payloads no longer contain `checklist` / `events` (the old write responses
  leaked them). The Manager list returns summaries; fetch `GET /api/installation-projects/{id}` for details.
* Technician conflict lists in admin endpoints report installation work with status `INSTALLATION_ASSIGNMENT` (was `INSTALLATION_SHIFT`);
  the work of a started, unfinished project is reported even after its interval ended.
* Only the customer completes a project. When the customer does not answer a completion report, follow up manually (call the
  customer); there is no automatic acceptance. A wrong report can be sent back with `return-to-rework`.

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
| installation-read / write | 90 / 20 per min | installation (including `return-to-rework`) |
| installation-preview ★ / scheduling ★ | 15 / 10 per min | technician candidates / assignment |

All values are configurable under `RateLimiting:<Section>`. The PayOS webhook has no user limit, only a 600/min per-IP guard (`RateLimiting:PayOsWebhook`).
