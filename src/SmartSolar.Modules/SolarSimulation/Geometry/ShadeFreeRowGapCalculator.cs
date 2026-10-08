namespace SmartSolar.Modules.SolarSimulation.Geometry;

/// <summary>
/// Preliminary estimate of the clear row gap that keeps one row's shadow off the next
/// row's footprint during the design window. Shadows are measured on the surface plane,
/// which over-estimates their reach compared with the raised next row (conservative).
/// Shading from obstacles and from row ends is not modeled.
/// </summary>
public static class ShadeFreeRowGapCalculator
{
    public static double Calculate(
        double latitudeDegree,
        SurfaceFrame surface,
        PanelFootprint footprint,
        ShadingDesignWindow window)
    {
        var n = surface.Normal;
        var theta = Angles.ToRadians(footprint.RowRotationDegree);
        var cos = Math.Cos(theta);
        var sin = Math.Sin(theta);
        double gap = 0;

        foreach (var declination in window.DeclinationsDegree)
        {
            foreach (var hourAngle in window.HourAnglesDegree())
            {
                var sun = SolarPosition.SunDirection(latitudeDegree, declination, hourAngle);
                var sunOnSurface = sun.Dot(n);
                if (sun.Z <= 1e-6 || sunOnSurface <= 1e-6) continue;

                double minT = double.MaxValue, maxT = double.MinValue;
                foreach (var corner in footprint.SolidCorners)
                {
                    var height = footprint.FrontCenterHeightM + corner.Dot(n);
                    var shadow = corner - (height / sunOnSurface) * sun;
                    var local = surface.ToLocal(shadow);
                    var t = -local.X * sin + local.Y * cos;
                    minT = Math.Min(minT, t);
                    maxT = Math.Max(maxT, t);
                }

                gap = Math.Max(gap, Math.Max(maxT - footprint.MaxT, footprint.MinT - minT));
            }
        }

        return Math.Max(0, gap);
    }
}
