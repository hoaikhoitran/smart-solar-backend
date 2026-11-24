# Per-panel service packages — business rules (backend, 2026-11-25)

Scope: technician on-site sale of service packages priced per solar panel, one unified contract total paid 30/70, activation after full payment and
handover, and retail (100 % upfront) purchases after handover. All amounts are whole VND, `decimal`, computed by the backend. Every price comes from
the Admin catalog; the 700,000 VND/panel figure below is an example only.

## 1. Catalog

| Rule | Implementation |
| --- | --- |
| Each package has an explicit `pricingUnit`: `PER_PANEL` (price × panels) or `PER_INSTALLATION_LEGACY` (one installation-wide term) | `service_package.pricing_unit`, check constraint |
| Existing rows are **not** reinterpreted: migration default `PER_INSTALLATION_LEGACY`; create without `pricingUnit` = legacy | migration default, `CreatePackageAsync` |
| The pricing unit is fixed after creation (changing it would reinterpret a price) → 400; create a new package | `UpdatePackageAsync` |
| Only Admin edits price / pricing unit; the technician reads active `PER_PANEL` packages only | `/api/admin/after-sales/packages`, `/api/technician/service-packages` |

## 2. Quotation (technician cart)

- Packages: `MAINTENANCE`, `DISASTER_PROTECTION`; none, one or both; **at most one per kind** per quotation version (unique index).
- **Panel quantity** = Σ quantity of the current lines whose catalog product type is `SOLAR_PANEL` (every panel model counts; inverters, batteries,
  labour, transport and other services do not). It must be a positive whole number when a package is selected.
- Package quantity = the full panel quantity (partial coverage exists only in retail). The technician never types a price or a quantity.
- Changing panel lines recomputes package quantities and totals in the same transaction and revision. A change that would leave a selected package
  with 0 panels or a fractional count is refused (`QUOTATION_NO_ELIGIBLE_PANELS`, `QUOTATION_INVALID_PANEL_QUANTITY`); nothing is silently dropped.
- Issue re-reads every package from the catalog under the quotation lock and freezes the snapshot (unit price, benefits, catalog revision, time).
  A package that became inactive / incompatible blocks the issue. Lines and packages are frozen in the same step.
- A revision copies the packages (snapshot) into the new draft; its issue re-prices them. The previous issued/accepted version is never changed.

### Totals

```
installationTotal (= totalAmount, unchanged meaning: lines + quotation tax)
packageAmount      = catalog unit price × panelQuantity           (per package)
servicePackagesTotal = Σ packageAmount
grandTotal         = installationTotal + servicePackagesTotal     (what the customer agrees to)
```

**Tax:** the catalog package price is treated as a final selling price; the quotation tax rate is **not** applied to packages again
(`packageTaxTreatment = CATALOG_FINAL_PRICE_NO_QUOTATION_TAX` in the contract snapshot). See §8 (decision pending).

## 3. Contract (on-site AGREED)

- ONE 30/70 contract, created in the agreement transaction, with `totalAmount = grandTotal`, `installationTotal`, `servicePackagesTotal`
  (both null for contracts without packages, whose snapshot/hash is byte-identical to before).
- Each bundled package becomes a **commitment** (`contract_service_package`): immutable snapshot + its share of each installment. It is not a subscription.
- The canonical snapshot (hashed) contains the full breakdown; the generated terms state it (clauses 7–8).
- No separate package invoice or checkout ever exists for bundled packages.
- Withdrawal before the deposit: contract DISCARDED, commitments `DISCARDED`. After the deposit nothing about packages, quantity or total changes
  (an amendment would need a separately approved flow).
- Legacy paths (contract created from a 100 %-paid quotation, manual creation) refuse a quotation with packages.

## 4. Payment — 30/70 of everything (the FE proposal "30 % installation + 70 % installation + 100 % packages" is rejected)

```
deposit = Round(contractTotal × 30 %, whole VND, MidpointRounding.AwayFromZero)
balance = contractTotal − deposit                 deposit + balance = contractTotal (exact)
```

Worked example (mandatory): installation 50,000,000 + 10 panels × 700,000 = 7,000,000 → contract 57,000,000 → installment 1 = 17,100,000,
installment 2 = 39,900,000. No other payment exists for the package.

Internal allocation (accounting only, frozen at creation): each package gets Round(amount × 30 %) of installment 1 and the rest in installment 2; the
installation gets the remainder of each installment, so rounding differences are absorbed once and Σ = installment amounts exactly.

| | Installment 1 | Installment 2 | Total |
| --- | --- | --- | --- |
| Installation | 15,000,000 | 35,000,000 | 50,000,000 |
| Disaster package | 2,100,000 | 4,900,000 | 7,000,000 |
| **Payable** | **17,100,000** | **39,900,000** | **57,000,000** |

Both kinds (maintenance 300,000/panel): 50,000,000 + 3,000,000 + 7,000,000 = 60,000,000 → 18,000,000 + 42,000,000.

Unchanged payment safety: one live attempt per installment, exact amount, webhook idempotency, reconciliation, Manager/Admin-only cash confirmation
with evidence and receipt reference, unapplied-money queue, overdue collection cases (on the full balance).

## 5. Activation (bundled)

A bundled package is provisioned as a subscription only when **all** hold: contract fully paid (both installments verified), installation handed over,
ACTIVE installed panel units (serialised panels + lots) = contracted quantity, and no conflicting coverage of the same kind. Then:

- one PANEL-scope subscription per commitment (unique index → never twice, even with repeated webhooks / reconciliation / retries);
- price = commitment amount, `PaidAt` = full-payment time, no checkout;
- coverage allocated to the real panel rows (a lot gets one allocation with its unit count; no serial is invented);
- term start = later of full payment and handover; disaster coverage after `afterSales.disasterWaitingDays` (default 7); ACTIVE when the start is reached.

Mismatch (e.g. 10 contracted, 9 installed, or 12 installed) → commitment `RECONCILIATION_REQUIRED`, Managers notified, no subscription, no coverage,
no money change. Manager/Admin corrects the equipment record and retries (`POST /api/manager/after-sales/bundled-packages/{id}/reconcile`).

## 6. Benefits

- Maintenance: the package's plan months (visits) — **not** multiplied by panels. Quantity says how many panels the coverage applies to.
- Disaster protection: a claim against a PANEL-scope term must name the damaged panel (`suspectedAssetId`); it is assessed only under the term whose
  allocation includes that panel. A panel never borrows another panel's coverage. Per-incident and term limits are the term's own (not × panels).
  Existing Manager approval, reconsideration, reservation/consumption rules are unchanged.
- Installation-wide (legacy) terms keep covering the whole installation, as before.

## 7. Retail (after handover, 100 % upfront)

- Eligibility (`GET …/contracts/{id}/panel-coverage`): ACTIVE panel rows of the owned, fully paid, handed-over installation; per kind the units held by
  overlapping terms (active, paid, queued, or reserved by an open checkout / pending cash) and the units still available. Missing, replaced or
  unverified panels are never sellable.
- Purchase: `targetAssets` (row + units; a serialised panel is 1 unit, a lot up to its quantity), optional `quantity` (= Σ units). Amount = unit price × units.
  The contract total and the installments never change.
- No overlap for the same panel unit, kind and period: checked before the checkout and again under the contract row lock inside the checkout
  transaction (concurrent purchases sell a unit once) and again at payment settlement (money that cannot be applied goes to the refund queue).
- Renewal (`renewsSubscriptionId`): a per-panel term in force can be renewed once; the new term starts when it ends (continuous, no new waiting period).
- Retail of a kind is refused while the contract's bundled package of that kind waits for reconciliation (it is never sold twice).
- Installation-wide and per-panel coverage of the same kind exclude each other (either way).

## 8. Decisions still required (not guessed in code)

| # | Decision | Current safe behaviour |
| --- | --- | --- |
| D1 | **Tax on packages**: is the catalog price VAT-inclusive, and how is VAT shown on the contract/invoice? | No quotation tax added to packages; snapshot states `CATALOG_FINAL_PRICE_NO_QUOTATION_TAX`; terms say tax treatment awaits Finance/Legal. |
| D2 | **Coverage transfer on panel replacement** | Never transferred: allocation stays on the replaced panel (history); the replacement panel is offered for a new retail purchase. |
| D3 | **Quantity mismatch at handover** (refund, partial activation, or allocation to a subset when more panels are installed) | Reconciliation exception, nothing granted, no money change; Manager corrects the record and retries. |
| D4 | Legal wording of package clauses (7–8) | Generated under the existing `TermsVersion` marked pending legal review. |
| D5 | Installation-wide legacy term and per-panel term of the same kind at the same time | Refused both ways. |
