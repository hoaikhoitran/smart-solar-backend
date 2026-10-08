using SmartSolar.Modules.PreSurvey.Surface;
using SmartSolar.Modules.SolarSimulation.Constants;
using SmartSolar.Modules.SolarSimulation.Geometry;
using SmartSolar.Modules.SolarSimulation.Installation;

namespace SmartSolar.Modules.SolarSimulation.Calculation;

public sealed record SimulationGeometryInput(
    decimal SurfaceLengthM,
    decimal SurfaceWidthM,
    decimal SurfaceTiltDegree,
    decimal SurfaceAzimuthDegree,
    IReadOnlyList<SurfaceObstacle> Obstacles,
    decimal PanelWidthMm,
    decimal PanelHeightMm,
    string MountingType,
    decimal PanelTiltDegree,
    decimal PanelAzimuthDegree,
    ResolvedInstallation Installation,
    decimal? LatitudeDegree,
    ShadingDesignWindow Window,
    int OffsetSamples,
    int MaxPlacements,
    decimal RackSupportHeightWarningMm,
    long MaxLayoutWorkUnits = PanelLayoutCalculator.DefaultMaxWorkUnits);

/// <summary>One panel: its footprint on the surface and its physical 3D pose.</summary>
public sealed record SimulatedPanel(
    int Index,
    double FootprintCenterXM,
    double FootprintCenterYM,
    double FootprintWidthM,
    double FootprintDepthM,
    double RowRotationDegree,
    double PhysicalWidthM,
    double PhysicalLengthM,
    double ThicknessM,
    double FrontCenterXM,
    double FrontCenterYM,
    double FrontCenterHeightM,
    Vec3 WorldCenter,
    Quaternion Rotation,
    double LowEdgeClearanceM,
    double HighEdgeHeightM);

public sealed record SimulationLayoutResult(
    LayoutOrientation? Orientation,
    IReadOnlyList<SimulatedPanel> Placements,
    LayoutAreas Areas,
    string? NoPanelsReason,
    bool ExceededMaxPlacements,
    bool FootprintIsConservative,
    ResolvedParameter RowGap,
    decimal? ShadeEstimateRowGapMm,
    double PanelTiltDegree,
    double PanelAzimuthDegree,
    double RowRotationDegree,
    IReadOnlyList<SimulationWarning> Warnings,
    bool ExceededComputationBudget = false);

/// <summary>
/// Pure orchestration of footprint, shading estimate, row-gap resolution and layout for both
/// orientations. No I/O; deterministic for the same input.
/// </summary>
public static class SimulationCalculator
{
    public static SimulationLayoutResult Calculate(SimulationGeometryInput input)
    {
        var surface = new SurfaceFrame((double)input.SurfaceTiltDegree, (double)input.SurfaceAzimuthDegree);
        var panel = new PanelOrientation((double)input.PanelTiltDegree, (double)input.PanelAzimuthDegree);
        var isRack = input.MountingType == MountingTypes.Rack;
        var installation = input.Installation;

        var shortSide = (double)Math.Min(input.PanelWidthMm, input.PanelHeightMm) / 1000;
        var longSide = (double)Math.Max(input.PanelWidthMm, input.PanelHeightMm) / 1000;
        var thickness = (double)installation.ModuleThickness.ValueMm / 1000;
        var lowEdge = isRack ? (double)installation.RackLowEdgeClearance.ValueMm / 1000 : 0;

        var candidates = new Dictionary<LayoutOrientation, OrientationCandidate>();
        var rowGaps = new Dictionary<LayoutOrientation, (ResolvedParameter RowGap, decimal? Estimate)>();

        foreach (var orientation in new[] { LayoutOrientation.Portrait, LayoutOrientation.Landscape })
        {
            var (width, length) = orientation == LayoutOrientation.Portrait ? (shortSide, longSide) : (longSide, shortSide);
            var footprint = PanelFootprintCalculator.Calculate(surface, panel, width, length, thickness, lowEdge);

            decimal? estimateMm = null;
            if (isRack && input.LatitudeDegree is { } latitude)
            {
                var gapM = ShadeFreeRowGapCalculator.Calculate((double)latitude, surface, footprint, input.Window);
                estimateMm = Math.Round((decimal)gapM * 1000m, 0, MidpointRounding.AwayFromZero);
            }

            var rowGap = ResolveRowGap(installation.RowGap, estimateMm);
            rowGaps[orientation] = (rowGap, estimateMm);
            candidates[orientation] = new OrientationCandidate(footprint, (double)rowGap.ValueMm / 1000);
        }

        var layout = PanelLayoutCalculator.Calculate(new LayoutRequest(
            (double)input.SurfaceWidthM,
            (double)input.SurfaceLengthM,
            (double)installation.EdgeSetback.ValueMm / 1000,
            (double)installation.ObstacleClearance.ValueMm / 1000,
            (double)installation.PanelGap.ValueMm / 1000,
            input.Obstacles.Select(o => new LayoutObstacle((double)o.XM, (double)o.YM, (double)o.WidthM, (double)o.LengthM)).ToList(),
            candidates,
            input.OffsetSamples,
            input.MaxPlacements,
            input.MaxLayoutWorkUnits));

        var chosen = layout.Orientation ?? LayoutOrientation.Portrait;
        var chosenFootprint = candidates[chosen].Footprint;
        var (chosenRowGap, chosenEstimate) = rowGaps[chosen];

        var placements = layout.Placements.Select(p => ToPanel(p, chosenFootprint, surface, panel)).ToList();

        var warnings = new List<SimulationWarning>();
        if (installation.ModuleThickness.Source == InstallationSources.PreliminaryDefault)
        {
            warnings.Add(new(SimulationWarningCodes.ModuleThicknessAssumed,
                $"Module thickness is not documented for this product; a preliminary {installation.ModuleThickness.ValueMm} mm is assumed for footprint and rendering."));
        }

        if (input.Obstacles.Count > 0)
        {
            warnings.Add(new(SimulationWarningCodes.ObstacleShadingNotModeled,
                "Shading from obstacles is not modeled. Obstacle heights are used for 3D rendering only."));
        }

        if (isRack)
        {
            if (surface.TiltDegree > 0.01)
            {
                warnings.Add(new(SimulationWarningCodes.RackOnInclinedSurface,
                    "Rack mounting on an inclined surface requires engineering review of supports and attachment."));
            }

            if (chosenFootprint.IsConservativeBoundingBox)
            {
                warnings.Add(new(SimulationWarningCodes.FootprintConservativeBoundingBox,
                    "Panel and surface azimuths differ on an inclined surface; the layout uses a conservative bounding box of each panel's projection."));
            }

            if ((decimal)chosenFootprint.HighEdgeHeightM * 1000m > input.RackSupportHeightWarningMm)
            {
                warnings.Add(new(SimulationWarningCodes.RackSupportHeightAbovePreliminaryThreshold,
                    $"Panel high edge is {chosenFootprint.HighEdgeHeightM:0.000} m above the surface, above the preliminary review threshold of {input.RackSupportHeightWarningMm / 1000m:0.###} m."));
            }

            if (input.LatitudeDegree is null)
            {
                warnings.Add(new(SimulationWarningCodes.ShadeSpacingNotEvaluated,
                    "Row shading was not evaluated because the site has no coordinates."));
            }
            else if (chosenEstimate is { } estimate && chosenRowGap.Source != InstallationSources.ComputedShadeEstimate && chosenRowGap.ValueMm < estimate)
            {
                warnings.Add(new(SimulationWarningCodes.RowGapBelowShadeEstimate,
                    $"Row gap {chosenRowGap.ValueMm} mm is below the advisory shading estimate of {estimate} mm (solstices, 09:00–15:00 solar time)."));
            }
        }

        return new SimulationLayoutResult(
            layout.Orientation,
            placements,
            layout.Areas,
            layout.NoPanelsReason,
            layout.ExceededMaxPlacements,
            chosenFootprint.IsConservativeBoundingBox,
            chosenRowGap,
            chosenEstimate,
            panel.TiltDegree,
            panel.AzimuthDegree,
            chosenFootprint.RowRotationDegree,
            warnings,
            layout.ExceededComputationBudget);
    }

    private static ResolvedParameter ResolveRowGap(RowGapPolicy policy, decimal? estimateMm)
    {
        if (policy.UseShadeEstimate && estimateMm is { } estimate)
        {
            var documented = policy.DocumentedMinimumMm;
            return documented is { } min && min > estimate
                ? new ResolvedParameter(min, InstallationSources.ManufacturerDocumentedMinimum, min, policy.DocumentRef, policy.DocumentSection, policy.DocumentUrl)
                : new ResolvedParameter(estimate, InstallationSources.ComputedShadeEstimate, documented, policy.DocumentRef, policy.DocumentSection, policy.DocumentUrl);
        }

        return new ResolvedParameter(
            policy.FixedValueMm ?? 0m,
            policy.FixedSource ?? InstallationSources.PreliminaryDefault,
            policy.DocumentedMinimumMm, policy.DocumentRef, policy.DocumentSection, policy.DocumentUrl);
    }

    private static SimulatedPanel ToPanel(FootprintPlacement placement, PanelFootprint footprint, SurfaceFrame surface, PanelOrientation panel)
    {
        // The footprint box min corner sits at (footprint.MinS, footprint.MinT) relative to the
        // panel's front-face center, so the front center is offset back from the box corner.
        var front = placement.ToLocal(placement.MinS - footprint.MinS, placement.MinT - footprint.MinT);
        var center = placement.CenterLocal;
        var world = surface.ToWorld(front.X, front.Y, footprint.FrontCenterHeightM);

        return new SimulatedPanel(
            placement.Index,
            center.X,
            center.Y,
            footprint.WidthAlongRowM,
            footprint.DepthAcrossRowM,
            footprint.RowRotationDegree,
            footprint.PhysicalWidthM,
            footprint.PhysicalLengthM,
            footprint.ThicknessM,
            front.X,
            front.Y,
            footprint.FrontCenterHeightM,
            world,
            panel.ToQuaternion(),
            footprint.LowEdgeClearanceM,
            footprint.HighEdgeHeightM);
    }
}
