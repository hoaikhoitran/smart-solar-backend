using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartSolar.Modules.Catalog.Entities;
using SmartSolar.Modules.Identity.Entities;
using SmartSolar.Modules.PreSurvey.Entities;
using SmartSolar.Modules.SolarSimulation.Constants;
using SmartSolar.Modules.SolarSimulation.Entities;

namespace SmartSolar.Infrastructure.Persistence.Configurations.SolarSimulation;

/// <summary>
/// Compact summary of the selected simulation, one row per pre-survey. Full results are not
/// stored in PostgreSQL (temporary cache + optional S3 snapshot).
/// </summary>
public sealed class SelectedSimulationConfiguration : IEntityTypeConfiguration<SelectedSimulation>
{
    public void Configure(EntityTypeBuilder<SelectedSimulation> builder)
    {
        builder.ToTable("selected_simulation", table =>
        {
            table.HasCheckConstraint("ck_selected_simulation_panel_count_non_negative", "panel_count >= 0");
            table.HasCheckConstraint("ck_selected_simulation_capacity_non_negative", "installed_capacity_kwp >= 0");
            table.HasCheckConstraint("ck_selected_simulation_geometry_version_non_negative", "pre_survey_geometry_version >= 0");
            table.HasCheckConstraint("ck_selected_simulation_status",
                $"status IN ('{SimulationStatusCodes.Completed}', '{SimulationStatusCodes.PartiallyCompleted}')");
            table.HasCheckConstraint("ck_selected_simulation_energy_status",
                $"energy_status IN ('{ComponentStatusCodes.Succeeded}', '{ComponentStatusCodes.NotApplicable}', '{ComponentStatusCodes.Unavailable}', '{ComponentStatusCodes.Failed}')");
            table.HasCheckConstraint("ck_selected_simulation_mounting_type",
                $"mounting_type IN ('{MountingTypes.Flush}', '{MountingTypes.Rack}')");
            // The snapshot reference is all-or-nothing, with a positive size and a lowercase hex SHA-256.
            table.HasCheckConstraint("ck_selected_simulation_snapshot_complete",
                "(snapshot_bucket IS NULL AND snapshot_object_key IS NULL AND snapshot_sha256 IS NULL AND snapshot_size_bytes IS NULL) OR " +
                "(snapshot_bucket IS NOT NULL AND snapshot_object_key IS NOT NULL AND snapshot_sha256 IS NOT NULL AND snapshot_size_bytes IS NOT NULL AND snapshot_size_bytes > 0)");
        });

        builder.HasKey(x => x.Id);

        // Principal key for the pre-survey's composite "selected simulation" FK, so the selection
        // can only ever point at this pre-survey's own summary row.
        builder.HasAlternateKey(x => new { x.PreSurveyId, x.SimulationId })
            .HasName("ak_selected_simulation_pre_survey_id_simulation_id");

        builder.HasIndex(x => x.PreSurveyId)
            .IsUnique()
            .HasDatabaseName("ux_selected_simulation_pre_survey");

        builder.HasIndex(x => x.SimulationId)
            .IsUnique()
            .HasDatabaseName("ux_selected_simulation_simulation");

        builder.HasIndex(x => x.ProductId)
            .HasDatabaseName("ix_selected_simulation_product");

        builder.HasIndex(x => x.SelectedByUserId)
            .HasDatabaseName("ix_selected_simulation_selected_by");

        builder.HasOne<PreSurvey>()
            .WithMany()
            .HasForeignKey(x => x.PreSurveyId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_selected_simulation_pre_survey");

        builder.HasOne<Product>()
            .WithMany()
            .HasForeignKey(x => x.ProductId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_selected_simulation_product");

        builder.HasOne<UserAccount>()
            .WithMany()
            .HasForeignKey(x => x.SelectedByUserId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_selected_simulation_user_account");

        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.PreSurveyId).HasColumnName("pre_survey_id").IsRequired();
        builder.Property(x => x.SimulationId).HasColumnName("simulation_id").IsRequired();
        builder.Property(x => x.SelectedByUserId).HasColumnName("selected_by_user_id").IsRequired();
        builder.Property(x => x.SelectedAt).HasColumnName("selected_at").IsRequired();
        builder.Property(x => x.PreSurveyGeometryVersion).HasColumnName("pre_survey_geometry_version").IsRequired();
        builder.Property(x => x.AlgorithmVersion).HasColumnName("algorithm_version").HasMaxLength(30).IsRequired();
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(30).IsRequired();
        builder.Property(x => x.EnergyStatus).HasColumnName("energy_status").HasMaxLength(30).IsRequired();
        builder.Property(x => x.MountingType).HasColumnName("mounting_type").HasMaxLength(30).IsRequired();

        builder.Property(x => x.ProductId).HasColumnName("product_id").IsRequired();
        builder.Property(x => x.ProductSku).HasColumnName("product_sku").HasMaxLength(50).IsRequired();
        builder.Property(x => x.ProductName).HasColumnName("product_name").HasMaxLength(255).IsRequired();

        builder.Property(x => x.PanelCount).HasColumnName("panel_count").IsRequired();
        builder.Property(x => x.InstalledCapacityKwp).HasColumnName("installed_capacity_kwp").IsRequired();
        builder.Property(x => x.AnnualEnergyKwh).HasColumnName("annual_energy_kwh").IsRequired(false);
        builder.Property(x => x.SpecificYieldKwhPerKwpYear).HasColumnName("specific_yield_kwh_per_kwp_year").IsRequired(false);
        builder.Property(x => x.PanelTiltDegree).HasColumnName("panel_tilt_degree").IsRequired();
        builder.Property(x => x.PanelAzimuthDegree).HasColumnName("panel_azimuth_degree").IsRequired();

        builder.Property(x => x.SnapshotBucket).HasColumnName("snapshot_bucket").HasMaxLength(63).IsRequired(false);
        builder.Property(x => x.SnapshotObjectKey).HasColumnName("snapshot_object_key").HasMaxLength(1024).IsRequired(false);
        builder.Property(x => x.SnapshotSha256).HasColumnName("snapshot_sha256").HasColumnType("char(64)").HasMaxLength(64).IsFixedLength().IsRequired(false);
        builder.Property(x => x.SnapshotSizeBytes).HasColumnName("snapshot_size_bytes").IsRequired(false);

        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
    }
}
