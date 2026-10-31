using System.Text.Json;
using SmartSolar.Modules.Catalog.Constants;
using SmartSolar.Modules.Common.Persistence;
using SmartSolar.Modules.Common.Results;
using SmartSolar.Modules.OnSiteSurveys;

namespace SmartSolar.Modules.Quotations;

/// <summary>One priced (or unpriceable) bill-of-materials item, as captured when the quotation was generated.</summary>
public sealed record PricedItem(Guid ProductId, string? Sku, string? Name, string? ProductType, string? Unit, decimal Quantity, decimal? UnitPrice, decimal? LineTotal);

/// <summary>Why a quotation could not be priced and issued automatically. <see cref="Code"/> is stable (see <see cref="PricingIssueCodes"/>).</summary>
public sealed record PricingIssue(string Code, string Message, Guid? ProductId = null);

/// <summary>
/// What an automatic quotation was calculated from (stored with the quotation, never changed afterwards). The selected simulation
/// and the installed capacity are references for engineering and consultation only: the price never depends on estimated kWh.
/// </summary>
public sealed record PricingBasis(
    int OnSiteSurveyVersion,
    Guid? SelectedSimulationId,
    decimal? InstalledCapacityKwp,
    string? InstallationFeasibility,
    string? InstallationNotes,
    IReadOnlyList<PricedItem> Materials,
    IReadOnlyList<PricedItem> Services,
    IReadOnlyList<PricingIssue> Issues,
    DateTimeOffset PricedAt);

public static class PricingIssueCodes
{
    public const string ProductUnavailable = "PRODUCT_UNAVAILABLE";
    public const string WrongItemKind = "WRONG_ITEM_KIND";
    public const string CurrencyNotSupported = "CURRENCY_NOT_SUPPORTED";
    public const string PriceMissing = "PRICE_MISSING";
    public const string MissingSolarPanel = "MISSING_SOLAR_PANEL";
    public const string MissingInverter = "MISSING_INVERTER";
    public const string LaborNotSpecified = "LABOR_NOT_SPECIFIED";
    public const string ConditionsRequireReview = "CONDITIONS_REQUIRE_REVIEW";
    public const string InvalidTotal = "INVALID_TOTAL";
}

/// <summary>Result of the automatic quotation step (stable strings for the FE).</summary>
public static class AutomaticQuotationOutcomes
{
    /// <summary>Priced from the catalog and ISSUED to the customer.</summary>
    public const string Issued = "AUTO_ISSUED";
    /// <summary>A DRAFT with pricing issues waits for Sales (exception path); the customer sees nothing yet.</summary>
    public const string PendingSalesReview = "PENDING_SALES_REVIEW";
    /// <summary>A quotation for this survey version already exists (idempotent repeat).</summary>
    public const string AlreadyGenerated = "ALREADY_GENERATED";
    /// <summary>No bill of materials: Sales prepares the quotation manually, as before.</summary>
    public const string ManualQuotationRequired = "MANUAL_QUOTATION_REQUIRED";
    /// <summary>The technician found the installation NOT_FEASIBLE: no installation quotation is made.</summary>
    public const string NotFeasible = "NOT_FEASIBLE";
    /// <summary>No Sales owner yet: generation waits until the request is claimed (then retry).</summary>
    public const string SalesOwnerRequired = "SALES_OWNER_REQUIRED";
    /// <summary>The survey was amended after a quotation for an older version: Sales decides (revision), nothing is replaced automatically.</summary>
    public const string SurveyChangedReviewRequired = "SURVEY_CHANGED_REVIEW_REQUIRED";
    public const string SurveyNotCompleted = "SURVEY_NOT_COMPLETED";
    /// <summary>The survey request was withdrawn or closed: nothing is generated.</summary>
    public const string RequestClosed = "REQUEST_CLOSED";
    /// <summary>Unexpected failure; nothing was stored (one transaction) and the step can be retried.</summary>
    public const string Failed = "FAILED";
}

public sealed record AutomaticQuotationResult(string Outcome, QuotationView? Quotation, IReadOnlyList<PricingIssue> Issues);

/// <summary>
/// Automatic STANDARD installation quotation. After the on-site survey is completed, the backend prices the technician's bill of
/// materials from the catalog and issues the quotation itself; Sales is only involved for exceptions. Rules:
/// <list type="bullet">
/// <item>Prices come only from active VND catalog products (equipment as lines; INSTALLATION_LABOR / TRANSPORT / OTHER_SERVICE
/// products summed into labor, transport and other services). Nobody types a price on this path, and a missing price is never zero.</item>
/// <item>No discount is applied automatically (no discount policy exists); tax is the configured <see cref="QuotationOptions.DefaultTaxRatePercent"/>.</item>
/// <item>Any problem (unknown/inactive product, missing price, no panel / inverter / labor, conditions to review, invalid total) leaves a
/// DRAFT for Sales with the issues listed; drafts are invisible to the customer.</item>
/// <item>Idempotent per survey version, and the one-draft / one-live database indexes stop concurrent duplicates.</item>
/// </list>
/// </summary>
public sealed partial class QuotationHandlers
{
    private static readonly JsonSerializerOptions BasisJson = new(JsonSerializerDefaults.Web);

    public async Task<AutomaticQuotationResult> GenerateStandardQuotationAsync(Guid surveyRequestId, Guid actorUserId, CancellationToken cancellationToken)
    {
        var source = await unitOfWork.GetSourceAsync(surveyRequestId, cancellationToken);
        var basis = await unitOfWork.GetSurveyBasisAsync(surveyRequestId, cancellationToken);
        if (source is null || basis is null || basis.Status != "COMPLETED") return Result(AutomaticQuotationOutcomes.SurveyNotCompleted);
        if (IsClosedRequest(source.SurveyRequestStatus)) return Result(AutomaticQuotationOutcomes.RequestClosed);

        // 1. Idempotency: one quotation per survey version; an older live quotation is never replaced automatically.
        var existing = await unitOfWork.ListBySurveyRequestAsync(surveyRequestId, cancellationToken);
        // A rejected quotation of this version also counts: generation never answers a customer's rejection by itself.
        var sameVersion = existing.Where(q => q.OnSiteSurveyVersion == basis.Version && q.Status != QuotationStatuses.Superseded)
            .OrderByDescending(q => q.Version).FirstOrDefault();
        if (sameVersion is not null) return Result(AutomaticQuotationOutcomes.AlreadyGenerated, await ViewAsync(sameVersion, true, cancellationToken));
        if (existing.Any(q => q.Status != QuotationStatuses.Superseded)) return Result(AutomaticQuotationOutcomes.SurveyChangedReviewRequired);

        // 2. What the technician decided.
        var bom = OnSiteSurveyHandlers.ParseBillOfMaterials(basis.BillOfMaterialsJson);
        if (bom is null || (bom.Materials ?? []).Count == 0) return Result(AutomaticQuotationOutcomes.ManualQuotationRequired);
        if (basis.InstallationFeasibility == "NOT_FEASIBLE") return Result(AutomaticQuotationOutcomes.NotFeasible);
        if (source.AssignedSaleId is not { } salesUserId) return Result(AutomaticQuotationOutcomes.SalesOwnerRequired);

        // 3. Price everything from the catalog.
        var issues = new List<PricingIssue>();
        var materials = new List<(PricedItem Item, CatalogProductSnapshot? Product)>();
        foreach (var item in bom.Materials ?? []) materials.Add(await PriceAsync(item, expectService: false, issues, cancellationToken));
        var services = new List<(PricedItem Item, CatalogProductSnapshot? Product)>();
        foreach (var item in bom.ServiceItems ?? []) services.Add(await PriceAsync(item, expectService: true, issues, cancellationToken));

        // A standard grid-connected installation needs at least one panel, one inverter and the installation labor; anything else
        // (hybrid systems, unusual work) is a case for Sales instead of a guess.
        // "Listed" is checked on the item's catalog type even when the item could not be priced (its price issue is already recorded).
        if (!materials.Any(m => IsListed(m.Item, ProductTypes.SolarPanel)))
            issues.Add(new(PricingIssueCodes.MissingSolarPanel, "The bill of materials has no SOLAR_PANEL."));
        if (!materials.Any(m => IsListed(m.Item, ProductTypes.Inverter)))
            issues.Add(new(PricingIssueCodes.MissingInverter, "The bill of materials has no INVERTER."));
        if (!services.Any(s => IsListed(s.Item, ProductTypes.InstallationLabor)))
            issues.Add(new(PricingIssueCodes.LaborNotSpecified, "No INSTALLATION_LABOR service item: the labor price cannot be determined."));
        if (basis.InstallationFeasibility == "FEASIBLE_WITH_CONDITIONS")
            issues.Add(new(PricingIssueCodes.ConditionsRequireReview, "The installation is feasible with conditions; Sales must review how they are priced."));

        // 4. Build the quotation from the priced items only (unpriceable items are listed in the issues, never at zero).
        var now = time.GetUtcNow();
        var quotation = new Quotation
        {
            Id = Guid.NewGuid(), QuoteNumber = NewQuoteNumber(now), SurveyRequestId = surveyRequestId, OnSiteSurveyReportId = basis.ReportId,
            OnSiteSurveyVersion = basis.Version, Version = 1, Status = QuotationStatuses.Draft, CustomerUserId = source.CustomerUserId,
            SalesUserId = salesUserId, Currency = QuotationPricing.Currency, Origin = QuotationOrigins.Automatic,
            LaborCost = ServiceTotal(services, ProductTypes.InstallationLabor), TransportCost = ServiceTotal(services, ProductTypes.Transport),
            OtherServiceCost = ServiceTotal(services, ProductTypes.OtherService), DiscountAmount = 0m, TaxRatePercent = options.DefaultTaxRatePercent,
            ValidUntil = now.AddDays(Math.Clamp(options.AutomaticValidityDays, 1, options.MaxValidityDays)), Terms = options.AutomaticTerms,
            CreatedAt = now, UpdatedAt = now,
        };
        var lines = materials.Where(m => m.Item.LineTotal is not null).Select((m, i) => new QuotationLine
        {
            Id = Guid.NewGuid(), QuotationId = quotation.Id, LineNumber = i + 1, ProductId = m.Product!.ProductId, Sku = m.Product.Sku,
            Name = m.Product.Name, Brand = m.Product.Brand, Model = m.Product.Model, Unit = m.Product.Unit, Quantity = m.Item.Quantity,
            UnitPrice = m.Product.UnitPrice, CatalogUnitPrice = m.Product.UnitPrice, LineTotal = m.Item.LineTotal!.Value, Description = "MATERIAL",
        }).ToList();
        Recalculate(quotation, lines);
        if (quotation.TotalAmount <= 0 || quotation.TotalAmount > QuotationPricing.MaxAmount)
            issues.Add(new(PricingIssueCodes.InvalidTotal, "The calculated total is not a positive amount within the supported range."));

        // Installed capacity (kWp) of the confirmed panels: Σ rated W × quantity / 1000. A technical reference, not a price input.
        var panels = materials.Where(m => IsType(m.Product, ProductTypes.SolarPanel) && m.Product!.RatedPowerW is not null).ToList();
        decimal? kwp = panels.Count == 0 ? null : Math.Round(panels.Sum(m => m.Product!.RatedPowerW!.Value * m.Item.Quantity) / 1000m, 3);
        quotation.PricingBasisJson = JsonSerializer.Serialize(new PricingBasis(basis.Version, basis.SelectedSimulationId, kwp, basis.InstallationFeasibility,
            bom.InstallationNotes, materials.Select(m => m.Item).ToList(), services.Select(s => s.Item).ToList(), issues, now), BasisJson);

        // 5. Issue automatically only when nothing needs a human decision.
        var issueNow = issues.Count == 0;
        if (issueNow)
        {
            quotation.Status = QuotationStatuses.Issued;
            quotation.IssuedAt = now;
            quotation.CustomerSnapshot = CustomerSnapshotOf(source);
        }
        try
        {
            var created = await unitOfWork.ExecuteInTransactionAsync<bool>(async token =>
            {
                if (await RequestClosedUnderLockAsync(surveyRequestId, token)) return (false, false);
                unitOfWork.AddQuotation(quotation);
                foreach (var line in lines) unitOfWork.AddLine(line);
                AddEvent(quotation.Id, QuotationEventTypes.AutomaticallyGenerated, actorUserId,
                    $"survey v{basis.Version}; {lines.Count} material line(s); {issues.Count} issue(s)", now);
                if (issueNow) AddEvent(quotation.Id, QuotationEventTypes.Issued, actorUserId, $"automatic; total={quotation.TotalAmount} {quotation.Currency}", now);
                await unitOfWork.SaveChangesAsync(token);
                return (true, true);
            }, cancellationToken);
            if (!created) return Result(AutomaticQuotationOutcomes.RequestClosed);
        }
        catch (PersistenceConflictException)
        {
            // A concurrent generation for the same request won (one-draft / one-live indexes): report its quotation.
            var winner = (await unitOfWork.ListBySurveyRequestAsync(surveyRequestId, cancellationToken))
                .FirstOrDefault(q => q.OnSiteSurveyVersion == basis.Version && q.Status != QuotationStatuses.Superseded);
            return Result(AutomaticQuotationOutcomes.AlreadyGenerated, winner is null ? null : await ViewAsync(winner, true, cancellationToken));
        }

        var view = await ViewAsync(quotation, true, cancellationToken);
        return new AutomaticQuotationResult(issueNow ? AutomaticQuotationOutcomes.Issued : AutomaticQuotationOutcomes.PendingSalesReview, view, issues);
    }

    /// <summary>Sales owner (or Manager) retries the automatic step, e.g. after claiming the request or fixing the catalog.</summary>
    public async Task<OperationResult<AutomaticQuotationResult>> GenerateForStaffAsync(Guid staffUserId, bool isManager, Guid surveyRequestId, CancellationToken cancellationToken)
    {
        var source = await unitOfWork.GetSourceAsync(surveyRequestId, cancellationToken);
        if (source is null || (!isManager && source.AssignedSaleId != staffUserId))
            return OperationResult<AutomaticQuotationResult>.Fail(QuotationErrorCodes.NotFound, "Survey request was not found.");
        return OperationResult<AutomaticQuotationResult>.Ok(await GenerateStandardQuotationAsync(surveyRequestId, staffUserId, cancellationToken));
    }

    /// <summary>
    /// The Sales review queue of the caller's requests (all requests for a Manager): AUTOMATIC drafts with pricing issues, and every
    /// quotation whose on-site survey was amended after it was made (<c>surveyChanged = true</c>). Derived from the data, so a retried
    /// amendment never produces a second entry.
    /// </summary>
    public async Task<IReadOnlyList<QuotationView>> ListPendingReviewAsync(Guid staffUserId, bool isManager, CancellationToken cancellationToken)
    {
        var owner = isManager ? null : (Guid?)staffUserId;
        var quotations = (await unitOfWork.ListAutomaticDraftsAsync(owner, cancellationToken))
            .Concat(await unitOfWork.ListSurveyChangedAsync(owner, cancellationToken))
            .DistinctBy(q => q.Id).OrderBy(q => q.CreatedAt);
        var list = new List<QuotationView>();
        foreach (var q in quotations)
            list.Add(await ViewAsync(q, includeEvents: true, cancellationToken));
        return list;
    }

    /// <summary>Looks up the catalog price of one item; records an issue (and returns no total) when it cannot be priced.</summary>
    private async Task<(PricedItem Item, CatalogProductSnapshot? Product)> PriceAsync(BillOfMaterialsItem item, bool expectService, List<PricingIssue> issues,
        CancellationToken cancellationToken)
    {
        var product = await unitOfWork.GetProductAsync(item.ProductId, cancellationToken);
        PricingIssue? issue = product is null || !product.IsActive
                ? new(PricingIssueCodes.ProductUnavailable, $"Product {item.ProductId} is not an active catalog product.", item.ProductId)
            : ProductTypes.IsService(product.ProductType) != expectService
                ? new(PricingIssueCodes.WrongItemKind, $"Product {product.Sku} is listed as {(expectService ? "a service" : "equipment")} but its type is {product.ProductType}.", item.ProductId)
            : product.Currency != QuotationPricing.Currency
                ? new(PricingIssueCodes.CurrencyNotSupported, $"Product {product.Sku} is priced in {product.Currency}, not VND.", item.ProductId)
            : product.UnitPrice <= 0
                ? new(PricingIssueCodes.PriceMissing, $"Product {product.Sku} has no catalog price.", item.ProductId)
            : null;
        if (issue is not null) issues.Add(issue);
        var lineTotal = issue is null ? QuotationPricing.LineTotal(item.Quantity, product!.UnitPrice) : (decimal?)null;
        return (new PricedItem(item.ProductId, product?.Sku, product?.Name, product?.ProductType, product?.Unit, item.Quantity,
            issue is null ? product!.UnitPrice : null, lineTotal), issue is null ? product : null);
    }

    private static bool IsListed(PricedItem item, string type) => item.ProductType is not null && ProductTypes.Normalize(item.ProductType) == type;

    private static bool IsType(CatalogProductSnapshot? product, string type) => product is not null && ProductTypes.Normalize(product.ProductType) == type;

    private static decimal ServiceTotal(IEnumerable<(PricedItem Item, CatalogProductSnapshot? Product)> services, string type)
        => services.Where(s => IsType(s.Product, type)).Sum(s => s.Item.LineTotal ?? 0m);

    private static AutomaticQuotationResult Result(string outcome, QuotationView? quotation = null) => new(outcome, quotation, quotation?.PricingBasis?.Issues ?? []);
}
