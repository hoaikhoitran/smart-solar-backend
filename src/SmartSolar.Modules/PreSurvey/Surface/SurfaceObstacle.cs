using System.Text.Json.Serialization;

namespace SmartSolar.Modules.PreSurvey.Surface;

/// <summary>
/// Client-drawn rectangular obstacle in the surface frame. All values are meters:
/// XM/YM locate the corner nearest the surface origin (upper-left in the editor),
/// WidthM runs along X (surface width), LengthM along Y (surface length, down-slope).
/// HeightM is measured along the surface normal and is used for 3D rendering only.
/// </summary>
public sealed record SurfaceObstacle(
    string Name,
    [property: JsonPropertyName("xM")] decimal XM,
    [property: JsonPropertyName("yM")] decimal YM,
    decimal WidthM,
    decimal LengthM,
    decimal? HeightM);
