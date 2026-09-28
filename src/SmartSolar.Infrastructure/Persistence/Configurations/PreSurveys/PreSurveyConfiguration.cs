using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartSolar.Modules.PreSurvey.Entities;
using SmartSolar.Modules.PreSurvey.Enums;

namespace SmartSolar.Infrastructure.Persistence.Configurations.PreSurveys;

public sealed class PreSurveyConfiguration
    : IEntityTypeConfiguration<PreSurvey>
{
    private const string Draft = "DRAFT";
    private const string Submitted = "SUBMITTED";

    public void Configure(EntityTypeBuilder<PreSurvey> builder)
    {
        builder.ToTable("pre_survey");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id)
            .HasColumnName("id");

        builder.Property(x => x.PropertyId)
            .HasColumnName("property_id")
            .IsRequired();

        builder.Property(x => x.TotalAreaM2)
            .HasColumnName("total_area_m2")
            .IsRequired(false);

        builder.Property(x => x.UsableAreaM2)
            .HasColumnName("usable_area_m2")
            .IsRequired(false);

        builder.Property(x => x.TiltDegree)
            .HasColumnName("tilt_degree")
            .IsRequired(false);

        builder.Property(x => x.AzimuthDegree)
            .HasColumnName("azimuth_degree")
            .IsRequired(false);

        builder.Property(x => x.HasObstruction)
            .HasColumnName("has_obstruction")
            .IsRequired(false);

        builder.Property(x => x.Status)
            .HasColumnName("status")
            .HasMaxLength(30)
            .HasConversion(
                status => ToDatabase(status),
                value => FromDatabase(value))
            .IsRequired();

        builder.Property(x => x.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(x => x.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        builder.HasOne(x => x.Property)
            .WithMany(x => x.PreSurveys)
            .HasForeignKey(x => x.PropertyId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static string ToDatabase(PreSurveyStatus status) => status switch
    {
        PreSurveyStatus.Draft => Draft,
        PreSurveyStatus.Submitted => Submitted,
        _ => throw new ArgumentOutOfRangeException(
            nameof(status), status, "Unsupported pre-survey status.")
    };

    private static PreSurveyStatus FromDatabase(string value) => value switch
    {
        Draft => PreSurveyStatus.Draft,
        Submitted => PreSurveyStatus.Submitted,
        _ => throw new ArgumentOutOfRangeException(
            nameof(value), value, "Unsupported pre-survey status.")
    };
}