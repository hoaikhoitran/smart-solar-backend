using System.Text.Json;

namespace SmartSolar.Modules.PreSurvey.Surface;

/// <summary>Stores obstacles as a camelCase JSON array in pre_survey.obstacles (jsonb).</summary>
public static class SurfaceObstacleSerializer
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize(IReadOnlyList<SurfaceObstacle> obstacles)
        => JsonSerializer.Serialize(obstacles, Options);

    /// <summary>
    /// Structural comparison of stored obstacles with a new list. The stored text must not be
    /// compared directly: PostgreSQL jsonb reorders keys, drops whitespace and keeps the numeric
    /// scale it was given, so identical data can come back as different text.
    /// Order is significant (obstacles are kept and returned in the order drawn), names are
    /// compared ordinally, and decimals by value (2 equals 2.0). No stored surface (null) never
    /// equals a list, even an empty one.
    /// </summary>
    public static bool AreEquivalent(string? storedJson, IReadOnlyList<SurfaceObstacle> obstacles)
    {
        var stored = Deserialize(storedJson);
        return stored is not null && stored.SequenceEqual(obstacles);
    }

    /// <summary>Null when no surface has been saved yet.</summary>
    public static IReadOnlyList<SurfaceObstacle>? Deserialize(string? json)
        => string.IsNullOrWhiteSpace(json)
            ? null
            : JsonSerializer.Deserialize<List<SurfaceObstacle>>(json, Options) ?? [];
}
