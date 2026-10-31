# Automated installation quotation — business and integration handoff

Branch `feat/fe-backend-gap-fixes`. Audience: frontend developers, Sales/Manager product owners and backend reviewers.
All prices, products and people below are **SYNTHETIC examples**, not production data.

## 1. What changed in the business flow

Before: after the on-site survey, Sales typed every quotation line, price and service cost by hand, then issued it.

Now: the **technician confirms a bill of materials (BOM)**: catalog product ids and quantities only, never prices. When the
survey is completed, the **backend prices the BOM from the catalog** and, when nothing needs a human decision, **issues the
STANDARD quotation automatically**. Sales handles only the exceptions.

```
Technician completes survey with BOM
        │
        ▼
Backend prices every item from the active VND catalog
        │
        ├── no issues ───────────────► quotation ISSUED (origin AUTOMATIC) → customer accepts → contract → PayOS → installation
        │
        └── any issue ──────────────► quotation DRAFT (origin AUTOMATIC) in the Sales review queue
                                         Sales fixes and issues it → same flow as above
```

Unchanged: the manual Sales quotation (a survey **without** a BOM behaves exactly as before), customer revision requests and
new versions, physical contract signing and Sales review, PayOS single 100 % payment, installation, handover, after-sales,
and the solar/shading simulation.

### When the backend issues automatically (STANDARD)

All of the following hold:

* The survey is COMPLETED with feasibility `FEASIBLE`.
* The BOM has at least one `SOLAR_PANEL`, at least one `INVERTER` and at least one `INSTALLATION_LABOR` service item.
* Every item is an **active** catalog product priced in **VND** with a price > 0, and its kind matches its list:
  equipment in `materials`, and `INSTALLATION_LABOR` / `TRANSPORT` / `OTHER_SERVICE` in `serviceItems`.
* The request has a Sales owner, which is needed for follow-up and the contract.
* The total is positive and within the maximum amount.

### When it goes to Sales (EXCEPTION, `PENDING_SALES_REVIEW`)

A DRAFT is stored with `pricingBasis.issues[]`. Drafts are invisible to the customer.

| `issues[].code` | Meaning | Typical Sales action |
|---|---|---|
| `PRICE_MISSING` | Catalog price is 0 or missing (never treated as free) | Agree the price; set labor/transport/other or edit the line; issue |
| `PRODUCT_UNAVAILABLE` | Product unknown, inactive or deleted at pricing time | Replace the line |
| `WRONG_ITEM_KIND` | Equipment listed as a service or the reverse | Correct the lines |
| `CURRENCY_NOT_SUPPORTED` | Catalog product not priced in VND | Replace or price manually |
| `MISSING_SOLAR_PANEL` / `MISSING_INVERTER` / `LABOR_NOT_SPECIFIED` | A required part of a standard installation is missing | Ask the technician to amend, or add it |
| `CONDITIONS_REQUIRE_REVIEW` | Feasibility `FEASIBLE_WITH_CONDITIONS` | Review the conditions in the survey notes |
| `INVALID_TOTAL` | Total ≤ 0 or above the maximum | Correct quantities |

### Other outcomes

| `automaticQuotationOutcome` | Meaning |
|---|---|
| `AUTO_ISSUED` | Issued to the customer |
| `PENDING_SALES_REVIEW` | Draft waiting for Sales (see above) |
| `ALREADY_GENERATED` | A quotation for this survey version already exists. Repeats are idempotent. |
| `MANUAL_QUOTATION_REQUIRED` | No BOM; Sales prepares the quotation manually as before |
| `NOT_FEASIBLE` | Technician found the installation not feasible; no quotation |
| `SALES_OWNER_REQUIRED` | Request not yet claimed; retry after claiming |
| `SURVEY_CHANGED_REVIEW_REQUIRED` | The survey was amended after a quotation for an older version; Sales decides (revision). Nothing is replaced automatically. |
| `SURVEY_NOT_COMPLETED` | Survey not completed |
| `FAILED` | Unexpected error; nothing stored; safe to retry |

### Pricing formula (unchanged `QuotationPricing`)

```
lineTotal = round(quantity × catalogUnitPrice)                  whole VND, 0.5 rounds away from zero
equipmentSubtotal = Σ lineTotal (materials)
labor / transport / other = Σ (quantity × price) of the matching service products
subtotal = equipment + labor + transport + other
tax      = round((subtotal − discount) × taxRatePercent / 100)
total    = subtotal − discount + tax
```

Automatic quotations apply **no discount**, because no discount policy exists. Tax is `Quotation:DefaultTaxRatePercent` (env `Quotation__DefaultTaxRatePercent`)
(default 8 %). The quotation is valid for `Quotation:AutomaticValidityDays` (default 30). Both are validated at startup.

**Worked example (synthetic catalog, tax 0 %)**: 2 panels × 3,000,000 + 1 inverter × 10,000,000 = 16,000,000 equipment;
labor 2,000,000; transport 500,000; subtotal 18,500,000; total **18,500,000 VND**. After a customer revision with an
authorized 500,000 discount, version 2 totals **18,000,000 VND**, version 1 becomes SUPERSEDED, and the contract uses version 2.
With 8 % tax the first version would be 18,500,000 + 1,480,000 = **19,980,000 VND**.

The simulation is a **reference only**. `pricingBasis.selectedSimulationId` links the customer's selected simulation and
`installedCapacityKwp = Σ panel ratedPowerW × quantity / 1000`. The price never depends on estimated kWh.

Every price is copied into the quotation (line `unitPrice` and `catalogUnitPrice`, plus `pricingBasis`). Later catalog price
changes never alter an issued or draft quotation.

## 2. API changes (all additive)

### Technician: BOM in the on-site survey

`PUT /api/technicians/me/appointments/{appointmentId}/onsite-survey` (existing save body) gains an optional `billOfMaterials`.
Omitting it keeps the stored BOM:

```json
"billOfMaterials": {
  "materials":    [ { "productId": "…panel id…", "quantity": 2 }, { "productId": "…inverter id…", "quantity": 1 } ],
  "serviceItems": [ { "productId": "…labor id…", "quantity": 1 }, { "productId": "…transport id…", "quantity": 1 } ],
  "installationNotes": "Standard rooftop installation"
}
```

There is no price field; a price sent by the client is ignored by design.

Validation is 400 `VALIDATION_FAILED` with `fieldErrors`: at most 100 items, a non-empty productId, 0 < quantity ≤ 100,000,
no duplicate product in a list, and notes ≤ 2,000 characters. On **complete**, the catalog is checked: 400
`ONSITE_SURVEY_BOM_VALIDATION_FAILED` for unknown or inactive products, the wrong kind, or a fractional quantity for
PCS/PC/SET/UNIT/EA units.

After completion, BOM changes go through the existing **amendment** (with a reason). The change is recorded as
`billOfMaterials: { old, new }` and the survey version increases.

The complete response (`OnSiteSurveyView`) gains `automaticQuotationOutcome` (see the table above). It is `null` on other calls.

The catalog service products are ordinary products with `productType` = `INSTALLATION_LABOR`, `TRANSPORT` or `OTHER_SERVICE`
(unit e.g. `JOB`, `TRIP`, `KWP`), managed with the existing admin product endpoints.

### Sales / Manager / Admin

| Method & path | Roles | Purpose |
|---|---|---|
| `POST /api/survey-requests/{surveyRequestId}/quotations/generate` | Sales owner, Manager, Admin | Runs or repeats the automatic step (idempotent), e.g. after claiming the request or fixing the catalog. 200 → `{ outcome, quotation, issues }`. 404 `QUOTATION_NOT_FOUND` for another Sales' request. |
| `GET /api/quotations/pending-review` | Sales (own requests), Manager/Admin (all) | Exception queue: AUTOMATIC drafts with `pricingBasis.issues`. |

Status codes: 401 without a token; 403 for Technician and Customer. These are covered by `AutomaticQuotationEndpointsTests`.

`QuotationView` (Sales and customer responses) gains `origin` (`SALES` | `AUTOMATIC`) and `pricingBasis` (or `null` for
manual quotations). Existing edit, issue and revision endpoints stay **Sales-only**; a Technician or Customer gets 403.

## 3. Database

Migration `20261030122441_AutomatedInstallationQuotation`, additive and **NOT applied** to any shared database:

* `quotation.origin` varchar(20) NOT NULL DEFAULT `'SALES'` + check `ck_quotation_origin` (`SALES`, `AUTOMATIC`). Existing
  rows become `SALES`.
* `quotation.pricing_basis` jsonb NULL.
* `onsite_survey_report.bill_of_materials` jsonb NULL.

`Down` drops all three. Apply only through the approved migration process.

## 4. Consistency, security, concurrency

* The survey completion commits first. Quotation generation runs in **its own transaction**: quotation, lines and events
  are written together or not at all. A generation failure never undoes the completed survey; it is logged and can be
  retried (repeat completion or the generate endpoint).
* Duplicates: the result is idempotent per survey version, and the existing unique "one draft / one live quotation per
  request" indexes stop concurrent generations. The loser returns `ALREADY_GENERATED` with the winner.
* Prices are never accepted from the client on this path. Events `AUTOMATICALLY_GENERATED` and `ISSUED` record the actor
  and the total.
* Ownership checks are unchanged (Sales owner or Manager). Other users get 404, not 403, so the request's existence is
  not revealed.

## 5. Not implemented / open decisions

* **Manager approval** (e.g. for discounts above a threshold): no approval policy exists in the codebase, so none was
  invented. Automatic quotations carry no discount; Sales discounts follow the existing Sales rules. To add one, define
  the policy (threshold, approver role) first.
* `installedCapacityKwp` is not validated against the selected simulation's layout. A large difference is not flagged yet.
* The PostgreSQL concurrency of two simultaneous generations relies on the existing unique indexes. A dedicated Postgres
  race test was not run, because Docker was unavailable.

## 6. Tests

* `Unit/InstallationWorkflow/AutomaticQuotationTests`: formula, auto-issue, idempotency, catalog snapshot, missing price
  → Sales, feasibility, no BOM, missing parts, BOM validation, Sales owner required, amended survey.
* `EndToEnd/InstallationLifecycleE2ETests` (E2E-01..E2E-10): real handlers of every module on one database. Fakes are used
  only at the provider boundary (PV/climate, S3 verification, PayOS). The HTTP layer is covered by the endpoint tests.
* `Integration/InstallationWorkflow/AutomaticQuotationEndpointsTests`: 401/403/404 role checks through the real pipeline.
