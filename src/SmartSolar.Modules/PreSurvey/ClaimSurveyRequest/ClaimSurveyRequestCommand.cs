namespace SmartSolar.Modules.PreSurvey.ClaimSurveyRequest;

public sealed record ClaimSurveyRequestCommand(
    Guid SurveyRequestId,
    Guid SaleUserId);