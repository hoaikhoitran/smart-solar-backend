namespace SmartSolar.Modules.PreSurvey.SubmitPreSurvey;

public enum SubmitPreSurveyOutcome
{
    Submitted,
    CustomerNotFound,
    PreSurveyNotFound,
    NotOwned,
    AlreadySubmitted,
    Incomplete
}

public sealed record SubmitPreSurveyResult(
    SubmitPreSurveyOutcome Outcome,
    Guid? SurveyRequestId)
{
    public static SubmitPreSurveyResult Submitted(
        Guid surveyRequestId)
        => new(
            SubmitPreSurveyOutcome.Submitted,
            surveyRequestId);

    public static SubmitPreSurveyResult CustomerNotFound()
        => new(
            SubmitPreSurveyOutcome.CustomerNotFound,
            null);

    public static SubmitPreSurveyResult PreSurveyNotFound()
        => new(
            SubmitPreSurveyOutcome.PreSurveyNotFound,
            null);

    public static SubmitPreSurveyResult NotOwned()
        => new(
            SubmitPreSurveyOutcome.NotOwned,
            null);

    public static SubmitPreSurveyResult AlreadySubmitted()
        => new(
            SubmitPreSurveyOutcome.AlreadySubmitted,
            null);

    public static SubmitPreSurveyResult Incomplete()
        => new(
            SubmitPreSurveyOutcome.Incomplete,
            null);
}