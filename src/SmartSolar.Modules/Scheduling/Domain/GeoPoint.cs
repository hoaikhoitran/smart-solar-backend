namespace SmartSolar.Modules.Scheduling.Domain;

/// <summary>WGS-84 coordinate in decimal degrees.</summary>
public readonly record struct GeoPoint(decimal Latitude, decimal Longitude)
{
    /// <summary>
    /// A usable routing coordinate: inside the valid ranges and not the (0, 0) placeholder that
    /// usually means "not set". Nothing is ever substituted for an invalid coordinate.
    /// </summary>
    public bool IsValid =>
        Latitude is >= -90 and <= 90 && Longitude is >= -180 and <= 180 && !(Latitude == 0 && Longitude == 0);

    public static GeoPoint? From(decimal? latitude, decimal? longitude)
        => latitude is { } lat && longitude is { } lon && new GeoPoint(lat, lon) is { IsValid: true } point ? point : null;

    /// <summary>Great-circle distance in meters. Used only to ORDER candidates; never as a travel time.</summary>
    public double StraightLineMetersTo(GeoPoint other)
    {
        const double earthRadius = 6_371_000;
        double ToRad(decimal d) => (double)d * Math.PI / 180;
        var dLat = ToRad(other.Latitude - Latitude);
        var dLon = ToRad(other.Longitude - Longitude);
        var a = Math.Pow(Math.Sin(dLat / 2), 2) +
                Math.Cos(ToRad(Latitude)) * Math.Cos(ToRad(other.Latitude)) * Math.Pow(Math.Sin(dLon / 2), 2);
        return 2 * earthRadius * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }
}
