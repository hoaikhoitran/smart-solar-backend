using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartSolar.Modules.AfterSales;
using SmartSolar.Modules.CustomerContracts;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.Identity.Entities;
using SmartSolar.Modules.Installations;
using SmartSolar.Modules.Payments;
using SmartSolar.Modules.PreSurvey.Entities;

namespace SmartSolar.Infrastructure.Persistence.Configurations.AfterSales;

// Flow 3 — After-Sales Services. Money is numeric(18,2) holding whole VND; every foreign key is RESTRICT (history is never
// deleted); important invariants are check constraints or (partial) unique indexes. The PostgreSQL exclusion constraint that
// forbids two overlapping terms of one kind for one contract is created by the migration's manual SQL.

internal static class AfterSalesSql
{
    public static string In(IEnumerable<string> values) => string.Join(", ", values.Select(v => $"'{v}'"));
}

public sealed class WarrantyOptionConfiguration : IEntityTypeConfiguration<WarrantyOption>
{
    public void Configure(EntityTypeBuilder<WarrantyOption> builder)
    {
        builder.ToTable("warranty_option", t =>
        {
            t.HasCheckConstraint("ck_warranty_option_kind", $"kind IN ({AfterSalesSql.In(WarrantyOptionKinds.All)})");
            t.HasCheckConstraint("ck_warranty_option_months", "months BETWEEN 1 AND 240");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.Kind).HasColumnName("kind").HasMaxLength(20).IsRequired();
        builder.Property(x => x.Months).HasColumnName("months").IsRequired();
        builder.Property(x => x.IsDefault).HasColumnName("is_default").IsRequired();
        builder.Property(x => x.IsActive).HasColumnName("is_active").IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.HasIndex(x => new { x.Kind, x.Months }).IsUnique().HasDatabaseName("ux_warranty_option_kind_months");
        builder.HasIndex(x => x.Kind).IsUnique().HasFilter("is_default").HasDatabaseName("ux_warranty_option_one_default");
    }
}

public sealed class ServicePackageConfiguration : IEntityTypeConfiguration<ServicePackage>
{
    public void Configure(EntityTypeBuilder<ServicePackage> builder)
    {
        builder.ToTable("service_package", t =>
        {
            t.HasCheckConstraint("ck_service_package_kind", $"kind IN ({AfterSalesSql.In(ServicePackageKinds.All)})");
            t.HasCheckConstraint("ck_service_package_price", "price > 0 AND duration_months BETWEEN 1 AND 120");
            t.HasCheckConstraint("ck_service_package_maintenance",
                $"kind <> '{ServicePackageKinds.Maintenance}' OR visit_months IS NOT NULL");
            t.HasCheckConstraint("ck_service_package_disaster",
                $"kind <> '{ServicePackageKinds.DisasterProtection}' OR (covered_disaster_types IS NOT NULL AND coverage_percent BETWEEN 0 AND 100 "
                + "AND per_incident_limit >= 0 AND term_limit >= per_incident_limit)");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.Kind).HasColumnName("kind").HasMaxLength(30).IsRequired();
        builder.Property(x => x.Name).HasColumnName("name").HasMaxLength(150).IsRequired();
        builder.Property(x => x.Description).HasColumnName("description").HasMaxLength(2000);
        builder.Property(x => x.Price).HasColumnName("price").HasPrecision(18, 2).IsRequired();
        builder.Property(x => x.DurationMonths).HasColumnName("duration_months").IsRequired();
        builder.Property(x => x.VisitMonths).HasColumnName("visit_months").HasMaxLength(200);
        builder.Property(x => x.CoveredDisasterTypes).HasColumnName("covered_disaster_types").HasMaxLength(200);
        builder.Property(x => x.CoveragePercent).HasColumnName("coverage_percent").HasPrecision(5, 2);
        builder.Property(x => x.PerIncidentLimit).HasColumnName("per_incident_limit").HasPrecision(18, 2);
        builder.Property(x => x.TermLimit).HasColumnName("term_limit").HasPrecision(18, 2);
        builder.Property(x => x.IsActive).HasColumnName("is_active").IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(x => x.Revision).HasColumnName("revision").IsConcurrencyToken().IsRequired();
        builder.HasIndex(x => new { x.Kind, x.IsActive }).HasDatabaseName("ix_service_package_kind_active");
    }
}

public sealed class ServiceSubscriptionConfiguration : IEntityTypeConfiguration<ServiceSubscription>
{
    public void Configure(EntityTypeBuilder<ServiceSubscription> builder)
    {
        builder.ToTable("service_subscription", t =>
        {
            t.HasCheckConstraint("ck_service_subscription_kind", $"kind IN ({AfterSalesSql.In(ServicePackageKinds.All)})");
            t.HasCheckConstraint("ck_service_subscription_status", $"status IN ({AfterSalesSql.In(SubscriptionStatuses.All)})");
            t.HasCheckConstraint("ck_service_subscription_price", "price > 0 AND duration_months BETWEEN 1 AND 120");
            t.HasCheckConstraint("ck_service_subscription_term", "(starts_at IS NULL) = (ends_at IS NULL) AND (ends_at IS NULL OR ends_at > starts_at)");
            t.HasCheckConstraint("ck_service_subscription_active",
                $"status <> '{SubscriptionStatuses.Active}' OR (starts_at IS NOT NULL AND paid_at IS NOT NULL AND project_id IS NOT NULL)");
            t.HasCheckConstraint("ck_service_subscription_support",
                "support_reserved >= 0 AND support_consumed >= 0 AND (term_limit IS NULL OR support_reserved + support_consumed <= term_limit)");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.CustomerUserId).HasColumnName("customer_user_id").IsRequired();
        builder.Property(x => x.ContractId).HasColumnName("contract_id").IsRequired();
        builder.Property(x => x.ProjectId).HasColumnName("project_id");
        builder.Property(x => x.PackageId).HasColumnName("package_id").IsRequired();
        builder.Property(x => x.Kind).HasColumnName("kind").HasMaxLength(30).IsRequired();
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(30).IsRequired();
        builder.Property(x => x.PackageName).HasColumnName("package_name").HasMaxLength(150).IsRequired();
        builder.Property(x => x.Price).HasColumnName("price").HasPrecision(18, 2).IsRequired();
        builder.Property(x => x.DurationMonths).HasColumnName("duration_months").IsRequired();
        builder.Property(x => x.VisitMonths).HasColumnName("visit_months").HasMaxLength(200);
        builder.Property(x => x.CoveredDisasterTypes).HasColumnName("covered_disaster_types").HasMaxLength(200);
        builder.Property(x => x.CoveragePercent).HasColumnName("coverage_percent").HasPrecision(5, 2);
        builder.Property(x => x.PerIncidentLimit).HasColumnName("per_incident_limit").HasPrecision(18, 2);
        builder.Property(x => x.TermLimit).HasColumnName("term_limit").HasPrecision(18, 2);
        builder.Property(x => x.StartsAt).HasColumnName("starts_at");
        builder.Property(x => x.EndsAt).HasColumnName("ends_at");
        builder.Property(x => x.CoverageStartsAt).HasColumnName("coverage_starts_at");
        builder.Property(x => x.RenewsSubscriptionId).HasColumnName("renews_subscription_id");
        builder.Property(x => x.CurrentPaymentAttemptId).HasColumnName("current_payment_attempt_id");
        builder.Property(x => x.CheckoutExpiresAt).HasColumnName("checkout_expires_at");
        builder.Property(x => x.PaidAt).HasColumnName("paid_at");
        builder.Property(x => x.ActivatedAt).HasColumnName("activated_at");
        builder.Property(x => x.SupportReserved).HasColumnName("support_reserved").HasPrecision(18, 2).IsRequired();
        builder.Property(x => x.SupportConsumed).HasColumnName("support_consumed").HasPrecision(18, 2).IsRequired();
        builder.Property(x => x.CancellationRequestedAt).HasColumnName("cancellation_requested_at");
        builder.Property(x => x.CancellationReason).HasColumnName("cancellation_reason").HasMaxLength(500);
        builder.Property(x => x.CancelledAt).HasColumnName("cancelled_at");
        builder.Property(x => x.ExpiryReminderSentAt).HasColumnName("expiry_reminder_sent_at");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(x => x.Revision).HasColumnName("revision").IsConcurrencyToken().IsRequired();
        builder.Ignore(x => x.SupportAvailable);
        builder.HasIndex(x => new { x.ContractId, x.Kind, x.Status }).HasDatabaseName("ix_service_subscription_contract_kind_status");
        builder.HasIndex(x => new { x.CustomerUserId, x.CreatedAt }).HasDatabaseName("ix_service_subscription_customer_created_at");
        builder.HasIndex(x => new { x.Status, x.StartsAt }).HasDatabaseName("ix_service_subscription_status_starts_at");
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.CustomerUserId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_service_subscription_customer");
        builder.HasOne<CustomerContract>().WithMany().HasForeignKey(x => x.ContractId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_service_subscription_contract");
        builder.HasOne<InstallationProject>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_service_subscription_project");
        builder.HasOne<ServicePackage>().WithMany().HasForeignKey(x => x.PackageId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_service_subscription_package");
        builder.HasOne<ServiceSubscription>().WithMany().HasForeignKey(x => x.RenewsSubscriptionId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_service_subscription_renews");
        // The checkout is saved in the same transaction that sets this reference, so it always exists.
        builder.HasOne<PaymentAttempt>().WithMany().HasForeignKey(x => x.CurrentPaymentAttemptId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_service_subscription_current_payment_attempt");
    }
}

public sealed class MaintenanceEntitlementConfiguration : IEntityTypeConfiguration<MaintenanceEntitlement>
{
    public void Configure(EntityTypeBuilder<MaintenanceEntitlement> builder)
    {
        builder.ToTable("maintenance_entitlement", t =>
        {
            t.HasCheckConstraint("ck_maintenance_entitlement_status", $"status IN ({AfterSalesSql.In(EntitlementStatuses.All)})");
            t.HasCheckConstraint("ck_maintenance_entitlement_period", "plan_month >= 1 AND period_ends_at > period_starts_at");
            t.HasCheckConstraint("ck_maintenance_entitlement_consumed", $"(status = '{EntitlementStatuses.Consumed}') = (consumed_at IS NOT NULL)");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.SubscriptionId).HasColumnName("subscription_id").IsRequired();
        builder.Property(x => x.PlanMonth).HasColumnName("plan_month").IsRequired();
        builder.Property(x => x.PeriodStartsAt).HasColumnName("period_starts_at").IsRequired();
        builder.Property(x => x.PeriodEndsAt).HasColumnName("period_ends_at").IsRequired();
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(30).IsRequired();
        builder.Property(x => x.CurrentRequestId).HasColumnName("current_request_id");
        builder.Property(x => x.ExtendedUntil).HasColumnName("extended_until");
        builder.Property(x => x.NotifiedAt).HasColumnName("notified_at");
        builder.Property(x => x.FollowUpCreatedAt).HasColumnName("follow_up_created_at");
        builder.Property(x => x.ConsumedAt).HasColumnName("consumed_at");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(x => x.Revision).HasColumnName("revision").IsConcurrencyToken().IsRequired();
        builder.HasIndex(x => new { x.SubscriptionId, x.PlanMonth }).IsUnique().HasDatabaseName("ux_maintenance_entitlement_subscription_month");
        builder.HasIndex(x => new { x.Status, x.PeriodStartsAt }).HasDatabaseName("ix_maintenance_entitlement_status_period");
        builder.HasOne<ServiceSubscription>().WithMany().HasForeignKey(x => x.SubscriptionId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_maintenance_entitlement_subscription");
        // Cycle with service_request.maintenance_entitlement_id: the entitlement exists first, the request is added and linked in
        // the same transaction; both columns are nullable, so neither row blocks the other.
        builder.HasOne<ServiceRequest>().WithMany().HasForeignKey(x => x.CurrentRequestId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_maintenance_entitlement_current_request");
    }
}

public sealed class ServiceRequestConfiguration : IEntityTypeConfiguration<ServiceRequest>
{
    public void Configure(EntityTypeBuilder<ServiceRequest> builder)
    {
        builder.ToTable("service_request", t =>
        {
            t.HasCheckConstraint("ck_service_request_kind", $"kind IN ({AfterSalesSql.In(ServiceRequestKinds.All)})");
            t.HasCheckConstraint("ck_service_request_status", $"status IN ({AfterSalesSql.In(ServiceRequestStatuses.All)})");
            t.HasCheckConstraint("ck_service_request_maintenance_link",
                $"(kind = '{ServiceRequestKinds.Maintenance}') = (maintenance_entitlement_id IS NOT NULL)");
            t.HasCheckConstraint("ck_service_request_cancelled", $"status <> '{ServiceRequestStatuses.Cancelled}' OR cancelled_at IS NOT NULL");
            t.HasCheckConstraint("ck_service_request_repair_warranty",
                "(repair_warranty_starts_at IS NULL) = (repair_warranty_ends_at IS NULL) AND (repair_warranty_ends_at IS NULL OR repair_warranty_ends_at > repair_warranty_starts_at)");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.RequestNumber).HasColumnName("request_number").HasMaxLength(30).IsRequired();
        builder.Property(x => x.Kind).HasColumnName("kind").HasMaxLength(30).IsRequired();
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(30).IsRequired();
        builder.Property(x => x.CustomerUserId).HasColumnName("customer_user_id").IsRequired();
        builder.Property(x => x.ProjectId).HasColumnName("project_id").IsRequired();
        builder.Property(x => x.PropertySiteId).HasColumnName("property_site_id").IsRequired();
        builder.Property(x => x.Description).HasColumnName("description").HasMaxLength(2000).IsRequired();
        builder.Property(x => x.SuspectedAssetId).HasColumnName("suspected_asset_id");
        builder.Property(x => x.PreferredStartsAt).HasColumnName("preferred_starts_at");
        builder.Property(x => x.InstallationWarrantyEndsAt).HasColumnName("installation_warranty_ends_at");
        builder.Property(x => x.WithinInstallationWarranty).HasColumnName("within_installation_warranty").IsRequired();
        builder.Property(x => x.WarrantySourceRequestId).HasColumnName("warranty_source_request_id");
        builder.Property(x => x.WithinRepairWarranty).HasColumnName("within_repair_warranty").IsRequired();
        builder.Property(x => x.MaintenanceEntitlementId).HasColumnName("maintenance_entitlement_id");
        builder.Property(x => x.OriginRequestId).HasColumnName("origin_request_id");
        builder.Property(x => x.DuplicateOfRequestId).HasColumnName("duplicate_of_request_id");
        builder.Property(x => x.Outcome).HasColumnName("outcome").HasMaxLength(30);
        builder.Property(x => x.CustomerDecisionReason).HasColumnName("customer_decision_reason").HasMaxLength(1000);
        builder.Property(x => x.AcceptanceRequestedAt).HasColumnName("acceptance_requested_at");
        builder.Property(x => x.LastAcceptanceReminderAt).HasColumnName("last_acceptance_reminder_at");
        builder.Property(x => x.CompletedAt).HasColumnName("completed_at");
        builder.Property(x => x.RepairWarrantyMonths).HasColumnName("repair_warranty_months");
        builder.Property(x => x.RepairWarrantyStartsAt).HasColumnName("repair_warranty_starts_at");
        builder.Property(x => x.RepairWarrantyEndsAt).HasColumnName("repair_warranty_ends_at");
        builder.Property(x => x.CancellationRequestedAt).HasColumnName("cancellation_requested_at");
        builder.Property(x => x.CancellationReason).HasColumnName("cancellation_reason").HasMaxLength(1000);
        builder.Property(x => x.CancelledAt).HasColumnName("cancelled_at");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(x => x.Revision).HasColumnName("revision").IsConcurrencyToken().IsRequired();
        builder.HasIndex(x => x.RequestNumber).IsUnique().HasDatabaseName("ux_service_request_number");
        builder.HasIndex(x => new { x.ProjectId, x.Status }).HasDatabaseName("ix_service_request_project_status");
        builder.HasIndex(x => new { x.CustomerUserId, x.CreatedAt }).HasDatabaseName("ix_service_request_customer_created_at");
        builder.HasIndex(x => new { x.Status, x.UpdatedAt }).HasDatabaseName("ix_service_request_status_updated_at");
        // One live booking per maintenance visit entitlement (no visit can be booked twice at the same time).
        builder.HasIndex(x => x.MaintenanceEntitlementId).IsUnique()
            .HasFilter($"maintenance_entitlement_id IS NOT NULL AND status NOT IN ('{ServiceRequestStatuses.Completed}', '{ServiceRequestStatuses.NotCompleted}', "
                + $"'{ServiceRequestStatuses.Cancelled}', '{ServiceRequestStatuses.ClosedDuplicate}')")
            .HasDatabaseName("ux_service_request_live_entitlement");
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.CustomerUserId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_service_request_customer");
        builder.HasOne<InstallationProject>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_service_request_project");
        builder.HasOne<PropertySite>().WithMany().HasForeignKey(x => x.PropertySiteId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_service_request_property_site");
        builder.HasOne<InstalledAsset>().WithMany().HasForeignKey(x => x.SuspectedAssetId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_service_request_suspected_asset");
        builder.HasOne<MaintenanceEntitlement>().WithMany().HasForeignKey(x => x.MaintenanceEntitlementId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_service_request_entitlement");
        builder.HasOne<ServiceRequest>().WithMany().HasForeignKey(x => x.WarrantySourceRequestId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_service_request_warranty_source");
        builder.HasOne<ServiceRequest>().WithMany().HasForeignKey(x => x.OriginRequestId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_service_request_origin");
        builder.HasOne<ServiceRequest>().WithMany().HasForeignKey(x => x.DuplicateOfRequestId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_service_request_duplicate_of");
    }
}

public sealed class ServiceItemConfiguration : IEntityTypeConfiguration<ServiceItem>
{
    public void Configure(EntityTypeBuilder<ServiceItem> builder)
    {
        builder.ToTable("service_item", t =>
        {
            t.HasCheckConstraint("ck_service_item_coverage", $"coverage IN ({AfterSalesSql.In(ItemCoverages.All)})");
            t.HasCheckConstraint("ck_service_item_status", $"status IN ({AfterSalesSql.In(ItemStatuses.All)})");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.RequestId).HasColumnName("request_id").IsRequired();
        builder.Property(x => x.AssetId).HasColumnName("asset_id");
        builder.Property(x => x.Description).HasColumnName("description").HasMaxLength(500).IsRequired();
        builder.Property(x => x.Diagnosis).HasColumnName("diagnosis").HasMaxLength(2000).IsRequired();
        builder.Property(x => x.RequiredParts).HasColumnName("required_parts").HasMaxLength(1000);
        builder.Property(x => x.Coverage).HasColumnName("coverage").HasMaxLength(30).IsRequired();
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(20).IsRequired();
        builder.Property(x => x.IsSupplementary).HasColumnName("is_supplementary").IsRequired();
        builder.Property(x => x.QuotationId).HasColumnName("quotation_id");
        builder.Property(x => x.CoverageReason).HasColumnName("coverage_reason").HasMaxLength(1000);
        builder.Property(x => x.WorkResult).HasColumnName("work_result").HasMaxLength(2000);
        builder.Property(x => x.PartsUsed).HasColumnName("parts_used").HasMaxLength(1000);
        builder.Property(x => x.ReportedByUserId).HasColumnName("reported_by_user_id").IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.HasIndex(x => x.RequestId).HasDatabaseName("ix_service_item_request");
        builder.HasOne<ServiceRequest>().WithMany().HasForeignKey(x => x.RequestId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_service_item_request");
        builder.HasOne<InstalledAsset>().WithMany().HasForeignKey(x => x.AssetId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_service_item_asset");
        builder.HasOne<ServiceQuotation>().WithMany().HasForeignKey(x => x.QuotationId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_service_item_quotation");
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.ReportedByUserId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_service_item_reported_by");
    }
}

public sealed class ServiceVisitConfiguration : IEntityTypeConfiguration<ServiceVisit>
{
    public void Configure(EntityTypeBuilder<ServiceVisit> builder)
    {
        builder.ToTable("service_visit", t =>
        {
            t.HasCheckConstraint("ck_service_visit_purpose", $"purpose IN ({AfterSalesSql.In(VisitPurposes.All)})");
            t.HasCheckConstraint("ck_service_visit_status", $"status IN ({AfterSalesSql.In(VisitStatuses.All)})");
            t.HasCheckConstraint("ck_service_visit_range", "ends_at > starts_at");
            t.HasCheckConstraint("ck_service_visit_reported", $"status <> '{VisitStatuses.Reported}' OR (reported_at IS NOT NULL AND report IS NOT NULL)");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.RequestId).HasColumnName("request_id").IsRequired();
        builder.Property(x => x.Purpose).HasColumnName("purpose").HasMaxLength(20).IsRequired();
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(20).IsRequired();
        builder.Property(x => x.TechnicianUserId).HasColumnName("technician_user_id");
        builder.Property(x => x.StartsAt).HasColumnName("starts_at").IsRequired();
        builder.Property(x => x.EndsAt).HasColumnName("ends_at").IsRequired();
        builder.Property(x => x.CompanyRescheduleReason).HasColumnName("company_reschedule_reason").HasMaxLength(500);
        builder.Property(x => x.CustomerConfirmationRequired).HasColumnName("customer_confirmation_required").IsRequired();
        builder.Property(x => x.CustomerConfirmedAt).HasColumnName("customer_confirmed_at");
        builder.Property(x => x.StartedAt).HasColumnName("started_at");
        builder.Property(x => x.Report).HasColumnName("report").HasMaxLength(4000);
        builder.Property(x => x.ReportedAt).HasColumnName("reported_at");
        builder.Property(x => x.FindingNote).HasColumnName("finding_note").HasMaxLength(2000);
        builder.Property(x => x.AssignedByUserId).HasColumnName("assigned_by_user_id").IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.HasIndex(x => x.RequestId).HasDatabaseName("ix_service_visit_request");
        builder.HasIndex(x => new { x.TechnicianUserId, x.StartsAt }).HasDatabaseName("ix_service_visit_technician_starts_at");
        builder.HasOne<ServiceRequest>().WithMany().HasForeignKey(x => x.RequestId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_service_visit_request");
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.TechnicianUserId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_service_visit_technician");
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.AssignedByUserId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_service_visit_assigned_by");
    }
}

public sealed class ServiceEvidenceConfiguration : IEntityTypeConfiguration<ServiceEvidence>
{
    public void Configure(EntityTypeBuilder<ServiceEvidence> builder)
    {
        builder.ToTable("service_evidence");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.RequestId).HasColumnName("request_id").IsRequired();
        builder.Property(x => x.VisitId).HasColumnName("visit_id");
        builder.Property(x => x.FileId).HasColumnName("file_id").IsRequired();
        builder.Property(x => x.Caption).HasColumnName("caption").HasMaxLength(300);
        builder.Property(x => x.AddedByUserId).HasColumnName("added_by_user_id").IsRequired();
        builder.Property(x => x.AddedAt).HasColumnName("added_at").IsRequired();
        builder.HasIndex(x => x.FileId).IsUnique().HasDatabaseName("ux_service_evidence_file");
        builder.HasIndex(x => x.RequestId).HasDatabaseName("ix_service_evidence_request");
        builder.HasOne<ServiceRequest>().WithMany().HasForeignKey(x => x.RequestId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_service_evidence_request");
        builder.HasOne<ServiceVisit>().WithMany().HasForeignKey(x => x.VisitId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_service_evidence_visit");
        builder.HasOne<StoredFile>().WithMany().HasForeignKey(x => x.FileId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_service_evidence_file");
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.AddedByUserId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_service_evidence_added_by");
    }
}

public sealed class ServiceQuotationConfiguration : IEntityTypeConfiguration<ServiceQuotation>
{
    public void Configure(EntityTypeBuilder<ServiceQuotation> builder)
    {
        builder.ToTable("service_quotation", t =>
        {
            t.HasCheckConstraint("ck_service_quotation_status", $"status IN ({AfterSalesSql.In(QuotationStatuses.All)})");
            t.HasCheckConstraint("ck_service_quotation_amounts",
                "parts_amount >= 0 AND labor_amount >= 0 AND total_amount = parts_amount + labor_amount AND total_amount > 0 "
                + "AND disaster_support_amount >= 0 AND disaster_support_amount <= total_amount AND customer_amount = total_amount - disaster_support_amount");
            t.HasCheckConstraint("ck_service_quotation_expiry", "expires_at > issued_at");
            t.HasCheckConstraint("ck_service_quotation_paid", $"status <> '{QuotationStatuses.Paid}' OR paid_at IS NOT NULL");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.RequestId).HasColumnName("request_id").IsRequired();
        builder.Property(x => x.Version).HasColumnName("version").IsRequired();
        builder.Property(x => x.IsSupplementary).HasColumnName("is_supplementary").IsRequired();
        builder.Property(x => x.SupersedesQuotationId).HasColumnName("supersedes_quotation_id");
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(20).IsRequired();
        builder.Property(x => x.SalesUserId).HasColumnName("sales_user_id").IsRequired();
        builder.Property(x => x.LinesJson).HasColumnName("lines").HasColumnType("jsonb").IsRequired();
        builder.Property(x => x.PartsAmount).HasColumnName("parts_amount").HasPrecision(18, 2).IsRequired();
        builder.Property(x => x.LaborAmount).HasColumnName("labor_amount").HasPrecision(18, 2).IsRequired();
        builder.Property(x => x.TotalAmount).HasColumnName("total_amount").HasPrecision(18, 2).IsRequired();
        builder.Property(x => x.DisasterSupportAmount).HasColumnName("disaster_support_amount").HasPrecision(18, 2).IsRequired();
        builder.Property(x => x.CustomerAmount).HasColumnName("customer_amount").HasPrecision(18, 2).IsRequired();
        builder.Property(x => x.DisasterClaimId).HasColumnName("disaster_claim_id");
        builder.Property(x => x.RepairWarrantyMonths).HasColumnName("repair_warranty_months");
        builder.Property(x => x.Note).HasColumnName("note").HasMaxLength(1000);
        builder.Property(x => x.IssuedAt).HasColumnName("issued_at").IsRequired();
        builder.Property(x => x.ExpiresAt).HasColumnName("expires_at").IsRequired();
        builder.Property(x => x.AcceptedAt).HasColumnName("accepted_at");
        builder.Property(x => x.RejectedAt).HasColumnName("rejected_at");
        builder.Property(x => x.PaidAt).HasColumnName("paid_at");
        builder.Property(x => x.CurrentPaymentAttemptId).HasColumnName("current_payment_attempt_id");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(x => x.Revision).HasColumnName("revision").IsConcurrencyToken().IsRequired();
        builder.HasIndex(x => new { x.RequestId, x.Version }).IsUnique().HasDatabaseName("ux_service_quotation_request_version");
        // At most one open (ISSUED / ACCEPTED) quotation per request.
        builder.HasIndex(x => x.RequestId).IsUnique()
            .HasFilter($"status IN ('{QuotationStatuses.Issued}', '{QuotationStatuses.Accepted}')").HasDatabaseName("ux_service_quotation_one_open");
        builder.HasIndex(x => new { x.Status, x.ExpiresAt }).HasDatabaseName("ix_service_quotation_status_expires_at");
        builder.HasOne<ServiceRequest>().WithMany().HasForeignKey(x => x.RequestId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_service_quotation_request");
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.SalesUserId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_service_quotation_sales");
        builder.HasOne<ServiceQuotation>().WithMany().HasForeignKey(x => x.SupersedesQuotationId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_service_quotation_supersedes");
        builder.HasOne<DisasterClaim>().WithMany().HasForeignKey(x => x.DisasterClaimId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_service_quotation_claim");
        builder.HasOne<PaymentAttempt>().WithMany().HasForeignKey(x => x.CurrentPaymentAttemptId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_service_quotation_current_payment_attempt");
    }
}

public sealed class DisasterClaimConfiguration : IEntityTypeConfiguration<DisasterClaim>
{
    public void Configure(EntityTypeBuilder<DisasterClaim> builder)
    {
        builder.ToTable("disaster_claim", t =>
        {
            t.HasCheckConstraint("ck_disaster_claim_status", $"status IN ({AfterSalesSql.In(ClaimStatuses.All)})");
            t.HasCheckConstraint("ck_disaster_claim_type", $"disaster_type IN ({AfterSalesSql.In(DisasterTypes.All)})");
            t.HasCheckConstraint("ck_disaster_claim_support",
                "coverage_percent BETWEEN 0 AND 100 AND support_reserved >= 0 AND support_consumed >= 0 AND support_reserved + support_consumed <= per_incident_limit");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.RequestId).HasColumnName("request_id").IsRequired();
        builder.Property(x => x.SubscriptionId).HasColumnName("subscription_id");
        builder.Property(x => x.DisasterType).HasColumnName("disaster_type").HasMaxLength(20).IsRequired();
        builder.Property(x => x.IncidentOccurredAt).HasColumnName("incident_occurred_at").IsRequired();
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(30).IsRequired();
        builder.Property(x => x.DecisionReason).HasColumnName("decision_reason").HasMaxLength(1000);
        builder.Property(x => x.DecidedByUserId).HasColumnName("decided_by_user_id");
        builder.Property(x => x.DecidedAt).HasColumnName("decided_at");
        builder.Property(x => x.ReconsiderationRequestedAt).HasColumnName("reconsideration_requested_at");
        builder.Property(x => x.ReconsiderationNote).HasColumnName("reconsideration_note").HasMaxLength(1000);
        builder.Property(x => x.FirstDecisionReason).HasColumnName("first_decision_reason").HasMaxLength(1000);
        builder.Property(x => x.CoveragePercent).HasColumnName("coverage_percent").HasPrecision(5, 2).IsRequired();
        builder.Property(x => x.PerIncidentLimit).HasColumnName("per_incident_limit").HasPrecision(18, 2).IsRequired();
        builder.Property(x => x.SupportReserved).HasColumnName("support_reserved").HasPrecision(18, 2).IsRequired();
        builder.Property(x => x.SupportConsumed).HasColumnName("support_consumed").HasPrecision(18, 2).IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(x => x.Revision).HasColumnName("revision").IsConcurrencyToken().IsRequired();
        builder.HasIndex(x => x.RequestId).IsUnique().HasDatabaseName("ux_disaster_claim_request");
        builder.HasIndex(x => x.SubscriptionId).HasDatabaseName("ix_disaster_claim_subscription");
        builder.HasOne<ServiceRequest>().WithMany().HasForeignKey(x => x.RequestId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_disaster_claim_request");
        builder.HasOne<ServiceSubscription>().WithMany().HasForeignKey(x => x.SubscriptionId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_disaster_claim_subscription");
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.DecidedByUserId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_disaster_claim_decided_by");
    }
}

public sealed class SalesTaskConfiguration : IEntityTypeConfiguration<SalesTask>
{
    public void Configure(EntityTypeBuilder<SalesTask> builder)
    {
        builder.ToTable("sales_task", t =>
        {
            t.HasCheckConstraint("ck_sales_task_type", $"task_type IN ({AfterSalesSql.In(SalesTaskTypes.All)})");
            t.HasCheckConstraint("ck_sales_task_status", $"status IN ({AfterSalesSql.In(SalesTaskStatuses.All)})");
            t.HasCheckConstraint("ck_sales_task_owner", $"status = '{SalesTaskStatuses.Open}' OR status = '{SalesTaskStatuses.Cancelled}' OR assigned_sales_user_id IS NOT NULL");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.TaskType).HasColumnName("task_type").HasMaxLength(30).IsRequired();
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(20).IsRequired();
        builder.Property(x => x.RequestId).HasColumnName("request_id");
        builder.Property(x => x.EntitlementId).HasColumnName("entitlement_id");
        builder.Property(x => x.CustomerUserId).HasColumnName("customer_user_id").IsRequired();
        builder.Property(x => x.AssignedSalesUserId).HasColumnName("assigned_sales_user_id");
        builder.Property(x => x.ClaimedAt).HasColumnName("claimed_at");
        builder.Property(x => x.StartedAt).HasColumnName("started_at");
        builder.Property(x => x.CompletedAt).HasColumnName("completed_at");
        builder.Property(x => x.Outcome).HasColumnName("outcome").HasMaxLength(30);
        builder.Property(x => x.Note).HasColumnName("note").HasMaxLength(2000);
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(x => x.Revision).HasColumnName("revision").IsConcurrencyToken().IsRequired();
        builder.HasIndex(x => new { x.Status, x.CreatedAt }).HasDatabaseName("ix_sales_task_status_created_at");
        builder.HasIndex(x => new { x.AssignedSalesUserId, x.Status }).HasDatabaseName("ix_sales_task_assignee_status");
        // One open task of a type per request / per maintenance visit (the queue never shows the same work twice).
        builder.HasIndex(x => new { x.RequestId, x.TaskType }).IsUnique()
            .HasFilter($"request_id IS NOT NULL AND status IN ('{SalesTaskStatuses.Open}', '{SalesTaskStatuses.Claimed}', '{SalesTaskStatuses.InProgress}')")
            .HasDatabaseName("ux_sales_task_open_request");
        builder.HasIndex(x => new { x.EntitlementId, x.TaskType }).IsUnique()
            .HasFilter($"entitlement_id IS NOT NULL AND status IN ('{SalesTaskStatuses.Open}', '{SalesTaskStatuses.Claimed}', '{SalesTaskStatuses.InProgress}')")
            .HasDatabaseName("ux_sales_task_open_entitlement");
        builder.HasOne<ServiceRequest>().WithMany().HasForeignKey(x => x.RequestId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_sales_task_request");
        builder.HasOne<MaintenanceEntitlement>().WithMany().HasForeignKey(x => x.EntitlementId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_sales_task_entitlement");
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.CustomerUserId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_sales_task_customer");
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.AssignedSalesUserId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_sales_task_assignee");
    }
}

public sealed class RefundRecordConfiguration : IEntityTypeConfiguration<RefundRecord>
{
    public void Configure(EntityTypeBuilder<RefundRecord> builder)
    {
        builder.ToTable("refund_record", t =>
        {
            t.HasCheckConstraint("ck_refund_record_status", $"status IN ({AfterSalesSql.In(RefundStatuses.All)})");
            t.HasCheckConstraint("ck_refund_record_amounts", "original_amount >= 0 AND (approved_amount IS NULL OR approved_amount BETWEEN 0 AND original_amount)");
            t.HasCheckConstraint("ck_refund_record_refunded",
                $"status <> '{RefundStatuses.Refunded}' OR (refunded_at IS NOT NULL AND recorded_by_user_id IS NOT NULL AND approved_amount IS NOT NULL AND approved_by_user_id IS NOT NULL)");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.CustomerUserId).HasColumnName("customer_user_id").IsRequired();
        builder.Property(x => x.PaymentAttemptId).HasColumnName("payment_attempt_id");
        builder.Property(x => x.SubscriptionId).HasColumnName("subscription_id");
        builder.Property(x => x.QuotationId).HasColumnName("quotation_id");
        builder.Property(x => x.RequestId).HasColumnName("request_id");
        builder.Property(x => x.OriginalAmount).HasColumnName("original_amount").HasPrecision(18, 2).IsRequired();
        builder.Property(x => x.ApprovedAmount).HasColumnName("approved_amount").HasPrecision(18, 2);
        builder.Property(x => x.Reason).HasColumnName("reason").HasMaxLength(1000).IsRequired();
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(20).IsRequired();
        builder.Property(x => x.ApprovedByUserId).HasColumnName("approved_by_user_id");
        builder.Property(x => x.ApprovedAt).HasColumnName("approved_at");
        builder.Property(x => x.DecisionNote).HasColumnName("decision_note").HasMaxLength(1000);
        builder.Property(x => x.RecordedByUserId).HasColumnName("recorded_by_user_id");
        builder.Property(x => x.RefundedAt).HasColumnName("refunded_at");
        builder.Property(x => x.RefundReference).HasColumnName("refund_reference").HasMaxLength(100);
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(x => x.Revision).HasColumnName("revision").IsConcurrencyToken().IsRequired();
        builder.HasIndex(x => new { x.Status, x.CreatedAt }).HasDatabaseName("ix_refund_record_status_created_at");
        builder.HasIndex(x => x.RefundedAt).HasDatabaseName("ix_refund_record_refunded_at");
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.CustomerUserId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_refund_record_customer");
        builder.HasOne<SmartSolar.Modules.Payments.PaymentAttempt>().WithMany().HasForeignKey(x => x.PaymentAttemptId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_refund_record_payment_attempt");
        builder.HasOne<ServiceSubscription>().WithMany().HasForeignKey(x => x.SubscriptionId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_refund_record_subscription");
        builder.HasOne<ServiceQuotation>().WithMany().HasForeignKey(x => x.QuotationId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_refund_record_quotation");
        builder.HasOne<ServiceRequest>().WithMany().HasForeignKey(x => x.RequestId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_refund_record_request");
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.ApprovedByUserId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_refund_record_approved_by");
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.RecordedByUserId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_refund_record_recorded_by");
    }
}

public sealed class AfterSalesEventConfiguration : IEntityTypeConfiguration<AfterSalesEvent>
{
    public void Configure(EntityTypeBuilder<AfterSalesEvent> builder)
    {
        builder.ToTable("after_sales_event");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.SubjectType).HasColumnName("subject_type").HasMaxLength(30).IsRequired();
        builder.Property(x => x.SubjectId).HasColumnName("subject_id").IsRequired();
        builder.Property(x => x.EventType).HasColumnName("event_type").HasMaxLength(50).IsRequired();
        builder.Property(x => x.ActorUserId).HasColumnName("actor_user_id");
        builder.Property(x => x.Detail).HasColumnName("detail").HasMaxLength(2000);
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.HasIndex(x => new { x.SubjectType, x.SubjectId, x.CreatedAt }).HasDatabaseName("ix_after_sales_event_subject");
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.ActorUserId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_after_sales_event_actor");
    }
}

public sealed class AfterSalesNotificationConfiguration : IEntityTypeConfiguration<AfterSalesNotification>
{
    public void Configure(EntityTypeBuilder<AfterSalesNotification> builder)
    {
        builder.ToTable("after_sales_notification");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.RecipientUserId).HasColumnName("recipient_user_id").IsRequired();
        builder.Property(x => x.Type).HasColumnName("type").HasMaxLength(50).IsRequired();
        builder.Property(x => x.Message).HasColumnName("message").HasMaxLength(500).IsRequired();
        builder.Property(x => x.RequestId).HasColumnName("request_id");
        builder.Property(x => x.SubscriptionId).HasColumnName("subscription_id");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(x => x.ReadAt).HasColumnName("read_at");
        builder.HasIndex(x => new { x.RecipientUserId, x.CreatedAt }).HasDatabaseName("ix_after_sales_notification_recipient");
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.RecipientUserId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_after_sales_notification_recipient");
        builder.HasOne<ServiceRequest>().WithMany().HasForeignKey(x => x.RequestId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_after_sales_notification_request");
        builder.HasOne<ServiceSubscription>().WithMany().HasForeignKey(x => x.SubscriptionId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_after_sales_notification_subscription");
    }
}
