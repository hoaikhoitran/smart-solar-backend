using System.Globalization;

namespace SmartSolar.Modules.Common.Time;

/// <summary>
/// Formats an instant for a human-readable message (notification texts, user-facing errors) in the business display time zone
/// (<c>Scheduling:TimeZoneId</c>, Asia/Ho_Chi_Minh), with the UTC offset so the text is unambiguous. Stored values and API fields stay UTC;
/// only the words of a message use local wall-clock time.
/// </summary>
public static class DisplayTime
{
    /// <summary>e.g. 2026-10-20T03:00:00Z → "10:00 20/10/2026 (GMT+07:00)".</summary>
    public static string Format(DateTimeOffset instant, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(instant, zone);
        var offset = local.Offset;
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        return string.Create(CultureInfo.InvariantCulture, $"{local:HH:mm dd/MM/yyyy} (GMT{sign}{offset:hh\\:mm})");
    }
}
