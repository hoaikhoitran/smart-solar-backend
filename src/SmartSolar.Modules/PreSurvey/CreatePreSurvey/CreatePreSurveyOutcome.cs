namespace SmartSolar.Modules.PreSurvey.CreatePreSurvey;

public enum CreatePreSurveyOutcome
{
    Created,
    CustomerNotFound,
    PropertySiteNotFound,
    PropertySiteNotOwned
}

public sealed record CreatePreSurveyResult(
    CreatePreSurveyOutcome Outcome,
    Guid? PreSurveyId)
{
    public static CreatePreSurveyResult Created(
        Guid preSurveyId)
        => new(
            CreatePreSurveyOutcome.Created,
            preSurveyId);

    public static CreatePreSurveyResult CustomerNotFound()
        => new(
            CreatePreSurveyOutcome.CustomerNotFound,
            null);

    public static CreatePreSurveyResult PropertySiteNotFound()
        => new(
            CreatePreSurveyOutcome.PropertySiteNotFound,
            null);

    public static CreatePreSurveyResult PropertySiteNotOwned()
        => new(
            CreatePreSurveyOutcome.PropertySiteNotOwned,
            null);
}