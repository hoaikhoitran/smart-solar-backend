using System.Text.Json.Serialization;

namespace SmartSolar.Api.Contracts.PreSurveys;

/// <summary>Obstacle in meters on the surface frame (never screen pixels).</summary>
public sealed record SurfaceObstacleRequest(
    string? Name,
    [property: JsonPropertyName("xM")] decimal? XM,
    [property: JsonPropertyName("yM")] decimal? YM,
    decimal? WidthM,
    decimal? LengthM,
    decimal? HeightM);

/// <summary>
/// Full replacement of the installation surface. expectedRevision is the revision returned by
/// GET .../surface; a mismatch returns 409 PRE_SURVEY_CONCURRENTLY_MODIFIED.
/// </summary>
public sealed record UpdatePreSurveySurfaceRequest(
    int? ExpectedRevision,
    decimal? SurfaceLengthM,
    decimal? SurfaceWidthM,
    decimal? SurfaceTiltDegree,
    decimal? SurfaceAzimuthDegree,
    IReadOnlyList<SurfaceObstacleRequest>? Obstacles);

public sealed record UpdatePreSurveySurfaceResponse(int Revision, int GeometryVersion);
