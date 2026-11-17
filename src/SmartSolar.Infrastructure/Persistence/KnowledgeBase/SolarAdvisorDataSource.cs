using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.Catalog.Constants;
using SmartSolar.Modules.Catalog.Enums;
using SmartSolar.Modules.KnowledgeBase.Advisor;

namespace SmartSolar.Infrastructure.Persistence.KnowledgeBase;

/// <summary>
/// Read-only advisor data: ACTIVE, non-deleted, VND-priced catalog products (the same products every signed-in user sees through
/// <c>GET /api/products</c>, so no internal price is exposed) and the caller's own property site. No tracking, no writes.
/// </summary>
public sealed class SolarAdvisorDataSource(AppDbContext db) : ISolarAdvisorDataSource
{
    public async Task<AdvisorCatalog> GetCatalogAsync(CancellationToken cancellationToken)
    {
        var products = await db.Products.AsNoTracking()
            .Where(p => p.Status == ProductStatus.Active && p.DeletedAt == null && p.UnitPrice > 0 && p.Currency == "VND")
            .Select(p => new { p.Id, p.Sku, p.ProductType, p.Name, p.Brand, p.Unit, p.UnitPrice, p.RatedPowerW, p.WidthMm, p.HeightMm, p.WarrantyMonth, p.Spec, p.UpdatedAt })
            .ToListAsync(cancellationToken);
        var panels = products
            .Where(p => ProductTypes.Normalize(p.ProductType) == ProductTypes.SolarPanel && p.RatedPowerW > 0 && p.WidthMm > 0 && p.HeightMm > 0)
            .Select(p => new AdvisorPanel(p.Id, p.Sku, p.Name, p.Brand, p.RatedPowerW!.Value, p.WidthMm!.Value, p.HeightMm!.Value, p.UnitPrice, p.WarrantyMonth, p.UpdatedAt))
            .OrderBy(p => p.Sku, StringComparer.Ordinal).ToList();
        var inverters = products
            .Where(p => ProductTypes.Normalize(p.ProductType) == ProductTypes.Inverter)
            .Select(p =>
            {
                var (ac, dc, phase) = SolarAdvisorService.InverterSpec(p.Spec);
                return new AdvisorInverter(p.Id, p.Sku, p.Name, p.Brand, p.UnitPrice, ac, dc, phase, p.UpdatedAt);
            })
            .OrderBy(p => p.Sku, StringComparer.Ordinal).ToList();
        var services = products
            .Where(p => ProductTypes.IsService(p.ProductType))
            .Select(p => new AdvisorServiceItem(p.Id, p.Sku, p.Name, ProductTypes.Normalize(p.ProductType), p.Unit, p.UnitPrice))
            .OrderBy(p => p.Sku, StringComparer.Ordinal).ToList();
        var batteries = products.Count(p => ProductTypes.Normalize(p.ProductType).Contains("BATTERY", StringComparison.Ordinal));
        return new AdvisorCatalog(panels, inverters, services, batteries);
    }

    public async Task<AdvisorSite?> GetOwnSiteAsync(Guid userId, Guid propertySiteId, CancellationToken cancellationToken)
    {
        var site = await db.PropertySites.AsNoTracking()
            .Where(p => p.Id == propertySiteId && p.Customer.UserId == userId)
            .Select(p => new { p.Id, p.Name, p.Latitude, p.Longitude })
            .FirstOrDefaultAsync(cancellationToken);
        if (site is null) return null;
        var preSurvey = (await db.PreSurveys.AsNoTracking().Where(s => s.PropertyId == propertySiteId)
                .Select(s => new { s.Id, s.TiltDegree, s.AzimuthDegree, s.UsableAreaM2, s.GeometryVersion, s.SelectedSimulationId, s.UpdatedAt })
                .ToListAsync(cancellationToken))
            .OrderByDescending(s => s.UpdatedAt).FirstOrDefault();
        AdvisorSimulation? simulation = null;
        if (preSurvey?.SelectedSimulationId is { } selectedId)
        {
            // Only a simulation of the current geometry with a computed specific yield (a stale or failed one is never used).
            simulation = await db.SelectedSimulations.AsNoTracking()
                .Where(s => s.SimulationId == selectedId && s.PreSurveyId == preSurvey.Id && s.PreSurveyGeometryVersion == preSurvey.GeometryVersion
                            && s.SpecificYieldKwhPerKwpYear != null && s.SpecificYieldKwhPerKwpYear > 0)
                .Select(s => new AdvisorSimulation(s.SimulationId, s.SpecificYieldKwhPerKwpYear!.Value, s.InstalledCapacityKwp, s.AnnualEnergyKwh, s.CreatedAt))
                .FirstOrDefaultAsync(cancellationToken);
        }
        return new AdvisorSite(site.Id, site.Name, site.Latitude, site.Longitude, preSurvey?.TiltDegree, preSurvey?.AzimuthDegree, preSurvey?.UsableAreaM2, simulation);
    }
}
