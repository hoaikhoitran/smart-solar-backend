using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartSolar.Modules.Catalog.Constants;
using SmartSolar.Modules.Catalog.Entities;
using SmartSolar.Modules.Catalog.Enums;

namespace SmartSolar.Infrastructure.Persistence.Configurations.Catalog;

public sealed class ProductConfiguration
    : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.ToTable("product");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id)
            .HasColumnName("id");

        builder.Property(x => x.Sku)
            .HasColumnName("sku")
            .HasMaxLength(50)
            .IsRequired();

        builder.HasIndex(x => x.Sku)
            .IsUnique();

        builder.Property(x => x.ProductType)
            .HasColumnName("product_type")
            .HasMaxLength(30)
            .IsRequired();

        builder.Property(x => x.Category)
            .HasColumnName("category")
            .HasMaxLength(50)
            .IsRequired(false);

        builder.Property(x => x.Name)
            .HasColumnName("name")
            .HasMaxLength(255)
            .IsRequired();

        builder.Property(x => x.Brand)
            .HasColumnName("brand")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(x => x.Model)
            .HasColumnName("model")
            .HasMaxLength(100)
            .IsRequired(false);

        builder.Property(x => x.Unit)
            .HasColumnName("unit")
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(x => x.UnitPrice)
            .HasColumnName("unit_price")
            .HasColumnType("numeric(15,2)")
            .IsRequired();

        builder.Property(x => x.Currency)
            .HasColumnName("currency")
            .HasColumnType("char(3)")
            .HasMaxLength(3)
            .IsFixedLength()
            .IsRequired();

        builder.Property(x => x.RatedPowerW)
            .HasColumnName("rated_power_w")
            .IsRequired(false);

        builder.Property(x => x.WidthMm)
            .HasColumnName("width_mm")
            .IsRequired(false);

        builder.Property(x => x.HeightMm)
            .HasColumnName("height_mm")
            .IsRequired(false);

        builder.Property(x => x.WarrantyMonth)
            .HasColumnName("warranty_month")
            .IsRequired(false);

        builder.Property(x => x.Spec)
            .HasColumnName("spec")
            .HasColumnType("jsonb")
            .IsRequired(false);

        builder.Property(x => x.Status)
            .HasColumnName("status")
            .HasMaxLength(30)
            .HasConversion(
                status => ToDatabase(status),
                value => FromDatabase(value))
            .IsRequired();

        builder.Property(x => x.ImageUrl)
            .HasColumnName("image_url")
            .HasMaxLength(1000)
            .IsRequired(false);

        builder.Property(x => x.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(x => x.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        builder.Property(x => x.DeletedAt)
            .HasColumnName("deleted_at")
            .IsRequired(false);
    }

    private static string ToDatabase(ProductStatus status) => status switch
    {
        ProductStatus.Active => ProductStatusCodes.Active,
        ProductStatus.Inactive => ProductStatusCodes.Inactive,
        _ => throw new ArgumentOutOfRangeException(
            nameof(status), status, "Unsupported product status.")
    };

    private static ProductStatus FromDatabase(string value) => value switch
    {
        ProductStatusCodes.Active => ProductStatus.Active,
        ProductStatusCodes.Inactive => ProductStatus.Inactive,
        _ => throw new ArgumentOutOfRangeException(
            nameof(value), value, "Unsupported product status.")
    };
}
