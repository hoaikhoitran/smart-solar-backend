using SmartSolar.Modules.Catalog.Constants;
using SmartSolar.Modules.Common.Notifications;
using SmartSolar.Modules.Common.Persistence;
using SmartSolar.Modules.Common.Results;

namespace SmartSolar.Modules.Quotations;

/// <summary>One unpriceable cart item found when the quotation is issued (inactive product, no catalog price, not VND).</summary>
public sealed record CartPriceProblem(Guid LineId, Guid? ProductId, string? Sku, string Problem);

/// <summary>
/// Simplified commercial flow (2026-11-17): the technician who completed the on-site survey builds the quotation like a shopping cart.
/// <list type="bullet">
/// <item>Only the technician of the request's COMPLETED survey report may build, read or issue it; anyone else gets 404.</item>
/// <item>Every line is an active VND catalog product (equipment or catalog service items); the unit price always comes from the catalog
/// and is snapshotted again when the quotation is issued. Nobody types a price, a discount or a tax rate; a product without a catalog price
/// is refused, never priced at zero.</item>
/// <item>Totals are computed by the backend: tax is the configured <see cref="QuotationOptions.DefaultTaxRatePercent"/>, no discount,
/// validity <see cref="QuotationOptions.AutomaticValidityDays"/> from issue, terms <see cref="QuotationOptions.AutomaticTerms"/>.</item>
/// <item>No installation quotation for a NOT_FEASIBLE survey. No Sales owner is needed.</item>
/// <item>Issued (finalized), accepted, rejected and superseded quotations are immutable; changes asked on site are a new version.</item>
/// <item>Issuing does not send anything to the customer: the technician presents it on site and records the outcome
/// (<see cref="RecordOnSiteDecisionAsync"/>).</item>
/// </list>
/// </summary>
public sealed partial class QuotationHandlers
{
    /// <summary>
    /// New DRAFT cart for a completed, feasible survey (or returns the technician's existing TECHNICIAN draft: idempotent). A legacy SALES /
    /// AUTOMATIC draft of the request (never shown to the customer) is closed as CANCELLED with the reason recorded, because only one draft can
    /// exist and the legacy flows are retired. An issued or accepted quotation must be revised instead.
    /// </summary>
    public async Task<OperationResult<QuotationView>> CreateCartAsync(Guid technicianUserId, Guid surveyRequestId, string? notes, CancellationToken cancellationToken)
    {
        if (notes is { Length: > 2000 }) return Invalid(new() { ["notes"] = ["At most 2000 characters."] });
        var (source, basis, error) = await AssignedSurveyAsync(technicianUserId, surveyRequestId, cancellationToken);
        if (error is not null) return error;

        var existing = await unitOfWork.ListBySurveyRequestAsync(surveyRequestId, cancellationToken);
        if (existing.FirstOrDefault(q => q.Status == QuotationStatuses.Draft && q.Origin == QuotationOrigins.Technician) is { } own)
            return OperationResult<QuotationView>.Ok(await ViewAsync(own, true, cancellationToken));
        if (existing.Any(q => q.Status is QuotationStatuses.Issued or QuotationStatuses.Accepted))
            return OperationResult<QuotationView>.Fail(QuotationErrorCodes.ActiveExists, "An issued or accepted quotation exists; create a revision of it instead.");

        var now = time.GetUtcNow();
        var quotation = NewCart(source!, basis!, technicianUserId, existing.Count == 0 ? 1 : existing.Max(q => q.Version) + 1, null, Trim(notes, 2000), now);
        var legacyDraft = existing.FirstOrDefault(q => q.Status == QuotationStatuses.Draft);
        return await PersistNewAsync(quotation, [], technicianUserId, QuotationEventTypes.Created, "technician cart", cancellationToken,
            legacyDraft is null ? null : async token =>
            {
                const string reason = "LEGACY_DRAFT_REPLACED: replaced by the technician cart quotation (Sales/automatic quotation flow retired)";
                if (!await unitOfWork.TryCloseQuotationAsync(legacyDraft.Id, QuotationStatuses.Draft, QuotationStatuses.Cancelled, technicianUserId, reason, now, token))
                    return OperationResult<bool>.Fail(QuotationErrorCodes.DraftExists, "The existing draft changed meanwhile. Reload and try again.");
                AddEvent(legacyDraft.Id, QuotationEventTypes.Cancelled, technicianUserId, reason, now);
                return OperationResult<bool>.Ok(true);
            });
    }

    /// <summary>Adds a catalog product to the cart at its current catalog price; adding a product already in the cart increases its quantity.</summary>
    public async Task<OperationResult<QuotationView>> AddCartLineAsync(Guid technicianUserId, Guid quotationId, int revision, Guid productId, decimal quantity,
        CancellationToken cancellationToken)
    {
        if (!ValidQuantity(quantity)) return InvalidQuantity();
        var product = await unitOfWork.GetProductAsync(productId, cancellationToken);
        if (PriceProblem(product) is { } problem)
            return OperationResult<QuotationView>.Fail(problem.Code, problem.Message, new { productId });
        if (WholeUnitProblem(product!.ProductType, quantity) is { } whole) return Invalid(new() { ["quantity"] = [whole] });

        return await MutateCartAsync(technicianUserId, quotationId, revision, (q, lines) =>
        {
            if (lines.FirstOrDefault(l => l.ProductId == productId) is { } same)
            {
                if (!ValidQuantity(same.Quantity + quantity)) return "The quantity of one item must be at most 100,000.";
                if (WholeUnitProblem(product.ProductType, same.Quantity + quantity) is { } wholeTotal) return wholeTotal;
                same.Quantity += quantity;
                Snapshot(same, product!);
                return null;
            }
            if (lines.Count >= options.MaxLines) return $"At most {options.MaxLines} lines are allowed.";
            var line = new QuotationLine
            {
                Id = Guid.NewGuid(), QuotationId = q.Id, LineNumber = lines.Count == 0 ? 1 : lines.Max(l => l.LineNumber) + 1, Quantity = quantity, Name = "",
                Unit = "",
            };
            Snapshot(line, product!);
            unitOfWork.AddLine(line);
            lines.Add(line);
            return null;
        }, cancellationToken);
    }

    /// <summary>Changes the quantity of a cart line (the price is re-read from the catalog).</summary>
    public async Task<OperationResult<QuotationView>> UpdateCartLineAsync(Guid technicianUserId, Guid quotationId, Guid lineId, int revision, decimal quantity,
        CancellationToken cancellationToken)
    {
        if (!ValidQuantity(quantity)) return InvalidQuantity();
        var current = (await unitOfWork.GetLinesAsync(quotationId, false, cancellationToken)).FirstOrDefault(l => l.Id == lineId);
        var product = current?.ProductId is { } productId ? await unitOfWork.GetProductAsync(productId, cancellationToken) : null;
        if (current is not null && PriceProblem(product) is { } problem)
            return OperationResult<QuotationView>.Fail(problem.Code, problem.Message, new { productId = current.ProductId });
        if (current is not null && WholeUnitProblem(product!.ProductType, quantity) is { } whole) return Invalid(new() { ["quantity"] = [whole] });

        return await MutateCartAsync(technicianUserId, quotationId, revision, (_, lines) =>
        {
            var line = lines.FirstOrDefault(l => l.Id == lineId);
            if (line is null) return "Line was not found.";
            line.Quantity = quantity;
            Snapshot(line, product!);
            return null;
        }, cancellationToken);
    }

    public Task<OperationResult<QuotationView>> RemoveCartLineAsync(Guid technicianUserId, Guid quotationId, Guid lineId, int revision, CancellationToken cancellationToken)
        => MutateCartAsync(technicianUserId, quotationId, revision, (_, lines) =>
        {
            var line = lines.FirstOrDefault(l => l.Id == lineId);
            if (line is null) return "Line was not found.";
            unitOfWork.RemoveLine(line);
            lines.Remove(line);
            return null;
        }, cancellationToken);

    /// <summary>
    /// DRAFT → ISSUED. Under the survey-request lock: the request is open, the survey still feasible, every line re-priced from the catalog
    /// (snapshot), totals recomputed. A draft made before the technician amended the survey is re-based on the current version (recorded).
    /// A revision supersedes its ISSUED / REVISION_REQUESTED predecessor (a REJECTED one keeps its outcome; an ACCEPTED one is never replaced).
    /// </summary>
    public async Task<OperationResult<QuotationView>> IssueCartAsync(Guid technicianUserId, Guid quotationId, int revision, CancellationToken cancellationToken)
    {
        var quotation = await unitOfWork.FindAsync(quotationId, false, cancellationToken);
        if (quotation is null || quotation.Origin != QuotationOrigins.Technician) return NotFound();
        var (source, basis, error) = await AssignedSurveyAsync(technicianUserId, quotation.SurveyRequestId, cancellationToken);
        if (error is not null) return error.ErrorCode == QuotationErrorCodes.NotAssignedTechnician ? NotFound() : error;
        var snapshot = CustomerSnapshotOf(source!);

        return await RunAsync(quotationId, async (q, lines, now, token) =>
        {
            if (q.Status != QuotationStatuses.Draft) return OperationResult<bool>.Fail(QuotationErrorCodes.NotDraft, "Only drafts can be issued.");
            if (q.Revision != revision) return Stale(q);
            if (IsClosedRequest((await unitOfWork.GetSourceAsync(q.SurveyRequestId, token))?.SurveyRequestStatus)) return RequestClosedError<bool>();
            if (lines.Count == 0) return OperationResult<bool>.Fail(QuotationErrorCodes.Empty, "Add at least one item before issuing.");

            // Catalog price snapshot at issue: the customer is offered today's catalog prices, frozen from now on.
            var problems = new List<CartPriceProblem>();
            foreach (var line in lines)
            {
                var product = line.ProductId is { } id ? await unitOfWork.GetProductAsync(id, token) : null;
                if (PriceProblem(product) is { } problem) problems.Add(new(line.Id, line.ProductId, line.Sku, problem.Message));
                else Snapshot(line, product!);
            }
            if (problems.Count > 0)
                return OperationResult<bool>.Fail(QuotationErrorCodes.PriceMissing, "Some items cannot be priced from the catalog; remove or replace them.", new { items = problems });
            // A draft saved before the whole-unit rule may hold fractional equipment: it cannot be issued until the quantity is corrected.
            var fractional = lines.Where(l => WholeUnitProblem(l.Description, l.Quantity) is not null)
                .Select(l => $"Line {l.LineNumber} ({l.Sku}): {WholeUnitProblem(l.Description, l.Quantity)}").ToList();
            if (fractional.Count > 0)
                return OperationResult<bool>.Fail(CommonErrorCodes.ValidationFailed, "One or more fields are invalid.",
                    new Dictionary<string, string[]> { ["quantity"] = [.. fractional] });
            q.DiscountAmount = 0m;
            q.LaborCost = q.TransportCost = q.OtherServiceCost = 0m;
            q.TaxRatePercent = options.DefaultTaxRatePercent;
            Recalculate(q, lines);
            if (q.TotalAmount <= 0 || q.TotalAmount > QuotationPricing.MaxAmount)
                return OperationResult<bool>.Fail(QuotationErrorCodes.InvalidLine, "The total amount must be positive and within the supported range.");
            // Packages are frozen in the same step as the lines: no issue with device lines frozen and package data stale.
            if (await RepricePackagesAtIssueAsync(q, lines, now, token) is { } packageError) return packageError;

            var current = await unitOfWork.GetSurveyBasisAsync(q.SurveyRequestId, token);
            if (current?.InstallationFeasibility == "NOT_FEASIBLE")
                return OperationResult<bool>.Fail(QuotationErrorCodes.SurveyNotFeasible, "The on-site survey now concludes NOT_FEASIBLE; no installation quotation can be issued.");
            if (current is not null && current.Version != q.OnSiteSurveyVersion)
            {
                AddEvent(q.Id, QuotationEventTypes.SurveyVersionConfirmed, technicianUserId, $"survey v{q.OnSiteSurveyVersion} → v{current.Version}: re-based at issue by the surveying technician", now);
                q.OnSiteSurveyVersion = current.Version;
            }

            if (q.PreviousQuotationId is { } previousId)
            {
                var previous = await unitOfWork.FindAsync(previousId, false, token);
                var superseded = previous is not null
                    && (previous.Status == QuotationStatuses.Rejected
                        || await unitOfWork.TryTransitionAsync(previousId, QuotationStatuses.RevisionRequested, QuotationStatuses.Superseded, now, token)
                        || await unitOfWork.TryTransitionAsync(previousId, QuotationStatuses.Issued, QuotationStatuses.Superseded, now, token));
                if (!superseded)
                    return OperationResult<bool>.Fail(QuotationErrorCodes.CannotRevise, "The previous version was accepted or changed meanwhile; this revision cannot be issued.");
                if (previous!.Status != QuotationStatuses.Rejected) AddEvent(previousId, QuotationEventTypes.Superseded, technicianUserId, $"by version {q.Version}", now);
            }

            q.Status = QuotationStatuses.Issued;
            q.IssuedAt = now;
            q.ValidUntil = now.AddDays(Math.Clamp(options.AutomaticValidityDays, 1, options.MaxValidityDays));
            q.Terms ??= options.AutomaticTerms;
            q.CustomerSnapshot = snapshot;
            // Finalized for presentation on the technician's device: nothing is sent to the customer's app (no approval request).
            AddEvent(q.Id, QuotationEventTypes.Issued, technicianUserId, q.ServicePackagesTotal > 0
                ? $"technician cart finalized for on-site presentation; installation={q.TotalAmount} + service packages={q.ServicePackagesTotal} = grand total {q.GrandTotal} {q.Currency}"
                : $"technician cart finalized for on-site presentation; total={q.TotalAmount} {q.Currency}", now);
            return OperationResult<bool>.Ok(true);
        }, cancellationToken, lockSurveyRequestId: quotation.SurveyRequestId);
    }

    /// <summary>
    /// New DRAFT version (changes asked on site): copied from an ISSUED, REVISION_REQUESTED or REJECTED quotation of the request (any origin, so
    /// legacy offers are answered through the cart too), based on the current survey version. Accepted (agreed) quotations are never revised here.
    /// </summary>
    public async Task<OperationResult<QuotationView>> CreateCartRevisionAsync(Guid technicianUserId, Guid quotationId, CancellationToken cancellationToken)
    {
        var original = await unitOfWork.FindAsync(quotationId, false, cancellationToken);
        if (original is null) return NotFound();
        var (source, basis, error) = await AssignedSurveyAsync(technicianUserId, original.SurveyRequestId, cancellationToken);
        if (error is not null) return error.ErrorCode == QuotationErrorCodes.NotAssignedTechnician ? NotFound() : error;
        if (original.Status is not (QuotationStatuses.Issued or QuotationStatuses.RevisionRequested or QuotationStatuses.Rejected))
            return OperationResult<QuotationView>.Fail(QuotationErrorCodes.CannotRevise,
                "Only issued, sent-back or rejected quotations can be revised; an accepted quotation is final.");
        var all = await unitOfWork.ListBySurveyRequestAsync(original.SurveyRequestId, cancellationToken);
        if (all.Any(q => q.Status == QuotationStatuses.Draft))
            return OperationResult<QuotationView>.Fail(QuotationErrorCodes.DraftExists, "A draft quotation already exists for this request.");
        if (all.Any(q => q.PreviousQuotationId == original.Id))
            return OperationResult<QuotationView>.Fail(QuotationErrorCodes.CannotRevise, "A newer version was already made from this quotation.");

        var now = time.GetUtcNow();
        var copy = NewCart(source!, basis!, technicianUserId, all.Max(q => q.Version) + 1, original, original.Notes, now);
        var lines = (await unitOfWork.GetLinesAsync(original.Id, false, cancellationToken))
            .Where(l => l.ProductId is not null)   // custom (non-catalog) lines of legacy quotations are not carried into a cart
            .OrderBy(l => l.LineNumber)
            .Select((l, i) => new QuotationLine
            {
                Id = Guid.NewGuid(), QuotationId = copy.Id, LineNumber = i + 1, ProductId = l.ProductId, Sku = l.Sku, Name = l.Name, Brand = l.Brand, Model = l.Model,
                Unit = l.Unit, Quantity = l.Quantity, UnitPrice = l.CatalogUnitPrice ?? l.UnitPrice, CatalogUnitPrice = l.CatalogUnitPrice ?? l.UnitPrice,
                LineTotal = QuotationPricing.LineTotal(l.Quantity, l.CatalogUnitPrice ?? l.UnitPrice), Description = l.Description,
            }).ToList();
        // Bundled packages are carried with their snapshot; the issue of the revision re-validates and re-prices them from the catalog.
        var packages = (await unitOfWork.GetServicePackagesAsync(original.Id, false, cancellationToken))
            .Select(p => new QuotationServicePackage
            {
                Id = Guid.NewGuid(), QuotationId = copy.Id, Kind = p.Kind, PackageId = p.PackageId, PackageName = p.PackageName, Description = p.Description,
                ImageUrl = p.ImageUrl, PricingUnit = p.PricingUnit, UnitPrice = p.UnitPrice, Quantity = p.Quantity, TotalAmount = p.TotalAmount,
                DurationMonths = p.DurationMonths, VisitMonths = p.VisitMonths, CoveredDisasterTypes = p.CoveredDisasterTypes, CoveragePercent = p.CoveragePercent,
                PerIncidentLimit = p.PerIncidentLimit, TermLimit = p.TermLimit, CatalogRevision = p.CatalogRevision, SnapshotAt = p.SnapshotAt, CreatedAt = now, UpdatedAt = now,
            }).ToList();
        return await PersistNewAsync(copy, lines, technicianUserId, QuotationEventTypes.RevisionCreated, $"technician cart from version {original.Version}", cancellationToken,
            packages: packages);
    }

    public async Task<OperationResult<QuotationView>> GetForTechnicianAsync(Guid technicianUserId, Guid quotationId, CancellationToken cancellationToken)
    {
        var quotation = await unitOfWork.FindAsync(quotationId, false, cancellationToken);
        if (quotation is null || (await unitOfWork.GetSurveyBasisAsync(quotation.SurveyRequestId, cancellationToken))?.TechnicianUserId != technicianUserId)
            return NotFound();
        return OperationResult<QuotationView>.Ok(await ViewAsync(quotation, includeEvents: true, cancellationToken));
    }

    public async Task<OperationResult<IReadOnlyList<QuotationView>>> ListForTechnicianAsync(Guid technicianUserId, Guid surveyRequestId, CancellationToken cancellationToken)
    {
        if ((await unitOfWork.GetSurveyBasisAsync(surveyRequestId, cancellationToken))?.TechnicianUserId != technicianUserId)
            return OperationResult<IReadOnlyList<QuotationView>>.Fail(QuotationErrorCodes.NotAssignedTechnician, "Survey request was not found.");
        return OperationResult<IReadOnlyList<QuotationView>>.Ok(await ViewsAsync(await unitOfWork.ListBySurveyRequestAsync(surveyRequestId, cancellationToken), false, cancellationToken));
    }

    // ------------------------------------------------------------------ internals

    /// <summary>The request and its COMPLETED survey, when <paramref name="technicianUserId"/> is the technician who surveyed it and quoting is allowed.</summary>
    private async Task<(QuotationSource? Source, QuotationSurveyBasis? Basis, OperationResult<QuotationView>? Error)> AssignedSurveyAsync(Guid technicianUserId,
        Guid surveyRequestId, CancellationToken cancellationToken)
    {
        var basis = await unitOfWork.GetSurveyBasisAsync(surveyRequestId, cancellationToken);
        var source = await unitOfWork.GetSourceAsync(surveyRequestId, cancellationToken);
        if (basis is null || source is null || basis.TechnicianUserId != technicianUserId)
            return (null, null, OperationResult<QuotationView>.Fail(QuotationErrorCodes.NotAssignedTechnician, "Survey request was not found."));
        if (IsClosedRequest(source.SurveyRequestStatus)) return (null, null, RequestClosedError<QuotationView>());
        if (basis.Status != "COMPLETED")
            return (null, null, OperationResult<QuotationView>.Fail(QuotationErrorCodes.SurveyNotCompleted, "Complete the on-site survey before building the quotation."));
        if (basis.InstallationFeasibility == "NOT_FEASIBLE")
            return (null, null, OperationResult<QuotationView>.Fail(QuotationErrorCodes.SurveyNotFeasible,
                "The on-site survey concluded the installation is NOT_FEASIBLE; no installation quotation can be made. Amend the survey if that was wrong."));
        return (source, basis, null);
    }

    private Quotation NewCart(QuotationSource source, QuotationSurveyBasis basis, Guid technicianUserId, int version, Quotation? previous, string? notes, DateTimeOffset now)
        => new()
        {
            Id = Guid.NewGuid(), QuoteNumber = previous?.QuoteNumber ?? NewQuoteNumber(now), SurveyRequestId = source.SurveyRequestId,
            OnSiteSurveyReportId = basis.ReportId, OnSiteSurveyVersion = basis.Version, Version = version, PreviousQuotationId = previous?.Id,
            Status = QuotationStatuses.Draft, CustomerUserId = source.CustomerUserId, SalesUserId = null, Currency = QuotationPricing.Currency,
            Origin = QuotationOrigins.Technician, TaxRatePercent = options.DefaultTaxRatePercent, Terms = options.AutomaticTerms, Notes = notes,
            CreatedAt = now, UpdatedAt = now,
        };

    /// <summary>Loads the technician's DRAFT cart under its row lock, applies the change, recomputes the totals and bumps the revision.</summary>
    private async Task<OperationResult<QuotationView>> MutateCartAsync(Guid technicianUserId, Guid quotationId, int revision,
        Func<Quotation, List<QuotationLine>, string?> change, CancellationToken cancellationToken)
    {
        var quotation = await unitOfWork.FindAsync(quotationId, false, cancellationToken);
        if (quotation is null || quotation.Origin != QuotationOrigins.Technician
            || (await unitOfWork.GetSurveyBasisAsync(quotation.SurveyRequestId, cancellationToken))?.TechnicianUserId != technicianUserId)
            return NotFound();

        return await RunAsync(quotationId, async (q, lines, now, token) =>
        {
            if (q.Status != QuotationStatuses.Draft)
                return OperationResult<bool>.Fail(QuotationErrorCodes.NotDraft, "Issued, accepted or closed quotations cannot be edited; create a revision.");
            if (q.Revision != revision) return Stale(q);
            if (change(q, lines) is { } error) return OperationResult<bool>.Fail(QuotationErrorCodes.InvalidLine, error);
            Recalculate(q, lines);
            // Bundled per-panel packages follow the panel count in the same transaction and revision (refused when no whole panel count remains).
            var packages = await unitOfWork.GetServicePackagesAsync(q.Id, true, token);
            if (ApplyPackageTotals(q, lines, packages) is { } packageError) return OperationResult<bool>.Fail(packageError.Code, packageError.Message);
            AddEvent(q.Id, QuotationEventTypes.Updated, technicianUserId,
                packages.Count == 0 ? null : $"service packages follow {q.PanelQuantity} panels: {string.Join(", ", packages.Select(p => $"{p.Kind} {p.TotalAmount:0} VND"))}", now);
            return OperationResult<bool>.Ok(true);
        }, cancellationToken);
    }

    /// <summary>Catalog snapshot of the product on the line: identity, unit and today's catalog unit price.</summary>
    private static void Snapshot(QuotationLine line, CatalogProductSnapshot product)
    {
        line.ProductId = product.ProductId;
        line.Sku = product.Sku;
        line.Name = Trim(product.Name, 255)!;
        line.Brand = product.Brand;
        line.Model = product.Model;
        line.Unit = Trim(product.Unit, 20)!;
        line.UnitPrice = QuotationPricing.Round(product.UnitPrice);
        line.CatalogUnitPrice = product.UnitPrice;
        line.LineTotal = QuotationPricing.LineTotal(line.Quantity, line.UnitPrice);
        line.Description = ProductTypes.Normalize(product.ProductType) is { Length: > 0 } type ? type : null;
    }

    private static (string Code, string Message)? PriceProblem(CatalogProductSnapshot? product)
        => product is null || !product.IsActive ? (QuotationErrorCodes.ProductUnavailable, "The product is not an active catalog product.")
            : product.Currency != QuotationPricing.Currency ? (QuotationErrorCodes.ProductUnavailable, $"Product {product.Sku} is not priced in VND.")
            : product.UnitPrice <= 0 ? (QuotationErrorCodes.PriceMissing, $"Product {product.Sku} has no catalog price; it cannot be quoted until the catalog has one.")
            : product.UnitPrice > QuotationPricing.MaxAmount ? (QuotationErrorCodes.PriceMissing, $"Product {product.Sku} has an unsupported catalog price.")
            : null;

    private static bool ValidQuantity(decimal quantity) => quantity > 0 && quantity <= 100_000 && decimal.Round(quantity, 3) == quantity;

    /// <summary>Why <paramref name="quantity"/> is not allowed for equipment sold by whole units (SOLAR_PANEL, INVERTER); null when it is.</summary>
    private static string? WholeUnitProblem(string? productType, decimal quantity)
        => ProductTypes.RequiresWholeQuantity(productType) && decimal.Truncate(quantity) != quantity
            ? $"{ProductTypes.Normalize(productType!)} is sold by whole units: the quantity must be a positive whole number (got {quantity})."
            : null;

    private static OperationResult<QuotationView> InvalidQuantity()
        => Invalid(new() { ["quantity"] = ["Must be greater than 0 and at most 100,000 (up to 3 decimals)."] });
}
