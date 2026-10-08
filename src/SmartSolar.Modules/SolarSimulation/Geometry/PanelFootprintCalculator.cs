namespace SmartSolar.Modules.SolarSimulation.Geometry;

public static class PanelFootprintCalculator
{
    private const double AlignmentToleranceDegree = 0.01;

    /// <summary>A panel whose front normal points into the surface cannot be mounted on it.</summary>
    public static bool FacesIntoSurface(SurfaceFrame surface, PanelOrientation panel)
        => panel.Normal.Dot(surface.Normal) <= 1e-9;

    /// <summary>The panel's horizontal edge has no direction on the surface (edge along the surface normal).</summary>
    public static bool HasDegenerateRowAxis(SurfaceFrame surface, PanelOrientation panel)
    {
        var w = panel.WidthAxis;
        var n = surface.Normal;
        return (w - w.Dot(n) * n).Length < 1e-6;
    }

    /// <param name="physicalWidthM">Edge length along the panel's horizontal width axis.</param>
    /// <param name="physicalLengthM">Edge length along the panel's up-slope axis.</param>
    /// <param name="thicknessM">Module thickness behind the front face.</param>
    /// <param name="lowEdgeClearanceM">Gap between the lowest point of the solid and the surface.</param>
    public static PanelFootprint Calculate(
        SurfaceFrame surface,
        PanelOrientation panel,
        double physicalWidthM,
        double physicalLengthM,
        double thicknessM,
        double lowEdgeClearanceM)
    {
        var n = surface.Normal;
        var w = panel.WidthAxis;
        var u = panel.UpAxis;
        var pn = panel.Normal;

        // Row axis: the panel's horizontal edge projected onto the surface plane.
        var rowAxisWorld = (w - w.Dot(n) * n).Normalized();
        var rowAxisLocal = surface.ToLocal(rowAxisWorld);
        var theta = Math.Atan2(rowAxisLocal.Y, rowAxisLocal.X);
        var cos = Math.Cos(theta);
        var sin = Math.Sin(theta);

        var halfW = physicalWidthM / 2;
        var halfL = physicalLengthM / 2;
        var corners = new List<Vec3>(8);
        foreach (var depth in new[] { 0.0, thicknessM })
        {
            foreach (var sw in new[] { -1.0, 1.0 })
            {
                foreach (var su in new[] { -1.0, 1.0 })
                {
                    corners.Add(sw * halfW * w + su * halfL * u - depth * pn);
                }
            }
        }

        double minS = double.MaxValue, maxS = double.MinValue, minT = double.MaxValue, maxT = double.MinValue;
        double minH = double.MaxValue, maxH = double.MinValue;
        foreach (var corner in corners)
        {
            var local = surface.ToLocal(corner);
            var s = local.X * cos + local.Y * sin;
            var t = -local.X * sin + local.Y * cos;
            minS = Math.Min(minS, s); maxS = Math.Max(maxS, s);
            minT = Math.Min(minT, t); maxT = Math.Max(maxT, t);
            var h = corner.Dot(n);
            minH = Math.Min(minH, h); maxH = Math.Max(maxH, h);
        }

        var frontCenterHeight = lowEdgeClearanceM - minH;

        // On a horizontal surface, or when the panel faces the same way as an inclined
        // surface, the projection is an exact rectangle. Otherwise it is a parallelogram
        // and the bounding box used for layout is conservative (never overlapping).
        var conservative = surface.TiltDegree > AlignmentToleranceDegree
            && AzimuthConvention.Difference(panel.AzimuthDegree, surface.AzimuthDegree) > AlignmentToleranceDegree;

        return new PanelFootprint(
            physicalWidthM,
            physicalLengthM,
            thicknessM,
            Angles.ToDegrees(theta),
            minS, minT, maxS, maxT,
            frontCenterHeight,
            lowEdgeClearanceM,
            frontCenterHeight + maxH,
            conservative,
            corners,
            panel);
    }
}
