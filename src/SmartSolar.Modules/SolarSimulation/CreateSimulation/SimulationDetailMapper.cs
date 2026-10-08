using SmartSolar.Modules.SolarSimulation.Geometry;
using SmartSolar.Modules.SolarSimulation.Models;
using SimulationEntity = SmartSolar.Modules.SolarSimulation.Entities.SolarSimulation;

namespace SmartSolar.Modules.SolarSimulation.CreateSimulation;

public static class SimulationDetailMapper
{
    private const string AreaNote =
        "Computed on the surface plane from the saved surface and obstacles. Customer-declared areas are separate and never used here.";

    public static ComputedAreasView Areas(SimulationEntity s) => new(
        s.GrossSurfaceAreaM2, s.ObstacleOccupiedAreaM2, s.AvailableSurfaceAreaM2,
        s.InstallableAreaM2, s.PanelCoveredAreaM2, s.TotalModuleAreaM2, AreaNote);

    public static ComputedAreasView Areas(LayoutAreas areas, Func<double, decimal> round) => new(
        round(areas.GrossSurfaceAreaM2), round(areas.ObstacleOccupiedAreaM2), round(areas.AvailableSurfaceAreaM2),
        round(areas.InstallableAreaM2), round(areas.PanelCoveredAreaM2), round(areas.TotalModuleAreaM2), AreaNote);

    public static SimulationDetail ToDetail(SimulationEntity s, int currentGeometryVersion, Guid? selectedSimulationId)
    {
        var installation = SnapshotJson.Deserialize<InstallationDocument>(s.Installation);
        return new(
            s.Id,
            s.PreSurveyId,
            s.Status,
            s.PreSurveyGeometryVersion != currentGeometryVersion,
            selectedSimulationId == s.Id,
            s.PreSurveyGeometryVersion,
            s.AlgorithmVersion,
            s.CreatedAt,
            EngineeringReviewRequired: true,
            new ProductSnapshotView(s.ProductId, s.ProductSku, s.ProductName, s.ProductBrand, s.ProductModel,
                s.ProductRatedPowerW, s.ProductWidthMm, s.ProductHeightMm),
            new SurfaceSnapshotView(s.SurfaceLengthM, s.SurfaceWidthM, s.SurfaceTiltDegree, s.SurfaceAzimuthDegree,
                s.Latitude, s.Longitude, SnapshotJson.Deserialize<List<ObstacleDocument>>(s.Obstacles)),
            new MountingView(s.MountingType, s.PanelTiltDegree, s.PanelAzimuthDegree,
                Math.Round((decimal)AzimuthConvention.ToPvgisAspect((double)s.PanelAzimuthDegree), 2)),
            installation,
            new LayoutView(s.PanelCount, s.InstalledCapacityKwp, Areas(s), SnapshotJson.Deserialize<LayoutDocument>(s.Layout)),
            SnapshotJson.Deserialize<EnergyDocument>(s.Energy),
            SnapshotJson.Deserialize<ClimateDocument>(s.Climate),
            SnapshotJson.Deserialize<List<Constants.SimulationWarning>>(s.Warnings),
            SimulationLimitations.For(installation.ShadingWindow));
    }
}
