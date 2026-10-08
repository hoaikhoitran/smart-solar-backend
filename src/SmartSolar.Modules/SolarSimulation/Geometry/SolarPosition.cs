namespace SmartSolar.Modules.SolarSimulation.Geometry;

/// <summary>
/// Sun direction from latitude, solar declination and hour angle (solar time), using
/// the standard spherical-astronomy relations. No refraction or equation of time: the
/// design window is defined in solar time, so neither is needed.
/// </summary>
public static class SolarPosition
{
    /// <summary>Unit vector toward the sun in the world frame (East, North, Up).</summary>
    public static Vec3 SunDirection(double latitudeDegree, double declinationDegree, double hourAngleDegree)
    {
        var phi = Angles.ToRadians(latitudeDegree);
        var delta = Angles.ToRadians(declinationDegree);
        var h = Angles.ToRadians(hourAngleDegree);

        var east = -Math.Cos(delta) * Math.Sin(h);
        var north = Math.Cos(phi) * Math.Sin(delta) - Math.Sin(phi) * Math.Cos(delta) * Math.Cos(h);
        var up = Math.Sin(phi) * Math.Sin(delta) + Math.Cos(phi) * Math.Cos(delta) * Math.Cos(h);

        return new Vec3(east, north, up).Normalized();
    }
}

/// <summary>
/// Preliminary design window for the row-shading estimate. The approved default is
/// 09:00–15:00 solar time on both solstices, sampled every 15 minutes. It is advisory:
/// it does not prove year-round shade-free operation.
/// </summary>
public sealed record ShadingDesignWindow(
    IReadOnlyList<double> DeclinationsDegree,
    double StartSolarHour,
    double EndSolarHour,
    double StepMinutes)
{
    public static ShadingDesignWindow Default { get; } = new([23.44, -23.44], 9, 15, 15);

    public IEnumerable<double> HourAnglesDegree()
    {
        var step = Math.Max(1, StepMinutes) / 60.0;
        for (var hour = StartSolarHour; hour <= EndSolarHour + 1e-9; hour += step)
        {
            yield return (hour - 12.0) * 15.0;
        }
    }
}
