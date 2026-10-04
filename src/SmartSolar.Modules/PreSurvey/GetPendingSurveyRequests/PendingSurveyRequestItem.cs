using SmartSolar.Modules.PreSurvey.Enums;

namespace SmartSolar.Modules.PreSurvey.GetPendingSurveyRequests;

public sealed record PendingSurveyRequestItem(
    Guid SurveyRequestId,
    Guid PreSurveyId,
    string CustomerName,
    string PropertyName,
    string Province,
    string? District,
    InstallationSurfaceType? InstallationSurfaceType,
    decimal? TotalAreaM2,
    decimal? UsableAreaM2,
    DateTimeOffset SubmittedAt);