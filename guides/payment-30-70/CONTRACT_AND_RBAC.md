# Contracts, paper signature and access control (2026-11-19)

## 1. Read access
| Data | Customer | Sales | Technician | Manager | Admin |
|---|---|---|---|---|---|
| Contract list (`GET /api/contracts`, paged, filters, search) | — | all | all | all | all |
| Contract detail (`GET /api/contracts/{id}`) incl. installments, signature, events, collection summary | — | all | all | all | all |
| Own contracts (`GET /api/customers/me/contracts[/{id}]`) — every site / project / version, 30/70 DRAFT included | own | — | — | — | — |
| Signature sets / page URLs | own | all | all | all | all |
| Installment payment state | own | all | all (assigned: also write) | all | all |
| Official PDF / legacy scan URLs (legacy contracts) | own | all | all | all | all |
| Collection cases | — | all | — | all | all |
| Finance overview | — | — | — | — | yes |

* "all" = read-all since 2026-11-19 (previously Sales saw only the requests it owned and Technicians saw no contract).
* Another customer's contract, page or installment answers **404** (no disclosure). Unknown ids also 404.
* Every staff detail read, list read and page-URL issue is recorded in `contract_access_audit` (actor, role, action, contract, short detail,
  time; never updated or deleted). The audit write never blocks the read (a failure is logged).

## 2. Write access (unchanged scope)
| Action | Who |
|---|---|
| On-site decision (creates the 30/70 contract) | surveying technician |
| Open / cancel installment payment | surveying technician (on site), the owning customer (app) |
| Confirm cash | Manager / Admin (never the technician or Sales) |
| Signed pages: start / upload / finalize | surveying technician; Manager / Admin as support (`/api/contracts/{id}/signature-set…`) |
| Accept / request rework | the project's customer |
| Return to rework | Manager / Admin |
| Collection activity / escalate | Sales, Manager, Admin |
| Collection legal review / resolve | Manager, Admin |
| Legacy contract steps (terms, issue, official PDF, publish, scan review, amendments, discard) | Manager / Admin, **legacy contracts only** (30/70 → 409 `CONTRACT_PAYMENT_PLAN_NOT_APPLICABLE`) |

## 3. Paper contract storage
* Each photographed page is a `file_reference` with purpose `SIGNED_CONTRACT_PAGE` (prefix `contracts/signed-pages/{uploader}/…`), verified
  against S3-compatible storage (size, content type, SHA-256, pinned object version) before it can be linked.
* `contract_signature_set` (version, expected page count, status UPLOADING / FINALIZED / SUPERSEDED, signed-on date, note, created / finalized
  by and at, revision) and `contract_signed_page` (set, page number, file, SHA-256, content type, uploader, time, replaced_at).
* One current set per contract and one current file per page (partial unique indexes); a file is never used twice; replaced pages and restarted
  sets stay as history. Finalizing re-verifies every stored object (a changed or missing object blocks it).
* Files are never public: short-lived presigned GET URLs (`Storage:S3:PresignedUrlTtlSeconds`, `Cache-Control: no-store`) after the RBAC check.
* Legacy PDFs (official document, signed scans) stay readable with their existing endpoints.

## 4. Retired / changed endpoints
| Endpoint | Now |
|---|---|
| `POST /api/technician/quotations/{id}/payment` (100% before contract) | 410 `ENDPOINT_RETIRED` (GET / cancel kept for in-flight legacy payments) |
| `POST /api/customers/me/contracts/{id}/signed-document` (customer scan upload) | 410 |
| `POST /api/installation-projects/{id}/acceptance` (Manager internal acceptance) | 410 |
| `POST /api/customers/me/installation-projects/{id}/acceptance` | **restored**: ACCEPT / REQUEST_REWORK / REJECT |
| Staff contract reads | Technician added; no request ownership filter |
| Legacy staff scan upload + review (`POST /api/contracts/{id}/signed-document`, `…/review`) | kept for legacy contracts in flight only (409 for 30/70) |

## 5. Contract content
At the agreement the contract freezes the quotation snapshot (lines, totals, customer data), the default installation warranty, and generated
payment / acceptance / ownership-retention clauses (`PaymentPlanCalculator.Clauses`, version label `terms_version`, default
`PAYMENT_30_70_DRAFT_PENDING_LEGAL_REVIEW`). The canonical hash covers that content. The clauses are business wording pending legal approval
(see LEGAL_REVIEW_CHECKLIST.md); the system makes no legal decision.
