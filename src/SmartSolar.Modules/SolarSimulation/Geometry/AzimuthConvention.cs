namespace SmartSolar.Modules.SolarSimulation.Geometry;

/// <summary>
/// SmartSolar uses compass azimuths everywhere: 0 = North, 90 = East, 180 = South,
/// 270 = West, measured clockwise when seen from above. 360 is treated as 0.
/// PVGIS 5.3 uses a south-based "aspect": 0 = South, -90 = East, 90 = West.
/// </summary>
public static class AzimuthConvention
{
    public static double NormalizeCompass(double degrees)
    {
        var normalized = degrees % 360.0;
        if (normalized < 0) normalized += 360.0;
        return Math.Abs(normalized - 360.0) < 1e-12 ? 0 : normalized;
    }

    /// <summary>PVGIS aspect = compass - 180, in [-180, 180). Due north becomes -180.</summary>
    public static double ToPvgisAspect(double compassDegrees) => NormalizeCompass(compassDegrees) - 180.0;

    /// <summary>Horizontal unit vector pointing toward the given compass bearing.</summary>
    public static Vec3 HorizontalDirection(double compassDegrees)
    {
        var radians = Angles.ToRadians(compassDegrees);
        return new Vec3(Math.Sin(radians), Math.Cos(radians), 0);
    }

    /// <summary>Smallest absolute difference between two bearings, 0..180.</summary>
    public static double Difference(double a, double b)
    {
        var d = Math.Abs(NormalizeCompass(a) - NormalizeCompass(b));
        return d > 180 ? 360 - d : d;
    }
}
