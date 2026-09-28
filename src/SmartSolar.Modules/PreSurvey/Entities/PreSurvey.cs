using SmartSolar.Modules.PreSurvey.Enums;

namespace SmartSolar.Modules.PreSurvey.Entities;

public sealed class PreSurvey
{
    public Guid Id { get; set; }

    public Guid PropertyId { get; set; }

    public decimal? TotalAreaM2 { get; set; }

    public decimal? UsableAreaM2 { get; set; }

    public decimal? TiltDegree { get; set; }

    public decimal? AzimuthDegree { get; set; }

    public bool? HasObstruction { get; set; }

    public PreSurveyStatus Status { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public PropertySite Property { get; set; } = null!;
}