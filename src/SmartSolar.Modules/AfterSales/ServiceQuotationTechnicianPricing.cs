using SmartSolar.Modules.Catalog.Constants;
using SmartSolar.Modules.Common.Results;
using SmartSolar.Modules.Quotations;

namespace SmartSolar.Modules.AfterSales;

/// <summary>One catalog product of a technician quotation line: quantity only (the price always comes from the catalog).</summary>
public sealed record QuotationComponentInput(Guid ProductId, decimal Quantity);

/// <summary>A chargeable / disaster-protected item of the diagnosis with the catalog products and labor the repair needs.</summary>
public sealed record TechnicianQuotationLineInput(Guid ItemId, IReadOnlyList<QuotationComponentInput> Components, string? Description);

public sealed record TechnicianQuotationInput(IReadOnlyList<TechnicianQuotationLineInput> Lines, int? RepairWarrantyMonths, string? Note);

/// <summary>
/// Catalog snapshot of one priced component, stored in the quotation lines (history: later catalog changes never alter an issued quotation).
/// <see cref="Kind"/> is LABOR for a priced service product (installation labor, transport, other service) and PARTS for equipment.
/// </summary>
public sealed record QuotationComponentSnapshot(Guid ProductId, string Sku, string Name, string ProductType, string Unit, decimal UnitPrice, decimal Quantity,
    decimal Amount, string Kind);

/// <summary>
/// Technician-priced after-sales quotations (business 2026-11-18): the technician assigned to a visit of a FAULT request prices the chargeable and
/// disaster-protected items of the diagnosis with catalog products (replacement parts) and priced service products (labor, transport). The backend
/// computes every amount — quantity × catalog unit price, rounded to whole VND — and snapshots the products; the client never sends an amount.
/// Coverage stays the Manager's decision (free items need no quotation); everything after the pricing is the shared issue step (versioning,
/// supersede, disaster support reservation, customer acceptance, payment). Inspected one-time maintenance follows the same path (2026-11-18);
/// package maintenance visits are prepaid and never quoted.
/// </summary>
public sealed partial class ServiceQuotationHandlers
{
    private const int MaxComponentsPerLine = 20;
    private const decimal MaxQuantity = 10_000m;

    public async Task<OperationResult<ServiceRequestView>> IssueByTechnicianAsync(Guid technicianUserId, Guid requestId, TechnicianQuotationInput input,
        CancellationToken cancellationToken)
    {
        var request = await data.FindRequestAsync(requestId, false, cancellationToken);
        // Only the technician of a (non-declined, non-cancelled) visit of this request may price it; anyone else: 404 without disclosure.
        var assigned = request is not null && (await data.ListVisitsAsync(requestId, false, cancellationToken))
            .Any(v => v.TechnicianUserId == technicianUserId && v.Status is not (VisitStatuses.Declined or VisitStatuses.Cancelled));
        if (request is null || !assigned) return Fail(AfterSalesErrorCodes.RequestNotFound, "Request was not found.");
        // FAULT requests, and one-time maintenance once inspected (diagnosed work items). A legacy one-time maintenance without inspection is priced by Sales.
        if (request.Kind == ServiceRequestKinds.Maintenance
            || request.Kind == ServiceRequestKinds.OneTimeMaintenance && (await data.ListItemsAsync(requestId, false, cancellationToken)).Count == 0)
            return Fail(AfterSalesErrorCodes.InvalidState,
                "Only fault requests and inspected one-time maintenance are priced by the technician (package visits are prepaid; a maintenance without inspection is priced by Sales).");

        if (input.Lines.Count is 0 or > 50) return Invalid("lines", "Give between 1 and 50 lines.");
        foreach (var line in input.Lines)
        {
            if (line.Components.Count is 0 or > MaxComponentsPerLine)
                return Invalid("lines", $"Each item needs between 1 and {MaxComponentsPerLine} catalog products.");
            if (line.Components.Any(c => c.ProductId == Guid.Empty || c.Quantity <= 0 || c.Quantity > MaxQuantity))
                return Invalid("lines", $"Each product needs an id and a quantity above 0 and at most {MaxQuantity:0}.");
            if (line.Components.Select(c => c.ProductId).Distinct().Count() != line.Components.Count)
                return Invalid("lines", "List each product once per item (add the quantities).");
        }
        if (input.Lines.Select(l => l.ItemId).Distinct().Count() != input.Lines.Count) return Invalid("lines", "Price each item once.");

        var catalog = await data.GetCatalogProductsAsync(input.Lines.SelectMany(l => l.Components.Select(c => c.ProductId)).Distinct().ToList(), cancellationToken);
        var problems = new List<object>();
        var anyPriceMissing = false;
        var priced = new List<QuotationLineInput>();
        var snapshots = new Dictionary<Guid, IReadOnlyList<QuotationComponentSnapshot>>();
        foreach (var line in input.Lines)
        {
            var components = new List<QuotationComponentSnapshot>();
            foreach (var c in line.Components)
            {
                var product = catalog.GetValueOrDefault(c.ProductId);
                if (PriceProblem(product) is { } problem)
                {
                    problems.Add(new { itemId = line.ItemId, productId = c.ProductId, code = problem.Code, message = problem.Message });
                    anyPriceMissing |= problem.Code == AfterSalesErrorCodes.PriceMissing;
                    continue;
                }
                var kind = ProductTypes.IsService(product!.ProductType) ? "LABOR" : "PARTS";
                components.Add(new QuotationComponentSnapshot(product.ProductId, product.Sku, product.Name, ProductTypes.Normalize(product.ProductType), product.Unit,
                    product.UnitPrice, c.Quantity, QuotationPricing.LineTotal(c.Quantity, product.UnitPrice), kind));
            }
            snapshots[line.ItemId] = components;
            priced.Add(new QuotationLineInput(line.ItemId, Text.Trim(line.Description, 300),
                components.Where(c => c.Kind == "PARTS").Sum(c => c.Amount), components.Where(c => c.Kind == "LABOR").Sum(c => c.Amount)));
        }
        if (problems.Count > 0)
        {
            // Never quote around a missing price: the whole quotation waits until the catalog can price every product.
            return OperationResult<ServiceRequestView>.Fail(anyPriceMissing ? AfterSalesErrorCodes.PriceMissing : AfterSalesErrorCodes.ProductUnavailable,
                "Some products cannot be priced from the catalog; replace them or ask an Admin to price them.", new { items = problems });
        }
        if (priced.Sum(l => l.PartsAmount + l.LaborAmount) > QuotationPricing.MaxAmount) return Invalid("lines", "The quotation total is not supported.");

        var open = (await data.ListQuotationsAsync(requestId, false, cancellationToken)).FirstOrDefault(q => QuotationStatuses.IsOpen(q.Status));
        return await IssueCoreAsync(technicianUserId, QuotationIssuers.Technician, request, open, SalesTaskTypes.RepairQuotation,
            new IssueQuotationInput(priced, input.RepairWarrantyMonths, input.Note), snapshots, cancellationToken);
    }

    private static (string Code, string Message)? PriceProblem(CatalogProductSnapshot? product)
        => product is null || !product.IsActive ? (AfterSalesErrorCodes.ProductUnavailable, "The product is not an active catalog product.")
            : product.Currency != QuotationPricing.Currency ? (AfterSalesErrorCodes.ProductUnavailable, $"Product {product.Sku} is not priced in VND.")
            : product.UnitPrice <= 0 ? (AfterSalesErrorCodes.PriceMissing, $"Product {product.Sku} has no catalog price; it cannot be quoted until the catalog has one.")
            : product.UnitPrice > QuotationPricing.MaxAmount ? (AfterSalesErrorCodes.PriceMissing, $"Product {product.Sku} has an unsupported catalog price.")
            : null;
}
