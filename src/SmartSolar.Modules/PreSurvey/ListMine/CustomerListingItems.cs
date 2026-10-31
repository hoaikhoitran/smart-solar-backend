namespace SmartSolar.Modules.PreSurvey.ListMine;

/// <summary>
/// One of the customer's property sites. <see cref="CommuneName"/> is the official ward/commune name of
/// <see cref="CommuneCode"/> (null without a code); <see cref="Latitude"/>/<see cref="Longitude"/> are the site's own coordinates.
/// </summary>
public sealed record CustomerSiteListItem(
    Guid SiteId,
    string Name,
    string Province,
    string? District,
    string? Ward,
    string? StreetLine,
    string? ProvinceCode,
    string? CommuneCode,
    string? CommuneName,
    decimal? Latitude,
    decimal? Longitude,
    string? InstallationSurfaceType,
    string? SurfaceMaterial,
    int PreSurveyCount);

/// <summary>One of the customer's pre-surveys. <see cref="Status"/> is "DRAFT" or "SUBMITTED"; send <see cref="Revision"/> with updates.</summary>
public sealed record CustomerPreSurveyListItem(
    Guid PreSurveyId,
    Guid PropertySiteId,
    string SiteName,
    string Status,
    int Revision,
    int GeometryVersion,
    decimal? TotalAreaM2,
    decimal? UsableAreaM2,
    Guid? SelectedSimulationId,
    Guid? SurveyRequestId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
