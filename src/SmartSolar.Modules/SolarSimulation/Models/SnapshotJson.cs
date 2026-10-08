using System.Text.Json;

namespace SmartSolar.Modules.SolarSimulation.Models;

/// <summary>Serialization for the jsonb snapshot documents (camelCase, stable across reads).</summary>
public static class SnapshotJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options)
        ?? throw new InvalidOperationException($"Snapshot document could not be read as {typeof(T).Name}.");
}
