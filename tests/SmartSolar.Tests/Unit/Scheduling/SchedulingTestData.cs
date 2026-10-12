using SmartSolar.Modules.Scheduling.Domain;
using SmartSolar.Modules.Scheduling.Travel;

namespace SmartSolar.Tests.Unit.Scheduling;

/// <summary>SYNTHETIC coordinates and travel results for scheduling tests. No provider is called.</summary>
internal static class SchedulingTestData
{
    public static readonly GeoPoint ThuDuc = new(10.8494m, 106.7537m);
    public static readonly GeoPoint BinhTan = new(10.7653m, 106.6037m);
    public static readonly GeoPoint District1 = new(10.7769m, 106.7009m);
    public static readonly GeoPoint Base = new(10.8000m, 106.6600m);

    /// <summary>Local Asia/Ho_Chi_Minh wall-clock time (UTC+7, no DST) as UTC.</summary>
    public static DateTimeOffset Local(int year, int month, int day, int hour, int minute = 0)
        => new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.FromHours(7)).ToUniversalTime();

    public static TravelEstimateResult Travel(int minutes, double meters = 10_000, DateTimeOffset? departure = null)
        => new(RouteCalculationStatuses.Ok, "VIETMAP", "v4", "motorcycle", departure ?? DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            meters, TimeSpan.FromMinutes(minutes), null, null);

    public static TravelEstimateResult Failed(string status)
        => new(status, "VIETMAP", "v4", "motorcycle", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null, null, null, "synthetic failure");
}
