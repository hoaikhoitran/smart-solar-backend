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
            t.HasCheckConstraint("ck_customer_contract_version", "version >= 1");
            t.HasCheckConstraint("ck_customer_contract_issued_hash",
                $"status = '{ContractStatuses.Draft}' OR (canonical_hash IS NOT NULL AND issued_at IS NOT NULL)");
            t.HasCheckConstraint("ck_customer_contract_published_document",
                $"status IN ('{ContractStatuses.Draft}', '{ContractStatuses.Issued}', '{ContractStatuses.Superseded}') OR (official_document_file_id IS NOT NULL AND official_document_sha256 IS NOT NULL)");
            t.HasCheckConstraint("ck_customer_contract_signed_review",
                $"status NOT IN ('{ContractStatuses.Signed}', '{ContractStatuses.Paid}') OR (signed_document_file_id IS NOT NULL AND reviewed_by_user_id IS NOT NULL AND signed_at IS NOT NULL)");
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
        builder.Property(x => x.SalesUserId).HasColumnName("sales_user_id").IsRequired();
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(30).IsRequired();
        builder.Property(x => x.Currency).HasColumnName("currency").HasMaxLength(3).IsRequired();
        builder.Property(x => x.TotalAmount).HasColumnName("total_amount").HasPrecision(18, 2).IsRequired();
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
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(x => x.Revision).HasColumnName("revision").IsConcurrencyToken().IsRequired();

        builder.HasIndex(x => new { x.ContractNumber, x.Version }).IsUnique().HasDatabaseName("ux_customer_contract_number_version");
        // One live contract and at most one amendment draft per quotation.
        builder.HasIndex(x => x.QuotationId).IsUnique()
            .HasFilter($"status NOT IN ('{ContractStatuses.Draft}', '{ContractStatuses.Superseded}')").HasDatabaseName("ux_customer_contract_one_live");
        builder.HasIndex(x => new { x.QuotationId, x.Status }).IsUnique()
            .HasFilter($"status = '{ContractStatuses.Draft}'").HasDatabaseName("ux_customer_contract_one_draft");
        builder.HasIndex(x => new { x.CustomerUserId, x.Status }).HasDatabaseName("ix_customer_contract_customer_status");
        builder.HasIndex(x => x.SurveyRequestId).HasDatabaseName("ix_customer_contract_survey_request");

        builder.HasOne<Quotation>().WithMany().HasForeignKey(x => x.QuotationId).OnDelete(DeleteBehavior.Restrict);
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
