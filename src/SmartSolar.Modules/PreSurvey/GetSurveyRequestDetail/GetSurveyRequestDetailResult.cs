namespace SmartSolar.Modules.PreSurvey.GetSurveyRequestDetail;

public enum GetSurveyRequestDetailOutcome
{
    Found,
    NotFound,
    NotAssignedToSale
}

public sealed record GetSurveyRequestDetailResult(
    GetSurveyRequestDetailOutcome Outcome,
    SurveyRequestDetail? Detail)
{
    public static GetSurveyRequestDetailResult Found(
        SurveyRequestDetail detail)
        => new(
            GetSurveyRequestDetailOutcome.Found,
            detail);

    public static GetSurveyRequestDetailResult NotFound()
        => new(
            GetSurveyRequestDetailOutcome.NotFound,
            null);

    public static GetSurveyRequestDetailResult NotAssignedToSale()
        => new(
            GetSurveyRequestDetailOutcome.NotAssignedToSale,
            null);
}