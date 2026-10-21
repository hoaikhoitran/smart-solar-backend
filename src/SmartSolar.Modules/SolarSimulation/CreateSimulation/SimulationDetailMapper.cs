using SmartSolar.Modules.Catalog.Models;
using SmartSolar.Modules.SolarSimulation.Contracts.Caching;
using SmartSolar.Modules.SolarSimulation.Contracts.Persistence;
using SmartSolar.Modules.SolarSimulation.Contracts.Storage;
using SmartSolar.Modules.SolarSimulation.Entities;
using SmartSolar.Modules.SolarSimulation.Geometry;
using SmartSolar.Modules.SolarSimulation.Models;

namespace SmartSolar.Modules.SolarSimulation.CreateSimulation;

public static class SimulationDetailMapper
{
    private const string AreaNote =
        "Computed on the surface plane from the saved surface and obstacles. Customer-declared areas are separate and never used here.";

    public static ComputedAreasView Areas(LayoutAreas areas, Func<double, decimal> round) => new(
        round(areas.GrossSurfaceAreaM2), round(areas.ObstacleOccupiedAreaM2), round(areas.AvailableSurfaceAreaM2),
        round(areas.InstallableAreaM2), round(areas.PanelCoveredAreaM2), round(areas.TotalModuleAreaM2), AreaNote);

    /// <summary>
    /// Full payload of a freshly computed result, with neutral view flags (IsStale = false,
    /// IsSelected = false); <see cref="ForViewer"/> sets them per read. Product, surface and
    /// installation values are copied in, so later Catalog or pre-survey changes never alter it.
    /// </summary>
    public static SimulationDetail ToDetail(
        ComputedSimulation computed, Guid simulationId, Guid preSurveyId, int geometryVersion, DateTimeOffset createdAt,
        ProductDto product, decimal length, decimal width, decimal surfaceTilt, decimal surfaceAzimuth,
        decimal? latitude, decimal? longitude, string mountingType, decimal panelTilt, decimal panelAzimuth)
        => new(
            simulationId,
            preSurveyId,
            computed.Status,
            IsStale: false,
            IsSelected: false,
            geometryVersion,
            Constants.AlgorithmVersions.Current,
            createdAt,
            EngineeringReviewRequired: true,
            new ProductSnapshotView(product.Id, product.Sku, product.Name, product.Brand, product.Model,
                product.RatedPowerW!.Value, product.WidthMm!.Value, product.HeightMm!.Value),
            new SurfaceSnapshotView(length, width, surfaceTilt, surfaceAzimuth, latitude, longitude, computed.Obstacles),
            new MountingView(mountingType, panelTilt, panelAzimuth,
                Math.Round((decimal)AzimuthConvention.ToPvgisAspect((double)panelAzimuth), 2)),
            computed.Installation,
            new LayoutView(computed.PanelCount, computed.InstalledCapacityKwp, computed.Areas, computed.Layout),
            computed.Energy,
            computed.Climate,
            computed.Warnings,
            SimulationLimitations.For(computed.Installation.ShadingWindow));

    /// <summary>A cached result as one viewer sees it: staleness and selection against the current pre-survey.</summary>
    public static SimulationDetail ForViewer(CachedSimulation cached, SimulationSource source)
        => cached.Detail with
        {
            IsStale = cached.GeometryVersion != source.GeometryVersion,
            IsSelected = source.SelectedSimulationId == cached.SimulationId,
            ExpiresAt = cached.ExpiresAt,
            Source = SimulationSources.Cache,
        };

    public static SimulationListItem ToListItem(CachedSimulation cached, SimulationSource source)
    {
        var d = cached.Detail;
        return new SimulationListItem(
            cached.SimulationId, d.Status, d.Energy.Status, d.Climate.Status,
            cached.GeometryVersion != source.GeometryVersion,
            source.SelectedSimulationId == cached.SimulationId,
            cached.GeometryVersion, d.Mounting.Type, d.Product.Id, d.Product.Sku, d.Product.Name,
            d.Layout.PanelCount, d.Layout.InstalledCapacityKwp, d.Energy.AnnualEnergyKwh, cached.CreatedAt, cached.ExpiresAt);
    }

    /// <summary>The compact PostgreSQL row for a selection: summary columns only, never the full payload.</summary>
    public static SelectedSimulation ToSelection(CachedSimulation cached, Guid selectedByUserId, DateTimeOffset selectedAt, SimulationSnapshotLocation? snapshot)
    {
        var d = cached.Detail;
        return new SelectedSimulation
        {
            Id = Guid.NewGuid(),
            PreSurveyId = cached.PreSurveyId,
            SimulationId = cached.SimulationId,
            SelectedByUserId = selectedByUserId,
            SelectedAt = selectedAt,
            PreSurveyGeometryVersion = cached.GeometryVersion,
            AlgorithmVersion = d.AlgorithmVersion,
            Status = d.Status,
            EnergyStatus = d.Energy.Status,
            MountingType = d.Mounting.Type,
            ProductId = d.Product.Id,
            ProductSku = d.Product.Sku,
            ProductName = d.Product.Name,
            PanelCount = d.Layout.PanelCount,
            InstalledCapacityKwp = d.Layout.InstalledCapacityKwp,
            AnnualEnergyKwh = d.Energy.AnnualEnergyKwh,
            SpecificYieldKwhPerKwpYear = d.Energy.SpecificYieldKwhPerKwpYear,
            PanelTiltDegree = d.Mounting.PanelTiltDegree,
            PanelAzimuthDegree = d.Mounting.PanelAzimuthDegree,
            SnapshotBucket = snapshot?.Bucket,
            SnapshotObjectKey = snapshot?.ObjectKey,
            SnapshotSha256 = snapshot?.Sha256,
            SnapshotSizeBytes = snapshot?.SizeBytes,
            CreatedAt = cached.CreatedAt,
        };
    }

    public static SelectedSimulationView ToView(SelectedSimulation s, int currentGeometryVersion, string? snapshotStatus = null)
        => new(
            s.SimulationId, s.PreSurveyId, s.PreSurveyGeometryVersion != currentGeometryVersion, s.PreSurveyGeometryVersion,
            s.AlgorithmVersion, s.Status, s.EnergyStatus, s.MountingType, s.ProductId, s.ProductSku, s.ProductName,
            s.PanelCount, s.InstalledCapacityKwp, s.AnnualEnergyKwh, s.SpecificYieldKwhPerKwpYear,
            s.PanelTiltDegree, s.PanelAzimuthDegree, s.CreatedAt, s.SelectedAt,
            s.SnapshotObjectKey is not null,
            snapshotStatus ?? (s.SnapshotObjectKey is null ? SnapshotStatuses.None : SnapshotStatuses.Stored));
}
