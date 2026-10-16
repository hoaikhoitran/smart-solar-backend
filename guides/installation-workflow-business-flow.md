# SmartSolar Installation Flow — business flow

The Installation Flow starts where Flow 1 ends (the assigned technician has **acknowledged** the survey appointment)
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
| Who | Only the technician of the **active, ACKNOWLEDGED** appointment of the survey request. |
| Start | `POST …/onsite-survey/start` from 120 min (`OnSiteSurvey:EarlyStartMinutes`) before the slot. |
| Draft | `PUT …/onsite-survey` saves partial data at any time (DRAFT → IN_PROGRESS). |
| Evidence | Photos/PDF uploaded by the FE to S3 (`ONSITE_SURVEY_EVIDENCE`), registered with `POST /api/files`, then attached with a category (ROOF, ELECTRICAL_CABINET, …). |
| Complete | Requires the full measurement set **and at least one ROOF photo**. Atomically: report → COMPLETED and survey request **Scheduled → Completed**. Idempotent. |
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

### 5.1 Planning (Manager)
* Projects are created only from **PAID** contracts (one project per contract).
* The Manager picks a team (≤ 12 technicians) with **exactly one leader** and daily shifts
  (e.g. 08:00–12:00, 13:00–17:00). `schedule/preview` shows per technician × shift feasibility.
* Shifts reuse the Flow 1 scheduling core: working calendar, per-technician weekly schedule, leave,
  earliest base departure, **Vietmap travel time + safety buffer** to and from surrounding survey
  appointments. Installation shifts do **not** count toward the daily survey-appointment limit.
* `PUT …/schedule` books everybody on every shift atomically (all or nothing). Survey bookings and
  installation planning are serialised through the technician-profile row lock, and PostgreSQL rejects
  any overlapping ACTIVE assignment of one technician (exclusion constraint). Affected survey route
  chains are re-estimated by the durable recalculation queue.
* A technician with upcoming installation shifts cannot be disabled for scheduling or have the
  account deactivated until the team is changed.

### 5.2 Execution (leader)
* Default plan: 7 weighted milestones; ELECTRICAL_WIRING, TESTING_COMMISSIONING and
  ACCEPTANCE_PREPARATION are mandatory. The Manager may replace the plan (reason required after start).
* Only the **leader** updates progress, adds **verified photo evidence** (`INSTALLATION_EVIDENCE`),
  completes milestones (evidence of the current rework round required; repeat completion is a no-op),
  registers installed assets (serial numbers unique per manufacturer; at least one PANEL and one
  INVERTER by default) and requests customer acceptance.
* Manager checklist items (e.g. grid-connection paperwork) can be marked SATISFIED with a PROJECT_DOCUMENT.

### 5.3 Acceptance and handover (Customer)
* The customer **ACCEPTs** or **REQUEST_REWORK** (reason required). Rework reopens the project; the
  leader reopens milestones (new round, old evidence no longer counts) and requests acceptance again.
* On acceptance the project is COMPLETED and the **handover** (assets, warranty end dates, evidence,
  documents) becomes available to the customer and staff.

Project statuses: `PLANNING → SCHEDULED → IN_PROGRESS → PENDING_CUSTOMER_ACCEPTANCE → COMPLETED`, with `REWORK_REQUIRED` loops.

## 6. Files (all phases)

The frontend uploads directly to S3; the backend only receives **object references**. The object key
must be `{purposePrefix}{uploaderUserId}/…` and is verified server-side (HEAD, size, magic bytes,
streamed SHA-256 where required). Unverifiable objects stay `PENDING_VERIFICATION` and cannot be used.
See [installation-workflow-api-integration.md](installation-workflow-api-integration.md#files).

## 7. Knowledge chatbot (RAG)

Admins manage knowledge documents (PDF/DOCX/TXT/MD); only **published** versions are used to answer.
Answers cite their sources and say so when the documents are insufficient. See
[rag-architecture.md](rag-architecture.md).
