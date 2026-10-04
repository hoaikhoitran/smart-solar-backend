namespace SmartSolar.Modules.PreSurvey.CreatePropertySite;

public enum CreatePropertySiteOutcome
{
    Created,
    CustomerNotFound
}

public sealed record CreatePropertySiteResult(
    CreatePropertySiteOutcome Outcome,
    Guid? PropertySiteId)
{
    public static CreatePropertySiteResult Created(Guid propertySiteId)
        => new(
            CreatePropertySiteOutcome.Created,
            propertySiteId);

    public static CreatePropertySiteResult CustomerNotFound()
        => new(
            CreatePropertySiteOutcome.CustomerNotFound,
            null);
}