
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

    /// <summary>
    /// Official administrative location (catalog codes, strings with leading zeros): the two-digit province and the five-digit
    /// commune (phường / xã / đặc khu) after the 2025 reorganisation; null = not provided. Scheduling coverage uses the commune
    /// (commune → operational zone), never the free-text display names above.
    /// </summary>
    public string? ProvinceCode { get; set; }

    public string? CommuneCode { get; set; }

    public decimal? Latitude { get; set; }

    public decimal? Longitude { get; set; }

    public InstallationSurfaceType? InstallationSurfaceType { get; set; }

    public string? SurfaceMaterial { get; set; }

    public string? Note { get; set; }

    public Customer Customer { get; set; } = null!;
    public ICollection<PreSurvey> PreSurveys { get; set; } = [];
}