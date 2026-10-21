using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.Identity.Entities;
using SmartSolar.Modules.OnSiteSurveys;
using SmartSolar.Modules.PreSurvey.Entities;
using SmartSolar.Modules.Scheduling.Entities;

namespace SmartSolar.Infrastructure.Persistence.Configurations.InstallationWorkflow;

public sealed class OnSiteSurveyReportConfiguration : IEntityTypeConfiguration<OnSiteSurveyReport>
{
    public void Configure(EntityTypeBuilder<OnSiteSurveyReport> builder)
    {
        builder.ToTable("onsite_survey_report", t =>
        {
            t.HasCheckConstraint("ck_onsite_survey_report_status",
                $"status IN ('{OnSiteSurveyStatuses.Draft}', '{OnSiteSurveyStatuses.InProgress}', '{OnSiteSurveyStatuses.Completed}')");
            t.HasCheckConstraint("ck_onsite_survey_report_completed",
                $"status <> '{OnSiteSurveyStatuses.Completed}' OR (completed_at IS NOT NULL AND started_at IS NOT NULL AND version >= 1)");
            t.HasCheckConstraint("ck_onsite_survey_report_dimensions",
                "(roof_length_m IS NULL OR roof_length_m > 0) AND (roof_width_m IS NULL OR roof_width_m > 0) AND (usable_area_m2 IS NULL OR usable_area_m2 > 0)");
            t.HasCheckConstraint("ck_onsite_survey_report_angles",
                "(tilt_degree IS NULL OR tilt_degree BETWEEN 0 AND 90) AND (azimuth_degree IS NULL OR (azimuth_degree >= 0 AND azimuth_degree < 360))");
            t.HasCheckConstraint("ck_onsite_survey_report_location_paired", "(measured_latitude IS NULL) = (measured_longitude IS NULL)");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.SurveyRequestId).HasColumnName("survey_request_id").IsRequired();
        builder.Property(x => x.AppointmentId).HasColumnName("appointment_id").IsRequired();
        builder.Property(x => x.TechnicianUserId).HasColumnName("technician_user_id").IsRequired();
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(20).IsRequired();
        builder.Property(x => x.StartedAt).HasColumnName("started_at");
        builder.Property(x => x.CompletedAt).HasColumnName("completed_at");
        builder.Property(x => x.RoofType).HasColumnName("roof_type").HasMaxLength(50);
        builder.Property(x => x.RoofMaterial).HasColumnName("roof_material").HasMaxLength(50);
        builder.Property(x => x.RoofLengthM).HasColumnName("roof_length_m").HasPrecision(8, 2);
        builder.Property(x => x.RoofWidthM).HasColumnName("roof_width_m").HasPrecision(8, 2);
        builder.Property(x => x.UsableAreaM2).HasColumnName("usable_area_m2").HasPrecision(10, 2);
        builder.Property(x => x.TiltDegree).HasColumnName("tilt_degree").HasPrecision(5, 2);
        builder.Property(x => x.AzimuthDegree).HasColumnName("azimuth_degree").HasPrecision(5, 2);
        builder.Property(x => x.RoofCondition).HasColumnName("roof_condition").HasMaxLength(20);
        builder.Property(x => x.ElectricalCabinetNotes).HasColumnName("electrical_cabinet_notes").HasMaxLength(2000);
        builder.Property(x => x.ElectricalConnectionNotes).HasColumnName("electrical_connection_notes").HasMaxLength(2000);
        builder.Property(x => x.InstallationFeasibility).HasColumnName("installation_feasibility").HasMaxLength(30);
        builder.Property(x => x.ProposedInverterLocation).HasColumnName("proposed_inverter_location").HasMaxLength(500);
        builder.Property(x => x.ProposedPanelLayoutNotes).HasColumnName("proposed_panel_layout_notes").HasMaxLength(2000);
        builder.Property(x => x.MeasuredLatitude).HasColumnName("measured_latitude").HasPrecision(9, 6);
        builder.Property(x => x.MeasuredLongitude).HasColumnName("measured_longitude").HasPrecision(9, 6);
        builder.Property(x => x.Notes).HasColumnName("notes").HasMaxLength(4000);
        builder.Property(x => x.Version).HasColumnName("version").IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(x => x.Revision).HasColumnName("revision").IsConcurrencyToken().IsRequired();
        // One survey aggregate per survey request: a second start/complete can never create another.
        builder.HasIndex(x => x.SurveyRequestId).IsUnique().HasDatabaseName("ux_onsite_survey_report_survey_request");
        builder.HasIndex(x => x.AppointmentId).HasDatabaseName("ix_onsite_survey_report_appointment");
        builder.HasOne<SurveyRequest>().WithMany().HasForeignKey(x => x.SurveyRequestId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<TechnicianAppointment>().WithMany().HasForeignKey(x => x.AppointmentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.TechnicianUserId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class OnSiteSurveyObstacleConfiguration : IEntityTypeConfiguration<OnSiteSurveyObstacle>
{
    public void Configure(EntityTypeBuilder<OnSiteSurveyObstacle> builder)
    {
        builder.ToTable("onsite_survey_obstacle", t => t.HasCheckConstraint("ck_onsite_survey_obstacle_sizes",
            "(height_m IS NULL OR height_m >= 0) AND (area_m2 IS NULL OR area_m2 >= 0)"));
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.ReportId).HasColumnName("report_id").IsRequired();
        builder.Property(x => x.SortOrder).HasColumnName("sort_order").IsRequired();
        builder.Property(x => x.Kind).HasColumnName("kind").HasMaxLength(30).IsRequired();
        builder.Property(x => x.Description).HasColumnName("description").HasMaxLength(500);
        builder.Property(x => x.HeightM).HasColumnName("height_m").HasPrecision(6, 2);
        builder.Property(x => x.AreaM2).HasColumnName("area_m2").HasPrecision(8, 2);
        builder.HasIndex(x => new { x.ReportId, x.SortOrder }).IsUnique().HasDatabaseName("ux_onsite_survey_obstacle_report_order");
        builder.HasOne<OnSiteSurveyReport>().WithMany().HasForeignKey(x => x.ReportId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class OnSiteSurveyAttachmentConfiguration : IEntityTypeConfiguration<OnSiteSurveyAttachment>
{
    public void Configure(EntityTypeBuilder<OnSiteSurveyAttachment> builder)
    {
        builder.ToTable("onsite_survey_attachment");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.ReportId).HasColumnName("report_id").IsRequired();
        builder.Property(x => x.FileId).HasColumnName("file_id").IsRequired();
        builder.Property(x => x.Category).HasColumnName("category").HasMaxLength(20).IsRequired();
        builder.Property(x => x.Caption).HasColumnName("caption").HasMaxLength(300);
        builder.Property(x => x.AddedByUserId).HasColumnName("added_by_user_id").IsRequired();
        builder.Property(x => x.AddedAt).HasColumnName("added_at").IsRequired();
        builder.Property(x => x.AmendmentId).HasColumnName("amendment_id");
        builder.HasIndex(x => x.FileId).IsUnique().HasDatabaseName("ux_onsite_survey_attachment_file");
        builder.HasIndex(x => x.ReportId).HasDatabaseName("ix_onsite_survey_attachment_report");
        builder.HasOne<OnSiteSurveyReport>().WithMany().HasForeignKey(x => x.ReportId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<StoredFile>().WithMany().HasForeignKey(x => x.FileId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.AddedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<OnSiteSurveyAmendment>().WithMany().HasForeignKey(x => x.AmendmentId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class OnSiteSurveyAmendmentConfiguration : IEntityTypeConfiguration<OnSiteSurveyAmendment>
{
    public void Configure(EntityTypeBuilder<OnSiteSurveyAmendment> builder)
    {
        builder.ToTable("onsite_survey_amendment");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.ReportId).HasColumnName("report_id").IsRequired();
        builder.Property(x => x.AmendmentNumber).HasColumnName("amendment_number").IsRequired();
        builder.Property(x => x.AuthorUserId).HasColumnName("author_user_id").IsRequired();
        builder.Property(x => x.Reason).HasColumnName("reason").HasMaxLength(1000).IsRequired();
        builder.Property(x => x.ChangesJson).HasColumnName("changes").HasColumnType("jsonb").IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.HasIndex(x => new { x.ReportId, x.AmendmentNumber }).IsUnique().HasDatabaseName("ux_onsite_survey_amendment_report_number");
        builder.HasOne<OnSiteSurveyReport>().WithMany().HasForeignKey(x => x.ReportId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.AuthorUserId).OnDelete(DeleteBehavior.Restrict);
    }
}
