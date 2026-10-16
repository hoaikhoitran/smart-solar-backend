using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartSolar.Modules.Catalog.Entities;
using SmartSolar.Modules.CustomerContracts;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.Identity.Entities;
using SmartSolar.Modules.Installations;
using SmartSolar.Modules.PreSurvey.Entities;

namespace SmartSolar.Infrastructure.Persistence.Configurations.InstallationWorkflow;

public sealed class InstallationProjectConfiguration : IEntityTypeConfiguration<InstallationProject>
{
    public void Configure(EntityTypeBuilder<InstallationProject> builder)
    {
        builder.ToTable("installation_project", t =>
        {
            t.HasCheckConstraint("ck_installation_project_status", $"status IN ({string.Join(", ", InstallationStatuses.All.Select(s => $"'{s}'"))})");
            t.HasCheckConstraint("ck_installation_project_dates", "planned_end_date IS NULL OR planned_start_date IS NULL OR planned_end_date >= planned_start_date");
            t.HasCheckConstraint("ck_installation_project_completed", $"status <> '{InstallationStatuses.Completed}' OR (completed_at IS NOT NULL AND handover_at IS NOT NULL)");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.ContractId).HasColumnName("contract_id").IsRequired();
        builder.Property(x => x.SurveyRequestId).HasColumnName("survey_request_id").IsRequired();
        builder.Property(x => x.CustomerUserId).HasColumnName("customer_user_id").IsRequired();
        builder.Property(x => x.PropertySiteId).HasColumnName("property_site_id").IsRequired();
        builder.Property(x => x.ManagerUserId).HasColumnName("manager_user_id").IsRequired();
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(40).IsRequired();
        builder.Property(x => x.PlannedStartDate).HasColumnName("planned_start_date");
        builder.Property(x => x.PlannedEndDate).HasColumnName("planned_end_date");
        builder.Property(x => x.StartedAt).HasColumnName("started_at");
        builder.Property(x => x.AcceptanceRequestedAt).HasColumnName("acceptance_requested_at");
        builder.Property(x => x.CompletedAt).HasColumnName("completed_at");
        builder.Property(x => x.HandoverAt).HasColumnName("handover_at");
        builder.Property(x => x.AcceptanceRound).HasColumnName("acceptance_round").IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(x => x.Revision).HasColumnName("revision").IsConcurrencyToken().IsRequired();
        builder.HasIndex(x => x.ContractId).IsUnique().HasDatabaseName("ux_installation_project_contract");
        builder.HasIndex(x => new { x.CustomerUserId, x.Status }).HasDatabaseName("ix_installation_project_customer_status");
        builder.HasOne<CustomerContract>().WithMany().HasForeignKey(x => x.ContractId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<SurveyRequest>().WithMany().HasForeignKey(x => x.SurveyRequestId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<PropertySite>().WithMany().HasForeignKey(x => x.PropertySiteId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.CustomerUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.ManagerUserId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class InstallationTeamMemberConfiguration : IEntityTypeConfiguration<InstallationTeamMember>
{
    public void Configure(EntityTypeBuilder<InstallationTeamMember> builder)
    {
        builder.ToTable("installation_team_member", t => t.HasCheckConstraint("ck_installation_team_member_leader_active", "NOT is_leader OR removed_at IS NULL"));
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.ProjectId).HasColumnName("project_id").IsRequired();
        builder.Property(x => x.TechnicianUserId).HasColumnName("technician_user_id").IsRequired();
        builder.Property(x => x.IsLeader).HasColumnName("is_leader").IsRequired();
        builder.Property(x => x.AssignedByUserId).HasColumnName("assigned_by_user_id").IsRequired();
        builder.Property(x => x.AssignedAt).HasColumnName("assigned_at").IsRequired();
        builder.Property(x => x.RemovedAt).HasColumnName("removed_at");
        builder.HasIndex(x => new { x.ProjectId, x.TechnicianUserId }).IsUnique().HasFilter("removed_at IS NULL").HasDatabaseName("ux_installation_team_member_active");
        // Exactly one leader at a time (at least one is enforced by the planning use case).
        builder.HasIndex(x => new { x.ProjectId, x.IsLeader }).IsUnique().HasFilter("is_leader AND removed_at IS NULL").HasDatabaseName("ux_installation_team_member_one_leader");
        builder.HasOne<InstallationProject>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.TechnicianUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.AssignedByUserId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class InstallationShiftConfiguration : IEntityTypeConfiguration<InstallationShift>
{
    public void Configure(EntityTypeBuilder<InstallationShift> builder)
    {
        builder.ToTable("installation_shift", t =>
        {
            t.HasCheckConstraint("ck_installation_shift_range", "ends_at > starts_at AND local_end > local_start");
            t.HasCheckConstraint("ck_installation_shift_status", "status IN ('ACTIVE', 'CANCELLED')");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.ProjectId).HasColumnName("project_id").IsRequired();
        builder.Property(x => x.LocalDate).HasColumnName("local_date").IsRequired();
        builder.Property(x => x.LocalStart).HasColumnName("local_start").IsRequired();
        builder.Property(x => x.LocalEnd).HasColumnName("local_end").IsRequired();
        builder.Property(x => x.StartsAt).HasColumnName("starts_at").IsRequired();
        builder.Property(x => x.EndsAt).HasColumnName("ends_at").IsRequired();
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(20).IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.HasIndex(x => new { x.ProjectId, x.StartsAt }).IsUnique().HasFilter("status = 'ACTIVE'").HasDatabaseName("ux_installation_shift_project_start");
        builder.HasOne<InstallationProject>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class InstallationShiftAssignmentConfiguration : IEntityTypeConfiguration<InstallationShiftAssignment>
{
    public void Configure(EntityTypeBuilder<InstallationShiftAssignment> builder)
    {
        // ex_installation_shift_assignment_no_overlap (EXCLUDE USING gist, PostgreSQL) is created by the manual migration SQL.
        builder.ToTable("installation_shift_assignment", t =>
        {
            t.HasCheckConstraint("ck_installation_shift_assignment_range", "ends_at > starts_at");
            t.HasCheckConstraint("ck_installation_shift_assignment_status", "status IN ('ACTIVE', 'RELEASED')");
            t.HasCheckConstraint("ck_installation_shift_assignment_released", "(status = 'RELEASED') = (released_at IS NOT NULL)");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.ShiftId).HasColumnName("shift_id").IsRequired();
        builder.Property(x => x.ProjectId).HasColumnName("project_id").IsRequired();
        builder.Property(x => x.TechnicianUserId).HasColumnName("technician_user_id").IsRequired();
        builder.Property(x => x.StartsAt).HasColumnName("starts_at").IsRequired();
        builder.Property(x => x.EndsAt).HasColumnName("ends_at").IsRequired();
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(20).IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(x => x.ReleasedAt).HasColumnName("released_at");
        builder.HasIndex(x => new { x.ShiftId, x.TechnicianUserId }).IsUnique().HasFilter("status = 'ACTIVE'").HasDatabaseName("ux_installation_shift_assignment_shift_technician");
        builder.HasIndex(x => new { x.TechnicianUserId, x.StartsAt }).HasDatabaseName("ix_installation_shift_assignment_technician_starts_at");
        builder.HasOne<InstallationShift>().WithMany().HasForeignKey(x => x.ShiftId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<InstallationProject>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.TechnicianUserId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class InstallationMilestoneConfiguration : IEntityTypeConfiguration<InstallationMilestone>
{
    public void Configure(EntityTypeBuilder<InstallationMilestone> builder)
    {
        builder.ToTable("installation_milestone", t =>
        {
            t.HasCheckConstraint("ck_installation_milestone_status", $"status IN ('{MilestoneStatuses.NotStarted}', '{MilestoneStatuses.InProgress}', '{MilestoneStatuses.Completed}')");
            t.HasCheckConstraint("ck_installation_milestone_numbers", "weight_percent BETWEEN 0 AND 100 AND progress_percent BETWEEN 0 AND 100 AND rework_round >= 0");
            t.HasCheckConstraint("ck_installation_milestone_completed", $"status <> '{MilestoneStatuses.Completed}' OR (completed_at IS NOT NULL AND completed_by_user_id IS NOT NULL)");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.ProjectId).HasColumnName("project_id").IsRequired();
        builder.Property(x => x.SortOrder).HasColumnName("sort_order").IsRequired();
        builder.Property(x => x.Code).HasColumnName("code").HasMaxLength(40).IsRequired();
        builder.Property(x => x.Name).HasColumnName("name").HasMaxLength(100).IsRequired();
        builder.Property(x => x.Description).HasColumnName("description").HasMaxLength(1000);
        builder.Property(x => x.WeightPercent).HasColumnName("weight_percent").IsRequired();
        builder.Property(x => x.IsMandatory).HasColumnName("is_mandatory").IsRequired();
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(20).IsRequired();
        builder.Property(x => x.ProgressPercent).HasColumnName("progress_percent").IsRequired();
        builder.Property(x => x.Notes).HasColumnName("notes").HasMaxLength(2000);
        builder.Property(x => x.ReworkRound).HasColumnName("rework_round").IsRequired();
        builder.Property(x => x.StartedAt).HasColumnName("started_at");
        builder.Property(x => x.CompletedAt).HasColumnName("completed_at");
        builder.Property(x => x.CompletedByUserId).HasColumnName("completed_by_user_id");
        builder.Property(x => x.Revision).HasColumnName("revision").IsConcurrencyToken().IsRequired();
        builder.HasIndex(x => new { x.ProjectId, x.SortOrder }).IsUnique().HasDatabaseName("ux_installation_milestone_project_order");
        builder.HasIndex(x => new { x.ProjectId, x.Code }).IsUnique().HasDatabaseName("ux_installation_milestone_project_code");
        builder.HasOne<InstallationProject>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.CompletedByUserId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class MilestoneEvidenceConfiguration : IEntityTypeConfiguration<MilestoneEvidence>
{
    public void Configure(EntityTypeBuilder<MilestoneEvidence> builder)
    {
        builder.ToTable("installation_milestone_evidence");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.MilestoneId).HasColumnName("milestone_id").IsRequired();
        builder.Property(x => x.ProjectId).HasColumnName("project_id").IsRequired();
        builder.Property(x => x.FileId).HasColumnName("file_id").IsRequired();
        builder.Property(x => x.ReworkRound).HasColumnName("rework_round").IsRequired();
        builder.Property(x => x.Caption).HasColumnName("caption").HasMaxLength(300);
        builder.Property(x => x.AddedByUserId).HasColumnName("added_by_user_id").IsRequired();
        builder.Property(x => x.AddedAt).HasColumnName("added_at").IsRequired();
        builder.HasIndex(x => x.FileId).IsUnique().HasDatabaseName("ux_installation_milestone_evidence_file");
        builder.HasIndex(x => new { x.MilestoneId, x.ReworkRound }).HasDatabaseName("ix_installation_milestone_evidence_milestone_round");
        builder.HasOne<InstallationMilestone>().WithMany().HasForeignKey(x => x.MilestoneId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<InstallationProject>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<StoredFile>().WithMany().HasForeignKey(x => x.FileId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.AddedByUserId).OnDelete(DeleteBehavior.Restrict);
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
        builder.Property(x => x.Detail).HasColumnName("detail").HasMaxLength(500);
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.HasIndex(x => new { x.ProjectId, x.CreatedAt }).HasDatabaseName("ix_installation_event_project_created_at");
        builder.HasOne<InstallationProject>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.ActorUserId).OnDelete(DeleteBehavior.Restrict);
    }
}
