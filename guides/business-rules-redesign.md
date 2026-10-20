# Business-rule redesign — Flow 1 and Installation Flow (overview)

> **Superseded in part (2026-10-20):** the `service_area` hierarchy (province → district → ward) and its APIs were
> replaced by the official two-level catalog (province `79` → 5-digit commune codes) and **operational zones**
> (one active zone per commune, technicians in many zones), plus dispatch requests for uncovered areas and
> Admin-configurable timing settings. `service-areas` endpoints, `districtCode` and `wardCode` no longer exist.
> See [administrative-locations-and-zones.md](administrative-locations-and-zones.md),
> [dispatch-and-uncovered-zones.md](dispatch-and-uncovered-zones.md), [system-settings.md](system-settings.md) and
> [database-migration-administrative-zones-dispatch.md](database-migration-administrative-zones-dispatch.md).
> Sections below that mention service areas describe the 2026-10-16 state.

Branch `fix/business-rules-flow-1-2`. This page summarises **what changed and why**, the new user journeys,
the breaking API changes for the frontend, and where each detail is documented.
Status: implemented and tested; database migration `20261016161547_BusinessRulesFlow1And2Redesign`
created and rehearsed locally — **not applied** to Supabase Dev (see
[database-migration-business-rules-redesign.md](database-migration-business-rules-redesign.md)).

| Topic | Detailed guide |
|---|---|
| Service areas, booking, technician self-acceptance, Sales fallback, admin | [scheduling-and-admin.md](scheduling-and-admin.md) |
| Installation Flow (business rules) | [installation-workflow-business-flow.md](installation-workflow-business-flow.md) |
| Installation Flow (API) | [installation-workflow-api-integration.md](installation-workflow-api-integration.md) |
| Product gallery, simulation storage | [catalog-and-simulation-api-integration.md](catalog-and-simulation-api-integration.md) |
| RAG admin configuration | [rag-architecture.md](rag-architecture.md) §8, [knowledge-base-api-integration.md](knowledge-base-api-integration.md) |
| Installation evidence viewing/download (presigned S3) | [installation-workflow-api-integration.md](installation-workflow-api-integration.md) "Evidence photos", security design in [installation-workflow-business-flow.md](installation-workflow-business-flow.md) §6.1 |
| Database migration report | [database-migration-business-rules-redesign.md](database-migration-business-rules-redesign.md) |
| Supabase security findings, hardening, deployment plan | [supabase-security.md](supabase-security.md) |

## 1. Flow 1 — old vs new

| | Before | Now |
|---|---|---|
| Who picks the technician | Sales claims the request, then assigns a technician | Technicians **serving the customer's service area** see the booking and **accept it themselves**; first accept wins |
| Area model | none (GPS distance + Vietmap routes) | `service_area` hierarchy (province → district → ward) managed by Admin/Manager; technicians have 1..N areas |
| Capacity before acceptance | a nearest technician was reserved | one eligible technician's capacity is **held** (never shown, never an acceptance); the hold moves when another technician accepts |
| Nobody accepts | — | after the acceptance deadline the appointment is **ESCALATED** to the Sales queue; Sales (owner) assigns with a reason; Manager/Admin override |
| Sales ownership | claim = step 1 of scheduling | claim is **commercial ownership only** (quotation, contract); can happen before or after the survey; Manager can set the owner |
| Travel time | Vietmap route API (required) | **removed**; a configurable buffer between commitments (`Scheduling:BufferBetweenAppointmentsMinutes`, 30) — not a travel estimate |
| Technician acknowledge step | ASSIGNED → ACKNOWLEDGED | removed; ACCEPTED / ASSIGNED are confirmed |

```text
Customer creates pre-survey → (optional) runs simulation and selects one → submits survey request
  → books a slot (only slots with an eligible technician in the site's area are offered)
  → appointment PENDING_ACCEPTANCE, visible to eligible technicians of that area
  → first eligible technician accepts → ACCEPTED (customer sees the technician) → on-site survey
Nobody accepts before the deadline → ESCALATED → Sales owner assigns an eligible technician (reason) → ASSIGNED
```

Appointment states: `PENDING_ACCEPTANCE`, `ESCALATED`, `ACCEPTED`, `ASSIGNED`, `COMPLETED`, `CANCELLED`
(reschedule = CANCELLED + a replacement appointment). Customer-facing `acceptanceStatus`:
`WAITING_FOR_TECHNICIAN`, `CONFIRMED`, `NEEDS_STAFF`, `CANCELLED`, `COMPLETED`.
Survey request states: `Pending` → `Scheduled` (technician confirmed) → `Completed`; claiming no longer changes it.
An open appointment (PENDING_ACCEPTANCE / ESCALATED) that nobody confirmed before its start is cancelled automatically
(`NOT_ACCEPTED_BEFORE_START`) by the escalation worker — or by the customer's next booking — and the request returns to
`Pending`, so the customer can book again.

Eligibility (one shared rule set, also used by installations): active account with the TECHNICIAN role,
scheduling enabled, membership in an active area covering the site, working hours, no leave, no conflicting
survey appointment or installation assignment within the buffer, daily limit. Ineligibility codes:
`TECHNICIAN_INACTIVE`, `TECHNICIAN_SCHEDULING_DISABLED`, `TECHNICIAN_OUTSIDE_SERVICE_AREA`,
`OUTSIDE_WORKING_HOURS`, `TECHNICIAN_ON_LEAVE`, `TECHNICIAN_SCHEDULE_CONFLICT`, `DAILY_LIMIT_REACHED`.

Concurrency: every accept / assign / release / reschedule / cancel runs in one transaction that locks the
involved `technician_profile` rows (sorted) and updates the appointment conditionally on its revision and
status; the database exclusion constraint `ex_technician_appointment_no_overlap` is the backstop. Exactly
one of two concurrent accepts (or accept vs Sales assign) wins; the loser gets `409 APPOINTMENT_ALREADY_TAKEN`.
Verified by PostgreSQL race tests.

## 2. Installation Flow — old vs new

| | Before | Now |
|---|---|---|
| Responsible party | a team (members, one leader) on shared shifts | **one responsible technician** per project, chosen by the Manager among technicians of the project's area; the technician organises their own crew |
| Scheduling | shifts per team member | one scheduled interval (`scheduledStartsAt`/`scheduledEndsAt`, ≤ 14 days), conflict-checked against survey appointments and other installations |
| Progress | 7 weighted milestones, per-milestone progress and photos | none: start → completion report with evidence photos and installed equipment |
| Technician consent | — | the technician **accepts or declines** the assignment |
| Kept | quotation, contract, PayOS 100% payment, EVN/PCCC/structural checklist (applicability per project), installed assets with serials/warranty, customer acceptance or rework, handover, audit events | unchanged in substance |

```text
On-site survey completed → Sales quotation → customer accepts → contract signed → 100% PayOS payment verified
  → Manager creates project (PENDING_ASSIGNMENT) → assigns responsible technician + interval (PENDING_TECHNICIAN_ACCEPTANCE)
  → technician accepts (SCHEDULED) | declines (back to PENDING_ASSIGNMENT)
  → technician starts on/after the scheduled date, required checklist satisfied (IN_PROGRESS)
  → technician reports completion: evidence photos + PANEL and INVERTER assets (PENDING_CUSTOMER_ACCEPTANCE)
  → customer accepts (COMPLETED, handover) | requests rework (REWORK_REQUIRED → new evidence → report again)
Manager may reassign/reschedule (reason), cancel before IN_PROGRESS (contract stays PAID; refund is manual), or return a
pending completion report to rework (reason). Completion stays the customer's decision; an unresponsive customer is a
manual business follow-up. A start after the scheduled interval ended is refused (`INSTALLATION_SCHEDULE_EXPIRED`).
```

## 3. Product catalog, simulation, RAG

* **Products** have an ordered gallery with one primary image (`images[]`); `imageUrl` is deprecated.
* **Simulations** are temporary (cache, 24 h TTL); the customer explicitly **selects** one, which stores only a
  compact summary in PostgreSQL (optional verified S3 snapshot).
* **RAG**: Admin manages versioned runtime settings (generation model from an allowlist, temperature,
  output tokens, chunk size/overlap, top-k, min score, context, history, prompt version) via
  `/api/admin/rag-configuration`; activation takes effect within ≤ 15 s without redeploy. Chunk changes need
  an explicit `reindex-stale`; live chunks are replaced atomically only when the new indexing succeeds.
  Secrets, URLs and the embedding model (`text-embedding-3-small`, 1536) stay in the environment.

## 4. New and changed endpoints

New: `GET /api/service-areas`; `GET|POST /api/admin/service-areas`, `PUT /api/admin/service-areas/{id}`,
`GET /api/admin/service-areas/{id}/technicians`, `GET /api/admin/service-areas/coverage`;
`PUT /api/admin/technicians/{id}/service-areas`; `PUT /api/customers/me/sites/{siteId}/administrative-area`;
`GET /api/technicians/me/open-appointments[/{id}]`; `POST /api/technicians/me/appointments/{id}/accept|release`;
`GET /api/survey-requests/escalated`; `POST /api/admin/survey-appointments/{id}/assign-technician`;
`PUT /api/admin/survey-requests/{id}/sales-owner`;
`GET /api/installation-projects/{id}/technician-candidates`, `PUT …/{id}/assignment`, `POST …/{id}/cancel`, `POST …/{id}/return-to-rework`;
`POST /api/technicians/me/installation-projects/{id}/accept|decline|evidence|completion-report`;
`GET …/installation-projects/{id}/evidence` and `GET …/evidence/{evidenceId}/url?disposition=inline|attachment`
(customer, involved technician, Manager/Admin — short-lived presigned S3 URL);
`POST /api/pre-surveys/{id}/simulations/{simulationId}/select`, `DELETE /api/pre-surveys/{id}/simulations/selection`;
`GET /api/admin/products/{id}`, `POST|DELETE|PUT …/products/{id}/images…`;
`/api/admin/rag-configuration` (read, models, versions, draft, validate, activate, rollback, reindex-stale).

Changed: `POST /api/survey-requests/{id}/assign-technician` now requires `reason` (fallback/override only);
`POST /api/survey-requests/{id}/claim` = commercial ownership only; `GET /api/technicians/availability` lists
eligible technicians with reasons (no `startsAt/endsAt`); `PUT /api/admin/technicians/{id}/profile` takes only
`isSchedulingActive`, `maxAppointmentsPerDay`; `POST /api/customers/me/sites` accepts `provinceCode`,
`districtCode`, `wardCode`; `GET …/simulations` returns `{ items, selected, cacheAvailable }`;
`POST …/simulations` no longer selects; product responses add `images[]`.

Removed: `POST /api/technicians/me/appointments/{id}/acknowledge`; installation schedule preview/confirm,
leader, milestones and milestone routes, `acceptance-request` (replaced by `completion-report`).

## 5. Role matrix (new/changed behaviour)

| Action | Customer | Sales | Technician | Manager | Admin |
|---|---|---|---|---|---|
| Book / reschedule / cancel own survey | ✓ | | | | |
| Set site administrative codes | ✓ (no active booking) | | | | |
| List / accept open jobs in own areas, release own job | | | ✓ | | |
| Claim commercial ownership | | ✓ | | | |
| Assign technician (escalated/pending, with reason) | | ✓ owner only | | ✓ override | ✓ override |
| Set Sales owner | | | | ✓ | ✓ |
| Manage service areas, technician areas, profiles, leave | | | | ✓ | ✓ |
| Create / assign / cancel installation, checklist, return to rework | | | | ✓ | ✓ |
| Accept/decline/start/report installation | | | ✓ responsible | | |
| Accept handover / request rework | ✓ owner | | | | |
| Manage product gallery | | | | ✓ | ✓ |
| RAG configuration | | | | | ✓ |

## 6. Frontend migration checklist

1. Site forms: send `provinceCode`/`districtCode`/`wardCode` (from `GET /api/service-areas`); existing sites without
   codes get `422 SITE_AREA_REQUIRED` on slots/booking until the customer sets them.
2. Customer booking screens: show `acceptanceStatus`; the technician appears only once confirmed.
3. Technician app: new "open jobs" list + accept/release; remove the acknowledge button.
4. Sales: "escalated" queue; assign requires a reason; claim is independent from scheduling.
5. Admin: service-area management and technician area assignment; profile no longer has a base location.
6. Installation screens: replace team/shift/milestone UIs by assignment, accept/decline, evidence, completion report,
   Manager return-to-rework. Evidence photos: list via `…/evidence`, then request `…/evidence/{id}/url` **each time** a photo
   is shown (`disposition=inline`) or downloaded (`attachment`); never store the URL beyond `expiresAt`, re-request on S3 403;
   handle 410 (photo removed from storage) and 503 (storage not configured/unavailable).
7. Simulation: handle the list object, explicit select, 410/503.
8. Product UI: use `images[]`; manage the gallery with the image endpoints.
9. Admin RAG settings page: `/api/admin/rag-configuration`.

## 7. Configuration

`Scheduling`: `BufferBetweenAppointmentsMinutes` (30), `AcceptanceWindowMinutes` (240),
`AcceptanceMinimumLeadMinutes` (120), `CustomerChangeLeadMinutes` (120), `EscalationPollSeconds` (60).
`Installation`: `MaxAssignmentDays` (14). `SolarSimulation`: `ResultTtlMinutes` (1440), `MaxRecentResults` (20),
`Snapshots:*` (disabled). `Catalog`: `MaxImagesPerProduct` (10), `ImageAllowedHosts` ([]).
`Rag`: `GenerationModels`, `PromptVersions`, `ConfigurationLimits`, bootstrap defaults. Removed: `Providers:Vietmap:*`.

## 8. Testing report (local, 2026-10-19)

| Suite | Result |
|---|---|
| `dotnet build SmartSolar.sln --no-incremental` | 0 warnings, 0 errors |
| .NET tests with disposable PostgreSQL 17 + Redis 7 (`SMARTSOLAR_TEST_POSTGRES`, `SMARTSOLAR_TEST_REDIS`) | **1238 passed, 0 failed, 0 skipped** |
| PostgreSQL/Redis race suites, repeated on the final code | 3× 49/49 without failure (earlier: 5× on the audit snapshot) |
| Python RAG service (`pytest -q` with a disposable pgvector database) | 98 passed, 1 skipped (live OpenAI embedding smoke — not run by design) |
| `ruff check .` | clean |
| `dotnet ef migrations has-pending-model-changes` | none |
| Migration rehearsal on a restored Dev backup | Up → Down (data identical to before) → Up; 0 invalid constraints/indexes |
| Supabase hardening draft on a local Supabase-like emulation | anon/authenticated 63 → 0 tables, RLS 63/63, idempotent |

Not verified (needs approval / real services): real S3 (presign + HEAD, snapshot upload), OpenAI generation/embeddings,
PayOS, Supabase migration and grants, multi-instance Redis/cache behaviour under load.

## 9. Deployment checklist

1. Approve and run the plan in [supabase-security.md](supabase-security.md) §5 (backup → maintenance window → migration →
   hardening → Data API restriction → verification), with the pre-flight queries of the migration report §4a.
2. S3: private bucket, Block Public Access on, versioning on, CORS for the FE upload origin; signer/verifier credential with
   only `s3:GetObject` + `s3:GetObjectVersion` on the used prefixes; set `Storage__*` (incl. `PresignedUrlTtlSeconds`).
3. RAG: deploy the RAG service before or together with the API; if `RAG_LLM_ALLOWED_MODELS` is set it must include every
   model of `Rag:GenerationModels`; after activating a configuration with new chunk settings run `reindex-stale`.
4. Create service areas and technician memberships before announcing booking; ask customers to set site codes.
5. Enable `Features__InstallationWorkflowEnabled` / `Features__KnowledgeBaseEnabled` only after steps 1–3.

## 10. Known limitations

* Issued presigned URLs cannot be revoked before their TTL (default 300 s). A HEAD 403 from S3 (credential without
  `s3:ListBucket`) is reported as `STORAGE_UNAVAILABLE`, not 410.
* RAG audit: draft edits record only the creator; chat messages store the model but not the configuration version
  (both need new columns — deliberately not added).
* Started open holds keep counting as busy until the worker cancels them (≤ `EscalationPollSeconds`).

