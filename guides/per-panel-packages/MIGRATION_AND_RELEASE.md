# Per-panel packages — migration and release

**Nothing was applied to Supabase / production, deployed, pushed or committed.** The migration was applied only to disposable local PostgreSQL 17
(pgvector image) databases created and dropped by the test suite.

## 1. Migration

`20261124173442_PerPanelServicePackages` (EF Core 8, `src/SmartSolar.Infrastructure/Persistence/Migrations/`). Additive.

| Object | Change | Default / existing rows |
| --- | --- | --- |
| `service_package.pricing_unit` | new varchar(30) NOT NULL + check `PER_PANEL`/`PER_INSTALLATION_LEGACY` | `PER_INSTALLATION_LEGACY` (historical meaning kept) |
| `quotation.panel_quantity` | int NULL | null |
| `quotation.service_packages_total` | numeric(18,2) NOT NULL | 0 (no packages; `total_amount` untouched) |
| `customer_contract.installation_total`, `service_packages_total` | numeric NULL + check (both null, or both > 0 and sum = `total_amount`) | null (old totals untouched) |
| `service_subscription.coverage_scope` | varchar(20) NOT NULL + check | `INSTALLATION` (every purchased term keeps its meaning) |
| `service_subscription.unit_price`, `covered_units`, `contract_service_package_id` | NULL + check (PANEL ⇒ price = unit × units) + unique filtered index + FK | null |
| `disaster_claim.coverage_allocation_id` | uuid NULL + FK | null |
| `quotation_service_package` | new table (unique quotation × kind; amount = unit × qty; PER_PANEL only) | — |
| `contract_service_package` | new table (unique contract × kind; deposit + balance = amount; PROVISIONED ⇔ subscription) | — |
| `service_coverage_allocation` | new table (unit_count > 0; unique subscription × asset) | — |
| `ex_service_subscription_no_overlap` | re-created with `AND coverage_scope = 'INSTALLATION'` | identical behaviour for every existing row |
| RLS | `ENABLE ROW LEVEL SECURITY` on the three new tables (same as other tables) | — |

No backfill guesses a price or a panel count. `__EFMigrationsHistory` is used normally.

**Down:** refused (`RAISE EXCEPTION … per-panel package data exists`) while any per-panel row exists (package snapshot, commitment, allocation,
PANEL-scope term, PER_PANEL catalog row, or contract breakdown); otherwise drops the new objects and restores the previous exclusion. Rolling back after
real sales therefore means restoring a backup — take one before applying.

## 2. Rehearsal evidence (disposable PostgreSQL 17.12, pgvector/pgvector:pg17)

- `PostgresPerPanelPackageTests.MIGRATION_up_down_up_keeps_existing_rows_with_their_historical_meaning`: previous schema + a legacy package row → Up
  (row = `PER_INSTALLATION_LEGACY`, RLS on, exclusion narrowed, defaults) → Down (tables gone, original exclusion back, row kept) → Up.
- `…The_bundled_flow_runs_on_postgres_and_constraints_hold_on_real_data_and_down_is_refused`: full flow on PostgreSQL; unique / check constraints
  reject a second provisioning and broken money invariants; Down refused with data.
- Every migration-list test (`PostgresM1M4MigrationTests`, `PostgresIotMonitoringTests`, `PostgresCustomerToInstallationTests`,
  `PostgresPaymentPlanTests`, `PostgresSharedPublicationCutoverTests`) updated for the new last migration and passing.

## 3. Backward compatibility with the older API build (rolling deploy on the shared Supabase database)

- Every new column is nullable or has a default carrying the old meaning, so the older API keeps inserting packages (legacy), quotations (0 packages),
  contracts (no breakdown) and subscriptions (INSTALLATION scope).
- The older API does not know per-panel data: it must not serve technicians/customers once per-panel packages are sold (its contract views would omit
  the breakdown, though `totalAmount` and installments are correct). Keep the window short.
- Admins must not create `PER_PANEL` packages until the new API is fully rolled out (the old Admin UI would show their price as installation-wide).

## 4. Safe release sequence (requires explicit approval for every production step)

1. Backup / snapshot of the Supabase database; rehearse the migration on a restored copy (Up, smoke test, Down guard check).
2. Apply `20261124173442_PerPanelServicePackages` (separate approval).
3. Deploy the API (no new configuration keys; `afterSales.disasterWaitingDays` unchanged).
4. Admin creates the PER_PANEL packages with approved prices (no price is shipped by the code).
5. Deploy the FE that implements FE_HANDOFF.md.
6. Watch `GET /api/manager/after-sales/bundled-packages?status=RECONCILIATION_REQUIRED` after the first handovers.

## 5. Rollback

- Before any per-panel sale: redeploy the previous API, then `Down` (succeeds; guard finds no data).
- After per-panel sales: the Down guard refuses; restore the backup or keep the schema and roll back only the API (the old API ignores the new
  columns). Money is never recomputed by a rollback.
