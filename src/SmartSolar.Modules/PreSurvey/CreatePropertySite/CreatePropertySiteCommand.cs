using SmartSolar.Modules.PreSurvey.Enums;

namespace SmartSolar.Modules.PreSurvey.CreatePropertySite;

public sealed record CreatePropertySiteCommand(
    Guid UserId,
    string Name,
    string Province,
    string? District,
    string? Ward,
    string? StreetLine,
    decimal? Latitude,
    decimal? Longitude,
    InstallationSurfaceType? InstallationSurfaceType,
    string? SurfaceMaterial,
    string? Note);