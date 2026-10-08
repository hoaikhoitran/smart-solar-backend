using System.Globalization;
using System.Text.Json;

namespace SmartSolar.Infrastructure.ExternalProviders;

internal static class JsonReading
{
    public static JsonElement? Path(JsonElement element, params string[] names)
    {
        var current = element;
        foreach (var name in names)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out current))
            {
                return null;
            }
        }
        return current;
    }

    /// <summary>Reads a finite number, also accepting numeric strings (PVGIS returns some losses as strings).</summary>
    public static decimal? Decimal(JsonElement? element)
    {
        if (element is not { } value) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String
            && decimal.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) return parsed;
        return null;
    }

    public static int? Int(JsonElement? element)
        => element is { ValueKind: JsonValueKind.Number } value && value.TryGetInt32(out var number) ? number : null;

    public static string? String(JsonElement? element)
        => element is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    public static string Invariant(decimal value) => (value / 1.0000000000000000000000000000m).ToString(CultureInfo.InvariantCulture);
}
