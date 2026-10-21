# Pre-deployment migration review — 20261020051352_AdministrativeLocationsZonesDispatchAndSettings

Date: 2026-10-20. Scope: READ-ONLY review. Nothing was applied to Supabase Dev; Dev was only queried inside
`BEGIN READ ONLY` sessions with `default_transaction_read_only = on`. Rehearsals used disposable local PostgreSQL 17
databases, dropped afterwards. **Migration execution is not approved.**

## A. Generated SQL

| File | Purpose | sha256 |
|---|---|---|
| `guides/sql/migration-20261020051352-administrative-zones-dispatch-settings.up.sql` | forward, 20261016161547 → 20261020051352 (476 lines) | `6cbe7cbe4d329d2fd5748d5988219d7c328df8cb850d05896c46039a8c913a54` |
| `guides/sql/migration-20261020051352-administrative-zones-dispatch-settings.rollback.sql` | rollback, 20261020051352 → 20261016161547 (87 lines) | `ae58f5c48b73e5f39bee417622a0fd2fb207977e36cf8a662c58a9308694244b` |

Generated with `dotnet-ef migrations script <from> <to>` (EF Core 8.0.32) and a dummy connection string that points to port 1
(no connection is possible). `dotnet-ef migrations has-pending-model-changes`: *No changes have been made to the model since
the last migration.* The forward script is one `START TRANSACTION … COMMIT` block with exactly one
`INSERT INTO "__EFMigrationsHistory"` (`20261020051352_AdministrativeLocationsZonesDispatchAndSettings`, `8.0.32`); it
contains no other migration.

## B. Operations (forward)

| Kind | Count | Objects |
|---|---|---|
| `DROP CONSTRAINT` (CHECK only) | 3 | `ck_technician_appointment_assignment_source` (re-added widened), `ck_property_site_district_code`, `ck_property_site_ward_code` (legacy dotted-code checks; the columns and data stay) |
| `ADD` column (nullable, no default, metadata-only) | 7 | `technician_appointment.operational_zone_id`, `.acceptance_reminder_sent_at`, `.acceptance_window_minutes`, `.cross_zone_authorization_id`, `.settings_version`; `installation_project.operational_zone_id`; `property_site.commune_code varchar(5)` |
| `CREATE TABLE` | 16 | `administrative_province`, `administrative_commune`, `administrative_legacy_commune_map`, `administrative_catalog_import`, `operational_zone`, `operational_zone_commune`, `technician_zone_membership`, `dispatch_request`, `dispatch_preferred_window`, `dispatch_proposal`, `dispatch_event`, `dispatch_notification`, `cross_zone_authorization`, `system_setting`, `system_setting_revision`, `system_settings_version` |
| `CREATE INDEX` / `CREATE UNIQUE INDEX` | 48 / 8 | unique: `ux_administrative_catalog_import_dataset_key`, `ux_administrative_legacy_commune_map_commune_match_key`, `ux_operational_zone_code`, `ux_operational_zone_commune_active` (`WHERE removed_at IS NULL`), `ux_technician_zone_membership_active` (`WHERE removed_at IS NULL`), `ux_dispatch_request_active_survey_request` (`WHERE status IN ('OPEN','PROPOSAL_PENDING')`), `ux_dispatch_proposal_accepted` (`WHERE status = 'ACCEPTED'`), `ux_system_setting_revision_key_version` |
| `ADD CONSTRAINT` on existing tables | 6 | CHECK `ck_technician_appointment_assignment_source` (adds `'MANAGER_DISPATCH'`, keeps the 3 old values), CHECK `ck_property_site_commune_code` (`commune_code IS NULL OR (province_code IS NOT NULL AND length(commune_code) = 5)`), FK `fk_property_site_administrative_commune` (`(province_code, commune_code)` → `administrative_commune(province_code, code)`), FK `fk_technician_appointment_operational_zone`, FK `fk_technician_appointment_cross_zone_authorization`, FK `fk_installation_project_operational_zone` |
| FKs in total | 42 | every one `ON DELETE RESTRICT`; no cascade |
| CHECK constraints on new tables | 40 | status/reason/resolution vocabularies, code formats, SLA and expiry invariants, singleton `system_settings_version.id = 1` |
| `ALTER TABLE … ENABLE ROW LEVEL SECURITY` | 16 | every new table; no policies |
| `DO $$` | 1 | `REVOKE ALL ON <16 new tables> FROM anon` / `FROM authenticated` when those roles exist |

Absent from the forward script (checked by keyword scan): `DROP TABLE`, `DROP COLUMN`, `DROP INDEX`, `TRUNCATE`, `DELETE`,
`UPDATE`, `RENAME`, `ALTER COLUMN`, `SET NOT NULL`, `GRANT`, `CASCADE`, `DISABLE`, `ALTER DEFAULT PRIVILEGES`,
`CREATE POLICY`, functions, triggers, extensions, `OWNER TO`. Ordering is EF-generated (tables before their FKs; the
composite unique key `ux_administrative_commune_province_code` exists before the `property_site` FK that uses it).

## C. Destructive or potentially unsafe statements

| Statement | Risk | Assessment |
|---|---|---|
| 3 × `DROP CONSTRAINT` (CHECK) | loses a DB-level guard, not data | the two `property_site` checks guarded the retired dotted `district_code`/`ward_code` model (34/34 Dev sites have both NULL); the appointment check is re-created as a superset in the same transaction |
| `ADD CONSTRAINT` CHECK/FK on `technician_appointment`, `property_site`, `installation_project` | validation scan + `ACCESS EXCLUSIVE` / `SHARE ROW EXCLUSIVE` locks (also on `user_account`, `survey_request`, `technician_profile`, `installation_project` while FKs are created) | Dev rows: 3 / 34 / 0; validation passes (assignment sources on Dev: `NULL`, `SALES_ASSIGNED`); locks are milliseconds but queue behind long transactions — run with a `lock_timeout` (F, step 5) |
| No `IF EXISTS` on the 3 `DROP CONSTRAINT` | the script fails (and rolls back) if a name is missing | all three exist on Dev (verified) |
| Rollback script | `DROP TABLE` × 16, `DROP COLUMN` × 7, `DELETE FROM "__EFMigrationsHistory"` | destructive by design — see G |

No statement modifies or deletes existing rows.

## D. RLS and permissions

Dev today (read-only): 13 migrations (last `20261016161547_BusinessRulesFlow1And2Redesign`), **64** public tables,
RLS **64/64**, `anon` = 0 / `authenticated` = 0 tables with any privilege, `service_role` = 64; every table owned by
`postgres` (the migration role: not superuser, BYPASSRLS).

* **Expected after the migration: 80 tables, RLS 80/80.** 64 = 63 EF tables + `knowledge_chunk` (RAG-owned, created from
  `src/SmartSolar.RagService/sql/knowledge_chunk.sql`). Rehearsed on a Dev-equivalent database (pgvector + `knowledge_chunk`
  + Supabase-like roles): 64 → **80/80**, client privileges 0 immediately after the migration (before any hardening re-run).
* **Default privileges** (read-only `pg_default_acl`, schema `public`): objects created by `postgres` grant only `postgres` and
  `service_role` (tables `arwdDxtm`, sequences `rwU`, functions `X`) — **no `anon` / `authenticated`**: the earlier hardening
  is in place. The migration does not alter default privileges. The 6 entries granting `anon`/`authenticated` belong to
  `supabase_admin` (platform-created objects only); the migration runs as `postgres`, so they do not apply to the new tables.
* **New tables:** RLS on (no policies ⇒ deny for client roles), explicit `REVOKE ALL … FROM anon, authenticated` in the same
  transaction, so there is no exposure window even if default privileges were ever reset. `service_role` receives the same
  full access it has on the existing 64 tables (pre-existing policy: server-side key, BYPASSRLS; optional revocation is a
  separate decision, `supabase-security-hardening-draft.sql` lines 119–122).
* **Data API / GraphQL:** PostgREST is connected (2 sessions) and event triggers `pgrst_ddl_watch`, `issue_pg_graphql_access`,
  `issue_pg_cron_access`, `issue_pg_net_access` fire on DDL (schema-cache reload / grants on their own schemas only); without
  client privileges the new tables are not reachable through them.
* **Sensitive tables** (`system_setting*`, `cross_zone_authorization`, `dispatch_*`, `technician_zone_membership`): none
  readable by client roles after the migration.

## E. Compatibility and historical data

**Applies to the exact schema it was generated for.** A full schema fingerprint (1370 lines: every column with type,
nullability and default, every constraint definition, every index definition; `knowledge_chunk` excluded) of Dev (read-only)
is **identical** to a local database migrated to `20261016161547` from this working tree.

**Rehearsal of the generated files with `psql`** (local, disposable):
* schema-only clone of Dev: `up.sql` ⇒ schema identical to the EF-applied target (1704 lines); `rollback.sql` ⇒ schema
  identical to `20261016161547` = Dev;
* restored Dev backup with data: `up.sql` → `rollback.sql` → `up.sql`: row counts of `user_account`, `customer`,
  `property_site`, `survey_request`, `pre_survey`, `technician_appointment`, `installation_project`, `product`,
  `product_image`, `service_area`, `technician_service_area`, `technician_profile` identical at every step; 0 invalid
  constraints; every new column NULL on existing rows;
* rollback guard: with one `MANAGER_DISPATCH` appointment, `rollback.sql` aborts with *Down refused …* and changes nothing.

| Dev data (read-only, 2026-10-20) | After the migration |
|---|---|
| 34 sites, `province_code`/`district_code`/`ward_code` all NULL, 13 with free-text ward | unchanged; `commune_code` NULL (allowed by the new CHECK and the MATCH SIMPLE FK). No inference/backfill. With the new API these sites answer `SITE_LOCATION_REQUIRED` until the customer sets a commune (legacy lookup is suggest-only) |
| 3 appointments: 2 ASSIGNED (`SALES_ASSIGNED`, both in the past), 1 CANCELLED | unchanged; satisfy the widened check; `operational_zone_id` / snapshot columns NULL; no worker acts on past ASSIGNED appointments |
| `service_area_id` NULL on 3/3 appointments; 0 installation projects | columns, FKs and indexes retained, untouched |
| `service_area` 0 rows, `technician_service_area` 0 rows | tables retained (unmapped by the new model) |
| 18 users, 3 customers, 11 survey requests (9 Pending, 2 Scheduled), 24 pre-surveys, 12 products, 6 product images, 2 technician profiles, 0 customer contracts | untouched (no DML) |

**Running old API.** The `smartsolar-api` container (image built 2026-10-19T08:09Z, `restartPolicy=no`) is connected to Supabase
Dev and contains the `20261016` code (`ServiceAreaHandlers`; no zone/dispatch types). After the migration it keeps working:
every table/column it maps still exists, the added columns are nullable and ignored by its explicit column lists, its
`assignment_source` values are a subset of the widened check, and it never touches the new tables. EF does not validate the
migration history at runtime and nothing in the code migrates or seeds at startup. It must not run **together** with the new API
for long (two escalation workers with different rules over the same appointments; old code would write legacy fields only) —
replace it in one step.

**New API startup** needs no imported data: no migrate/seed at startup; the catalog CLI is a separate process mode
(`admin-catalog import`, exits before the web host is built); scheduling settings fall back to defaults when `system_setting` is
empty. Functionally, without the catalog every site location is refused (`ADMINISTRATIVE_LOCATION_UNKNOWN`), and without zones
every booking becomes a `NO_ZONE_COVERAGE` dispatch request that escalates to Admins after 4 h.

**Background workers in the new API** (all on by default): `AppointmentEscalationWorker` (open appointments only — Dev has
none), `DispatchWorker` (`Dispatch:WorkerEnabled`, default **true**; acts only on dispatch rows — none exist; never expires
requests), `PaymentReconciliationWorker` (payment attempts; 0 contracts), `KnowledgeIngestionWorker` (RAG flag). None modifies
existing rows unexpectedly on first start.

**Verification scripts.** `post-migration-verification-zones-dispatch.sql` covers this migration (history 14, the 16 named
tables with RLS, retained legacy objects, exclusive commune index, widened assignment check, invalid constraints/indexes,
catalog counts, one zone per commune, client access + RLS over all tables). It does **not** check: the total of 80 tables, the
`ck_dispatch_request_reason` vocabulary, `dispatch_request.released_appointment_id`, operational-zone audit columns, or default
privileges. The older `pre-migration-checks.sql` / `post-migration-verification.sql` target the 20261013/20261016 migrations
(hard-coded history counts) and must **not** be used for this one. Supplementary read-only checks:

```sql
BEGIN READ ONLY;
SELECT 'public_tables', count(*), CASE WHEN count(*) = 80 AND bool_and(relrowsecurity) THEN 'OK' ELSE 'FAIL' END
FROM pg_class WHERE relnamespace = 'public'::regnamespace AND relkind IN ('r','p');
SELECT 'dispatch_reason_check', pg_get_constraintdef(oid),
       CASE WHEN pg_get_constraintdef(oid) LIKE '%ZONE_NOT_SERVICEABLE%' AND pg_get_constraintdef(oid) LIKE '%ASSIGNMENT_RELEASED%' THEN 'OK' ELSE 'FAIL' END
FROM pg_constraint WHERE conname = 'ck_dispatch_request_reason';
SELECT 'new_columns', count(*), CASE WHEN count(*) = 3 THEN 'OK' ELSE 'FAIL' END FROM information_schema.columns
WHERE table_schema = 'public' AND ((table_name = 'dispatch_request' AND column_name = 'released_appointment_id')
   OR (table_name = 'operational_zone' AND column_name IN ('created_by_user_id', 'updated_by_user_id')));
SELECT 'default_acl_postgres_client_grants', count(*), CASE WHEN count(*) = 0 THEN 'OK' ELSE 'FAIL' END
FROM pg_default_acl d JOIN pg_namespace n ON n.oid = d.defaclnamespace, LATERAL unnest(d.defaclacl) a
WHERE n.nspname = 'public' AND d.defaclrole = 'postgres'::regrole AND split_part(a::text, '=', 1) IN ('anon', 'authenticated');
ROLLBACK;
```

## F. Recommended Supabase Dev sequence (each step needs explicit approval)

0. Freeze: commit/tag the exact source (the migration files are currently **uncommitted**) and build the new API image from it;
   keep the current image (`smart-solar-backend-api`, built 2026-10-19T08:09Z) tagged for rollback.
1. Fresh backup of Dev: `pg_dump -Fc` + sha256 (the existing local dump is from 2026-10-13, before two migrations); confirm the
   Supabase-side backup/PITR status.
2. Re-run the read-only pre-checks (this review's queries): 13 migrations, 64 tables, RLS 64/64, client privileges 0, the three
   dropped constraint names present, no name collisions.
3. Maintenance window: stop the old API (`smartsolar-api`) — no writes or workers during the DDL.
4. Apply **one** of: `psql -X -v ON_ERROR_STOP=1 -f guides/sql/migration-20261020051352-administrative-zones-dispatch-settings.up.sql`
   (do **not** add `-1`: the script has its own transaction) or `dotnet ef database update 20261020051352_…` with the migration
   role. Use the session-mode connection (port 5432) rather than the transaction pooler.
5. Optionally prefix the session with `SET lock_timeout = '5s';` so a lock wait aborts (and rolls back) instead of blocking.
6. Re-run `supabase-security-hardening-draft.sql` (idempotent; keeps the hardening-→-migration-→-hardening policy).
7. Verify: `post-migration-verification-zones-dispatch.sql` (expect every line OK, `client_role_access … rls=80/80`) and the
   supplementary checks in E.
8. Catalog import with the new image in CLI mode (no web host): dry run
   `admin-catalog import --dataset <dir> --confirm-host <host> --confirm-database <db> --confirm-user postgres.<ref>`, review the
   diff (expect 1 province, 168 communes, 493 legacy rows), then repeat with `--apply --operator <name>`.
9. Start the new API with `Dispatch__WorkerEnabled=false` (and only internal/staff access). Admin creates zones (from
   `guides/reference/operational-zones-initial-proposal.csv`; the 17 `NEEDS_REVIEW` communes only after a decision), sets
   responsible managers, assigns technicians; Admin reviews the scheduling settings (reason required).
10. Enable `Dispatch__WorkerEnabled=true` (restart of the new API), then open customer traffic; ask the 34 customers to confirm
    their site location.

## G. Rollback and recovery limitations

* **Before step 9** (no new-API writes): `rollback.sql` (or `dotnet ef database update 20261016161547_…`) restores the schema
  exactly (rehearsed: identical fingerprint) and loses nothing except the imported catalog; then restart the old image.
* **After customers/staff used the new API:** rollback **drops** the 16 tables (catalog, zones, memberships, dispatch requests,
  proposals, events, notifications, cross-zone authorizations, settings and their history) and the 7 columns (confirmed
  `commune_code` of sites, appointment zones and settings snapshots). Rows written into retained tables stay but lose meaning:
  appointments booked by the new API keep `operational_zone_id`-less rows, `province_code = '79'` on sites stays, appointments
  cancelled with `RELEASED_TO_DISPATCH` stay cancelled, survey requests whose dispatch request is dropped remain plain `Pending`.
* **Guard:** as soon as one appointment has `assignment_source = 'MANAGER_DISPATCH'` the rollback aborts (nothing changed) —
  recovery is then a restore from the step-1 backup (losing everything since) or a roll-forward fix.
* The rollback re-creates the two legacy `property_site` checks and the 3-value assignment check; they validate existing rows
  (passes unless the guard case above). The old API image must be redeployed after a rollback; the new API must not run on the
  rolled-back schema.

## H. Blockers before execution

No technical blocker was found in the migration or the generated SQL. Required before execution (procedural):

1. **Explicit approval** to migrate Supabase Dev (not given).
2. **Fresh Dev backup** (step 1) — the only local dump predates `20261016161547` and `20261013182132`.
3. **Source provenance:** `20261016161547_*` (already applied to Dev) and `20261020051352_*` and the snapshot are uncommitted;
   commit/tag them (and build the image from that revision) so the repository matches the database history.
4. **Planned replacement of the running old API** (stop during DDL; do not run old and new API side by side).

## I. Conclusion

**READY for deployment review** — not approved for execution. Evidence: the forward SQL is purely additive apart from three
CHECK replacements, never modifies rows, carries one migration in one transaction, applies to a schema proven identical to
Dev, was rehearsed with the exact files (up / rollback / up, data preserved, guard verified), yields 80 tables with RLS 80/80
and no client privileges, and is compatible with the running old API; the procedural items in H must be completed first.
