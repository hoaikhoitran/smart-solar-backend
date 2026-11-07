using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartSolar.Modules.PreSurvey.Entities;
using SmartSolar.Modules.PreSurvey.Enums;

namespace SmartSolar.Infrastructure.Persistence.Configurations.PreSurveys;

public sealed class SurveyRequestConfiguration
    : IEntityTypeConfiguration<SurveyRequest>
{
    public void Configure(
        EntityTypeBuilder<SurveyRequest> builder)
    {
        // Backstop for the redesign: Assigned/Reviewing are retired, so an outdated instance cannot write them.
        builder.ToTable("survey_request", t =>
        {
            t.HasCheckConstraint("ck_survey_request_status",
                $"status IN ({string.Join(", ", Enum.GetNames<SurveyRequestStatus>().Select(n => $"'{n}'"))})");
            // Closure metadata only on a terminal request; a Closed request always carries it. Cancelled rows written before the
            // closure columns existed have none, which stays valid.
            t.HasCheckConstraint("ck_survey_request_closure_terminal",
                $"closed_at IS NULL OR status IN ('{nameof(SurveyRequestStatus.Cancelled)}', '{nameof(SurveyRequestStatus.Closed)}')");
            t.HasCheckConstraint("ck_survey_request_closed_fields",
                $"status <> '{nameof(SurveyRequestStatus.Closed)}' OR (closed_at IS NOT NULL AND closed_by_user_id IS NOT NULL AND closure_reason_code IS NOT NULL)");
            t.HasCheckConstraint("ck_survey_request_closure_reason",
                $"(closed_at IS NULL) = (closure_reason_code IS NULL) AND (closure_reason_code IS NULL OR closure_reason_code IN ({string.Join(", ", SurveyRequestClosureReasons.All.Select(r => $"'{r}'"))}))");
        });

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

        builder.Property(x => x.ClosedAt).HasColumnName("closed_at");
        builder.Property(x => x.ClosedByUserId).HasColumnName("closed_by_user_id");
        builder.Property(x => x.ClosureReasonCode).HasColumnName("closure_reason_code").HasMaxLength(40);
        builder.Property(x => x.ClosureNote).HasColumnName("closure_note").HasMaxLength(1000);
        // Sales work queues filter by owner and status.
        builder.HasIndex(x => new { x.AssignedSaleId, x.Status }).HasDatabaseName("ix_survey_request_assigned_sale_status");
        builder.HasOne<SmartSolar.Modules.Identity.Entities.UserAccount>().WithMany().HasForeignKey(x => x.ClosedByUserId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_survey_request_closed_by");

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