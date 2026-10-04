using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartSolar.Modules.PreSurvey.Entities;

namespace SmartSolar.Infrastructure.Persistence.Configurations.PreSurveys;

public sealed class SurveyRequestConfiguration
    : IEntityTypeConfiguration<SurveyRequest>
{
    public void Configure(
        EntityTypeBuilder<SurveyRequest> builder)
    {
        builder.ToTable("survey_request");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id)
            .HasColumnName("id");

        builder.Property(x => x.PreSurveyId)
            .HasColumnName("pre_survey_id")
            .IsRequired();

        builder.HasIndex(x => x.PreSurveyId)
            .IsUnique();

        builder.Property(x => x.AssignedSaleId)
            .HasColumnName("assigned_sale_id")
            .IsRequired(false);

        builder.Property(x => x.Status)
            .HasColumnName("status")
            .HasMaxLength(30)
            .HasConversion<string>()
            .IsRequired();

        builder.Property(x => x.SubmittedAt)
            .HasColumnName("submitted_at")
            .IsRequired();

        builder.Property(x => x.AssignedAt)
            .HasColumnName("assigned_at")
            .IsRequired(false);

        builder.Property(x => x.ScheduledAt)
            .HasColumnName("scheduled_at")
            .IsRequired(false);

        builder.Property(x => x.SalesNote)
            .HasColumnName("sales_note")
            .HasMaxLength(1000)
            .IsRequired(false);

        builder.HasOne(x => x.PreSurvey)
            .WithOne(x => x.SurveyRequest)
            .HasForeignKey<SurveyRequest>(
                x => x.PreSurveyId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(x => x.AssignedSale)
            .WithMany()
            .HasForeignKey(x => x.AssignedSaleId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}