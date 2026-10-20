using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartSolar.Modules.Catalog.Entities;
using SmartSolar.Modules.CustomerContracts;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.Identity.Entities;
using SmartSolar.Modules.Installations;
using SmartSolar.Modules.PreSurvey.Entities;
using SmartSolar.Modules.Scheduling.Entities;
using SmartSolar.Modules.Scheduling.Zones;

namespace SmartSolar.Infrastructure.Persistence.Configurations.InstallationWorkflow;

public sealed class InstallationProjectConfiguration : IEntityTypeConfiguration<InstallationProject>
{
    private static string In(params string[] statuses) => $"status IN ({string.Join(", ", statuses.Select(s => $"'{s}'"))})";

    public void Configure(EntityTypeBuilder<InstallationProject> builder)
    {
        builder.ToTable("installation_project", t =>
        {
            t.HasCheckConstraint("ck_installation_project_status", In(InstallationStatuses.All.ToArray()));
            t.HasCheckConstraint("ck_installation_project_dates", "planned_end_date IS NULL OR planned_start_date IS NULL OR planned_end_date >= planned_start_date");
            t.HasCheckConstraint("ck_installation_project_completed", $"status <> '{InstallationStatuses.Completed}' OR (completed_at IS NOT NULL AND handover_at IS NOT NULL)");
            t.HasCheckConstraint("ck_installation_project_schedule",
                "(scheduled_starts_at IS NULL) = (scheduled_ends_at IS NULL) AND (scheduled_ends_at IS NULL OR scheduled_ends_at > scheduled_starts_at)");
            t.HasCheckConstraint("ck_installation_project_unassigned",
                $"status <> '{InstallationStatuses.PendingAssignment}' OR (responsible_technician_user_id IS NULL AND technician_accepted_at IS NULL)");
            t.HasCheckConstraint("ck_installation_project_assigned",
                $"NOT {In(InstallationStatuses.PendingTechnicianAcceptance, InstallationStatuses.Scheduled, InstallationStatuses.PendingCustomerAcceptance)} "
                + "OR (responsible_technician_user_id IS NOT NULL AND scheduled_starts_at IS NOT NULL)");
            t.HasCheckConstraint("ck_installation_project_accepted",
                $"NOT {In(InstallationStatuses.Scheduled, InstallationStatuses.PendingCustomerAcceptance)} OR technician_accepted_at IS NOT NULL");
            t.HasCheckConstraint("ck_installation_project_started",
                $"NOT {In(InstallationStatuses.InProgress, InstallationStatuses.PendingCustomerAcceptance, InstallationStatuses.ReworkRequired, InstallationStatuses.Completed)} "
                + "OR started_at IS NOT NULL");
            t.HasCheckConstraint("ck_installation_project_cancelled",
                $"(status = '{InstallationStatuses.Cancelled}') = (cancelled_at IS NOT NULL) AND (status <> '{InstallationStatuses.Cancelled}' OR cancellation_reason IS NOT NULL)");
            t.HasCheckConstraint("ck_installation_project_round", "acceptance_round >= 0");
        });
        builder.HasKey(x => x.Id);
        builder.Ignore(x => x.CurrentRound);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.ContractId).HasColumnName("contract_id").IsRequired();
        builder.Property(x => x.SurveyRequestId).HasColumnName("survey_request_id").IsRequired();
        builder.Property(x => x.CustomerUserId).HasColumnName("customer_user_id").IsRequired();
        builder.Property(x => x.PropertySiteId).HasColumnName("property_site_id").IsRequired();
        builder.Property(x => x.ManagerUserId).HasColumnName("manager_user_id").IsRequired();
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(40).IsRequired();
        // service_area_id stays in the database (nullable, unused) until the cleanup draft; the zone replaces it.
        builder.Property(x => x.OperationalZoneId).HasColumnName("operational_zone_id");
        builder.Property(x => x.ResponsibleTechnicianUserId).HasColumnName("responsible_technician_user_id");
        builder.Property(x => x.TechnicianAcceptedAt).HasColumnName("technician_accepted_at");
        builder.Property(x => x.ScheduledStartsAt).HasColumnName("scheduled_starts_at");
        builder.Property(x => x.ScheduledEndsAt).HasColumnName("scheduled_ends_at");
        builder.Property(x => x.PlannedStartDate).HasColumnName("planned_start_date");
        builder.Property(x => x.PlannedEndDate).HasColumnName("planned_end_date");
        builder.Property(x => x.StartedAt).HasColumnName("started_at");
        builder.Property(x => x.AcceptanceRequestedAt).HasColumnName("acceptance_requested_at");
        builder.Property(x => x.CompletedAt).HasColumnName("completed_at");
        builder.Property(x => x.HandoverAt).HasColumnName("handover_at");
        builder.Property(x => x.AcceptanceRound).HasColumnName("acceptance_round").IsRequired();
        builder.Property(x => x.CancelledAt).HasColumnName("cancelled_at");
        builder.Property(x => x.CancellationReason).HasColumnName("cancellation_reason").HasMaxLength(500);
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(x => x.Revision).HasColumnName("revision").IsConcurrencyToken().IsRequired();
        builder.HasIndex(x => x.ContractId).IsUnique().HasDatabaseName("ux_installation_project_contract");
        builder.HasIndex(x => new { x.CustomerUserId, x.Status }).HasDatabaseName("ix_installation_project_customer_status");
        builder.HasIndex(x => x.ResponsibleTechnicianUserId).HasDatabaseName("ix_installation_project_responsible_technician");
        builder.HasOne<CustomerContract>().WithMany().HasForeignKey(x => x.ContractId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<SurveyRequest>().WithMany().HasForeignKey(x => x.SurveyRequestId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<PropertySite>().WithMany().HasForeignKey(x => x.PropertySiteId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.CustomerUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.ManagerUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<OperationalZone>().WithMany().HasForeignKey(x => x.OperationalZoneId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_installation_project_operational_zone");
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.ResponsibleTechnicianUserId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_installation_project_responsible_technician");
    }
}

public sealed class InstallationAssignmentConfiguration : IEntityTypeConfiguration<InstallationAssignment>
{
    public void Configure(EntityTypeBuilder<InstallationAssignment> builder)
    {
        // ex_installation_assignment_no_overlap (EXCLUDE USING gist on technician + tstzrange WHERE status IN ('PENDING','ACCEPTED'), PostgreSQL)
        // is created by manual migration SQL.
        builder.ToTable("installation_assignment", t =>
        {
            t.HasCheckConstraint("ck_installation_assignment_range", "ends_at > starts_at");
            t.HasCheckConstraint("ck_installation_assignment_status", $"status IN ({string.Join(", ", AssignmentStatuses.All.Select(s => $"'{s}'"))})");
            t.HasCheckConstraint("ck_installation_assignment_responded",
                $"status NOT IN ('{AssignmentStatuses.Accepted}', '{AssignmentStatuses.Declined}') OR responded_at IS NOT NULL");
            t.HasCheckConstraint("ck_installation_assignment_declined", $"status <> '{AssignmentStatuses.Declined}' OR response_reason IS NOT NULL");
            t.HasCheckConstraint("ck_installation_assignment_closed",
                $"(status IN ('{AssignmentStatuses.Pending}', '{AssignmentStatuses.Accepted}')) = (closed_at IS NULL)");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.ProjectId).HasColumnName("project_id").IsRequired();
        builder.Property(x => x.TechnicianUserId).HasColumnName("technician_user_id").IsRequired();
        builder.Property(x => x.StartsAt).HasColumnName("starts_at").IsRequired();
        builder.Property(x => x.EndsAt).HasColumnName("ends_at").IsRequired();
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(20).IsRequired();
        builder.Property(x => x.AssignedByUserId).HasColumnName("assigned_by_user_id").IsRequired();
        builder.Property(x => x.AssignedAt).HasColumnName("assigned_at").IsRequired();
        builder.Property(x => x.AssignmentReason).HasColumnName("assignment_reason").HasMaxLength(500);
        builder.Property(x => x.RespondedAt).HasColumnName("responded_at");
        builder.Property(x => x.ResponseReason).HasColumnName("response_reason").HasMaxLength(500);
        builder.Property(x => x.ClosedAt).HasColumnName("closed_at");
        // One current (PENDING or ACCEPTED) assignment per project.
        builder.HasIndex(x => x.ProjectId).IsUnique().HasFilter($"status IN ('{AssignmentStatuses.Pending}', '{AssignmentStatuses.Accepted}')")
            .HasDatabaseName("ux_installation_assignment_current");
        builder.HasIndex(x => new { x.TechnicianUserId, x.StartsAt }).HasDatabaseName("ix_installation_assignment_technician_starts_at");
        builder.HasIndex(x => new { x.ProjectId, x.AssignedAt }).HasDatabaseName("ix_installation_assignment_project_assigned_at");
        builder.HasOne<InstallationProject>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_installation_assignment_project");
        builder.HasOne<TechnicianProfile>().WithMany().HasForeignKey(x => x.TechnicianUserId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_installation_assignment_technician_profile");
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.AssignedByUserId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_installation_assignment_assigned_by");
    }
}

public sealed class InstallationEvidenceConfiguration : IEntityTypeConfiguration<InstallationEvidence>
{
    public void Configure(EntityTypeBuilder<InstallationEvidence> builder)
    {
        builder.ToTable("installation_evidence", t => t.HasCheckConstraint("ck_installation_evidence_round", "round >= 1"));
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.ProjectId).HasColumnName("project_id").IsRequired();
        builder.Property(x => x.Round).HasColumnName("round").IsRequired();
        builder.Property(x => x.FileId).HasColumnName("file_id").IsRequired();
        builder.Property(x => x.Caption).HasColumnName("caption").HasMaxLength(300);
        builder.Property(x => x.AddedByUserId).HasColumnName("added_by_user_id").IsRequired();
        builder.Property(x => x.AddedAt).HasColumnName("added_at").IsRequired();
        builder.HasIndex(x => x.FileId).IsUnique().HasDatabaseName("ux_installation_evidence_file");
        builder.HasIndex(x => new { x.ProjectId, x.Round }).HasDatabaseName("ix_installation_evidence_project_round");
        builder.HasOne<InstallationProject>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_installation_evidence_project");
        builder.HasOne<StoredFile>().WithMany().HasForeignKey(x => x.FileId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_installation_evidence_file_reference");
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.AddedByUserId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_installation_evidence_added_by");
    }
}

public sealed class InstallationAcceptanceConfiguration : IEntityTypeConfiguration<InstallationAcceptance>
{
    public void Configure(EntityTypeBuilder<InstallationAcceptance> builder)
    {
        builder.ToTable("installation_acceptance", t =>
        {
            t.HasCheckConstraint("ck_installation_acceptance_decision",
                $"decision IN ('{AcceptanceDecisions.Pending}', '{AcceptanceDecisions.Accepted}', '{AcceptanceDecisions.ReworkRequested}')");
            t.HasCheckConstraint("ck_installation_acceptance_decided", $"(decision = '{AcceptanceDecisions.Pending}') = (decided_at IS NULL)");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.ProjectId).HasColumnName("project_id").IsRequired();
        builder.Property(x => x.Round).HasColumnName("round").IsRequired();
        builder.Property(x => x.RequestedByUserId).HasColumnName("requested_by_user_id").IsRequired();
        builder.Property(x => x.RequestedAt).HasColumnName("requested_at").IsRequired();
        builder.Property(x => x.CompletionReport).HasColumnName("completion_report").HasMaxLength(8000).IsRequired();
        builder.Property(x => x.Decision).HasColumnName("decision").HasMaxLength(30).IsRequired();
        builder.Property(x => x.DecidedByUserId).HasColumnName("decided_by_user_id");
        builder.Property(x => x.DecidedAt).HasColumnName("decided_at");
        builder.Property(x => x.Reason).HasColumnName("reason").HasMaxLength(2000);
        builder.HasIndex(x => new { x.ProjectId, x.Round }).IsUnique().HasDatabaseName("ux_installation_acceptance_project_round");
        builder.HasIndex(x => new { x.ProjectId, x.Decision }).IsUnique().HasFilter($"decision = '{AcceptanceDecisions.Pending}'").HasDatabaseName("ux_installation_acceptance_one_pending");
        builder.HasOne<InstallationProject>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.RequestedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.DecidedByUserId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class InstalledAssetConfiguration : IEntityTypeConfiguration<InstalledAsset>
{
    public void Configure(EntityTypeBuilder<InstalledAsset> builder)
    {
        builder.ToTable("installed_asset", t =>
        {
            t.HasCheckConstraint("ck_installed_asset_type", $"asset_type IN ({string.Join(", ", AssetTypes.All.Select(s => $"'{s}'"))})");
            t.HasCheckConstraint("ck_installed_asset_warranty", "warranty_months IS NULL OR warranty_months BETWEEN 0 AND 600");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.ProjectId).HasColumnName("project_id").IsRequired();
        builder.Property(x => x.AssetType).HasColumnName("asset_type").HasMaxLength(20).IsRequired();
        builder.Property(x => x.ProductId).HasColumnName("product_id");
        builder.Property(x => x.Manufacturer).HasColumnName("manufacturer").HasMaxLength(100).IsRequired();
        builder.Property(x => x.Model).HasColumnName("model").HasMaxLength(100).IsRequired();
        builder.Property(x => x.SerialNumber).HasColumnName("serial_number").HasMaxLength(100).IsRequired();
        builder.Property(x => x.PositionCode).HasColumnName("position_code").HasMaxLength(50);
        builder.Property(x => x.WarrantyMonths).HasColumnName("warranty_months");
        builder.Property(x => x.RegisteredByUserId).HasColumnName("registered_by_user_id").IsRequired();
        builder.Property(x => x.RegisteredAt).HasColumnName("registered_at").IsRequired();
        builder.HasIndex(x => new { x.Manufacturer, x.SerialNumber }).IsUnique().HasDatabaseName("ux_installed_asset_manufacturer_serial");
        builder.HasIndex(x => x.ProjectId).HasDatabaseName("ix_installed_asset_project");
        builder.HasOne<InstallationProject>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.RegisteredByUserId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class InstallationChecklistItemConfiguration : IEntityTypeConfiguration<InstallationChecklistItem>
{
    public void Configure(EntityTypeBuilder<InstallationChecklistItem> builder)
    {
        builder.ToTable("installation_checklist_item", t =>
        {
            t.HasCheckConstraint("ck_installation_checklist_item_status",
                $"status IN ('{ChecklistStatuses.Pending}', '{ChecklistStatuses.Satisfied}', '{ChecklistStatuses.NotApplicable}')");
            t.HasCheckConstraint("ck_installation_checklist_item_evidence", $"status <> '{ChecklistStatuses.Satisfied}' OR evidence_file_id IS NOT NULL");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.ProjectId).HasColumnName("project_id").IsRequired();
        builder.Property(x => x.Code).HasColumnName("code").HasMaxLength(50).IsRequired();
        builder.Property(x => x.Title).HasColumnName("title").HasMaxLength(200).IsRequired();
        builder.Property(x => x.IsRequired).HasColumnName("is_required").IsRequired();
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(20).IsRequired();
        builder.Property(x => x.EvidenceFileId).HasColumnName("evidence_file_id");
        builder.Property(x => x.Note).HasColumnName("note").HasMaxLength(1000);
        builder.Property(x => x.VerifiedByUserId).HasColumnName("verified_by_user_id");
        builder.Property(x => x.VerifiedAt).HasColumnName("verified_at");
        builder.HasIndex(x => new { x.ProjectId, x.Code }).IsUnique().HasDatabaseName("ux_installation_checklist_item_project_code");
        builder.HasOne<InstallationProject>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<StoredFile>().WithMany().HasForeignKey(x => x.EvidenceFileId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.VerifiedByUserId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class InstallationEventConfiguration : IEntityTypeConfiguration<InstallationEvent>
{
    public void Configure(EntityTypeBuilder<InstallationEvent> builder)
    {
        builder.ToTable("installation_event");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.ProjectId).HasColumnName("project_id").IsRequired();
        builder.Property(x => x.EventType).HasColumnName("event_type").HasMaxLength(40).IsRequired();
        builder.Property(x => x.ActorUserId).HasColumnName("actor_user_id").IsRequired();
        builder.Property(x => x.FromStatus).HasColumnName("from_status").HasMaxLength(40);
        builder.Property(x => x.ToStatus).HasColumnName("to_status").HasMaxLength(40);
        builder.Property(x => x.Detail).HasColumnName("detail").HasMaxLength(500);
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.HasIndex(x => new { x.ProjectId, x.CreatedAt }).HasDatabaseName("ix_installation_event_project_created_at");
        builder.HasOne<InstallationProject>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.ActorUserId).OnDelete(DeleteBehavior.Restrict);
    }
}
