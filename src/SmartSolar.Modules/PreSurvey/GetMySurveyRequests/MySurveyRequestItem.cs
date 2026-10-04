using SmartSolar.Modules.PreSurvey.Enums;

namespace SmartSolar.Modules.PreSurvey.GetMySurveyRequests;

public sealed record MySurveyRequestItem(
    Guid SurveyRequestId,
    Guid PreSurveyId,
    string CustomerName,
    string PropertyName,
    string Province,
    string? District,
    SurveyRequestStatus Status,
    DateTimeOffset SubmittedAt,
    DateTimeOffset? AssignedAt,
    DateTimeOffset? ScheduledAt);