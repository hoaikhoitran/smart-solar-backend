namespace SmartSolar.Api.Contracts.PreSurveys;

public sealed record CreatePreSurveyRequest(
    Guid PropertySiteId,
    decimal? TotalAreaM2,
    decimal? UsableAreaM2,
    decimal? TiltDegree,
    decimal? AzimuthDegree,
    bool? HasObstruction);