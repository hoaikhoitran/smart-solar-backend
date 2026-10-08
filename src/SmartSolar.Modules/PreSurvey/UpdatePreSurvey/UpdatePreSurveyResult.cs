namespace SmartSolar.Modules.PreSurvey.UpdatePreSurvey;

public enum UpdatePreSurveyOutcome
{
    Updated,
    CustomerNotFound,
    PreSurveyNotFound,
    NotOwned,
    NotEditable,
    ConcurrentlyModified
}

public sealed record UpdatePreSurveyResult(
    UpdatePreSurveyOutcome Outcome)
{
    public static UpdatePreSurveyResult Updated()
        => new(UpdatePreSurveyOutcome.Updated);

    public static UpdatePreSurveyResult CustomerNotFound()
        => new(UpdatePreSurveyOutcome.CustomerNotFound);

    public static UpdatePreSurveyResult PreSurveyNotFound()
        => new(UpdatePreSurveyOutcome.PreSurveyNotFound);

    public static UpdatePreSurveyResult NotOwned()
        => new(UpdatePreSurveyOutcome.NotOwned);

    public static UpdatePreSurveyResult NotEditable()
        => new(UpdatePreSurveyOutcome.NotEditable);

    public static UpdatePreSurveyResult ConcurrentlyModified()
        => new(UpdatePreSurveyOutcome.ConcurrentlyModified);
}