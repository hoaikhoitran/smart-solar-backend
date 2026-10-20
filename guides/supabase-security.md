# Supabase security & deployment readiness

Status: **findings verified read-only on Supabase Dev (2026-10-17); nothing changed remotely.** Hardening is a
reviewed draft (`guides/sql/supabase-security-hardening-draft.sql`), tested only on a disposable local PostgreSQL
that emulates Supabase's roles and default privileges. Applying it — and the redesign migration — needs approval.

## 1. Findings (read-only audit: `guides/sql/supabase-security-audit.sql`)

| # | Finding | Evidence (Supabase Dev) | Severity |
|---|---|---|---|
| S1 | Every table in `public` grants SELECT **and** INSERT/UPDATE/DELETE to `anon` and `authenticated` | 56/56 tables (incl. `user_account` password hashes, `refresh_token`, `auth_action_token`, `payment_*`, `customer`, `property_site`, `knowledge_chunk`) | **CRITICAL if the Data API exposes `public`** (Supabase default), otherwise HIGH (one misconfiguration away) |
| S2 | RLS is off almost everywhere | RLS only on `refresh_token`; 0 policies | HIGH (no second line of defence) |
| S3 | Default privileges grant every future object to `anon`/`authenticated` | `pg_default_acl` for `postgres` and `supabase_admin` on tables, sequences, functions | HIGH (new tables of every migration are exposed too — the redesign's 8 new tables would be) |
| S4 | Data API is **running** (PostgREST 14.5 sessions as `authenticator`, verified 2026-10-19); which schemas it exposes cannot be read from SQL | `pg_stat_activity`; `pgrst.db_schemas` not set on `authenticator` → project setting | Running: VERIFIED · exposed schemas: NOT VERIFIED — check Dashboard → Settings → API ("Exposed schemas") |
| S5 | `public` functions executable by `anon` | 188/188 — all `btree_gist` extension functions owned by `supabase_admin`, **0 SECURITY DEFINER** | LOW (pure operator-support functions) |

Also verified read-only: no triggers on public tables, no `cron` schema, no public table in the `supabase_realtime` publication,
no storage buckets; `service_role` keeps full access (BYPASSRLS + grants) — optional revoke if nothing server-side uses it.
Not a risk: the API and migrations connect as `postgres` (not superuser, owner of every `public` table, BYPASSRLS);
`btree_gist` 1.7 is in `public` and `gist_uuid_ops` resolves, so `ex_installation_assignment_no_overlap` can be created;
pgvector 0.8.2 is in `extensions` (on the search path).

## 1a. Effective role privileges (read-only, Supabase Dev, 2026-10-19)

| Role | Attributes | Memberships | Effective table access (56 public tables) |
|---|---|---|---|
| `postgres` (API, RAG, migrations) | LOGIN, BYPASSRLS, not superuser, INHERIT | inherits `anon`, `authenticated`, `service_role`, `authenticator`, `pg_read_all_data`, `supabase_privileged_role` | owner of all 56 → unaffected by revokes and by RLS |
| `authenticator` (PostgREST) | LOGIN, **NOINHERIT** | `anon`/`authenticated`/`service_role` with `inherit=false, set=true` | **0** directly; acts only through SET ROLE per request |
| `anon` | NOLOGIN | — | 56/56 SELECT+INSERT+UPDATE+DELETE, **all from direct grants** (no PUBLIC table grants, nothing inherited) |
| `authenticated` | NOLOGIN | — | 56/56, same |
| `service_role` | NOLOGIN, BYPASSRLS | — | 56/56 (left unchanged by the draft; optional revoke) |

Other facts: no column-level ACLs (`pg_attribute.attacl`; the 5296 rows of `information_schema.column_privileges` are derived
from table grants), no sequences, views or materialized views in `public`, schema `public` USAGE for PUBLIC/anon/authenticated/
service_role (no CREATE), default privileges in `public` for creators `postgres` and `supabase_admin` only.
Consequence for the draft: revoking the direct grants removes all client-role table access (verified locally with the same
membership layout through a transaction pooler: anon/authenticated/authenticator 0, PostgREST-like `SET ROLE anon` →
permission denied; owner 63/63, service_role 63/63).

## 2. Strategy

SmartSolar's frontend talks **only** to the .NET API; nothing should reach tables through the Data API. Therefore:

1. **Grants are the primary control**: `anon` and `authenticated` get no privilege on SmartSolar tables, sequences or
   functions, now and for future objects (S1, S3).
2. **RLS deny-by-default as defence in depth** (S2): enable RLS on every `public` table with **no policies**. The table owner
   (`postgres`, used by the API) is not subject to RLS; `service_role` has BYPASSRLS.
3. **Disable or restrict the Data API** for `public` in the Dashboard if no Supabase feature needs it (S4).
4. Keep `service_role` key server-side only (it bypasses everything). The RAG service should eventually get its own
   least-privilege role (`SELECT, INSERT, DELETE` on `knowledge_chunk` + a policy) — optional block in the draft.

## 3. Permissions the backend needs

| Component | Role | Needs |
|---|---|---|
| .NET API + EF migrations | `postgres` (owner) | owner of all `public` tables (DDL in migrations, DML at runtime); `CREATE` on `public`; `btree_gist` |
| RAG service | today: connection from `RAG_DATABASE_URL` | `SELECT, INSERT, DELETE` on `knowledge_chunk`; `USAGE` on `extensions` (pgvector) |
| Supabase clients (`anon`, `authenticated`) | — | **nothing** in `public` |

## 4. Local verification of the hardening draft

Disposable PostgreSQL 17 + emulated `anon` / `authenticated` / `service_role` with Supabase-style default privileges, a
restored copy of the Dev backup and both migrations applied:

| Check | Before | After |
|---|---|---|
| tables granted to `anon` / `authenticated` | 63 / 63 | **0 / 0** |
| tables with RLS | 1 | **63** |
| default privileges of `postgres` | anon, authenticated, service_role | service_role |
| `SET ROLE anon; SELECT … FROM user_account` | rows | `permission denied` |
| owner read/write (`SELECT user_account`, `UPDATE survey_request`) | ok | ok |
| redesign migration Down + Up after hardening | — | ok; re-created tables have **no** `anon` grant, but RLS is off on them → re-run step 3 after each migration |
| running the draft twice | — | idempotent, same result |

## 5. Deployment plan

Superseded by [deployment-readiness.md](deployment-readiness.md) §3. Note the corrected order: **hardening → migration →
hardening** (rehearsed locally). Running the migration first would expose its 8 new tables to `anon`/`authenticated`
through Supabase's default privileges until the hardening runs.

## 6. Post-migration verification checklist

* `SELECT count(*), max("MigrationId") FROM "__EFMigrationsHistory"` → 13, `20261016161547_BusinessRulesFlow1And2Redesign`.
* `SELECT status, count(*) FROM technician_appointment GROUP BY 1` → only the new vocabulary; no `PENDING_ACCEPTANCE` without holder.
* `SELECT status, count(*) FROM survey_request GROUP BY 1` → no `Assigned` / `Reviewing`.
* `SELECT count(*) FROM selected_simulation` = `SELECT count(*) FROM pre_survey WHERE selected_simulation_id IS NOT NULL`.
* `SELECT count(*) FROM pg_constraint WHERE NOT convalidated` → 0; `SELECT count(*) FROM pg_index WHERE NOT indisvalid` → 0.
* Exclusion constraints: `ex_technician_appointment_no_overlap`, `ex_installation_assignment_no_overlap` (+ retained shift one).
* Re-run `guides/sql/supabase-security-audit.sql`: `anon_select = 0`, `rls_on = public_tables`, default ACL without anon/authenticated.
* API smoke (read-only): health, products, a customer survey request, a technician open-appointments list.

## 7. Backup / restore

* Backup: `pg_dump -Fc --no-owner` of the database (or Supabase PITR/daily backup if enabled) + `shasum -a 256`.
* Restore into a fresh database: `CREATE EXTENSION btree_gist` first, then
  `pg_restore -l dump | grep -v ' SCHEMA - public ' > toc && pg_restore --no-owner --no-privileges -L toc -d <db> dump`
  (this exact procedure was used for every rehearsal in this branch).

## 8. Rollback / recovery

* **Hardening**: re-grant (`GRANT ALL ON ALL TABLES IN SCHEMA public TO anon, authenticated;` + the reverse
  `ALTER DEFAULT PRIVILEGES … GRANT`) and `ALTER TABLE … DISABLE ROW LEVEL SECURITY` — only if something legitimately used
  the Data API (nothing in SmartSolar does).
* **Migration**: `dotnet ef database update 20261013182132_InstallationWorkflowAndKnowledgeBase` — its guard refuses once
  new-model data exists (holder-less appointment, installation assignment, selection without legacy row); then restore the backup.
* **Destructive cleanup** (`guides/sql/business-rules-cleanup-draft.sql`) has no rollback other than the backup — run it last, separately.
