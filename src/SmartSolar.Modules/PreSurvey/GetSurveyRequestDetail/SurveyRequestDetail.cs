using SmartSolar.Modules.PreSurvey.Enums;

namespace SmartSolar.Modules.PreSurvey.GetSurveyRequestDetail;

public sealed record SurveyRequestDetail(
    Guid SurveyRequestId,
    Guid PreSurveyId,
    Guid? AssignedSaleId,

    string CustomerName,
    string? CustomerPhone,
    string? CustomerEmail,

    string PropertyName,
    string Province,
    string? District,
    string? Ward,
    string? StreetLine,

    decimal? Latitude,
    decimal? Longitude,

    InstallationSurfaceType? InstallationSurfaceType,
    string? SurfaceMaterial,

    decimal? TotalAreaM2,
    decimal? UsableAreaM2,
    decimal? TiltDegree,
    decimal? AzimuthDegree,
    bool? HasObstruction,

    SurveyRequestStatus Status,
    DateTimeOffset SubmittedAt,
    DateTimeOffset? AssignedAt,
    DateTimeOffset? ScheduledAt,

    string? SalesNote);