namespace SmartSolar.Modules.PreSurvey.UpdatePreSurvey;

public sealed record UpdatePreSurveyCommand(
    Guid UserId,
    Guid PreSurveyId,
    decimal? TotalAreaM2,
    decimal? UsableAreaM2,
    decimal? TiltDegree,
    decimal? AzimuthDegree,
    bool? HasObstruction);