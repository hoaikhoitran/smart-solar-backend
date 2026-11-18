using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartSolar.Modules.CustomerContracts;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.Identity.Entities;

namespace SmartSolar.Infrastructure.Persistence.Configurations.InstallationWorkflow;

internal static class PlanSql
{
    public static string In(IEnumerable<string> values) => string.Join(", ", values.Select(v => $"'{v}'"));
}

public sealed class ContractInstallmentConfiguration : IEntityTypeConfiguration<ContractInstallment>
{
    public void Configure(EntityTypeBuilder<ContractInstallment> builder)
    {
        builder.ToTable("contract_installment", t =>
        {
            t.HasCheckConstraint("ck_contract_installment_kind", $"kind IN ({PlanSql.In(InstallmentKinds.All)})");
            t.HasCheckConstraint("ck_contract_installment_status", $"status IN ({PlanSql.In(InstallmentStatuses.All)})");
            t.HasCheckConstraint("ck_contract_installment_sequence",
                $"(sequence = 1 AND kind = '{InstallmentKinds.Deposit}') OR (sequence = 2 AND kind = '{InstallmentKinds.Balance}')");
            t.HasCheckConstraint("ck_contract_installment_amount", "amount > 0 AND percent > 0 AND percent < 100");
            // PAID ⇔ settled: when, how much (exactly the installment amount) and by which verified attempt.
            t.HasCheckConstraint("ck_contract_installment_paid",
                $"(status = '{InstallmentStatuses.Paid}') = (paid_at IS NOT NULL AND paid_amount IS NOT NULL AND settled_payment_attempt_id IS NOT NULL)");
            t.HasCheckConstraint("ck_contract_installment_paid_amount", "paid_amount IS NULL OR paid_amount = amount");
            // A payable / paid installment has been opened; the balance has a due date once opened.
            t.HasCheckConstraint("ck_contract_installment_opened",
                $"status NOT IN ('{InstallmentStatuses.Due}', '{InstallmentStatuses.Paid}') OR opened_at IS NOT NULL");
            t.HasCheckConstraint("ck_contract_installment_balance_due",
                $"kind <> '{InstallmentKinds.Balance}' OR status NOT IN ('{InstallmentStatuses.Due}', '{InstallmentStatuses.Paid}') OR due_at IS NOT NULL");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.ContractId).HasColumnName("contract_id").IsRequired();
        builder.Property(x => x.Sequence).HasColumnName("sequence").IsRequired();
        builder.Property(x => x.Kind).HasColumnName("kind").HasMaxLength(20).IsRequired();
        builder.Property(x => x.Percent).HasColumnName("percent").HasPrecision(5, 2).IsRequired();
        builder.Property(x => x.Amount).HasColumnName("amount").HasPrecision(18, 2).IsRequired();
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(20).IsRequired();
        builder.Property(x => x.OpenedAt).HasColumnName("opened_at");
        builder.Property(x => x.DueAt).HasColumnName("due_at");
        builder.Property(x => x.PaidAt).HasColumnName("paid_at");
        builder.Property(x => x.PaidAmount).HasColumnName("paid_amount").HasPrecision(18, 2);
        builder.Property(x => x.PaidLate).HasColumnName("paid_late").IsRequired();
        builder.Property(x => x.CurrentPaymentAttemptId).HasColumnName("current_payment_attempt_id");
        builder.Property(x => x.SettledPaymentAttemptId).HasColumnName("settled_payment_attempt_id");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(x => x.Revision).HasColumnName("revision").IsConcurrencyToken().IsRequired();
        builder.HasIndex(x => new { x.ContractId, x.Sequence }).IsUnique().HasDatabaseName("ux_contract_installment_contract_sequence");
        builder.HasIndex(x => new { x.Status, x.DueAt }).HasDatabaseName("ix_contract_installment_status_due");
        builder.HasOne<CustomerContract>().WithMany().HasForeignKey(x => x.ContractId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_contract_installment_contract");
    }
}

public sealed class ContractSignatureSetConfiguration : IEntityTypeConfiguration<ContractSignatureSet>
{
    public void Configure(EntityTypeBuilder<ContractSignatureSet> builder)
    {
        builder.ToTable("contract_signature_set", t =>
        {
            t.HasCheckConstraint("ck_contract_signature_set_status", $"status IN ({PlanSql.In(SignatureSetStatuses.All)})");
            t.HasCheckConstraint("ck_contract_signature_set_pages", "expected_page_count BETWEEN 1 AND 50");
            t.HasCheckConstraint("ck_contract_signature_set_finalized",
                $"(status = '{SignatureSetStatuses.Finalized}') = (finalized_at IS NOT NULL AND finalized_by_user_id IS NOT NULL)");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.ContractId).HasColumnName("contract_id").IsRequired();
        builder.Property(x => x.Version).HasColumnName("version").IsRequired();
        builder.Property(x => x.ExpectedPageCount).HasColumnName("expected_page_count").IsRequired();
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(20).IsRequired();
        builder.Property(x => x.SignedOn).HasColumnName("signed_on");
        builder.Property(x => x.Note).HasColumnName("note").HasMaxLength(500);
        builder.Property(x => x.CreatedByUserId).HasColumnName("created_by_user_id").IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(x => x.FinalizedByUserId).HasColumnName("finalized_by_user_id");
        builder.Property(x => x.FinalizedAt).HasColumnName("finalized_at");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(x => x.Revision).HasColumnName("revision").IsConcurrencyToken().IsRequired();
        builder.HasIndex(x => new { x.ContractId, x.Version }).IsUnique().HasDatabaseName("ux_contract_signature_set_contract_version");
        // One current (uploading or finalized) set per contract; restarted uploads are SUPERSEDED history.
        builder.HasIndex(x => x.ContractId).IsUnique().HasFilter($"status <> '{SignatureSetStatuses.Superseded}'")
            .HasDatabaseName("ux_contract_signature_set_one_current");
        builder.HasOne<CustomerContract>().WithMany().HasForeignKey(x => x.ContractId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_contract_signature_set_contract");
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_contract_signature_set_created_by");
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.FinalizedByUserId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_contract_signature_set_finalized_by");
    }
}

public sealed class ContractSignedPageConfiguration : IEntityTypeConfiguration<ContractSignedPage>
{
    public void Configure(EntityTypeBuilder<ContractSignedPage> builder)
    {
        builder.ToTable("contract_signed_page", t => t.HasCheckConstraint("ck_contract_signed_page_number", "page_number BETWEEN 1 AND 50"));
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.SignatureSetId).HasColumnName("signature_set_id").IsRequired();
        builder.Property(x => x.ContractId).HasColumnName("contract_id").IsRequired();
        builder.Property(x => x.PageNumber).HasColumnName("page_number").IsRequired();
        builder.Property(x => x.FileId).HasColumnName("file_id").IsRequired();
        builder.Property(x => x.Sha256).HasColumnName("sha256").HasMaxLength(64).IsRequired();
        builder.Property(x => x.ContentType).HasColumnName("content_type").HasMaxLength(100).IsRequired();
        builder.Property(x => x.UploadedByUserId).HasColumnName("uploaded_by_user_id").IsRequired();
        builder.Property(x => x.UploadedAt).HasColumnName("uploaded_at").IsRequired();
        builder.Property(x => x.ReplacedAt).HasColumnName("replaced_at");
        // One current file per page number; a file is never reused for two pages.
        builder.HasIndex(x => new { x.SignatureSetId, x.PageNumber }).IsUnique().HasFilter("replaced_at IS NULL")
            .HasDatabaseName("ux_contract_signed_page_current");
        builder.HasIndex(x => x.FileId).IsUnique().HasDatabaseName("ux_contract_signed_page_file");
        builder.HasIndex(x => x.ContractId).HasDatabaseName("ix_contract_signed_page_contract");
        builder.HasOne<ContractSignatureSet>().WithMany().HasForeignKey(x => x.SignatureSetId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_contract_signed_page_set");
        builder.HasOne<CustomerContract>().WithMany().HasForeignKey(x => x.ContractId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_contract_signed_page_contract");
        builder.HasOne<StoredFile>().WithMany().HasForeignKey(x => x.FileId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_contract_signed_page_file");
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.UploadedByUserId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_contract_signed_page_uploaded_by");
    }
}

public sealed class ContractCollectionCaseConfiguration : IEntityTypeConfiguration<ContractCollectionCase>
{
    public void Configure(EntityTypeBuilder<ContractCollectionCase> builder)
    {
        builder.ToTable("contract_collection_case", t =>
        {
            t.HasCheckConstraint("ck_contract_collection_case_status", $"status IN ({PlanSql.In(CollectionCaseStatuses.All)})");
            t.HasCheckConstraint("ck_contract_collection_case_resolved",
                $"(status = '{CollectionCaseStatuses.Resolved}') = (resolved_at IS NOT NULL AND resolution IS NOT NULL)");
            t.HasCheckConstraint("ck_contract_collection_case_amount", "amount_due > 0");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.ContractId).HasColumnName("contract_id").IsRequired();
        builder.Property(x => x.InstallmentId).HasColumnName("installment_id").IsRequired();
        builder.Property(x => x.CustomerUserId).HasColumnName("customer_user_id").IsRequired();
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(30).IsRequired();
        builder.Property(x => x.OpenedAt).HasColumnName("opened_at").IsRequired();
        builder.Property(x => x.DueAt).HasColumnName("due_at").IsRequired();
        builder.Property(x => x.AmountDue).HasColumnName("amount_due").HasPrecision(18, 2).IsRequired();
        builder.Property(x => x.PromisedPaymentDate).HasColumnName("promised_payment_date");
        builder.Property(x => x.ResolvedAt).HasColumnName("resolved_at");
        builder.Property(x => x.ResolvedByUserId).HasColumnName("resolved_by_user_id");
        builder.Property(x => x.Resolution).HasColumnName("resolution").HasMaxLength(1000);
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(x => x.Revision).HasColumnName("revision").IsConcurrencyToken().IsRequired();
        // One open case per installment (the overdue sweep and concurrent sweeps never open two).
        builder.HasIndex(x => x.InstallmentId).IsUnique().HasFilter($"status <> '{CollectionCaseStatuses.Resolved}'")
            .HasDatabaseName("ux_contract_collection_case_one_open");
        builder.HasIndex(x => new { x.Status, x.OpenedAt }).HasDatabaseName("ix_contract_collection_case_status_opened");
        builder.HasIndex(x => x.ContractId).HasDatabaseName("ix_contract_collection_case_contract");
        builder.HasOne<CustomerContract>().WithMany().HasForeignKey(x => x.ContractId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_contract_collection_case_contract");
        builder.HasOne<ContractInstallment>().WithMany().HasForeignKey(x => x.InstallmentId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_contract_collection_case_installment");
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.CustomerUserId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_contract_collection_case_customer");
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.ResolvedByUserId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_contract_collection_case_resolved_by");
    }
}

public sealed class ContractCollectionActivityConfiguration : IEntityTypeConfiguration<ContractCollectionActivity>
{
    public void Configure(EntityTypeBuilder<ContractCollectionActivity> builder)
    {
        builder.ToTable("contract_collection_activity", t => t.HasCheckConstraint("ck_contract_collection_activity_kind",
            $"kind IN ({PlanSql.In(CollectionActivityKinds.All)})"));
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.CaseId).HasColumnName("case_id").IsRequired();
        builder.Property(x => x.Kind).HasColumnName("kind").HasMaxLength(20).IsRequired();
        builder.Property(x => x.FromStatus).HasColumnName("from_status").HasMaxLength(30);
        builder.Property(x => x.ToStatus).HasColumnName("to_status").HasMaxLength(30);
        builder.Property(x => x.ActorUserId).HasColumnName("actor_user_id");
        builder.Property(x => x.ActorRole).HasColumnName("actor_role").HasMaxLength(20);
        builder.Property(x => x.Channel).HasColumnName("channel").HasMaxLength(30);
        builder.Property(x => x.Summary).HasColumnName("summary").HasMaxLength(2000);
        builder.Property(x => x.PromisedPaymentDate).HasColumnName("promised_payment_date");
        builder.Property(x => x.EvidenceFileIds).HasColumnName("evidence_file_ids").HasColumnType("jsonb");
        builder.Property(x => x.OccurredAt).HasColumnName("occurred_at").IsRequired();
        builder.HasIndex(x => new { x.CaseId, x.OccurredAt }).HasDatabaseName("ix_contract_collection_activity_case");
        builder.HasOne<ContractCollectionCase>().WithMany().HasForeignKey(x => x.CaseId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_contract_collection_activity_case");
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.ActorUserId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_contract_collection_activity_actor");
    }
}

public sealed class ContractAccessAuditConfiguration : IEntityTypeConfiguration<ContractAccessAudit>
{
    public void Configure(EntityTypeBuilder<ContractAccessAudit> builder)
    {
        builder.ToTable("contract_access_audit");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.ContractId).HasColumnName("contract_id");
        builder.Property(x => x.ActorUserId).HasColumnName("actor_user_id").IsRequired();
        builder.Property(x => x.ActorRole).HasColumnName("actor_role").HasMaxLength(20).IsRequired();
        builder.Property(x => x.Action).HasColumnName("action").HasMaxLength(20).IsRequired();
        builder.Property(x => x.Detail).HasColumnName("detail").HasMaxLength(500);
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.HasIndex(x => new { x.ContractId, x.CreatedAt }).HasDatabaseName("ix_contract_access_audit_contract");
        builder.HasIndex(x => new { x.ActorUserId, x.CreatedAt }).HasDatabaseName("ix_contract_access_audit_actor");
    }
}
