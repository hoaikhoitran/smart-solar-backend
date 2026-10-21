using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartSolar.Modules.Catalog.Entities;
using SmartSolar.Modules.Catalog.ProductImages;
using SmartSolar.Modules.Identity.Entities;

namespace SmartSolar.Infrastructure.Persistence.Configurations.Catalog;

public sealed class ProductImageConfiguration
    : IEntityTypeConfiguration<ProductImage>
{
    public void Configure(EntityTypeBuilder<ProductImage> builder)
    {
        builder.ToTable("product_image", table =>
        {
            table.HasCheckConstraint("ck_product_image_sort_order_non_negative", "sort_order >= 0");
            table.HasCheckConstraint("ck_product_image_url_https", "url LIKE 'https://%'");
        });

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id)
            .HasColumnName("id");

        builder.Property(x => x.ProductId)
            .HasColumnName("product_id")
            .IsRequired();

        builder.Property(x => x.Url)
            .HasColumnName("url")
            .HasMaxLength(ProductImageRules.MaxUrlLength)
            .IsRequired();

        builder.Property(x => x.AltText)
            .HasColumnName("alt_text")
            .HasMaxLength(ProductImageRules.MaxAltTextLength)
            .IsRequired(false);

        builder.Property(x => x.SortOrder)
            .HasColumnName("sort_order")
            .IsRequired();

        builder.Property(x => x.IsPrimary)
            .HasColumnName("is_primary")
            .IsRequired();

        builder.Property(x => x.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(x => x.CreatedByUserId)
            .HasColumnName("created_by_user_id")
            .IsRequired(false);

        builder.HasIndex(x => new { x.ProductId, x.SortOrder })
            .IsUnique()
            .HasDatabaseName("ux_product_image_product_sort");

        builder.HasIndex(x => x.ProductId)
            .IsUnique()
            .HasFilter("\"is_primary\"")
            .HasDatabaseName("ux_product_image_one_primary");

        builder.HasIndex(x => new { x.ProductId, x.Url })
            .IsUnique()
            .HasDatabaseName("ux_product_image_product_url");

        builder.HasIndex(x => x.CreatedByUserId)
            .HasDatabaseName("ix_product_image_created_by_user_id");

        // Restrict, not cascade: products are soft-deleted, and images are only
        // ever removed explicitly through the image operations.
        builder.HasOne<Product>()
            .WithMany(p => p.Images)
            .HasForeignKey(x => x.ProductId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_product_image_product");

        builder.HasOne<UserAccount>()
            .WithMany()
            .HasForeignKey(x => x.CreatedByUserId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_product_image_user_account_created_by");
    }
}
