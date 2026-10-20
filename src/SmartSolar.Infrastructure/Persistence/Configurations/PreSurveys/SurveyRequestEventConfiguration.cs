using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartSolar.Modules.Identity.Entities;
using SmartSolar.Modules.PreSurvey.Entities;

namespace SmartSolar.Infrastructure.Persistence.Configurations.PreSurveys;

public sealed class SurveyRequestEventConfiguration : IEntityTypeConfiguration<SurveyRequestEvent>
{
    public void Configure(EntityTypeBuilder<SurveyRequestEvent> builder)
    {
        builder.ToTable("survey_request_event", t =>
            t.HasCheckConstraint("ck_survey_request_event_type", $"event_type IN ('{SurveyRequestEventTypes.SalesOwnerChanged}')"));
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.SurveyRequestId).HasColumnName("survey_request_id").IsRequired();
        builder.Property(x => x.EventType).HasColumnName("event_type").HasMaxLength(40).IsRequired();
        builder.Property(x => x.ActorUserId).HasColumnName("actor_user_id").IsRequired(false);
        builder.Property(x => x.PreviousSaleUserId).HasColumnName("previous_sale_user_id").IsRequired(false);
        builder.Property(x => x.NewSaleUserId).HasColumnName("new_sale_user_id").IsRequired(false);
        builder.Property(x => x.Reason).HasColumnName("reason").HasMaxLength(500).IsRequired(false);
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.HasIndex(x => new { x.SurveyRequestId, x.CreatedAt }).HasDatabaseName("ix_survey_request_event_survey_request_created_at");
        builder.HasOne<SurveyRequest>().WithMany().HasForeignKey(x => x.SurveyRequestId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_survey_request_event_survey_request");
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.ActorUserId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_survey_request_event_actor");
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.PreviousSaleUserId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_survey_request_event_previous_sale");
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.NewSaleUserId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_survey_request_event_new_sale");
    }
}
