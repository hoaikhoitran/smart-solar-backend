using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartSolar.Modules.PreSurvey.Entities;

namespace SmartSolar.Infrastructure.Persistence.Configurations.PreSurveys;

public sealed class PropertySiteConfiguration
    : IEntityTypeConfiguration<PropertySite>
{
    public void Configure(EntityTypeBuilder<PropertySite> builder)
    {
        builder.ToTable("property_site");

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

        builder.Property(x => x.Latitude)
            .HasColumnName("latitude")
            .IsRequired(false);

        builder.Property(x => x.Longitude)
            .HasColumnName("longitude")
            .IsRequired(false);

        builder.Property(x => x.RoofType)
            .HasColumnName("roof_type")
            .HasMaxLength(50)
            .IsRequired(false);

        builder.Property(x => x.RoofMaterial)
            .HasColumnName("roof_material")
            .HasMaxLength(50)
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