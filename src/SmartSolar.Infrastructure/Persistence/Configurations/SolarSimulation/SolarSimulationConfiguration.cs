using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartSolar.Modules.Catalog.Entities;
using SmartSolar.Modules.Identity.Entities;
using SmartSolar.Modules.PreSurvey.Entities;
using SmartSolar.Modules.SolarSimulation.Constants;
using SimulationEntity = SmartSolar.Modules.SolarSimulation.Entities.SolarSimulation;

namespace SmartSolar.Infrastructure.Persistence.Configurations.SolarSimulation;

public sealed class SolarSimulationConfiguration : IEntityTypeConfiguration<SimulationEntity>
{
    public void Configure(EntityTypeBuilder<SimulationEntity> builder)
    {
        builder.ToTable("solar_simulation", table =>
        {
            table.HasCheckConstraint("ck_solar_simulation_panel_count_non_negative", "panel_count >= 0");
            table.HasCheckConstraint("ck_solar_simulation_geometry_version_non_negative", "pre_survey_geometry_version >= 0");
            table.HasCheckConstraint("ck_solar_simulation_status",
                $"status IN ('{SimulationStatusCodes.Completed}', '{SimulationStatusCodes.PartiallyCompleted}')");
            table.HasCheckConstraint("ck_solar_simulation_energy_status",
                $"energy_status IN ('{ComponentStatusCodes.Succeeded}', '{ComponentStatusCodes.NotApplicable}', '{ComponentStatusCodes.Unavailable}', '{ComponentStatusCodes.Failed}')");
            table.HasCheckConstraint("ck_solar_simulation_climate_status",
                $"climate_status IN ('{ComponentStatusCodes.Succeeded}', '{ComponentStatusCodes.Unavailable}', '{ComponentStatusCodes.Failed}')");
            table.HasCheckConstraint("ck_solar_simulation_mounting_type",
                $"mounting_type IN ('{MountingTypes.Flush}', '{MountingTypes.Rack}')");
        });

        builder.HasKey(x => x.Id);

        // Principal key for the pre-survey's composite "selected simulation" FK.
        builder.HasAlternateKey(x => new { x.PreSurveyId, x.Id })
            .HasName("ak_solar_simulation_pre_survey_id_id");

        builder.HasIndex(x => new { x.PreSurveyId, x.CreatedAt })
            .IsDescending(false, true)
            .HasDatabaseName("ix_solar_simulation_pre_survey_id_created_at");

        // Identical reusable inputs may exist only once per pre-survey. Snapshots with a
        // failed provider are kept as history and do not block a retry.
        builder.HasIndex(x => new { x.PreSurveyId, x.InputFingerprint })
            .IsUnique()
            .HasFilter("\"is_reusable\"")
            .HasDatabaseName("ux_solar_simulation_reusable_fingerprint");

        builder.HasOne<PreSurvey>()
            .WithMany()
            .HasForeignKey(x => x.PreSurveyId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Product>()
            .WithMany()
            .HasForeignKey(x => x.ProductId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<UserAccount>()
            .WithMany()
            .HasForeignKey(x => x.CreatedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.PreSurveyId).HasColumnName("pre_survey_id").IsRequired();
        builder.Property(x => x.CreatedByUserId).HasColumnName("created_by_user_id").IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(x => x.PreSurveyGeometryVersion).HasColumnName("pre_survey_geometry_version").IsRequired();
        builder.Property(x => x.InputFingerprint).HasColumnName("input_fingerprint").HasColumnType("char(64)").HasMaxLength(64).IsFixedLength().IsRequired();
        builder.Property(x => x.IsReusable).HasColumnName("is_reusable").IsRequired();
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(30).IsRequired();
        builder.Property(x => x.EnergyStatus).HasColumnName("energy_status").HasMaxLength(30).IsRequired();
        builder.Property(x => x.ClimateStatus).HasColumnName("climate_status").HasMaxLength(30).IsRequired();
        builder.Property(x => x.AlgorithmVersion).HasColumnName("algorithm_version").HasMaxLength(30).IsRequired();

        builder.Property(x => x.ProductId).HasColumnName("product_id").IsRequired();
        builder.Property(x => x.ProductSku).HasColumnName("product_sku").HasMaxLength(50).IsRequired();
        builder.Property(x => x.ProductName).HasColumnName("product_name").HasMaxLength(255).IsRequired();
        builder.Property(x => x.ProductBrand).HasColumnName("product_brand").HasMaxLength(100).IsRequired();
        builder.Property(x => x.ProductModel).HasColumnName("product_model").HasMaxLength(100).IsRequired(false);
        builder.Property(x => x.ProductRatedPowerW).HasColumnName("product_rated_power_w").IsRequired();
        builder.Property(x => x.ProductWidthMm).HasColumnName("product_width_mm").IsRequired();
        builder.Property(x => x.ProductHeightMm).HasColumnName("product_height_mm").IsRequired();
        builder.Property(x => x.ProductInstallationSpec).HasColumnName("product_installation_spec").HasColumnType("jsonb").IsRequired(false);

        builder.Property(x => x.SurfaceLengthM).HasColumnName("surface_length_m").IsRequired();
        builder.Property(x => x.SurfaceWidthM).HasColumnName("surface_width_m").IsRequired();
        builder.Property(x => x.SurfaceTiltDegree).HasColumnName("surface_tilt_degree").IsRequired();
        builder.Property(x => x.SurfaceAzimuthDegree).HasColumnName("surface_azimuth_degree").IsRequired();
        builder.Property(x => x.Latitude).HasColumnName("latitude").IsRequired(false);
        builder.Property(x => x.Longitude).HasColumnName("longitude").IsRequired(false);

        builder.Property(x => x.MountingType).HasColumnName("mounting_type").HasMaxLength(30).IsRequired();
        builder.Property(x => x.PanelTiltDegree).HasColumnName("panel_tilt_degree").IsRequired();
        builder.Property(x => x.PanelAzimuthDegree).HasColumnName("panel_azimuth_degree").IsRequired();

        builder.Property(x => x.LayoutOrientation).HasColumnName("layout_orientation").HasMaxLength(20).IsRequired(false);
        builder.Property(x => x.PanelCount).HasColumnName("panel_count").IsRequired();
        builder.Property(x => x.InstalledCapacityKwp).HasColumnName("installed_capacity_kwp").IsRequired();

        builder.Property(x => x.GrossSurfaceAreaM2).HasColumnName("gross_surface_area_m2").IsRequired();
        builder.Property(x => x.ObstacleOccupiedAreaM2).HasColumnName("obstacle_occupied_area_m2").IsRequired();
        builder.Property(x => x.AvailableSurfaceAreaM2).HasColumnName("available_surface_area_m2").IsRequired();
        builder.Property(x => x.InstallableAreaM2).HasColumnName("installable_area_m2").IsRequired();
        builder.Property(x => x.PanelCoveredAreaM2).HasColumnName("panel_covered_area_m2").IsRequired();
        builder.Property(x => x.TotalModuleAreaM2).HasColumnName("total_module_area_m2").IsRequired();

        builder.Property(x => x.AnnualEnergyKwh).HasColumnName("annual_energy_kwh").IsRequired(false);
        builder.Property(x => x.SpecificYieldKwhPerKwpYear).HasColumnName("specific_yield_kwh_per_kwp_year").IsRequired(false);

        builder.Property(x => x.Obstacles).HasColumnName("obstacles").HasColumnType("jsonb").IsRequired();
        builder.Property(x => x.Installation).HasColumnName("installation").HasColumnType("jsonb").IsRequired();
        builder.Property(x => x.Layout).HasColumnName("layout").HasColumnType("jsonb").IsRequired();
        builder.Property(x => x.Warnings).HasColumnName("warnings").HasColumnType("jsonb").IsRequired();
        builder.Property(x => x.Energy).HasColumnName("energy").HasColumnType("jsonb").IsRequired();
        builder.Property(x => x.Climate).HasColumnName("climate").HasColumnType("jsonb").IsRequired();
    }
}
