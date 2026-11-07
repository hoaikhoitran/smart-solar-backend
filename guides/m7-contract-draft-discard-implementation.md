# SmartSolar V2 — M-7 Contract draft discard (2026-10-31)

Audience: backend reviewers, the database owner, QA and the release owner.

Scope: the local working tree of `feat/fe-backend-gap-fixes` (HEAD `2810286` plus uncommitted changes). The Product Owner
approved M-7. Nothing was committed, pushed or deployed. The migration was applied only to disposable PostgreSQL test databases.

## 1. Business rules (approved) and how they are met

| # | Rule | Implementation |
|---|---|---|
| 1 | Only DRAFT contracts | `DiscardAsync` refuses any other status (`409 CONTRACT_NOT_DISCARDABLE`). |
| 2 | Only the authorised Sales owner | Sales role on the endpoint; the request's `assigned_sale_id` must be the caller, otherwise `404`. Manager and Admin are **not** given this action. |
| 3–4 | Mandatory reason; actor, reason, UTC time | `400 CONTRACT_DISCARD_REASON_REQUIRED` (1–1000 characters). Stored in `discarded_at`, `discarded_by_user_id` (FK), `discard_reason`, plus a `STATUS_CHANGED` contract event (DRAFT → DISCARDED) with the reason. |
| 5–7 | Row kept, never deleted, not disguised as SUPERSEDED | New terminal status `DISCARDED`. The row keeps its number, terms, quotation link and history. |
| 8–9 | No change to issued, signed, paid or completed records; no payment or refund touched | Only the DRAFT row changes. Payments need SIGNED, projects need PAID. |
| 10–12 | Closure possible after the discard; closing never discards; two explicit operations | A DISCARDED contract no longer counts as live for closure. Closure still refuses a live DRAFT (`SURVEY_REQUEST_CONTRACT_EXISTS`, `details.contractStatus = "DRAFT"`). |
| 13 | History of request ↔ quotation ↔ contract kept | `quotation_id` and `survey_request_id` unchanged; the quotation is CANCELLED only by the later closure. |
| 14 | Never active again | Issue, terms update, amendment, signing, payment and project creation all refuse a DISCARDED contract. The database forbids issue, sign and pay data on DISCARDED rows. |
| 15 | Repeated or concurrent discards: no duplicate audit, no inconsistency | A repeat on a DISCARDED contract returns it unchanged with no second event. Concurrency handling is in §5. |

**Scope decisions (documented, not invented):**

* **Only the initial draft** (no `previous_contract_id`) can be discarded.
  * An amendment draft always sits beside a live predecessor, which still blocks the closure.
  * Discarding it would also break the one-live-contract index.
  * Result: `409 CONTRACT_NOT_DISCARDABLE`. Needs a decision if amendment drafts should be discardable.
* **Re-contracting after a discard** is refused (`409 CONTRACT_DRAFT_DISCARDED`). The DISCARDED row also stays inside
  `ux_customer_contract_one_live`. Whether a new contract may be made for the same accepted quotation is a **Product Owner
  decision**; the intended path is closure.

## 2. Entity and migration

**Migration:** `20261031043834_ContractDraftDiscard` (7th pending migration, after `20261030201824_ManufacturerWarrantyCoverage`).

**`CustomerContract`** (`customer_contract`) gains:
* `DiscardedAt` (`discarded_at timestamptz`);
* `DiscardedByUserId` (`discarded_by_user_id uuid`, FK `fk_customer_contract_discarded_by` → `user_account`, RESTRICT, indexed);
* `DiscardReason` (`discard_reason varchar(1000)`).

**`ContractStatuses`** gains `Discarded = "DISCARDED"` and `IsLive(status)` (not SUPERSEDED or DISCARDED).

| Constraint | Definition |
|---|---|
| `ck_customer_contract_status` | extended with `DISCARDED` |
| `ck_customer_contract_discarded` (new) | `(status = 'DISCARDED') = (discarded_at, discarded_by_user_id, discard_reason all NOT NULL)`: discard data exactly on discarded rows |
| `ck_customer_contract_discarded_never_issued` (new) | DISCARDED ⇒ `issued_at`, `signed_at`, `paid_at`, `previous_contract_id` all NULL |
| `ck_customer_contract_issued_hash` | now `status IN ('DRAFT','DISCARDED') OR (hash and issued_at present)`. Without this, a never-issued draft could not be DISCARDED; this is the same class of defect PostgreSQL found in M-1. |
| `ck_customer_contract_published_document` | DISCARDED added to the statuses without an official document |

**Indexes:** `ux_customer_contract_one_live` and `ux_customer_contract_one_draft` are unchanged. DISCARDED counts as "live" for
the uniqueness index on purpose (see §1). One new FK index was added.

**Historical compatibility:**
* No backfill. Existing rows keep every value, and no row becomes DISCARDED.
* Verified on PostgreSQL with DRAFT, ISSUED, SIGNED and PAID contracts (§6).

**Rollback:**
* `Down` restores the old checks. It **fails while any DISCARDED row exists** (verified): such rows must be remapped first,
  and their discard data would be lost.
* Code rollback before applying the migration is safe.

## 3. Status transitions

```text
DRAFT ──(Sales owner, reason)──► DISCARDED   (terminal: no issue, sign, pay, amend, re-contract)
DRAFT ──► ISSUED ──► AWAITING_SIGNATURE ──► … ──► PAID   (unchanged; any of these refuses discard)
```

Survey request: `DRAFT contract → discard → close (explicit, separate) → Closed`. Binding contracts keep blocking the
closure.

## 4. API

| Role | Method | Path | Body | Responses |
|---|---|---|---|---|
| Sales (owner) | POST | `/api/contracts/{contractId}/discard` | `{ revision, reason }` | `200 ContractView` (`status: "DISCARDED"`, staff-only `discard: { discardedAt, discardedByUserId, reason }`). The idempotent repeat is also 200. |

**Errors:**
* `401` unauthenticated.
* `403` Customer, Manager, Admin, Technician.
* `400 VALIDATION_FAILED` (missing `revision`), `400 CONTRACT_DISCARD_REASON_REQUIRED`.
* `404 CONTRACT_NOT_FOUND` (unknown, or another Sales owner's contract).
* `409 CONTRACT_NOT_DISCARDABLE` (not an initial draft), `409 CONTRACT_STALE_REVISION`.
* New on contract creation: `409 CONTRACT_DRAFT_DISCARDED`.

**Visibility:** customers never see drafts, so they don't see DISCARDED contracts either (list and detail). The discard
details appear only in staff views. The endpoint is in OpenAPI through the controller attributes, and the existing OpenAPI
test passes.

## 5. Transactions and locking

* **Discard:** inside one transaction:
  1. `SELECT … FROM survey_request … FOR UPDATE` (the same lock as closure, contract creation and quotation inserts);
  2. re-read the contract;
  3. check status, revision and initial-draft;
  4. update with the revision token; the event is in the same transaction.
* **Lock order** stays survey_request → (quotation) → contract. Issue and terms edits touch only the contract row, with its
  revision token, so there is no cycle.
* **Outcomes:**

| Race | Outcome |
|---|---|
| Discard vs issue | Exactly one wins. The loser gets STALE or NOT_DISCARDABLE / INVALID_STATE. |
| Discard vs discard | One writes; the others see DISCARDED and return it unchanged (one event). |
| Discard vs terms edit | Exactly one wins. |
| Discard vs closure, discard first | While the discard is uncommitted, the closure's pre-check still sees the live DRAFT and refuses immediately, so it never closes past a contract. After the commit, the explicit closure succeeds. |
| Closure while the draft is live | Refused before taking the lock, so it never holds up the discard. |

## 6. Tests

* **SQLite** (`Unit/InstallationWorkflow/ContractDraftDiscardTests.cs`, 4):
  * the E2E accepted quotation → DRAFT → discard → close, with refusal-first, validation, ownership, stale, idempotency,
    queue exclusion and history;
  * a DISCARDED contract cannot be issued, edited, amended, re-contracted, signed, paid or turned into a project, and is
    invisible to the customer;
  * issued, paid and amendment contracts cannot be discarded, and the closure stays blocked;
  * retry after a stale failure, and the database constraints.
* **HTTP** (`ApprovedPolicyEndpointsTests`, +1): 401; 403 for Customer, Manager, Admin, Technician; 400 (revision, reason); 404.
* **PostgreSQL** (`PostgresSalesClosureConcurrencyTests`, +4; `PostgresM1M4MigrationTests`, +1;
  `PostgresPendingSchemaConstraintTests`, scenario extended with a discarded draft and closure): results in
  [final-postgresql-concurrency-verification.md](final-postgresql-concurrency-verification.md) §8.

## 7. Remaining release blockers (unchanged by M-7)

* Applying all seven migrations on a staging copy.
* Real PayOS, S3, PVGIS, SMTP and RAG.
* NFR measurements.
* The manufacturer-claim payer decision.
* **New decisions** (not blockers):
  * re-contracting after a discard;
  * discarding amendment drafts.
