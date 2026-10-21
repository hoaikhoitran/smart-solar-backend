namespace SmartSolar.Tests.Unit.Scheduling;

/// <summary>Time helpers for pure scheduling tests.</summary>
internal static class SchedulingTestData
{
    /// <summary>Local Asia/Ho_Chi_Minh wall-clock time (UTC+7, no DST) as UTC.</summary>
    public static DateTimeOffset Local(int year, int month, int day, int hour, int minute = 0)
        => new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.FromHours(7)).ToUniversalTime();
}
