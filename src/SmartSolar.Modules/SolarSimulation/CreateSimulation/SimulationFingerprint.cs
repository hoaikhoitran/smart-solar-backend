using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartSolar.Modules.SolarSimulation.CreateSimulation;

/// <summary>
/// SHA-256 over a canonical JSON document of every input that can change a simulation's
/// result. Top-level keys are sorted and decimals are written without trailing zeros, so
/// key order and decimal scale do not change the fingerprint.
/// </summary>
public static class SimulationFingerprint
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new NormalizedDecimalConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string Compute(IReadOnlyDictionary<string, object?> inputs)
    {
        var sorted = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in inputs)
        {
            sorted[key] = value;
        }

        var json = JsonSerializer.Serialize(sorted, Options);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private sealed class NormalizedDecimalConverter : JsonConverter<decimal>
    {
        public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => reader.GetDecimal();

        public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options)
            => writer.WriteRawValue((value / 1.0000000000000000000000000000m).ToString(CultureInfo.InvariantCulture));
    }
}
