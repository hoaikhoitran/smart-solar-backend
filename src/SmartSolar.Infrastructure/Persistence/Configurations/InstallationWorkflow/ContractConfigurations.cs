using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartSolar.Modules.CustomerContracts;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.Identity.Entities;
using SmartSolar.Modules.PreSurvey.Entities;
using SmartSolar.Modules.Quotations;

namespace SmartSolar.Infrastructure.Persistence.Configurations.InstallationWorkflow;

public sealed class CustomerContractConfiguration : IEntityTypeConfiguration<CustomerContract>
{
    public void Configure(EntityTypeBuilder<CustomerContract> builder)
    {
        builder.ToTable("customer_contract", t =>
        {
            t.HasCheckConstraint("ck_customer_contract_status", $"status IN ({string.Join(", ", ContractStatuses.All.Select(s => $"'{s}'"))})");
            t.HasCheckConstraint("ck_customer_contract_amount", "total_amount > 0 AND currency = 'VND'");
            t.HasCheckConstraint("ck_customer_contract_package_breakdown",
                "(installation_total IS NULL) = (service_packages_total IS NULL) AND (installation_total IS NULL OR "
                + "(installation_total > 0 AND service_packages_total > 0 AND installation_total + service_packages_total = total_amount))");
            t.HasCheckConstraint("ck_customer_contract_version", "version >= 1");
            // A discarded contract was a never-issued draft: it has no hash, document, signature or payment.
            t.HasCheckConstraint("ck_customer_contract_issued_hash",
                $"status IN ('{ContractStatuses.Draft}', '{ContractStatuses.Discarded}') OR (canonical_hash IS NOT NULL AND issued_at IS NOT NULL)");
            t.HasCheckConstraint("ck_customer_contract_published_document",
                $"status IN ('{ContractStatuses.Draft}', '{ContractStatuses.Issued}', '{ContractStatuses.Superseded}', '{ContractStatuses.Discarded}') OR (official_document_file_id IS NOT NULL AND official_document_sha256 IS NOT NULL)"
                // 30/70 plan (2026-11-19): the paper is signed on site from the frozen contract content; no official PDF upload step.
                + " OR payment_plan IS NOT NULL");
            t.HasCheckConstraint("ck_customer_contract_discarded",
                $"(status = '{ContractStatuses.Discarded}') = (discarded_at IS NOT NULL AND discarded_by_user_id IS NOT NULL AND discard_reason IS NOT NULL)");
            t.HasCheckConstraint("ck_customer_contract_discarded_never_issued",
                $"status <> '{ContractStatuses.Discarded}' OR (issued_at IS NULL AND signed_at IS NULL AND paid_at IS NULL AND previous_contract_id IS NULL)");
            t.HasCheckConstraint("ck_customer_contract_signed_review",
                $"status NOT IN ('{ContractStatuses.Signed}', '{ContractStatuses.Paid}') OR (signed_document_file_id IS NOT NULL AND reviewed_by_user_id IS NOT NULL AND signed_at IS NOT NULL)"
                // 30/70 plan: signed = finalized set of photographed pages (contract_signature_set), no single scan and no review step.
                + " OR (payment_plan IS NOT NULL AND signed_at IS NOT NULL)");
            t.HasCheckConstraint("ck_customer_contract_payment_plan",
                $"payment_plan IS NULL OR payment_plan IN ({string.Join(", ", PaymentPlans.All.Select(p => $"'{p}'"))})");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.ContractNumber).HasColumnName("contract_number").HasMaxLength(30).IsRequired();
        builder.Property(x => x.Version).HasColumnName("version").IsRequired();
        builder.Property(x => x.PreviousContractId).HasColumnName("previous_contract_id");
        builder.Property(x => x.QuotationId).HasColumnName("quotation_id").IsRequired();
        builder.Property(x => x.QuotationVersion).HasColumnName("quotation_version").IsRequired();
        builder.Property(x => x.SurveyRequestId).HasColumnName("survey_request_id").IsRequired();
        builder.Property(x => x.CustomerUserId).HasColumnName("customer_user_id").IsRequired();
        // Nullable since 2026-11-17: contracts drafted on customer acceptance have no Sales owner (legacy rows keep theirs).
        builder.Property(x => x.SalesUserId).HasColumnName("sales_user_id").IsRequired(false);
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(30).IsRequired();
        builder.Property(x => x.Currency).HasColumnName("currency").HasMaxLength(3).IsRequired();
        builder.Property(x => x.TotalAmount).HasColumnName("total_amount").HasPrecision(18, 2).IsRequired();
        builder.Property(x => x.InstallationTotal).HasColumnName("installation_total").HasPrecision(18, 2);
        builder.Property(x => x.ServicePackagesTotal).HasColumnName("service_packages_total").HasPrecision(18, 2);
        builder.Property(x => x.PricingSnapshot).HasColumnName("pricing_snapshot").HasColumnType("jsonb").IsRequired();
        builder.Property(x => x.CustomerSnapshot).HasColumnName("customer_snapshot").HasColumnType("jsonb").IsRequired();
        builder.Property(x => x.Terms).HasColumnName("terms").HasMaxLength(8000);
        builder.Property(x => x.WarrantyMonths).HasColumnName("warranty_months");
        builder.Property(x => x.CanonicalHash).HasColumnName("canonical_hash").HasMaxLength(64);
        builder.Property(x => x.OfficialDocumentFileId).HasColumnName("official_document_file_id");
        builder.Property(x => x.OfficialDocumentSha256).HasColumnName("official_document_sha256").HasMaxLength(64);
        builder.Property(x => x.OfficialDocumentConfirmedByUserId).HasColumnName("official_document_confirmed_by_user_id");
        builder.Property(x => x.OfficialDocumentConfirmedAt).HasColumnName("official_document_confirmed_at");
        builder.Property(x => x.SignedDocumentFileId).HasColumnName("signed_document_file_id");
        builder.Property(x => x.SignedDocumentSha256).HasColumnName("signed_document_sha256").HasMaxLength(64);
        builder.Property(x => x.SignedSubmittedAt).HasColumnName("signed_submitted_at");
        builder.Property(x => x.ReviewedByUserId).HasColumnName("reviewed_by_user_id");
        builder.Property(x => x.ReviewedAt).HasColumnName("reviewed_at");
        builder.Property(x => x.RejectionReason).HasColumnName("rejection_reason").HasMaxLength(2000);
        builder.Property(x => x.IssuedAt).HasColumnName("issued_at");
        builder.Property(x => x.PublishedAt).HasColumnName("published_at");
        builder.Property(x => x.SignedAt).HasColumnName("signed_at");
        builder.Property(x => x.PaidAt).HasColumnName("paid_at");
        builder.Property(x => x.DiscardedAt).HasColumnName("discarded_at");
        builder.Property(x => x.DiscardedByUserId).HasColumnName("discarded_by_user_id");
        builder.Property(x => x.DiscardReason).HasColumnName("discard_reason").HasMaxLength(1000);
        builder.Property(x => x.PaymentPlan).HasColumnName("payment_plan").HasMaxLength(30);
        builder.Property(x => x.TermsVersion).HasColumnName("terms_version").HasMaxLength(60);
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(x => x.Revision).HasColumnName("revision").IsConcurrencyToken().IsRequired();

        builder.HasIndex(x => new { x.ContractNumber, x.Version }).IsUnique().HasDatabaseName("ux_customer_contract_number_version");
        // One live contract and at most one amendment draft per quotation. A DISCARDED initial draft stays inside "one live", so a discard
        // never makes room for a second contract of the same quotation (re-contracting after a discard is not a defined process).
        builder.HasIndex(x => x.QuotationId).IsUnique()
            .HasFilter($"status NOT IN ('{ContractStatuses.Draft}', '{ContractStatuses.Superseded}')").HasDatabaseName("ux_customer_contract_one_live");
        builder.HasIndex(x => new { x.QuotationId, x.Status }).IsUnique()
            .HasFilter($"status = '{ContractStatuses.Draft}'").HasDatabaseName("ux_customer_contract_one_draft");
        builder.HasIndex(x => new { x.CustomerUserId, x.Status }).HasDatabaseName("ix_customer_contract_customer_status");
        builder.HasIndex(x => x.SurveyRequestId).HasDatabaseName("ix_customer_contract_survey_request");

        builder.HasOne<Quotation>().WithMany().HasForeignKey(x => x.QuotationId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.DiscardedByUserId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_customer_contract_discarded_by");
        builder.HasOne<SurveyRequest>().WithMany().HasForeignKey(x => x.SurveyRequestId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<CustomerContract>().WithMany().HasForeignKey(x => x.PreviousContractId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.CustomerUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.SalesUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.ReviewedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.OfficialDocumentConfirmedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<StoredFile>().WithMany().HasForeignKey(x => x.OfficialDocumentFileId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<StoredFile>().WithMany().HasForeignKey(x => x.SignedDocumentFileId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class ContractSignatureSubmissionConfiguration : IEntityTypeConfiguration<ContractSignatureSubmission>
{
    public void Configure(EntityTypeBuilder<ContractSignatureSubmission> builder)
    {
        builder.ToTable("contract_signature_submission", t => t.HasCheckConstraint("ck_contract_signature_submission_outcome",
            $"outcome IN ('{SignatureOutcomes.Pending}', '{SignatureOutcomes.Approved}', '{SignatureOutcomes.Rejected}')"));
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.ContractId).HasColumnName("contract_id").IsRequired();
        builder.Property(x => x.FileId).HasColumnName("file_id").IsRequired();
        builder.Property(x => x.FileSha256).HasColumnName("file_sha256").HasMaxLength(64).IsRequired();
        builder.Property(x => x.OfficialDocumentSha256).HasColumnName("official_document_sha256").HasMaxLength(64).IsRequired();
        builder.Property(x => x.SubmittedByUserId).HasColumnName("submitted_by_user_id").IsRequired();
        builder.Property(x => x.SubmittedAt).HasColumnName("submitted_at").IsRequired();
        builder.Property(x => x.Outcome).HasColumnName("outcome").HasMaxLength(20).IsRequired();
        builder.Property(x => x.ReviewedByUserId).HasColumnName("reviewed_by_user_id");
        builder.Property(x => x.ReviewedAt).HasColumnName("reviewed_at");
        builder.Property(x => x.RejectionReason).HasColumnName("rejection_reason").HasMaxLength(2000);
        builder.HasIndex(x => x.FileId).IsUnique().HasDatabaseName("ux_contract_signature_submission_file");
        // At most one submission waiting for review per contract.
        builder.HasIndex(x => new { x.ContractId, x.Outcome }).IsUnique().HasFilter($"outcome = '{SignatureOutcomes.Pending}'")
            .HasDatabaseName("ux_contract_signature_submission_one_pending");
        builder.HasOne<CustomerContract>().WithMany().HasForeignKey(x => x.ContractId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<StoredFile>().WithMany().HasForeignKey(x => x.FileId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.SubmittedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.ReviewedByUserId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class ContractEventConfiguration : IEntityTypeConfiguration<ContractEvent>
{
    public void Configure(EntityTypeBuilder<ContractEvent> builder)
    {
        builder.ToTable("contract_event");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.ContractId).HasColumnName("contract_id").IsRequired();
        builder.Property(x => x.EventType).HasColumnName("event_type").HasMaxLength(40).IsRequired();
        builder.Property(x => x.FromStatus).HasColumnName("from_status").HasMaxLength(30);
        builder.Property(x => x.ToStatus).HasColumnName("to_status").HasMaxLength(30);
        builder.Property(x => x.ActorUserId).HasColumnName("actor_user_id").IsRequired();
        builder.Property(x => x.Detail).HasColumnName("detail").HasMaxLength(500);
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.HasIndex(x => new { x.ContractId, x.CreatedAt }).HasDatabaseName("ix_contract_event_contract_created_at");
        builder.HasOne<CustomerContract>().WithMany().HasForeignKey(x => x.ContractId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.ActorUserId).OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>
/// Package commitment of a 30/70 contract: one per kind; deposit + balance shares = amount = unit price × panels; at most one subscription is ever
/// provisioned from it (unique index on service_subscription.contract_service_package_id).
/// </summary>
public sealed class ContractServicePackageConfiguration : IEntityTypeConfiguration<ContractServicePackage>
{
    public void Configure(EntityTypeBuilder<ContractServicePackage> builder)
    {
        builder.ToTable("contract_service_package", t =>
        {
            t.HasCheckConstraint("ck_contract_service_package_kind", "kind IN ('MAINTENANCE', 'DISASTER_PROTECTION')");
            t.HasCheckConstraint("ck_contract_service_package_status",
                $"status IN ({string.Join(", ", ContractServicePackageStatuses.All.Select(s => $"'{s}'"))})");
            t.HasCheckConstraint("ck_contract_service_package_amount",
                "unit_price > 0 AND quantity > 0 AND total_amount = unit_price * quantity AND deposit_allocation >= 0 AND balance_allocation >= 0 "
                + "AND deposit_allocation + balance_allocation = total_amount AND duration_months BETWEEN 1 AND 120");
            t.HasCheckConstraint("ck_contract_service_package_provisioned",
                $"(status = '{ContractServicePackageStatuses.Provisioned}') = (subscription_id IS NOT NULL)");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.ContractId).HasColumnName("contract_id").IsRequired();
        builder.Property(x => x.QuotationId).HasColumnName("quotation_id").IsRequired();
        builder.Property(x => x.QuotationServicePackageId).HasColumnName("quotation_service_package_id").IsRequired();
        builder.Property(x => x.Kind).HasColumnName("kind").HasMaxLength(30).IsRequired();
        builder.Property(x => x.PackageId).HasColumnName("package_id").IsRequired();
        builder.Property(x => x.PackageName).HasColumnName("package_name").HasMaxLength(150).IsRequired();
        builder.Property(x => x.Description).HasColumnName("description").HasMaxLength(2000);
        builder.Property(x => x.ImageUrl).HasColumnName("image_url").HasMaxLength(SmartSolar.Modules.Catalog.ProductImages.ProductImageRules.MaxUrlLength);
        builder.Property(x => x.PricingUnit).HasColumnName("pricing_unit").HasMaxLength(30).IsRequired();
        builder.Property(x => x.UnitPrice).HasColumnName("unit_price").HasPrecision(18, 2).IsRequired();
        builder.Property(x => x.Quantity).HasColumnName("quantity").IsRequired();
        builder.Property(x => x.TotalAmount).HasColumnName("total_amount").HasPrecision(18, 2).IsRequired();
        builder.Property(x => x.DurationMonths).HasColumnName("duration_months").IsRequired();
        builder.Property(x => x.VisitMonths).HasColumnName("visit_months").HasMaxLength(200);
        builder.Property(x => x.CoveredDisasterTypes).HasColumnName("covered_disaster_types").HasMaxLength(200);
        builder.Property(x => x.CoveragePercent).HasColumnName("coverage_percent").HasPrecision(5, 2);
        builder.Property(x => x.PerIncidentLimit).HasColumnName("per_incident_limit").HasPrecision(18, 2);
        builder.Property(x => x.TermLimit).HasColumnName("term_limit").HasPrecision(18, 2);
        builder.Property(x => x.CatalogRevision).HasColumnName("catalog_revision").IsRequired();
        builder.Property(x => x.SnapshotAt).HasColumnName("snapshot_at").IsRequired();
        builder.Property(x => x.DepositAllocation).HasColumnName("deposit_allocation").HasPrecision(18, 2).IsRequired();
        builder.Property(x => x.BalanceAllocation).HasColumnName("balance_allocation").HasPrecision(18, 2).IsRequired();
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(30).IsRequired();
        builder.Property(x => x.SubscriptionId).HasColumnName("subscription_id");
        builder.Property(x => x.StatusReason).HasColumnName("status_reason").HasMaxLength(1000);
        builder.Property(x => x.StatusChangedAt).HasColumnName("status_changed_at");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(x => x.Revision).HasColumnName("revision").IsConcurrencyToken().IsRequired();
        builder.HasIndex(x => new { x.ContractId, x.Kind }).IsUnique().HasDatabaseName("ux_contract_service_package_kind");
        builder.HasIndex(x => x.Status).HasDatabaseName("ix_contract_service_package_status");
        builder.HasOne<CustomerContract>().WithMany().HasForeignKey(x => x.ContractId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_contract_service_package_contract");
        builder.HasOne<QuotationServicePackage>().WithMany().HasForeignKey(x => x.QuotationServicePackageId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_contract_service_package_quotation_package");
    }
}
