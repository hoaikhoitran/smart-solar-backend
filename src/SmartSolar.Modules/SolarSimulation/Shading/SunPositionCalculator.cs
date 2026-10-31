using SmartSolar.Modules.SolarSimulation.Geometry;

namespace SmartSolar.Modules.SolarSimulation.Shading;

/// <summary>Sun position at one instant. Elevation above the horizon and azimuth clockwise from geographic north, degrees.</summary>
public readonly record struct SunPosition(double ElevationDegree, double AzimuthDegree)
{
    public bool IsAboveHorizon => ElevationDegree > 0;

    /// <summary>Unit vector toward the sun in the world frame (X = East, Y = North, Z = Up).</summary>
    public Vec3 Direction
    {
        get
        {
            var elevation = ElevationDegree * Math.PI / 180;
            var azimuth = AzimuthDegree * Math.PI / 180;
            return new Vec3(Math.Cos(elevation) * Math.Sin(azimuth), Math.Cos(elevation) * Math.Cos(azimuth), Math.Sin(elevation));
        }
    }
}

/// <summary>
/// Solar position from latitude, longitude and a UTC instant: the NOAA solar-position equations (Meeus, "Astronomical
/// Algorithms"), a widely used equivalent of the NREL Solar Position Algorithm (SPA). NREL SPA is accurate to ±0.0003°;
/// these equations to about 0.01° between 1800 and 2100, which is far below the size of the direction bins used for
/// shading. The result is checked against the NREL SPA reference case in the unit tests.
/// <para>
/// Steps: Julian century T → mean longitude L0 and anomaly M → equation of centre C → apparent longitude λ (nutation
/// approximated through Ω) → obliquity ε → declination δ and equation of time E. The true solar time uses the real UTC
/// clock time, the longitude and E (the hour angle is NOT simply 15 × (clockHour − 12)). Refraction near the horizon uses
/// the Bennett formula of SPA for a standard atmosphere (1010 hPa, 10 °C), so it only matters for the lowest sun heights.
/// </para>
/// Azimuth convention: clockwise from geographic north (0 = N, 90 = E, 180 = S, 270 = W), the same as roof and panel
/// azimuths in SmartSolar. PVGIS "aspect" is the same angle measured from south: aspect = azimuth − 180.
/// </summary>
public static class SunPositionCalculator
{
    public static SunPosition Calculate(double latitudeDegree, double longitudeDegree, DateTimeOffset instant)
    {
        var utc = instant.ToUniversalTime();
        var julianDay = utc.ToUnixTimeMilliseconds() / 86_400_000.0 + 2_440_587.5;
        var t = (julianDay - 2_451_545.0) / 36_525.0;   // Julian centuries since J2000.0

        var meanLongitude = Normalize(280.46646 + t * (36_000.76983 + t * 0.0003032));
        var meanAnomaly = 357.52911 + t * (35_999.05029 - 0.0001537 * t);
        var eccentricity = 0.016708634 - t * (0.000042037 + 0.0000001267 * t);
        var m = Rad(meanAnomaly);
        var centre = Math.Sin(m) * (1.914602 - t * (0.004817 + 0.000014 * t))
                     + Math.Sin(2 * m) * (0.019993 - 0.000101 * t)
                     + Math.Sin(3 * m) * 0.000289;
        var omega = Rad(125.04 - 1934.136 * t);
        var apparentLongitude = meanLongitude + centre - 0.00569 - 0.00478 * Math.Sin(omega);
        var meanObliquity = 23 + (26 + (21.448 - t * (46.815 + t * (0.00059 - t * 0.001813))) / 60) / 60;
        var obliquity = Rad(meanObliquity + 0.00256 * Math.Cos(omega));

        var declination = Math.Asin(Math.Sin(obliquity) * Math.Sin(Rad(apparentLongitude)));

        // Equation of time (minutes).
        var y = Math.Pow(Math.Tan(obliquity / 2), 2);
        var l0 = Rad(meanLongitude);
        var equationOfTime = 4 * Deg(y * Math.Sin(2 * l0) - 2 * eccentricity * Math.Sin(m)
                                     + 4 * eccentricity * y * Math.Sin(m) * Math.Cos(2 * l0)
                                     - 0.5 * y * y * Math.Sin(4 * l0) - 1.25 * eccentricity * eccentricity * Math.Sin(2 * m));

        // True solar time (minutes) from the UTC clock time and the longitude (4 minutes per degree east).
        var utcMinutes = utc.TimeOfDay.TotalMinutes;
        var trueSolarMinutes = utcMinutes + equationOfTime + 4 * longitudeDegree;
        var hourAngle = Rad(trueSolarMinutes / 4 - 180);

        var latitude = Rad(latitudeDegree);
        var cosZenith = Math.Sin(latitude) * Math.Sin(declination) + Math.Cos(latitude) * Math.Cos(declination) * Math.Cos(hourAngle);
        var elevation = 90 - Deg(Math.Acos(Math.Clamp(cosZenith, -1, 1)));

        // Azimuth from north, clockwise: atan2 gives the angle from south toward west, +180 turns it to north-based.
        var azimuth = Normalize(Deg(Math.Atan2(Math.Sin(hourAngle),
            Math.Cos(hourAngle) * Math.Sin(latitude) - Math.Tan(declination) * Math.Cos(latitude))) + 180);

        return new SunPosition(elevation + Refraction(elevation), azimuth);
    }

    /// <summary>Atmospheric refraction (degrees), SPA/Bennett formula for 1010 hPa and 10 °C; zero well below the horizon.</summary>
    private static double Refraction(double elevationDegree)
    {
        if (elevationDegree < -(0.26667 + 0.5667)) return 0;
        return 1.02 / (60 * Math.Tan(Rad(elevationDegree + 10.3 / (elevationDegree + 5.11))));
    }

    private static double Rad(double degrees) => degrees * Math.PI / 180;

    private static double Deg(double radians) => radians * 180 / Math.PI;

    private static double Normalize(double degrees)
    {
        var value = degrees % 360;
        return value < 0 ? value + 360 : value;
    }
}
