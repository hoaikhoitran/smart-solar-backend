namespace SmartSolar.Modules.PreSurvey.SubmitPreSurvey;

public sealed record SubmitPreSurveyCommand(
    Guid UserId,
    Guid PreSurveyId);