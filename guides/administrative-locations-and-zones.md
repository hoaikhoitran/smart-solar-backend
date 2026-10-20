# Administrative locations and operational zones

This guide describes how SmartSolar knows **where** a property site is and **who** may serve it. It replaces the former
3-level "service area" model (`service_area`, `technician_service_area`, dotted codes `79.769.26794`), which is gone from
the code and the API (no compatibility shims).

Two layers:

1. **Official administrative catalog** — reference data from the national registry (Quyết định 19/2025/QĐ-TTg, effective
   2025-07-01): provinces and communes (phường / xã / đặc khu). Since the 2025 reorganisation there is **no district level**.
   The catalog also keeps the official conversion table old → new (the *legacy map*) so users who only know a pre-2025
   address can find their current commune.
2. **Operational zones** — internal, editable groupings of official communes, served by technicians. Zones are data
   managed by **Admin only** (structure and commune mapping); nothing in code lists them. Managers read them and manage
   technician memberships of the zones they are responsible for (see "Authorization" below).

Coverage of a site is therefore: **site → `commune_code` → the active mapping of that commune → its zone (must be active)**.

All responses use the envelope `{ "isSuccess", "traceId", "data", "error": { "code", "message", "details" } }`.

---

## 1. Catalog model

| Table | Key / notes |
|---|---|
| `administrative_province` | `code` varchar(2) PK (`^[0-9]{2}$`, leading zeros kept, e.g. `"01"`, `"79"`), `name`, `unit_type` `PROVINCE`\|`MUNICIPALITY`, `valid_from`, `valid_to` (NULL = in force), `dataset_key`, `created_at` |
| `administrative_commune` | `code` varchar(5) PK (`^[0-9]{5}$`), `province_code` FK, `name`, `search_name` (accent-free lower case), `unit_type` `WARD`\|`COMMUNE`\|`SPECIAL_ZONE`, `valid_from`, `valid_to`, `dataset_key`; UNIQUE `(province_code, code)` (FK target of sites); indexes `(province_code, name)`, `(province_code, search_name)` |
| `administrative_legacy_commune_map` | `id`, `commune_code` FK (current commune), `legacy_commune_code` varchar(5) NULL (NULL = a district-level predecessor, e.g. huyện Côn Đảo → đặc khu Côn Đảo), `legacy_commune_name`, `legacy_commune_search_name`, `legacy_district_code` varchar(3), `legacy_district_name`, `legacy_district_search_name`, `legacy_province_code`, `legacy_province_name`, `merge_type` `WHOLE`\|`PARTIAL`\|`UNCHANGED`, `source_note`, `match_key`, `dataset_key` |
| `administrative_catalog_import` | `id`, `dataset_key` UNIQUE, `source_url`, `source_sha256`, `effective_from`, `files_sha256` (jsonb), `counts` (jsonb: manifest counts + what the apply changed), `status` `VALIDATED`\|`APPLIED`\|`REJECTED`, `validated_at`, `applied_at`, `applied_by` (operator name, never a credential), `notes` |

* **Codes are text.** Leading zeros are significant (`"00123"` ≠ `123`). The database checks the digit formats with portable
  `length()` + `substr() BETWEEN '0' AND '9'` expressions; the application checks them again.
* **Legacy unique key.** EF Core 8 cannot express `UNIQUE … NULLS NOT DISTINCT`, so the row carries
  `match_key = legacy_district_code || ':' || COALESCE(legacy_commune_code, '-')` (e.g. `718:25741`, `755:-`) — enforced by the
  check `ck_administrative_legacy_commune_map_match_key` — and the unique index is `(commune_code, match_key)`. This is exactly
  "(commune, legacy district, legacy commune) unique with NULL treated as a value".
* **"Active"** means `valid_from <= today AND (valid_to IS NULL OR valid_to > today)`, where *today* is the calendar date in
  Vietnam (UTC+07:00). A code missing from a newer dataset is **closed** (`valid_to` = the new effective date), never deleted.
* **Packaged dataset** (verified, do not edit):
  `src/SmartSolar.Infrastructure/Persistence/ReferenceData/AdministrativeCatalog/vn-2025-07-01-hcm-79/`
  (`manifest.json`, `communes.csv`, `legacy-commune-map.csv`): province 79 *Thành phố Hồ Chí Minh* (MUNICIPALITY),
  168 communes (113 WARD, 54 COMMUNE, 1 SPECIAL_ZONE), 493 legacy rows (WHOLE 390, PARTIAL 98, UNCHANGED 5; from former
  provinces 79/74/77). The manifest holds the source URL, legal basis, the sha256 of the source xlsx and of each CSV. The API
  build copies these folders to `ReferenceData/AdministrativeCatalog/` next to `SmartSolar.Api.dll`.

## 2. Controlled import (CLI only)

There is **no HTTP endpoint and no startup import**. The catalog is written only by a command-line mode of the API host:

```bash
# dry run (default): validates the files and prints the diff against the target database; writes nothing
dotnet SmartSolar.Api.dll admin-catalog import \
  --dataset ReferenceData/AdministrativeCatalog/vn-2025-07-01-hcm-79 --confirm-host <db-host> --confirm-database <db-name> --confirm-user <db-user>

# apply in ONE transaction
dotnet SmartSolar.Api.dll admin-catalog import \
  --dataset ReferenceData/AdministrativeCatalog/vn-2025-07-01-hcm-79 \
  --confirm-host <db-host> --confirm-database <db-name> --confirm-user <db-user> --apply --operator "<your name>"
```

* The connection string is `ConnectionStrings:DefaultConnection` (appsettings + environment variables, e.g.
  `ConnectionStrings__DefaultConnection`). The command prints `Target database: host=… database=… user=…` (never the password) and
  **refuses to run** (exit code 2) unless `--confirm-host`, `--confirm-database` and `--confirm-user` all equal the resolved
  target. On Supabase's shared pooler every project in a region has the same host; only the user name `postgres.<project-ref>`
  tells Dev from another project, so `--confirm-user` is mandatory. A database error is reported as one sanitized line (exception
  type + SQLSTATE, nothing applied). Exit codes: 0 success, 1 invalid dataset or refused import, 2 usage / configuration /
  target confirmation / database error.
* **Validation** (nothing touches the database before it passes): manifest fields (dataset key, 2-digit province code, unit
  type, `effectiveFrom`, https source URL, 64-hex source sha256); sha256 of every CSV equals the manifest; exact CSV headers
  (RFC 4180 quoting, UTF-8); code formats as strings (commune 5 digits — a code that lost a leading zero is rejected — legacy
  district 3, provinces 2); unit types and merge types; duplicates (commune codes; legacy `(commune, match_key)`); every commune
  belongs to the manifest province; every legacy row references a commune of `communes.csv`; every commune has a predecessor; and
  every declared count (communes, wards, rural communes, special zones, legacy rows, per merge type, per legacy province).
* **Diff** against the database: province new/changed, communes new / changed (name, unit type) / closed / reopened, legacy rows
  new / changed / unchanged / retained (rows of the database not in this dataset are kept).
* **Refusals:** a dataset older than catalog data already stored for its province; commune codes stored under another province;
  a `dataset_key` already **APPLIED with different checksums** (publish new files under a new key). Re-applying the same files is
  idempotent (nothing changes, the first `APPLIED` record stays). A refused `--apply` is recorded as `REJECTED` when the key has no
  record yet. `VALIDATED` is reserved (dry runs write nothing).

**Process:** download the official xlsx → package a new dataset folder (new `datasetKey`, manifest with checksums) through code
review → dry run against the target → review the printed diff (especially *closed* codes: sites referencing them keep their
value but count as having no valid location until the customer picks the successor) → `--apply` with explicit approval.

## 3. Public read APIs (anonymous, rate limit `catalog-read`, `Cache-Control: public, max-age=3600`)

### `GET /api/administrative-units/provinces`

```json
{ "isSuccess": true, "data": [ { "code": "79", "name": "Thành phố Hồ Chí Minh", "unitType": "MUNICIPALITY" } ] }
```

### `GET /api/administrative-units/communes?provinceCode=79[&q=thu duc][&unitType=WARD]`

Active communes of the province ordered by name. `provinceCode` is required (2 digits); `q` (≤ 100 characters) is an
accent-insensitive "contains" on the name; `unitType` ∈ `WARD`, `COMMUNE`, `SPECIAL_ZONE`. Invalid filters → `400 VALIDATION_FAILED`.

```json
{ "isSuccess": true, "data": [ { "code": "26824", "name": "Phường Thủ Đức", "unitType": "WARD", "provinceCode": "79" } ] }
```

### `GET /api/administrative-units/legacy-lookup?q=hiep thanh[&provinceCode=79]`

Matches legacy (pre-2025) commune **or** district names (accent-insensitive "contains") or codes (exact, e.g. `q=25741` or
`q=760`), only towards active current communes (of `provinceCode` when given). `q` needs at least 2 characters; at most 50
matches (`truncated = true` beyond). Each match is one legacy unit — `LEGACY_COMMUNE` or `LEGACY_DISTRICT` (all predecessor
communes of that district) — with **every** candidate current commune. `ambiguous` = more than one distinct candidate;
`requiresConfirmation` is **always true**: the API never selects a commune, the user must confirm one.

```json
{ "isSuccess": true, "data": {
  "query": "hiep thanh", "provinceCode": "79", "ambiguous": true, "requiresConfirmation": true, "truncated": false,
  "matches": [
    { "matchType": "LEGACY_COMMUNE", "legacyCommune": { "code": "25741", "name": "Phường Hiệp Thành" },
      "legacyDistrict": { "code": "718", "name": "Thành phố Thủ Dầu Một" }, "legacyProvince": { "code": "74", "name": "Tỉnh Bình Dương" },
      "ambiguous": true, "requiresConfirmation": true,
      "candidates": [
        { "code": "25750", "name": "Phường Phú Lợi", "unitType": "WARD", "provinceCode": "79", "mergeType": "PARTIAL",
          "legacyDistrict": { "code": "718", "name": "Thành phố Thủ Dầu Một" }, "legacyCommune": { "code": "25741", "name": "Phường Hiệp Thành" } },
        { "code": "25747", "name": "Phường Thủ Dầu Một", "unitType": "WARD", "provinceCode": "79", "mergeType": "PARTIAL", "…": "…" } ] },
    { "matchType": "LEGACY_COMMUNE", "legacyCommune": { "code": "26770", "name": "Phường Hiệp Thành" },
      "legacyDistrict": { "code": "761", "name": "Quận 12" }, "ambiguous": false, "requiresConfirmation": true,
      "candidates": [ { "code": "26782", "mergeType": "WHOLE", "…": "…" } ] } ] } }
```

`PARTIAL` means the predecessor's territory was split: the old name alone cannot decide the commune.

## 4. Site location

* `POST /api/customers/me/sites` accepts `provinceCode` and `communeCode` (both optional; a commune requires its province).
  `districtCode` / `wardCode` no longer exist. The free-text `province`, `district` (optional), `ward` stay display text only.
* `PUT /api/customers/me/sites/{siteId}/administrative-location` `{ "provinceCode": "79", "communeCode": "27442" }` (CUSTOMER;
  both required; rate limit `scheduling-write`).

Validation: formats → `400 SCHEDULING_VALIDATION_FAILED` (`PRESURVEY_VALIDATION_FAILED` on site creation) with field details; unknown
province / commune, or a commune of another province → `422 ADMINISTRATIVE_LOCATION_UNKNOWN`; a code no longer in force →
`422 ADMINISTRATIVE_LOCATION_SUPERSEDED` (superseded codes are refused for new values). Not the caller's site → `404
PROPERTY_SITE_NOT_FOUND`. A change is refused with `409 SITE_HAS_ACTIVE_BOOKING` while an open or confirmed survey appointment
of the site records a zone (open appointments whose start passed are ignored) and with `409 SITE_HAS_ACTIVE_INSTALLATION` while an
installation project of the site is unfinished. Re-sending the current location succeeds without change. Legacy appointments with
`operational_zone_id` NULL get the zone now mapping the commune in the same transaction (rule M1).

```json
{ "isSuccess": true, "data": { "propertySiteId": "…", "provinceCode": "79", "communeCode": "27442", "communeName": "Phường Bình Tân",
  "coverageStatus": "COVERED", "zone": { "zoneId": "…", "code": "KHU-BINH-TAN", "name": "Khu Bình Tân" } } }
```

In the database `property_site` gains `commune_code` varchar(5) with the composite FK `(province_code, commune_code)` →
`administrative_commune (province_code, code)` (MATCH SIMPLE: not checked while `commune_code` is NULL) and the check
`ck_property_site_commune_code` (a commune requires its province). `province_code` keeps its column and check.

## 5. Operational zones

| Table | Notes |
|---|---|
| `operational_zone` | `id`, `code` varchar(50) UNIQUE (`^[A-Z0-9][A-Z0-9-]{1,49}$`, upper-cased by the server, immutable), `name`, `is_active`, `is_serviceable` (default true), `responsible_manager_user_id` NULL FK, `created_at`, `updated_at`, `revision` |
| `operational_zone_commune` | `id`, `zone_id` FK, `commune_code` FK `administrative_commune`, `assigned_by_user_id`, `assigned_at`, `removed_at`, `removed_by_user_id`, `removal_reason`; partial UNIQUE `(commune_code) WHERE removed_at IS NULL` (**a commune belongs to one zone at a time**, history kept); check removed_at ⇔ removed_by |
| `technician_zone_membership` | `id`, `technician_user_id` FK `technician_profile`, `zone_id` FK, `assigned_by_user_id`, `assigned_at`, `removed_at`, `removed_by_user_id`; partial UNIQUE `(technician_user_id, zone_id) WHERE removed_at IS NULL` (a technician may belong to several zones) |

`technician_appointment.operational_zone_id` and `installation_project.operational_zone_id` record the zone at booking /
assignment time. Existing appointments keep their technician; nothing is reassigned.

### Coverage codes (stable)

| Code | Meaning | Where |
|---|---|---|
| `COVERED` | the site's zone is active and serviceable and (with an interval) some member can take it | coverage evaluation |
| `SITE_LOCATION_REQUIRED` | no valid commune (missing, not in force, or not inside the site's province); replaces `SITE_AREA_REQUIRED` | 422 on slots, booking, accept/assign, reschedule, installation |
| `NO_ZONE_COVERAGE` | the commune is not mapped to an **active** zone; replaces `AREA_NOT_COVERED` | 409 + `dispatchAvailable: true` on customer booking (temporarily uncovered); 422 for installation |
| `ZONE_NOT_SERVICEABLE` | the zone is active but `is_serviceable = false` (temporarily) | 409 + `dispatchAvailable: true` on customer booking; 422 for installation |
| `NO_ZONE_TECHNICIAN` | no active membership of a schedulable technician (active account, TECHNICIAN role, scheduling on) | coverage evaluation |
| `NO_AVAILABLE_TECHNICIAN` | members exist but none can take any bookable slot in the booking horizon | coverage evaluation |
| `SCHEDULE_CONFLICT` | some member is free elsewhere in the horizon, but none at the requested time | coverage evaluation |

`TECHNICIAN_OUTSIDE_ZONE` (409) replaces `TECHNICIAN_OUTSIDE_SERVICE_AREA` as the eligibility reason.

The single evaluation service is `ZoneCoverageService.EvaluateAsync(propertySiteId, startsAtUtc?, endsAtUtc?, ignoreCommitmentIds?)`
(`SmartSolar.Modules.Scheduling.Zones`): it returns the status above, the site location and zone, and the zone members split into
`Eligible` / `Ineligible` with reason codes (via `TechnicianEligibility`; a member blocked only by their own pending holds is
eligible with `RequiresHoldRehoming`). It is read-only; bookings and assignments re-check everything under their locks. Slots answer
200 with `coverageStatus` + `dispatchAvailable`; a customer booking/reschedule without capacity fails with the precise code
(`SITE_LOCATION_REQUIRED` → 422; `NO_ZONE_COVERAGE`, `ZONE_NOT_SERVICEABLE`, `NO_ZONE_TECHNICIAN`, `NO_AVAILABLE_TECHNICIAN`,
`SCHEDULE_CONFLICT` → 409, all dispatchable) and `details = { coverageStatus, dispatchAvailable }`. Uncovered-zone dispatch builds on this service:
see `guides/dispatch-and-uncovered-zones.md`.

### Concurrency

Lock order everywhere: `technician_profile FOR UPDATE` → `property_site FOR UPDATE` → `operational_zone FOR SHARE` (bookings,
acceptance/assignment, reschedule, installation project creation/assignment). Zone writes take the zone `FOR UPDATE`
(deactivation, serviceability, commune set) and then count the unfinished work, so they cannot miss work that is committing; a
booking whose zone lost the commune or was deactivated meanwhile re-reads its coverage under the lock and retries/fails. The partial
unique index is the backstop when two zones race for one commune (`409 COMMUNE_ALREADY_MAPPED`). SQLite (tests) skips row locks.

### Authorization (final, 2026-10-20)

| Action | Admin | Manager | Enforcement |
|---|---|---|---|
| Read zones, coverage, members | ✓ | ✓ | controller roles |
| Create / update / deactivate / serviceability / commune mapping | ✓ | ✗ `403` | controller `[Authorize(Roles = ADMIN)]` **and** the handler re-checks the actor's current account + ADMIN role (`403 ZONE_MANAGEMENT_FORBIDDEN`) |
| `PUT /api/admin/technicians/{id}/zones` | any zone | only zones whose `responsible_manager_user_id` is the caller: every membership **added or removed** must be in scope, other memberships must stay as they are (`403 ZONE_SCOPE_FORBIDDEN`, `details.zoneIds`) | handler, inside the transaction under the technician lock, against current roles |

Manager responsibility is the existing single `operational_zone.responsible_manager_user_id` (set by an Admin); a zone without a
responsible manager is Admin-only for memberships.

### Admin/Manager API (`api/admin/operational-zones`, read: ADMIN, MANAGER; write: ADMIN; `scheduling-read` / `scheduling-write`)

| Method | Route | Notes |
|---|---|---|
| GET | `/api/admin/operational-zones` | every zone: `zoneId, code, name, isActive, isServiceable, responsibleManagerUserId, communeCount, memberCount, schedulableMemberCount, hasAvailableTechnician, revision, createdAt, updatedAt` |
| GET | `/api/admin/operational-zones/{id}` | `{ zone, communes: [{ code, name, unitType, provinceCode, assignedAt }] }`; unknown → `404 ZONE_NOT_FOUND` |
| POST | `/api/admin/operational-zones` `{ code, name, isActive?=true, isServiceable?=true, responsibleManagerUserId? }` | `201`; duplicate code → `409 ZONE_CODE_EXISTS`; bad code → `400`; manager must be an active MANAGER/ADMIN (`422 RESPONSIBLE_MANAGER_INVALID`) |
| PUT | `/api/admin/operational-zones/{id}` `{ name, isActive?, isServiceable?, responsibleManagerUserId, revision }` | null flags keep the current value; stale → `409 ZONE_STALE_REVISION`; deactivating or marking not serviceable while unfinished work records the zone → `409 ZONE_IN_USE` `details { blockingAppointments, blockingInstallationProjects }` |
| PUT | `/api/admin/operational-zones/{id}/communes` `{ communeCodes: [], reason?, revision }` | replaces the set (≤ 500 five-digit codes): new codes are mapped, others soft-removed with `reason`; codes must be active catalog communes (`422 ADMINISTRATIVE_LOCATION_UNKNOWN` / `_SUPERSEDED`, `details { unknownCommuneCodes, supersededCommuneCodes }`); a commune of another **active** zone → `409 COMMUNE_ALREADY_MAPPED` `details.communes[{ communeCode, zoneId, zoneCode, zoneName }]` (a commune held by an inactive zone is moved, its old mapping gets `MOVED_TO_ZONE <code>`); removing/moving a commune whose sites have unfinished survey appointments or installation projects → `409 COMMUNE_IN_USE` `details { communes[{ communeCode, blockingAppointments, blockingInstallationProjects }], blockingAppointments, blockingInstallationProjects }`; the revision increases only when something changed |
| GET | `/api/admin/operational-zones/coverage[?provinceCode=79]` | `{ unmappedCommuneCount, unmappedCommunes[{ code, name, unitType, provinceCode }], zonesWithoutSchedulableTechnicians[...] }` (unmapped = active communes without an active mapping to an active zone) |
| GET | `/api/admin/operational-zones/{id}/technicians` | active members `{ technicianUserId, fullName, isActiveUser, isSchedulingActive, isSchedulable, assignedAt }` |
| PUT | `/api/admin/technicians/{id}/zones` `{ zoneIds: [] }` | replaces `…/service-areas`; profile required (`409 TECHNICIAN_PROFILE_REQUIRED`); unknown zone `404 ZONE_NOT_FOUND`; confirmed future survey work or installation work outside the new zones → `409 TECHNICIAN_HAS_CONFLICTING_APPOINTMENTS` (`TECHNICIAN_OUTSIDE_ZONE`); pending holds no longer covered are re-homed or escalated (`rehomedHolds`); removed memberships are kept as history |
| GET | `/api/admin/technicians?zoneId=` | replaces `?serviceAreaId=`; each technician lists `zones` |

Example:

```http
POST /api/admin/operational-zones
{ "code": "khu-thu-duc", "name": "Khu Thủ Đức" }
→ 201 { "data": { "zoneId": "…", "code": "KHU-THU-DUC", "isActive": true, "isServiceable": true, "communeCount": 0, "revision": 0, … } }

PUT /api/admin/operational-zones/{zoneId}/communes
{ "communeCodes": ["26824", "26800"], "revision": 0 }
→ 200 { "data": { "zone": { "communeCount": 2, "revision": 1, … }, "communes": [ { "code": "26800", "name": "Phường Linh Xuân", … }, … ] } }
```

Removed: `GET /api/service-areas`, `api/admin/service-areas*`, `PUT /api/admin/technicians/{id}/service-areas`,
`PUT /api/customers/me/sites/{siteId}/administrative-area`, and `districtCode` / `wardCode` in every contract. Views now carry
`zone { zoneId, code, name }` instead of `serviceArea` (slots, open jobs, availability, escalated queue, installation staff
view; `zoneName` on the installation technician view; `operationalZoneId` on summaries).

## 6. Initial zone proposal (data artifact, not applied)

`guides/reference/operational-zones-initial-proposal.csv` (generated by `ZoneProposalTests` from the official legacy map;
regenerate with `SMARTSOLAR_REGENERATE_ZONE_PROPOSAL=1`): one proposed zone per **former district** —
`zone_code` `KHU-<ASCII slug>`, `zone_name` `Khu <former district name without Quận/Huyện/Thành phố/Thị xã>` — with status
`PROPOSED` for the 151 communes whose predecessors all come from that single former district, and `NEEDS_REVIEW` (zone left empty,
`former_districts` listing every candidate `province:district name`) for the 17 others. Columns:
`zone_code, zone_name, commune_code, commune_name, unit_type, status, former_districts`. Nothing reads this file; zones are
created only later through the Admin API after business approval.

## 7. Frontend checklist

- [ ] Address form: province dropdown from `GET /api/administrative-units/provinces`; commune dropdown from
      `GET /api/administrative-units/communes?provinceCode=…` (type-ahead with `q`, accent-insensitive). Send the **codes**
      (`provinceCode`, `communeCode`, strings with leading zeros) plus the free-text names for display; no district field is
      needed (keep an optional free-text "old district" if the UX wants it).
- [ ] "I only know my old address": call `GET /api/administrative-units/legacy-lookup?q=…&provinceCode=…` (≥ 2 characters),
      show every match with all its candidates and `mergeType` (`PARTIAL` = split, the old name is not enough), and **require the
      user to confirm** one current commune (`requiresConfirmation` is always true; never auto-select, even when only one candidate).
- [ ] Existing sites without a commune: prompt the customer to set it with
      `PUT /api/customers/me/sites/{id}/administrative-location`; handle `422 ADMINISTRATIVE_LOCATION_UNKNOWN / _SUPERSEDED /
      _OUT_OF_SERVICE_AREA` (a province other than 79: outside the service area),
      `409 SITE_HAS_ACTIVE_BOOKING`, `409 SITE_HAS_ACTIVE_INSTALLATION`; show `coverageStatus` from the response.
- [ ] Booking screens: read `coverageStatus` / `dispatchAvailable` from the slots response (always 200); on booking handle
      `422 SITE_LOCATION_REQUIRED` (ask for the location) and `409 NO_ZONE_COVERAGE / ZONE_NOT_SERVICEABLE / NO_ZONE_TECHNICIAN /
      NO_AVAILABLE_TECHNICIAN / SCHEDULE_CONFLICT` (offer a dispatch request when
      `error.details.dispatchAvailable`), and `409 DISPATCH_REQUEST_OPEN`. Read `zone` instead of `serviceArea`.
- [ ] Zone admin (write: Admin only; Managers read-only and handle `403 ZONE_SCOPE_FORBIDDEN` on memberships outside their zones): list with counts and `hasAvailableTechnician`; create (code upper-cased), edit with `revision`
      (`409 ZONE_STALE_REVISION` → reload); commune picker per province with the full set on save (show
      `COMMUNE_ALREADY_MAPPED` with the holding zone, `COMMUNE_IN_USE` with the blocking counts, `ZONE_IN_USE` on deactivate /
      not-serviceable); coverage page (unmapped communes, zones without schedulable technicians); technician page with
      `PUT …/technicians/{id}/zones` (multi-select; show `TECHNICIAN_HAS_CONFLICTING_APPOINTMENTS` details and `rehomedHolds`).
- [ ] The initial zone proposal CSV can seed the zone-admin UI's suggestions, but every zone must be created explicitly.

## Database

The integrator creates the single migration for this change (no migration is part of this change set). It must:

* **create** `administrative_province`, `administrative_commune`, `administrative_legacy_commune_map`, `administrative_catalog_import`,
  `operational_zone`, `operational_zone_commune`, `technician_zone_membership` (with their checks, indexes and FKs);
* **add** `property_site.commune_code` varchar(5) NULL + index `ix_property_site_province_commune` + FK
  `fk_property_site_administrative_commune` + check `ck_property_site_commune_code`;
* **add** (not rename) `technician_appointment.operational_zone_id` (+ `ix_technician_appointment_operational_zone_id`,
  `fk_technician_appointment_operational_zone`) and `installation_project.operational_zone_id` (+ index,
  `fk_installation_project_operational_zone`) — EF's differ proposes `RENAME COLUMN service_area_id TO operational_zone_id`, which
  must be replaced by an `AddColumn`;
* **drop only** the checks `ck_property_site_district_code` and `ck_property_site_ward_code`;
* **not drop** `service_area`, `technician_service_area`, `technician_appointment.service_area_id`,
  `installation_project.service_area_id` (and their FKs/indexes), `property_site.district_code`, `property_site.ward_code` —
  they leave the EF model but stay in the database (empty / NULL on Dev) until an approved cleanup.

After the migration, load the catalog with the CLI (section 2) before creating zones.
