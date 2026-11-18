# Migration verification (2026-11-18)

Two migrations were created in this remediation, both **additive** and applied **only to disposable local PostgreSQL databases**. Neither was applied to
Supabase or any production database.

| Migration | Previous head | Purpose |
|---|---|---|
| `20261117171006_InstallationSlotOffers` | `20261117131913_CloudRagCostControl` | installation intervals offered by the technician for the customer to choose |
| `20261117185657_AfterSalesTechnicianQuotation` | `20261117171006_InstallationSlotOffers` | after-sales quotations priced and issued by the assigned technician |

## 1. `20261117171006_InstallationSlotOffers`

| Operation | Detail |
|---|---|
| CREATE TABLE `installation_slot_offer` | id, contract_id, technician_user_id, starts_at, ends_at, status (varchar 20), note (500), created_by_user_id, created_at, updated_at, selected_at, selected_schedule_id, withdrawn_at, withdrawn_reason (500), revision |
| Checks | status OPEN/SELECTED/WITHDRAWN; ends_at > starts_at; SELECTED ⇔ selected_at + selected_schedule_id; WITHDRAWN ⇔ withdrawn_at + withdrawn_reason |
| Foreign keys (RESTRICT) | `customer_contract`, `user_account` (technician, created_by), `installation_schedule_proposal` |
| Indexes | contract+status, technician+status, created_by, partial unique open interval per contract, partial unique selected schedule |
| Manual SQL | RLS enabled (no policy); `REVOKE ALL` from `anon` / `authenticated` when present (Supabase new-table policy) |
| Down | guard raises (changes nothing) while rows exist; otherwise DROP TABLE |

## 2. `20261117185657_AfterSalesTechnicianQuotation`

| Operation | Detail |
|---|---|
| ADD COLUMN `service_quotation.issued_by_role` | `varchar(20) NOT NULL DEFAULT 'SALES'` — every existing row was issued by Sales; an older API build that does not set it keeps inserting valid rows |
| ADD CHECK `ck_service_quotation_issued_by_role` | `issued_by_role IN ('SALES', 'TECHNICIAN')` |
| Unchanged on purpose | `sales_user_id` stays NOT NULL (it now holds the issuing user); making it nullable would break the older live build that maps it as non-nullable |
| Down | guard raises while TECHNICIAN quotations exist; otherwise drops the check and the column |

Payment idempotency needed **no** schema change: the existing unique indexes (one payable / one paid attempt per contract, one line per item per checkout,
one SETTLED line per package / quotation, unique cash receipt per provider) and the item reservation (revision-checked `current_payment_attempt_id`) cover
the new cash paths; this is verified by the PostgreSQL race tests below.

## 3. Generated SQL (inspected)

`TestResults/qa-2026-11-18/migration/InstallationSlotOffers.up.sql|down.sql` and `AfterSalesTechnicianQuotation.up.sql|down.sql` (git-ignored).
Up scripts contain no DROP, ALTER COLUMN of existing columns, UPDATE or DELETE of existing data; Down scripts start with their row guards.

## 4. Where applied

| Environment | Applied | Evidence |
|---|---|---|
| Disposable PostgreSQL 17 + pgvector (127.0.0.1:55433, random password, container removed afterwards) | **Yes** — each `[PostgresFact]` creates a fresh database and applies every migration | `PostgresInstallationSlotOfferTests`, `PostgresAfterSalesRemediationTests` (check violation, default `SALES`, Down refused with technician rows, Down/Up round trip), and the migration-chain tests `PostgresM1M4MigrationTests`, `PostgresIotMonitoringTests`, `PostgresCustomerToInstallationTests`, `PostgresAiCostControlTests` (expected lists now include both migrations) — results in TEST_EXECUTION_REPORT.md |
| Supabase (`.env`, pooler `aws-0-ap-northeast-2`) | **No** | used by a live API on an older image; treated as production. Needs explicit approval, backup and rehearsal |
| VPS / production | **No** | – |

## 5. Rolling-deploy compatibility

Both migrations are safe for the older API currently serving Supabase: a new table it never touches, and a defaulted column it never sets.

## 6. Operator checklist (when approved)

1. `pg_dump -Fc` backup + `.sha256`; restore into `pgvector/pgvector:pg17` (extensions in schema `extensions`); compare table and row counts.
2. Rehearse `dotnet ef database update 20261117185657_AfterSalesTechnicianQuotation` on the restored copy; check `relrowsecurity` on
   `installation_slot_offer`, no `anon` / `authenticated` grants, `issued_by_role` = SALES on every existing row.
3. Apply to Supabase; verify `__EFMigrationsHistory` and that the deployed older API still serves requests.
4. Rollback: Down refuses while offers / technician quotations exist (export first); code rollback without schema rollback is safe for both.

## 7. Final acceptance pass (2026-11-18, pass 3)

No new migration in pass 3. Head stays `20261117185657_AfterSalesTechnicianQuotation`.

| Check | Command / evidence | Result |
|---|---|---|
| Model ↔ snapshot | `dotnet ef migrations has-pending-model-changes` (startup = Api, local DB connection string) | "No changes have been made to the model since the last migration." |
| Supabase readiness preflight | `TestResults/qa-2026-11-18/migration/supabase-readiness.preflight.sql`: read-only (`SET TRANSACTION READ ONLY` … `ROLLBACK`); checks the current head, that neither migration is applied, no object exists already, referenced tables exist, server version, row estimate, Supabase roles | Validated on a local database migrated to the previous head `20261117131913_CloudRagCostControl`: all expected values (applied = f/f, later = 0, pre-existing objects = f/f/f, referenced tables = t). **Not run on Supabase.** |
| Up scripts idempotent | both `.up.sql` applied twice in order on that local database | no error; second run is a no-op (history guards) |
| Down scripts | both `.down.sql` in reverse order (no rows) | head back to `20261117131913_CloudRagCostControl`; local database dropped afterwards |

### Compatibility with the older live API (Supabase)

| Change | Older API behavior | Compatible |
|---|---|---|
| `installation_slot_offer` table | never reads or writes it | Yes |
| `service_quotation.issued_by_role` (NOT NULL DEFAULT 'SALES', CHECK) | inserts without the column get `SALES`; reads ignore it | Yes |
| One-time maintenance now starts in `SUBMITTED` (inspection) instead of `AWAITING_QUOTATION` (code only, no schema) | status value already exists. An older API build that lacks the `(ONE_TIME_MAINTENANCE, SUBMITTED) → INSPECTION` visit purpose cannot plan the inspection visit for such a request during the overlap. Which build serves Supabase was not verified | Data-compatible; **operational caveat** during a mixed-version window: plan inspection visits from the new build |
| Technician-issued quotations (`TECHNICIAN` rows) | read as ordinary quotations; accept / pay paths unchanged | Yes |

Production readiness for the database therefore needs only: the authorized operator runs the preflight, takes the backup, rehearses, applies (section 6).
