
using SmartSolar.Modules.PreSurvey.Enums;

namespace SmartSolar.Modules.PreSurvey.Entities;

public sealed class PropertySite
{
    public Guid Id { get; set; }

    public Guid CustomerId { get; set; }

    public string Name { get; set; } = null!;

    public string Province { get; set; } = null!;

    public string? District { get; set; }

    public string? Ward { get; set; }

    public string? StreetLine { get; set; }

    public decimal? Latitude { get; set; }

    public decimal? Longitude { get; set; }

    public InstallationSurfaceType? InstallationSurfaceType { get; set; }

    public string? SurfaceMaterial { get; set; }

    public string? Note { get; set; }

    public Customer Customer { get; set; } = null!;
    public ICollection<PreSurvey> PreSurveys { get; set; } = [];
}