namespace SmartSolar.Modules.PreSurvey.CreatePreSurvey;

public sealed record CreatePreSurveyCommand(
    Guid UserId,
    Guid PropertySiteId,
    decimal? TotalAreaM2,
    decimal? UsableAreaM2,
    decimal? TiltDegree,
    decimal? AzimuthDegree,
    bool? HasObstruction);