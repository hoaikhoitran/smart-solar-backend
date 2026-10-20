# Supabase Dev deployment readiness — business-rule redesign

> **Next migration (2026-10-20, not applied):** `20261020051352_AdministrativeLocationsZonesDispatchAndSettings`
> (16 new tables with RLS, non-destructive) follows the same hardening → migration → hardening order. Its own steps
> (catalog import CLI, zone setup, technician zones, worker enablement) and verification script are in
> [database-migration-administrative-zones-dispatch.md](database-migration-administrative-zones-dispatch.md).
> After it, every `service-areas` reference below reads `operational-zones` (e.g. step 11: `GET /api/admin/operational-zones`).

Status (2026-10-19): **plan only — nothing has been executed on Supabase.** All Supabase facts below come from
read-only sessions (`BEGIN READ ONLY`, `default_transaction_read_only = on`). Local rehearsals used disposable Docker
PostgreSQL databases that were dropped afterwards. Related: [supabase-security.md](supabase-security.md) (findings,
scripts), [database-migration-business-rules-redesign.md](database-migration-business-rules-redesign.md) (migration report),
[integration-smoke-test-plan.md](integration-smoke-test-plan.md) (real-service tests).

Legend used in every status column: **CODE** implemented · **LOCAL** verified on local/disposable infrastructure ·
**DEV** verified on Supabase Dev · **NOT VERIFIED**.

## 1. Facts this plan relies on

| Fact | Evidence | Status |
|---|---|---|
| The API talks to PostgreSQL directly (Npgsql/EF Core) through the Supavisor pooler, session mode :5432, as role `postgres`; no Supabase SDK / Data API in the code | `DependencyInjection.cs:161` (`ConnectionStrings:DefaultConnection`), csproj (Npgsql only), code search for supabase/postgrest/`/rest/v1`: none; `.env` username `postgres.<project-ref>` | CODE + DEV |
| The RAG service also connects as `postgres` (psycopg) | `.env` `RAG_DATABASE_URL` user `postgres.<project-ref>` | DEV (config) |
| `postgres` owns every public table, is not superuser, has BYPASSRLS | `supabase-security-audit.sql` on Dev | DEV |
| PostgREST (Data API) is **running** | `pg_stat_activity`: 2 sessions `authenticator` / `PostgREST 14.5` | DEV |
| Which schemas PostgREST exposes | `pgrst.db_schemas` not set on `authenticator` → project setting (Dashboard → Settings → API) | **NOT VERIFIED** |
| No other writers inside the DB | 0 user triggers on public tables, no `cron` schema, `supabase_realtime` publication contains 0 public tables, 0 storage buckets | DEV |
| Data the migration will transform (dry-run projection, SELECT only) | appointments 2 ACKNOWLEDGED → ASSIGNED, 1 CANCELLED; survey requests → 9 Pending (8 keep their Sales owner) + 2 Scheduled; 11 selected simulations → 11 summaries; 6 of 7 product images → gallery; installation/knowledge tables empty | DEV (read-only) |
| One product image will disappear from the API | its `image_url` is a 5-character non-URL value (stays in the retained column) | DEV — needs an owner decision (re-add the image after migration) |
| No service areas exist and 34/34 sites have no administrative codes | read-only counts | DEV — booking is impossible until areas are created |
| Feature flags are already `true` in `.env` (`Features__InstallationWorkflowEnabled`, `Features__KnowledgeBaseEnabled`) | `.env` key check (values of flags only) | DEV (config) |
| **Pooler session port 5432 refuses connections** from the deployment machine since 2026-10-19 (TCP refused; port 6543 transaction mode open; status.supabase.com reachable). The API, RAG service and `dotnet ef` are configured for 5432 | `nc` + psql attempts (one read-only check then ran via 6543: all pre-migration checks OK) | DEV — **BLOCKER**: check Dashboard → Database → Network bans / pooler settings / project status. Do not run EF migrations through 6543 (transaction mode) without a separate test; prefer fixing 5432 or the direct connection |
| Pre-migration checks (`guides/sql/pre-migration-checks.sql`) on Dev | history 12 / last 20261013182132, new tables absent, btree_gist public 1.7, `postgres` owns all tables, unmappable installation rows 0, no other client sessions | DEV (read-only) — OK |
| The backup used for rehearsals (2026-10-13) differs from today's Dev data | e.g. survey requests 7→8 Assigned, one non-https image | DEV — **take a fresh backup and rehearse on it** before applying |

## 1a. Connection diagnostics (2026-10-19)

| Path | Result from the deployment machine | Layer / evidence |
|---|---|---|
| Shared pooler, session mode `aws-0-…pooler.supabase.com:5432` | **fails**: TIMEOUT / `ENETUNREACH` on every pooler IP (earlier the same day: RST) | TCP, before TLS/auth. The same happens for pooler hosts of **other regions** and for the neutral test host `portquiz.net:5432`, while `portquiz.net:5433/6543/443/22` are open ⇒ **outbound TCP 5432 is blocked by the local network path** (en0 → local gateway, no VPN, no proxy). Not a Supabase IP ban: no credentials were involved and unrelated hosts are blocked too |
| Shared pooler, transaction mode `…:6543` | **works** (TCP open; read-only SQL sessions succeeded) | — |
| Direct connection `db.<ref>.supabase.co:5432` | **unusable here**: AAAA (IPv6) only, the machine has no IPv6 route; port 5432 is blocked anyway | DNS + routing |
| Credentials / auth | not involved in the failure (never reached); no repeated password attempts were made | — |
| Network bans (Supabase) | NOT VERIFIED (needs Dashboard → Database → Network bans, or `supabase network-bans get`); the evidence above does not point to a ban | — |

Configuration read (no secrets): API/EF/workers/`dotnet ef` use Npgsql with `Host=<pooler>; Port=5432; Username=postgres.<ref>;
SSL Mode=Require; Trust Server Certificate=true` (certificate **not validated** — MITM risk; prefer `SSL Mode=VerifyFull` with the
Supabase CA) and default Npgsql pooling/timeouts; RAG uses psycopg `…:5432/postgres?sslmode=require`, pool 1–10.

**Transaction pooler (6543) compatibility — rehearsed through PgBouncer 1.26 in transaction mode (prepared statements off,
no reset query), an approximation of Supavisor:**

| Component | Result |
|---|---|
| .NET PostgreSQL suites (48 tests: migrations from scratch, first-accept-wins races, `FOR UPDATE`, exclusion constraints, catalog) | **48/48 pass** |
| `dotnet ef database update` Up → Down → Up | works (13 migrations, 0 invalid constraints) |
| Code review for session state | only `pg_advisory_xact_lock`, `SET LOCAL`; no temp tables, LISTEN/NOTIFY, explicit prepares; Npgsql auto-prepare off |
| API (this branch) on the hardened DB through the pooler | products/service-areas 200, register 201, login 401 (unverified e-mail, expected), protected 401, 0 server errors, escalation worker running |
| **RAG (psycopg) with default `prepare_threshold`** | **fails** under concurrency: `DuplicatePreparedStatement "_pg3_0"` (8/12 clients; pgvector suite 4/6) |
| RAG with `prepare_threshold=None` (patch on a scratch copy, not applied) | pgvector suite 6/6 through the pooler; full suite 98 passed / 1 skipped; ruff clean |

Safest fixes, in order: (1) run the deployment from a network where outbound 5432 works (or fix the local network) and keep
session mode; (2) if 6543 must be used: approve the one-line RAG change
`kwargs={"autocommit": False, "prepare_threshold": None}` in `app/vectorstore.py`, point `dotnet ef`/API/RAG at 6543 only after
a read-only connectivity check, and keep Npgsql auto-prepare off. Retry condition: `nc -z <pooler> 5432` succeeds from the
deployment machine (or the 6543 route is approved).

## 2. The unsafe-permission window

Supabase's default privileges grant `anon`/`authenticated` ALL on every table `postgres` creates. If the migration runs
**before** the hardening, its 8 new tables are exposed from its commit until the hardening runs (and the 56 existing ones
stay exposed the whole time). Rehearsed locally (Supabase-like roles + default ACL, owner **without** BYPASSRLS):

| Order | Result |
|---|---|
| migration → hardening (old plan) | 8 new tables granted to anon until the hardening ⇒ **window > 0** |
| **hardening → migration → hardening** (this plan) | after the first hardening: anon/authenticated 0/55 tables, RLS 55/55; the migration's 8 new tables are created **without** any anon grant (window = 0), RLS off on them until the second run; second run: RLS 63/63; owner row counts unchanged on all 55 pre-existing tables; owner DML works; the migration (incl. data mapping) succeeds with RLS on; the API (this branch) on the hardened DB: `GET /api/products` 200, `GET /api/service-areas` 200, `POST /api/auth/register` 201 (row written), protected routes 401, 0 server errors |

`supabase_admin`'s default privileges cannot be changed by `postgres`; they only apply to objects `supabase_admin` creates —
SmartSolar's migrations create objects as `postgres`.

## 3. Deployment order (each numbered step needs explicit approval; nothing here is automated)

| # | Step | How | Checks (must pass to continue) | Rollback / recovery |
|---|---|---|---|---|
| 1 | Confirm Data API exposure | Dashboard → Settings → API → "Exposed schemas"; Dashboard → API keys (who holds the anon key) | record whether `public` is exposed | — |
| 2 | Close the dangerous path now (independent of the migration) | if `public` is exposed and nothing uses the Data API: remove `public` from Exposed schemas (or disable the Data API) | `curl -s https://<ref>.supabase.co/rest/v1/user_account?select=id&limit=1 -H "apikey: <anon>"` must fail (only the owner runs this check, it reads one id) | re-add `public` to Exposed schemas |
| 3 | Fresh backup | `pg_dump -Fc --no-owner` of the database + `shasum -a 256`; restore test into a disposable local DB (procedure in supabase-security.md §7) | dump restores; row counts match | — (nothing changed yet) |
| 4 | Stop every writer | API containers/instances (incl. every developer/FE `docker-compose.pull.yml` stack pointing at Dev), RAG service, background workers (escalation, payment reconciliation, ingestion run inside the API); PayOS retries webhooks, nothing is lost | `pg_stat_activity`: no sessions from the API (`application_name`) besides your own | start them again |
| 5 | Pre-migration checks | `guides/sql/pre-migration-checks.sql` (read-only; dry run on Dev 2026-10-19: all OK) | history = 12, last `20261013182132_…`; new tables absent; unmappable installation rows 0; BOOKED/flags noted; image exception acknowledged | — |
| 6 | Hardening, first pass | `guides/sql/supabase-security-hardening-draft.sql` as `postgres` | its final SELECT: `anon_tables=0, authenticated_tables=0, rls_everywhere=t` | supabase-security.md §8 (re-grant + disable RLS) |
| 7 | Apply the migration | `dotnet ef database update` from the reviewed build, or `guides/sql/business-rules-redesign-migration-up.sql` (SHA-256 in the migration report) | command exits 0 | **Down** (`dotnet ef database update 20261013182132_InstallationWorkflowAndKnowledgeBase`) is possible right after the migration (no new-model data yet); otherwise restore the backup from step 3 |
| 8 | Hardening, second pass | same script (idempotent) | `anon_tables=0`, `rls_everywhere=t` (63 tables) | as step 6 |
| 9 | Schema & data verification | `guides/sql/post-migration-verification.sql` (read-only; on the local Supabase-like rehearsal: every line OK) | every check line reports `OK` | Down or restore (step 7) |
| 10 | Backend connectivity | start ONE API instance of this branch (flags as intended), workers off; then the RAG service if used | API starts; `GET /api/products` 200; no `fail:` log lines | stop it; Down or restore |
| 11 | Critical endpoints (read-only first) | customer `GET /api/customers/me/survey-requests`, technician `GET /api/technicians/me/open-appointments`, Sales `GET /api/survey-requests/escalated`, admin `GET /api/admin/service-areas`, manager `GET /api/installation-projects`, admin `GET /api/admin/rag-configuration` with real Dev test accounts | 200 / expected 403/404 by role; no 5xx | as step 10 |
| 12 | Resume | enable workers, start remaining instances, re-enable Exposed schemas only if a real need was confirmed (with RLS policies first); create service areas + technician memberships; ask customers to set site codes; fix the one product image | smoke plan for external services (separate approval) | — |

Version skew to avoid: the **old** API image must not run after step 7 (it writes BOOKED appointments and assumes a NOT NULL
holder; `ck_survey_request_status` now rejects its `Assigned` status), and **this** branch must not run before step 7 (new
columns are missing). With the flags already `true` in `.env`, any instance started from this branch before the migration
fails on the new columns.

## 4. Go / No-Go gates

| Gate | Criterion | CODE | LOCAL | DEV | Overall |
|---|---|---|---|---|---|
| G1 Migration safe | non-destructive, data mapping correct, Down/restore possible | ✓ | ✓ (fresh DB + Dev-backup rehearsal Up→Down(identical)→Up; L1 edge seeded) | dry-run projection only | **PARTIAL** — rehearse on a fresh backup (step 3) |
| G2 Hardening verified | anon/authenticated 0 tables, RLS everywhere, backend unaffected | ✓ | ✓ (Supabase-like emulation, owner NOBYPASSRLS, API smoke) | not executed | **PARTIAL** |
| G3 Backend connects | API + RAG reach the DB after both | ✓ | ✓ (API on hardened DB, also through a transaction pooler) | 5432 blocked from this network; 6543 reachable read-only | **FAIL / BLOCKER** until a connection method is chosen (and the RAG patch approved for 6543) |
| G4 No serious authorization bug | role/ownership/IDOR reviews; tests | ✓ | ✓ (1238 tests, 2 independent reviews, audit) | — | **PASS (local)** |
| G5 No data loss | row counts and mapping as projected | ✓ | ✓ | projection only | **PARTIAL** (1 product image needs re-adding) |
| G6 Core business flows work | Flow 1, installation, simulation, products, RAG config | ✓ | ✓ (unit/integration/PG race tests) | not executed | **NOT VERIFIED on Dev** |
| G7 Critical integration tests | S3, OpenAI, PayOS | partially (fakes) | fakes only | not executed | **NOT VERIFIED** |
| G8 Recovery plan | backup + restore + Down + hardening rollback documented and rehearsed | ✓ | ✓ (restore procedure used in every rehearsal) | backup not yet taken | **PARTIAL** |
| G9 Logs and evidence | test outputs, rehearsal outputs, audit outputs kept | ✓ | ✓ | read-only audit output | **PASS** |

**Go** requires G1–G3, G5 and G8 to be DEV-verified during the window (steps 3–11) and G4/G9 PASS; G6/G7 can follow in
the separately approved smoke-test phase, with the feature flags left `false` until they pass.
