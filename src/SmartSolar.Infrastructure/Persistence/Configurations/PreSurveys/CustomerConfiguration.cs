using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartSolar.Modules.PreSurvey.Entities;

namespace SmartSolar.Infrastructure.Persistence.Configurations.PreSurveys;

public sealed class CustomerConfiguration
    : IEntityTypeConfiguration<Customer>
{
    public void Configure(EntityTypeBuilder<Customer> builder)
    {
        builder.ToTable("customer");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id)
            .HasColumnName("id");

        builder.Property(x => x.UserId)
            .HasColumnName("user_id")
            .IsRequired();


        builder.HasIndex(x => x.UserId)
            .IsUnique();

        builder.Property(x => x.CustomerType)
            .HasColumnName("customer_type")
            .HasMaxLength(30)
            .HasConversion<string>()
            .IsRequired(false);

        builder.Property(x => x.CompanyName)
            .HasColumnName("company_name")
            .HasMaxLength(255)
            .IsRequired(false);

        builder.Property(x => x.TaxCode)
            .HasColumnName("tax_code")
            .HasMaxLength(50)
            .IsRequired(false);


        builder.Property(x => x.Note)
            .HasColumnName("note")
            .IsRequired(false);

        builder.HasOne(x => x.User)
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}