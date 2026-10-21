using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartSolar.Modules.AdministrativeUnits;
using SmartSolar.Modules.PreSurvey.Entities;

namespace SmartSolar.Infrastructure.Persistence.Configurations.PreSurveys;

public sealed class PropertySiteConfiguration
    : IEntityTypeConfiguration<PropertySite>
{
    public void Configure(EntityTypeBuilder<PropertySite> builder)
    {
        builder.ToTable("property_site", t =>
        {
            // Official location (administrative catalog): province_code keeps its column and check; the commune requires its
            // province, and the composite FK below makes (province_code, commune_code) an existing commune of that province.
            // district_code / ward_code (3-level model) stay in the database (all NULL) until the cleanup draft; their checks are dropped.
            t.HasCheckConstraint("ck_property_site_province_code",
                "province_code IS NULL OR (province_code NOT LIKE '%.%' AND length(province_code) BETWEEN 1 AND 3)");
            t.HasCheckConstraint("ck_property_site_commune_code", "commune_code IS NULL OR (province_code IS NOT NULL AND length(commune_code) = 5)");
        });

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id)
            .HasColumnName("id");

        builder.Property(x => x.CustomerId)
            .HasColumnName("customer_id")
            .IsRequired();

        builder.Property(x => x.Name)
            .HasColumnName("name")
            .HasMaxLength(150)
            .IsRequired();

        builder.Property(x => x.Province)
            .HasColumnName("province")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(x => x.District)
            .HasColumnName("district")
            .HasMaxLength(100)
            .IsRequired(false);

        builder.Property(x => x.Ward)
            .HasColumnName("ward")
            .HasMaxLength(100)
            .IsRequired(false);

        builder.Property(x => x.StreetLine)
            .HasColumnName("street_line")
            .HasMaxLength(255)
            .IsRequired(false);

        builder.Property(x => x.ProvinceCode)
            .HasColumnName("province_code")
            .HasMaxLength(50)
            .IsRequired(false);

        builder.Property(x => x.CommuneCode)
            .HasColumnName("commune_code")
            .HasMaxLength(5)
            .IsRequired(false);

        builder.HasIndex(x => new { x.ProvinceCode, x.CommuneCode })
            .HasDatabaseName("ix_property_site_province_commune");

        // MATCH SIMPLE: no check while commune_code is NULL; otherwise the pair must be an official commune of that province.
        builder.HasOne<AdministrativeCommune>()
            .WithMany()
            .HasForeignKey(x => new { x.ProvinceCode, x.CommuneCode })
            .HasPrincipalKey(x => new { x.ProvinceCode, x.Code })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_property_site_administrative_commune");

        builder.Property(x => x.Latitude)
            .HasColumnName("latitude")
            .IsRequired(false);

        builder.Property(x => x.Longitude)
            .HasColumnName("longitude")
            .IsRequired(false);

        builder.Property(x => x.InstallationSurfaceType)
            .HasColumnName("installation_surface_type")
            .HasMaxLength(30)
            .HasConversion<string>()
            .IsRequired(false);

        builder.Property(x => x.SurfaceMaterial)
            .HasColumnName("surface_material")
            .HasMaxLength(100)
            .IsRequired(false);

        builder.Property(x => x.Note)
            .HasColumnName("note")
            .IsRequired(false);

        builder.HasOne(x => x.Customer)
            .WithMany(x => x.PropertySites)
            .HasForeignKey(x => x.CustomerId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}