using SmartSolar.Modules.PreSurvey.Enums;

namespace SmartSolar.Api.Contracts.PropertySites;

public sealed record CreatePropertySiteRequest(
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