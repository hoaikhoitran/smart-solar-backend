# SmartSolar Installation Flow — business flow

The Installation Flow starts where Flow 1 ends (a technician has **accepted** — or staff **assigned** — the survey appointment)
and finishes with a handed-over installation:

```
on-site survey → quotation → contract (hand-signed PDF) → PayOS payment (100%) → installation → handover
```

> **Status: code complete, database PENDING_MANUAL_MIGRATION.** Every Installation Flow / knowledge base endpoint answers
> `503 FEATURE_PENDING_MIGRATION` until `Features__InstallationWorkflowEnabled=true` (knowledge base: `Features__KnowledgeBaseEnabled=true`), which must only be set after
> `guides/sql/installation-workflow-and-knowledge-base-schema-review.sql` has been reviewed and applied. See
> [installation-workflow-and-knowledge-base-database-plan.md](installation-workflow-and-knowledge-base-database-plan.md).

Roles: **Customer**, **Sales** (owner of the survey request since the Flow 1 claim), **Technician**,
**Manager**, **Admin**. Every state change is optimistic-concurrency protected (`revision`) and writes
an audit event.

---

## 1. On-site survey (Technician)

| Rule | Detail |
|---|---|
| Who | Only the confirmed technician of the survey request's appointment (status **ACCEPTED** or **ASSIGNED**; **COMPLETED** for amendments). |
| Start | `POST …/onsite-survey/start` from 120 min (`OnSiteSurvey:EarlyStartMinutes`) before the slot. |
| Draft | `PUT …/onsite-survey` saves partial data at any time (DRAFT → IN_PROGRESS). |
| Evidence | Photos/PDF uploaded by the FE to S3 (`ONSITE_SURVEY_EVIDENCE`), registered with `POST /api/files`, then attached with a category (ROOF, ELECTRICAL_CABINET, …). |
| Complete | Requires the full measurement set **and at least one ROOF photo**. Atomically: report → COMPLETED, appointment → **COMPLETED** (`completed_at`) and survey request **Scheduled → Completed**. Idempotent. |
| After completion | Data is immutable; corrections are **amendments** (reason + versioned diff, original kept). |

Statuses: `DRAFT → IN_PROGRESS → COMPLETED`.

## 2. Quotation (Sales, self-issued)

* Created only for a survey request whose on-site survey is **COMPLETED**, by the owning Sales.
* Lines reference catalog products (name/unit/price **snapshotted**) or are custom lines; labour,
  transport and other service costs, discount, tax (default 8 %, applied after discount).
  VND amounts are rounded to whole đồng, midpoint away from zero. Discount ≤ subtotal.
* `issue` freezes the version. A new version (`revisions`) supersedes the previous one when issued.
  One DRAFT and one live (ISSUED/ACCEPTED) version per survey request at most (DB unique indexes).
* Customer: **accept** or **request revision** (reason required). Accept and revision-request race
  safely: exactly one wins.

Statuses: `DRAFT → ISSUED → ACCEPTED | REVISION_REQUESTED`, any older version → `SUPERSEDED`.

## 3. Contract (hand-signed PDF)

1. Sales creates the contract from the **ACCEPTED** quotation (pricing snapshot copied), edits terms, `issue`s it.
   Issuing computes a **canonical hash** of the contract content.
2. Sales generates the official PDF outside the system, uploads it (`CONTRACT_DOCUMENT`), and attaches it
   with the canonical hash — the API refuses a PDF attached to a different content version.
3. `publish` → **AWAITING_SIGNATURE**. The customer downloads, signs by hand, scans, uploads
   (`SIGNED_CONTRACT`) and submits it together with the SHA-256 of the official PDF they signed.
4. Review by the owning Sales or a Manager: **approve → SIGNED**, **reject (reason) → SIGNATURE_REJECTED**
   (the customer can resubmit). Double approval applies once.
5. Changes after issue are **amendments**: a new contract version supersedes the old one.

Statuses: `DRAFT → ISSUED → AWAITING_SIGNATURE → UNDER_REVIEW → SIGNED | SIGNATURE_REJECTED → … → PAID`; replaced versions `SUPERSEDED`.

## 4. Payment — PayOS, one 100 % payment

* Only a **SIGNED** contract can be paid; amount = contract total (VND, whole units, ≤ 10 billion).
* `POST …/payments` is idempotent: it returns the live link if one exists, otherwise creates a new
  attempt (unique `orderCode`, 15 min expiry). At most 5 new attempts per contract per 10 minutes
  (counted in the database before each new attempt — not atomic, but concurrent creations are bounded by
  the one-payable-attempt unique index) plus the HTTP rate limit.
* **The return URL is never trusted.** Payment is confirmed only by (a) a PayOS webhook whose
  HMAC-SHA256 signature verifies, or (b) server-to-server reconciliation (`GET /v2/payment-requests/{id}`),
  which also runs on customer status polls (throttled) and every minute in the background.
* Confirmation is atomic: amount must match **exactly** → attempt PAID **and** contract SIGNED → PAID
  in one transaction. A mismatch → `REVIEW_REQUIRED`; money received for an already-paid contract →
  `REFUND_REQUIRED` (both visible to staff, nothing automatic).
* Duplicate webhooks are deduplicated by payload SHA-256; webhook + reconciliation races confirm once.

Attempt statuses: `PENDING`, `PROCESSING`, `PAID`, `CANCELLED`, `EXPIRED`, `FAILED`, `REVIEW_REQUIRED`, `REFUND_REQUIRED`.

## 5. Installation

One **responsible technician** carries out an installation project (no team, shifts or milestones any more).

### 5.1 State machine

```
PENDING_ASSIGNMENT ──assign──▶ PENDING_TECHNICIAN_ACCEPTANCE ──accept──▶ SCHEDULED ──start──▶ IN_PROGRESS
      ▲                              │  ▲                                     │                  │
      └────────── decline ───────────┘  └── reassign / reschedule ────────────┘          completion report
                                                                                                 ▼
                         COMPLETED ◀──customer ACCEPT── PENDING_CUSTOMER_ACCEPTANCE ◀── REWORK_REQUIRED
                                                              │                              ▲
                                                              └── customer REQUEST_REWORK ───┘
                                                              └── Manager return-to-rework ──┘
CANCELLED ◀── Manager cancel (only from PENDING_ASSIGNMENT, PENDING_TECHNICIAN_ACCEPTANCE, SCHEDULED)
```

| From | Action (who) | To | Rules |
|---|---|---|---|
| — | create (Manager/Admin) | PENDING_ASSIGNMENT | contract `PAID`; one project per contract (even after cancellation); operational zone resolved from the site's commune (`operational_zone_id`) |
| PENDING_ASSIGNMENT, PENDING_TECHNICIAN_ACCEPTANCE, SCHEDULED | assign / reassign / reschedule (Manager/Admin) | PENDING_TECHNICIAN_ACCEPTANCE | eligibility (below); `reason` required when a technician was already assigned; the previous assignment becomes `REPLACED` |
| IN_PROGRESS, REWORK_REQUIRED | reassign / reschedule (Manager/Admin, reason) | unchanged | the new assignment is `PENDING`; the new technician must accept before any further work |
| PENDING_TECHNICIAN_ACCEPTANCE | accept (assigned technician) | SCHEDULED | repeat accept is a no-op; refused after the assigned interval ended |
| PENDING_TECHNICIAN_ACCEPTANCE | decline (assigned technician, reason) | PENDING_ASSIGNMENT | assignment `DECLINED`, reason visible to the Manager; an accepted job cannot be declined |
| SCHEDULED | start (responsible technician) | IN_PROGRESS | accepted assignment; local date ≥ scheduled start date; before the scheduled end (afterwards `409 INSTALLATION_SCHEDULE_EXPIRED`: the Manager must reschedule); every required checklist item SATISFIED or NOT_APPLICABLE; repeat is a no-op |
| IN_PROGRESS, REWORK_REQUIRED | completion report (responsible technician) | PENDING_CUSTOMER_ACCEPTANCE | report ≤ 8000 chars; ≥ 1 evidence photo of the **current round**; ≥ 1 PANEL and ≥ 1 INVERTER asset (`Installation:RequirePanelAndInverterAssets`) |
| PENDING_CUSTOMER_ACCEPTANCE | ACCEPT (customer owner) | COMPLETED | completion + handover recorded; assignment `COMPLETED`; repeat accept is a no-op |
| PENDING_CUSTOMER_ACCEPTANCE | REQUEST_REWORK (customer owner, reason) | REWORK_REQUIRED | the next round starts: new evidence is required for the next report |
| PENDING_CUSTOMER_ACCEPTANCE | return-to-rework (Manager/Admin, reason ≤ 500) | REWORK_REQUIRED | the pending round is closed as `REWORK_REQUESTED` with the manager as decider and the reason (event `RETURNED_TO_REWORK`); as after a customer rework, new evidence is required for the next report. Completion stays customer-only |
| PENDING_ASSIGNMENT, PENDING_TECHNICIAN_ACCEPTANCE, SCHEDULED | cancel (Manager/Admin, reason) | CANCELLED | current assignment `CANCELLED`; contract and payment untouched |

Every other transition is refused with `409 INSTALLATION_INVALID_STATE`. Every transition is guarded by the project
`revision` (optimistic concurrency, conditional UPDATE) and recorded in `installation_event` with from/to status.

### 5.2 Responsible technician and assignments

* `installation_assignment` keeps the history: `PENDING → ACCEPTED → COMPLETED`, or `DECLINED`, `REPLACED`, `CANCELLED`.
  At most one **current** (PENDING or ACCEPTED) assignment per project (partial unique index + application check).
* Eligibility uses the shared `TechnicianEligibility` (same as survey scheduling): active account with the TECHNICIAN role,
  scheduling enabled, active membership in the active zone of the site's commune (`TECHNICIAN_OUTSIDE_ZONE`; a technician may belong to several zones),
  every local day touched by the interval is a working day of the technician, the start/end lie inside that day's working hours
  (breaks inside the interval are fine), no leave on the working part of any day, and no other commitment — confirmed
  (ACCEPTED/ASSIGNED) survey appointments and other current installation assignments — within
  `Scheduling:BufferBetweenAppointmentsMinutes` (default 30; a fixed conservative gap, not a travel time). The technician's own
  pending survey capacity holds (PENDING_ACCEPTANCE/ESCALATED) overlapping the interval plus buffer do not make them unavailable:
  they are re-homed to other eligible technicians of their zones in the same transaction (event `HOLD_MOVED`, the planner shared
  with survey acceptance; those technicians are locked too), and the assignment is refused with `409 TECHNICIAN_SCHEDULE_CONFLICT`
  when one of them cannot be handed over. The candidate list reports such a technician as eligible. Installation work does
  not count toward the daily survey limit. An interval may touch at most `Installation:MaxAssignmentDays` (14) local days and must
  start in the future within `Installation:MaxPlanningHorizonDays` (365).
* A site without a valid commune → `422 SITE_LOCATION_REQUIRED`; its commune is not mapped to an active zone → `422 NO_ZONE_COVERAGE`;
  the zone is marked not serviceable → `422 ZONE_NOT_SERVICEABLE` (same codes as survey booking; candidates and assignment).
* The check is repeated inside the transaction under the `technician_profile` row locks (new and previous technician), the same
  locks used by survey booking, self-acceptance, Sales assignment and the admin technician changes; PostgreSQL additionally rejects
  overlapping current assignments of one technician (exclusion constraint `ex_installation_assignment_no_overlap`).
* **Both directions:** a current installation assignment is a busy interval for survey slots, booking, self-acceptance and Sales
  assignment. A technician with current installation assignments cannot be disabled for scheduling, put on leave over them, moved out
  of the project's zone, given a weekly schedule that no longer covers them, or have the account deactivated
  (`409 TECHNICIAN_HAS_CONFLICTING_APPOINTMENTS`, items with status `INSTALLATION_ASSIGNMENT`). Reassign the installation first.
  The current assignment of a **started, unfinished** project (IN_PROGRESS, REWORK_REQUIRED, PENDING_CUSTOMER_ACCEPTANCE) keeps
  blocking disabling scheduling, removing the project's zone and account deactivation even after its interval ended.
* On a reassignment the outgoing technician may take over the new technician's survey holds (their replaced assignment leaves them
  in the same transaction).
* Coverage is read again after the site lock and the `FOR SHARE` lock on the zone; when the site's zone changed since the
  pre-read (its commune moved to another zone), the operation is retried (bounded) with the new zone locked, and
  `operational_zone_id` is written only from the in-lock read (project creation and assignment). An unfinished project
  blocks deactivating its zone (`409 ZONE_IN_USE`) and removing its commune from the zone (`409 COMMUNE_IN_USE`).
* While a project of the site is not COMPLETED/CANCELLED the customer cannot change the site's administrative location
  (`409 SITE_HAS_ACTIVE_INSTALLATION`); project creation and assignment take the same site lock.

### 5.3 Execution (responsible technician)
* Only the technician of the current (PENDING/ACCEPTED) assignment, or of the COMPLETED assignment once the customer accepted,
  can read the project (others, including technicians of replaced or declined assignments: `404`); only the current one can act.
  Accept/decline need the PENDING assignment; start, evidence, assets and the completion report need it ACCEPTED
  (`409 INSTALLATION_ASSIGNMENT_NOT_ACCEPTED`).
* **Evidence**: verified `INSTALLATION_EVIDENCE` photos uploaded by the responsible technician themself, each file used once
  (`FILE_ALREADY_LINKED`), recorded per round in `installation_evidence` while IN_PROGRESS or REWORK_REQUIRED. Evidence is never deleted.
* **Assets**: equipment actually installed (type PANEL/INVERTER/BATTERY/METER/OTHER, manufacturer, model, serial, position, warranty
  months 0–600). Serial numbers are unique per manufacturer (`INSTALLATION_DUPLICATE_SERIAL`).
* **Checklist** (Manager/Admin): EVN grid connection, PCCC fire safety, roof structural assessment. Items start not required; the
  Manager marks applicability and satisfaction (SATISFIED requires a verified `PROJECT_DOCUMENT`). Required PENDING items block the start.
  The checklist is staff-only; the technician sees `canStart` / `startBlockedReason`.

### 5.4 Acceptance and handover (Customer)
* The customer **ACCEPTs** or **REQUEST_REWORK** (reason required). Decisions are immutable; the acceptance history keeps every round
  with `decidedBy` (`CUSTOMER`, or `STAFF` when a Manager returned the report to rework — no staff id is shown).
* A Manager/Admin can return a pending report to rework (`POST /api/installation-projects/{id}/return-to-rework`) when it is wrong or
  incomplete, but cannot complete the project. A customer who never answers is a **manual business follow-up** (contact the
  customer); the system has no automatic acceptance or timeout.
* On acceptance the project is COMPLETED and the **handover** (assets with warranty end dates, evidence, acceptance history, the
  technician's name) is available to the customer and staff.

### 5.5 Cancellation
* Manager/Admin only, before IN_PROGRESS, with a reason. The contract stays `PAID` and no payment is changed: **refunds are a manual
  business process** outside the system. The contract cannot get a second project.

### 5.6 Who sees what
* **Staff** (Manager/Admin): everything, including assignment history with decline reasons, checklist and audit events.
* **Responsible technician**: interval, site address and customer contact, evidence, assets, acceptance history, `canStart`.
* **Customer**: status, scheduled interval, technician name only once accepted, evidence, assets, acceptance history.
  Never the checklist, events, assignment history or technician ids. Write responses use the caller's view too.

## 6. Files (all phases)

The frontend uploads directly to S3; the backend only receives **object references**. The object key
must be `{purposePrefix}{uploaderUserId}/…` and is verified server-side (HEAD, size, magic bytes,
streamed SHA-256 where required). Unverifiable objects stay `PENDING_VERIFICATION` and cannot be used.
See [installation-workflow-api-integration.md](installation-workflow-api-integration.md#files).

### 6.1 Presigned URL security design (installation evidence)

Customers, the involved technician and staff view and download installation evidence photos through **short-lived
presigned S3 GET URLs** that the backend issues per photo, per request, after authorization
([API](installation-workflow-api-integration.md#evidence-photos-viewing-and-download)).

* **Authorization first, 404 otherwise.** Customer: `installation_project.customer_user_id` = caller. Technician: a current
  (PENDING/ACCEPTED) or COMPLETED assignment on the project — the same rule as the technician project view; a replaced or
  declined technician loses access. Manager/Admin: any project. A caller without access gets `INSTALLATION_PROJECT_NOT_FOUND`
  (no existence disclosure); evidence that is not on the requested project gets `INSTALLATION_EVIDENCE_NOT_FOUND`.
* **The location never comes from the client.** Bucket, key and version id are read from the `file_reference` row linked by
  `installation_evidence.file_id`. Before signing the backend requires: status `VERIFIED`, purpose `INSTALLATION_EVIDENCE`, a
  verified content type allowed for the purpose, a bucket in `Storage:DefaultBucket` / `Storage:AllowedBuckets`, and a key
  under the purpose prefix `installations/` with no `..`, no leading `/`, no empty segment (defence in depth against a
  corrupted or tampered row). Otherwise `FILE_NOT_VERIFIED` / `FILE_NOT_SIGNABLE` (409) and nothing is signed.
* **Pinned, typed, named.** The URL carries `versionId` (the exact verified version is served even if the key was uploaded
  again), `response-content-type` = the verified type (magic-byte detected, never the client's claim) and
  `response-content-disposition` = `inline` or `attachment` with an RFC 6266 / RFC 5987 file name (base name only; quotes,
  CR/LF, `;`, `%`, path separators and control characters removed; ASCII fallback plus `filename*=UTF-8''…`).
* **Short-lived.** `Storage:S3:PresignedUrlTtlSeconds` (default 300, startup fails outside 30–3600); a per-request lifetime
  never exceeds it. URL responses are sent with `Cache-Control: no-store`; the FE must not persist URLs (see the API guide).
* **Existence check.** With `Storage:S3:VerifyObjectBeforeSigning=true` (default) the pinned version is HEAD-checked first:
  missing → `410 INSTALLATION_EVIDENCE_OBJECT_MISSING`, unreachable → `503 STORAGE_UNAVAILABLE`. S3 disabled →
  `503 STORAGE_NOT_CONFIGURED`. Signing itself is a local SigV4 computation (no call to S3).
* **HTTPS only**, unless `Storage:S3:ServiceUrl` is explicitly `http://` (a local MinIO/LocalStack); a signed URL with any other
  scheme is refused. A `ServiceUrl` that is not an absolute http(s) URL fails startup.
* **Nothing sensitive in responses or logs.** No response field contains the bucket, key, version id or credentials (only the
  opaque URL itself). Logs record evidence id, project id, disposition, role and outcome — never the URL, the key or the
  signature (asserted by tests).

**Required S3 setup**

* Private bucket, **S3 Block Public Access on** (all four settings), no public ACLs or bucket policy grants, versioning enabled.
* The signing credential (instance role, or `Storage__S3__AccessKeyId`/`SecretAccessKey` from the environment) is the same
  read-only identity used for verification. A presigned URL can do no more than its signer, so grant it **only** reads:

```json
{
  "Version": "2012-10-17",
  "Statement": [{
    "Sid": "ReadVerifiedUploads",
    "Effect": "Allow",
    "Action": ["s3:GetObject", "s3:GetObjectVersion"],
    "Resource": ["arn:aws:s3:::<bucket>/installations/*"]
  }]
}
```

  Add the other purpose prefixes (`onsite-surveys/*`, `quotations/*`, `contracts/*`, `knowledge/*`, `project-documents/*`) to
  `Resource` only because upload verification reads them too; never grant `s3:PutObject`, `s3:DeleteObject*`, `s3:ListBucket`
  or `s3:*`. Note that HEAD on a missing key answers 403 instead of 404 without `s3:ListBucket`; the backend then reports
  `STORAGE_UNAVAILABLE` rather than 410, which is the safer failure.
* Optionally add an explicit bucket-policy `Deny` for requests with `aws:SecureTransport = false`, and for presigned requests
  older than the TTL (`s3:signatureAge`, in milliseconds) as a second bound.

**Limitations.** A URL is a bearer token until it expires: whoever obtains it (shared screen capture, forwarded link) can read
that single photo for at most the TTL. Revoking access (for example a technician who was replaced) stops new URLs immediately
but cannot recall URLs already issued; keep the TTL short.

## 7. Knowledge chatbot (RAG)

Admins manage knowledge documents (PDF/DOCX/TXT/MD); only **published** versions are used to answer.
Answers cite their sources and say so when the documents are insufficient. See
[rag-architecture.md](rag-architecture.md).
