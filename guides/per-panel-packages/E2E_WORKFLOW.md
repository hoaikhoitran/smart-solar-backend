# Per-panel packages — end-to-end workflow

Actors: **T** technician (surveyed the site), **C** customer, **M** Manager/Admin, **A** Admin, **P** PayOS. Every state below is set by the backend;
no HTTP success, redirect, QR display or technician action is treated as a verified payment.

## 1. Bundled sale → contract → 30/70 → activation

| # | Step | Endpoint | Result / state |
| --- | --- | --- | --- |
| 0 | A creates per-panel packages | `POST /api/admin/after-sales/packages` (`pricingUnit: "PER_PANEL"`) | catalog row, revision 0 |
| 1 | T builds the cart (panels, inverter, labour…) | `POST /api/technician/survey-requests/{id}/quotation`, `POST …/quotations/{id}/lines` | DRAFT; `panelQuantity` |
| 2 | T reads the sellable packages | `GET /api/technician/service-packages?kind=` | active PER_PANEL only |
| 3 | T selects / replaces / removes a package | `PUT|DELETE /api/technician/quotations/{id}/service-packages/{kind}` | quantity = panels; totals + revision |
| 4 | T issues | `POST /api/technician/quotations/{id}/issue` | ISSUED; lines + packages frozen; `grandTotal` |
| 5 | C agrees on site; T records it | `POST /api/technician/quotations/{id}/on-site-decision` `AGREED` | ACCEPTED; contract DRAFT, `totalAmount = grandTotal`; commitments COMMITTED; installment 1 DUE (30 % of all), 2 PENDING |
| 6 | Deposit: QR (P webhook/reconciliation) or cash (M confirms receipt + evidence) | `POST /api/technician/contracts/{id}/installments/1/payment`; `POST /api/manager/payments/{attemptId}/cash/confirm` | contract AWAITING_SIGNATURE; packages still COMMITTED, nothing active |
| 7 | Paper signed, photographed pages finalized | `POST …/signature-set`, `PUT …/pages/{n}`, `POST …/finalize` | SIGNED; installation scheduled |
| 8 | Installation; T registers real panels (serial or lot by count) and reports completion | installation routes | PENDING_CUSTOMER_ACCEPTANCE |
| 9 | C accepts (or requests rework) | `POST /api/customers/me/installation-projects/{id}/acceptance` | handover; installment 2 DUE (deadline ≤ 72 h). Rework: installment 2 stays PENDING |
| 10 | Balance 70 % of all | installment 2 payment (customer or technician QR / cash) | contract PAID → provisioning in the same transaction |
| 11 | Provisioning | automatic | panels match → PANEL-scope subscription(s), allocations, term from max(full payment, handover), disaster waiting period; else RECONCILIATION_REQUIRED |
| 12 | Term reached | sweep / immediately | ACTIVE; maintenance entitlements = package plan months |

## 2. Retail top-up after handover

1. C reads capacity: `GET /api/customers/me/after-sales/contracts/{contractId}/panel-coverage` (units held / available per kind and panel row, renewable terms, active PER_PANEL packages).
2. C buys: `POST /api/customers/me/after-sales/purchases` with `{ contractId, disasterPackageId | maintenancePackageId, includeContract: false, paymentMethod, quantity, targetAssets: [{ assetId, units }] }` (or `renewsSubscriptionId`).
3. Backend re-checks the units under the contract lock and opens ONE checkout of unit price × units (PayOS or cash). Units are reserved while it is open.
4. Verified payment → overlap re-checked → PAID → term starts (now, or at the renewed term's end) → ACTIVE. The installation contract is untouched.

## 3. Exception branches

| Branch | Outcome |
| --- | --- |
| Package inactive / wrong kind / legacy pricing unit / unknown | 409 `QUOTATION_SERVICE_PACKAGE_INACTIVE` / `…_INCOMPATIBLE`, 404 `…_NOT_FOUND`; nothing changed |
| No panels / fractional panels with a package | 409 `QUOTATION_NO_ELIGIBLE_PANELS` / `QUOTATION_INVALID_PANEL_QUANTITY` (selection and line edits) |
| Stale revision (two editors) | 409 `QUOTATION_STALE_REVISION` (`details.currentRevision`) |
| Not the surveying technician | 404 `QUOTATION_NOT_FOUND` |
| Edit after issue | 409 `QUOTATION_NOT_DRAFT` (create a revision) |
| Catalog changes after issue | issued quotation, contract and installments unchanged |
| Total above 999,999,999,999 VND | 409 `QUOTATION_TOTAL_OUT_OF_RANGE` |
| Customer withdraws before deposit | contract DISCARDED, commitments DISCARDED |
| Wrong cash amount / technician tries to confirm cash | refused / 403 |
| Duplicate webhook, webhook vs reconciliation race | applied once; one subscription per commitment (unique index) |
| Rework requested | balance stays PENDING; no activation |
| Balance overdue | collection case for the full balance (packages included); nothing activated |
| Installed panels ≠ contracted | RECONCILIATION_REQUIRED, Managers notified, no coverage, no money change; retry after correction |
| Retail on covered units / concurrent buyers of a unit | 409 `SERVICE_SUBSCRIPTION_OVERLAP` (one sale) |
| Retail on a replaced / unknown panel, bad units | 400 `SERVICE_PANEL_SELECTION_VALIDATION_FAILED` |
| Retail with per-panel package but no panels; per-panel fields on a legacy package | 400 `SERVICE_PACKAGE_PRICING_UNIT_VALIDATION_FAILED` |
| Retail before handover / contract not fully paid | 409 `AFTER_SALES_NOT_HANDED_OVER` / `AFTER_SALES_INVALID_STATE` |
| Disaster claim without naming the panel, or on an uncovered / replacement panel | claim NOT_ELIGIBLE with the reason |
| Legacy installation-wide package while per-panel coverage holds (or the reverse) | 409 `SERVICE_SUBSCRIPTION_OVERLAP` |
