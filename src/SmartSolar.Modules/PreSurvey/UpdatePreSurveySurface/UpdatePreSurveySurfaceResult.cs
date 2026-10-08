namespace SmartSolar.Modules.PreSurvey.UpdatePreSurveySurface;

public enum UpdatePreSurveySurfaceOutcome
{
    Updated,
    CustomerNotFound,
    PreSurveyNotFound,
    NotOwned,
    NotEditable,
    ConcurrentlyModified
}

public sealed record UpdatePreSurveySurfaceResult(
    UpdatePreSurveySurfaceOutcome Outcome,
    int? Revision,
    int? GeometryVersion)
{
    public static UpdatePreSurveySurfaceResult Updated(int revision, int geometryVersion)
        => new(UpdatePreSurveySurfaceOutcome.Updated, revision, geometryVersion);

    public static UpdatePreSurveySurfaceResult Failed(UpdatePreSurveySurfaceOutcome outcome)
        => new(outcome, null, null);
}
