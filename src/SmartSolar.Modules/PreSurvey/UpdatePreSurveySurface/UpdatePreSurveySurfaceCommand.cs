using System.Text.Json.Serialization;

namespace SmartSolar.Modules.PreSurvey.UpdatePreSurveySurface;

/// <summary>Obstacle as sent by the client; meters in the surface frame.</summary>
public sealed record SurfaceObstacleInput(
    string? Name,
    [property: JsonPropertyName("xM")] decimal? XM,
    [property: JsonPropertyName("yM")] decimal? YM,
    decimal? WidthM,
    decimal? LengthM,
    decimal? HeightM);

/// <summary>
/// Replaces the rectangular installation surface of a draft pre-survey. Tilt and azimuth
/// are the surface orientation and are written to the existing tilt/azimuth fields.
/// <see cref="ExpectedRevision"/> is the revision the client last read (optimistic concurrency).
/// </summary>
public sealed record UpdatePreSurveySurfaceCommand(
    Guid UserId,
    Guid PreSurveyId,
    int? ExpectedRevision,
    decimal? SurfaceLengthM,
    decimal? SurfaceWidthM,
    decimal? SurfaceTiltDegree,
    decimal? SurfaceAzimuthDegree,
    IReadOnlyList<SurfaceObstacleInput>? Obstacles);
