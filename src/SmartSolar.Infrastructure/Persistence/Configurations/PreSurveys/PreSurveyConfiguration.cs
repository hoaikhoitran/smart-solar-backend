using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartSolar.Modules.PreSurvey.Entities;
using SmartSolar.Modules.PreSurvey.Enums;
using SimulationEntity = SmartSolar.Modules.SolarSimulation.Entities.SolarSimulation;

namespace SmartSolar.Infrastructure.Persistence.Configurations.PreSurveys;

public sealed class PreSurveyConfiguration
    : IEntityTypeConfiguration<PreSurvey>
{
    private const string Draft = "DRAFT";
    private const string Submitted = "SUBMITTED";

    public void Configure(EntityTypeBuilder<PreSurvey> builder)
    {
        builder.ToTable("pre_survey", table =>
        {
            table.HasCheckConstraint(
                "ck_pre_survey_surface_dimensions_paired",
                "(surface_length_m IS NULL) = (surface_width_m IS NULL)");
            table.HasCheckConstraint(
                "ck_pre_survey_versions_non_negative",
                "geometry_version >= 0 AND revision >= 0");
        });

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

        builder.Property(x => x.SurfaceLengthM)
            .HasColumnName("surface_length_m")
            .IsRequired(false);

        builder.Property(x => x.SurfaceWidthM)
            .HasColumnName("surface_width_m")
            .IsRequired(false);

        builder.Property(x => x.Obstacles)
            .HasColumnName("obstacles")
            .HasColumnType("jsonb")
            .IsRequired(false);

        builder.Property(x => x.GeometryVersion)
            .HasColumnName("geometry_version")
            .IsRequired();

        builder.Property(x => x.Revision)
            .HasColumnName("revision")
            .IsConcurrencyToken()
            .IsRequired();

        builder.Property(x => x.SelectedSimulationId)
            .HasColumnName("selected_simulation_id")
            .IsRequired(false);

        // The selected simulation must belong to this same pre-survey: a composite FK
        // (id, selected_simulation_id) -> solar_simulation (pre_survey_id, id).
        // A null selection skips the check (MATCH SIMPLE).
        builder.HasOne<SimulationEntity>()
            .WithMany()
            .HasForeignKey(x => new { x.Id, x.SelectedSimulationId })
            .HasPrincipalKey(x => new { x.PreSurveyId, x.Id })
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_pre_survey_selected_simulation_same_pre_survey");

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