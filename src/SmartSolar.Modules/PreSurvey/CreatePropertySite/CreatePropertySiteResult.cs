using SmartSolar.Modules.AdministrativeUnits;

namespace SmartSolar.Modules.PreSurvey.CreatePropertySite;

public enum CreatePropertySiteOutcome
{
    Created,
    CustomerNotFound,
    InvalidLocation
}

public sealed record CreatePropertySiteResult(
    CreatePropertySiteOutcome Outcome,
    Guid? PropertySiteId,
    LocationProblem? LocationProblem = null)
{
    public static CreatePropertySiteResult Created(Guid propertySiteId)
        => new(
            CreatePropertySiteOutcome.Created,
            propertySiteId);

    public static CreatePropertySiteResult CustomerNotFound()
        => new(
            CreatePropertySiteOutcome.CustomerNotFound,
            null);

    /// <summary>The province/commune codes are well-formed but not ACTIVE official catalog rows that belong together (422).</summary>
    public static CreatePropertySiteResult InvalidLocation(LocationProblem problem)
        => new(
            CreatePropertySiteOutcome.InvalidLocation,
            null,
            problem);
}