# Migration report — `20261118202100_PaymentPlan3070PaperSignatureCollection` (Supabase production)

**Result: APPLIED and VERIFIED** on 2026-11-18 20:50:54–20:51:02 UTC (2026-11-19 03:50–03:51 Asia/Ho_Chi_Minh), following section 12 of the
request. No credentials, host names or personal data are recorded here.

## 1. Target identification
| Check | Result |
|---|---|
| Target | the Supabase project configured in the local `.env` (`ConnectionStrings__DefaultConnection`), Supavisor pooler region ap-northeast-2, database `postgres` — the same database recorded on 2026-11-17 as used by a live older API build |
| Server | PostgreSQL 17.6, connection `sslmode=require`, application name `smartsolar-migration-3070`; inspections in `READ ONLY` sessions |
| Live activity before | only idle pooler / PostgREST sessions; 1 open payment (after-sales CASH repair quotation, not affected) |
| Data volume | 107 public tables, ≈ 5,600 rows, 27 MB; contracts DRAFT 4 / PAID 5 / SIGNED 1; 22 payment attempts |

## 2. Migration history comparison
* Production `__EFMigrationsHistory`: 32 rows, last `20261118102011_TechnicianLedOnSiteSales` (product versions 8.0.31–8.0.34).
* Repository: 33 migrations. **Every production migration exists in the repository** (no unknown migration); the only pending one was
  `20261118202100_PaymentPlan3070PaperSignatureCollection`.
* No ACCEPTED quotation without a contract existed (cutover needed for none at the time).

## 3. Backups and restore verification
| Backup | Taken (UTC) | Format | Size | SHA-256 | Restore check |
|---|---|---|---|---|---|
| #1 | 2026-11-18 20:41:21 | `pg_dump -Fc --schema=public --no-owner --no-privileges` | 1,859,819 B | `e47089e5edce0d44c28b8afb22d7eccff0da732cc54a52eb5c9a0de758d12734` | restored into a disposable local `pgvector/pgvector:pg17` (extensions `vector`, `pgcrypto`, `uuid-ossp` in `extensions`, `btree_gist` in `public`): 107 tables, 32 history rows, contracts / attempts / row totals equal to production |
| #2 (immediately before apply) | 2026-11-18 20:48:12 | same | 1,859,819 B | `2d777c1d760fabe445e938fd84a02cfd8cb0a49c72a079ba73cc8984f1c62bfa` | restored into a second disposable database: 107 tables, 32 history rows, 10 contracts |

Backups are files in the session scratchpad (`…/scratchpad/backup/`, mode 0600, never committed). They contain personal data: keep them only as long as the company's
backup policy requires, then delete them. Supabase's own platform backups are independent of these.

## 4. Rehearsal (on the restored copy of backup #1)
| Step | Result |
|---|---|
| Script | `dotnet ef migrations script 20261118102011_TechnicianLedOnSiteSales 20261118202100_PaymentPlan3070PaperSignatureCollection --idempotent` → `TestResults/qa-2026-11-19/migration/up-3070.sql` (512 lines, one transaction), **SHA-256 `251f9b1325d76b5512a1d87edb7f46093033716b56847fa05948156115362cbe`** |
| Up | exit 0 in ~1 s; exact row counts of all 107 tables: unchanged except `__EFMigrationsHistory` 32 → 33 and six new empty tables |
| Older-API compatibility (simulated, rolled back) | legacy SIGNED contract: 100% PayOS attempt + CONTRACT line + SETTLED + PAID + contract SIGNED → PAID ✔; legacy file purpose insert ✔; installation acceptance update without the new columns ✔; deferred checks ✔ |
| Constraints | 0 not-validated constraints; 3 guard triggers present |
| Down (`down-3070.sql`, SHA-256 `e3e7a5a38da49434ae20f713e6863d5c025c6800106e6b7c209ca36947bfab24`) | exit 0; all 107 tables' row counts identical to the backup |
| Up again / idempotent second run | exit 0 / exit 0 (no-op), identical row counts |

The same migration chain is also covered by the PostgreSQL 17 integration tests (`PostgresPaymentPlanTests`, migration list tests).

## 5. Production apply
* Command: `psql -v ON_ERROR_STOP=1` with `SET lock_timeout = '5s'; SET statement_timeout = '120s';` then the rehearsed `up-3070.sql`
  (same SHA-256 `251f9b13…62cbe`).
* Start 20:50:54 UTC, end 20:51:02 UTC, exit 0. History row: `20261118202100_PaymentPlan3070PaperSignatureCollection` / `8.0.34`.

## 6. Read-only verification after apply
| Check | Result |
|---|---|
| Row counts (all 107 previous tables) | identical to before, except `__EFMigrationsHistory` 32 → 33 |
| New tables | `contract_installment`, `contract_signature_set`, `contract_signed_page`, `contract_collection_case`, `contract_collection_activity`, `contract_access_audit` (all empty) |
| New columns | `customer_contract.payment_plan`, `customer_contract.terms_version`, `payment_attempt_item.contract_installment_id`, `installation_acceptance.decided_by_role`, `installation_acceptance.evidence_file_ids` (all NULL on existing rows) |
| Guards | triggers `trg_payment_attempt_payment_plan_guard`, `trg_customer_contract_payment_plan_transition`, `trg_contract_installment_settled`; checks `ck_customer_contract_payment_plan`, `ck_payment_attempt_item_contract_installment`, `ck_contract_installment_paid`, `ck_contract_collection_case_resolved`; index `ux_payment_attempt_item_settled_contract_installment` |
| Not-validated constraints | 0 |
| Business data | contracts with a payment plan: 0; statuses unchanged (DRAFT 4 / PAID 5 / SIGNED 1); open payable attempts unchanged (1) |

## 7. Data rules respected
* No existing payment, contract, file or audit row was changed or deleted; no old payment was converted to 30%; nothing was marked PAID by SQL.
* Legacy contracts (`payment_plan IS NULL`) keep the 100% rule and get no 70% receivable; in-flight legacy quotation payments keep their
  settlement; 30/70 applies only to contracts created by the new API at an on-site agreement.
* The older API cannot treat a 30% payment as 100%: it never creates 30/70 rows, and the triggers refuse a 100% contract attempt or an
  out-of-order status change on a 30/70 contract.

## 8. Compatibility window and rollback
* Until the new API is deployed (not done here: no deploy), the migration is inert: the older API keeps working exactly as before (verified).
* Rollback: `down-3070.sql` (rehearsed). It refuses (and changes nothing) once any 30/70 data exists — export first, then decide. Restore from
  backup #2 is the last resort (it predates any 30/70 data).
* Deploy order: deploy the new API image, then enable usage; no feature flag needed (30/70 contracts are only created by the new on-site
  decision endpoint).
