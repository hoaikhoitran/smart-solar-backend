namespace SmartSolar.Modules.PreSurvey.ClaimSurveyRequest;

public enum ClaimSurveyRequestOutcome
{
    Claimed,
    Unavailable
}

public sealed record ClaimSurveyRequestResult(
    ClaimSurveyRequestOutcome Outcome)
{
    public static ClaimSurveyRequestResult Claimed()
        => new(ClaimSurveyRequestOutcome.Claimed);

    public static ClaimSurveyRequestResult Unavailable()
        => new(ClaimSurveyRequestOutcome.Unavailable);
}