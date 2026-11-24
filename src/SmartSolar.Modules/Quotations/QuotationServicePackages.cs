using SmartSolar.Modules.Catalog.Constants;
using SmartSolar.Modules.Common.Results;

namespace SmartSolar.Modules.Quotations;

/// <summary>A bundled per-panel package of a quotation (snapshot; <see cref="TotalAmount"/> = <see cref="UnitPrice"/> × <see cref="Quantity"/>).</summary>
public sealed record QuotationServicePackageView(Guid PackageId, string Kind, string PackageName, string? Description, string? ImageUrl, string PricingUnit,
    decimal UnitPrice, int Quantity, decimal TotalAmount, int DurationMonths, IReadOnlyList<int> VisitMonths, IReadOnlyList<string> CoveredDisasterTypes,
    decimal? CoveragePercent, decimal? PerIncidentLimit, decimal? TermLimit, int CatalogRevision, DateTimeOffset SnapshotAt);

/// <summary>Kinds and pricing unit as the quotation module knows them (the after-sales module owns the catalog; the values are the same strings).</summary>
public static class QuotationPackageRules
{
    public const string Maintenance = "MAINTENANCE";
    public const string DisasterProtection = "DISASTER_PROTECTION";
    public static readonly IReadOnlyList<string> Kinds = [Maintenance, DisasterProtection];
    public const string PerPanel = "PER_PANEL";

    /// <summary>
    /// Whole physical solar panels of the lines: Σ quantity of the lines whose catalog product type is SOLAR_PANEL (every model). Null count with a
    /// problem when the sum is fractional; 0 when there is no panel line.
    /// </summary>
    public static (int Count, string? Problem) PanelCount(IEnumerable<QuotationLine> lines)
    {
        var sum = lines.Where(l => l.ProductId is not null && l.Description == ProductTypes.SolarPanel).Sum(l => l.Quantity);
        if (decimal.Truncate(sum) != sum) return (0, $"The quotation holds {sum} solar panels; a per-panel package needs a whole number of physical panels.");
        return sum > int.MaxValue ? (0, "Too many solar panels.") : ((int)sum, null);
    }

    public static IReadOnlyList<int> Months(string? value)
        => string.IsNullOrWhiteSpace(value) ? [] : value.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList();

    public static IReadOnlyList<string> List(string? value)
        => string.IsNullOrWhiteSpace(value) ? [] : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    public static QuotationServicePackageView View(QuotationServicePackage p) => new(p.PackageId, p.Kind, p.PackageName, p.Description, p.ImageUrl, p.PricingUnit,
        p.UnitPrice, p.Quantity, p.TotalAmount, p.DurationMonths, Months(p.VisitMonths), List(p.CoveredDisasterTypes), p.CoveragePercent, p.PerIncidentLimit,
        p.TermLimit, p.CatalogRevision, p.SnapshotAt);
}

/// <summary>
/// Per-panel service packages sold by the surveying technician with the installation (2026-11-25):
/// <list type="bullet">
/// <item>At most one package per kind (MAINTENANCE, DISASTER_PROTECTION) on a DRAFT; the catalog package must be active, of that kind and priced
/// PER_PANEL. The technician never types a price or a quantity.</item>
/// <item>Quantity = the whole number of SOLAR_PANEL units of the current lines, recomputed with every line change in the same transaction and revision
/// (a change that leaves a bundled package without a whole, positive panel count is refused).</item>
/// <item>Package amount = catalog unit price × panels. The catalog price is a final selling price: the quotation's tax rate is NOT applied to it again
/// (tax treatment pending Finance/Legal confirmation, see guides/per-panel-packages/BUSINESS_RULES.md).</item>
/// <item>Issue re-reads every package from the catalog under the quotation lock and freezes the snapshot; a package no longer sellable blocks the issue.</item>
/// </list>
/// </summary>
public sealed partial class QuotationHandlers
{
    /// <summary>Chooses (or atomically replaces) the package of <paramref name="kind"/> on the technician's DRAFT.</summary>
    public async Task<OperationResult<QuotationView>> SetCartServicePackageAsync(Guid technicianUserId, Guid quotationId, string kind, Guid packageId, int revision,
        CancellationToken cancellationToken)
    {
        if (!QuotationPackageRules.Kinds.Contains(kind)) return Invalid(new() { ["kind"] = [$"Use one of {string.Join(", ", QuotationPackageRules.Kinds)}."] });
        if (!await OwnsCartAsync(technicianUserId, quotationId, cancellationToken)) return NotFound();

        return await RunAsync(quotationId, async (q, lines, now, token) =>
        {
            if (q.Status != QuotationStatuses.Draft)
                return OperationResult<bool>.Fail(QuotationErrorCodes.NotDraft, "Issued, accepted or closed quotations cannot be edited; create a revision.");
            if (q.Revision != revision) return Stale(q);
            var catalog = await unitOfWork.GetServicePackageAsync(packageId, token);
            if (PackageProblem(catalog, kind) is { } problem) return OperationResult<bool>.Fail(problem.Code, problem.Message, new { packageId, kind });

            var packages = (await unitOfWork.GetServicePackagesAsync(q.Id, true, token)).ToList();
            var current = packages.FirstOrDefault(p => p.Kind == kind);
            if (current is null)
            {
                current = new QuotationServicePackage { Id = Guid.NewGuid(), QuotationId = q.Id, Kind = kind, CreatedAt = now };
                unitOfWork.AddServicePackage(current);
                packages.Add(current);
            }
            var replaced = current.PackageName is null ? null : $"{current.PackageName} ({current.UnitPrice:0} VND/panel)";
            SnapshotPackage(current, catalog!, now);
            Recalculate(q, lines);
            if (ApplyPackageTotals(q, lines, packages) is { } error) return OperationResult<bool>.Fail(error.Code, error.Message);
            AddEvent(q.Id, QuotationEventTypes.Updated, technicianUserId,
                $"service package {kind}: {current.PackageName} {current.UnitPrice:0} VND × {current.Quantity} panels = {current.TotalAmount:0} VND"
                + (replaced is null ? "" : $" (replaces {replaced})"), now);
            return OperationResult<bool>.Ok(true);
        }, cancellationToken);
    }

    /// <summary>Removes the package of <paramref name="kind"/> from the technician's DRAFT.</summary>
    public async Task<OperationResult<QuotationView>> RemoveCartServicePackageAsync(Guid technicianUserId, Guid quotationId, string kind, int revision,
        CancellationToken cancellationToken)
    {
        if (!QuotationPackageRules.Kinds.Contains(kind)) return Invalid(new() { ["kind"] = [$"Use one of {string.Join(", ", QuotationPackageRules.Kinds)}."] });
        if (!await OwnsCartAsync(technicianUserId, quotationId, cancellationToken)) return NotFound();

        return await RunAsync(quotationId, async (q, lines, now, token) =>
        {
            if (q.Status != QuotationStatuses.Draft)
                return OperationResult<bool>.Fail(QuotationErrorCodes.NotDraft, "Issued, accepted or closed quotations cannot be edited; create a revision.");
            if (q.Revision != revision) return Stale(q);
            var packages = (await unitOfWork.GetServicePackagesAsync(q.Id, true, token)).ToList();
            var current = packages.FirstOrDefault(p => p.Kind == kind);
            if (current is null)
                return OperationResult<bool>.Fail(QuotationErrorCodes.ServicePackageNotFound, $"The quotation has no {kind} package.", new { kind });
            unitOfWork.RemoveServicePackage(current);
            packages.Remove(current);
            Recalculate(q, lines);
            if (ApplyPackageTotals(q, lines, packages) is { } error) return OperationResult<bool>.Fail(error.Code, error.Message);
            AddEvent(q.Id, QuotationEventTypes.Updated, technicianUserId, $"service package {kind} removed: {current.PackageName}", now);
            return OperationResult<bool>.Ok(true);
        }, cancellationToken);
    }

    private async Task<bool> OwnsCartAsync(Guid technicianUserId, Guid quotationId, CancellationToken cancellationToken)
    {
        var quotation = await unitOfWork.FindAsync(quotationId, false, cancellationToken);
        return quotation is not null && quotation.Origin == QuotationOrigins.Technician
               && (await unitOfWork.GetSurveyBasisAsync(quotation.SurveyRequestId, cancellationToken))?.TechnicianUserId == technicianUserId;
    }

    /// <summary>Why a catalog package cannot be bundled as <paramref name="kind"/> (null = sellable).</summary>
    private static (string Code, string Message)? PackageProblem(CatalogServicePackageSnapshot? package, string kind)
        => package is null ? (QuotationErrorCodes.ServicePackageNotFound, "The service package was not found.")
            : package.Kind != kind ? (QuotationErrorCodes.ServicePackageIncompatible, $"{package.Name} is a {package.Kind} package, not {kind}.")
            : package.PricingUnit != QuotationPackageRules.PerPanel
                ? (QuotationErrorCodes.ServicePackageIncompatible, $"{package.Name} is priced {package.PricingUnit}; only PER_PANEL packages can be sold with an installation.")
            : !package.IsActive ? (QuotationErrorCodes.ServicePackageInactive, $"{package.Name} is not sold any more.")
            : package.Price <= 0 || decimal.Truncate(package.Price) != package.Price || package.Price > QuotationPricing.MaxAmount
                ? (QuotationErrorCodes.ServicePackageIncompatible, $"{package.Name} has no valid whole-VND catalog price.")
            : null;

    private static void SnapshotPackage(QuotationServicePackage target, CatalogServicePackageSnapshot package, DateTimeOffset now)
    {
        target.PackageId = package.PackageId;
        target.PackageName = package.Name;
        target.Description = package.Description;
        target.ImageUrl = package.ImageUrl;
        target.PricingUnit = package.PricingUnit;
        target.UnitPrice = package.Price;
        target.DurationMonths = package.DurationMonths;
        target.VisitMonths = package.VisitMonths;
        target.CoveredDisasterTypes = package.CoveredDisasterTypes;
        target.CoveragePercent = package.CoveragePercent;
        target.PerIncidentLimit = package.PerIncidentLimit;
        target.TermLimit = package.TermLimit;
        target.CatalogRevision = package.Revision;
        target.SnapshotAt = now;
        target.UpdatedAt = now;
    }

    /// <summary>
    /// Sets the panel quantity, each package's quantity and amount and the packages total from the current lines. Refused (nothing to keep) when a
    /// package exists without a whole, positive panel count, or when an amount leaves the supported VND range.
    /// </summary>
    private static (string Code, string Message)? ApplyPackageTotals(Quotation q, IReadOnlyList<QuotationLine> lines, IReadOnlyList<QuotationServicePackage> packages)
    {
        var (panels, problem) = QuotationPackageRules.PanelCount(lines);
        q.PanelQuantity = problem is null && panels > 0 ? panels : null;
        if (packages.Count > 0)
        {
            if (problem is not null) return (QuotationErrorCodes.InvalidPanelQuantity, problem);
            if (panels <= 0)
                return (QuotationErrorCodes.NoEligiblePanels, "A per-panel service package needs at least one solar panel in the quotation; remove the package or add panels.");
        }
        foreach (var package in packages)
        {
            package.Quantity = panels;
            package.TotalAmount = QuotationPricing.Round(package.UnitPrice * panels);
            if (package.TotalAmount > QuotationPricing.MaxAmount) return (QuotationErrorCodes.TotalOutOfRange, $"The {package.Kind} package amount exceeds the supported range.");
        }
        q.ServicePackagesTotal = packages.Sum(p => p.TotalAmount);
        if (q.TotalAmount > QuotationPricing.MaxAmount || q.GrandTotal > QuotationPricing.MaxAmount)
            return (QuotationErrorCodes.TotalOutOfRange, "The quotation total (installation + service packages) exceeds the supported range.");
        return null;
    }

    /// <summary>Issue step: every bundled package re-read from the catalog and re-snapshotted (price, benefits, revision) under the quotation lock.</summary>
    private async Task<OperationResult<bool>?> RepricePackagesAtIssueAsync(Quotation q, IReadOnlyList<QuotationLine> lines, DateTimeOffset now, CancellationToken token)
    {
        var packages = await unitOfWork.GetServicePackagesAsync(q.Id, true, token);
        var problems = new List<object>();
        (string Code, string Message)? first = null;
        foreach (var package in packages)
        {
            var catalog = await unitOfWork.GetServicePackageAsync(package.PackageId, token);
            if (PackageProblem(catalog, package.Kind) is { } problem)
            {
                first ??= problem;
                problems.Add(new { package.Kind, package.PackageId, code = problem.Code, problem = problem.Message });
            }
            else SnapshotPackage(package, catalog!, now);
        }
        if (first is { } blocking)
            return OperationResult<bool>.Fail(blocking.Code, "A bundled service package can no longer be sold; remove or replace it before issuing.", new { packages = problems });
        return ApplyPackageTotals(q, lines, packages) is { } error ? OperationResult<bool>.Fail(error.Code, error.Message) : null;
    }
}
