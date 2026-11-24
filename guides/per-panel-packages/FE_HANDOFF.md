# FE handoff — per-panel service packages (backend API contract)

Date: 2026-11-25. Backend only: this document describes what the API returns and accepts; it prescribes no FE source change. Every response uses the
existing envelope `{ isSuccess, traceId, data, error: { code, message, details } }`. All money is whole VND numbers. All additions are **backward
compatible** (new optional request fields, new response fields); no existing route changed meaning.

> **The 30/70 split is computed on `grandTotal` (installation + packages), never on the installation alone.** The FE proposal
> "30 % installation + 70 % installation + 100 % packages" is rejected: there is no separate package payment for bundled packages.

## 1. Technician

### 1.1 `GET /api/technician/service-packages?kind=MAINTENANCE|DISASTER_PROTECTION` (TECHNICIAN; read rate limit)

Active `PER_PANEL` packages only. `data`: `PackageView[]`

| Field | Type | Note |
| --- | --- | --- |
| `packageId`, `kind`, `name`, `description`, `imageUrl` | | |
| `price` | number | **price per panel** when `pricingUnit = PER_PANEL` |
| `pricingUnit` | `PER_PANEL` \| `PER_INSTALLATION_LEGACY` | always `PER_PANEL` here |
| `durationMonths`, `visitMonths[]` | | maintenance visits are per term, not per panel |
| `coveredDisasterTypes[]`, `coveragePercent`, `perIncidentLimit`, `termLimit` | | disaster limits are per term, not per panel |
| `isActive`, `revision` | | |

Errors: 400 `AFTER_SALES_VALIDATION_FAILED` (unknown kind); 401; 403 for other roles.

### 1.2 `PUT /api/technician/quotations/{quotationId}/service-packages/{kind}` — choose or replace

Body `{ "packageId": "uuid", "revision": 7 }`. No price, no quantity. Response `QuotationView` (revision + 1).

### 1.3 `DELETE /api/technician/quotations/{quotationId}/service-packages/{kind}?revision=7` — remove

Response `QuotationView`.

Errors (1.2, 1.3 — and the package codes can also come from line edits and issue):

| HTTP | Code | When |
| --- | --- | --- |
| 400 | `VALIDATION_FAILED` | missing `packageId` / `revision`, unknown `kind` |
| 404 | `QUOTATION_NOT_FOUND` | unknown, or not the caller's survey |
| 404 | `QUOTATION_SERVICE_PACKAGE_NOT_FOUND` | unknown package; DELETE of a kind not selected |
| 409 | `QUOTATION_NOT_DRAFT` | issued/accepted/closed — create a revision |
| 409 | `QUOTATION_STALE_REVISION` | `details.currentRevision`: reload |
| 409 | `QUOTATION_SERVICE_PACKAGE_INACTIVE` | no longer sold |
| 409 | `QUOTATION_SERVICE_PACKAGE_INCOMPATIBLE` | other kind, or not `PER_PANEL` |
| 409 | `QUOTATION_NO_ELIGIBLE_PANELS` | no `SOLAR_PANEL` line (also when removing the last panel line while a package is selected) |
| 409 | `QUOTATION_INVALID_PANEL_QUANTITY` | fractional panel total |
| 409 | `QUOTATION_TOTAL_OUT_OF_RANGE` | total above the supported range |

`POST …/issue` additionally returns the first blocking package code with `details.packages[{ kind, packageId, code, problem }]`.

### 1.4 `QuotationView` — new fields (every technician quotation response)

| Field | Meaning |
| --- | --- |
| `totalAmount` | **unchanged meaning**: installation total (lines + quotation tax) |
| `installationTotal` | same value as `totalAmount` (explicit name) |
| `panelQuantity` | whole SOLAR_PANEL units (null when none or fractional) |
| `servicePackages[]` | `{ packageId, kind, packageName, description, imageUrl, pricingUnit, unitPrice, quantity, totalAmount, durationMonths, visitMonths[], coveredDisasterTypes[], coveragePercent, perIncidentLimit, termLimit, catalogRevision, snapshotAt }` |
| `maintenancePackagesTotal`, `disasterPackagesTotal`, `servicePackagesTotal` | sums |
| `grandTotal` | **amount to present to the customer** = `installationTotal + servicePackagesTotal` |

Example (10 panels × 700,000 disaster package):

```json
{ "totalAmount": 50000000, "installationTotal": 50000000, "panelQuantity": 10,
  "servicePackages": [ { "packageId": "…", "kind": "DISASTER_PROTECTION", "pricingUnit": "PER_PANEL", "unitPrice": 700000,
                         "quantity": 10, "totalAmount": 7000000, "durationMonths": 12, "catalogRevision": 3, "snapshotAt": "…" } ],
  "maintenancePackagesTotal": 0, "disasterPackagesTotal": 7000000, "servicePackagesTotal": 7000000, "grandTotal": 57000000, "currency": "VND" }
```

Display rules: show package lines separately from equipment lines; show "Giá gói = giá danh mục, không cộng thêm thuế báo giá" (tax treatment
pending Finance/Legal, see BUSINESS_RULES §8 D1); after a line change re-read `servicePackages` (quantities follow the panels).

## 2. Contract and installments (customer and staff contract views)

`GET /api/customers/me/contracts/{id}`, `GET /api/contracts/{id}` — `ContractView` new fields:

| Field | Meaning |
| --- | --- |
| `totalAmount` | **grand total** for contracts with packages (57,000,000 in the example) |
| `installationTotal`, `servicePackagesTotal` | breakdown; **null** for contracts without packages |
| `servicePackages[]` | `{ commitmentId, kind, packageId, packageName, description, imageUrl, pricingUnit, unitPrice, quantity, totalAmount, durationMonths, visitMonths[], coveredDisasterTypes[], coveragePercent, perIncidentLimit, termLimit, catalogRevision, snapshotAt, depositAllocation, balanceAllocation, status, statusReason, subscriptionId, statusChangedAt }` |
| `paymentPlan.total` | = `totalAmount` |
| `paymentPlan.installments[].allocation` | `{ sequence, amount, installation, maintenancePackages, disasterPackages }` (null without packages) |
| `pricingSnapshot` | adds `installationTotal, panelQuantity, servicePackages[], servicePackagesTotal, grandTotal, packageTaxTreatment` |

`servicePackages[].status`: `COMMITTED` (paid within the contract, not active yet) · `PROVISIONED` (subscription exists; see its own status) ·
`RECONCILIATION_REQUIRED` (installed panels differ; staff review; nothing granted) · `DISCARDED` (contract withdrawn before the deposit).

Installment payment views (`GET|POST /api/technician/contracts/{id}/installments/{n}/payment`, `/api/customers/me/contracts/{id}/installments/{n}/payment`,
`GET /api/contracts/{id}/installments/{n}/payment`) add `contractTotal`, `installationTotal`, `servicePackagesTotal`, `allocation`. **The payable amount
is `amount`** (QR amount = installment amount). Never add a package line or a second checkout.

Example installments: `[{ sequence: 1, percent: 30, amount: 17100000, allocation: { installation: 15000000, disasterPackages: 2100000 } },
{ sequence: 2, percent: 70, amount: 39900000, allocation: { installation: 35000000, disasterPackages: 4900000 } }]`.

## 3. Customer after-sales

### 3.1 `GET /api/customers/me/after-sales/contracts/{contractId}/panel-coverage` (CUSTOMER, own contract)

```
{ contractId, projectId, contractFullyPaid, handedOver, canPurchase, reason,
  panels: [{ assetId, manufacturer, model, serialNumber, isLot, quantity, positionCode,
             coverage: [{ kind, heldUnits, availableUnits }] }],
  kinds:  [{ kind, activePanelUnits, heldUnits, availableUnits, blocker, renewableSubscriptionIds[] }],
  perPanelPackages: PackageView[] }
```

Use `availableUnits` as the maximum; never compute it in the FE. 404 `AFTER_SALES_PROJECT_NOT_FOUND` for another customer's contract.

### 3.2 `POST /api/customers/me/after-sales/purchases` — extended (old payloads unchanged)

```json
{ "contractId": "…", "disasterPackageId": "…", "includeContract": false, "paymentMethod": "ONLINE",
  "quantity": 2, "targetAssets": [ { "assetId": "…", "units": 1 }, { "assetId": "…", "units": 1 } ] }
```

Renewal: `{ "contractId", "disasterPackageId", "includeContract": false, "renewsSubscriptionId": "…" }` (targets default to the renewed term's panels).
Response unchanged (`checkout`, `subscriptionIds`); checkout amount = unit price × units (100 % upfront).

| HTTP | Code | When |
| --- | --- | --- |
| 400 | `SERVICE_PANEL_SELECTION_VALIDATION_FAILED` | panel not an active installed panel of this installation, units out of range, duplicate row, `quantity` ≠ Σ units |
| 400 | `SERVICE_PACKAGE_PRICING_UNIT_VALIDATION_FAILED` | per-panel package without `targetAssets`; per-panel fields with a legacy package; mixing both kinds of pricing unit; per-panel with `includeContract` on a legacy SIGNED contract (on a 30/70 contract `includeContract` is refused earlier with 409 `AFTER_SALES_INVALID_STATE`, as before) |
| 409 | `SERVICE_SUBSCRIPTION_OVERLAP` | units already covered / reserved for that kind and period; bundled package of that kind awaiting reconciliation; installation-wide term in force |
| 409 | `AFTER_SALES_NOT_HANDED_OVER` / `AFTER_SALES_INVALID_STATE` | not handed over / contract not fully paid / renewal of a term not in force |

### 3.3 `GET /api/customers/me/after-sales/subscriptions` — new fields

`coverageScope` (`INSTALLATION` legacy | `PANEL`), `unitPrice`, `coveredUnits`, `coveredPanels[{ allocationId, assetId, unitCount, manufacturer, model,
serialNumber, isLot, assetStatus }]`, `contractServicePackageId` (bundled origin), `renewsSubscriptionId`.

### 3.4 Disaster fault request

`POST /api/customers/me/after-sales/fault-requests` with `disasterType`: for per-panel protection send `suspectedAssetId` (the damaged panel). The claim
(`claim.coverageAllocationId`) is NOT_ELIGIBLE with a reason when the panel is not named or not covered.

## 4. Staff

- `GET /api/manager/after-sales/bundled-packages?status=&page=&pageSize=` (Manager/Admin): paged `{ items, page, pageSize, totalItems, totalPages }` of
  `{ commitmentId, contractId, kind, packageName, unitPrice, quantity, totalAmount, depositAllocation, balanceAllocation, status, statusReason,
  statusChangedAt, subscriptionId, subscriptionStatus, startsAt, endsAt, coverageStartsAt, revision }`.
- `POST /api/manager/after-sales/bundled-packages/{commitmentId}/reconcile` (Manager/Admin): retry after correcting the installed panels.
- `POST|PUT /api/admin/after-sales/packages` (Admin): optional `pricingUnit` (`PER_PANEL` | `PER_INSTALLATION_LEGACY`, default legacy; immutable).
- `GET /api/admin/finance/overview` adds `collectedByComponent[]`: `INSTALLATION`, `BUNDLED_MAINTENANCE_PACKAGES`, `BUNDLED_DISASTER_PROTECTION_PACKAGES`
  (Σ = deposits + balances collected), `RETAIL_MAINTENANCE_PACKAGES`, `RETAIL_DISASTER_PROTECTION_PACKAGES`.
- Cash confirmation stays Manager/Admin only (`POST /api/manager/payments/{attemptId}/cash/confirm`).

## 5. RBAC summary

| Route | Allowed |
| --- | --- |
| technician service-packages, quotation service-packages PUT/DELETE | TECHNICIAN (own surveyed request only; others 404) |
| panel-coverage, purchases | CUSTOMER (own contract) |
| bundled-packages, reconcile | MANAGER, ADMIN |
| admin packages | ADMIN |

## 6. FE integration steps

1. Technician cart: package picker from 1.1 per kind; PUT/DELETE with the current `revision`; render `servicePackages` and `grandTotal`.
2. Present `grandTotal` to the customer; show installation and package subtotals separately.
3. Contract screens: `totalAmount` (grand), breakdown, installment `amount` (payable) and `allocation` (informative only).
4. Customer subscriptions: show `coverageScope`, covered panels and status; `COMMITTED` packages appear on the contract until provisioned.
5. Retail: call 3.1, let the user pick rows/units up to `availableUnits`, send 3.2.
6. Fault form: allow choosing the damaged panel (asset list) for disaster claims.
